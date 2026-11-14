using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NoIntrovert.Services;

/// <summary>
/// Shows files in the system file manager: on Windows, Explorer opens each
/// folder with the files selected; on macOS, Finder reveals them; elsewhere the
/// folder opens (Linux file managers have no common way to select a file).
/// </summary>
public static class FileReveal
{
    /// <summary>At most this many folders open at once, so a selection across a library doesn't open dozens of windows.</summary>
    public const int MaxFolders = 5;

    /// <summary>The menu item's name on this system.</summary>
    public static string MenuLabel => OperatingSystem.IsWindows() ? "Open file location"
        : OperatingSystem.IsMacOS() ? "Show in Finder" : "Open containing folder";

    /// <summary>The paths that still exist, grouped by folder (first-seen order), at most <see cref="MaxFolders"/> folders.</summary>
    public static IReadOnlyList<(string Folder, IReadOnlyList<string> Paths)> Group(IEnumerable<string> paths) =>
        paths.Where(p => File.Exists(p) || Directory.Exists(p))
            .Select(Path.GetFullPath)
            .GroupBy(p => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(p)) ?? p, Core.PathNames.Comparer)
            .Take(MaxFolders)
            .Select(g => (g.Key, (IReadOnlyList<string>)g.Distinct(Core.PathNames.Comparer).ToList()))
            .ToList();

    /// <summary>
    /// Reveals <paramref name="paths"/>; <paramref name="openFolder"/> opens a folder where a file
    /// can't be selected. Never throws: a file manager that won't start is only a missed convenience.
    /// </summary>
    public static async Task RevealAsync(IEnumerable<string> paths, Func<string, Task> openFolder)
    {
        foreach (var (folder, files) in Group(paths))
        {
            try
            {
                if (OperatingSystem.IsWindows()) { if (!SelectInExplorer(folder, files)) Start("explorer.exe", $"/select,\"{files[0]}\""); }
                else if (OperatingSystem.IsMacOS()) Start("open", ["-R", .. files]);
                else await openFolder(folder);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or COMException)
            {
                try { await openFolder(folder); } catch (Exception) { /* nothing more to try */ }
            }
        }
    }

    private static void Start(string file, string arguments) => Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false })?.Dispose();

    private static void Start(string file, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        Process.Start(info)?.Dispose();
    }

    // ---- Windows: SHOpenFolderAndSelectItems selects several files in one Explorer window ----

    private static bool SelectInExplorer(string folder, IReadOnlyList<string> files)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var folderPidl = IntPtr.Zero;
        var items = new List<IntPtr>();
        try
        {
            if (SHParseDisplayName(folder, IntPtr.Zero, out folderPidl, 0, out _) != 0) return false;
            foreach (var file in files)
                if (SHParseDisplayName(Path.TrimEndingDirectorySeparator(file), IntPtr.Zero, out var pidl, 0, out _) == 0) items.Add(pidl);
            if (items.Count == 0) return false;
            // Each item is an absolute pidl; ILFindLastID gives the part relative to the folder, which is what's expected.
            var relative = items.Select(ILFindLastID).ToArray();
            return SHOpenFolderAndSelectItems(folderPidl, (uint)relative.Length, relative, 0) == 0;
        }
        finally
        {
            foreach (var pidl in items) ILFree(pidl);
            if (folderPidl != IntPtr.Zero) ILFree(folderPidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, [In] IntPtr[] items, uint flags);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);
}
