using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>
/// Run feedback in one place (UX review R1): the status bar is the summary (the queue, its progress, "2 failed",
/// the log's errors and warnings, Activity), and the Activity drawer above it holds the queue and the log as
/// two tabs. Ctrl+J opens and closes it; the old "log expanded" setting opens it on the Log tab.
/// </summary>
internal static class ActivityTests
{
    /// <summary>Without a window: the settings migration, the shortcut, the chip's words, and opening on errors.</summary>
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir)
    {
        test("Activity: a settings file with the log expanded opens Activity on its Log tab, and LogExpanded is dropped", () =>
        {
            (string Json, bool Open, ActivityTab Tab)[] cases =
            [
                ("""{ "SchemaVersion": 2, "General": { "LogExpanded": true } }""", true, ActivityTab.Log),
                ("""{ "SchemaVersion": 2, "General": { "LogExpanded": false } }""", false, ActivityTab.Queue),
                // Already migrated (a file an older build wrote again): the drawer's own setting wins.
                ("""{ "SchemaVersion": 2, "General": { "LogExpanded": true, "ActivityOpen": false, "ActivityTab": "Queue" } }""", false, ActivityTab.Queue),
            ];
            var previous = AppSettings.SettingsDirectoryOverride;
            try
            {
                for (var i = 0; i < cases.Length; i++)
                {
                    var folder = dir($"activity-migrate-{i}");
                    Directory.CreateDirectory(folder);
                    AppSettings.SettingsDirectoryOverride = folder;
                    var file = Path.Combine(folder, "settings.json");
                    File.WriteAllText(file, cases[i].Json);
                    var loaded = AppSettings.Load();
                    if (loaded.General.ActivityOpen != cases[i].Open || loaded.General.ActivityTab != cases[i].Tab)
                        return $"case {i}: open {loaded.General.ActivityOpen} on {loaded.General.ActivityTab}, not {cases[i].Open} on {cases[i].Tab}";
                    var saved = File.ReadAllText(file);
                    if (saved.Contains("LogExpanded")) return $"case {i}: LogExpanded is still in the saved file";
                    if (!saved.Contains("ActivityOpen")) return $"case {i}: the migrated file wasn't saved";
                }
                return null;
            }
            finally { AppSettings.SettingsDirectoryOverride = previous; }
        });

        test("Activity: Ctrl+J (⌘J) toggles it, and the shortcuts sheet lists it", () =>
        {
            if (!PageShortcuts.TogglesActivity(Key.J, KeyModifiers.Control) || !PageShortcuts.TogglesActivity(Key.J, KeyModifiers.Meta))
                return "Ctrl+J or ⌘J isn't the Activity shortcut";
            if (PageShortcuts.TogglesActivity(Key.J, KeyModifiers.None) || PageShortcuts.TogglesActivity(Key.J, KeyModifiers.Control | KeyModifiers.Shift))
                return "J without Ctrl, or Ctrl+Shift+J, toggles Activity";
            var sheet = PageShortcuts.Sheet(macOS: false);
            return sheet.Any(s => s.Keys == "Ctrl+J" && s.What.Contains("Activity")) ? null : "the sheet doesn't list Ctrl+J";
        });

        test("Activity: the status bar's chip names only the counts there are", () =>
        {
            var texts = new[] { LogViewModel.DescribeProblems(0, 0), LogViewModel.DescribeProblems(1, 3), LogViewModel.DescribeProblems(0, 2), LogViewModel.DescribeProblems(2, 0) };
            return string.Join(" | ", texts) == " | 1 error · 3 warnings | 2 warnings | 2 errors" ? null : string.Join(" | ", texts);
        });

        test("Activity: an error opens it on the Log tab when Settings › Log says so, without saving that as the user's choice", () =>
        {
            var previous = AppSettings.SettingsDirectoryOverride;
            AppSettings.SettingsDirectoryOverride = dir("activity-open-on-errors");
            try
            {
                foreach (var on in new[] { true, false })
                {
                    var settings = new AppSettings();
                    settings.General.OpenLogOnErrors = on;
                    var log = new LogViewModel(new NoDialogs(), settings);
                    var activity = new ActivityViewModel(settings, log);
                    log.Warn("only a warning");
                    Drain(log);
                    if (activity.IsOpen) return "a warning opened Activity";
                    log.Error("a run failed");
                    Drain(log);
                    if (on && !(activity.IsOpen && activity.IsLogTab)) return $"an error left Activity {(activity.IsOpen ? "open on " + activity.Tab : "closed")}";
                    if (!on && activity.IsOpen) return "an error opened Activity with the setting off";
                    if (settings.General.ActivityOpen || settings.General.ActivityTab != ActivityTab.Queue) return "opening for an error was saved as the user's choice";
                    // The user's own choice is saved.
                    activity.Toggle();
                    activity.Toggle();
                    if (on && !settings.General.ActivityOpen) return "the user's open drawer wasn't remembered";
                }
                return null;
            }
            finally { AppSettings.SettingsDirectoryOverride = previous; }
        });
    }

    /// <summary>In the real window (headless).</summary>
    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch, string? screenshots)
    {
        test("Activity: no log card or queue drawer under the page while it's closed; the page reaches down to the status bar", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("convert");
            f.Vm.Activity.IsOpen = false;
            f.Layout();
            if (f.Find<Border>(b => b.Name == "ActivityCard") is not { } card) return "no Activity card";
            if (card.IsEffectivelyVisible) return "the Activity card shows while closed";
            if (f.Find<ListBox>(l => l.Name is "LogList" or "QueueList" && l.IsEffectivelyVisible) is { } list) return $"{list.Name} shows while Activity is closed";
            var page = f.Find<JobListPanel>(p => p.IsEffectivelyVisible);
            var bar = f.Find<Border>(b => b.Name == "StatusBar");
            if (page is null || bar is null) return "no job list or status bar";
            var gap = bar.TranslatePoint(default, f.Window)!.Value.Y - page.TranslatePoint(new Avalonia.Point(0, page.Bounds.Height), f.Window)!.Value.Y;
            return gap is >= 0 and <= 16 ? null : $"{gap:F0} px between the page and the status bar";
        }));

        test("Activity: the status bar's button and Ctrl+J open and close the drawer; its tabs switch Queue and Log; ✕ closes it", () => dispatch(() =>
        {
            var activity = f.Vm.Activity;
            f.Resize(1366, 768);
            f.Show("convert");
            activity.IsOpen = false;
            f.Layout();
            try
            {
                if (f.Find<Button>(b => b.Name == "ActivityButton") is not { IsEffectivelyVisible: true } button) return "no Activity button";
                if (ToolTip.GetTip(button) as string is not { } tip || !tip.Contains(ActivityViewModel.ShortcutLabel)) return $"the button's tooltip is \"{ToolTip.GetTip(button)}\"";
                if (!button.IsEffectivelyEnabled) return "the Activity button is disabled";
                button.Command!.Execute(null);
                f.Layout();
                if (f.Find<Border>(b => b.Name == "ActivityCard") is not { IsEffectivelyVisible: true } card) return "the button didn't open the drawer";
                var wanted = MainWindow.FitActivity(activity.Height, card.Parent is Grid g ? g.Bounds.Height : 0);
                if (Math.Abs(card.Bounds.Height + card.Margin.Bottom - wanted) > 2) return $"the drawer is {card.Bounds.Height:F0} px tall, not about {wanted:F0}";

                activity.Tab = ActivityTab.Queue;
                f.Layout();
                if (f.Find<ListBox>(l => l.Name == "QueueList") is not { IsEffectivelyVisible: true }) return "the Queue tab doesn't show the queue";
                if (f.Find<RadioButton>(r => r.Name == "LogTab") is not { } logTab) return "no Log tab";
                logTab.IsChecked = true;
                f.Layout();
                if (!activity.IsLogTab || f.Find<ListBox>(l => l.Name == "LogList") is not { IsEffectivelyVisible: true }) return "the Log tab didn't show the log";
                if (f.Find<ListBox>(l => l.Name == "QueueList") is { IsEffectivelyVisible: true }) return "the queue still shows on the Log tab";
                if (f.Find<CheckBox>(c => c.Content as string == "Show only errors") is not { IsEffectivelyVisible: true }) return "the Log tab has no Show only errors";

                f.Find<Button>(b => b.Name == "ActivityClose")!.Command!.Execute(null);
                f.Layout();
                if (activity.IsOpen) return "✕ didn't close the drawer";

                f.Window.KeyPressQwerty(PhysicalKey.J, RawInputModifiers.Control);
                f.Layout();
                if (!activity.IsOpen || !activity.IsLogTab) return "Ctrl+J didn't open the drawer on the tab it was left on";
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "activity-log.png"));
                // From the filter box too: a text box doesn't use Ctrl+J.
                f.Find<TextBox>(t => t.Name == "FilterBox" && t.IsEffectivelyVisible)?.Focus();
                f.Window.KeyPressQwerty(PhysicalKey.J, RawInputModifiers.Control);
                f.Layout();
                return activity.IsOpen ? "Ctrl+J from the filter box didn't close the drawer" : null;
            }
            finally { activity.IsOpen = false; activity.Tab = ActivityTab.Queue; f.Layout(); }
        }));

        test("Activity: \"2 failed\" on the status bar filters the page to its failed rows; Escape in the filter box shows them all again", () => dispatch(() =>
        {
            var model = f.Vm.Queue;
            var tab = f.Vm.Convert;
            f.Resize(1366, 768);
            f.Show("verify");
            f.Layout();
            if (tab.Items.Count < 2) return "Convert needs two rows";
            var failedRow = tab.Items[^1];
            var batch = model.Queue.Submit("Convert", [
                new BatchJob("bad.cue", new NullReport(), _ => new JobOutcome(JobStatus.Failed, "Can't read the file"), _ => { }, _ => { }),
                new BatchJob("worse.cue", new NullReport(), _ => new JobOutcome(JobStatus.Failed, "Can't read the file"), _ => { }, _ => { }),
            ], new AppSettings(), new NullLog());
            try
            {
                if (!batch.Completion.Wait(TimeSpan.FromSeconds(20))) return "the jobs didn't finish";
                model.Refresh();
                failedRow.Finished(new JobOutcome(JobStatus.Failed, "Can't read the file"));
                f.Layout();
                if (f.Find<Button>(b => b.Name == "FailedBadge") is not { IsEffectivelyVisible: true } badge) return "no failure badge on the status bar";
                if (badge.Content as string != "2 failed") return $"the badge reads \"{badge.Content}\"";
                if (!badge.Classes.Contains("danger")) return "the badge isn't in the danger style";
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "activity-failed-badge.png"));
                // Verify (showing) has no failed rows: the badge goes to Convert, which has one.
                badge.Command!.Execute(null);
                f.Layout();
                f.Pump(() => tab.VisibleItems.Count == 1);
                if (f.Vm.SelectedPage.Id != "convert") return $"the badge showed {f.Vm.SelectedPage.Id}, not the page with the failed row";
                if (tab.Filter != BatchTabViewModel.FailedFilter) return $"the filter is \"{tab.Filter}\"";
                if (tab.VisibleItems.Count != 1 || tab.VisibleItems[0] != failedRow) return $"{tab.VisibleItems.Count} rows show, not just the failed one";
                if (f.Find<TextBox>(t => t.Name == "FilterBox" && t.IsEffectivelyVisible) is not { } box) return "no filter box";
                f.Pump(() => box.IsFocused);
                if (box.Text != BatchTabViewModel.FailedFilter) return $"the filter box reads \"{box.Text}\"";
                if (!box.IsFocused) return "the filter box doesn't have focus, so Escape wouldn't clear it";
                f.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                f.Layout();
                f.Pump(() => tab.VisibleItems.Count == tab.Items.Count);
                return tab.Filter == "" && tab.VisibleItems.Count == tab.Items.Count ? null : $"Escape left the filter \"{tab.Filter}\" with {tab.VisibleItems.Count} of {tab.Items.Count} rows";
            }
            finally
            {
                failedRow.Reset();
                tab.Filter = "";
                model.Queue.ClearFinished();
                model.Refresh();
                f.Layout();
            }
        }));

        test("Activity: the error chip opens the Log tab with only the errors showing", () => dispatch(() =>
        {
            var log = f.Vm.Log;
            var activity = f.Vm.Activity;
            var setting = f.Settings.General.OpenLogOnErrors;
            f.Settings.General.OpenLogOnErrors = false;
            f.Resize(1366, 768);
            f.Show("convert");
            activity.IsOpen = false;
            try
            {
                log.Error("activity test: an error");
                log.Drain();
                f.Layout();
                if (activity.IsOpen) return "the error opened Activity with Settings › Log off";
                if (f.Find<Button>(b => b.Name == "ProblemsChip") is not { IsEffectivelyVisible: true } chip) return "no error chip on the status bar";
                if (f.Find<TextBlock>(t => t.Classes.Contains("counts") && t.FindAncestorOfType<Button>() == chip) is not { } text || !text.Classes.Contains("error"))
                    return "the chip isn't red with an error";
                chip.Command!.Execute(null);
                f.Layout();
                return activity.IsOpen && activity.IsLogTab && log.ShowOnlyErrors ? null
                    : $"the chip left Activity {(activity.IsOpen ? "open on " + activity.Tab : "closed")}, only errors {log.ShowOnlyErrors}";
            }
            finally
            {
                log.ShowOnlyErrors = false;
                log.ClearCommand.Execute(null);
                activity.IsOpen = false;
                activity.Tab = ActivityTab.Queue;
                f.Settings.General.OpenLogOnErrors = setting;
                f.Layout();
            }
        }));

        test("Activity: at 900×600 the status bar fits (the summary drops time left and speed), and the open drawer leaves the page its run bar", () => dispatch(() =>
        {
            var activity = f.Vm.Activity;
            f.Resize(900, 600);
            f.Show("convert");
            try
            {
                foreach (var open in new[] { false, true })
                {
                    activity.IsOpen = open;
                    f.Layout();
                    if (f.Find<Border>(b => b.Name == "StatusBar") is not { } bar) return "no status bar";
                    if (!bar.Classes.Contains("narrow")) return "the status bar isn't narrow at 900 px";
                    if (f.Find<TextBlock>(t => t.Classes.Contains("statusshort")) is not { IsEffectivelyVisible: true }) return "the short summary doesn't show at 900 px";
                    var button = f.Find<Button>(b => b.Name == "ActivityButton")!;
                    var right = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width, 0), f.Window)!.Value.X;
                    if (right > f.Window.ClientSize.Width + 0.5) return $"the Activity button ends {right - f.Window.ClientSize.Width:F0} px past the window";
                    if (f.Find<Button>(b => b.Classes.Contains("runbutton") && b.IsEffectivelyVisible) is not { } run) return $"no run button (drawer {(open ? "open" : "closed")})";
                    var runBottom = run.TranslatePoint(new Avalonia.Point(0, run.Bounds.Height), f.Window)!.Value.Y;
                    var top = open ? f.Find<Border>(b => b.Name == "ActivityCard")!.TranslatePoint(default, f.Window)!.Value.Y : bar.TranslatePoint(default, f.Window)!.Value.Y;
                    if (runBottom > top + 0.5) return $"the run button runs under the {(open ? "drawer" : "status bar")}";
                    if (screenshots is not null) f.Save(Path.Combine(screenshots, $"activity-900x600-{(open ? "open" : "closed")}.png"));
                    var problems = LayoutTests.Check(f.Window);
                    if (problems.Count > 0) return $"drawer {(open ? "open" : "closed")}: " + string.Join("\n        ", problems.Take(6));
                }
                return null;
            }
            finally { activity.IsOpen = false; f.Layout(); }
        }));

        test("Run bar: one line of status (no progress bar), trimmed with the whole text in its tooltip", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            f.Show("convert");
            f.Layout();
            var panel = f.Find<JobListPanel>(p => p.IsEffectivelyVisible);
            if (panel is null) return "no job list";
            if (panel.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(s => s.Classes.Contains("runstatus")) is not { } status) return "no run status";
            if (status.GetVisualDescendants().OfType<ProgressBar>().Any()) return "the run bar still has a progress bar";
            var text = status.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "RunStatusText");
            if (text is null || text.TextWrapping != Avalonia.Media.TextWrapping.NoWrap || text.TextTrimming == Avalonia.Media.TextTrimming.None)
                return "the run status wraps or doesn't trim";
            return text.IsSet(ToolTip.TipProperty) ? null : "the run status has no tooltip";
        }));
    }

    private static void Drain(LogViewModel log) => log.Drain();

    private sealed class NullLog : ILog
    {
        public void Write(LogLevel level, string message) { }
    }
}
