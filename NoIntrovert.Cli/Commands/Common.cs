using System.CommandLine;
using System.Text.Json.Nodes;
using NoIntrovert.Cli.Cli;
using NoIntrovert.Cli.Output;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Pipelines;

namespace NoIntrovert.Cli.Commands;

/// <summary>What the commands that work on files share: paths, filters, Save to, keys, systems, and running jobs with progress.</summary>
public static class Common
{
    // ---- Paths and filters ----

    /// <summary>
    /// The paths given: the arguments (a response file, @list.txt, is already expanded), "-" for one path per line
    /// on stdin, and with --library the app's Library folders. None at all is a usage error.
    /// </summary>
    public static List<string> Paths(ParseResult r, Argument<string[]> argument, string example)
    {
        var paths = new List<string>();
        foreach (var p in r.GetValue(argument) ?? [])
        {
            if (p == "-")
            {
                if (!Console.IsInputRedirected) throw new UsageException("\"-\" reads paths from stdin, but nothing is piped in.", $"Example: dir /b /s *.cue | {example} -");
                while (Console.ReadLine() is { } line)
                    if (line.Trim().Length > 0) paths.Add(line.Trim().Trim('"'));
            }
            else paths.Add(p);
        }
        if (r.GetValue(PathOptions.Library))
        {
            var folders = AppSettings.Peek().Library.Folders;
            if (folders.Count == 0) throw new UsageException("--library: the app's Settings › Library has no folders yet.", "Give the folders instead, or add them in the app.");
            paths.AddRange(folders);
        }
        if (paths.Count == 0) throw new UsageException("Give some files or folders to work on.", $"Example: {example} D:\\Roms", "Or --library for the folders in the app's Settings › Library.");
        return paths;
    }

    public static InputFilter Filter(ParseResult r) => new()
    {
        Recurse = !r.GetValue(PathOptions.NoRecurse),
        Include = r.GetValue(PathOptions.Include) ?? [],
        Exclude = r.GetValue(PathOptions.Exclude) ?? [],
        Systems = (r.GetValue(PathOptions.System) ?? []).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(ResolveSystem).ToList(),
    };

    /// <summary>
    /// Gathers the inputs and reports what couldn't be used. When nothing usable was given at all (every path
    /// missing), it's a usage error.
    /// </summary>
    public static InputList Gather(Session s, List<string> paths, Inputs.Kind kind, InputFilter filter)
    {
        s.Ui.Debug($"Looking for files in {Words.Count(paths.Count, "path")}…");
        var list = Inputs.Gather(paths, kind, filter, s.Settings);
        foreach (var p in list.Problems) s.Ui.Warn(p);
        if (list.Items.Count == 0 && list.Problems.Count == paths.Count && list.Folders.Count == 0)
            throw new UsageException("None of the paths given could be used.", "Check the paths, or quote ones with spaces in them.");
        return list;
    }

    /// <summary>A system by its id (playstation-2), ES-DE folder (ps2), RomM name or full name.</summary>
    public static string ResolveSystem(string word)
    {
        var w = word.Trim();
        var system = TargetFormats.All.FirstOrDefault(s => s.Id.Equals(w, StringComparison.OrdinalIgnoreCase))
                     ?? TargetFormats.All.FirstOrDefault(s => string.Equals(s.Esde, w, StringComparison.OrdinalIgnoreCase))
                     ?? TargetFormats.All.FirstOrDefault(s => string.Equals(s.Romm, w, StringComparison.OrdinalIgnoreCase))
                     ?? TargetFormats.All.FirstOrDefault(s => s.Name.Equals(w, StringComparison.OrdinalIgnoreCase))
                     ?? TargetFormats.All.FirstOrDefault(s => s.Folders.Any(f => f.Equals(w, StringComparison.OrdinalIgnoreCase)));
        if (system is not null) return system.Id;
        var names = TargetFormats.All.SelectMany(s => new[] { s.Id, s.Esde, s.Romm }).OfType<string>().Distinct();
        var guess = Suggest.Closest(w, names);
        throw new UsageException($"Unknown system \"{word}\".", guess is null ? "noi presets lists every system." : $"Did you mean \"{guess}\"?  noi presets lists every system.");
    }

