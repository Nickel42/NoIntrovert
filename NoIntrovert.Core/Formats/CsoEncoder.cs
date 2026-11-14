using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using K4os.Compression.LZ4;
using Lib7zZipKit;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Writes an ISO as CSO v1 ("CISO", raw deflate blocks, for a real PSP, Adrenaline and PPSSPP) or
/// ZSO ("ZISO", LZ4 blocks, for OPL on a real PS2 and PCSX2). The inverse of <see cref="CsoDecoder"/>.
/// </summary>
/// <remarks>
/// Deflate goes through 7-Zip's encoder at its ultra setting (lib-7z-zip-kit's
/// <see cref="SevenZipDeflater"/>), which packs a few percent smaller than zlib; where that native
/// engine isn't built, .NET's own deflate at its smallest setting is used. ZSO uses LZ4 HC at its
/// highest level. A block that doesn't get smaller is stored as is, flagged by the index entry's
/// top bit. Images too big for 31-bit offsets get an index shift, with each block aligned to it.
/// </remarks>
internal static class CsoEncoder
{
    /// <summary>The block size real hardware and OPL expect.</summary>
    public const int DefaultBlockSize = 2048;

    private const int HeaderSize = 24;

    /// <summary>Whether CSO deflate goes through 7-Zip's encoder (true) or .NET's (false).</summary>
    public static bool UsesSevenZipDeflate => SevenZipDeflater.IsAvailable(out _);

    /// <param name="lz4">ZSO (LZ4) when true, CSO (deflate) when false.</param>
    /// <param name="threads">Blocks compressed at once; 0 for every processor.</param>
    /// <param name="sourceHash">Fed every byte of the image as it's read, to check the result against later.</param>
    /// <param name="minimumShift">Tests only: an index shift (and block alignment) smaller images don't need.</param>
    public static void Encode(string isoPath, string outputPath, bool lz4, IProgress<double>? progress, CancellationToken token,
        int threads = 0, IncrementalHash? sourceHash = null, int blockSize = DefaultBlockSize, int batchBytes = BlockBatches.DefaultBatchBytes,
        int minimumShift = 0)
    {
        if (blockSize < 512 || blockSize > 1 << 20 || (blockSize & (blockSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "The block size must be a power of two from 512 bytes to 1 MiB.");

        using var input = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var total = input.Length;
        if (total == 0) throw new InvalidDataException("The image is empty.");
        var blockCount = (total + blockSize - 1) / blockSize;
        if (blockCount >= int.MaxValue / 4) throw new InvalidDataException("The image is too big for CSO.");
        var shift = Math.Max(minimumShift, IndexShift(total, (int)blockCount));
        var align = 1L << shift;

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        var header = new byte[HeaderSize];
        (lz4 ? "ZISO"u8 : "CISO"u8).CopyTo(header);
        BitConverter.TryWriteBytes(header.AsSpan(4), (uint)HeaderSize);
        BitConverter.TryWriteBytes(header.AsSpan(8), total);
        BitConverter.TryWriteBytes(header.AsSpan(16), blockSize);
        header[20] = 1; // version
        header[21] = (byte)shift;
        output.Write(header);
        var index = new uint[blockCount + 1];
        output.Write(new byte[index.Length * 4]);
        Pad(output, align);

        var perBatch = (int)Math.Clamp(batchBytes / blockSize, 1, blockCount);
        var raw = new byte[(long)perBatch * blockSize];
        var packed = new byte[(long)perBatch * blockSize];
        var lengths = new int[perBatch];
        var errors = new ExceptionDispatchInfo?[perBatch];
        var deflaters = new ConcurrentBag<Deflater>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount,
            CancellationToken = token,
        };

        try
        {
            for (long first = 0; first < blockCount; first += perBatch)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report((double)first / blockCount);
                var count = (int)Math.Min(perBatch, blockCount - first);
                var batchBytesRead = (int)Math.Min((long)count * blockSize, total - first * blockSize);
                input.ReadExactly(raw, 0, batchBytesRead);
                sourceHash?.AppendData(raw, 0, batchBytesRead);

                Array.Clear(errors);
                Parallel.For(0, count, options, j =>
                {
                    var deflater = lz4 ? null : deflaters.TryTake(out var d) ? d : new Deflater();
                    try
                    {
                        var length = (int)Math.Min(blockSize, total - (first + j) * blockSize);
                        var source = raw.AsSpan(j * blockSize, length);
                        var target = packed.AsSpan(j * blockSize, blockSize - 1);
                        lengths[j] = lz4 ? Lz4(source, target) : deflater!.Compress(source, target);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors[j] = ExceptionDispatchInfo.Capture(ex);
                    }
                    finally
                    {
                        if (deflater is not null) deflaters.Add(deflater);
                    }
                });

                for (var j = 0; j < count; j++)
                {
                    errors[j]?.Throw();
                    var block = first + j;
                    var length = (int)Math.Min(blockSize, total - block * blockSize);
                    var start = output.Position;
                    var n = lengths[j];
                    // Stored as is when compression doesn't pay, counting the alignment padding, so decoders
                    // that tell stored blocks by their size (as CSO v2 does) read it the same way.
                    var stored = n <= 0 || n + (align - 1) >= blockSize || n >= length;
                    index[block] = (uint)(start >> shift) | (stored ? 0x80000000u : 0);
                    if (stored) output.Write(raw, j * blockSize, length);
                    else output.Write(packed, j * blockSize, n);
                    Pad(output, align);
                }
            }
            index[blockCount] = (uint)(output.Position >> shift);
        }
        finally
        {
            foreach (var d in deflaters) d.Dispose();
        }

        output.Position = HeaderSize;
        var indexBytes = new byte[index.Length * 4];
        Buffer.BlockCopy(index, 0, indexBytes, 0, indexBytes.Length);
        output.Write(indexBytes);
        output.Flush(flushToDisk: true);
        progress?.Report(1);
    }

