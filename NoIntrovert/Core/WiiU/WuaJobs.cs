using LibWiiUKit;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.WiiU;

/// <summary>
/// The Wii U tab's job: build one .wua (Cemu's ZArchive) per game from
/// extracted title folders and existing .wua files, holding the base game,
/// its newest update and its DLC. The building itself is lib-wii-u-kit's
/// <see cref="WuaBuilder"/>; this adds NoIntrovert's settings, output
/// folders, overwrite rule, logging and Replace the originals.
/// </summary>
public static class WuaJobs
{
    /// <summary>What a missing keys.txt is reported as.</summary>
    public const string NoKeysMessage =
        "No keys.txt with keys was found. Put your own keys.txt (the same file Cemu uses) in NoIntrovert's settings folder or Cemu's, or choose it on the Wii U tab.";

    /// <summary>
    /// The keys.txt in <paramref name="path"/> if set, otherwise every one
    /// found in NoIntrovert's settings folder and Cemu's usual folder.
    /// </summary>
    public static WiiUKeys LoadKeys(string? path) =>
        WiiUKeys.Load(path, [Path.Combine(AppSettings.SettingsDirectory, "keys.txt"), .. WiiUKeys.DefaultLocations()]);

    /// <summary>
    /// The paths in <paramref name="folder"/> worth listing: title folders and
    /// .wua files, looking a few levels down but not inside a title.
    /// </summary>
    public static List<string> FindSources(string folder, bool recursive, int maxDepth = 4) =>
        FormatRegistry.Default.Scan(folder, recursive, maxDepth)
            .Where(s => KnownFormats.WiiUInputs.Contains(s.Format))
            .Select(s => s.Primary)
            .ToList();

    /// <summary>
    /// Writes the .wua for one game from every title found in
    /// <paramref name="sources"/> that belongs to <paramref name="baseId"/>.
    /// Older versions of the same title are dropped in favour of the newest.
    /// The archive is written to a temporary file, verified, and only then
    /// moved into place (replacing an input .wua of the same name if needed).
    /// </summary>
    /// <param name="outputFolder">Where the .wua goes; by default the output folder setting or the first source's folder.</param>
    /// <param name="removeSources">Whether Replace the originals may remove <paramref name="sources"/> (not when they were unpacked from an archive).</param>
    public static JobOutcome Build(JobContext c, ulong baseId, IReadOnlyList<string> sources, string? outputFolder = null, bool removeSources = true)
    {
        try { return BuildGame(c, baseId, sources, outputFolder, removeSources); }
        catch (WiiUKeysMissingException) { throw new WiiUKeysMissingException(NoKeysMessage); }
    }

    private static JobOutcome BuildGame(JobContext c, ulong baseId, IReadOnlyList<string> sources, string? outputFolder, bool removeSources)
    {
        var s = c.Settings;
        var log = c.Log;

        // Every title from every source, then the newest of each title ID.
        var keys = NeedsKeys(sources) ? LoadKeys(s.Keys.WiiUKeysPath) : WiiUKeys.Empty;
        var chosen = WuaBuilder.ChooseTitles(sources.SelectMany(p => WiiUTitles.Read(p, keys)), baseId, (level, m) => Log(log, level, m));
        if (chosen.Count == 0) return JobOutcome.Skipped("No titles for this game");

        // Next to the first source, never inside it: "Game [NUS]\" would otherwise give the folder itself.
        outputFolder ??= OutputPaths.FolderFor(s, ScanSet.Normalize(sources[0]));
        Directory.CreateDirectory(outputFolder);
        var output = Path.Combine(outputFolder, WiiUTitles.WuaFileName(baseId, WuaBuilder.GameName(chosen)));
        var replacesInput = sources.Any(p => Path.GetFullPath(p).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase));
        if (!replacesInput)
        {
            var wanted = output;
            output = OutputPaths.Available(wanted, s.Output.Overwrite);
            if (output != wanted) log.Info(OutputPaths.Renamed(wanted, output));
        }

        log.Info($"Building {Path.GetFileName(output)}: {string.Join(", ", chosen.Select(t => $"{t.KindText} v{t.Version}"))}");
        var result = WuaBuilder.Build(output, chosen, new WuaBuildOptions
        {
            Keys = keys,
            Stage = c.Report.SetStage,
            Progress = c.Report.SetProgress,
            Log = (level, m) => Log(log, level, m),
            CancellationToken = c.Token,
        });

