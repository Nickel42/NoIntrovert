using System.Runtime.InteropServices;

namespace NoIntrovert.Core.Archives;

public enum ArchiveFormat { SevenZip = 0, Zip = 1 }

/// <summary>Compression levels 7-Zip understands (its -mx values).</summary>
public enum ArchiveLevel { Store = 0, Fastest = 1, Fast = 3, Normal = 5, Maximum = 7, Ultra = 9 }

public sealed class ArchiveException(string message) : Exception(message);

/// <summary>
/// 7z and zip archives through 7-Zip's own engine: noi_archive (our C
/// interface over bit7z) driving 7z.dll / 7z.so. One instance runs one
/// operation at a time; give each worker its own.
/// </summary>
public sealed class ArchiveEngine : IDisposable
{
    private IntPtr _handle;

    /// <summary>File name of 7-Zip's library on this platform.</summary>
    public static string SevenZipLibraryName => OperatingSystem.IsWindows() ? "7z.dll" : "7z.so";

    /// <summary>Where 7-Zip's library is expected: next to the app (inside the .app bundle on macOS).</summary>
    public static string SevenZipLibraryPath => Path.Combine(AppContext.BaseDirectory, SevenZipLibraryName);

    public ArchiveEngine()
    {
        var path = SevenZipLibraryPath;
        if (!File.Exists(path))
            throw new ArchiveException($"7-Zip's library ({SevenZipLibraryName}) was not found next to the app at {path}.");
        int abi;
        try { abi = Native.noi_abi_version(); }
        catch (DllNotFoundException ex) { throw new ArchiveException($"noi_archive could not be loaded: {ex.Message}"); }
        if (abi != Native.AbiVersion)
            throw new ArchiveException($"noi_archive has interface version {abi}; this app needs {Native.AbiVersion}.");

        var status = Native.noi_open(path, out _handle, out var error);
        if (status != Native.Status.Ok)
            throw new ArchiveException($"7-Zip's library could not be loaded: {Marshal.PtrToStringUTF8(error) ?? status.ToString()}");
    }

    /// <summary>Whether the archive engine can load, and why not when it can't.</summary>
    public static bool IsAvailable(out string problem)
    {
        try
        {
            using var _ = new ArchiveEngine();
            problem = "";
            return true;
        }
        catch (ArchiveException ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Compresses <paramref name="inputs"/> (files by name, folders with their
    /// contents) into a new archive. With <paramref name="contentsOnly"/> and a
    /// single folder, stores what is inside the folder rather than the folder.
    /// </summary>
    public void Compress(ArchiveFormat format, ArchiveLevel level, int threads, bool solid,
        IReadOnlyList<string> inputs, string output, bool contentsOnly, Action<double>? progress, CancellationToken token)
    {
        if (inputs.Count == 0) throw new ArgumentException("Nothing to compress.", nameof(inputs));
        var options = new Native.CompressOptions
        {
            StructSize = (uint)Marshal.SizeOf<Native.CompressOptions>(),
            Format = (int)format,
            Level = (int)level,
            Threads = (uint)Math.Max(0, threads),
            Solid = solid ? 1 : 0,
            ContentsOnly = contentsOnly ? 1 : 0,
        };
        var pointers = inputs.Select(p => Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(p))).ToArray();
        try
        {
            Run(callback => Native.noi_compress(_handle, ref options, pointers, pointers.Length, Path.GetFullPath(output), callback, IntPtr.Zero),
                progress, token, output);
        }
        finally
        {
            foreach (var p in pointers) Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>Extracts every item of any archive 7-Zip reads into <paramref name="outputDirectory"/>.</summary>
    public void Extract(string archive, string outputDirectory, Action<double>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(outputDirectory);
        Run(callback => Native.noi_extract(_handle, Path.GetFullPath(archive), Path.GetFullPath(outputDirectory), callback, IntPtr.Zero),
            progress, token, null);
    }

    /// <summary>Decompresses every item and checks its CRC, writing nothing.</summary>
    public void Test(string archive, Action<double>? progress, CancellationToken token) =>
        Run(callback => Native.noi_test(_handle, Path.GetFullPath(archive), callback, IntPtr.Zero), progress, token, null);

    private void Run(Func<Native.ProgressFn, Native.Status> call, Action<double>? progress, CancellationToken token, string? partialOutput)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        token.ThrowIfCancellationRequested();
        Native.ProgressFn callback = (_, done, total) =>
        {
            if (total > 0) progress?.Invoke(Math.Clamp((double)done / total, 0, 1));
            return token.IsCancellationRequested ? 0 : 1;
        };
        var status = call(callback);
        GC.KeepAlive(callback);

        if (status == Native.Status.Ok) return;
        if (partialOutput is not null) TryDelete(partialOutput);
        if (status == Native.Status.Cancelled) throw new OperationCanceledException(token);
        var message = status == Native.Status.BadArgs ? "invalid arguments" : Marshal.PtrToStringUTF8(Native.noi_last_error(_handle));
        throw new ArchiveException(string.IsNullOrWhiteSpace(message) ? "7-Zip reported an error." : message.Trim());
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Native.noi_close(_handle);
        _handle = IntPtr.Zero;
    }

    private static class Native
    {
        public const int AbiVersion = 1;
        private const string Lib = "noi_archive";

        public enum Status { Ok = 0, Error = 1, Cancelled = 2, BadArgs = 3 }

        [StructLayout(LayoutKind.Sequential)]
        public struct CompressOptions
        {
            public uint StructSize;
            public int Format;
            public int Level;
            public uint Threads;
            public int Solid;
            public int ContentsOnly;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ProgressFn(IntPtr user, ulong done, ulong total);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int noi_abi_version();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Status noi_open([MarshalAs(UnmanagedType.LPUTF8Str)] string lib7zPath, out IntPtr archive, out IntPtr error);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void noi_close(IntPtr archive);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr noi_last_error(IntPtr archive);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Status noi_compress(IntPtr archive, ref CompressOptions options, IntPtr[] inputs, int inputCount,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string outPath, ProgressFn progress, IntPtr user);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Status noi_extract(IntPtr archive, [MarshalAs(UnmanagedType.LPUTF8Str)] string archivePath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string outDir, ProgressFn progress, IntPtr user);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Status noi_test(IntPtr archive, [MarshalAs(UnmanagedType.LPUTF8Str)] string archivePath,
            ProgressFn progress, IntPtr user);
    }
}
