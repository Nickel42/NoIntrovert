using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoIntrovert.Core;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>
/// Opens the real main window without a screen (Avalonia's headless platform,
/// drawn with Skia) at common desktop sizes, shows every page with sample
/// files in it, and checks the layout: nothing overlaps, nothing is cut off
/// or pushed outside its card, text isn't clipped, and inputs keep a usable
/// width. With --screenshots DIR it also saves a PNG of each page and size.
/// </summary>
static class LayoutTests
{
    /// <summary>Window sizes, in device-independent pixels: the minimum, small laptops, the default, and large screens.</summary>
    public static readonly (int Width, int Height)[] Sizes =
    [
        (900, 600), (1024, 700), (1180, 820), (1366, 768), (1600, 900), (1920, 1080),
    ];

    /// <summary>Used by <see cref="HeadlessUnitTestSession"/> to start the app.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHarfBuzz() // real text shaping, as on the desktop (the headless stand-in draws gibberish)
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont()
        .LogToTrace(Avalonia.Logging.LogEventLevel.Warning);

    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir, string? screenshots)
    {
        if (Environment.GetEnvironmentVariable("NOINTROVERT_LAYOUT_TRACE") is not null)
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutTests));
        string? Dispatch(Func<string?> check) => session.Dispatch(check, CancellationToken.None).GetAwaiter().GetResult();

        // One window with sample files on every page, reused for every size.
        Fixture? fixture = null;
        test("Layout: the main window opens headless with sample games and files", () => Dispatch(() =>
        {
            fixture = Fixture.Open(dir("layout-library"), dir("layout-files"));
            return fixture.Error;
        }));
        if (fixture?.Error is not null) return;
        var f = fixture!;

        test("Layout: the Library's add-folders message doesn't cover a list of games", () => Dispatch(() =>
        {
            f.Resize(1180, 820);
            f.Show("library");
            var message = f.Find<TextBlock>(t => t.Text == "Add the folders where you keep your games.");
            if (message is not null && message.IsEffectivelyVisible) return "the empty-library message shows over the games";
            // Folders forgotten (e.g. settings from an older version) but games still in the index: the games show, no message.
            f.Settings.Library.Folders.Clear();
            f.Vm.Library.RefreshFolders();
            f.Layout();
            message = f.Find<TextBlock>(t => t.Text == "Add the folders where you keep your games.");
            var shown = message is not null && message.IsEffectivelyVisible;
            f.Vm.Library.AddFoldersAsync([f.LibraryFolder]).GetAwaiter();
            f.Pump(() => f.Vm.Library.HasFolders);
            return shown ? "the empty-library message shows over the games when the folders list is empty" : null;
        }));

        foreach (var (width, height) in Sizes)
        {
            foreach (var state in Fixture.States)
            {
                test($"Layout {width}×{height}: {state.Name}", () =>
                {
                    Dispatch(() =>
                    {
                        f.Resize(width, height);
                        state.Apply(f);
                        f.Layout();
                        return null;
                    });
                    // Drawn in a dispatch of its own, so the frame shows this state.
                    if (screenshots is not null) Dispatch(() => { f.Save(Path.Combine(screenshots, $"{width}x{height}-{state.Slug}.png")); return null; });
                    return Dispatch(() =>
                    {
                        var problems = Check(f.Window);
                        state.Reset(f);
                        return problems.Count == 0 ? null : string.Join("\n        ", problems.Take(12)) + (problems.Count > 12 ? $"\n        …and {problems.Count - 12} more" : "");
                    });
                });
            }
        }

        test("Layout 1366×768: the Library's game titles get a readable width", () => Dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("library");
            f.Layout();
            var title = f.Find<TextBlock>(t => t.Text == f.Vm.Library.VisibleGames[0].Title && t.FindAncestorOfType<ListBoxItem>() is not null);
            return title is null ? "no game row" : title.Bounds.Width >= 260 ? null : $"the game column is {title.Bounds.Width:F0} px wide";
        }));

        Dispatch(() => { f.Window.Close(); return null; });
    }

    // ---- The checks ----

    /// <summary>Everything wrong with the window's current layout, one line each.</summary>
    public static List<string> Check(Window window)
    {
        var problems = new List<string>();
        var leaves = new List<Leaf>();
        Collect(window, window, leaves);

        foreach (var leaf in leaves)
        {
            var c = leaf.Control;
            // Cut off by something that clips (a scrolled list may cut rows top and bottom, never sideways).
            if (leaf.Visible.Width + 1 < leaf.Rect.Width && !leaf.HorizontallyScrollable)
                problems.Add($"{Describe(c)} is cut off: {leaf.Visible.Width:F0} of {leaf.Rect.Width:F0} px wide shows");
            else if (leaf.Visible.Height + 1 < leaf.Rect.Height && !leaf.InScroller)
                problems.Add($"{Describe(c)} is cut off: {leaf.Visible.Height:F0} of {leaf.Rect.Height:F0} px high shows");

            // Pushed outside its card or the window.
            if (CardOf(c) is { } card && !leaf.InScroller)
            {
                var cardRect = RectIn(card, window);
                if (!Contains(cardRect, leaf.Rect)) problems.Add($"{Describe(c)} sticks out of its card");
            }
            if (!Contains(new Rect(window.ClientSize), leaf.Visible)) problems.Add($"{Describe(c)} is outside the window");

            // Usable widths.
            if (c is TextBox && leaf.Rect.Width < 120) problems.Add($"{Describe(c)} is only {leaf.Rect.Width:F0} px wide");
            if (c is ComboBox && leaf.Rect.Width < 90) problems.Add($"{Describe(c)} is only {leaf.Rect.Width:F0} px wide");
        }

        // Text and buttons that don't fit: measured unconstrained, then given their layout back.
        foreach (var leaf in leaves)
        {
            var c = leaf.Control;
            if (c is TextBlock { TextWrapping: TextWrapping.NoWrap } tb && !string.IsNullOrWhiteSpace(TextOf(tb)))
            {
                var natural = Natural(tb);
                if (tb.TextTrimming == TextTrimming.None && natural > leaf.Rect.Width + 1)
                    problems.Add($"{Describe(c)} doesn't fit: needs {natural:F0} px, has {leaf.Rect.Width:F0}");
                else if (tb.TextTrimming != TextTrimming.None && natural > leaf.Rect.Width + 1 && leaf.Rect.Width < 48)
                    problems.Add($"{Describe(c)} is squeezed to {leaf.Rect.Width:F0} px");
            }
            else if (c is Button { Content: string } && Natural(c) > leaf.Rect.Width + 1)
                problems.Add($"{Describe(c)} doesn't fit: needs {Natural(c):F0} px, has {leaf.Rect.Width:F0}");
        }
        foreach (var leaf in leaves) leaf.Control.InvalidateMeasure();
        window.UpdateLayout();

        // Overlaps between controls that aren't inside one another.
        for (var i = 0; i < leaves.Count; i++)
            for (var j = i + 1; j < leaves.Count; j++)
            {
                var a = leaves[i];
                var b = leaves[j];
                var overlap = a.Visible.Intersect(b.Visible);
                if (overlap.Width < 2 || overlap.Height < 2) continue;
                if (a.Control.IsVisualAncestorOf(b.Control) || b.Control.IsVisualAncestorOf(a.Control)) continue;
                problems.Add($"{Describe(a.Control)} overlaps {Describe(b.Control)}");
            }
        return problems.Distinct().ToList();
    }

    private sealed record Leaf(Control Control, Rect Rect, Rect Visible, bool InScroller, bool HorizontallyScrollable);

    /// <summary>Controls a person reads or uses: text, inputs, buttons, images. Templated inputs are leaves (their insides aren't checked).</summary>
    private static bool IsLeaf(Control c) => c is TextBlock or Button or TextBox or ComboBox or NumericUpDown or Slider or ProgressBar or Image;

    private static void Collect(Visual visual, Window window, List<Leaf> leaves)
    {
        foreach (var child in visual.GetVisualChildren())
        {
            if (!child.IsVisible || child.Opacity == 0) continue;
            if (child is ScrollBar or GridSplitter) continue;
            if (child is Control c && IsLeaf(c))
            {
                if (c.Bounds.Width <= 0 || c.Bounds.Height <= 0) continue;
                if (c is TextBlock t && string.IsNullOrWhiteSpace(TextOf(t))) continue;
                var rect = RectIn(c, window);
                var (visible, inScroller, hScroll) = VisiblePart(c, rect, window);
                if (visible.Width <= 0 || visible.Height <= 0) continue; // scrolled out of view
                leaves.Add(new Leaf(c, rect, visible, inScroller, hScroll));
                continue;
            }
            Collect(child, window, leaves);
        }
    }

    /// <summary>The part of a control left after everything above it that clips (scrolled lists, clipped panels, the window).</summary>
    private static (Rect Visible, bool InScroller, bool HorizontallyScrollable) VisiblePart(Control c, Rect rect, Window window)
    {
        var visible = rect.Intersect(new Rect(window.ClientSize));
        var inScroller = false;
        var hScroll = false;
        for (var v = c.GetVisualParent(); v is not null && v != window; v = v.GetVisualParent())
        {
            if (v is ScrollContentPresenter scp)
            {
                inScroller = true;
                if (scp.FindAncestorOfType<ScrollViewer>() is { HorizontalScrollBarVisibility: not ScrollBarVisibility.Disabled } sv && sv.Extent.Width > sv.Viewport.Width + 1)
                    hScroll = true;
            }
            if (v.ClipToBounds || v is ScrollContentPresenter) visible = visible.Intersect(RectIn(v, window));
        }
        return (visible, inScroller, hScroll);
    }

    private static Border? CardOf(Control c)
    {
        for (var v = c.GetVisualParent(); v is not null; v = v.GetVisualParent())
            if (v is Border b && b.Classes.Contains("card")) return b;
        return null;
    }

    private static Rect RectIn(Visual v, Window window) =>
        v.TranslatePoint(new Point(0, 0), window) is { } p ? new Rect(p, v.Bounds.Size) : default;

    private static bool Contains(Rect outer, Rect inner) =>
        inner.X >= outer.X - 1 && inner.Y >= outer.Y - 1 && inner.Right <= outer.Right + 1 && inner.Bottom <= outer.Bottom + 1;

    /// <summary>The width a control asks for with no limit.</summary>
    private static double Natural(Control c)
    {
        c.Measure(Size.Infinity);
        return c.DesiredSize.Width - c.Margin.Left - c.Margin.Right;
    }

    private static string TextOf(TextBlock t) =>
        t.Text ?? (t.Inlines is { } inlines ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : "");

    private static string Describe(Control c)
    {
        var what = c switch
        {
            TextBlock t => $"text \"{Short(TextOf(t))}\"",
            Button { Content: string s } b => $"{b.GetType().Name} \"{Short(s)}\"",
            ComboBox cb => $"ComboBox \"{Short(cb.SelectedItem?.ToString() ?? cb.PlaceholderText ?? "")}\"",
            TextBox tb => $"TextBox \"{Short(tb.Text is { Length: > 0 } x ? x : tb.PlaceholderText ?? "")}\"",
            _ => c.GetType().Name + (c.Name is { Length: > 0 } n ? $" {n}" : ""),
        };
        var view = c.FindAncestorOfType<UserControl>()?.GetType().Name;
        return view is null ? what : $"{what} ({view})";
    }

    private static string Short(string s) => s.Length <= 40 ? s : s[..37] + "…";

    // ---- The window under test ----

    /// <summary>A state of the window to check: a page, with the log, queue or side panel as a person would have them.</summary>
    public sealed record State(string Name, string Slug, Action<Fixture> Apply, Action<Fixture> Reset);

    public sealed class Fixture
    {
        public required MainWindow Window { get; init; }
        public required MainViewModel Vm { get; init; }
        public required AppSettings Settings { get; init; }
        public required string LibraryFolder { get; init; }
        public string? Error { get; private set; }

        public static readonly State[] States =
        [
            new("Optimize (Library) with a game selected", "library", f => { f.Show("library"); f.SelectGame(); }, _ => { }),
            new("Convert with a row focused", "convert", f => { f.Show("convert"); f.FocusRow(f.Vm.Convert); }, _ => { }),
            new("Convert with the log and queue open", "convert-log-queue", f =>
            {
                f.Show("convert");
                f.Vm.Log.IsExpanded = true;
                f.Vm.Queue.IsOpen = true;
            }, f => { f.Vm.Log.IsExpanded = false; f.Vm.Queue.IsOpen = false; }),
            new("Verify with a row focused", "verify", f => { f.Show("verify"); f.FocusRow(f.Vm.Verify); }, _ => { }),
            new("Organize", "organize", f => f.Show("cleanup"), _ => { }),
            new("Settings", "settings", f => f.Show("settings"), _ => { }),
        ];

        public static Fixture Open(string libraryFolder, string filesFolder)
        {
            WriteSamples(libraryFolder, filesFolder);
            var settings = new AppSettings();
            settings.Library.Folders.Add(libraryFolder);
            settings.General.WindowWidth = 1180;
            settings.General.WindowHeight = 820;
            var window = new MainWindow();
            var vm = new MainViewModel(settings, window);
            window.DataContext = vm;
            window.Show();
            var f = new Fixture { Window = window, Vm = vm, Settings = settings, LibraryFolder = libraryFolder };
            if (!f.Pump(() => vm.Library.VisibleGames.Count > 0)) f.Error = "the Library found no games in the samples";
            var files = Directory.GetFiles(filesFolder);
            vm.Convert.AddPaths(files);
            vm.Verify.AddPaths(files);
            if (!f.Pump(() => vm.Convert.Items.Count >= files.Length && vm.Verify.Items.Count >= files.Length))
                f.Error ??= $"Convert has {vm.Convert.Items.Count} and Verify {vm.Verify.Items.Count} of {files.Length} files";
            return f;
        }

        /// <summary>Long names on purpose: the layout has to cope with real ones.</summary>
        private static void WriteSamples(string libraryFolder, string filesFolder)
        {
            string[] nes =
            [
                "Battletoads-Double Dragon (USA)", "Castlevania (USA) (Castlevania Anniversary Collection)",
                "Castlevania III - Dracula's Curse (USA) (Castlevania Anniversary Collection)", "Dragon Warrior (USA) (Rev 1)",
                "EarthBound Beginnings (USA, Europe) (Virtual Console)", "Ghosts'n Goblins (USA)", "Kirby's Adventure (USA) (Rev 1)",
                "Legend of Zelda, The (USA) (Rev 1)", "Mega Man 2 (USA)", "Ninja Gaiden - Episode II - The Dark Sword of Chaos (USA) (Sample) (1990-01-18)",
                "Super Mario Bros. 3 (USA) (Rev 1)", "Tetris (USA)",
            ];
            var nesFolder = Path.Combine(libraryFolder, "NES");
            Directory.CreateDirectory(nesFolder);
            var rom = new byte[16 + 32 * 1024];
            "NES\u001a"u8.CopyTo(rom);
            rom[4] = 2;
            foreach (var name in nes)
            {
                File.WriteAllBytes(Path.Combine(nesFolder, name + ".nes"), rom);
                File.WriteAllBytes(Path.Combine(filesFolder, name + ".nes"), rom);
            }
            File.WriteAllBytes(Path.Combine(filesFolder, "A Very Long Game Title That Goes On And On - Special Collector's Edition (USA) (Rev 2).iso"), new byte[64 * 1024]);
        }

        public void Show(string page) => Vm.SelectedPage = Vm.Pages.First(p => p.Id == page);

        public void SelectGame()
        {
            Vm.Library.SelectedGame = Vm.Library.VisibleGames.FirstOrDefault();
        }

        public void FocusRow(BatchTabViewModel tab) => tab.FocusedItem = tab.Items.FirstOrDefault();

        public void Resize(double width, double height)
        {
            Window.Width = width;
            Window.Height = height;
            Layout();
        }

        public void Layout()
        {
            for (var i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Window.UpdateLayout();
            }
        }

        /// <summary>Runs the UI thread's work (and lets background tasks finish) until <paramref name="done"/> or 20 seconds.</summary>
        public bool Pump(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                if (done()) { Layout(); return true; }
                Thread.Sleep(15);
            }
            return false;
        }

        public T? Find<T>(Func<T, bool> match) where T : Visual =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(match);

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Layout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (Window.GetLastRenderedFrame() is { } frame && frame.PixelSize.Width == (int)Window.ClientSize.Width && Environment.GetEnvironmentVariable("NOINTROVERT_LAYOUT_FRAME") is not null)
            {
                frame.Save(path);
                return;
            }
            var size = Window.ClientSize;
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
            bitmap.Render(Window);
            bitmap.Save(path);
        }
    }
}