    // ---- Save to, layout, keys ----

    public static void ApplySaveTo(ParseResult r, Session s, IReadOnlyList<string> folders)
    {
        var output = r.GetValue(SaveOptions.Output);
        var replace = r.GetValue(SaveOptions.Replace);
        if (output is not null && replace)
            throw new UsageException("--output and --replace can't be used together.", "--output keeps the originals and saves into a folder; --replace swaps each original for its new file.");
        var o = s.Settings.Output;
        o.SaveTo = output is not null ? SaveTo.Folder : replace ? SaveTo.ReplaceOriginals : SaveTo.NextToOriginals;
        if (output is not null) o.Folder = Path.GetFullPath(output);
        if (r.GetValue(SaveOptions.Layout) is { } layout) o.FolderLayout = FolderLayoutOf(layout);
        if (r.GetValue(SaveOptions.Overwrite)) o.Overwrite = true;
        if (r.GetValue(SaveOptions.NoRecycleBin)) o.UseRecycleBin = false;
        o.ScanSubfolders = !r.GetValue(PathOptions.NoRecurse);
        o.SourceRoots = [.. folders];
    }

    public static FolderLayout FolderLayoutOf(string word) => word.ToLowerInvariant() switch
    {
        "romm" => FolderLayout.RomM,
        "keep" or "same" => FolderLayout.SameAsOriginals,
        "flat" => FolderLayout.Flat,
        _ => FolderLayout.EsDe,
    };

    public static LibraryLayout LibraryLayoutOf(string word) => word.ToLowerInvariant() switch
    {
        "romm" => LibraryLayout.RomM,
        "keep" or "same" => LibraryLayout.InPlace,
        "flat" => LibraryLayout.Flat,
        _ => LibraryLayout.EsDe,
    };

    public static void ApplyKeys(ParseResult r, Session s)
    {
        var k = s.Settings.Keys;
        if (r.GetValue(KeyOptions.WiiU) is { } wiiu) k.WiiUKeysPath = Path.GetFullPath(wiiu);
        if (r.GetValue(KeyOptions.ThreeDs) is { } threeDs) k.ThreeDsKeysFolder = Path.GetFullPath(threeDs);
        if (r.GetValue(KeyOptions.Switch) is { } nx) k.SwitchKeysPath = Path.GetFullPath(nx);
        if (r.GetValue(KeyOptions.Ps3) is { } ps3) k.Ps3KeysFolder = Path.GetFullPath(ps3);
        if (r.GetValue(KeyOptions.Zrif) is { } zrif) k.VitaZrifPath = Path.GetFullPath(zrif);
    }

    // ---- Confirmation for Replace the originals ----

    /// <summary>
    /// Replace the originals asks first (§5.1): on a terminal it asks with how many go; elsewhere it needs --yes.
    /// Returns an exit code to stop with, or null to go ahead.
    /// </summary>
    public static int? ConfirmReplace(Session s, int replacing)
    {
        if (!s.Settings.Output.ReplacesOriginals || replacing == 0 || s.DryRun) return null;
        if (s.Yes) return null;
        if (!s.CanAsk)
            throw new UsageException("--replace removes the originals, and nothing can be asked here (--no-input, a script or CI).",
                "Add --yes to confirm, or leave out --replace to keep them.");
        var where = s.Settings.Output.UseRecycleBin && FileRemover.CanUseBin ? $"to the {FileRemover.BinName}" : "deleted permanently";
        if (s.Ui.Confirm($"Replace {Words.Count(replacing, "original")}? Each goes ({where}) only after its new file verifies.")) return null;
        s.Ui.Answer("Not started: nothing was changed.");
        return Exit.Ok;
    }

    // ---- Running jobs with progress ----

