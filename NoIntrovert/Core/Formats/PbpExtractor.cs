using System.IO.Compression;
using System.Text;
using NoIntrovert.Core.Cd;

namespace NoIntrovert.Core.Formats;

/// <summary>One disc rebuilt from a PS1 EBOOT.PBP.</summary>
internal sealed record PbpDisc(int Number, string DiscId, string CuePath, string BinPath);

/// <summary>
/// Rebuilds the cue/bin of each PlayStation disc stored in a PSP-format
/// EBOOT.PBP ("PS1 classics" made with popstation-style tools).
/// </summary>
/// <remarks>
/// The PBP header lists eight section offsets; the last is DATA.PSAR. That
/// starts with "PSISOIMG0000" for a single disc or "PSTITLEIMG000000" for a
/// multi-disc set, which has a table of disc offsets at +0x200. Each disc
/// block holds the disc ID at +0x400, a TOC of 10-byte Q-subchannel style
/// entries at +0x800 (BCD; points A0/A1/A2 give first track, last track and
/// lead-out), a block index at +0x4000 (32-byte entries: offset and size),
/// and the image itself from +0x100000 in raw-deflated 16-sector blocks.
/// Encrypted PSN downloads are not supported.
/// </remarks>
internal static class PbpExtractor
{
    private const int BlockBytes = 16 * CdSector.RawSize; // 0x9300
    private const int TocOffset = 0x800;
    private const int IndexOffset = 0x4000;
    private const int DataOffset = 0x100000;

    public static bool IsPbp(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var fs = File.OpenRead(path);
        return fs.Read(head) == 4 && head.SequenceEqual("\0PBP"u8);
    }

    public static IReadOnlyList<PbpDisc> Extract(string pbpPath, string outputDirectory, string baseName,
        IProgress<double>? progress, CancellationToken token)
    {
        using var fs = File.OpenRead(pbpPath);
        using var r = new BinaryReader(fs);

        if (!r.ReadBytes(4).AsSpan().SequenceEqual("\0PBP"u8))
            throw new InvalidDataException("Not a PBP file.");
        r.ReadUInt32(); // version
        var offsets = new uint[8];
        for (var i = 0; i < 8; i++) offsets[i] = r.ReadUInt32();
        long psar = offsets[7];
        if (psar <= 0 || psar >= fs.Length)
            throw new InvalidDataException("PBP has no DATA.PSAR section; it is probably a PSP game, not a PS1 disc.");

        fs.Position = psar;
        var magic = Encoding.ASCII.GetString(r.ReadBytes(16));
        var discStarts = new List<long>();
        if (magic.StartsWith("PSISOIMG0000", StringComparison.Ordinal))
        {
            discStarts.Add(psar);
        }
        else if (magic.StartsWith("PSTITLEIMG000000", StringComparison.Ordinal))
        {
            fs.Position = psar + 0x200;
            for (var i = 0; i < 5; i++)
            {
                var o = r.ReadUInt32();
                if (o != 0) discStarts.Add(psar + o);
            }
        }
        else if (magic.StartsWith("NPUMDIMG", StringComparison.Ordinal) || magic.StartsWith("NPUMD", StringComparison.Ordinal))
        {
            throw new NotSupportedException("This PBP holds an encrypted PSN/UMD image, which can't be decoded here.");
        }
        else
        {
            throw new InvalidDataException("DATA.PSAR is not a PS1 disc image (no PSISOIMG/PSTITLEIMG header). It may be encrypted.");
        }

        var discs = new List<PbpDisc>();
        for (var d = 0; d < discStarts.Count; d++)
        {
            token.ThrowIfCancellationRequested();
            var name = discStarts.Count == 1 ? baseName : $"{baseName} (Disc {d + 1})";
            var weight = 1.0 / discStarts.Count;
            var sub = progress is null ? null : new Progress<double>(f => progress.Report((d + f) * weight));
            discs.Add(ExtractDisc(fs, r, discStarts[d], d + 1, outputDirectory, name, sub, token));
        }
        progress?.Report(1);
        return discs;
    }

    private sealed record TocTrack(int Number, bool IsAudio, long StartLba);

