using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.Archives;

public enum ArchiveAction { Compress, Convert, Extract }

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

/// <summary>The Archive tab's jobs: compress files/folders, convert between zip and 7z, and extract archives.</summary>
public static class ArchiveJobs
{
    public static readonly string[] ConvertibleExtensions = [".zip", ".7z"];

    public static string Extension(ArchiveFormat format) => format == ArchiveFormat.Zip ? ".zip" : ".7z";

    public static bool IsConvertible(string path) =>
        File.Exists(path) && ConvertibleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The archive name for one input: a folder's name, or a file's name without its extension.</summary>
    public static string BaseName(string input) =>
        Directory.Exists(input) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(input)) : Path.GetFileNameWithoutExtension(input);

    /// <summary>
    /// Compresses <paramref name="inputs"/> into one archive called
    /// <paramref name="name"/> (plus the format's extension, and " (2)" when
    /// that's taken), then tests it and, with Replace the originals, removes the inputs.
    /// </summary>
    public static JobOutcome CompressOne(JobContext c, IReadOnlyList<string> inputs, string name, string outputFolder)
    {
        var s = c.Settings;
        var wanted = Path.Combine(outputFolder, name + Extension(s.Archives.Format));
        var output = OutputPaths.Available(wanted, s.Output.Overwrite);
        if (inputs.Any(i => Path.GetFullPath(i).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)))
            return JobOutcome.Failed($"{Path.GetFileName(output)} would overwrite one of its own inputs.");
        if (output != wanted) c.Log.Info(OutputPaths.Renamed(wanted, output));
        if (File.Exists(output))
        {
            // Available only gives a taken name when Overwrite is on; otherwise something else made it meanwhile.
            if (!s.Output.Overwrite) return JobOutcome.Failed($"{Path.GetFileName(output)} appeared while this job was starting; nothing was overwritten.");
            File.Delete(output);
        }

        var inputBytes = inputs.Sum(SizeOf);

        // Already-compressed files (CHD, RVZ, 7z, ...) don't get smaller: one on its own is
        // left alone, and a set that's nearly all compressed is stored, not compressed again.
        var level = s.Archives.Level;
        var packedBytes = CompressedBytes(inputs);
        if (inputs.Count == 1 && File.Exists(inputs[0]) && KnownFormats.IsAlreadyCompressed(inputs[0]))
        {
            var ext = Path.GetExtension(inputs[0]).TrimStart('.').ToUpperInvariant();
            var hint = ext is "ZIP" or "7Z" ? " Use Convert to switch between zip and 7z." : "";
            c.Log.Info($"  {Path.GetFileName(inputs[0])} is already compressed ({ext}); a {FormatName(s.Archives.Format)} wouldn't make it smaller, so it's skipped.{hint}");
            return JobOutcome.Skipped($"Already compressed ({ext})");
        }
        if (level != ArchiveLevel.Store && inputBytes > 0 && packedBytes >= 0.9 * inputBytes)
        {
            level = ArchiveLevel.Store;
            c.Log.Info($"  {packedBytes * 100 / inputBytes}% of {Describe(inputs)} is already compressed; storing it without compressing again.");
        }

        c.Log.Info($"Compressing {Describe(inputs)} → {Path.GetFileName(output)} ({FormatName(s.Archives.Format)}, {level})");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var engine = new SevenZipEngine();
        c.Report.SetStage(level == ArchiveLevel.Store ? "Storing" : "Compressing");
        engine.Compress(s.Archives.Format, level, s.Performance.ThreadsPerJob, s.Archives.Solid, inputs, output,
            contentsOnly: false, c.Report.SetProgress, c.Token);
        sw.Stop();

        var outcome = AfterWrite(c, engine, output, inputs, inputBytes, sw.Elapsed);
        return outcome;
    }

    /// <summary>One archive per file or subfolder inside <paramref name="folder"/>.</summary>
    public static JobOutcome CompressEntries(JobContext c, string folder, string outputFolderOrEmpty)
    {
        var entries = Directory.EnumerateFileSystemEntries(folder)
            .Where(e => !IsOwnOutput(e, c.Settings.Archives.Format))
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
            var o = CompressOne(new JobContext(c.Chdman, c.Settings, c.Log, sub, c.Token, c.RvzSource), [entries[i]], names[i], outputFolder);
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

    /// <summary>Rewrites a zip as 7z or a 7z as zip, through a temporary folder.</summary>
    public static JobOutcome Convert(JobContext c, string archive)
    {
        var s = c.Settings;
        var ext = Path.GetExtension(archive).ToLowerInvariant();
        var target = s.Archives.ConvertTarget switch
        {
            ConvertTarget.ToZip => ArchiveFormat.Zip,
            ConvertTarget.ToSevenZip => ArchiveFormat.SevenZip,
            _ => ext == ".7z" ? ArchiveFormat.Zip : ArchiveFormat.SevenZip,
        };
        if (ext == Extension(target))
            return JobOutcome.Skipped($"Already {FormatName(target)}");

        var outputFolder = OutputPaths.FolderFor(s, archive);
        var wanted = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(archive) + Extension(target));
        var output = OutputPaths.Available(wanted, s.Output.Overwrite);
        if (output != wanted) c.Log.Info(OutputPaths.Renamed(wanted, output));
        if (File.Exists(output))
        {
            // Available only gives a taken name when Overwrite is on; otherwise something else made it meanwhile.
            if (!s.Output.Overwrite) return JobOutcome.Failed($"{Path.GetFileName(output)} appeared while this job was starting; nothing was overwritten.");
            File.Delete(output);
        }

        Directory.CreateDirectory(s.Performance.EffectiveTempFolder);
        using var work = new WorkDirectory(s.Performance.EffectiveTempFolder);
        var contents = Path.Combine(work.Path, "contents");
        using var engine = new SevenZipEngine();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        c.Log.Info($"Converting {Path.GetFileName(archive)} → {Path.GetFileName(output)}");
        c.Report.SetStage("Extracting");
        engine.Extract(archive, contents, f => c.Report.SetProgress(f * 0.4), c.Token);
        if (!Directory.EnumerateFileSystemEntries(contents).Any())
            return JobOutcome.Skipped("Archive is empty");

        c.Report.SetStage($"Compressing to {FormatName(target)}");
        engine.Compress(target, s.Archives.Level, s.Performance.ThreadsPerJob, s.Archives.Solid, [contents], output,
            contentsOnly: true, f => c.Report.SetProgress(0.4 + f * 0.6), c.Token);
        sw.Stop();

        return AfterWrite(c, engine, output, [archive], new FileInfo(archive).Length, sw.Elapsed);
    }

    /// <summary>
    /// Rewrites a zip or 7z in its own format with a method RetroArch opens
    /// (R8: 7-Zip's default LZMA2 for 7z, Deflate for zip): unpacked to a
    /// temporary folder, packed again, and always tested before it takes the
    /// original's place. With a Save to folder, the original stays where
    /// it is (a taken name there gets " (2)").
    /// </summary>
    public static JobOutcome Recompress(JobContext c, string archive)
    {
        var s = c.Settings;
        var format = Path.GetExtension(archive).Equals(".zip", StringComparison.OrdinalIgnoreCase) ? ArchiveFormat.Zip : ArchiveFormat.SevenZip;
        var output = Path.Combine(OutputPaths.FolderFor(s, archive), Path.GetFileName(archive));
        var inPlace = Path.GetFullPath(output).Equals(Path.GetFullPath(archive), StringComparison.OrdinalIgnoreCase);
        if (!inPlace)
        {
            var wanted = output;
            output = OutputPaths.Available(wanted, s.Output.Overwrite);
            if (output != wanted) c.Log.Info(OutputPaths.Renamed(wanted, output));
        }

        Directory.CreateDirectory(s.Performance.EffectiveTempFolder);
        using var work = new WorkDirectory(s.Performance.EffectiveTempFolder);
        var contents = Path.Combine(work.Path, "contents");
        var packed = Path.Combine(work.Path, Path.GetFileName(archive));
        using var engine = new SevenZipEngine();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        c.Log.Info($"Recompressing {Path.GetFileName(archive)} ({FormatName(format)}, {s.Archives.Level})");
        c.Report.SetStage("Extracting");
        engine.Extract(archive, contents, f => c.Report.SetProgress(f * 0.4), c.Token);
        if (!Directory.EnumerateFileSystemEntries(contents).Any())
            return JobOutcome.Skipped("Archive is empty");

        c.Report.SetStage($"Compressing to {FormatName(format)}");
        engine.Compress(format, s.Archives.Level, s.Performance.ThreadsPerJob, s.Archives.Solid, [contents], packed,
            contentsOnly: true, f => c.Report.SetProgress(0.4 + f * 0.5), c.Token);
        c.Report.SetStage("Testing");
        try { engine.Test(packed, f => c.Report.SetProgress(0.9 + f * 0.1), c.Token); }
        catch (ArchiveException ex)
        {
            c.Log.Error($"  testing the new {Path.GetFileName(archive)} failed: {ex.Message}; the original is unchanged.");
            return JobOutcome.Failed("The recompressed archive failed its test");
        }
        sw.Stop();

        var inputBytes = new FileInfo(archive).Length;
        var stats = new OperationStats(sw.Elapsed, inputBytes, new FileInfo(packed).Length);
        File.Move(packed, output, overwrite: true);
        if (!inPlace && s.Output.ReplacesOriginals)
        {
            c.Report.SetStage("Removing originals");
            FileRemover.Remove([archive], s.Output.UseRecycleBin, c.Log);
        }
        c.Log.Success($"{Path.GetFileName(output)}: recompressed and tested, {ChdJobs.Size(inputBytes)} → {ChdJobs.Size(stats.OutputBytes)}");
        return new JobOutcome(JobStatus.Succeeded, $"{Path.GetFileName(output)} · {stats.Ratio:P0}", stats);
    }

    /// <summary>
    /// Unpacks an archive into a folder named after it (or straight into the
    /// output folder). Everything lands in a hidden staging folder first and
    /// is then moved into place, so existing files are only replaced with
    /// Overwrite on (otherwise a taken name gets " (2)"), and a failed or
    /// cancelled extraction leaves nothing behind. 7-Zip checks every file's
    /// CRC as it extracts, so a clean extraction is also a full test of the
    /// archive, and Replace the originals then removes it.
    /// </summary>
    public static JobOutcome Extract(JobContext c, string archive)
    {
        var s = c.Settings;
        var outputFolder = OutputPaths.FolderFor(s, archive);
        var destination = s.Archives.ExtractToOwnFolder
            ? Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(archive))
            : outputFolder;

        if (s.Archives.ExtractToOwnFolder && !s.Output.Overwrite && Directory.Exists(destination) &&
            Directory.EnumerateFileSystemEntries(destination).Any())
        {
            var wanted = destination;
            destination = OutputPaths.Available(wanted, overwrite: false, isFolder: true);
            c.Log.Info(OutputPaths.Renamed(wanted, destination));
        }
        else if (s.Archives.ExtractToOwnFolder && File.Exists(destination))
        {
            destination = OutputPaths.Available(destination, overwrite: false, isFolder: true);
        }

        // Staging next to the destination keeps the final move on one drive.
        var staging = Path.Combine(outputFolder, $".noi-extract-{Guid.NewGuid().ToString("N")[..8]}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        c.Log.Info($"Extracting {Path.GetFileName(archive)} → {destination}");
        try
        {
            using (var engine = new SevenZipEngine())
            {
                c.Report.SetStage("Extracting");
                engine.Extract(archive, staging, c.Report.SetProgress, c.Token);
            }

            c.Report.SetStage("Moving into place");
            Directory.CreateDirectory(destination);
            var (moved, renamed) = MoveEntries(staging, destination, s.Output.Overwrite, c.Log);
            sw.Stop();

            var stats = new OperationStats(sw.Elapsed, new FileInfo(archive).Length, SizeOf(destination));
            if (moved == 0)
                return JobOutcome.Skipped("Archive is empty");
            if (renamed > 0)
                c.Log.Info($"  {renamed} item(s) were already in {Path.GetFileName(destination)}, so the new ones got \" (2)\" names (turn on Overwrite to replace them).");

            if (s.Output.ReplacesOriginals)
            {
                c.Report.SetStage("Removing the archive");
                FileRemover.Remove([archive], s.Output.UseRecycleBin, c.Log);
            }

            c.Log.Success($"{Path.GetFileName(archive)}: extracted {moved} item(s) in {sw.Elapsed:mm\\:ss\\.f}");
            var where = s.Archives.ExtractToOwnFolder ? Path.GetFileName(destination) + Path.DirectorySeparatorChar : "output folder";
            return new JobOutcome(JobStatus.Succeeded, $"{moved} item(s) → {where}", stats);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Moves everything under <paramref name="from"/> into <paramref name="to"/>,
    /// merging folders. An existing file is replaced only when
    /// <paramref name="overwrite"/> is on; otherwise the new one gets a
    /// " (2)" name, and is counted as renamed.
    /// </summary>
    private static (int Moved, int Renamed) MoveEntries(string from, string to, bool overwrite, ILog log)
    {
        int moved = 0, renamed = 0;
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            var target = Path.Combine(to, Path.GetFileName(dir));
            if (Directory.Exists(target))
            {
                var (m, r) = MoveEntries(dir, target, overwrite, log); // merge into the existing folder
                moved += m;
                renamed += r;
                continue;
            }
            if (File.Exists(target))
            {
                if (overwrite) File.Delete(target);
                else { target = OutputPaths.Available(target, overwrite: false, isFolder: true); renamed++; }
            }
            Directory.Move(dir, target);
            moved++;
        }
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (Directory.Exists(target) || (File.Exists(target) && !overwrite))
            {
                target = OutputPaths.Available(target, overwrite: false);
                renamed++;
            }
            File.Move(file, target, overwrite: true);
            moved++;
        }
        return (moved, renamed);
    }

    /// <summary>Tests a new archive (always with Replace the originals), then, with Replace, removes the originals.</summary>
    private static JobOutcome AfterWrite(JobContext c, SevenZipEngine engine, string output, IReadOnlyList<string> originals,
        long inputBytes, TimeSpan elapsed)
    {
        var s = c.Settings;
        var outputBytes = new FileInfo(output).Length;
        var stats = new OperationStats(elapsed, inputBytes, outputBytes);
        c.Log.Info($"  wrote {Path.GetFileName(output)}: {ChdJobs.Size(inputBytes)} → {ChdJobs.Size(outputBytes)} ({stats.Ratio:P1}) in {elapsed:mm\\:ss\\.f}");

        var replace = s.Output.ReplacesOriginals;
        if (s.Archives.TestAfter || replace)
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
                return new JobOutcome(JobStatus.Failed, replace ? "The test failed, so the originals were kept" : "Written, but the test failed", stats);
            }
        }

        if (replace)
        {
            c.Report.SetStage("Removing originals");
            FileRemover.Remove(originals, s.Output.UseRecycleBin, c.Log);
        }

        c.Log.Success($"{Path.GetFileName(output)}: done{(s.Archives.TestAfter || replace ? " and tested" : "")}");
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

    /// <summary>The bytes in <paramref name="inputs"/> (files, and files inside folders) that are already compressed.</summary>
    public static long CompressedBytes(IEnumerable<string> inputs)
    {
        long total = 0;
        foreach (var input in inputs)
        {
            var files = Directory.Exists(input) ? Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories) : [input];
            foreach (var f in files.Where(KnownFormats.IsAlreadyCompressed))
            {
                try { total += new FileInfo(f).Length; }
                catch (IOException) { }
            }
        }
        return total;
    }

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
    internal sealed class SubReport(IJobReport inner, double start, double weight, string prefix) : IJobReport
    {
        public void SetStage(string stage) => inner.SetStage(prefix + stage);
        public void SetProgress(double fraction) => inner.SetProgress(start + Math.Clamp(fraction, 0, 1) * weight);
    }
}
