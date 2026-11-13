using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using NoIntrovert.Core;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Library;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>
/// The 2026-11-12 review's optional extras: per-kind boxes on the Cleanup
/// (Organize) page, a Set up keys link on rows that failed for missing keys
/// that opens Settings with the Keys card in view, and 3DS and Switch key
/// sections in the Convert side panel, like the Wii U one.
/// </summary>
internal static class OptCleanupTests
{
    private static CleanupStep Step(CleanupKind kind, int n, string root = "C:/lib") =>
        new() { Kind = kind, GameKey = $"{kind}{n}", Game = $"{kind} game {n}", Root = root, Sources = [$"{root}/{kind}{n}.bin"], Reason = "" };

    /// <summary>2 to convert, 1 to rename, 3 to move, a clash and one left alone.</summary>
    internal static CleanupPlan SamplePlan(string root = "C:/lib") => new(
    [
        Step(CleanupKind.Convert, 1, root), Step(CleanupKind.Convert, 2, root), Step(CleanupKind.Rename, 1, root),
        Step(CleanupKind.Move, 1, root), Step(CleanupKind.Move, 2, root), Step(CleanupKind.Move, 3, root),
        Step(CleanupKind.Clash, 1, root), Step(CleanupKind.Untouched, 1, root),
    ]);

    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir)
    {
        test("Cleanup: one box per kind of step, with its count, ticks or unticks that kind, half-ticked when some are", () =>
            WithSettingsFolder(dir("opt-kinds-settings"), () =>
            {
                var cleanup = NewCleanup();
                cleanup.ShowPlan(SamplePlan());
                var labels = string.Join(", ", cleanup.Kinds.Select(k => k.Label));
                if (labels != "Convert (2), Rename (1), Move (3)") return $"the boxes read \"{labels}\"";
                var move = cleanup.Kinds.Single(k => k.Kind == CleanupKind.Move);
                var convert = cleanup.Kinds.Single(k => k.Kind == CleanupKind.Convert);
                if (move.IsChecked != true || cleanup.AllEnabled != true) return "a new plan's boxes aren't all ticked";

                move.ToggleCommand.Execute(null);
                if (cleanup.Steps.Any(s => s.Step.Kind == CleanupKind.Move && s.Enabled)) return "unticking Move left a move ticked";
                if (cleanup.Steps.Any(s => s.Step.Kind != CleanupKind.Move && s.CanRun && !s.Enabled)) return "unticking Move unticked another kind";
                if (move.IsChecked != false || cleanup.AllEnabled is not null) return $"Move reads {move.IsChecked}, tick all {cleanup.AllEnabled}";
                if (cleanup.Summary.Contains("to move")) return $"the summary still counts moves: \"{cleanup.Summary}\"";

                var changed = false;
                convert.PropertyChanged += (_, e) => changed |= e.PropertyName == nameof(CleanupKindToggle.IsChecked);
                cleanup.Steps.First(s => s.Step.Kind == CleanupKind.Convert).Enabled = false;
                if (!changed || convert.IsChecked is not null) return $"one of two conversions ticked reads {convert.IsChecked}, not half";
                convert.ToggleCommand.Execute(null);
                if (convert.IsChecked != true) return "clicking a half-ticked box didn't tick every step of its kind";

                cleanup.AllEnabled = false;
                if (cleanup.Kinds.Any(k => k.IsChecked != false)) return "unticking all left a kind's box on";
                cleanup.AllEnabled = true;
                if (cleanup.Kinds.Any(k => k.IsChecked != true)) return "ticking all left a kind's box off";
                return cleanup.Steps.Single(s => s.Step.Kind == CleanupKind.Clash).Enabled
                       && cleanup.Kinds.All(k => k.Kind is not (CleanupKind.Clash or CleanupKind.Untouched))
                    ? null : "a clash or a game left alone has a box";
            }));

        test("Cleanup: a plan with nothing to run has no per-kind boxes", () =>
            WithSettingsFolder(dir("opt-kinds-empty-settings"), () =>
            {
                var cleanup = NewCleanup();
                cleanup.ShowPlan(SamplePlan());
                cleanup.ShowPlan(new CleanupPlan([Step(CleanupKind.Clash, 1), Step(CleanupKind.Untouched, 1)]));
                return cleanup.Kinds.Count == 0 && !cleanup.HasRunnableSteps ? null : $"{cleanup.Kinds.Count} box(es) for a plan of a clash and a game left alone";
            }));

        test("Keys: a row that failed for missing Wii U, 3DS or Switch keys needs keys; other rows don't", () =>
        {
            var row = new JobItemViewModel(Path.Combine(dir("opt-keys-row"), "Game.3ds"));
            foreach (var message in new[] { $"3DS key missing (slot0x2CKeyX). {Core.Ctr.CtrJobs.NoKeysHelp}", Core.Switch.SwitchJobs.NoKeysHelp, Core.WiiU.WuaJobs.NoKeysMessage })
            {
                row.Reset();
                if (row.NeedsKeys) return "a queued row needs keys";
                var changed = false;
                row.PropertyChanged += Watch;
                row.Finished(JobOutcome.Failed(message));
                row.PropertyChanged -= Watch;
                if (!row.NeedsKeys || !changed) return $"a row that failed with \"{message}\" doesn't offer the keys link";
                void Watch(object? _, System.ComponentModel.PropertyChangedEventArgs e) => changed |= e.PropertyName == nameof(JobItemViewModel.NeedsKeys);
            }
            row.Reset();
            row.Finished(JobOutcome.Failed("not a 3DS game image or .cia"));
            if (row.NeedsKeys) return "a failure that isn't about keys offers the keys link";
            row.Reset();
            return row.NeedsKeys ? "a reset row still offers the keys link" : null;
        });

        test("Keys: Set up keys asks the main window, and Settings is asked to bring the Keys card into view", () =>
            WithSettingsFolder(dir("opt-keys-link-settings"), () =>
            {
                var dialogs = new NoDialogs();
                var app = new AppSettings();
                var settings = new SettingsViewModel(app, dialogs);
                var convert = new ConvertViewModel(settings, new LogViewModel(dialogs, app), dialogs, new JobQueue(() => 1));
                var asked = 0;
                convert.OpenKeysRequested = () => asked++;
                convert.OpenKeysCommand.Execute(null);
                if (asked != 1) return "Set up keys didn't ask for Settings › Keys";
                var raised = false;
                settings.ShowKeysRequested += (_, _) => raised = true;
                settings.ShowKeys();
                return raised && settings.ShowKeysPending ? null : "Settings wasn't asked to show its Keys card";
            }));

        var ctrFixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", "lib-3ds-kit", "tests", "fixtures"));
        test("Keys: encrypted 3DS images, .cia files and archives need keys; decrypted images and other files don't", () =>
        {
            var folder = dir("opt-3ds-files");
            var archive = Path.Combine(folder, "Game.7z");
            var junk = Path.Combine(folder, "Not a game.3ds");
            File.WriteAllBytes(archive, new byte[64]);
            File.WriteAllBytes(junk, new byte[4096]);
            if (!KeysLink.ThreeDsFileNeedsKeys(archive)) return "an archive isn't taken to need keys";
            if (KeysLink.ThreeDsFileNeedsKeys(junk)) return "a file that isn't a 3DS game needs keys";
            if (!Directory.Exists(ctrFixtures)) return null; // lib-3ds-kit's fixtures aren't checked out
            if (!KeysLink.ThreeDsFileNeedsKeys(Path.Combine(ctrFixtures, "game_a.3ds"))) return "an encrypted image doesn't need keys";
            if (!KeysLink.ThreeDsFileNeedsKeys(Path.Combine(ctrFixtures, "game_b.cia"))) return "a .cia doesn't need keys";
            return KeysLink.ThreeDsFileNeedsKeys(Path.Combine(ctrFixtures, "expected", "game_a.decrypted.3ds")) ? "a decrypted image needs keys" : null;
        });

        test("Convert side panel: 3DS and Switch key sections follow the selected rows, like Wii U's", () =>
            WithSettingsFolder(dir("opt-panel-settings"), () =>
            {
                var dialogs = new NoDialogs();
                var app = new AppSettings();
                var convert = new ConvertViewModel(new SettingsViewModel(app, dialogs), new LogViewModel(dialogs, app), dialogs, new JobQueue(() => 1));
                var folder = dir("opt-panel-files");
                JobItemViewModel Row(string name, string? system)
                {
                    File.WriteAllBytes(Path.Combine(folder, name), new byte[4096]);
                    var row = new JobItemViewModel(Path.Combine(folder, name)) { SystemId = system };
                    convert.Items.Add(row);
                    return row;
                }
                var nsp = Row("Game.nsp", KeysLink.SwitchSystem);
                var zip = Row("Game.7z", KeysLink.ThreeDsSystem);
                var plain = Row("Not a game.3ds", KeysLink.ThreeDsSystem);
                var nes = Row("Game.nes", "nes");
                string? Select(string what, bool threeDs, bool switchKeys, params JobItemViewModel[] rows)
                {
                    convert.SelectedItems = rows.ToList();
                    convert.FocusedItem = rows.LastOrDefault();
                    var p = convert.Panel;
                    p.Refresh(); // what the page's coalesced refresh does on the UI thread
                    return p.ShowsThreeDsKeys == threeDs && p.ShowsSwitchKeys == switchKeys
                        ? null : $"{what}: 3DS keys {(p.ShowsThreeDsKeys ? "show" : "hidden")}, Switch keys {(p.ShowsSwitchKeys ? "show" : "hidden")}";
                }
                return Select("a Switch file", false, true, nsp)
                       ?? Select("a 3DS archive", true, false, zip)
                       ?? Select("a 3DS file that needs no keys", false, false, plain)
                       ?? Select("a NES file with a Switch file", false, true, nes, nsp)
                       ?? Select("both", true, true, nsp, zip)
                       ?? Select("a NES file", false, false, nes);
            }));
    }

    // ---- In the real window (headless), from LayoutTests ----

    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch, string? screenshots)
    {
        test("Organize: the per-kind boxes show their counts, fit, and a click unticks that kind", () => dispatch(() =>
        {
            f.Resize(1180, 820);
            f.Show("cleanup");
            var cleanup = f.Vm.Cleanup;
            var summary = cleanup.Summary;
            cleanup.ShowPlan(SamplePlan(f.LibraryFolder));
            try
            {
                f.Layout();
                var boxes = f.Window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.DataContext is CleanupKindToggle && c.IsEffectivelyVisible).ToList();
                var labels = string.Join(", ", boxes.Select(b => b.Content));
                if (labels != "Convert (2), Rename (1), Move (3)") return $"the boxes read \"{labels}\"";
                var move = boxes[2];
                if (Avalonia.Automation.AutomationProperties.GetName(move) != "Run every move step") return "the Move box has no name for screen readers";
                // Toggled as a screen reader does (the box's own click: it toggles, then runs its command).
                var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(move);
                if (peer.GetProvider<Avalonia.Automation.Provider.IToggleProvider>() is not { } toggle) return "the Move box can't be toggled by a screen reader";
                toggle.Toggle();
                f.Layout();
                if (cleanup.Steps.Any(s => s.Step.Kind == CleanupKind.Move && s.Enabled)) return $"a click on Move left a move ticked (the box reads {move.IsChecked})";
                if (move.IsChecked != false) return $"the Move box reads {move.IsChecked} after the click";
                var tickAll = f.Find<CheckBox>(c => c.Name == "TickAll");
                if (tickAll?.IsChecked is not null) return $"tick all reads {tickAll?.IsChecked}, not half";
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "organize-kinds.png"));
                var problems = LayoutTests.Check(f.Window);
                return problems.Count == 0 ? null : string.Join("\n        ", problems.Take(6));
            }
            finally
            {
                cleanup.ShowPlan(new CleanupPlan([]));
                cleanup.Summary = summary;
                f.Layout();
            }
        }));

        test("Convert: a row that failed for missing keys has Set up keys, which shows Settings with the Keys card in view", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("convert");
            var row = f.Vm.Convert.Items[0];
            row.Finished(JobOutcome.Failed($"3DS key missing (slot0x2CKeyX). {Core.Ctr.CtrJobs.NoKeysHelp}"));
            try
            {
                f.Layout();
                var link = f.Find<HyperlinkButton>(b => b.Content as string == "Set up keys" && b.IsEffectivelyVisible);
                if (link is null) return "the failed row has no Set up keys link";
                if (f.Find<HyperlinkButton>(b => b.Content as string == "Set up keys" && b.IsEffectivelyVisible && b.DataContext != row) is not null)
                    return "a row that didn't fail shows Set up keys";
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "convert-keys-link.png"));
                var problems = LayoutTests.Check(f.Window);
                if (problems.Count > 0) return string.Join("\n        ", problems.Take(6));

                link.Command!.Execute(link.CommandParameter);
                f.Layout();
                if (!f.Pump(() => !f.Vm.Settings.ShowKeysPending)) return "the Settings page never brought the Keys card into view";
                if (f.Vm.SelectedPage.Id != "settings") return $"Set up keys opened {f.Vm.SelectedPage.Id}";
                var shown = KeysInView(f);
                if (shown is not null) return shown;
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "settings-keys-deep-link.png"));

                // Already on Settings, scrolled to the top: the link scrolls again.
                var scroll = f.Find<Border>(b => b.Name == "KeysCard")!.FindAncestorOfType<ScrollViewer>()!;
                scroll.Offset = default;
                f.Layout();
                f.Vm.ShowKeys();
                if (!f.Pump(() => !f.Vm.Settings.ShowKeysPending)) return "on the Settings page, the Keys card wasn't brought into view";
                return KeysInView(f);
            }
            finally
            {
                row.Reset();
                f.Show("convert");
                f.Layout();
            }
        }));

        test("Convert: the side panel's 3DS and Switch key sections show for the files that need them, and fit", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("convert");
            var tab = f.Vm.Convert;
            var folder = Path.Combine(f.LibraryFolder, "..", "layout-keys");
            Directory.CreateDirectory(folder);
            var added = new List<JobItemViewModel>();
            JobItemViewModel Row(string name, string system)
            {
                File.WriteAllBytes(Path.Combine(folder, name), new byte[4096]);
                var row = new JobItemViewModel(Path.Combine(folder, name)) { SystemId = system };
                tab.Items.Add(row);
                added.Add(row);
                return row;
            }
            var nsp = Row("Some Switch Game [0100000000010000][v0].nsp", KeysLink.SwitchSystem);
            var zip = Row("Some 3DS Game (USA).7z", KeysLink.ThreeDsSystem);
            try
            {
                f.Layout();
                var list = f.Window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "List" && l.IsEffectivelyVisible);
                list.SelectedItems!.Clear();
                list.SelectedItems.Add(nsp);
                list.SelectedItems.Add(zip);
                tab.FocusedItem = zip;
                f.Layout();
                var threeDs = f.Find<StackPanel>(p => p.Name == "ThreeDsKeys");
                var switchKeys = f.Find<StackPanel>(p => p.Name == "SwitchKeys");
                if (threeDs is not { IsEffectivelyVisible: true }) return "no 3DS keys section for a selected 3DS archive";
                if (switchKeys is not { IsEffectivelyVisible: true }) return "no Switch keys section for a selected Switch file";
                if (f.Find<TextBlock>(t => t.Text == f.Vm.Settings.Keys.SwitchKeysSummary && t.IsEffectivelyVisible) is null) return "the Switch section doesn't say which prod.keys is used";
                var panel = f.Window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(v => v.DataContext is SidePanelViewModel);
                panel?.ScrollToEnd();
                f.Layout();
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "convert-3ds-switch-keys.png"));
                var problems = LayoutTests.Check(f.Window);
                if (problems.Count > 0) return string.Join("\n        ", problems.Take(6));
                list.SelectedItems.Clear();
                tab.FocusedItem = tab.Items.First(i => !added.Contains(i));
                f.Layout();
                return threeDs.IsEffectivelyVisible || switchKeys.IsEffectivelyVisible ? "the key sections stay for a row that needs no keys" : null;
            }
            finally
            {
                foreach (var row in added) tab.Items.Remove(row);
                f.Window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(v => v.DataContext is SidePanelViewModel)?.ScrollToHome();
                f.Layout();
            }
        }));
    }

    /// <summary>Null when the Keys card's top is in the window's view and focus is on one of its buttons.</summary>
    private static string? KeysInView(LayoutTests.Fixture f)
    {
        f.Layout();
        var card = f.Find<Border>(b => b.Name == "KeysCard");
        if (card is not { IsEffectivelyVisible: true }) return "the Keys card isn't showing";
        var scroll = card.FindAncestorOfType<ScrollViewer>();
        if (scroll is null || card.TranslatePoint(default, scroll) is not { } top) return "the Keys card isn't in a scrolling page";
        if (top.Y < -1 || top.Y > scroll.Viewport.Height / 2) return $"the Keys card's top is at {top.Y:F0} of a {scroll.Viewport.Height:F0} px page";
        var focused = f.Window.FocusManager?.GetFocusedElement() as Visual;
        return focused is not null && card.IsVisualAncestorOf(focused) ? null : $"focus is on {focused}, not in the Keys card";
    }

    private static CleanupViewModel NewCleanup()
    {
        var dialogs = new NoDialogs();
        var app = new AppSettings();
        return new CleanupViewModel(new SettingsViewModel(app, dialogs), new LogViewModel(dialogs, app), new JobQueue(() => 1), () => new LibraryIndex(), dialogs);
    }

    private static string? WithSettingsFolder(string folder, Func<string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try { return body(); }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }
}
