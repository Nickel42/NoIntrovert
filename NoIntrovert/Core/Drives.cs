using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NoIntrovert.Core;

/// <summary>
/// Whether a path is on a spinning hard drive, asked once per drive. Anything that can't be told (a network share,
/// macOS, a failed query) counts as not spinning, so it keeps the usual behaviour.
/// </summary>
internal static class Drives
{
    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For tests: answers for a path instead of asking the drive (null: ask it).</summary>
    internal static Func<string, bool?>? ForTests { get; set; }

    /// <summary>How many drives have been asked so far (each is asked once).</summary>
    internal static int Queries;

    /// <summary>True when <paramref name="path"/> (a file or folder, which needn't exist yet) is on a rotational drive.</summary>
    public static bool IsSpinning(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (ForTests?.Invoke(path) is { } answer) return answer;
        try
        {
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
            {
                var root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false; // a share
                return Cache.GetOrAdd(root, WindowsSpinning);
            }
            if (OperatingSystem.IsLinux() && LinuxDevice(full) is { } device)
                return Cache.GetOrAdd(device, LinuxSpinning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return false;
    }

    // ---- Windows: IOCTL_STORAGE_QUERY_PROPERTY, StorageDeviceSeekPenaltyProperty ----

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref StoragePropertyQuery input, int inputSize,
        out DeviceSeekPenaltyDescriptor output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    private static bool WindowsSpinning(string root)
    {
        Interlocked.Increment(ref Queries);
        try
        {
            const uint driveFixed = 3, driveRemovable = 2;
            var type = GetDriveTypeW(root);
            if (type is not (driveFixed or driveRemovable)) return false; // network, optical, RAM disk, unknown
            // No access rights needed for this query, so it works without elevation. Only "C:"-style roots have a device.
            if (root.Length < 2 || root[1] != ':') return false;
            using var device = CreateFileW($@"\\.\{root[0]}:", 0, 3 /* read | write sharing */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (device.IsInvalid) return false;
            var query = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
            if (!DeviceIoControl(device, IoctlStorageQueryProperty, ref query, Marshal.SizeOf<StoragePropertyQuery>(),
                    out var answer, Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(), out var returned, IntPtr.Zero))
                return false; // a volume over several disks, a driver that doesn't say
            return returned > 8 && answer.IncursSeekPenalty; // the flag is the byte after Version and Size
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    // ---- Linux: the mount's block device, then /sys/dev/block/<major:minor>/queue/rotational ----

    /// <summary>"8:1" for the device the path's mount is on (the longest mount point that holds it); null when unknown.</summary>
    private static string? LinuxDevice(string full)
    {
        const string mountInfo = "/proc/self/mountinfo";
        if (!File.Exists(mountInfo)) return null;
        string? best = null, device = null;
        foreach (var line in File.ReadLines(mountInfo))
        {
            // id parent major:minor root mount-point options ... - type source super-options
            var parts = line.Split(' ');
            if (parts.Length < 5) continue;
            var mount = parts[4].Replace("\\040", " ");
            var inside = mount == "/" || full == mount || full.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal);
            if (!inside || (best is not null && mount.Length < best.Length)) continue;
            best = mount;
            device = parts[2];
        }
        return device;
    }

    private static bool LinuxSpinning(string device)
    {
        Interlocked.Increment(ref Queries);
        try
        {
            // /sys/dev/block/8:1 links to …/sda/sda1; a partition's queue/ is its disk's, one folder up.
            var block = new DirectoryInfo($"/sys/dev/block/{device}");
            var real = block.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? block.FullName;
            foreach (var folder in new[] { real, Path.GetDirectoryName(real) })
            {
                if (folder is null) continue;
                var flag = Path.Combine(folder, "queue", "rotational");
                if (File.Exists(flag)) return File.ReadAllText(flag).Trim() == "1";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }
}
