using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Presets;
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

        // Themes (Core/Themes): the window repaints in the chosen theme of the kind showing.
        test("Layout: choosing a theme repaints the window, each kind with its own theme", () => Dispatch(() =>
        {
            // The window's own app (the headless session's Application.Current can be another instance).
            var app = (App)((Avalonia.Styling.IStyleHost)f.Window).StylingParent!;
            var general = f.Vm.Settings.General;
            app.BindThemes(general);
            f.Resize(1180, 820);
            f.Show("settings");
            Color Background()
            {
                f.Layout();
                // What the window is painted with (its Background is {DynamicResource AppBackground}).
                return f.Window.Background is ISolidColorBrush b ? b.Color : default;
            }
            var themes = general.ColorThemes;
            general.IsDarkTheme = true;
            themes.SelectedDark = themes.DarkThemes.First(o => o.Id == "ember");
            themes.SelectedLight = themes.LightThemes.First(o => o.Id == "seafoam");
            if (Background() != themes.ActiveDark[Core.Themes.ThemeRole.Background]) return "the dark theme isn't showing";
            if (screenshots is not null) f.Save(Path.Combine(screenshots, "theme-ember-settings.png"));
            general.IsLightTheme = true;
            if (Background() != themes.ActiveLight[Core.Themes.ThemeRole.Background]) return "switching to light didn't show the light theme";
            if (screenshots is not null) f.Save(Path.Combine(screenshots, "theme-seafoam-settings.png"));
            // The editor previews what it edits, whichever kind was showing.
            themes.Customize("Dark");
            themes.Editor!.Rows.First(r => r.Role == Core.Themes.ThemeRole.Background).Hex = "#203040";
            var previewed = Background() == Color.Parse("#203040");
            if (screenshots is not null) f.Save(Path.Combine(screenshots, "theme-editor.png"));
            themes.Editor.CancelCommand.Execute(null);
            if (!previewed) return "the editor's change wasn't previewed";
            if (Background() != themes.ActiveLight[Core.Themes.ThemeRole.Background]) return "Cancel didn't go back to the light theme";
            Color Link()
            {
                // What the link resolves (its Foreground animates there, and the headless clock doesn't run).
                var link = f.Find<Button>(b => b.Content as string == "Import file…");
                return link is not null && link.TryFindResource("SettingsLink", link.ActualThemeVariant, out var v) && v is ISolidColorBrush b ? b.Color : default;
            }
            foreach (var id in new[] { "cartridge", "sunrise", "base16-gruvbox-dark-hard", "base16-penumbra-light-contrast-plus-plus", "nointrovert-dark", "nointrovert-light" })
            {
                var dark = themes.DarkThemes.FirstOrDefault(o => o.Id == id);
                if (dark is not null) { general.IsDarkTheme = true; themes.SelectedDark = dark; }
                else { general.IsLightTheme = true; themes.SelectedLight = themes.LightThemes.First(o => o.Id == id); }
                var theme = dark is not null ? themes.ActiveDark : themes.ActiveLight;
                if (Background() != theme[Core.Themes.ThemeRole.Background]) return $"{id}: the window isn't in its background";
                var link = Core.Themes.ThemeResources.Colors(theme)["SettingsLink"];
                if (Link() != link) return $"{id}: a link is {Link()}, not {link}";
                if (screenshots is not null) f.Save(Path.Combine(screenshots, $"theme-{id}-settings.png"));
            }
            // Back to how the other layout tests expect it.
            general.IsSystemTheme = true;
            themes.SelectedDark = themes.DarkThemes.First(o => o.Id == Core.Themes.ThemeLibrary.DefaultDarkId);
            themes.SelectedLight = themes.LightThemes.First(o => o.Id == Core.Themes.ThemeLibrary.DefaultLightId);
            return null;
        }));

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

        test("Convert: rows ticked in the list reach the summary and the panel header", () => Dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("convert");
            f.Layout();
            var tab = f.Vm.Convert;
            var list = f.Find<ListBox>(l => l.Name == "List" && l.IsEffectivelyVisible);
            if (list is null) return "no job list";
            list.SelectedItems!.Clear();
            list.SelectedItems.Add(tab.Items[0]);
            list.SelectedItems.Add(tab.Items[1]);
            tab.FocusedItem = tab.Items[1];
            f.Layout();
            var summary = tab.Summary;
            var header = tab.Panel.Header;
            list.SelectedItems.Clear();
            tab.FocusedItem = null;
            f.Layout();
            if (tab.SelectedCount != 0) return "clearing the list's selection left rows selected on the page";
            return summary.StartsWith("2 selected · ") && header == $"Focused: row 2 of {tab.Items.Count} · 2 selected"
                ? null : $"summary '{summary}', header '{header}'";
        }));

        test("Layout 1366×768: the Library's game titles get a readable width", () => Dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("library");
            f.Layout();
            var title = f.Find<TextBlock>(t => t.Text == f.Vm.Library.VisibleGames[0].Title && t.FindAncestorOfType<ListBoxItem>() is not null);
            return title is null ? "no game row" : title.Bounds.Width >= 260 ? null : $"the game column is {title.Bounds.Width:F0} px wide";
        }));

        test("Layout: the Library's column headings line up with their columns, with the list scrolling", () => Dispatch(() =>
        {
            var problems = new List<string>();
            foreach (var (width, height) in new[] { (1366, 560), (1180, 820), (1920, 1080) })
            {
                f.Resize(width, height);
                f.Show("library");
                f.Layout();
                foreach (var column in new[] { "col-system", "col-formats", "col-size", "col-status" })
                {
                    var cells = f.Window.GetVisualDescendants().OfType<Control>()
                        .Where(c => c.Classes.Contains(column) && c.IsEffectivelyVisible).ToList();
                    var heading = cells.FirstOrDefault(c => c.FindAncestorOfType<ListBoxItem>() is null);
                    var cell = cells.FirstOrDefault(c => c.FindAncestorOfType<ListBoxItem>() is not null);
                    if (heading is null && cell is null) continue; // hidden at this width
                    if (heading is null || cell is null) { problems.Add($"{width}×{height} {column}: heading {heading is not null}, cell {cell is not null}"); continue; }
                    var h = heading.TranslatePoint(new Point(0, 0), f.Window)!.Value.X;
                    var c = cell.TranslatePoint(new Point(0, 0), f.Window)!.Value.X;
                    if (Math.Abs(h - c) > 1 || Math.Abs(heading.Bounds.Width - cell.Bounds.Width) > 1)
                        problems.Add($"{width}×{height} {column}: heading at {h:F0} ({heading.Bounds.Width:F0} wide), column at {c:F0} ({cell.Bounds.Width:F0} wide)");
                }
            }
            f.Resize(1180, 820);
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }));

        test("Library: clicking a column heading sorts by it, again turns the order round, and the arrow shows which", () => Dispatch(() =>
        {
            var lib = f.Vm.Library;
            f.Show("library");
            f.Layout();
            string Arrows() => $"{lib.GameArrow}|{lib.SystemArrow}|{lib.FormatsArrow}|{lib.SizeArrow}|{lib.StatusArrow}";
            var byTitle = StringComparer.OrdinalIgnoreCase;
            bool Ordered<T>(Func<GameRowViewModel, T> key, IComparer<T> comparer, bool descending)
            {
                var rows = lib.VisibleGames.ToList();
                for (var i = 1; i < rows.Count; i++)
                {
                    var c = comparer.Compare(key(rows[i - 1]), key(rows[i]));
                    if (descending ? c < 0 : c > 0) return false;
                    if (c == 0 && byTitle.Compare(rows[i - 1].Title, rows[i].Title) > 0) return false; // ties go by title
                }
                return true;
            }
            try
            {
                if (Arrows() != "▲||||") return $"by default: {Arrows()}";
                if (!Ordered(g => g.Title, byTitle, false)) return "not A to Z by title";
                // The heading is a button bound to SortByCommand, as a click runs it.
                var sizeHeading = f.Find<Button>(b => b.Classes.Contains("colsort") && b.CommandParameter is LibrarySortColumn.Size);
                if (sizeHeading is null || sizeHeading.Command != lib.SortByCommand) return "the Size heading isn't a sort button";
                lib.SortByCommand.Execute(LibrarySortColumn.Size);
                if (Arrows() != "|||▲|" || !Ordered(g => g.Game.Size, Comparer<long>.Default, false)) return $"by size: {Arrows()}";
                lib.SortByCommand.Execute(LibrarySortColumn.Size);
                if (Arrows() != "|||▼|" || !Ordered(g => g.Game.Size, Comparer<long>.Default, true)) return $"by size, turned round: {Arrows()}";
                lib.SortByCommand.Execute(LibrarySortColumn.System);
                if (Arrows() != "|▲|||" || !Ordered(g => g.System, byTitle, false)) return $"by system: {Arrows()}";
                lib.SortByCommand.Execute(LibrarySortColumn.Game);
                lib.SortByCommand.Execute(LibrarySortColumn.Game);
                if (Arrows() != "▼||||" || !Ordered(g => g.Title, byTitle, true)) return $"by title, Z to A: {Arrows()}";
                return f.Settings.Library.SortColumn == LibrarySortColumn.Game && f.Settings.Library.SortDescending ? null : "the sort isn't kept in the settings";
            }
            finally
            {
                f.Settings.Library.SortColumn = LibrarySortColumn.System; // so the next click below lands on Game, A to Z
                lib.SortByCommand.Execute(LibrarySortColumn.Game);
            }
        }));

        test("Library: a search keeps the chosen order and the counts, and clearing it brings every game back in order", () => Dispatch(() =>
        {
            var lib = f.Vm.Library;
            f.Show("library");
            f.Layout();
            var byTitle = StringComparer.OrdinalIgnoreCase;
            try
            {
                lib.SortByCommand.Execute(LibrarySortColumn.Size);
                lib.SortByCommand.Execute(LibrarySortColumn.Size); // largest first: the samples are all one size, so ties go by title
                var all = lib.VisibleGames.ToList();
                lib.Search = "castlevania usa";
                f.Layout();
                var found = lib.VisibleGames.ToList();
                var expected = all.Where(g => g.Title.Contains("castlevania", StringComparison.OrdinalIgnoreCase)).ToList();
                if (expected.Count < 2) return $"the samples have {expected.Count} Castlevania games";
                if (!found.SequenceEqual(expected)) return $"search found {string.Join(" | ", found.Select(g => g.Title))}";
                for (var i = 1; i < found.Count; i++)
                    if (found[i - 1].Size < found[i].Size || found[i - 1].Size == found[i].Size && byTitle.Compare(found[i - 1].Title, found[i].Title) > 0)
                        return "the search results aren't in the chosen order";
                if (!lib.Summary.StartsWith($"{found.Count} of {all.Count} games")) return $"summary '{lib.Summary}'";
                lib.SortByCommand.Execute(LibrarySortColumn.Game); // a new sort while searching re-sorts the results
                var aToZ = lib.VisibleGames.ToList();
                if (!aToZ.SequenceEqual(found.OrderBy(g => g.Title, byTitle))) return "sorting while searching didn't sort the results A to Z";
                lib.Search = "";
                f.Layout();
                return lib.VisibleGames.Count == all.Count && lib.VisibleGames.SequenceEqual(all.OrderBy(g => g.Title, byTitle).ThenBy(g => g.Game.Key, StringComparer.Ordinal))
                    ? null : "clearing the search didn't bring every game back, A to Z";
            }
            finally
            {
                lib.Search = "";
                f.Settings.Library.SortColumn = LibrarySortColumn.System;
                lib.SortByCommand.Execute(LibrarySortColumn.Game);
                f.Layout();
            }
        }));

        test("Library: a selected game's files and match summary are read in the background", () => Dispatch(() =>
        {
            var lib = f.Vm.Library;
            f.Show("library");
            f.Layout();
            var game = lib.VisibleGames[0];
            lib.SelectedGame = game;
            if (game.Files.Count != game.Game.Items.Count) return "the file rows aren't there straight away";
            if (!f.Pump(() => lib.DetailsLoaded.IsCompleted && game.MatchSummary.Length > 0)) return "the details never arrived";
            if (!game.MatchSummary.StartsWith("Not checked")) return $"match summary '{game.MatchSummary}'";
            if (lib.ShowsChdInfo) return "a game with no CHD shows the CHD panel";
            var shown = f.Find<TextBlock>(t => t.Text == game.MatchSummary && t.IsEffectivelyVisible);
            return shown is null ? "the details panel doesn't show the match summary" : null;
        }));

        test("Queue: the refresh timer runs only while jobs are queued or running", () => Dispatch(() =>
        {
            var queue = new JobQueue(() => 1);
            var vm = new QueueViewModel(queue);
            if (vm.IsTicking) return "the timer runs with an empty queue";
            using var release = new ManualResetEventSlim();
            var job = new BatchJob("Wait", new NullReport(), _ =>
            {
                release.Wait(TimeSpan.FromSeconds(20));
                return new JobOutcome(JobStatus.Succeeded, "ok");
            }, _ => { }, _ => { });
            queue.Submit("Test", [job], new AppSettings(), f.Vm.Log);
            if (!f.Pump(() => vm.IsTicking && vm.IsBusy)) return "the timer didn't start when a job was queued";
            release.Set();
            if (!f.Pump(() => !vm.IsTicking && vm.StatusText.StartsWith("Idle"))) return $"the timer still runs once the queue is idle ({vm.StatusText})";
            return vm.Rows.Count == 1 ? null : $"{vm.Rows.Count} rows for one job";
        }));

        test("Layout: the title bar's moon/sun flips the Theme setting and the lilac follows the theme", () => Dispatch(() =>
        {
            var app = Avalonia.Application.Current!;
            var before = f.Settings.General.Theme;
            try
            {
                if (f.Find<Button>(b => b.Name == "ThemeToggle") is not { IsEffectivelyVisible: true }) return "no theme toggle in the title bar";
                app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                f.Window.ToggleTheme();
                if (f.Settings.General.Theme != AppTheme.Light) return $"from dark the toggle chose {f.Settings.General.Theme}, not Light";
                app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
                if (!app.TryGetResource("SukiPrimaryColor", null, out var light) || light is not Avalonia.Media.Color { R: 0x61, G: 0x62, B: 0x83 })
                    return $"the light theme's primary is {light}, not #616283";
                f.Window.ToggleTheme();
                if (f.Settings.General.Theme != AppTheme.Dark) return $"from light the toggle chose {f.Settings.General.Theme}, not Dark";
                app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                if (!app.TryGetResource("SukiPrimaryColor", null, out var dark) || dark is not Avalonia.Media.Color { R: 0x7C, G: 0x7E, B: 0xA6 })
                    return $"the dark theme's primary is {dark}, not #7C7EA6";
                return null;
            }
            finally
            {
                f.Settings.General.Theme = before;
                app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            }
        }));

        test("Layout: Settings shows Keys without opening Advanced; Enter saves a working folder; the log bar counts in words", () => Dispatch(() =>
        {
            f.Show("settings");
            f.Layout();
            if (f.Find<Border>(b => b.Name == "KeysCard") is not { IsEffectivelyVisible: true } keys) return "the Keys card isn't showing";
            if (keys.FindAncestorOfType<Expander>() is not null) return "Keys is still inside Advanced";
            if (f.Find<TextBox>(t => t.Name == "TempFolderBox") is not { } box) return "no working folder box";
            var before = f.Settings.Performance.TempFolder;
            var folder = dir("layout-work");
            try
            {
                box.Focus();
                box.Text = folder;
                if (f.Settings.Performance.TempFolder != before) return "the working folder was saved while typing";
                box.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter, Source = box });
                if (f.Settings.Performance.TempFolder != folder) return $"Enter didn't save the working folder (it's \"{f.Settings.Performance.TempFolder}\")";
            }
            finally { f.Vm.Settings.Performance.TempFolder = before; }
            var counts = f.Find<TextBlock>(t => t.Classes.Contains("counts"));
            var expected = LogViewModel.DescribeCounts(f.Vm.Log.ErrorCount, f.Vm.Log.WarningCount);
            return counts?.Text == expected ? null : $"the log bar reads \"{counts?.Text}\", not \"{expected}\"";
        }));

        UxLayoutTests.Run(test, f, Dispatch);
        OptUxTests.RunLayout(test, f, Dispatch, screenshots);
        OptCleanupTests.RunLayout(test, f, Dispatch, screenshots);

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
            new("Convert with a PlayStation row on Custom (Zstandard) and others selected", "convert-custom", f => f.ShowCustom(), f => f.ResetCustom()),
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
            WritePlayStationDisc(filesFolder, "Final Fantasy VII (USA) (Disc 3)");
            File.WriteAllBytes(Path.Combine(filesFolder, "A Very Long Game Title That Goes On And On - Special Collector's Edition (USA) (Rev 2).iso"), new byte[64 * 1024]);
        }

        /// <summary>A raw Mode 2 cue/bin whose volume descriptor says PLAYSTATION, so it's read as a PS1 disc.</summary>
        private static void WritePlayStationDisc(string folder, string name)
        {
            const int sectors = 40, size = 2352;
            var data = new byte[sectors * size];
            for (var i = 0; i < sectors; i++)
            {
                data.AsSpan(i * size + 1, 10).Fill(0xFF);
                data[i * size + 15] = 2;
            }
            var pvd = 16 * size + 24;
            data[pvd] = 1;
            "CD001"u8.CopyTo(data.AsSpan(pvd + 1));
            "PLAYSTATION"u8.CopyTo(data.AsSpan(pvd + 8));
            File.WriteAllBytes(Path.Combine(folder, name + ".bin"), data);
            File.WriteAllText(Path.Combine(folder, name + ".cue"), string.Join(Environment.NewLine,
                $"FILE \"{name}.bin\" BINARY", "  TRACK 01 MODE2/2352", "    INDEX 01 00:00:00", ""));
        }

        private PresetListItem? _customWas;

        /// <summary>Convert with the PlayStation row focused and on Custom with Zstandard, so every CHD setting and warning shows, and two more rows selected.</summary>
        public void ShowCustom()
        {
            Show("convert");
            var tab = Vm.Convert;
            var row = tab.Items.First(i => i.SystemId == "playstation");
            var list = Window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "List" && l.IsEffectivelyVisible);
            list.SelectedItems!.Clear();
            foreach (var other in tab.Items.Where(i => i != row).Take(2)) list.SelectedItems.Add(other);
            list.SelectedItems.Add(row);
            tab.FocusedItem = row;
            _customWas = row.PresetChoice;
            row.PresetChoice = row.PresetOptions.First(o => o.IsPreset && o.Preset!.IsCustom);
            Layout();
            var custom = tab.Panel.Custom!;
            custom.Chd.SelectedCompression = ChdSettingsViewModel.CompressionChoices.First(c => c.Value == "cdzs,cdzl,cdfl");
            custom.Pull();
            Layout();
            // Show the end of the panel, where the settings are.
            Window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(v => v.DataContext is SidePanelViewModel)?.ScrollToEnd();
            Layout();
        }

        public void ResetCustom()
        {
            var tab = Vm.Convert;
            var row = tab.Items.First(i => i.SystemId == "playstation");
            if (_customWas is not null) row.PresetChoice = _customWas;
            Window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "List" && l.IsEffectivelyVisible).SelectedItems!.Clear();
            Window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(v => v.DataContext is SidePanelViewModel)?.ScrollToHome();
            Layout();
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
                frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                return;
            }
            var size = Window.ClientSize;
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
            bitmap.Render(Window);
            bitmap.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
