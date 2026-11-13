using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>
/// The queue drawer: Clear finished clears finished jobs even while their batch still runs (but not one a queued
/// job is waiting on), and a long result or stage trims in its column instead of running over the progress bar.
/// </summary>
internal static class QueueClearTests
{
    private static BatchJob Job(string name, Func<JobOutcome> work, IReadOnlyList<BatchJob>? after = null) =>
        new(name, new NullReport(), _ => work(), _ => { }, _ => { }, After: after);

    private static bool WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < until) Thread.Sleep(10);
        return done();
    }

    public static void Run(Action<string, Func<string?>> test)
    {
        test("Queue: Clear finished clears finished jobs while their batch still runs, and keeps one a queued job waits on", () =>
        {
            if (Environment.ProcessorCount < 2) return null; // two jobs at once need two cores
            var queue = new JobQueue(() => 2);
            var log = new NullLog();
            using var release = new ManualResetEventSlim();
            var slow = Job("slow", () => { release.Wait(TimeSpan.FromSeconds(30)); return new JobOutcome(JobStatus.Succeeded, "slow done"); });
            var quick = Job("quick", () => new JobOutcome(JobStatus.Succeeded, "quick done"));
            var disc = Job("disc", () => new JobOutcome(JobStatus.Succeeded, "disc done"));
            // Waits on the disc and on the slow job, so it's still queued when the disc has finished.
            var playlist = Job("playlist", () => new JobOutcome(JobStatus.Succeeded, "m3u"), after: [disc, slow]);
            var batch = queue.Submit("batch", [slow, quick, disc, playlist], new AppSettings(), log);
            try
            {
                QueuedState StateOf(BatchJob spec) => queue.Jobs.First(j => ReferenceEquals(j.Spec, spec)).State;
                if (!WaitFor(() => StateOf(quick) == QueuedState.Finished && StateOf(disc) == QueuedState.Finished)) return "the quick jobs didn't finish";
                if (batch.IsFinished) return "the batch finished early";
                queue.ClearFinished();
                var left = queue.Jobs.Select(j => j.Spec.Name).ToList();
                if (left.Contains("quick")) return "a finished job stayed while its batch ran: " + string.Join(", ", left);
                if (!left.Contains("disc")) return "the job the playlist waits on was cleared";
                if (!left.Contains("slow") || !left.Contains("playlist")) return "an unfinished job was cleared: " + string.Join(", ", left);
                release.Set();
                if (!batch.Completion.Wait(TimeSpan.FromSeconds(20))) return "the batch didn't finish";
                if (batch.Jobs.First(j => ReferenceEquals(j.Spec, playlist)).Outcome?.Status != JobStatus.Succeeded) return "the playlist didn't run after its disc";
                queue.ClearFinished();
                return queue.Jobs.Count == 0 ? null : "jobs left after the batch finished: " + queue.Jobs.Count;
            }
            finally
            {
                release.Set();
                batch.Completion.Wait(TimeSpan.FromSeconds(20));
            }
        });
    }

    /// <summary>In the real window (headless): a long result stays in its column.</summary>
    public static void RunLayout(Action<string, Func<string?>> test, LayoutTests.Fixture f, Func<Func<string?>, string?> dispatch, string? screenshots)
    {
        test("Queue: a long result trims in its column instead of running over the progress bar", () => dispatch(() =>
        {
            var model = f.Vm.Queue;
            const string longName = "Gundam - Side Story 0079 - Rise from the Ashes v1.003 (2000)(Bandai)(US)[!].chd";
            var batch = model.Queue.Submit("Convert", [Job("Gundam.cue", () => new JobOutcome(JobStatus.Succeeded, longName))], new AppSettings(), new NullLog());
            try
            {
                if (!batch.Completion.Wait(TimeSpan.FromSeconds(20))) return "the job didn't finish";
                model.Refresh();
                model.IsOpen = true;
                foreach (var (width, height) in LayoutTests.Sizes)
                {
                    f.Resize(width, height);
                    f.Layout();
                    var stage = f.Find<TextBlock>(t => t.Text == longName && t.IsEffectivelyVisible && t.FindAncestorOfType<ListBoxItem>() is not null
                                                       && t.TextTrimming != Avalonia.Media.TextTrimming.None && t.ToolTip() == longName);
                    if (stage?.FindAncestorOfType<ListBoxItem>()?.GetVisualDescendants().OfType<ProgressBar>().FirstOrDefault() is not { } bar)
                        return $"{width}×{height}: no queue row with the result and a progress bar";
                    var right = stage.TranslatePoint(new Point(stage.Bounds.Width, 0), f.Window)!.Value.X;
                    var barLeft = bar.TranslatePoint(new Point(0, 0), f.Window)!.Value.X;
                    if (right > barLeft + 0.5) return $"{width}×{height}: the result runs {right - barLeft:F0} px over the progress bar";
                    if (width == 1366 && screenshots is not null) f.Save(Path.Combine(screenshots, "queue-long-result.png"));
                }
                // The drawer's Clear finished button is enabled, and clicking it empties the list.
                if (f.Find<Button>(b => b.Content as string == "Clear finished" && b.IsEffectivelyVisible) is not { } clear) return "no Clear finished button";
                if (!clear.IsEffectivelyEnabled || clear.Command?.CanExecute(null) != true) return "Clear finished is disabled with a finished job in the queue";
                clear.Command.Execute(null);
                f.Pump(() => model.Rows.Count == 0);
                return model.Rows.Count == 0 ? null : $"{model.Rows.Count} row(s) left after Clear finished";
            }
            finally
            {
                model.IsOpen = false;
                model.Queue.ClearFinished();
                model.Refresh();
            }
        }));
    }

    private static string? ToolTip(this Control c) => Avalonia.Controls.ToolTip.GetTip(c) as string;

    private sealed class NullLog : ILog
    {
        public void Write(LogLevel level, string message) { }
    }
}