    private static PbpDisc ExtractDisc(FileStream fs, BinaryReader r, long start, int number, string outputDirectory,
        string name, IProgress<double>? progress, CancellationToken token)
    {
        fs.Position = start;
        if (!Encoding.ASCII.GetString(r.ReadBytes(12)).Equals("PSISOIMG0000", StringComparison.Ordinal))
            throw new InvalidDataException($"Disc {number} in this PBP has no PSISOIMG header.");

        fs.Position = start + 0x400;
        var discId = Encoding.ASCII.GetString(r.ReadBytes(16)).TrimEnd('\0', ' ').TrimStart('_').Replace('_', '-');

        var (tracks, leadOutLba) = ReadToc(fs, r, start + TocOffset);
        if (tracks.Count == 0 || leadOutLba <= 0)
            throw new InvalidDataException($"Disc {number} in this PBP has no usable table of contents.");

        var binPath = Path.Combine(outputDirectory, name + ".bin");
        var cuePath = Path.Combine(outputDirectory, name + ".cue");
        var totalBytes = leadOutLba * CdSector.RawSize;

        using (var output = new BufferedStream(File.Create(binPath), 1 << 20))
        {
            var compressed = new byte[BlockBytes + 1024];
            var block = new byte[BlockBytes];
            long written = 0;
            var entry = 0;
            while (written < totalBytes)
            {
                if ((entry & 0x3F) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report((double)written / totalBytes);
                }

                fs.Position = start + IndexOffset + entry * 32L;
                var offset = r.ReadUInt32();
                var length = r.ReadUInt16();
                if (length == 0 || length > compressed.Length)
                    throw new InvalidDataException($"Disc {number}: block index ends early or is corrupt (block {entry}).");

                fs.Position = start + DataOffset + offset;
                fs.ReadExactly(compressed, 0, length);

                var outLength = (int)Math.Min(BlockBytes, totalBytes - written);
                if (length == BlockBytes)
                {
                    output.Write(compressed, 0, outLength);
                }
                else
                {
                    using var deflate = new DeflateStream(new MemoryStream(compressed, 0, length), CompressionMode.Decompress);
                    int got;
                    try { got = CsoDecoder.ReadFully(deflate, block); }
                    catch (InvalidDataException) { throw new InvalidDataException($"Disc {number}: block {entry} won't decompress; the PBP may be encrypted or damaged."); }
                    if (got < outLength)
                        throw new InvalidDataException($"Disc {number}: block {entry} decompressed short.");
                    output.Write(block, 0, outLength);
                }
                written += outLength;
                entry++;
            }
        }

        var cue = new CueSheet();
        var file = new CueFile { Name = Path.GetFileName(binPath) };
        cue.Files.Add(file);
        for (var i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var track = new CueTrack { Number = t.Number, Type = t.IsAudio ? "AUDIO" : "MODE2/2352" };
            // Audio tracks after the first get the usual 2-second pregap when it fits.
            if (i > 0 && t.StartLba - CdSector.LeadInFrames >= tracks[i - 1].StartLba)
                track.Indexes.Add(new CueIndex(0, t.StartLba - CdSector.LeadInFrames));
            track.Indexes.Add(new CueIndex(1, t.StartLba));
            file.Tracks.Add(track);
        }
        cue.Save(cuePath);
        progress?.Report(1);
        return new PbpDisc(number, discId, cuePath, binPath);
    }

    private static (List<TocTrack> Tracks, long LeadOutLba) ReadToc(FileStream fs, BinaryReader r, long tocStart)
    {
        var tracks = new List<TocTrack>();
        long leadOut = 0;
        fs.Position = tocStart;
        for (var i = 0; i < 102; i++)
        {
            var e = r.ReadBytes(10);
            var control = e[0];
            var point = e[2];
            if (control == 0 && point == 0) break;
            var lba = CdSector.BcdMsfToFrames(e[7], e[8], e[9]) - CdSector.LeadInFrames;
            switch (point)
            {
                case 0xA0 or 0xA1: break;
                case 0xA2: leadOut = lba; break;
                default:
                    tracks.Add(new TocTrack(CdSector.FromBcd(point), (control & 0x40) == 0, Math.Max(0, lba)));
                    break;
            }
        }
        tracks.Sort((a, b) => a.Number.CompareTo(b.Number));
        return (tracks, leadOut);
    }
}
