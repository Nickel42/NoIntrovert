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
            foreach (var id in new[] { "cartridge", "sunrise", "dracula", "solarized-light", "nointrovert-dark", "nointrovert-light" })
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

        test("Rail: no logo; LIBRARY over Optimize and FILES over Convert, Verify, Organize; the icon-only rail has dividers instead", () => Dispatch(() =>
        {
            var general = f.Vm.Settings.General;
            var powerUser = general.PowerUserMode;
            try
            {
                general.PowerUserMode = false;
                f.Resize(1366, 768);
                f.Show("convert");
                f.Layout();
                if (f.Find<Image>(i => i.IsEffectivelyVisible && i.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("railpane"))) is not null)
                    return "the rail still shows an image (the logo)";
                var groups = f.Vm.RailGroups.Where(g => g.IsVisible).Select(g => $"{g.Header}:{string.Join("+", g.Pages.Where(p => p.IsVisible).Select(p => p.Title))}");
                if (string.Join(",", groups) != "LIBRARY:Optimize,FILES:Convert+Verify+Organize") return "the rail's groups are " + string.Join(",", groups);
                List<string> Headers() => f.Window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.Classes.Contains("railheader") && t.IsEffectivelyVisible && t.Bounds.Height > 0).Select(t => t.Text ?? "").ToList();
                int Dividers() => f.Window.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("raildivider") && b.IsEffectivelyVisible);
                if (string.Join(",", Headers()) != "LIBRARY,FILES") return "the wide rail shows the headers " + string.Join(",", Headers());
                if (Dividers() != 0) return "the wide rail shows group dividers";
                // Optimize isn't on a card of its own: unselected, it's drawn as Convert is when unselected (f.Show("verify")).
                Avalonia.Media.IBrush? Fill(string title) => f.Find<Button>(b => b.Classes.Contains("rail") && b.IsEffectivelyVisible
                        && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == title))
                    ?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "RootBorder")?.Background;
                f.Show("verify");
                f.Layout();
                if (Fill("Optimize") is Avalonia.Media.ISolidColorBrush { Color.A: > 0 } card) return $"Optimize sits on a card ({card.Color})";
                if (f.Find<TextBlock>(t => t.Classes.Contains("railsub") && t.IsEffectivelyVisible) is not { Text: "Verify, convert, organize" })
                    return "Optimize lost the line under its title";
                // Power-user mode adds TOOLS (Library tests).
                general.PowerUserMode = true;
                f.Layout();
                if (string.Join(",", Headers()) != "LIBRARY,FILES,TOOLS") return "in Power-user mode the rail shows the headers " + string.Join(",", Headers());
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "rail-groups.png"));
                // The icon-only rail: no headers, a divider between groups (not above the first).
                f.Resize(1024, 700);
                f.Layout();
                if (Headers().Count != 0) return "the icon-only rail shows the headers " + string.Join(",", Headers());
                if (Dividers() != 2) return $"the icon-only rail has {Dividers()} group dividers, not 2";
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "rail-compact.png"));
                return null;
            }
            finally
            {
                general.PowerUserMode = powerUser;
                f.Layout();
            }
        }));

        test("Rail: the selected page is a tint with an accent bar, its words read", () => Dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("optimize");
            f.Layout();
            var titles = f.Vm.MainPages.Where(p => p.IsVisible).Select(p => p.Title).ToList();
            if (string.Join(",", titles) != "Optimize,Convert,Verify,Organize") return "the rail lists " + string.Join(",", titles);
            var selected = f.Find<Button>(b => b.Classes.Contains("rail") && b.Classes.Contains("selected") && b.IsEffectivelyVisible);
            if (selected?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "RootBorder")?.Background is not Avalonia.Media.ISolidColorBrush fill) return "no selected rail button";
            var accent = Accent();
            if (accent == fill.Color) return "the selected page is still a solid accent pill";
            if (!selected.TryFindResource("PrimaryTint", selected.ActualThemeVariant, out var tint) || tint is not Avalonia.Media.ISolidColorBrush t || t.Color != fill.Color)
                return $"the selected page is {fill.Color}, not the tint";
            if (selected.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "RailBar") is not { IsEffectivelyVisible: true, Bounds.Width: >= 3, Background: Avalonia.Media.ISolidColorBrush bar }
                || bar.Color != accent)
                return "the selected page has no accent bar at its left";
            if (f.Find<Button>(b => b.Classes.Contains("primary") && b.IsEffectivelyVisible) is { Background: Avalonia.Media.ISolidColorBrush run } && run.Color != accent)
                return $"the run button is {run.Color}, not the accent {accent}";
            foreach (var text in selected.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
            {
                if (text.Foreground is not Avalonia.Media.ISolidColorBrush ink) return $"\"{text.Text}\" has no colour";
                var contrast = Core.Themes.ThemeRoles.Contrast(fill.Color, ink.Color);
                if (contrast < 4.5) return $"\"{text.Text}\" reads at {contrast:F1}:1 on the selected page";
            }
            if (screenshots is not null) f.Save(Path.Combine(screenshots, "rail-selected.png"));
            return null;
        }));

        test("Progress bars: a bar's fill is the theme's accent, not grey", () => Dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("convert");
            f.Layout();
            var bar = f.Find<ProgressBar>(b => !b.IsIndeterminate && b.FindAncestorOfType<ListBoxItem>() is not null);
            if (bar?.Foreground is not Avalonia.Media.ISolidColorBrush fill) return "no progress bar in a row";
            return fill.Color == Accent() ? null : $"the bar is {fill.Color}, not the accent {Accent()}";
        }));

        test("Every page: the same Add ▾, Remove, ⋯, checkboxes and filter on Optimize, Convert, Verify and Organize", () => Dispatch(() =>
        {
            f.Resize(1366, 768);
            foreach (var page in new[] { "optimize", "convert", "verify", "cleanup" })
            {
                f.Show(page);
                f.Layout();
                var tab = (BatchTabViewModel)f.Vm.SelectedPage.Content;
                if (!f.Pump(() => tab.Items.Count > 0 && !tab.IsScanning)) return $"{page}: no rows";
                f.Layout();
                foreach (var name in new[] { "AddButton", "AddMenuButton", "RemoveButton", "MoreButton" })
                    if (f.Find<Button>(b => b.Name == name && b.IsEffectivelyVisible) is null) return $"{page}: no {name}";
                if (f.Find<CheckBox>(c => c.Name == "SelectAll" && c.IsEffectivelyVisible) is null) return $"{page}: no select-all box";
                if (f.Find<CheckBox>(c => c.IsEffectivelyVisible && c.FindAncestorOfType<ListBoxItem>() is not null && c.DataContext is JobItemViewModel) is null)
                    return $"{page}: the rows have no checkboxes";
                if (f.Find<TextBox>(t => t.Name == "FilterBox" && t.IsEffectivelyVisible) is null) return $"{page}: no filter";
            }
            return null;
        }));

        test("Shared files: a file added on one page shows on the others when they're opened; Remove takes it off every page", () => Dispatch(() =>
        {
            var folder = Path.GetFullPath(Path.Combine(f.LibraryFolder, "..", "layout-shared"));
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "Shared Test Game (USA).nes");
            var rom = new byte[16 + 16 * 1024];
            "NES\u001a"u8.CopyTo(rom);
            rom[4] = 1;
            File.WriteAllBytes(file, rom);
            bool Has(BatchTabViewModel tab) => tab.Items.Any(i => PathNames.Same(i.Path, file));
            try
            {
                f.Show("verify");
                f.Vm.Verify.AddPaths([file]);
                if (!f.Pump(() => Has(f.Vm.Verify))) return "Verify didn't take the file";
                foreach (var (page, tab) in new (string, BatchTabViewModel)[] { ("optimize", f.Vm.Optimize), ("convert", f.Vm.Convert), ("cleanup", f.Vm.Cleanup) })
                {
                    if (Has(tab)) return $"{page} had the file before it was opened";
                    f.Show(page);
                    if (!f.Pump(() => Has(tab))) return $"{page} doesn't show the file added on Verify";
                }
                // Removed on Organize: gone from every page, and a page opened later doesn't bring it back.
                f.Layout();
                var list = f.Find<ListBox>(l => l.Name == "List" && l.IsEffectivelyVisible)!;
                list.SelectedItems!.Clear();
                list.SelectedItems.Add(f.Vm.Cleanup.Items.First(i => PathNames.Same(i.Path, file)));
                f.Vm.Cleanup.RemoveSelectedCommand.Execute(null);
                f.Layout();
                foreach (var tab in f.Vm.SharedTabs)
                    if (Has(tab)) return $"{tab.Title} still has the removed file";
                f.Show("verify");
                f.Layout();
                return Has(f.Vm.Verify) ? "the removed file came back on Verify" : null;
            }
            finally
            {
                foreach (var tab in f.Vm.SharedTabs) tab.Items.RemoveWhere(i => PathNames.Same(i.Path, file));
            }
        }));

        test("Optimize: each row's CHANGES says what happens to it, Verify first, and lines up with its heading", () => Dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("optimize");
            var tab = f.Vm.Optimize;
            if (!f.Pump(() => tab.Items.Count > 0 && !tab.IsScanning && tab.PlanMade.IsCompleted && !tab.IsPlanning && tab.Items.All(i => i.ChangesTarget.Length > 0)))
                return "the rows never got their changes";
            f.Layout();
            var bad = tab.Items.FirstOrDefault(i => i.ChangesSteps.FirstOrDefault() != "Verify");
            if (bad is not null) return $"{bad.Name}: \"{bad.ChangesTarget}\" ({bad.ChangesStepsText})";
            var ps = tab.Items.FirstOrDefault(i => i.SystemId == "playstation");
            if (ps is null) return "no PlayStation row";
            if (!ps.ChangesSteps.Contains("Convert") || !ps.ChangesTarget.Contains(".chd")) return $"the cue/bin's changes: \"{ps.ChangesTarget}\" ({ps.ChangesStepsText})";
            var list = f.Find<ListBox>(l => l.Name == "List" && l.IsEffectivelyVisible)!;
            list.ScrollIntoView(ps);
            f.Layout();
            var heading = f.Find<TextBlock>(t => t.Text == "CHANGES" && t.IsEffectivelyVisible);
            var cell = f.Find<TextBlock>(t => t.Text == ps.ChangesTarget && t.Classes.Contains("changestarget") && t.IsEffectivelyVisible);
            if (heading is null || cell is null) return "no CHANGES column";
            // Every column's cells sit the list's scroll bar to the left of their heading; CHANGES lines up as STATUS does.
            double X(Control c) => c.TranslatePoint(new Point(0, 0), f.Window)!.Value.X;
            var statusHeading = f.Find<TextBlock>(t => t.Text == "STATUS" && t.IsEffectivelyVisible);
            var statusCell = f.Find<DockPanel>(d => d.Classes.Contains("col-status") && d.DataContext == ps);
            if (statusHeading is null || statusCell is null) return "no STATUS column";
            var off = X(heading) - X(cell) - (X(statusHeading) - X(statusCell));
            if (Math.Abs(off) > 1) return $"the CHANGES column is {off:F0} px off its heading, against STATUS's";
            if (screenshots is not null) f.Save(Path.Combine(screenshots, "optimize-changes.png"));
            var problems = Check(f.Window);
            return problems.Count == 0 ? null : string.Join("\n        ", problems.Take(6));
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

        test("Layout: the title bar's theme button cycles System, Dark, Light and says so; the lilac follows the theme", () => Dispatch(() =>
        {
            var app = Avalonia.Application.Current!;
            var before = f.Settings.General.Theme;
            var general = f.Vm.Settings.General;
            try
            {
                if (f.Find<Button>(b => b.Name == "ThemeToggle") is not { IsEffectivelyVisible: true } toggle) return "no theme toggle in the title bar";
                general.SelectedTheme = GeneralSettingsViewModel.Themes.First(t => t.Value == AppTheme.System);
                string? Says(string mode, string next)
                {
                    var tip = ToolTip.GetTip(toggle) as string ?? "";
                    var name = Avalonia.Automation.AutomationProperties.GetName(toggle) ?? "";
                    if (!tip.StartsWith($"Theme: {mode}", StringComparison.Ordinal) || !tip.EndsWith($"Click for {next}.", StringComparison.Ordinal))
                        return $"the tooltip says \"{tip}\" in {mode}";
                    return name == tip ? null : $"the accessible name \"{name}\" isn't the tooltip";
                }
                if (Says("System", "Dark") is { } s0) return s0;
                f.Window.ToggleTheme();
                if (f.Settings.General.Theme != AppTheme.Dark) return $"from System the toggle chose {f.Settings.General.Theme}, not Dark";
                if (Says("Dark", "Light") is { } s1) return s1;
                f.Window.ToggleTheme();
                if (f.Settings.General.Theme != AppTheme.Light) return $"from Dark the toggle chose {f.Settings.General.Theme}, not Light";
                if (Says("Light", "System") is { } s2) return s2;
                app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
                if (!app.TryGetResource("SukiPrimaryColor", null, out var light) || light is not Avalonia.Media.Color { R: 0x61, G: 0x62, B: 0x83 })
                    return $"the light theme's primary is {light}, not #616283";
                f.Window.ToggleTheme();
                if (f.Settings.General.Theme != AppTheme.System) return $"from Light the toggle chose {f.Settings.General.Theme}, not System";
                f.Window.ToggleTheme();
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

        // The real failure: a GlassCard animated by its parent's template (which outranks a style), whose own template has
        // no RootPanel, threw inside the window's layout pass. It must lay out quietly, unanimated.
        test("Layout: a GlassCard animated by a parent template, with no RootPanel in its template, lays out without throwing", () => Dispatch(() =>
        {
            var host = f.Window.GetVisualDescendants().OfType<Panel>().First();
            var card = new SukiUI.Controls.GlassCard { Template = new Avalonia.Controls.Templates.FuncControlTemplate<SukiUI.Controls.GlassCard>((_, _) => new Border()) };
            card.SetValue(SukiUI.Controls.GlassCard.IsAnimatedProperty, true, Avalonia.Data.BindingPriority.Template);
            var later = new SukiUI.Controls.GlassCard();
            host.Children.Add(card);
            host.Children.Add(later);
            try
            {
                f.Layout();
                later.SetValue(SukiUI.Controls.GlassCard.IsAnimatedProperty, true, Avalonia.Data.BindingPriority.Template);
                later.Template = new Avalonia.Controls.Templates.FuncControlTemplate<SukiUI.Controls.GlassCard>((_, _) => new Border());
                f.Layout();
                return card.IsAnimated || later.IsAnimated ? "a GlassCard is still animated" : null;
            }
            catch (Exception ex) { return $"layout threw: {ex.Message}"; }
            finally
            {
                host.Children.Remove(card);
                host.Children.Remove(later);
                f.Layout();
            }
        }));

        test("Layout: the GlassCard report (written to the log when RootPanel can't be found) lists the window's cards and what set IsAnimated", () => Dispatch(() =>
        {
            f.Show("convert");
            f.Layout();
            // A card outside the window (as one in a tooltip or a drop-down is) is listed too, with its history.
            var outside = new SukiUI.Controls.GlassCard { Template = new Avalonia.Controls.Templates.FuncControlTemplate<SukiUI.Controls.GlassCard>((_, _) => new Border()) };
            var report = GlassCardReport.Describe([f.Window]);
            GC.KeepAlive(outside);
            if (!report.Contains("GlassCards;")) return "no window line";
            if (!report.Contains("#1 ")) return "no cards listed";
            if (!(report.Contains("IsAnimated=") && report.Contains(" from ") && report.Contains("in: "))) return "a card's details are missing";
            var moment = report[report.IndexOf("Card being templated last:", StringComparison.Ordinal)..report.IndexOf("Tracked GlassCards:", StringComparison.Ordinal)];
            Console.WriteLine(moment);
            if (!moment.Contains("first part")) return "the card being templated last isn't described";
            var others = report[report.IndexOf("Tracked GlassCards:", StringComparison.Ordinal)..];
            Console.WriteLine(string.Join(Environment.NewLine, others.Split('\n').Take(6)));
            return others.Contains("root=none (detached)") && others.Contains("history: ") && others.Contains("Template set") ? null : "the card outside the window isn't listed with its history";
        }));

        // SukiUI's progress bar template holds a GlassCard that looks up a "RootPanel" part during layout; a bar
        // drawn with it threw inside the window's layout pass during runs, and the lists stopped drawing.
        test("Layout: no progress bar, with a known amount or not, uses SukiUI's template (with its GlassCard)", () => Dispatch(() =>
        {
            var host = f.Window.GetVisualDescendants().OfType<Panel>().First();
            var bars = new[]
            {
                new ProgressBar { Value = 40, Width = 100 },
                new ProgressBar { IsIndeterminate = true, Width = 60 },
                new ProgressBar { Value = 10, Width = 100 },
            };
            foreach (var bar in bars) host.Children.Add(bar);
            try
            {
                f.Layout();
                bars[2].IsIndeterminate = true; // switching mode keeps the template
                f.Layout();
                foreach (var bar in bars)
                {
                    if (bar.GetVisualDescendants().OfType<SukiUI.Controls.GlassCard>().Any())
                        return $"a{(bar.IsIndeterminate ? "n indeterminate" : "")} bar has SukiUI's GlassCard";
                    var shown = bar.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.Name is "PART_Indicator" or "Slider").Select(b => b.Name).ToList();
                    var want = bar.IsIndeterminate ? "Slider" : "PART_Indicator";
                    if (shown.Count != 1 || shown[0] != want) return $"a{(bar.IsIndeterminate ? "n indeterminate" : "")} bar shows [{string.Join(", ", shown)}], not {want}";
                }
                var slider = bars[1].GetVisualDescendants().OfType<Border>().First(b => b.Name == "Slider");
                return slider.Bounds.Width is > 0 and < 60 ? null : $"the indeterminate bar's segment is {slider.Bounds.Width} wide";
            }
            finally
            {
                foreach (var bar in bars) host.Children.Remove(bar);
                f.Layout();
            }
        }));

        test("Layout: Settings shows Keys without opening Advanced; Enter saves a working folder; the status bar's log chip counts in words", () => Dispatch(() =>
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
            // A warning, so the chip shows (it's hidden while there's nothing to count).
            f.Vm.Log.Warn("layout test: a warning");
            f.Vm.Log.Drain();
            f.Layout();
            var counts = f.Find<TextBlock>(t => t.Classes.Contains("counts") && t.IsEffectivelyVisible && t.FindAncestorOfType<Button>()?.Name == "ProblemsChip");
            var expected = LogViewModel.DescribeProblems(f.Vm.Log.ErrorCount, f.Vm.Log.WarningCount);
            return counts?.Text == expected ? null : $"the status bar's log chip reads \"{counts?.Text ?? "(no chip)"}\", not \"{expected}\"";
        }));

        UxLayoutTests.Run(test, f, Dispatch);
        ConventionsTests.RunLayout(test, f, Dispatch);
        ActivityTests.RunLayout(test, f, Dispatch, screenshots);
        OptUxTests.RunLayout(test, f, Dispatch, screenshots);
        OptCleanupTests.RunLayout(test, f, Dispatch, screenshots);
        FolderLayoutTests.RunLayout(test, f, Dispatch, screenshots);
        SelectedFileTests.RunLayout(test, f, Dispatch, screenshots);
        QueueClearTests.RunLayout(test, f, Dispatch, screenshots);
        OptimizeTests.RunLayout(test, f, Dispatch, screenshots);
        DensityTests.RunLayout(test, f, Dispatch, screenshots);
        SettingsNavTests.RunLayout(test, f, Dispatch, screenshots);
        JobListTests.RunLayout(test, f, Dispatch, screenshots);
        SmallWindowTests.RunLayout(test, f, Dispatch, screenshots);

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

    /// <summary>The theme's accent colour, as the window has it now.</summary>
    private static Avalonia.Media.Color? Accent() =>
        Avalonia.Application.Current!.TryGetResource("AccentBrush", Avalonia.Application.Current.ActualThemeVariant, out var accent)
        && accent is Avalonia.Media.ISolidColorBrush brush ? brush.Color : null;

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
            new("Optimize with a row focused", "optimize", f => { f.Show("optimize"); f.FocusRow(f.Vm.Optimize); }, _ => { }),
            new("Convert with a row focused", "convert", f => { f.Show("convert"); f.FocusRow(f.Vm.Convert); }, _ => { }),
            new("Convert with Activity open on the Log tab", "convert-activity-log", f =>
            {
                f.Show("convert");
                f.Vm.Activity.Open(ActivityTab.Log);
            }, f => f.Vm.Activity.IsOpen = false),
            new("Convert with Activity open on the Queue tab", "convert-activity-queue", f =>
            {
                f.Show("convert");
                f.Vm.Activity.Open(ActivityTab.Queue);
            }, f => f.Vm.Activity.IsOpen = false),
            new("Convert with a PlayStation row on Custom (Zstandard) and others selected", "convert-custom", f => f.ShowCustom(), f => f.ResetCustom()),
            new("Verify with a row focused", "verify", f => { f.Show("verify"); f.FocusRow(f.Vm.Verify); }, _ => { }),
            new("Organize with a row focused", "organize", f => { f.Show("cleanup"); f.FocusRow(f.Vm.Cleanup); }, _ => { }),
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
            if (!f.Pump(() => vm.Optimize.Items.Count > 0 && !vm.Optimize.IsScanning)) f.Error = "Optimize didn't load the library folder";
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
