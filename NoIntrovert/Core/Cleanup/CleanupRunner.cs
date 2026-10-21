using System.Text.Json;
using System.Text.Json.Serialization;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.Cleanup;

public enum JournalAction
{
    /// <summary>A file or folder moved (or renamed) from <see cref="JournalEntry.From"/> to <see cref="JournalEntry.Path"/>.</summary>
    Moved,
    /// <summary>A file the run made (a converted file, a playlist).</summary>
    Created,
    /// <summary>A file moved from <see cref="JournalEntry.From"/> into the run's trash at <see cref="JournalEntry.Path"/>.</summary>
    Trashed,
}

/// <summary>
/// One thing a run did, with the size and date the result had, so undo can
/// tell whether it changed since. <see cref="Step"/> ties a conversion's
/// new files to the originals it trashed.
/// </summary>
public sealed record JournalEntry(JournalAction Action, string Path, string? From, long Size, long Ticks, int Step);

/// <summary>One run of the wizard and its journal (FR-4), saved as it goes beside the settings.</summary>
public sealed class CleanupRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public int Steps { get; set; }
    public bool Undone { get; set; }
    public bool TrashEmptied { get; set; }
    public List<JournalEntry> Entries { get; set; } = [];

    /// <summary>The trash folders this run filled, one per library folder.</summary>
    public List<string> TrashFolders { get; set; } = [];

    [JsonIgnore] public string Summary =>
        $"{StartedUtc.ToLocalTime():g}: {Steps} step(s), {Entries.Count(e => e.Action == JournalAction.Trashed)} file(s) in the trash"
        + (Undone ? " · undone" : TrashEmptied ? " · trash emptied" : "");
}

