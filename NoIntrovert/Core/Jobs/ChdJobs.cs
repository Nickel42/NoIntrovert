using LibChdman;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Jobs;

/// <summary>The convert, verify and extract jobs behind the first three tabs.</summary>
public static class ChdJobs
{
    /// <summary>
    /// Converts one input to one or more CHDs: prepare (unpack/decode/rebuild
    /// a cue), create with libchdman, verify, then optionally remove the
    /// originals once every disc from this input verified. GameCube and Wii
    /// discs become RVZ instead (libdolphinrvz), unless the CHD target is set.
    /// </summary>
    public static JobOutcome Convert(JobContext c, string source)
    {
        var s = c.Settings;
        var log = c.Log;
        var name = Path.GetFileName(source);
        var outputFolder = OutputPaths.FolderFor(s, source);
        Directory.CreateDirectory(s.Performance.EffectiveTempFolder);

        log.Info($"Converting {name}");
        // One bar for the whole job: preparing, then each disc's compress and verify steps.
        var prepShare = Path.GetExtension(source).ToLowerInvariant() is ".cue" or ".gdi" or ".toc" or ".nrg" or ".iso" or ".bin" or ".img" or ".gcm" ? 0.02 : 0.15;
        c.Band(0, prepShare);
        c.Report.SetStage("Preparing");
        var preparer = new InputPreparer(s.Performance.EffectiveTempFolder, s.Chd.IsoMode, log);
        using var prepared = preparer.Prepare(source,
            new InlineProgress<(string Stage, double Fraction)>(p => { c.Report.SetStage(p.Stage); c.Report.SetProgress(p.Fraction); }), c.Token);

        var created = new List<(string Path, OperationStats Stats)>();
        var skipped = 0;
        var verifyFailed = false;
        var mustVerify = s.Chd.VerifyAfterCreate || s.Output.RemoveOriginals;

        for (var i = 0; i < prepared.Discs.Count; i++)
        {
            var disc = prepared.Discs[i];
            var discLabel = prepared.Discs.Count > 1 ? $" (disc {i + 1} of {prepared.Discs.Count})" : "";
            var discShare = (1 - prepShare) / prepared.Discs.Count;
            var discStart = prepShare + i * discShare;
            var compressShare = mustVerify ? discShare * 0.75 : discShare;
            log.Debug($"  input {disc.InputPath} [{disc.Note}] → {disc.Kind}");

            if (disc.IsNintendo && s.Nintendo.Target == NintendoTarget.Rvz)
            {
                var (result, rvzOutput, rvzStats) = RvzJobs.ConvertDisc(c, disc, outputFolder, discLabel, mustVerify, discStart, discShare);
                switch (result)
                {
                    case RvzJobs.DiscResult.Skipped: skipped++; break;
                    case RvzJobs.DiscResult.VerifyFailed: verifyFailed = true; created.Add((rvzOutput, rvzStats!)); break;
                    default: created.Add((rvzOutput, rvzStats!)); break;
                }
                continue;
            }

            var output = Path.Combine(outputFolder, disc.OutputName + ".chd");

            if (File.Exists(output) && !s.Output.Overwrite)
            {
                log.Warn($"  {Path.GetFileName(output)} already exists; skipped (turn on Overwrite to replace it).");
                skipped++;
                continue;
            }

            // A GameCube/Wii disc in GCZ/WIA/RVZ/TGC is decoded to ISO first; libchdman stores it as a DVD.
            var compressStart = discStart;
            if (disc.IsNintendo)
            {
                c.Band(discStart, compressShare * 0.3);
                disc = RvzJobs.DecodeForChd(c, disc, prepared.Work.Path, discLabel);
                compressStart += compressShare * 0.3;
                compressShare *= 0.7;
            }

            c.Band(compressStart, compressShare);
            c.TrackChdman($"Compressing{discLabel}");
            var chdSettings = c.OutputSettings();
            // D3: a PSP UMD goes in 2048-byte hunks (one sector each), which PPSSPP reads fastest.
            if (disc.Kind == DiscKind.Dvd && chdSettings.HunkSize == 0 && DiscProbe.SystemOf(disc.InputPath) == "playstation-portable")
                chdSettings.HunkSize = 2048;
            var stats = ChdService.Create(c.Chdman, disc, output, chdSettings, c.Token);
            log.Info($"  created {Path.GetFileName(output)}: {Size(stats.InputBytes)} → {Size(stats.OutputBytes)} ({stats.Ratio:P1}) in {stats.Elapsed:mm\\:ss\\.f} at {stats.MegabytesPerSecond:F1} MB/s");

            if (mustVerify)
            {
                c.Band(compressStart + compressShare, discStart + discShare - compressStart - compressShare);
                c.TrackChdman($"Verifying{discLabel}");
                var (result, _) = ChdService.Verify(c.Chdman, output, fix: false, c.Token);
                if (result.Outcome != ChdVerifyOutcome.Ok)
                {
                    verifyFailed = true;
                    log.Error($"  verification of {Path.GetFileName(output)} failed: {result.Outcome}");
                }
                else
                {
                    log.Debug($"  verified, raw SHA-1 {result.ComputedRawSha1}");
                }
            }
            created.Add((output, stats));
        }

        if (verifyFailed)
            return new JobOutcome(JobStatus.Failed, "Created, but verification failed", Combine(created));
        if (created.Count == 0)
            return JobOutcome.Skipped(skipped > 0 ? "Output already exists" : "Nothing to convert");

        if (s.Output.RemoveOriginals && skipped == 0)
        {
            c.Report.SetStage("Removing originals");
            FileRemover.Remove(DiscFiles.SetFiles(source), s.Output.UseRecycleBin, log);
        }

        var stats0 = Combine(created)!;
        var summary = created.Count == 1
            ? $"{Path.GetFileName(created[0].Path)} · {stats0.Ratio:P0}"
            : $"{created.Count} files · {stats0.Ratio:P0}";
        log.Success($"{name}: done{(mustVerify ? " and verified" : "")}");
        return new JobOutcome(skipped > 0 ? JobStatus.Warning : JobStatus.Succeeded, summary, stats0);
    }

