using NoIntrovert.Core.Cd;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Decodes ECM files (the "Error Code Modeler" format), which shrink raw CD
/// images by dropping the sync, EDC and ECC fields that can be recomputed.
/// The stream is a list of records, each a type (raw bytes, Mode 1, Mode 2
/// Form 1, Mode 2 Form 2) and a count, ended by a count of 0xFFFFFFFF and a
/// 4-byte EDC of everything decoded.
/// </summary>
internal static class EcmDecoder
{
    private static readonly byte[] Magic = "ECM\0"u8.ToArray();

    public static bool IsEcm(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var fs = File.OpenRead(path);
        return fs.Read(head) == 4 && head.SequenceEqual(Magic);
    }

    public static void Decode(string inputPath, string outputPath, IProgress<double>? progress, CancellationToken token)
    {
        using var input = new BufferedStream(File.OpenRead(inputPath), 1 << 20);
        using var output = new BufferedStream(File.Create(outputPath), 1 << 20);
        Decode(input, output, input.Length, progress, token);
    }

    public static void Decode(Stream input, Stream output, long inputLength, IProgress<double>? progress, CancellationToken token)
    {
        Span<byte> magic = stackalloc byte[4];
        ReadExactly(input, magic);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("Not an ECM file (missing 'ECM' header).");

        var sector = new byte[CdSector.RawSize];
        uint checksum = 0;
        long processedRecords = 0;

        while (true)
        {
            var (type, count) = ReadRecordHeader(input);
            if (count == uint.MaxValue) break;
            long remaining = (long)count + 1;

            if (type == 0)
            {
                var buffer = new byte[64 * 1024];
                while (remaining > 0)
                {
                    var chunk = (int)Math.Min(remaining, buffer.Length);
                    ReadExactly(input, buffer.AsSpan(0, chunk));
                    checksum = CdSector.ComputeEdc(buffer.AsSpan(0, chunk), checksum);
                    output.Write(buffer, 0, chunk);
                    remaining -= chunk;
                }
            }
            else
            {
                for (; remaining > 0; remaining--)
                {
                    var written = RebuildSector(input, type, sector);
                    checksum = CdSector.ComputeEdc(written.Span, checksum);
                    output.Write(written.Span);
                    if ((++processedRecords & 0x3FF) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (inputLength > 0) progress?.Report((double)input.Position / inputLength);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            if (inputLength > 0) progress?.Report((double)input.Position / inputLength);
        }

        Span<byte> stored = stackalloc byte[4];
        ReadExactly(input, stored);
        var expected = (uint)(stored[0] | stored[1] << 8 | stored[2] << 16 | stored[3] << 24);
        if (expected != checksum)
            throw new InvalidDataException($"ECM checksum mismatch (file says {expected:X8}, decoded data is {checksum:X8}); the file is damaged.");
        progress?.Report(1);
    }

    private static (int Type, uint Count) ReadRecordHeader(Stream input)
    {
        var c = ReadByte(input);
        var type = c & 3;
        var count = (uint)((c >> 2) & 0x1F);
        var bits = 5;
        while ((c & 0x80) != 0)
        {
            c = ReadByte(input);
            if (bits >= 32) throw new InvalidDataException("ECM record count is corrupt.");
            count |= (uint)(c & 0x7F) << bits;
            bits += 7;
        }
        return (type, count);
    }

    /// <summary>Reads one encoded sector and rebuilds it. Returns the bytes to write.</summary>
    private static ReadOnlyMemory<byte> RebuildSector(Stream input, int type, byte[] sector)
    {
        Array.Clear(sector);
        var s = sector.AsSpan();
        switch (type)
        {
            case 1: // Mode 1: 3 address bytes + 2048 data -> full 2352-byte sector
                CdSector.WriteSync(s);
                ReadExactly(input, s.Slice(12, 3));
                s[15] = 1;
                ReadExactly(input, s.Slice(16, 2048));
                CdSector.WriteEdc(s, 0, 0x810, 0x810);
                CdSector.WriteEcc(s, zeroAddress: false);
                return sector.AsMemory(0, CdSector.RawSize);

            case 2: // Mode 2 Form 1: 4 subheader + 2048 data -> 2336 bytes (no sync/header)
                ReadExactly(input, s.Slice(0x14, 0x804));
                s.Slice(0x14, 4).CopyTo(s.Slice(0x10, 4));
                CdSector.WriteEdc(s, 0x10, 0x808, 0x818);
                CdSector.WriteEcc(s, zeroAddress: true);
                return sector.AsMemory(0x10, CdSector.Mode2Size);

            case 3: // Mode 2 Form 2: 4 subheader + 2324 data -> 2336 bytes
                ReadExactly(input, s.Slice(0x14, 0x918));
                s.Slice(0x14, 4).CopyTo(s.Slice(0x10, 4));
                CdSector.WriteEdc(s, 0x10, 0x91C, 0x92C);
                return sector.AsMemory(0x10, CdSector.Mode2Size);

            default:
                throw new InvalidDataException($"Unknown ECM record type {type}.");
        }
    }

    private static int ReadByte(Stream s)
    {
        var b = s.ReadByte();
        if (b < 0) throw new EndOfStreamException("ECM file ends early.");
        return b;
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        try { s.ReadExactly(buffer); }
        catch (EndOfStreamException) { throw new EndOfStreamException("ECM file ends early."); }
    }
}
