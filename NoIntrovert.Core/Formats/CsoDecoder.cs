using System.IO.Compression;
using K4os.Compression.LZ4;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Decodes CSO ("CISO", raw deflate blocks) and ZSO ("ZISO", LZ4 blocks)
/// compressed ISOs, used mostly for PSP and PS2 games, back to a plain ISO.
/// </summary>
/// <remarks>
/// Layout: a 24-byte header (magic, header size, total bytes, block size,
/// version, index shift), then one 32-bit index entry per block plus one end
/// entry. An entry's low 31 bits, shifted left by the index shift, give the
/// block's file offset; the top bit marks a block stored uncompressed.
/// </remarks>
internal static class CsoDecoder
{
    public static bool IsCso(string path) => ReadMagic(path) is "CISO" or "ZISO";

    private static string ReadMagic(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var fs = File.OpenRead(path);
        return fs.Read(head) == 4 ? System.Text.Encoding.ASCII.GetString(head) : "";
    }

    /// <param name="threads">Blocks decoded at once; 0 for every processor.</param>
    public static void Decode(string inputPath, string outputPath, IProgress<double>? progress, CancellationToken token,
        int threads = 0, int batchBytes = BlockBatches.DefaultBatchBytes)
    {
        using var output = new BufferedStream(File.Create(outputPath), 1 << 20);
        Decode(inputPath, output, progress, token, threads, batchBytes);
    }

    /// <summary>Decodes into <paramref name="output"/> (a file, or a hash when checking a new CSO).</summary>
    public static void Decode(string inputPath, Stream output, IProgress<double>? progress, CancellationToken token,
        int threads = 0, int batchBytes = BlockBatches.DefaultBatchBytes)
    {
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);

        var header = new byte[24];
        input.ReadExactly(header);
        var magic = System.Text.Encoding.ASCII.GetString(header, 0, 4);
        var isLz4 = magic switch
        {
            "CISO" => false,
            "ZISO" => true,
            _ => throw new InvalidDataException("Not a CSO/ZSO file (bad magic)."),
        };
        var totalBytes = BitConverter.ToInt64(header, 8);
        var blockSize = BitConverter.ToInt32(header, 16);
        var version = header[20];
        var shift = header[21];
        if (blockSize <= 0 || blockSize > 16 * 1024 * 1024 || totalBytes <= 0)
            throw new InvalidDataException("CSO header has an invalid block size or length.");
        if (version > 2)
            throw new InvalidDataException($"CSO version {version} is not supported.");

        var blockCount = (int)((totalBytes + blockSize - 1) / blockSize);
        var indexBytes = new byte[(blockCount + 1) * 4];
        input.ReadExactly(indexBytes);
        var index = new uint[blockCount + 1];
        Buffer.BlockCopy(indexBytes, 0, index, 0, indexBytes.Length);

        var maxLength = blockSize * 2 + 64;
        BlockBatches.Block Describe(int i)
        {
            var start = (long)(index[i] & 0x7FFFFFFF) << shift;
            var end = (long)(index[i + 1] & 0x7FFFFFFF) << shift;
            var length = end - start;
            var outLength = (int)Math.Min(blockSize, totalBytes - (long)i * blockSize);
            if (length < 0 || length > maxLength)
                throw new InvalidDataException($"CSO block {i} has an invalid size; the file is damaged.");
            return new BlockBatches.Block(start, (int)length, outLength);
        }

        void DecodeBlock(int i, byte[] compressed, int offset, BlockBatches.Block b, Span<byte> slot)
        {
            // Version 2 files mark raw blocks by size instead of the top bit. In version 1 the size can
            // include alignment padding (an index shift), so only the top bit counts.
            if ((index[i] & 0x80000000) != 0 || (version == 2 && b.Length >= blockSize))
            {
                var copy = Math.Min(b.Length, b.OutLength);
                compressed.AsSpan(offset, copy).CopyTo(slot);
                slot[copy..b.OutLength].Clear(); // a short stored block (a damaged file): zeros, not stale bytes
            }
            else if (isLz4)
            {
                // An LZ4 block must end exactly where its data does. With an index shift, zero padding (up to
                // 2^shift - 1 bytes) follows it, so the padding is trimmed until the block decodes in full: a
                // longer length includes bytes LZ4 can't end on, so the first full decode is the block's own length.
                var data = compressed.AsSpan(offset, b.Length);
                var target = slot[..b.OutLength];
                var n = LZ4Codec.Decode(data, target);
                for (var trim = 1; n != b.OutLength && shift > 0 && trim < 1 << shift && trim < data.Length && data[^trim] == 0; trim++)
                    n = LZ4Codec.Decode(data[..^trim], target);
                if (n != b.OutLength) throw new InvalidDataException($"ZSO block {i} did not decompress fully.");
            }
            else
            {
                using var deflate = new DeflateStream(new MemoryStream(compressed, offset, b.Length, writable: false), CompressionMode.Decompress);
                var n = BlockBatches.ReadFully(deflate, slot);
                if (n < b.OutLength) throw new InvalidDataException($"CSO block {i} did not decompress fully.");
            }
        }

        BlockBatches.Decode(input, output, blockCount, Describe, DecodeBlock, maxLength, blockSize, threads,
            progress is null ? null : done => progress.Report((double)done / blockCount), token, batchBytes);
    }

    internal static int ReadFully(Stream s, byte[] buffer)
    {
        var total = 0;
        int n;
        while (total < buffer.Length && (n = s.Read(buffer, total, buffer.Length - total)) > 0)
            total += n;
        return total;
    }
}
