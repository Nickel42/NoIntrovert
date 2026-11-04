using System.Diagnostics;
using LibChdKit;
using NoIntrovert.Core.Cd;

namespace NoIntrovert.Core;

public enum ExtractFormat { Auto, CueBin, Gdi, Iso, Raw }

/// <summary>Timing and size figures for one lib-chd-kit operation.</summary>
public sealed record OperationStats(TimeSpan Elapsed, long InputBytes, long OutputBytes)
{
    public double MegabytesPerSecond => Elapsed.TotalSeconds > 0 ? InputBytes / 1048576.0 / Elapsed.TotalSeconds : 0;
    public double Ratio => InputBytes > 0 ? (double)OutputBytes / InputBytes : 0;
}

/// <summary>
/// The app's use of lib-chd-kit: create, verify, extract and inspect CHDs.
/// Every call blocks and runs on one <see cref="Chdman"/> context, so give
/// each parallel worker its own context.
/// </summary>
internal static class ChdService
{
    public static OperationStats Create(Chdman chdman, PreparedDisc disc, string outputPath, ChdOutputSettings settings, CancellationToken token)
    {
        var inputBytes = InputSize(disc.InputPath);
        var sw = Stopwatch.StartNew();
        switch (disc.Kind)
        {
            case DiscKind.Cd:
                chdman.CreateCd(disc.InputPath, outputPath, settings, token);
                break;
            case DiscKind.Dvd:
                chdman.CreateDvd(disc.InputPath, outputPath, settings, cancellationToken: token);
                break;
            case DiscKind.HardDisk:
                chdman.CreateHardDisk(outputPath, new ChdHardDiskOptions { Input = disc.InputPath }, settings, token);
                break;
        }
        sw.Stop();
        return new OperationStats(sw.Elapsed, inputBytes, new FileInfo(outputPath).Length);
    }

    public static (ChdVerifyResult Result, OperationStats Stats) Verify(Chdman chdman, string chdPath, bool fix, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var result = chdman.Verify(chdPath, fix, cancellationToken: token);
        sw.Stop();
        var info = chdman.Info(chdPath, cancellationToken: token);
        return (result, new OperationStats(sw.Elapsed, (long)info.LogicalBytes, new FileInfo(chdPath).Length));
    }

    public static bool IsCdLike(ChdInfo info) => info.MediaType is "cd" or "gd";

    /// <summary>Picks the output format for Auto: cue/bin for CDs, gdi for GD-ROMs, iso for DVDs, raw for hard disks.</summary>
    public static ExtractFormat ResolveFormat(ChdInfo info, ExtractFormat requested) => (requested, info.MediaType) switch
    {
        (ExtractFormat.Auto, "gd") => ExtractFormat.Gdi,
        (ExtractFormat.Auto, "cd") => ExtractFormat.CueBin,
        (ExtractFormat.Auto, "dvd") => ExtractFormat.Iso,
        (ExtractFormat.Auto, _) => ExtractFormat.Raw,
        _ => requested,
    };

