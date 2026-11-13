using System.Text.Json;
using NoIntrovert.Core;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Tests;

/// <summary>
/// Fixes from the 2026-11-12 performance review: the stores beside the
/// settings are written at most every two seconds and flushed when a job
/// group ends, own DAT headers are read once, the Cleanup journal is
/// appended to rather than rewritten (and older journals still undo), and a
/// Cleanup copy reports progress and leaves nothing behind when cancelled.
/// </summary>
internal static class StoreTests
{
    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir, ILog log)
    {
        test("Stores: verifying 40 files writes matches.json and hashes.json a few times, and all of it is on disk when the group ends", () =>
            WithSettingsFolder(dir("stores-verify-settings"), () =>
            {
                var folder = dir("stores-verify-files");
                var files = Enumerable.Range(0, 40).Select(i =>
                {
                    var path = Path.Combine(folder, $"Game {i:00}.nes");
                    File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(b => (byte)(b * (i + 1))).ToArray());
                    return path;
                }).ToList();
                var jobs = files.Select(f => new BatchJob(Path.GetFileName(f), new NullReport(), c => VerifyJobs.Check(c, f), _ => { }, _ => { })).ToList();
                var group = new JobQueue(() => 2).Submit("Verify", jobs, new AppSettings(), log);
                if (!group.Completion.Wait(TimeSpan.FromSeconds(60))) return "the verify run never finished";

                // A run this short writes each store once or twice on the timer, plus once at the end; it used to write 40 times.
                if (MatchStore.Default.Writes > 10 || HashCache.Default.Writes > 10)
                    return $"matches.json written {MatchStore.Default.Writes} times, hashes.json {HashCache.Default.Writes} times";
                if (HashCache.Default.IsDirty) return "hashes.json still had changes to write when the group ended";
                var matches = Keys(Path.Combine(AppSettings.SettingsDirectory, "matches.json"));
                var hashes = Keys(Path.Combine(AppSettings.SettingsDirectory, "hashes.json"));
                var missing = files.Where(f => !matches.Contains(f) || !hashes.Contains(f)).Select(Path.GetFileName).ToList();
                return missing.Count == 0 ? null : $"not on disk after the group: {string.Join(", ", missing)}";
            }));

        test("Stores: a change waits at most two seconds for its write, and changes in between are written together", () =>
            WithSettingsFolder(dir("stores-throttle-settings"), () =>
            {
                var folder = dir("stores-throttle-files");
                string Made(string name)
                {
                    var path = Path.Combine(folder, name);
                    File.WriteAllText(path, name);
                    return path;
                }
                var store = OriginalHashes.Default;
                var json = Path.Combine(AppSettings.SettingsDirectory, "originals.json");
                HashedFile[] files = [new("Game.iso", 1, "00000000", null)];

                store.Set(Made("A.chd"), "A.iso", files);
                if (!WaitFor(() => store.Writes == 1, TimeSpan.FromSeconds(2))) return "the first change wasn't written straight away";
                store.Set(Made("B.chd"), "B.iso", files);
                store.Set(Made("C.chd"), "C.iso", files);
                if (store.Writes != 1 || File.ReadAllText(json).Contains("B.chd")) return "a change within two seconds of the last write was written at once";
                if (!WaitFor(() => File.ReadAllText(json).Contains("C.chd"), TimeSpan.FromSeconds(5))) return "the later changes were never written";
                if (store.Writes != 2) return $"{store.Writes} writes, expected 2 (the two changes together)";
                store.Flush();
                return store.Writes == 2 ? null : "Flush wrote the file again with nothing changed";
            }));

        test("Stores: own DAT headers are read once, and again only when a DAT changes", () =>
        {
            var own = dir("stores-own-dats");
            string Dat(string file, string name)
            {
                var path = Path.Combine(own, file);
                File.WriteAllText(path, $"<?xml version=\"1.0\"?>\n<datafile><header><name>{name}</name></header></datafile>\n");
                return path;
            }
            Dat("a.dat", "Nintendo - Game Boy");
            Dat("b.dat", "Nintendo - Game Boy Advance");
            var c = Dat("c.dat", "Sega - Mega Drive - Genesis");
            var store = new DatStore(dir("stores-own-dats-cache"));
            for (var i = 0; i < 5; i++) store.For(null, own);
            if (store.HeaderReads != 3) return $"{store.HeaderReads} header reads for 3 DATs checked 5 times";
            Dat("c.dat", "Sega - Master System - Mark III");
            File.SetLastWriteTimeUtc(c, DateTime.UtcNow.AddMinutes(1));
            var names = store.For(null, own).Select(d => d.Name).ToList();
            if (store.HeaderReads != 4) return $"{store.HeaderReads} header reads after one DAT changed, expected 4";
            Dat("d.dat", "Nintendo - Nintendo 64");
            names = store.For(null, own).Select(d => d.Name).ToList();
            return store.HeaderReads == 5 && names.Contains("Sega - Master System - Mark III") && names.Contains("Nintendo - Nintendo 64")
                ? null : $"{store.HeaderReads} reads; DATs: {string.Join(", ", names)}";
        });

        test("Cleanup: a journal saved in the old format (entries in the .json) still lists and undoes", () =>
            WithSettingsFolder(dir("stores-old-journal-settings"), () =>
            {
                var lib = dir("stores-old-journal-lib");
                var moved = Path.Combine(lib, "Game (USA).nes");
                File.WriteAllText(moved, "rom");
                var (size, ticks) = HashCache.Stamp(moved);
                var old = Path.Combine(lib, "game.nes");
                // Exactly what CleanupRunner wrote before the journal was split into .json and .jsonl.
                Directory.CreateDirectory(CleanupRunner.RunsFolder);
                File.WriteAllText(Path.Combine(CleanupRunner.RunsFolder, "0123456789ab.json"), $$"""
                    {
                      "Id": "0123456789ab",
                      "StartedUtc": "2026-11-01T10:00:00Z",
                      "Steps": 1,
                      "Undone": false,
                      "TrashEmptied": false,
                      "Entries": [
                        {
                          "Action": "Moved",
                          "Path": {{JsonSerializer.Serialize(moved)}},
                          "From": {{JsonSerializer.Serialize(old)}},
                          "Size": {{size}},
                          "Ticks": {{ticks}},
                          "Step": 1
                        }
                      ],
                      "TrashFolders": [],
                      "NewLibrary": null
                    }
                    """);
                var run = CleanupRunner.Runs().SingleOrDefault();
                if (run is null || run.Entries.Count != 1) return $"read {run?.Entries.Count.ToString() ?? "no run"} entries";
                var problems = CleanupRunner.Undo(run, useBin: false, log);
                if (problems.Count > 0) return string.Join("; ", problems);
                if (!File.Exists(old) || File.Exists(moved)) return "the move wasn't undone";
                return CleanupRunner.Runs().Single() is { Undone: true, Entries.Count: 1 } ? null : "the undone run wasn't saved whole";
            }));

        test("Cleanup: the journal is appended one line per entry, survives a line cut short by a crash, and undoes from disk", () =>
            WithSettingsFolder(dir("stores-jsonl-settings"), () =>
            {
                var lib = dir("stores-jsonl-lib");
                var sources = Enumerable.Range(1, 3).Select(i =>
                {
                    var path = Path.Combine(lib, $"game{i}.nes");
                    File.WriteAllText(path, $"rom {i}");
                    return path;
                }).ToList();
                var steps = sources.Select((s, i) => new CleanupStep
                {
                    Kind = CleanupKind.Rename, GameKey = $"g{i}", Game = $"Game {i}", Sources = [s], Root = lib,
                    Target = Path.Combine(lib, $"Game {i + 1} (USA).nes"),
                }).ToList();
                var runner = new CleanupRunner();
                var jobs = runner.Jobs(new CleanupPlan(steps)).Select(j => new BatchJob(j.Name, new NullReport(), j.Work, _ => { }, _ => { })).ToList();
                if (!new JobQueue(() => 2).Submit("Cleanup", jobs, new AppSettings(), log).Completion.Wait(TimeSpan.FromSeconds(30))) return "the run never finished";

                var json = Path.Combine(CleanupRunner.RunsFolder, runner.Run.Id + ".json");
                var jsonl = Path.ChangeExtension(json, ".jsonl");
                if (File.ReadAllText(json).Contains("Moved")) return "the entries were written into the .json as well";
                if (File.ReadAllLines(jsonl).Length != 3) return $"{File.ReadAllLines(jsonl).Length} journal lines, expected 3";
                // A crash in the middle of a line: that entry is lost, the others still undo.
                File.AppendAllText(jsonl, "{\"Action\":\"Moved\",\"Path\":\"C:\\\\half");
                var run = CleanupRunner.Runs().Single();
                if (run.Entries.Count != 3) return $"{run.Entries.Count} entries read back, expected 3";
                var problems = CleanupRunner.Undo(run, useBin: false, log);
                if (problems.Count > 0) return string.Join("; ", problems);
                if (!sources.All(File.Exists)) return "not every rename was undone";
                if (File.Exists(jsonl)) return "the .jsonl was left once the undone run was saved whole";
                return CleanupRunner.Runs().Single() is { Undone: true, Entries.Count: 3 } ? null : "the undone run wasn't saved whole";
            }));

        test("Cleanup: a copy into a new library reports progress as it goes and, cancelled halfway, leaves no partial file", () =>
            WithSettingsFolder(dir("stores-copy-settings"), () =>
            {
                var lib = dir("stores-copy-lib");
                var dest = dir("stores-copy-dest");
                var source = Path.Combine(lib, "Big Game.iso");
                using (var f = File.Create(source)) f.SetLength(64L << 20);
                var step = new CleanupStep
                {
                    Kind = CleanupKind.Copy, GameKey = "big", Game = "Big Game", Sources = [source], Root = lib, Destination = dest,
                    Target = Path.Combine(dest, "Big Game (USA).iso"),
                };
                var runner = new CleanupRunner();
                var work = runner.Jobs(new CleanupPlan([step])).Single().Work;
                JobGroup? group = null;
                JobOutcome? outcome = null;
                var report = new CancelAt(0.25, () => group!.Cancel());
                var job = new BatchJob("Big Game", report, work, _ => { }, o => outcome = o);
                group = new JobQueue(() => 1).Submit("Cleanup", [job], new AppSettings(), log);
                if (!group.Completion.Wait(TimeSpan.FromSeconds(60))) return "the copy never finished";
                if (outcome?.Status != JobStatus.Cancelled) return $"the copy ended {outcome?.Status}: {outcome?.Summary}";
                if (report.Reports < 2) return $"progress reported {report.Reports} times while copying 64 MiB";
                var left = Directory.EnumerateFileSystemEntries(dest, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
                if (left.Count > 0) return $"left behind: {string.Join(", ", left)}";
                return runner.Run.Entries.Count == 0 && File.Exists(source) ? null : "the cancelled copy was journalled, or its source went";
            }));
    }

    /// <summary>Cancels the job the first time its progress passes a fraction, and counts the reports.</summary>
    private sealed class CancelAt(double fraction, Action cancel) : IJobReport
    {
        private bool _cancelled;
        public int Reports;

        public void SetStage(string stage) { }

        public void SetProgress(double value)
        {
            Reports++;
            if (value >= fraction && !_cancelled)
            {
                _cancelled = true;
                cancel();
            }
        }
    }

    private static HashSet<string> Keys(string json) =>
        File.Exists(json)
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(json))!.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

    private static bool WaitFor(Func<bool> done, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!done())
        {
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    private static string? WithSettingsFolder(string folder, Func<string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try { return body(); }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }
}
