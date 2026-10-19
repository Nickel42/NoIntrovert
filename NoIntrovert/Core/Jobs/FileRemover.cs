using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NoIntrovert.Core.Jobs;

/// <summary>
/// Removes source files after a successful conversion: to the Recycle Bin on
/// Windows, the Trash on macOS or the freedesktop.org Trash on Linux when
/// asked, otherwise permanently.
/// </summary>
internal static class FileRemover
{
    /// <summary>What the platform calls its bin, for messages and labels.</summary>
    public static string BinName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    public static bool CanUseBin => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

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
                else if (toBin && OperatingSystem.IsLinux()) FreedesktopTrash.Move(path, isFolder);
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

    /// <summary>
    /// The freedesktop.org Trash (the one GNOME, KDE and other desktops show):
    /// the home trash, $XDG_DATA_HOME/Trash, for files on the same file system,
    /// and $topdir/.Trash-$uid on the file's own volume for the rest, so nothing
    /// is copied across disks. Each entry gets an info/NAME.trashinfo file
    /// recording where it came from, so the desktop can restore it.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static class FreedesktopTrash
    {
        private const int Exdev = 18; // rename(2): not on the same file system

        public static void Move(string path, bool isFolder)
        {
            var full = Path.GetFullPath(path).TrimEnd('/');
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(dataHome) || !Path.IsPathRooted(dataHome))
                dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            if (TryMoveInto(Path.Combine(dataHome, "Trash"), full, full, isFolder)) return;

            // Different file system from the home trash: use the volume's own.
            var top = MountPointOf(full)
                ?? throw new IOException("It is on another file system and its mount point couldn't be found for that disk's Trash.");
            var volumeTrash = Path.Combine(top, $".Trash-{getuid()}");
            var relative = Path.GetRelativePath(top, full);
            if (!TryMoveInto(volumeTrash, full, relative, isFolder))
                throw new IOException($"It couldn't be moved into {volumeTrash}.");
        }

        /// <summary>Moves into one trash folder; false when that trash is on another file system.</summary>
        private static bool TryMoveInto(string trash, string full, string recordedPath, bool isFolder)
        {
            var files = Path.Combine(trash, "files");
            var info = Path.Combine(trash, "info");
            foreach (var dir in new[] { trash, files, info })
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var name = isFolder ? Path.GetFileName(full) : Path.GetFileNameWithoutExtension(full);
            var ext = isFolder ? "" : Path.GetExtension(full);
            var text = $"[Trash Info]\nPath={Escape(recordedPath)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n";
            for (var i = 1; ; i++)
            {
                var entry = i == 1 ? name + ext : $"{name} {i}{ext}";
                var infoPath = Path.Combine(info, entry + ".trashinfo");
                var target = Path.Combine(files, entry);
                if (File.Exists(target) || Directory.Exists(target)) continue;
                // Creating the .trashinfo exclusively claims the name (as the spec asks).
                try
                {
                    using var fs = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write);
                    fs.Write(Encoding.UTF8.GetBytes(text));
                }
                catch (IOException) when (File.Exists(infoPath)) { continue; }

                if (rename(full, target) == 0) return true;
                var errno = Marshal.GetLastPInvokeError();
                File.Delete(infoPath);
                if (errno == Exdev) return false;
                throw new IOException($"Moving it to the Trash failed ({Marshal.GetPInvokeErrorMessage(errno)}).");
            }
        }

        /// <summary>The longest mounted path containing <paramref name="full"/>.</summary>
        private static string? MountPointOf(string full)
        {
            string? best = null;
            foreach (var drive in DriveInfo.GetDrives())
            {
                var root = drive.RootDirectory.FullName.TrimEnd('/');
                if ((root.Length == 0 || full.StartsWith(root + "/", StringComparison.Ordinal)) && (best is null || root.Length > best.Length))
                    best = root;
            }
            return best is null ? null : best.Length == 0 ? "/" : best;
        }

        /// <summary>Percent-encodes a path for the Path= key, keeping slashes and unreserved characters.</summary>
        private static string Escape(string path)
        {
            var sb = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(path))
            {
                var c = (char)b;
                if (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~') sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int rename([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

        [DllImport("libc")]
        private static extern uint getuid();
    }
}
