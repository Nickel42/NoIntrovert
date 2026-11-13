using System.Diagnostics;
using LibWiiGameCubeKit;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Jobs;

/// <summary>What to make of GameCube and Wii discs on the Convert tab.</summary>
public enum NintendoTarget { Rvz, Chd }

/// <summary>
/// GameCube/Wii work through lib-wii-gamecube-kit (Dolphin's own disc code): disc →
/// RVZ on the Convert tab, a container → ISO before making a CHD, and
/// RVZ/WIA/GCZ → ISO on the Extract tab.
/// </summary>
public static class RvzJobs
{
    public enum DiscResult { Created, Skipped, VerifyFailed }

    public static ConvertSettings SettingsFor(AppSettings s, RvzFormat format = RvzFormat.Rvz) => new()
    {
        Format = format,
        Compression = s.Nintendo.Compression,
        CompressionLevel = s.Nintendo.Compression is RvzCompression.None or RvzCompression.Purge ? 0 : s.Nintendo.CompressionLevel,
        BlockSize = format == RvzFormat.Iso ? 0 : s.Nintendo.BlockSize,
        Scrub = format == RvzFormat.Rvz && s.Nintendo.Scrub,
        Overwrite = s.Output.Overwrite,
    };

    /// <summary>One prepared GameCube/Wii disc → .rvz in <paramref name="outputFolder"/>, then optionally verified.</summary>
    /// <param name="source">The job's input. When the disc is that file itself (not one unpacked from an archive), its
    /// hashes come from the hash cache, where R11 has usually just put them, instead of decoding it again.</param>
    internal static (DiscResult Result, string Output, OperationStats? Stats) ConvertDisc(
        JobContext c, PreparedDisc disc, string outputFolder, string discLabel, bool verify, double barStart = 0, double barSpan = 1,
        string? source = null)
    {
        var s = c.Settings;
        var log = c.Log;
        var rvz = c.Rvz;
        var wanted = Path.Combine(outputFolder, disc.OutputName + ".rvz");
        var output = wanted;

        var info = rvz.Probe(disc.InputPath, c.Token);
        log.Debug($"  {Platform(info)} {info.GameId} \"{info.InternalName}\" in {info.Container}");
        if (info.Container == DiscContainer.Rvz)
        {
            log.Warn($"  {Path.GetFileName(disc.InputPath)} is already RVZ; skipped.");
            return (DiscResult.Skipped, output, null);
        }
        if (!info.CanConvert)
            throw new WiiGameCubeKitException(WiiGameCubeKitStatus.Unsupported,
                $"{info.Container} images can't be converted: the container doesn't record the disc's exact size. Convert it to ISO with the tool that made it first.");
        output = OutputPaths.Available(wanted, s.Output.Overwrite);
        if (output != wanted) log.Info(OutputPaths.Renamed(wanted, output));
        if (info.IsNKit)
            log.Warn($"  {Path.GetFileName(disc.InputPath)} is an NKit image: the RVZ will be NKit too, not a Redump-identical dump.");

        var settings = SettingsFor(s);
        // The verify's source hash, when nothing has it yet: read beside the compression rather than after it.
        using var beside = verify && !settings.Scrub && source is not null && PathNames.Same(source, disc.InputPath)
                           && !BackgroundHash.IsRunning(source) && KnownHashes.Cached(source) is null
            ? HashBeside(c, source, md5: true)
            : null;
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
        var original = SourceHash(c, disc, source, discLabel);
        if (!string.Equals(original.Sha1, made.Sha1, StringComparison.OrdinalIgnoreCase) || original.Size != (long)made.Size)
        {
            log.Error($"  verification of {Path.GetFileName(output)} failed: source SHA-1 {original.Sha1}, RVZ {made.Sha1}");
            return (DiscResult.VerifyFailed, output, stats);
        }
        log.Debug($"  verified, SHA-1 {made.Sha1} (CRC32 {made.Crc32})");
        return (DiscResult.Created, output, stats);
    }