    /// <summary>
    /// Runs <paramref name="jobs"/> with live bars on a terminal (a line per file otherwise), calling
    /// <paramref name="finished"/> with each job as it ends and its place in the run ("[ 3/42]" in line mode).
    /// </summary>
    public static async Task RunAsync(Session s, string title, string verb, IReadOnlyList<RunJob> jobs, Action<RunJob, string> finished)
    {
        var done = 0;
        var width = jobs.Count.ToString().Length;
        var observer = new Observer(s, (job) =>
        {
            var n = Interlocked.Increment(ref done);
            var prefix = s.Ui.Term.LiveBars ? "" : $"[{n.ToString().PadLeft(width)}/{jobs.Count}] ";
            finished(job, prefix);
        });
        s.Ui.Event("start", new JsonObject { ["schema"] = 1, ["command"] = title, ["jobs"] = jobs.Count, ["bytes"] = jobs.Sum(j => j.Cost) });
        using var ticker = s.Ui.Verbosity == Verbosity.Ndjson ? new Timer(_ => Progress(s, jobs), null, 250, 250) : null;
        s.Ui.StartLive(jobs, verb);
        s.Ui.InRun = true;
        try { await RunEngine.RunAsync(title, jobs, s.Settings, s.Log, observer, s.Token); }
        finally
        {
            s.Ui.InRun = false;
            s.Ui.StopLive();
        }
    }

    private static void Progress(Session s, IReadOnlyList<RunJob> jobs)
    {
        foreach (var j in jobs.Where(j => j.State == RunJobState.Running))
            s.Ui.Event("progress", new JsonObject { ["name"] = j.Name, ["stage"] = j.Stage, ["fraction"] = Math.Round(j.Fraction, 4), ["bytesDone"] = (long)j.DoneBytes });
    }

    private sealed class Observer(Session s, Action<RunJob> finished) : IRunObserver
    {
        public void Started(RunJob job) =>
            s.Ui.Event("file-start", new JsonObject { ["name"] = job.Name, ["paths"] = Json.Array(job.Paths), ["bytes"] = job.Cost });

        public void Finished(RunJob job) => finished(job);
    }

    // ---- Words and numbers ----

    public static string Size(long bytes) => ChdJobs.Size(bytes);

    public static string Elapsed(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:0.0}s";

    /// <summary>Whether an outcome failed for want of keys (exit code 4).</summary>
    public static bool MissingKeys(JobOutcome? o) =>
        o?.Status == JobStatus.Failed && o.Summary is { } t
        && (t.Contains("Settings › Keys", StringComparison.Ordinal) || (t.Contains("key", StringComparison.OrdinalIgnoreCase)
            && (t.Contains("missing", StringComparison.OrdinalIgnoreCase) || t.Contains("no ", StringComparison.OrdinalIgnoreCase) || t.Contains("need", StringComparison.OrdinalIgnoreCase))));

    /// <summary>The colour and word a job's status prints with.</summary>
    public static (string Word, Func<string, string> Color) Look(Ui ui, JobStatus status, string ok) => status switch
    {
        JobStatus.Succeeded => (ok, ui.Paint.BoldGreen),
        JobStatus.Warning => (ok, ui.Paint.BoldYellow),
        JobStatus.Failed => ("Failed", ui.Paint.BoldRed),
        JobStatus.Cancelled => ("Cancelled", ui.Paint.BoldYellow),
        _ => ("Skipped", ui.Paint.Dim),
    };

    public static JsonObject Document(string command, int exitCode, TimeSpan elapsed) => new()
    {
        ["schema"] = 1,
        ["command"] = command,
        ["version"] = Program.Version,
        ["exitCode"] = exitCode,
        ["elapsedMs"] = (long)elapsed.TotalMilliseconds,
    };
}

public static class Json
{
    public static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
}

/// <summary>"Did you mean…": the closest of <c>names</c> to what was typed, by edit distance.</summary>
public static class Suggest
{
    public static string? Closest(string typed, IEnumerable<string> names)
    {
        var t = typed.ToLowerInvariant();
        var best = names.Select(n => (Name: n, Distance: Distance(t, n.ToLowerInvariant())))
            .OrderBy(x => x.Distance).FirstOrDefault();
        return best.Name is not null && best.Distance > 0 && best.Distance <= Math.Max(1, t.Length / 3) ? best.Name : null;
    }

    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
