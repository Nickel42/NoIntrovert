using System.IO.Compression;
using SharpCompress.Compressors.BZip2;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Decodes UltraISO compressed images (.isz) back to the image they hold.
/// </summary>
/// <remarks>
/// An ISZ file has a packed 48-byte header ("IsZ!", sector size, sector count,
/// block size, pointer width and the offsets of the block table and data),
/// a block table of fixed-width little-endian entries (the top two bits are
/// the block's storage: zeros, raw, zlib or bzip2; the rest is its stored
/// size), then the blocks back to back. The block table is obfuscated with a
/// 4-byte key. Password-protected and multi-part (.i01, .i02...) images are
/// not supported.
/// </remarks>
internal static class IszDecoder
{
    private static readonly byte[] Key = [0xB6, 0x8C, 0xA5, 0xDE];

    public static bool IsIsz(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var fs = File.OpenRead(path);
        return fs.Read(head) == 4 && head.SequenceEqual("IsZ!"u8);
    }

    internal sealed record Header(
        int HeaderSize, int SectorSize, long TotalSectors, bool HasPassword, long SegmentSize,
        int BlockCount, int BlockSize, int PointerLength, int SegmentNumber,
        long PointerOffset, long SegmentOffset, long DataOffset)
    {
        public long ImageBytes => TotalSectors * SectorSize;
    }

    internal static Header ReadHeader(BinaryReader r)
    {
        if (!r.ReadBytes(4).AsSpan().SequenceEqual("IsZ!"u8))
            throw new InvalidDataException("Not an ISZ file (bad magic).");
        int headerSize = r.ReadByte();
        r.ReadByte();      // version
        r.ReadUInt32();    // volume serial
        int sectorSize = r.ReadUInt16();
        long totalSectors = r.ReadUInt32();
        var hasPassword = r.ReadByte() != 0;
        var segmentSize = r.ReadInt64();
        var blockCount = (int)r.ReadUInt32();
        var blockSize = (int)r.ReadUInt32();
        int pointerLength = r.ReadByte();
        int segmentNumber = r.ReadSByte();
        long pointerOffset = r.ReadUInt32();
        long segmentOffset = r.ReadUInt32();
        long dataOffset = r.ReadUInt32();
        return new Header(headerSize, sectorSize, totalSectors, hasPassword, segmentSize, blockCount, blockSize,
            pointerLength, segmentNumber, pointerOffset, segmentOffset, dataOffset);
    }

    /// <summary>Encodes or decodes the block table in place (the operation is its own inverse).</summary>
    internal static void Scramble(Span<byte> table)
    {
        for (var i = 0; i < table.Length; i++)
            table[i] = (byte)~(table[i] ^ Key[i & 3]);
    }

    public static void Decode(string inputPath, string outputPath, IProgress<double>? progress, CancellationToken token)
    {
        using var input = File.OpenRead(inputPath);
        using var reader = new BinaryReader(input);
        var h = ReadHeader(reader);

        if (h.HasPassword)
            throw new NotSupportedException("This ISZ image is password protected; decrypt it in UltraISO first.");
        if (h.SegmentOffset != 0 && h.SegmentSize != 0 && h.SegmentSize < input.Length)
            throw new NotSupportedException("Multi-part ISZ images (.i01, .i02, ...) are not supported; merge them in UltraISO first.");
        if (h.SectorSize <= 0 || h.BlockSize <= 0 || h.PointerLength is < 2 or > 8)
            throw new InvalidDataException("ISZ header is invalid.");

        using var output = new BufferedStream(File.Create(outputPath), 1 << 20);

        // An image without a block table is stored raw after the header.
        if (h.PointerOffset == 0)
        {
            input.Position = h.DataOffset;
            CopyBytes(input, output, h.ImageBytes, progress, token);
            return;
        }

        var blocks = ReadBlockTable(input, h);
        var compressed = new byte[h.BlockSize * 2 + 1024];
        var plain = new byte[h.BlockSize];
        long written = 0;
        input.Position = h.DataOffset;

        for (var i = 0; i < blocks.Length; i++)
        {
            if ((i & 0x3F) == 0)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report((double)i / blocks.Length);
            }

            var (kind, size) = blocks[i];
            var outLength = (int)Math.Min(h.BlockSize, h.ImageBytes - written);
            if (outLength <= 0) break;

            switch (kind)
            {
                case 0: // all zeros, nothing stored
                    Array.Clear(plain, 0, outLength);
                    output.Write(plain, 0, outLength);
                    break;
                case 1: // stored raw
                    input.ReadExactly(compressed, 0, size);
                    output.Write(compressed, 0, Math.Min(size, outLength));
                    break;
                case 2: // zlib
                {
                    input.ReadExactly(compressed, 0, size);
                    using var z = new ZLibStream(new MemoryStream(compressed, 0, size), CompressionMode.Decompress);
                    if (CsoDecoder.ReadFully(z, plain) < outLength)
                        throw new InvalidDataException($"ISZ block {i} did not decompress fully.");
                    output.Write(plain, 0, outLength);
                    break;
                }
                case 3: // bzip2; ISZ blanks out the "BZh" signature, so put it back
                {
                    input.ReadExactly(compressed, 0, size);
                    if (size >= 3) { compressed[0] = (byte)'B'; compressed[1] = (byte)'Z'; compressed[2] = (byte)'h'; }
                    using var bz = BZip2Stream.Create(new MemoryStream(compressed, 0, size), SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: false);
                    if (CsoDecoder.ReadFully(bz, plain) < outLength)
                        throw new InvalidDataException($"ISZ block {i} did not decompress fully.");
                    output.Write(plain, 0, outLength);
                    break;
                }
            }
            written += outLength;
        }

        if (written < h.ImageBytes)
            throw new InvalidDataException("ISZ image ends early; the file is damaged or a part is missing.");
        progress?.Report(1);
    }

    /// <summary>
    /// Reads and unscrambles the block table. The table is checked against
    /// the file: its stored sizes must add up to the data that follows it.
    /// </summary>
    private static (int Kind, int Size)[] ReadBlockTable(FileStream input, Header h)
    {
        var table = new byte[h.BlockCount * h.PointerLength];
        input.Position = h.PointerOffset;
        input.ReadExactly(table);
        Scramble(table);

        var sizeBits = h.PointerLength * 8 - 2;
        var sizeMask = (1UL << sizeBits) - 1;
        var blocks = new (int, int)[h.BlockCount];
        long stored = 0;
        for (var i = 0; i < h.BlockCount; i++)
        {
            ulong value = 0;
            for (var b = 0; b < h.PointerLength; b++)
                value |= (ulong)table[i * h.PointerLength + b] << (8 * b);
            var kind = (int)(value >> sizeBits);
            var size = (int)(value & sizeMask);
            blocks[i] = (kind, size);
            if (kind != 0) stored += size;
        }

        if (h.DataOffset + stored > input.Length)
            throw new InvalidDataException("ISZ block table does not match the file size; the file is damaged or truncated.");
        return blocks;
    }

    private static void CopyBytes(Stream input, Stream output, long count, IProgress<double>? progress, CancellationToken token)
    {
        var buffer = new byte[1 << 20];
        long done = 0;
        while (done < count)
        {
            token.ThrowIfCancellationRequested();
            var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count - done));
            if (n <= 0) throw new EndOfStreamException("ISZ image ends early.");
            output.Write(buffer, 0, n);
            done += n;
            progress?.Report((double)done / count);
        }
    }
}