    /// <summary>
    /// The decoded source disc's SHA-1 and size, for the verify: from the hash cache when the disc is the job's own,
    /// unchanged input (R11 or Verify &amp; match hashed it already), else by decoding it, which then fills the cache.
    /// A disc unpacked from an archive is a temp file, so it's decoded and not cached.
    /// </summary>
    internal static (string Sha1, long Size) SourceHash(JobContext c, PreparedDisc disc, string? source, string discLabel = "")
    {
        (string, long) Decode()
        {
            c.TrackRvz($"Verifying{discLabel}: reading the source");
            var h = c.Rvz.Hash(disc.InputPath, c.Token);
            return (h.Sha1, (long)h.Size);
        }
        if (source is null || !PathNames.Same(source, disc.InputPath)) return Decode();

        BackgroundHash.WaitFor(source, c.Token); // hashed beside the compression: it lands in the cache
        if (KnownHashes.Single(KnownHashes.Cached(source)) is { } known)
        {
            c.Log.Debug($"  source SHA-1 from the hash cache: {known.Sha1}");
            return (known.Sha1!, known.Size);
        }
        // Cached in the form Verify & match gives a GameCube/Wii disc, so either can use the other's entry.
        if (!Dats.VerifyJobs.IsNintendoDisc(source)) return Decode();
        var files = Dats.HashCache.Default.GetOrAdd(source, () =>
        {
            c.TrackRvz($"Verifying{discLabel}: reading the source");
            return Dats.VerifyJobs.DiscHashes(c.Rvz, source, c.Token, md5: true);
        });
        var f = KnownHashes.Single(files);
        return f is null ? Decode() : (f.Sha1!, f.Size);
    }

    /// <summary>
    /// Starts hashing the GameCube/Wii disc file <paramref name="source"/> into the hash cache on another thread, with
    /// its own lib-wii-gamecube-kit context (the job's is busy compressing), while the compression reads the same file;
    /// in the form Verify &amp; match keeps (R11), or with MD5 for the verify. Null when it isn't such a disc, or when
    /// it's on a spinning drive and is better read after the compression.
    /// </summary>
    internal static BackgroundHash<IReadOnlyList<Dats.HashedFile>>? HashBeside(JobContext c, string source, bool md5)
    {
        if (!(md5 ? Dats.VerifyJobs.IsNintendoDisc(source) : Dats.VerifyJobs.HashesAsNintendoDisc(source))) return null;
        return BackgroundHash.Start(source, token => Dats.HashCache.Default.GetOrAdd(source, () =>
        {
            using var kit = new WiiGameCubeKit(1);
            return Dats.VerifyJobs.DiscHashes(kit, source, token, md5);
        }), c.Token);
    }

    /// <summary>
    /// A disc in one of Dolphin's containers (GCZ, WIA, RVZ, TGC) decoded to a plain
    /// ISO in the work folder, for lib-chd-kit to make a CHD from. Plain images pass through.
    /// </summary>
    internal static PreparedDisc DecodeForChd(JobContext c, PreparedDisc disc, string workFolder, string discLabel)
    {
        var ext = Path.GetExtension(disc.InputPath).ToLowerInvariant();
        if (ext is ".iso" or ".gcm" || NintendoDisc.IsPlainDisc(disc.InputPath))
            return disc;
        var info = c.Rvz.Probe(disc.InputPath, c.Token);
        if (!info.CanConvert)
            throw new WiiGameCubeKitException(WiiGameCubeKitStatus.Unsupported,
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
        var wanted = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".iso");
        var output = OutputPaths.Available(wanted, c.Settings.Output.Overwrite);
        if (output != wanted) c.Log.Info(OutputPaths.Renamed(wanted, output));
        var sw = Stopwatch.StartNew();
        c.TrackRvz("Decoding to ISO");
        c.Rvz.Convert(path, output, new ConvertSettings { Format = RvzFormat.Iso, Overwrite = c.Settings.Output.Overwrite }, c.Token);
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
