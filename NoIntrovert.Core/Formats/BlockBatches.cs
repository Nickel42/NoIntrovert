using System.Runtime.ExceptionServices;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Decodes an image stored as independent compressed blocks (CSO/ZSO, PBP) in batches: each batch's
/// compressed blocks are read in order, decoded in parallel into one buffer, then written in order. Memory
/// stays at about two batches, the output is the same as a block-by-block decode, and a damaged block fails
/// at the same point and with the same error, after the blocks before it are written.
/// </summary>
internal static class BlockBatches
{
    /// <summary>Where a block is in the file, how many bytes it takes there, and how many it adds to the output.</summary>
    public readonly record struct Block(long Offset, int Length, int OutLength);

    /// <summary>
    /// Decodes one block, the <paramref name="where"/>.Length bytes at <paramref name="offset"/> in <paramref name="compressed"/>,
    /// into <paramref name="slot"/> (the slot size); throws when it's damaged.
    /// </summary>
    public delegate void Decoder(int block, byte[] compressed, int offset, Block where, Span<byte> slot);

    /// <summary>Output bytes per batch: enough to keep every core busy, small enough not to matter.</summary>
    public const int DefaultBatchBytes = 8 << 20;

    /// <param name="describe">Block <c>i</c>'s place and sizes; throws for a damaged index.</param>
    /// <param name="maxLength">The most compressed bytes a block may take (<paramref name="describe"/> checks it).</param>
    /// <param name="slotSize">The bytes a block decodes to (its output is the first OutLength of them).</param>
    /// <param name="threads">Blocks decoded at once; 0 for every processor.</param>
    /// <param name="progress">Reports the fraction of blocks written, given the first block of the next batch.</param>
    public static void Decode(Stream input, Stream output, int blockCount, Func<int, Block> describe, Decoder decode,
        int maxLength, int slotSize, int threads, Action<int>? progress, CancellationToken token, int batchBytes = DefaultBatchBytes)
    {
        var perBatch = (int)Math.Clamp(batchBytes / Math.Max(1, slotSize), 1, Math.Max(1, blockCount));
        var compressed = new byte[(long)perBatch * maxLength];
        var decoded = new byte[(long)perBatch * slotSize];
        var blocks = new Block[perBatch];
        var starts = new int[perBatch];
        var errors = new ExceptionDispatchInfo?[perBatch];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount,
            CancellationToken = token,
        };

        for (var first = 0; first < blockCount; first += perBatch)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke(first);

            // Read: in order, stopping at the first block whose index entry or data is bad.
            var count = Math.Min(perBatch, blockCount - first);
            ExceptionDispatchInfo? stop = null;
            var used = 0;
            var n = 0;
            for (; n < count; n++)
            {
                try
                {
                    var b = describe(first + n);
                    if (input.Position != b.Offset) input.Position = b.Offset;
                    input.ReadExactly(compressed, used, b.Length);
                    blocks[n] = b;
                    starts[n] = used;
                    used += b.Length;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    stop = ExceptionDispatchInfo.Capture(ex);
                    break;
                }
            }

            // Decode: the blocks are independent.
            Array.Clear(errors);
            if (n == 1) DecodeOne(0);
            else if (n > 1) Parallel.For(0, n, options, DecodeOne);

            // Write: in order, up to the first block that failed.
            for (var j = 0; j < n; j++)
            {
                errors[j]?.Throw();
                output.Write(decoded, j * slotSize, blocks[j].OutLength);
            }
            stop?.Throw();

            void DecodeOne(int j)
            {
                try
                {
                    decode(first + j, compressed, starts[j], blocks[j], decoded.AsSpan(j * slotSize, slotSize));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors[j] = ExceptionDispatchInfo.Capture(ex);
                }
            }
        }
        progress?.Invoke(blockCount);
    }

    /// <summary>Reads <paramref name="source"/> to its end or until <paramref name="buffer"/> is full; returns the bytes read.</summary>
    public static int ReadFully(Stream source, Span<byte> buffer)
    {
        var total = 0;
        int n;
        while (total < buffer.Length && (n = source.Read(buffer[total..])) > 0)
            total += n;
        return total;
    }
}
