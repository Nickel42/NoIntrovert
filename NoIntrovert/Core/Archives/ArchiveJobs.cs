using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.Archives;

public enum ArchiveAction { Compress, Convert }

/// <summary>How the files and folders in the list become archives.</summary>
public enum ArchiveGrouping
{
    /// <summary>Each file or folder in the list gets its own archive.</summary>
    PerItem,
    /// <summary>Each file and subfolder directly inside a listed folder gets its own archive.</summary>
    PerEntryInFolders,
    /// <summary>Everything in the list goes into one archive.</summary>
    Single,
}

public enum ConvertTarget { Swap, ToSevenZip, ToZip }

/// <summary>The Archive tab's jobs: compress files/folders, and convert between zip and 7z.</summary>
public static class ArchiveJobs
{
    public static readonly string[] ConvertibleExtensions = [".zip", ".7z", ".rar"];

    public static string Extension(ArchiveFormat format) => format == ArchiveFormat.Zip ? ".zip" : ".7z";

    public static bool IsConvertible(string path) =>
        File.Exists(path) && ConvertibleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The archive name for one input: a folder's name, or a file's name without its extension.</summary>
    public static string BaseName(string input) =>
        Directory.Exists(input) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(input)) : Path.GetFileNameWithoutExtension(input);

    /// <summary>
    /// Compresses <paramref name="inputs"/> into one archive called
    /// <paramref name="name"/> (plus the format's extension), then tests it
    /// and removes the inputs if asked.
    /// </summary>
    public static JobOutcome CompressOne(JobContext c, IReadOnlyList<string> inputs, string name, string outputFolder)
    {
        var s = c.Settings;
        var output = Path.Combine(outputFolder, name + Extension(s.ArchiveFormat));
        if (inputs.Any(i => Path.GetFullPath(i).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)))
            return JobOutcome.Failed($"{Path.GetFileName(output)} would overwrite one of its own inputs.");
        if (File.Exists(output))
        {
            if (!s.Overwrite)
            {
                c.Log.Warn($"  {Path.GetFileName(output)} already exists; skipped (turn on Overwrite to replace it).");
                return JobOutcome.Skipped("Archive already exists");
            }
            File.Delete(output);
        }

        var inputBytes = inputs.Sum(SizeOf);
        c.Log.Info($"Compressing {Describe(inputs)} → {Path.GetFileName(output)} ({FormatName(s.ArchiveFormat)}, {s.ArchiveLevel})");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var engine = new ArchiveEngine();
        c.Report.SetStage("Compressing");
        engine.Compress(s.ArchiveFormat, s.ArchiveLevel, s.ThreadsPerJob, s.ArchiveSolid, inputs, output,
            contentsOnly: false, c.Report.SetProgress, c.Token);
        sw.Stop();

        var outcome = AfterWrite(c, engine, output, inputs, inputBytes, sw.Elapsed);
        return outcome;
    }

    /// <summary>One archive per file or subfolder inside <paramref name="folder"/>.</summary>
    public static JobOutcome CompressEntries(JobContext c, string folder, string outputFolderOrEmpty)
    {
        var entries = Directory.EnumerateFileSystemEntries(folder)
            .Where(e => !IsOwnOutput(e, c.Settings.ArchiveFormat))
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
        if (entries.Count == 0) return JobOutcome.Skipped("Folder is empty");

        var outputFolder = string.IsNullOrWhiteSpace(outputFolderOrEmpty) ? folder : outputFolderOrEmpty;
        var names = UniqueNames(entries);
        int done = 0, failed = 0, skipped = 0;
        long inBytes = 0, outBytes = 0;
        var elapsed = TimeSpan.Zero;

        for (var i = 0; i < entries.Count; i++)
        {
            c.Token.ThrowIfCancellationRequested();
            var weight = 1.0 / entries.Count;
            var index = i;
            var sub = new SubReport(c.Report, index * weight, weight, $"{i + 1}/{entries.Count}: ");
            var o = CompressOne(new JobContext(c.Chdman, c.Settings, c.Log, sub, c.Token), [entries[i]], names[i], outputFolder);
            switch (o.Status)
            {
                case JobStatus.Succeeded or JobStatus.Warning: done++; break;
                case JobStatus.Skipped: skipped++; break;
                default: failed++; c.Log.Error($"  {Path.GetFileName(entries[i])}: {o.Summary}"); break;
            }
            if (o.Stats is { } st) { inBytes += st.InputBytes; outBytes += st.OutputBytes; elapsed += st.Elapsed; }
        }

        var stats = new OperationStats(elapsed, inBytes, outBytes);
        var summary = $"{done} archive(s)" + (skipped > 0 ? $", {skipped} skipped" : "") + (failed > 0 ? $", {failed} failed" : "");
        var status = failed > 0 ? JobStatus.Failed : skipped > 0 ? JobStatus.Warning : JobStatus.Succeeded;
        return new JobOutcome(status, summary, stats);
    }

    /// <summary>Rewrites a zip as 7z or a 7z as zip (or a rar as either), through a temporary folder.</summary>
    public static JobOutcome Convert(JobContext c, string archive)
    {
        var s = c.Settings;
        var ext = Path.GetExtension(archive).ToLowerInvariant();
        var target = s.ArchiveConvertTarget switch
        {
            ConvertTarget.ToZip => ArchiveFormat.Zip,
            ConvertTarget.ToSevenZip => ArchiveFormat.SevenZip,
            _ => ext == ".7z" ? ArchiveFormat.Zip : ArchiveFormat.SevenZip,
        };
        if (ext == Extension(target))
            return JobOutcome.Skipped($"Already {FormatName(target)}");

        var outputFolder = OutputPaths.FolderFor(s, archive);
        var output = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(archive) + Extension(target));
        if (File.Exists(output))
        {
            if (!s.Overwrite)
            {
                c.Log.Warn($"  {Path.GetFileName(output)} already exists; skipped (turn on Overwrite to replace it).");
                return JobOutcome.Skipped("Target already exists");
            }
            File.Delete(output);
        }

        Directory.CreateDirectory(s.EffectiveTempFolder);
        using var work = new WorkDirectory(s.EffectiveTempFolder);
        var contents = Path.Combine(work.Path, "contents");
        using var engine = new ArchiveEngine();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        c.Log.Info($"Converting {Path.GetFileName(archive)} → {Path.GetFileName(output)}");
        c.Report.SetStage("Extracting");
        engine.Extract(archive, contents, f => c.Report.SetProgress(f * 0.4), c.Token);
        if (!Directory.EnumerateFileSystemEntries(contents).Any())
            return JobOutcome.Skipped("Archive is empty");

        c.Report.SetStage($"Compressing to {FormatName(target)}");
        engine.Compress(target, s.ArchiveLevel, s.ThreadsPerJob, s.ArchiveSolid, [contents], output,
            contentsOnly: true, f => c.Report.SetProgress(0.4 + f * 0.6), c.Token);
        sw.Stop();

        return AfterWrite(c, engine, output, [archive], new FileInfo(archive).Length, sw.Elapsed);
    }

    /// <summary>Tests a new archive (always, when originals are to be removed), then removes them.</summary>
    private static JobOutcome AfterWrite(JobContext c, ArchiveEngine engine, string output, IReadOnlyList<string> originals,
        long inputBytes, TimeSpan elapsed)
    {
        var s = c.Settings;
        var outputBytes = new FileInfo(output).Length;
        var stats = new OperationStats(elapsed, inputBytes, outputBytes);
        c.Log.Info($"  wrote {Path.GetFileName(output)}: {ChdJobs.Size(inputBytes)} → {ChdJobs.Size(outputBytes)} ({stats.Ratio:P1}) in {elapsed:mm\\:ss\\.f}");

        if (s.ArchiveTestAfter || s.ArchiveDeleteOriginals)
        {
            c.Report.SetStage("Testing");
            try
            {
                engine.Test(output, c.Report.SetProgress, c.Token);
                c.Log.Debug($"  {Path.GetFileName(output)} tested OK");
            }
            catch (ArchiveException ex)
            {
                c.Log.Error($"  testing {Path.GetFileName(output)} failed: {ex.Message}");
                return new JobOutcome(JobStatus.Failed, "Written, but the test failed", stats);
            }
        }

        if (s.ArchiveDeleteOriginals)
        {
            c.Report.SetStage("Removing originals");
            FileRemover.Remove(originals, s.UseRecycleBin, c.Log);
        }

        c.Log.Success($"{Path.GetFileName(output)}: done{(s.ArchiveTestAfter || s.ArchiveDeleteOriginals ? " and tested" : "")}");
        return new JobOutcome(JobStatus.Succeeded, $"{Path.GetFileName(output)} · {stats.Ratio:P0}", stats);
    }

    /// <summary>
    /// Archive names for a set of inputs: the base name, or the full file
    /// name where two inputs would otherwise collide (game.bin / game.cue).
    /// </summary>
    public static List<string> UniqueNames(IReadOnlyList<string> inputs)
    {
        var names = inputs.Select(BaseName).ToList();
        var clashes = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < inputs.Count; i++)
            if (clashes.Contains(names[i])) names[i] = Path.GetFileName(Path.TrimEndingDirectorySeparator(inputs[i]));
        return names;
    }

    private static bool IsOwnOutput(string entry, ArchiveFormat format) =>
        File.Exists(entry) && Path.GetExtension(entry).Equals(Extension(format), StringComparison.OrdinalIgnoreCase);

    public static string FormatName(ArchiveFormat format) => format == ArchiveFormat.Zip ? "zip" : "7z";

    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path))
                return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return 0;
    }

    private static string Describe(IReadOnlyList<string> inputs) =>
        inputs.Count == 1 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(inputs[0])) : $"{inputs.Count} items";

    /// <summary>Maps one step's progress into a slice of the job's progress bar.</summary>
    private sealed class SubReport(IJobReport inner, double start, double weight, string prefix) : IJobReport
    {
        public void SetStage(string stage) => inner.SetStage(prefix + stage);
        public void SetProgress(double fraction) => inner.SetProgress(start + Math.Clamp(fraction, 0, 1) * weight);
    }
}