/// <summary>
/// Runs a plan (FR-3): one queue job per game, its steps in order, each
/// skipped if one before it failed. Removed files go to a trash folder in
/// their library folder (same drive, so it's a rename), from which undo
/// restores them until the trash is emptied (FR-4, Q2).
/// </summary>
public sealed class CleanupRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly object _lock = new();
    private int _stepIds;

    public CleanupRun Run { get; } = new();

    public static string RunsFolder => System.IO.Path.Combine(AppSettings.SettingsDirectory, "cleanup");

    public static string TrashFolder(string root, string runId) => System.IO.Path.Combine(root, ".noi-trash", runId);

    /// <summary>The queue jobs for the plan's ticked steps: one per game.</summary>
    public IReadOnlyList<(string Name, long Cost, JobWork Work)> Jobs(CleanupPlan plan)
    {
        var games = plan.Steps.Where(s => s.Runs).GroupBy(s => s.GameKey).ToList();
        Run.Steps = games.Sum(g => g.Count());
        Save();
        return games.Select(g =>
        {
            var steps = g.ToList();
            return (steps[0].Game, steps.Sum(s => s.Sources.Sum(Size)), (JobWork)(c => RunGame(c, steps)));
        }).ToList();
    }

    private JobOutcome RunGame(JobContext c, IReadOnlyList<CleanupStep> steps)
    {
        var done = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            c.Token.ThrowIfCancellationRequested();
            var step = steps[i];
            c.Report.SetStage($"{i + 1}/{steps.Count}: {step.Kind}");
            try
            {
                var problem = step.Kind switch
                {
                    CleanupKind.Convert => Convert(c, step),
                    CleanupKind.Move or CleanupKind.Rename => MoveStep(step),
                    CleanupKind.RemoveDuplicate => TrashAll(step.Sources, step.Root, NextStep()),
                    _ => null,
                };
                if (problem is not null)
                {
                    c.Log.Error($"Cleanup: {step.Game}: {problem}; the steps after it were skipped.");
                    return new JobOutcome(JobStatus.Failed, problem);
                }
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                c.Log.Error($"Cleanup: {step.Game}: {ex.Message}; the steps after it were skipped.");
                return new JobOutcome(JobStatus.Failed, ex.Message);
            }
        }
        c.Log.Success($"Cleanup: {steps[0].Game}: {done} step(s) done");
        return new JobOutcome(JobStatus.Succeeded, $"{done} step(s)");
    }

    /// <summary>
    /// Converts into a staging folder beside the target with the Convert
    /// page's own jobs (verified, originals kept), moves the results to their
    /// database names, then moves the originals to the trash.
    /// </summary>
    private string? Convert(JobContext c, CleanupStep step)
    {
        var folder = System.IO.Path.GetDirectoryName(step.Target!)!;
        // R7 multi-disc: the playlist goes in the system folder and the discs in hidden/ beside it.
        var playlist = step.Target!.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
        if (playlist && Directory.Exists(step.Target)) return $"{System.IO.Path.GetFileName(step.Target)} is a folder";
        var id = NextStep();
        Directory.CreateDirectory(folder);
        var staging = System.IO.Path.Combine(folder, $".noi-cleanup-{Run.Id}");
        Directory.CreateDirectory(staging);
        try
        {
            var s = c.Settings.Clone();
            s.Output.Folder = staging;
            s.Output.Overwrite = false;
            // Whatever the presets say: verified or tested, originals kept (the run moves them to its trash itself).
            static void Safe(AppSettings x)
            {
                x.Output.RemoveOriginals = x.Output.RemoveArchiveOriginals = x.Output.RemoveWiiUOriginals = false;
                x.Chd.VerifyAfterCreate = true;
                x.Archives.TestAfter = true;
            }
            Safe(s);
            var plan = ConvertPlanner.Plan(step.Sources.Select(p => new PlanInput(p, step.ConvertTo, 1)).ToList(), s, c.Log, Safe);
            if (plan.Error is { } error) return error;
            foreach (var job in plan.Jobs)
            {
                var o = job.Work(c.WithSettings(s));
                if (o.Status is not (JobStatus.Succeeded or JobStatus.Warning)) return $"converting: {o.Summary}";
            }

            var made = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).ToList();
            if (made.Count == 0) return "the conversion made nothing";
            foreach (var file in made.Where(f => !f.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)))
            {
                var relative = System.IO.Path.GetRelativePath(staging, file);
                var name = step.Renames.GetValueOrDefault(System.IO.Path.GetFileName(file))
                           ?? (made.Count == 1 ? System.IO.Path.GetFileName(step.Target!) : System.IO.Path.GetFileName(file));
                var target = System.IO.Path.Combine(folder, System.IO.Path.GetDirectoryName(relative) ?? "", name);
                if (File.Exists(target)) return $"{System.IO.Path.GetFileName(target)} already exists";
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                var original = OriginalHashes.Default.Get(file);
                File.Move(file, target);
                if (original is not null) OriginalHashes.Default.Set(target, original.Source, original.Files); // R11 follows the file
                Record(JournalAction.Created, target, null, id);
            }
            // R7: the playlist lists the discs under their database names.
            if (made.FirstOrDefault(f => f.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)) is { } m3u)
            {
                var lines = File.ReadAllLines(m3u).Select(l =>
                {
                    var file = l.Split('/').Last();
                    var renamed = step.Renames.GetValueOrDefault(file);
                    return renamed is not null ? l[..^file.Length] + renamed : l;
                }).ToList();
                TrashOldPlaylists(step, id);
                File.WriteAllText(step.Target!, string.Join("\n", lines) + "\n");
                Record(JournalAction.Created, step.Target!, null, id);
                WriteSkipFile(step, id);
            }
            return TrashAll(step.Sources.SelectMany(Members).ToList(), step.Root, id);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string? MoveStep(CleanupStep step)
    {
        if (step.Target!.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)) return MovePlaylist(step);
        var id = NextStep();
        var folder = System.IO.Path.GetDirectoryName(step.Target!)!;
        Directory.CreateDirectory(folder);
        // A set (a cue and its bins) moves under its own names; a single file or folder takes the target's name.
        foreach (var source in step.Sources)
        {
            var target = step.Sources.Count == 1 ? step.Target! : System.IO.Path.Combine(folder, System.IO.Path.GetFileName(source));
            if (System.IO.Path.GetFileName(source) == System.IO.Path.GetFileName(target) && CleanupPlanner.SamePath(source, target)) continue;
            // A rename that only changes case is the same file on Windows and macOS: not a clash.
            if ((File.Exists(target) || Directory.Exists(target)) && !CleanupPlanner.SamePath(source, target))
                return $"{System.IO.Path.GetFileName(target)} already exists";
            if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target);
            Record(JournalAction.Moved, target, source, id);
        }
        return null;
    }

    /// <summary>
    /// ES-DE multi-disc (R7): moves converted discs into hidden/ in their
    /// system folder under their database names (<see cref="CleanupStep.Renames"/>
    /// gives each one's place, such as <c>hidden/Game (Disc 1).chd</c>), and
    /// writes the playlist beside hidden/, listing them in disc order. Old
    /// playlists go to the trash. A <c>Game.m3u</c> folder an earlier version
    /// made is emptied this way, then removed.
    /// </summary>
    private string? MovePlaylist(CleanupStep step)
    {
        var folder = System.IO.Path.GetDirectoryName(step.Target!)!;
        var name = System.IO.Path.GetFileName(step.Target!);
        // That old Game.m3u folder may hold only these discs and its own playlist; anything else stays the user's call.
        if (Directory.Exists(step.Target) && Directory.EnumerateFileSystemEntries(step.Target).Any(e =>
                !step.Sources.Contains(e, PathNames.Comparer) && !string.Equals(System.IO.Path.GetFileName(e), name, PathNames.Comparison)))
            return $"the {name} folder holds other files";
        var moves = new List<(string From, string To)>();
        var lines = new List<string>();
        foreach (var source in step.Sources)
        {
            var place = step.Renames.GetValueOrDefault(System.IO.Path.GetFileName(source)) ?? System.IO.Path.GetFileName(source);
            var target = System.IO.Path.Combine(folder, place.Replace('/', System.IO.Path.DirectorySeparatorChar));
            lines.Add(place);
            if (System.IO.Path.GetFileName(source) == System.IO.Path.GetFileName(target) && CleanupPlanner.SamePath(source, target)) continue;
            if ((File.Exists(target) || Directory.Exists(target)) && !CleanupPlanner.SamePath(source, target))
                return $"{System.IO.Path.GetFileName(target)} already exists";
            moves.Add((source, target));
        }
        var id = NextStep();
        TrashOldPlaylists(step, id);
        foreach (var (from, to) in moves)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            File.Move(from, to);
            Record(JournalAction.Moved, to, from, id);
        }
        if (Directory.Exists(step.Target) && !Directory.EnumerateFileSystemEntries(step.Target).Any()) Directory.Delete(step.Target);
        File.WriteAllText(step.Target!, string.Join("\n", lines.Order(StringComparer.OrdinalIgnoreCase)) + "\n");
        Record(JournalAction.Created, step.Target!, null, id);
        WriteSkipFile(step, id);
        return null;
    }

    /// <summary>
    /// The playlists a multi-disc step replaces: one where its playlist goes,
    /// one beside the discs (inside an old <c>Game.m3u</c> folder) and one
    /// beside their folder (beside an old hidden/). They go to the trash, so undo brings them back.
    /// </summary>
    private void TrashOldPlaylists(CleanupStep step, int id)
    {
        var name = System.IO.Path.GetFileName(step.Target!);
        var old = new List<string> { step.Target! };
        foreach (var source in step.Sources)
        {
            var dir = System.IO.Path.GetDirectoryName(source)!;
            old.Add(System.IO.Path.Combine(dir, name));
            if (System.IO.Path.GetDirectoryName(dir) is { } parent) old.Add(System.IO.Path.Combine(parent, name));
        }
        var files = old.Where(File.Exists).Distinct(PathNames.Comparer).ToList();
        if (files.Count > 0) TrashAll(files, step.Root, id);
    }

    /// <summary>ES-DE: an empty noload.txt in hidden/, so ES-DE doesn't list each disc as a game of its own.</summary>
    private void WriteSkipFile(CleanupStep step, int id)
    {
        if (step.SkipFile is not { } skip || File.Exists(skip)) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(skip)!);
        File.WriteAllText(skip, "");
        Record(JournalAction.Created, skip, null, id);
    }

    /// <summary>Moves files into the run's trash in their library folder, keeping their paths below it.</summary>
    private string? TrashAll(IReadOnlyList<string> files, string root, int id)
    {
        var trash = TrashFolder(root, Run.Id);
        lock (_lock) if (!Run.TrashFolders.Contains(trash)) Run.TrashFolders.Add(trash);
        foreach (var file in files.Where(f => File.Exists(f) || Directory.Exists(f)).Distinct(PathNames.Comparer))
        {
            var relative = System.IO.Path.GetRelativePath(root, file);
            if (relative.StartsWith("..")) relative = System.IO.Path.GetFileName(file);
            var target = System.IO.Path.Combine(trash, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            if (Directory.Exists(file)) Directory.Move(file, target); else File.Move(file, target, overwrite: true);
            Record(JournalAction.Trashed, target, file, id);
        }
        return null;
    }

    private static IEnumerable<string> Members(string path) =>
        FormatRegistry.Default.Identify(path)?.Members ?? [path];

    private int NextStep() => Interlocked.Increment(ref _stepIds);

    private void Record(JournalAction action, string path, string? from, int step)
    {
        var (size, ticks) = HashCache.Stamp(path);
        lock (_lock) Run.Entries.Add(new JournalEntry(action, path, from, size, ticks, step));
        Save();
    }

    private void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(RunsFolder);
            File.WriteAllText(System.IO.Path.Combine(RunsFolder, Run.Id + ".json"), JsonSerializer.Serialize(Run, JsonOptions));
        }
    }

    private static long Size(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    // ---- The runs on record, undo and the trash ----

    public static List<CleanupRun> Runs()
    {
        if (!Directory.Exists(RunsFolder)) return [];
        var runs = new List<CleanupRun>();
        foreach (var file in Directory.EnumerateFiles(RunsFolder, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<CleanupRun>(File.ReadAllText(file), JsonOptions) is { } run) runs.Add(run); }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return runs.OrderByDescending(r => r.StartedUtc).ToList();
    }

    private static void SaveRun(CleanupRun run)
    {
        Directory.CreateDirectory(RunsFolder);
        File.WriteAllText(System.IO.Path.Combine(RunsFolder, run.Id + ".json"), JsonSerializer.Serialize(run, JsonOptions));
    }

    /// <summary>
    /// Undoes a run in reverse order (FR-4). Moves go back; trashed files
    /// come back; a conversion is reversed only while all its originals are
    /// still in the trash (its new files then go to the Recycle Bin), and is
    /// otherwise left and reported. Anything changed since the run is skipped.
    /// Returns what couldn't be undone.
    /// </summary>
    public static List<string> Undo(CleanupRun run, bool useBin, ILog log)
    {
        var problems = new List<string>();
        static bool Unchanged(JournalEntry e)
        {
            var (size, ticks) = HashCache.Stamp(e.Path);
            return size == e.Size && ticks == e.Ticks;
        }
        foreach (var step in run.Entries.GroupBy(e => e.Step).OrderByDescending(g => g.Key))
        {
            var created = step.Where(e => e.Action == JournalAction.Created).ToList();
            var trashed = step.Where(e => e.Action == JournalAction.Trashed).ToList();
            if (created.Count > 0 && trashed.Any(e => !File.Exists(e.Path) && !Directory.Exists(e.Path)))
            {
                problems.Add($"{System.IO.Path.GetFileName(created[0].Path)}: its originals are no longer in the trash, so the conversion stays");
                continue;
            }
            foreach (var e in step.Reverse())
            {
                switch (e.Action)
                {
                    case JournalAction.Moved or JournalAction.Trashed:
                        if (!(File.Exists(e.Path) || Directory.Exists(e.Path)) || (e.Action == JournalAction.Moved && !Unchanged(e)))
                        {
                            problems.Add($"{System.IO.Path.GetFileName(e.From!)}: {(e.Action == JournalAction.Trashed ? "no longer in the trash" : "changed or moved since")}");
                            continue;
                        }
                        // A folder the run made where a file was (an ES-DE Game.m3u folder) is removed once it's empty again.
                        if (Directory.Exists(e.From) && File.Exists(e.Path) && !Directory.EnumerateFileSystemEntries(e.From).Any()) Directory.Delete(e.From);
                        if (File.Exists(e.From) || Directory.Exists(e.From)) { problems.Add($"{System.IO.Path.GetFileName(e.From!)}: something is in its place"); continue; }
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(e.From!)!);
                        if (Directory.Exists(e.Path)) Directory.Move(e.Path, e.From!); else File.Move(e.Path, e.From!);
                        break;
                    case JournalAction.Created:
                        if (File.Exists(e.Path) && Unchanged(e)) FileRemover.Remove([e.Path], useBin, log);
                        else problems.Add($"{System.IO.Path.GetFileName(e.Path)}: changed since, so it was left");
                        break;
                }
            }
            // A folder the step made (hidden/, or an older version's Game.m3u folder) goes too, once it's empty.
            foreach (var dir in step.Where(e => e.Action is JournalAction.Moved or JournalAction.Created)
                         .Select(e => System.IO.Path.GetDirectoryName(e.Path)!).Distinct(PathNames.Comparer)
                         .Where(d => Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()))
                Directory.Delete(dir);
        }
        run.Undone = true;
        SaveRun(run);
        foreach (var p in problems) log.Warn($"Cleanup undo: {p}");
        return problems;
    }

    /// <summary>Empties a run's trash into the Recycle Bin (or deletes it); after this its conversions can't be undone.</summary>
    public static void EmptyTrash(CleanupRun run, bool useBin, ILog log)
    {
        foreach (var trash in run.TrashFolders.Where(Directory.Exists))
        {
            FileRemover.Remove([trash], useBin, log);
            var parent = System.IO.Path.GetDirectoryName(trash)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        run.TrashEmptied = true;
        SaveRun(run);
    }
}