    public static JobOutcome Verify(JobContext c, string chdPath)
    {
        var name = Path.GetFileName(chdPath);
        c.TrackChdman(c.Settings.Chd.FixHeaderSha1 ? "Verifying and fixing" : "Verifying");
        var (result, stats) = ChdService.Verify(c.Chdman, chdPath, c.Settings.Chd.FixHeaderSha1, c.Token);
        switch (result.Outcome)
        {
            case ChdVerifyOutcome.Ok:
                c.Log.Success($"{name}: OK (raw SHA-1 {result.ComputedRawSha1}) in {stats.Elapsed:mm\\:ss\\.f}");
                return new JobOutcome(JobStatus.Succeeded, "OK", stats, $"raw SHA-1 {result.ComputedRawSha1}");
            case ChdVerifyOutcome.Fixed:
                c.Log.Warn($"{name}: header SHA-1 was wrong and has been fixed");
                return new JobOutcome(JobStatus.Warning, "Fixed header SHA-1", stats);
            case ChdVerifyOutcome.NotApplicable:
                c.Log.Warn($"{name}: has no checksum to verify");
                return new JobOutcome(JobStatus.Warning, "No checksum stored", stats);
            default:
                c.Log.Error($"{name}: {result.Outcome} (header {result.HeaderRawSha1}, data {result.ComputedRawSha1})");
                return new JobOutcome(JobStatus.Failed, result.Outcome == ChdVerifyOutcome.RawMismatch ? "Data is damaged" : "Checksum mismatch", stats,
                    $"header raw SHA-1 {result.HeaderRawSha1}\ncomputed raw SHA-1 {result.ComputedRawSha1}");
        }
    }

    public static JobOutcome Extract(JobContext c, string chdPath)
    {
        var name = Path.GetFileName(chdPath);
        var folder = OutputPaths.FolderFor(c.Settings, chdPath);
        c.TrackChdman("Extracting");
        var (files, stats) = ChdService.Extract(c.Chdman, chdPath, folder, c.Settings.Chd.ExtractFormat, c.Settings.Chd.SplitBin,
            c.Settings.Output.Overwrite, c.Log, c.Token);
        c.Log.Success($"{name}: wrote {files.Count} file(s) ({Size(stats.OutputBytes)}) in {stats.Elapsed:mm\\:ss\\.f}");
        var first = Path.GetFileName(files[0]);
        return new JobOutcome(JobStatus.Succeeded, files.Count == 1 ? first : $"{first} + {files.Count - 1} file(s)", stats);
    }

    private static OperationStats? Combine(List<(string Path, OperationStats Stats)> items) =>
        items.Count == 0 ? null : new OperationStats(
            TimeSpan.FromTicks(items.Sum(i => i.Stats.Elapsed.Ticks)),
            items.Sum(i => i.Stats.InputBytes),
            items.Sum(i => i.Stats.OutputBytes));

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}
