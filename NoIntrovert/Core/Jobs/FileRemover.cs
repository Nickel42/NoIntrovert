using System.Runtime.InteropServices;

namespace NoIntrovert.Core.Jobs;

/// <summary>
/// Removes source files after a successful conversion: to the Recycle Bin on
/// Windows or the Trash on macOS when asked, otherwise permanently.
/// </summary>
internal static class FileRemover
{
    /// <summary>What the platform calls its bin, for messages and labels.</summary>
    public static string BinName => OperatingSystem.IsMacOS() ? "Trash" : "Recycle Bin";

    public static bool CanUseBin => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Removes files and folders (folders with everything in them).</summary>
    public static void Remove(IEnumerable<string> paths, bool useBin, ILog log)
    {
        var toBin = useBin && CanUseBin;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var isFolder = Directory.Exists(path);
            if (!isFolder && !File.Exists(path)) continue;
            try
            {
                if (toBin && OperatingSystem.IsWindows()) SendToRecycleBin(path);
                else if (toBin) MoveToTrash(path, isFolder);
                else if (isFolder) Directory.Delete(path, recursive: true);
                else File.Delete(path);
                log.Info($"  removed {Path.GetFileName(path)}{(isFolder ? " (folder)" : "")}{(toBin ? $" (to the {BinName})" : "")}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.Warn($"  couldn't remove {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    /// <summary>Moves a file or folder into ~/.Trash, adding a number when the name is taken (as Finder does).</summary>
    private static void MoveToTrash(string path, bool isFolder)
    {
        var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
        Directory.CreateDirectory(trash);
        var name = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isFolder ? "" : Path.GetExtension(path);
        var target = Path.Combine(trash, name + ext);
        for (var i = 2; File.Exists(target) || Directory.Exists(target); i++)
            target = Path.Combine(trash, $"{name} {i}{ext}");
        if (isFolder) Directory.Move(path, target);
        else File.Move(path, target);
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