    /// <summary>Extracts a CHD and returns the files written, named <paramref name="outputName"/> (by default the CHD's own name).</summary>
    public static (List<string> Files, OperationStats Stats) Extract(Chdman chdman, string chdPath, string outputDirectory,
        ExtractFormat requested, bool splitBin, bool overwrite, ILog log, CancellationToken token, string? outputName = null)
    {
        var info = chdman.Info(chdPath, cancellationToken: token);
        var format = ResolveFormat(info, requested);
        var name = outputName ?? Path.GetFileNameWithoutExtension(chdPath);
        var sw = Stopwatch.StartNew();
        var files = new List<string>();

        switch (format)
        {
            case ExtractFormat.CueBin or ExtractFormat.Gdi:
            {
                if (!IsCdLike(info))
                    throw new InvalidOperationException($"This CHD is a {Describe(info.MediaType)}, not a CD, so it can't be written as {(format == ExtractFormat.Gdi ? "gdi" : "cue/bin")}.");
                var descriptor = Path.Combine(outputDirectory, name + (format == ExtractFormat.Gdi ? ".gdi" : ".cue"));
                // A .gdi always gets one file per track, so split-bin only matters for cue output.
                chdman.ExtractCd(chdPath, descriptor, outputBin: null, splitBin: splitBin && format == ExtractFormat.CueBin,
                    force: overwrite, cancellationToken: token);
                files.Add(descriptor);
                var written = format == ExtractFormat.Gdi ? Formats.DiscFiles.GdiReferences(descriptor) : Formats.DiscFiles.CueReferences(descriptor);
                files.AddRange(written.Select(n => Path.Combine(outputDirectory, n)).Distinct(StringComparer.OrdinalIgnoreCase));
                break;
            }

            case ExtractFormat.Iso when IsCdLike(info):
            {
                // CDs have no ISO form in chdman; extract a single bin and keep the 2048-byte user data.
                var temp = Path.Combine(outputDirectory, $".noi-{Guid.NewGuid():N}");
                Directory.CreateDirectory(temp);
                try
                {
                    var cue = Path.Combine(temp, name + ".cue");
                    chdman.ExtractCd(chdPath, cue, outputBin: null, splitBin: false, force: true, cancellationToken: token);
                    var sheet = CueSheet.Load(cue);
                    var tracks = sheet.Tracks.ToList();
                    if (tracks.Count != 1 || tracks[0].IsAudio)
                        throw new InvalidOperationException($"This CD has {tracks.Count} tracks; only a single data track can be written as an ISO. Use cue/bin instead.");
                    var iso = Path.Combine(outputDirectory, name + ".iso");
                    if (File.Exists(iso) && !overwrite)
                        throw new IOException($"{Path.GetFileName(iso)} already exists.");
                    BinToIso(Path.Combine(temp, sheet.Files[0].Name), iso, token);
                    files.Add(iso);
                }
                finally
                {
                    try { Directory.Delete(temp, true); } catch (IOException) { }
                }
                break;
            }

            case ExtractFormat.Iso or ExtractFormat.Raw:
            {
                var ext = format == ExtractFormat.Iso || info.MediaType == "dvd" ? ".iso" : info.MediaType == "hd" ? ".img" : ".raw";
                if (info.MediaType == "av")
                    throw new NotSupportedException("Laserdisc CHDs can only be extracted to .avi, which this app doesn't do.");
                var output = Path.Combine(outputDirectory, name + ext);
                chdman.ExtractRaw(chdPath, output, overwrite, cancellationToken: token);
                files.Add(output);
                break;
            }
        }

        sw.Stop();
        log.Debug($"Extracted {string.Join(", ", files.Select(Path.GetFileName))}");
        return (files, new OperationStats(sw.Elapsed, new FileInfo(chdPath).Length, files.Sum(f => File.Exists(f) ? new FileInfo(f).Length : 0)));
    }

    /// <summary>Writes the 2048-byte user data of each Mode 1 / Mode 2 Form 1 sector of a raw bin.</summary>
    public static void BinToIso(string binPath, string isoPath, CancellationToken token)
    {
        using var src = new BufferedStream(File.OpenRead(binPath), 1 << 20);
        using var dst = new BufferedStream(File.Create(isoPath), 1 << 20);
        var sector = new byte[CdSector.RawSize];
        long n = 0;
        while (src.ReadAtLeast(sector, sector.Length, throwOnEndOfStream: false) == sector.Length)
        {
            var offset = CdSector.UserDataOffset(sector);
            if (offset < 0)
                throw new InvalidOperationException($"Sector {n} is not a 2048-byte data sector (Mode 2 Form 2 or audio), so this disc can't be stored as an ISO without losing data.");
            dst.Write(sector, offset, CdSector.UserSize);
            if ((++n & 0xFFF) == 0) token.ThrowIfCancellationRequested();
        }
    }

    public static string Describe(string mediaType) => mediaType switch
    {
        "cd" => "CD-ROM",
        "gd" => "GD-ROM",
        "dvd" => "DVD",
        "hd" => "hard disk",
        "av" => "laserdisc",
        _ => "raw image",
    };

    /// <summary>Bytes the CHD will be made from: the files a cue/gdi names, or the image itself.</summary>
    public static long InputSize(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var dir = Path.GetDirectoryName(path)!;
            var names = ext switch
            {
                ".cue" => Formats.DiscFiles.CueReferences(path),
                ".gdi" => Formats.DiscFiles.GdiReferences(path),
                ".toc" => Formats.DiscFiles.TocReferences(path),
                _ => null,
            };
            if (names is null) return new FileInfo(path).Length;
            return names.Select(n => Path.GetFullPath(Path.Combine(dir, n))).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            return 0;
        }
    }
}
