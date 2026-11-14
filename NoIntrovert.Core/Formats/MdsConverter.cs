using System.Text;
using NoIntrovert.Core.Cd;

namespace NoIntrovert.Core.Formats;

/// <summary>What an Alcohol 120% image turned into.</summary>
internal sealed record MdsResult(bool IsDvd, string InputPath);

/// <summary>
/// Reads an Alcohol 120% descriptor (.mds) and makes its .mdf usable by
/// chdman: DVD images are plain ISO data and are used as they are; CD images
/// get a cue sheet, and when sectors carry interleaved subchannel data
/// (2448 bytes each) a copy without it is written first.
/// </summary>
/// <remarks>
/// .mds layout (little-endian): "MEDIA DESCRIPTOR" magic; medium type (u16)
/// at 0x12 (0x10 and up are DVDs); session count (u16) at 0x14; offset of the
/// first session block (u32) at 0x50. A 24-byte session block holds the
/// number of track blocks (u8 at +10) and their offset (u32 at +20). Each
/// 80-byte track block holds the mode (low nibble: 9 audio, A Mode 1, B/C/D
/// Mode 2) at +0, point (track number, or A0+ for lead-in entries) at +4,
/// the offset of an extra block (pregap and length, u32 each) at +12,
/// sector size (u16) at +16, start sector (u32) at +36 and start offset in
/// the .mdf (u64) at +40.
/// </remarks>
internal static class MdsConverter
{
    private sealed record MdsTrack(int Number, int Mode, int SectorSize, long StartSector, long StartOffset, long Pregap, long Length);

    public static bool IsMds(string path)
    {
        Span<byte> head = stackalloc byte[16];
        using var fs = File.OpenRead(path);
        return fs.Read(head) == 16 && head.SequenceEqual("MEDIA DESCRIPTOR"u8);
    }

    public static string FindImage(string mdsPath)
    {
        var mdf = Path.ChangeExtension(mdsPath, ".mdf");
        return File.Exists(mdf) ? mdf
            : throw new FileNotFoundException($"The Alcohol image file '{Path.GetFileName(mdf)}' that goes with this .mds was not found.", mdf);
    }

    public static IEnumerable<string> SetFiles(string mdsPath)
    {
        yield return mdsPath;
        var mdf = Path.ChangeExtension(mdsPath, ".mdf");
        if (File.Exists(mdf)) yield return mdf;
    }

