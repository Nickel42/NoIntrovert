using Avalonia.Controls;
using Avalonia.VisualTree;
using NoIntrovert.Core;
using NoIntrovert.Core.Emulators;
using NoIntrovert.Core.Jobs;
using NoIntrovert.ViewModels;
using NoIntrovert.Views;

namespace NoIntrovert.Tests;

/// <summary>
/// The optional extras from the 2026-11-12 review: Show only errors in the
/// log, a queue that forgets old finished batches, a banner on Convert when
/// the CHD converter is missing, the reason under a ✕ in Works with, and
/// "Add folder as one archive…".
/// </summary>
internal static class OptUxTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir)
    {
        test("Log: Show only errors shows, copies and saves only errors, and the counts still count every line", () =>
            WithSettingsFolder(dir("optux-log-settings"), () =>
            {
                var app = new AppSettings();
                var dialogs = new ClipboardDialogs();
                var log = new LogViewModel(dialogs, app);
                log.Write(LogLevel.Debug, "a debug line");
                log.Write(LogLevel.Info, "an info line");
                log.Write(LogLevel.Warning, "a warning");
                log.Write(LogLevel.Error, "an error");
                log.Drain();
                var before = log.Entries.Count;
                if (before != 3) return $"{before} lines shown before the filter, not 3";

                log.ShowOnlyErrors = true;
                if (log.Entries.Count != 1 || log.Entries[0].Level != LogLevel.Error) return $"{log.Entries.Count} lines shown with only errors";
                log.ShowDebug = true;
                if (log.Entries.Count != 1) return "Show debug brought other lines back while only errors are shown";
                log.Write(LogLevel.Info, "another info line");
                log.Write(LogLevel.Error, "another error");
                log.Drain();
                if (log.Entries.Count != 2 || log.Entries.Any(e => e.Level != LogLevel.Error)) return "a new non-error line was shown with only errors";
                if (log.CountsText != LogViewModel.DescribeCounts(2, 1)) return $"the counts read '{log.CountsText}' with only errors";
                log.CopyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                var copied = dialogs.Clipboard?.Split(Environment.NewLine) ?? [];
                if (copied.Length != 2 || copied.Any(l => !l.Contains("[ERR]"))) return $"Copy wrote '{dialogs.Clipboard}'";
                if (log.ExportLines().Count() != 2) return "Save would write more than the errors";

                log.ShowOnlyErrors = false;
                // Show debug is still on: every line is back, debug included.
                return log.Entries.Count == 6 ? null : $"{log.Entries.Count} lines shown after turning it off, not 6";
            }));

        test("Queue: finished batches beyond the cap are forgotten, oldest first; failed and running ones stay", () =>
        {
            var queue = new JobQueue(() => 2);
            var log = new NullLog();
            JobGroup Run(string title, JobStatus status)
            {
                var job = new BatchJob(title, new NullReport(), _ => new JobOutcome(status, status.ToString()), _ => { }, _ => { });
                var group = queue.Submit(title, [job], new AppSettings(), log);
                group.Completion.Wait(TimeSpan.FromSeconds(20));
                return group;
            }
            var failed = Run("failed", JobStatus.Failed);
            var first = Run("first", JobStatus.Succeeded);
            var later = Enumerable.Range(0, JobQueue.KeepFinishedGroups + 4).Select(i => Run($"batch {i}", JobStatus.Succeeded)).ToList();
            if (!later[^1].IsFinished) return "the batches didn't finish";
            using var release = new ManualResetEventSlim();
            var running = queue.Submit("running", [new BatchJob("wait", new NullReport(), _ =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return new JobOutcome(JobStatus.Succeeded, "ok");
            }, _ => { }, _ => { })], new AppSettings(), log);
            try
            {
                var groups = queue.Jobs.Select(j => j.Group).Distinct().ToList();
                if (!groups.Contains(failed)) return "the failed batch was removed";
                if (groups.Contains(first)) return "the oldest finished batch is still there";
                var finished = groups.Count(g => g.IsFinished && g != failed);
                if (finished != JobQueue.KeepFinishedGroups) return $"{finished} finished batches kept, not {JobQueue.KeepFinishedGroups}";
                if (!groups.Contains(later[^1])) return "the newest batch was removed";
                // Nothing to trim at the cap; trimming to none still leaves the running and failed batches.
                if (queue.TrimFinished() != 0) return "a second trim at the cap removed more";
                queue.TrimFinished(keep: 0);
                groups = queue.Jobs.Select(j => j.Group).Distinct().ToList();
                return groups.Count == 2 && groups.Contains(running) && groups.Contains(failed) ? null : $"{groups.Count} batches after trimming to none";
            }
            finally
            {
                release.Set();
                running.Completion.Wait(TimeSpan.FromSeconds(20));
            }
        });

        test("Queue: with 2 jobs at a time, a job added while a long one runs starts at once", () =>
        {
            if (Environment.ProcessorCount < 2) return null; // Concurrency is capped at the cores there are
            var queue = new JobQueue(() => 2);
            var log = new NullLog();
            using var release = new ManualResetEventSlim();
            var slow = queue.Submit("slow", [new BatchJob("wait", new NullReport(), _ =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return new JobOutcome(JobStatus.Succeeded, "ok");
            }, _ => { }, _ => { })], new AppSettings(), log);
            try
            {
                var quick = queue.Submit("quick", [new BatchJob("quick", new NullReport(), _ => new JobOutcome(JobStatus.Succeeded, "ok"), _ => { }, _ => { })],
                    new AppSettings(), log);
                if (!quick.Completion.Wait(TimeSpan.FromSeconds(10))) return "the second job waited for the first";
                return slow.IsFinished ? "the long job finished early" : null;
            }
            finally
            {
                release.Set();
                slow.Completion.Wait(TimeSpan.FromSeconds(20));
            }
        });
    }

    /// <summary>In the real window (headless): the banner, the reasons, the longer button label and the log toggle.</summary>
    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch, string? screenshots)
    {
        test("Convert: a banner says when the CHD converter is missing or blocked, and what to do", () => dispatch(() =>
        {
            var convert = f.Vm.Convert;
            if (convert.ChdConverterMissing) return "the banner is on although chdkit loaded";
            f.Show("convert");
            f.Resize(1366, 768);
            if (f.Find<Border>(b => b.Name == "ChdMissingBanner") is not { IsEffectivelyVisible: false }) return "the banner shows although chdkit loaded";
            convert.ChdConverterMissing = true;
            try
            {
                foreach (var (width, height) in new[] { (900, 600), (1366, 768) })
                {
                    f.Resize(width, height);
                    f.FocusRow(convert);
                    f.Layout();
                    var banner = f.Find<Border>(b => b.Name == "ChdMissingBanner");
                    if (banner is not { IsEffectivelyVisible: true }) return $"{width}×{height}: no banner";
                    var texts = banner.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
                    if (!texts.Any(t => t.Contains("Reinstall NoIntrovert")) || !texts.Any(t => t.Contains("antivirus"))) return "the banner doesn't say what to do";
                    if (screenshots is not null) f.Save(Path.Combine(screenshots, $"optux-chd-banner-{width}x{height}.png"));
                    var problems = LayoutTests.Check(f.Window);
                    if (problems.Count > 0) return $"{width}×{height}: " + string.Join("\n        ", problems.Take(6));
                }
                return null;
            }
            finally
            {
                convert.ChdConverterMissing = false;
                convert.FocusedItem = null;
                f.Layout();
            }
        }));

        test("Works with: a ✕ entry shows why under its name, and only its own row grows", () => dispatch(() =>
        {
            f.Resize(1366, 768);
            f.ShowCustom();
            try
            {
                var works = f.Vm.Convert.Panel.WorksWith;
                var cross = works.Cores.Concat(works.Standalone).FirstOrDefault(i => i.State == SupportState.DoesNot);
                if (cross is null) return "no ✕ entry with Zstandard codecs to check";
                var list = f.Find<WorksWithList>(_ => true);
                if (list is null) return "no Works with list";
                // Scroll the list into view, so it's laid out where it's seen.
                list.BringIntoView();
                f.Layout();
                if (screenshots is not null) f.Save(Path.Combine(screenshots, "optux-works-with-reasons.png"));
                var reasons = list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("reason") && t.IsEffectivelyVisible).ToList();
                if (reasons.Count == 0) return "no reason shows under a ✕ entry";
                if (!reasons.Any(r => r.Text == cross.Detail)) return $"the reason doesn't read '{cross.Detail}'";
                var entries = list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("entry")).ToList();
                var crossed = works.Cores.Concat(works.Standalone).Count(i => i.State == SupportState.DoesNot);
                if (reasons.Count != crossed) return $"{reasons.Count} reasons shown for {crossed} ✕ entries";
                // A row with no ✕ in it stays one line tall.
                // The cells: each entry's item container, as tall as its row.
                var heights = entries.Select(e => (e.GetVisualParent() as Avalonia.Visual ?? e).Bounds.Height).ToList();
                if (heights.Count == 0) return "no entries";
                var allCrossed = works.Cores.All(i => i.State == SupportState.DoesNot) && works.Standalone.All(i => i.State == SupportState.DoesNot);
                if (!allCrossed && heights.Min() >= heights.Max() - 1) return $"every entry is {heights.Max():F0} px tall: a reason made every row taller";
                var problems = LayoutTests.Check(f.Window);
                return problems.Count == 0 ? null : string.Join("\n        ", problems.Take(6));
            }
            finally { f.ResetCustom(); }
        }));

        test("Convert: 'Add folder as one archive…' fits the toolbar at every size", () => dispatch(() =>
        {
            f.Show("convert");
            foreach (var (width, height) in LayoutTests.Sizes)
            {
                f.Resize(width, height);
                f.Layout();
                if (f.Find<Button>(b => b.Content as string == "Pack a folder…" && b.IsEffectivelyVisible) is not null) return "the old 'Pack a folder…' label is still shown";
                var button = f.Find<Button>(b => b.Content as string == "Add folder as one archive…" && b.IsEffectivelyVisible);
                if (button is null) return $"{width}×{height}: no 'Add folder as one archive…' button";
                if (width == 900 && screenshots is not null) f.Save(Path.Combine(screenshots, "optux-add-folder-archive-900.png"));
                // Check finds a cut-off label or a button pushed out of its card.
                var problems = LayoutTests.Check(f.Window);
                if (problems.Count > 0) return $"{width}×{height}: " + string.Join("\n        ", problems.Take(6));
            }
            return null;
        }));

        test("Log: Show only errors sits next to Show debug, which is off while it's on", () => dispatch(() =>
        {
            var log = f.Vm.Log;
            var expanded = log.IsExpanded;
            log.IsExpanded = true;
            try
            {
                foreach (var (width, height) in new[] { (900, 600), (1366, 768) })
                {
                    f.Resize(width, height);
                    f.Show("convert");
                    f.Layout();
                    var only = f.Find<CheckBox>(c => c.Content as string == "Show only errors");
                    var debug = f.Find<CheckBox>(c => c.Content as string == "Show debug");
                    if (only is not { IsEffectivelyVisible: true } || debug is null) return $"{width}×{height}: no Show only errors box";
                    log.ShowOnlyErrors = true;
                    f.Layout();
                    var debugOff = !debug.IsEffectivelyEnabled;
                    if (width == 900 && screenshots is not null) f.Save(Path.Combine(screenshots, "optux-log-only-errors-900.png"));
                    log.ShowOnlyErrors = false;
                    f.Layout();
                    if (!debugOff) return "Show debug can still be changed while only errors are shown";
                    if (!debug.IsEffectivelyEnabled) return "Show debug stayed off after Show only errors was turned off";
                    var problems = LayoutTests.Check(f.Window);
                    if (problems.Count > 0) return $"{width}×{height}: " + string.Join("\n        ", problems.Take(6));
                }
                return null;
            }
            finally { log.ShowOnlyErrors = false; log.IsExpanded = expanded; f.Layout(); }
        }));
    }

    private static string? WithSettingsFolder(string folder, Func<string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try { return body(); }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }

    private sealed class NullLog : ILog
    {
        public void Write(LogLevel level, string message) { }
    }

    /// <summary>Keeps what Copy put on the clipboard.</summary>
    private sealed class ClipboardDialogs : NoIntrovert.Services.IDialogs
    {
        public string? Clipboard { get; private set; }
        public Task SetClipboardAsync(string text) { Clipboard = text; return Task.CompletedTask; }
        public Task OpenFolderAsync(string folder) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, IReadOnlyList<string> patterns) => throw new NotSupportedException();
        public Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns) => throw new NotSupportedException();
        public Task<string?> PickFolderAsync(string title) => throw new NotSupportedException();
        public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string pattern) => throw new NotSupportedException();
    }
}
