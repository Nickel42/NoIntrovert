namespace NoIntrovert.Core;

/// <summary>
/// How paths are compared for "is this the same file": without case on
/// Windows and macOS, whose file systems ignore it, and with case on Linux,
/// where NES/ and nes/ are two folders.
/// </summary>
public static class PathNames
{
    public static readonly StringComparer Comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static readonly StringComparison Comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Whether two paths name the same file or folder.</summary>
    public static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), Comparison);
}