    /// <summary>
    /// The smallest index shift that lets 31-bit index entries reach the end of the file, even if every
    /// block is stored and padded to the alignment the shift brings.
    /// </summary>
    internal static int IndexShift(long total, int blockCount)
    {
        for (var shift = 0; shift < 32; shift++)
        {
            var align = 1L << shift;
            var worst = HeaderSize + (blockCount + 1L) * 4 + align + total + blockCount * (align - 1);
            if (worst >> shift <= 0x7FFFFFFF) return shift;
        }
        throw new InvalidDataException("The image is too big for CSO.");
    }

    private static void Pad(Stream output, long align)
    {
        var over = output.Position & (align - 1);
        if (over == 0) return;
        Span<byte> zeros = stackalloc byte[(int)Math.Min(align - over, 4096)];
        for (var left = align - over; left > 0; left -= zeros.Length)
            output.Write(zeros[..(int)Math.Min(left, zeros.Length)]);
    }

    /// <summary>LZ4 HC at its highest level; 0 when the result doesn't fit in <paramref name="target"/>.</summary>
    private static int Lz4(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var n = LZ4Codec.Encode(source, target, LZ4Level.L12_MAX);
        return n > 0 ? n : 0;
    }

    /// <summary>One worker's deflate encoder: 7-Zip's when its engine is built, .NET's otherwise.</summary>
    private sealed class Deflater : IDisposable
    {
        private readonly SevenZipDeflater? _sevenZip = SevenZipDeflater.IsAvailable(out _) ? new SevenZipDeflater(9) : null;
        private readonly MemoryStream _buffer = new();

        /// <summary>Raw deflate of <paramref name="source"/> into <paramref name="target"/>; 0 when it doesn't fit.</summary>
        public int Compress(ReadOnlySpan<byte> source, Span<byte> target)
        {
            if (_sevenZip is not null) return _sevenZip.Compress(source, target);
            _buffer.SetLength(0);
            using (var deflate = new DeflateStream(_buffer, CompressionLevel.SmallestSize, leaveOpen: true))
                deflate.Write(source);
            if (_buffer.Length > target.Length) return 0;
            _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length).CopyTo(target);
            return (int)_buffer.Length;
        }

        public void Dispose()
        {
            _sevenZip?.Dispose();
            _buffer.Dispose();
        }
    }
}