        var stats = new OperationStats(result.Elapsed, result.InputBytes, result.OutputBytes);
        log.Info($"  wrote {Path.GetFileName(output)}: {result.FileCount} files, {ChdJobs.Size(stats.InputBytes)} → {ChdJobs.Size(stats.OutputBytes)} ({stats.Ratio:P1}) in {result.Elapsed:mm\\:ss\\.f}");

        if (s.Output.ReplacesOriginals && removeSources)
        {
            c.Report.SetStage("Removing originals");
            var originals = sources.Where(p => !Path.GetFullPath(p).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
                .SelectMany(WiiUEncrypted.ImageFiles);
            FileRemover.Remove(originals, s.Output.UseRecycleBin, log);
        }

        log.Success($"{Path.GetFileName(output)}: done and verified");
        var parts = string.Join(" + ", chosen.Select(t => t.KindText).Distinct());
        return new JobOutcome(JobStatus.Succeeded, $"{Path.GetFileName(output)} · {parts}", stats);
    }

    /// <summary>
    /// A zip, 7z or tar holding Wii U games (a .wux, a .wua, title or NUS
    /// folders): unpacked into the temp folder, then one .wua per game, written
    /// next to the archive. Replace the originals removes the archive once every
    /// game in it is built and verified.
    /// </summary>
    public static JobOutcome BuildFromArchive(JobContext c, string archive)
    {
        var s = c.Settings;
        Directory.CreateDirectory(s.Performance.EffectiveTempFolder);
        using var work = new WorkDirectory(s.Performance.EffectiveTempFolder);

        c.Band(0, 0.3);
        c.Report.SetStage("Unpacking archive");
        c.Log.Info($"Unpacking {Path.GetFileName(archive)}...");
        using (var engine = new SevenZipEngine())
            engine.Extract(archive, work.Path, c.Report.SetProgress, c.Token);

        var sources = FormatRegistry.Default.Scan(work.Path, recursive: true, maxDepth: 6)
            .Where(f => KnownFormats.WiiUBuildable.Contains(f.Format))
            .Select(f => f.Primary)
            .ToList();
        var keys = NeedsKeys(sources) ? LoadKeys(s.Keys.WiiUKeysPath) : WiiUKeys.Empty;
        IReadOnlyList<ulong> games;
        try { games = WuaBuilder.Games(sources, keys); }
        catch (WiiUKeysMissingException) { throw new WiiUKeysMissingException(NoKeysMessage); }
        // Nothing built means nothing verified, so the archive is never removed for it.
        if (games.Count == 0)
        {
            c.Log.Warn($"  {Path.GetFileName(archive)}: no Wii U game found (no .wux, .wud, .wua, NUS or title folder inside)");
            return JobOutcome.Skipped("No Wii U game found in the archive");
        }
        var outputFolder = OutputPaths.FolderFor(s, archive);

        var outcomes = new List<JobOutcome>();
        for (var i = 0; i < games.Count; i++)
        {
            c.Band(0.3 + 0.7 * i / games.Count, 0.7 / games.Count);
            outcomes.Add(Build(c, games[i], sources, outputFolder, removeSources: false));
        }

        var built = outcomes.Where(o => o.Status is JobStatus.Succeeded or JobStatus.Warning).ToList();
        if (s.Output.ReplacesOriginals && built.Count == outcomes.Count)
        {
            c.Report.SetStage("Removing the archive");
            FileRemover.Remove([archive], s.Output.UseRecycleBin, c.Log);
        }
        if (outcomes.Count == 1) return outcomes[0];
        var failed = outcomes.Count(o => o.Status == JobStatus.Failed);
        var stats = built.Select(o => o.Stats).OfType<OperationStats>()
            .Aggregate((OperationStats?)null, (a, b) => a is null ? b : new OperationStats(a.Elapsed + b.Elapsed, a.InputBytes + b.InputBytes, a.OutputBytes + b.OutputBytes));
        return new JobOutcome(failed > 0 ? JobStatus.Failed : built.Count == outcomes.Count ? JobStatus.Succeeded : JobStatus.Warning,
            $"{built.Count} of {outcomes.Count} games built" + (failed > 0 ? $", {failed} failed" : ""), stats);
    }

    private static bool NeedsKeys(IEnumerable<string> sources) =>
        sources.Any(p => WiiUEncrypted.IsNusFolder(p) || WiiUEncrypted.IsDiscImage(p));

    private static void Log(ILog log, WuaLogLevel level, string message)
    {
        switch (level)
        {
            case WuaLogLevel.Warning: log.Warn(message); break;
            case WuaLogLevel.Info: log.Info(message); break;
            default: log.Debug(message); break;
        }
    }
}
