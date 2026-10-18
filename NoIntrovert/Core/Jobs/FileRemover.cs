using System.Runtime.InteropServices;

namespace NoIntrovert.Core.Jobs;

/// <summary>Removes source files after a successful conversion, to the Recycle Bin where possible.</summary>
internal static class FileRemover
{
    public static void Remove(IEnumerable<string> files, bool useRecycleBin, ILog log)
    {
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file)) continue;
            try
            {
                if (useRecycleBin && OperatingSystem.IsWindows())
                    SendToRecycleBin(file);
                else
                    File.Delete(file);
                log.Info($"  removed {Path.GetFileName(file)}{(useRecycleBin && OperatingSystem.IsWindows() ? " (to Recycle Bin)" : "")}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.Warn($"  couldn't remove {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private static void SendToRecycleBin(string path)
    {
        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent,
        };
        var result = SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
            throw new IOException($"The Recycle Bin refused the file (error 0x{result:X}).");
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004, FofNoConfirmation = 0x0010, FofAllowUndo = 0x0040, FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct op);
}
