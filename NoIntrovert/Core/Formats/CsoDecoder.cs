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

    public static void Decode(string inputPath, string outputPath, IProgress<double>? progress, CancellationToken token)
    {
        using var input = File.OpenRead(inputPath);
        using var output = new BufferedStream(File.Create(outputPath), 1 << 20);

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

        var compressed = new byte[blockSize * 2 + 64];
        var block = new byte[blockSize];
        long written = 0;

        for (var i = 0; i < blockCount; i++)
        {
            if ((i & 0xFF) == 0)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report((double)i / blockCount);
            }

            var storedPlain = (index[i] & 0x80000000) != 0;
            var start = (long)(index[i] & 0x7FFFFFFF) << shift;
            var end = (long)(index[i + 1] & 0x7FFFFFFF) << shift;
            var length = (int)(end - start);
            var outLength = (int)Math.Min(blockSize, totalBytes - written);
            if (length < 0 || length > compressed.Length)
                throw new InvalidDataException($"CSO block {i} has an invalid size; the file is damaged.");

            input.Position = start;
            input.ReadExactly(compressed, 0, length);

            // Version 2 files mark raw blocks by size instead of the top bit.
            if (storedPlain || length >= blockSize)
            {
                output.Write(compressed, 0, outLength);
            }
            else if (isLz4)
            {
                var n = LZ4Codec.Decode(compressed, 0, length, block, 0, blockSize);
                if (n < outLength) throw new InvalidDataException($"ZSO block {i} did not decompress fully.");
                output.Write(block, 0, outLength);
            }
            else
            {
                using var deflate = new DeflateStream(new MemoryStream(compressed, 0, length), CompressionMode.Decompress);
                var n = ReadFully(deflate, block);
                if (n < outLength) throw new InvalidDataException($"CSO block {i} did not decompress fully.");
                output.Write(block, 0, outLength);
            }
            written += outLength;
        }
        progress?.Report(1);
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
