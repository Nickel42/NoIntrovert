using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>The window shell: the start page, the queue's overall progress, and the session log files.</summary>
internal static class ShellTests
{
    public static void Run(Action<string, Func<string?>> test, string scratch)
    {
        test("Queue progress counts items, not bytes, and leaves out skipped ones", () =>
        {
            if (QueueViewModel.OverallFraction([]) is not null) return "an empty queue should have no progress";
            if (QueueViewModel.OverallFraction([(QueuedState.Finished, JobStatus.Skipped, 0)]) is not null)
                return "a queue of only skipped items should have no progress";
            var jobs = new (QueuedState, JobStatus?, double)[]
            {
                (QueuedState.Finished, JobStatus.Succeeded, 1),
                (QueuedState.Finished, JobStatus.Failed, 0.3), // finished counts whole, whatever the result
                (QueuedState.Running, null, 0.5),
                (QueuedState.Queued, null, 0),
                (QueuedState.Finished, JobStatus.Skipped, 0),  // not counted on either side
            };
            var f = QueueViewModel.OverallFraction(jobs);
            if (f is not { } v || Math.Abs(v - 2.5 / 4) > 1e-9) return $"expected 0.625, got {f}";
            var running = QueueViewModel.OverallFraction([(QueuedState.Running, null, 0.25), (QueuedState.Running, null, 0.75)]);
            return running is { } r && Math.Abs(r - 0.5) < 1e-9 ? null : $"two running at 25% and 75% should be 50%, got {running}";
        });

        test("Start page: the chosen page, else Optimize with library folders, else Convert", () =>
        {
            var cases = new (string? Chosen, bool Folders, string Expected)[]
            {
                ("", true, "library"), ("", false, "convert"), (null, false, "convert"),
                ("verify", false, "verify"), ("cleanup", true, "cleanup"), ("organize", false, "cleanup"),
                ("library", false, "library"), ("convert", true, "convert"),
                ("settings", true, "library"), ("tests", false, "convert"), ("no-such-page", true, "library"),
                ("info", true, "convert"), // the removed CHD info page
            };
            var wrong = cases.Where(c => MainViewModel.StartPageId(c.Chosen, c.Folders) != c.Expected)
                .Select(c => $"{c.Chosen ?? "null"}/{c.Folders} → {MainViewModel.StartPageId(c.Chosen, c.Folders)} (expected {c.Expected})").ToList();
            return wrong.Count == 0 ? null : string.Join("; ", wrong);
        });

        test("Page names: --page info, organize and optimize still find a page", () =>
        {
            var map = new[] { ("info", "convert"), ("organize", "cleanup"), ("Optimize", "library"), ("extract", "convert"), ("verify", "verify") };
            var wrong = map.Where(m => MainViewModel.PageFor(m.Item1) != m.Item2).Select(m => $"{m.Item1} → {MainViewModel.PageFor(m.Item1)}").ToList();
            return wrong.Count == 0 ? null : string.Join("; ", wrong);
        });

        test("Session log: one file per session, info and above, old ones deleted", () =>
        {
            var dir = Path.Combine(scratch, "session-logs");
            Directory.CreateDirectory(dir);
            var now = new DateTime(2026, 11, 4, 9, 30, 15);

            // Leftovers: one past the limit, one inside it, and a file that isn't a session log.
            var old = Path.Combine(dir, "NoIntrovert-20260901-120000.log");
            var recent = Path.Combine(dir, "NoIntrovert-20261101-120000.log");
            var other = Path.Combine(dir, "notes.log");
            foreach (var f in new[] { old, recent, other }) File.WriteAllText(f, "x");
            File.SetLastWriteTime(old, now.AddDays(-31));
            File.SetLastWriteTime(recent, now.AddDays(-3));
            File.SetLastWriteTime(other, now.AddDays(-90));

            if (SessionLogFile.DeleteOld(dir, 0, now) != 0 || !File.Exists(old)) return "0 days should keep every log";
            var deleted = SessionLogFile.DeleteOld(dir, 30, now);
            if (deleted != 1 || File.Exists(old)) return $"expected the 31-day-old log deleted, deleted {deleted}";
            if (!File.Exists(recent) || !File.Exists(other)) return "deleted a recent log or a file that isn't a session log";

            var file = SessionLogFile.Start(dir, now);
            if (file is null) return "couldn't start the session log";
            if (Path.GetFileName(file.Path) != "NoIntrovert-20261104-093015.log") return $"unexpected name {Path.GetFileName(file.Path)}";
            file.Write(new LogEntry(now, LogLevel.Debug, "debug line"));
            file.Write(new LogEntry(now, LogLevel.Info, "info line"));
            file.Write(new LogEntry(now, LogLevel.Error, "error line"));
            string text;
            using (var s = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(s))
                text = r.ReadToEnd();
            if (text.Contains("debug line")) return "debug lines shouldn't be saved";
            return text.Contains("info line") && text.Contains("[ERR] error line") ? null : $"missing lines: {text}";
        });
    }
}
