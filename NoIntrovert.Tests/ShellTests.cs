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
                ("", true, "optimize"), ("", false, "convert"), (null, false, "convert"),
                ("verify", false, "verify"), ("cleanup", true, "cleanup"), ("organize", false, "cleanup"),
                ("optimize", false, "optimize"), ("library", false, "optimize"), ("convert", true, "convert"), // the Library page Optimize replaced
                ("settings", true, "optimize"), ("tests", false, "convert"), ("no-such-page", true, "optimize"),
                ("info", true, "convert"), // the removed CHD info page
            };
            var wrong = cases.Where(c => MainViewModel.StartPageId(c.Chosen, c.Folders) != c.Expected)
                .Select(c => $"{c.Chosen ?? "null"}/{c.Folders} → {MainViewModel.StartPageId(c.Chosen, c.Folders)} (expected {c.Expected})").ToList();
            return wrong.Count == 0 ? null : string.Join("; ", wrong);
        });

        test("Page names: --page info, organize, library and optimize still find a page", () =>
        {
            var map = new[] { ("info", "convert"), ("organize", "cleanup"), ("Optimize", "optimize"), ("library", "optimize"), ("extract", "convert"), ("verify", "verify") };
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

            // The old folder's session-*.log files get the same rule; nothing else there is touched.
            var legacyOld = Path.Combine(dir, "session-20260901-120000.log");
            var legacyRecent = Path.Combine(dir, "session-20261101-120000.log");
            foreach (var f in new[] { legacyOld, legacyRecent }) File.WriteAllText(f, "x");
            File.SetLastWriteTime(legacyOld, now.AddDays(-31));
            File.SetLastWriteTime(legacyRecent, now.AddDays(-3));
            deleted = SessionLogFile.DeleteOld(dir, 30, now, SessionLogFile.LegacyPattern);
            if (deleted != 1 || File.Exists(legacyOld) || !File.Exists(legacyRecent) || !File.Exists(other))
                return $"old-style logs: deleted {deleted}, expected only the 31-day-old one";
            if (SessionLogFile.LegacyDirectory is not null) return "the old log folder is pruned although the settings folder is moved (tests)";

            var file = SessionLogFile.Start(dir, now);
            if (file is null) return "couldn't start the session log";
            if (Path.GetFileName(file.Path) != "NoIntrovert-20261104-093015.log") return $"unexpected name {Path.GetFileName(file.Path)}";
            file.Write(new LogEntry(now, LogLevel.Debug, "debug line"));
            file.Write(new LogEntry(now, LogLevel.Info, "info line"));
            file.Write(new LogEntry(now, LogLevel.Error, "error line"));
            file.Flush();
            var text = ReadShared(file.Path);
            file.Dispose();
            if (text.Contains("debug line")) return "debug lines shouldn't be saved";
            return text.Contains("info line") && text.Contains("[ERR] error line") ? null : $"missing lines: {text}";
        });

        test("Session log: lines are buffered, reach the disk on the flush timer, and all of them on close", () =>
        {
            var dir = Path.Combine(scratch, "session-log-flush");
            Directory.CreateDirectory(dir);
            var file = SessionLogFile.Start(dir, new DateTime(2026, 11, 12, 10, 0, 0));
            if (file is null) return "couldn't start the session log";
            try
            {
                var when = DateTime.Now;
                file.Write(new LogEntry(when, LogLevel.Info, "first line"));
                // Not flushed per line any more: the timer writes it within a second or so.
                var wait = SessionLogFile.FlushInterval * 5;
                var until = DateTime.UtcNow + wait;
                while (!ReadShared(file.Path).Contains("first line"))
                {
                    if (DateTime.UtcNow > until) return $"the timer didn't flush the buffered line within {wait.TotalSeconds:F0}s";
                    Thread.Sleep(50);
                }
                // A burst right before closing: every line is written by the close, none lost.
                for (var i = 0; i < 2000; i++) file.Write(new LogEntry(when, LogLevel.Info, $"burst {i}"));
                file.Dispose();
                var lines = File.ReadAllLines(file.Path);
                if (lines.Length != 2001 || !lines[^1].EndsWith("burst 1999")) return $"{lines.Length} lines after closing, expected 2,001 ending with burst 1999";
                file.Write(new LogEntry(when, LogLevel.Info, "after close")); // dropped, not thrown
                file.Flush();
                return File.ReadAllText(file.Path).Contains("after close") ? "a line written after closing reached the file" : null;
            }
            finally { file.Dispose(); }
        });

        test("Log pane: a burst past the 5,000-line cap keeps the newest 5,000, in order, in one change", () =>
        {
            var log = new LogViewModel(new NoDialogs(), new AppSettings());
            var changes = 0;
            log.Entries.CollectionChanged += (_, _) => changes++;
            for (var i = 0; i < 3000; i++) log.Write(LogLevel.Info, $"line {i}");
            log.Drain();
            if (log.Entries.Count != 3000 || changes != 1) return $"3,000 lines: {log.Entries.Count} shown in {changes} changes, expected 3,000 in 1";
            for (var i = 3000; i < 7000; i++) log.Write(LogLevel.Info, $"line {i}");
            log.Drain();
            if (changes != 2) return $"the burst past the cap took {changes - 1} changes, expected 1";
            if (log.Entries.Count != LogViewModel.MaxVisible) return $"{log.Entries.Count} lines shown, expected {LogViewModel.MaxVisible}";
            if (log.Entries[0].Message != "line 2000" || log.Entries[^1].Message != "line 6999") return $"kept {log.Entries[0].Message} … {log.Entries[^1].Message}";
            for (var i = 7000; i < 13000; i++) log.Write(LogLevel.Info, $"line {i}"); // one burst bigger than the cap
            log.Drain();
            var inOrder = log.Entries.Select((e, n) => e.Message == $"line {8000 + n}").All(ok => ok);
            return log.Entries.Count == LogViewModel.MaxVisible && inOrder ? null
                : $"after a 6,000-line burst: {log.Entries.Count} lines, {log.Entries[0].Message} … {log.Entries[^1].Message}";
        });
    }

    private static string ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
