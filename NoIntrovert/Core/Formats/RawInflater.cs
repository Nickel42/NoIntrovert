using System.IO.Compression;
using System.Runtime.InteropServices;

namespace NoIntrovert.Core.Formats;

/// <summary>
/// Inflates one raw-deflate block (CSO, PBP) into a span, reusing one inflater per thread instead of building a
/// <see cref="DeflateStream"/> (and its native inflater, buffer and wrapper stream) for every block.
/// </summary>
/// <remarks>
/// .NET's DeflateStream can't be reset, so this calls the zlib that ships with the runtime
/// (System.IO.Compression.Native, the library DeflateStream itself uses; statically linked into a single-file
/// build) and resets it between blocks with inflateReset2. With 2 KiB CSO blocks that's about three times the
/// speed of a DeflateStream per block. When the runtime's library or one of its functions can't be found, every
/// block goes through a DeflateStream as before; the output and the errors are the same either way.
/// </remarks>
internal static unsafe class RawInflater
{
    private const string Library = "System.IO.Compression.Native";
    private const int RawWindowBits = -15; // raw deflate, 32 KiB window, no zlib/gzip header
    private const int ZFinish = 4;
    private const int ZOk = 0, ZStreamEnd = 1, ZBufError = -5;

    /// <summary>The runtime's PAL_ZStream (pal_zlib.h), which its exports take in place of zlib's z_stream.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PalZStream
    {
        public byte* NextIn;
        public byte* NextOut;
        public byte* Message;
        public void* InternalState;
        public uint AvailIn;
        public uint AvailOut;
    }

    [DllImport(Library, EntryPoint = "CompressionNative_InflateInit2_")]
    private static extern int InflateInit2(PalZStream* stream, int windowBits);

    [DllImport(Library, EntryPoint = "CompressionNative_InflateReset2_")]
    private static extern int InflateReset2(PalZStream* stream, int windowBits);

    [DllImport(Library, EntryPoint = "CompressionNative_Inflate")]
    private static extern int Inflate(PalZStream* stream, int flush);

    [DllImport(Library, EntryPoint = "CompressionNative_InflateEnd")]
    private static extern int InflateEnd(PalZStream* stream);

    /// <summary>False once the runtime's zlib couldn't be used; then every block uses a DeflateStream.</summary>
    private static volatile bool nativeUsable = true;

    [ThreadStatic] private static State? perThread;

    /// <summary>For tests: whether blocks currently go through the runtime's zlib directly.</summary>
    internal static bool UsesNative => nativeUsable && !ForceStream;

    /// <summary>For tests: send every block through a DeflateStream, as before.</summary>
    internal static bool ForceStream { get; set; }

    /// <summary>
    /// Inflates the raw-deflate data in <paramref name="source"/> into <paramref name="destination"/>, stopping when
    /// it's full or the data ends. Returns the bytes written; throws <see cref="InvalidDataException"/> for damaged data.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (nativeUsable && !ForceStream)
        {
            try
            {
                var written = (perThread ??= new State()).Decode(source, destination);
                if (written >= 0) return written;
                // Damaged or cut-short data: the DeflateStream below gives the same output and error as before.
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                nativeUsable = false;
                perThread = null;
            }
        }
        return DecodeWithStream(source, destination);
    }

    private static int DecodeWithStream(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var copy = source.ToArray();
        using var deflate = new DeflateStream(new MemoryStream(copy, writable: false), CompressionMode.Decompress);
        return BlockBatches.ReadFully(deflate, destination);
    }

    /// <summary>One thread's inflater: made on first use, reset for every block, freed when the thread ends.</summary>
    private sealed class State
    {
        private readonly PalZStream* stream;
        private bool initialized;

        public State() => stream = (PalZStream*)NativeMemory.AllocZeroed((nuint)sizeof(PalZStream));

        /// <summary>The bytes written, or -1 when the block needs a DeflateStream to say what's wrong with it.</summary>
        public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            if (!initialized)
            {
                if (InflateInit2(stream, RawWindowBits) != ZOk) return -1;
                initialized = true;
            }
            else if (InflateReset2(stream, RawWindowBits) != ZOk)
            {
                return -1;
            }

            fixed (byte* input = source)
            fixed (byte* output = destination)
            {
                stream->NextIn = input;
                stream->AvailIn = (uint)source.Length;
                stream->NextOut = output;
                stream->AvailOut = (uint)destination.Length;
                var result = Inflate(stream, ZFinish);
                var written = destination.Length - (int)stream->AvailOut;
                stream->NextIn = stream->NextOut = null;
                // The data's end, or a full output (Z_OK / Z_BUF_ERROR with more to come): what a DeflateStream read gives.
                if (result == ZStreamEnd) return written;
                if (result is ZOk or ZBufError && stream->AvailOut == 0) return written;
                return -1; // damaged, or the data ran out before its end
            }
        }

        ~State()
        {
            if (initialized) InflateEnd(stream);
            NativeMemory.Free(stream);
        }
    }
}
