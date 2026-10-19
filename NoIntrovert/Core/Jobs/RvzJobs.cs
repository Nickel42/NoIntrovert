using System.Diagnostics;
using LibDolphinRvz;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Jobs;

/// <summary>What to make of GameCube and Wii discs on the Convert tab.</summary>
public enum NintendoTarget { Rvz, Chd }

/// <summary>
/// GameCube/Wii work through libdolphinrvz (Dolphin's own disc code): disc →
/// RVZ on the Convert tab, a container → ISO before making a CHD, and
/// RVZ/WIA/GCZ → ISO on the Extract tab.
/// </summary>
public static class RvzJobs
{
    public enum DiscResult { Created, Skipped, VerifyFailed }

    public static ConvertSettings SettingsFor(AppSettings s, RvzFormat format = RvzFormat.Rvz) => new()
    {
        Format = format,
        Compression = s.RvzCompression,
        CompressionLevel = s.RvzCompression is RvzCompression.None or RvzCompression.Purge ? 0 : s.RvzCompressionLevel,
        BlockSize = format == RvzFormat.Iso ? 0 : s.RvzBlockSize,
        Scrub = format == RvzFormat.Rvz && s.RvzScrub,
        Overwrite = s.Overwrite,
    };

    /// <summary>One prepared GameCube/Wii disc → .rvz in <paramref name="outputFolder"/>, then optionally verified.</summary>
    internal static (DiscResult Result, string Output, OperationStats? Stats) ConvertDisc(
        JobContext c, PreparedDisc disc, string outputFolder, string discLabel, bool verify, double barStart = 0, double barSpan = 1)
    {
        var s = c.Settings;
        var log = c.Log;
        var rvz = c.Rvz;
        var output = Path.Combine(outputFolder, disc.OutputName + ".rvz");

        var info = rvz.Probe(disc.InputPath, c.Token);
        log.Debug($"  {Platform(info)} {info.GameId} \"{info.InternalName}\" in {info.Container}");
        if (info.Container == DiscContainer.Rvz)
        {
            log.Warn($"  {Path.GetFileName(disc.InputPath)} is already RVZ; skipped.");
            return (DiscResult.Skipped, output, null);
        }
        if (!info.CanConvert)
            throw new DolphinRvzException(DrvzStatus.Unsupported,
                $"{info.Container} images can't be converted: the container doesn't record the disc's exact size. Convert it to ISO with the tool that made it first.");
        if (File.Exists(output) && !s.Overwrite)
        {
            log.Warn($"  {Path.GetFileName(output)} already exists; skipped (turn on Overwrite to replace it).");
            return (DiscResult.Skipped, output, null);
        }
        if (info.IsNKit)
            log.Warn($"  {Path.GetFileName(disc.InputPath)} is an NKit image: the RVZ will be NKit too, not a Redump-identical dump.");

        var settings = SettingsFor(s);
        var sw = Stopwatch.StartNew();
        c.Band(barStart, verify ? barSpan * 0.6 : barSpan);
        c.TrackRvz($"Compressing to RVZ{discLabel}");
        rvz.Convert(disc.InputPath, output, settings, c.Token);
        var stats = new OperationStats(sw.Elapsed, new FileInfo(disc.InputPath).Length, new FileInfo(output).Length);
        log.Info($"  created {Path.GetFileName(output)}: {ChdJobs.Size(stats.InputBytes)} → {ChdJobs.Size(stats.OutputBytes)} ({stats.Ratio:P1}) in {stats.Elapsed:mm\\:ss\\.f} at {stats.MegabytesPerSecond:F1} MB/s");

        if (!verify)
            return (DiscResult.Created, output, stats);

        // RVZ has no stored checksum for the whole disc: verifying means decoding it again and
        // comparing with the source. A scrubbed RVZ differs from its source by design.
        c.Band(barStart + barSpan * 0.6, barSpan * 0.2);
        c.TrackRvz($"Verifying{discLabel}: reading the RVZ");
        var made = rvz.Hash(output, c.Token);
        if (settings.Scrub)
        {
            log.Warn($"  scrubbed, so only checked that the RVZ decodes (SHA-1 {made.Sha1}); it won't match a Redump checksum.");
            return (DiscResult.Created, output, stats);
        }
        c.Band(barStart + barSpan * 0.8, barSpan * 0.2);
        c.TrackRvz($"Verifying{discLabel}: reading the source");
        var original = rvz.Hash(disc.InputPath, c.Token);
        if (original.Sha1 != made.Sha1 || original.Size != made.Size)
        {
            log.Error($"  verification of {Path.GetFileName(output)} failed: source SHA-1 {original.Sha1}, RVZ {made.Sha1}");
            return (DiscResult.VerifyFailed, output, stats);
        }
        log.Debug($"  verified, SHA-1 {made.Sha1} (CRC32 {made.Crc32})");
        return (DiscResult.Created, output, stats);
    }

    /// <summary>
    /// A disc in one of Dolphin's containers (GCZ, WIA, RVZ, TGC) decoded to a plain
    /// ISO in the work folder, for libchdman to make a CHD from. Plain images pass through.
    /// </summary>
    internal static PreparedDisc DecodeForChd(JobContext c, PreparedDisc disc, string workFolder, string discLabel)
    {
        var ext = Path.GetExtension(disc.InputPath).ToLowerInvariant();
        if (ext is ".iso" or ".gcm" || NintendoDisc.IsPlainDisc(disc.InputPath))
            return disc;
        var info = c.Rvz.Probe(disc.InputPath, c.Token);
        if (!info.CanConvert)
            throw new DolphinRvzException(DrvzStatus.Unsupported,
                $"{info.Container} images can't be converted: the container doesn't record the disc's exact size.");
        var iso = Path.Combine(workFolder, disc.OutputName + ".iso");
        c.Log.Info($"Decoding {Path.GetFileName(disc.InputPath)} to a temporary ISO...");
        c.TrackRvz($"Decoding {info.Container}{discLabel}");
        c.Rvz.Convert(disc.InputPath, iso, new ConvertSettings { Format = RvzFormat.Iso, Overwrite = true }, c.Token);
        return disc with { InputPath = iso, Note = $"{disc.Note} → iso" };
    }

    /// <summary>Extract tab: RVZ, WIA or GCZ → ISO.</summary>
    public static JobOutcome Extract(JobContext c, string path)
    {
        var name = Path.GetFileName(path);
        var folder = OutputPaths.FolderFor(c.Settings, path);
        var output = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".iso");
        if (File.Exists(output) && !c.Settings.Overwrite)
        {
            c.Log.Warn($"{name}: {Path.GetFileName(output)} already exists; skipped (turn on Overwrite to replace it).");
            return JobOutcome.Skipped("Output already exists");
        }
        var sw = Stopwatch.StartNew();
        c.TrackRvz("Decoding to ISO");
        c.Rvz.Convert(path, output, new ConvertSettings { Format = RvzFormat.Iso, Overwrite = c.Settings.Overwrite }, c.Token);
        var stats = new OperationStats(sw.Elapsed, new FileInfo(path).Length, new FileInfo(output).Length);
        c.Log.Success($"{name}: wrote {Path.GetFileName(output)} ({ChdJobs.Size(stats.OutputBytes)}) in {stats.Elapsed:mm\\:ss\\.f}");
        return new JobOutcome(JobStatus.Succeeded, Path.GetFileName(output), stats);
    }

    private static string Platform(DiscInfo info) => info.Platform switch
    {
        DiscPlatform.Wii => "Wii",
        DiscPlatform.Triforce => "Triforce",
        _ => "GameCube",
    };
}