    public static MdsResult Prepare(string mdsPath, WorkDirectory work, Action<string>? warn,
        IProgress<double>? progress, CancellationToken token)
    {
        var data = File.ReadAllBytes(mdsPath);
        if (data.Length < 0x58 || !data.AsSpan(0, 16).SequenceEqual("MEDIA DESCRIPTOR"u8))
            throw new InvalidDataException("Not an Alcohol .mds file.");

        var mdf = FindImage(mdsPath);
        var mediumType = BitConverter.ToUInt16(data, 0x12);
        if (mediumType >= 0x10)
            return new MdsResult(true, mdf);

        var sessionCount = BitConverter.ToUInt16(data, 0x14);
        var sessionOffset = (int)BitConverter.ToUInt32(data, 0x50);
        if (sessionCount > 1)
            warn?.Invoke($"{Path.GetFileName(mdsPath)} has {sessionCount} sessions; all tracks are written into one CHD, which chdman does not mark as multi-session.");

        var tracks = new List<MdsTrack>();
        for (var s = 0; s < sessionCount; s++)
        {
            var sb = sessionOffset + s * 24;
            int blockCount = data[sb + 10];
            var trackOffset = (int)BitConverter.ToUInt32(data, sb + 20);
            for (var b = 0; b < blockCount; b++)
            {
                var tb = trackOffset + b * 80;
                int point = data[tb + 4];
                if (point is < 1 or > 99) continue;
                var extraOffset = (int)BitConverter.ToUInt32(data, tb + 12);
                long pregap = 0, length = 0;
                if (extraOffset > 0 && extraOffset + 8 <= data.Length)
                {
                    pregap = BitConverter.ToUInt32(data, extraOffset);
                    length = BitConverter.ToUInt32(data, extraOffset + 4);
                }
                tracks.Add(new MdsTrack(point, data[tb] & 0x0F, BitConverter.ToUInt16(data, tb + 16),
                    BitConverter.ToUInt32(data, tb + 36), (long)BitConverter.ToUInt64(data, tb + 40), pregap, length));
            }
        }
        if (tracks.Count == 0)
            throw new InvalidDataException("The .mds file lists no tracks.");
        tracks.Sort((a, b) => a.Number.CompareTo(b.Number));

        var sectorSize = tracks[0].SectorSize;
        if (tracks.Any(t => t.SectorSize != sectorSize))
            throw new NotSupportedException("This Alcohol image mixes sector sizes between tracks, which is not supported.");

        // Strip interleaved subchannel data (2352 + 96) into a plain 2352-byte image.
        var image = mdf;
        var cueSectorSize = sectorSize;
        if (sectorSize > CdSector.RawSize)
        {
            image = Path.Combine(work.Path, Path.GetFileNameWithoutExtension(mdf) + ".bin");
            StripSubchannel(mdf, image, sectorSize, progress, token);
            cueSectorSize = CdSector.RawSize;
        }

        var cuePath = work.CuePathFor(Path.GetFileNameWithoutExtension(mdsPath) + ".cue", [image]);
        var cue = new CueSheet();
        var file = new CueFile { Name = WorkDirectory.CueReference(cuePath, image) };
        cue.Files.Add(file);
        foreach (var t in tracks)
        {
            var start = t.StartOffset / sectorSize;
            var track = new CueTrack { Number = t.Number, Type = CueType(t.Mode, cueSectorSize, mdf) };
            if (t.Number > 1 && t.Pregap > 0 && start - t.Pregap >= 0)
                track.Indexes.Add(new CueIndex(0, start - t.Pregap));
            track.Indexes.Add(new CueIndex(1, start));
            file.Tracks.Add(track);
        }
        cue.Save(cuePath);
        return new MdsResult(false, cuePath);
    }

    private static string CueType(int mode, int sectorSize, string mdf) => mode switch
    {
        0x9 => "AUDIO",
        0xA => $"MODE1/{sectorSize}",
        0xB or 0xC or 0xD => $"MODE2/{(sectorSize == CdSector.UserSize ? CdSector.Mode2Size : sectorSize)}",
        _ => sectorSize == CdSector.RawSize ? GuessMode(mdf) : $"MODE1/{sectorSize}",
    };

    private static string GuessMode(string mdf)
    {
        var sector = new byte[CdSector.RawSize];
        using var fs = File.OpenRead(mdf);
        return fs.Read(sector) == sector.Length && CdSector.HasSync(sector) && sector[15] == 2 ? "MODE2/2352" : "MODE1/2352";
    }

    private static void StripSubchannel(string input, string output, int sectorSize, IProgress<double>? progress, CancellationToken token)
    {
        using var src = new BufferedStream(File.OpenRead(input), 1 << 20);
        using var dst = new BufferedStream(File.Create(output), 1 << 20);
        var total = src.Length / sectorSize;
        var sector = new byte[sectorSize];
        for (long i = 0; i < total; i++)
        {
            src.ReadExactly(sector);
            dst.Write(sector, 0, CdSector.RawSize);
            if ((i & 0xFFF) == 0)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report((double)i / total);
            }
        }
        progress?.Report(1);
    }

    internal static string DescribeMedium(string mdsPath)
    {
        var data = File.ReadAllBytes(mdsPath);
        return BitConverter.ToUInt16(data, 0x12) >= 0x10 ? "DVD" : "CD";
    }
}
