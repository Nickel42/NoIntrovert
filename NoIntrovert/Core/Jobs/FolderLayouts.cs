using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Esde;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Jobs;

/// <summary>Where Save to › Folder puts each result, by <see cref="OutputSettings.FolderLayout"/>.</summary>
public static class FolderLayouts
{
    /// <summary>The folder in <paramref name="target"/> that results for <paramref name="source"/> go in.</summary>
    public static string FolderIn(string target, AppSettings settings, string source) => settings.Output.FolderLayout switch
    {
        FolderLayout.Flat => target,
        FolderLayout.SameAsOriginals => SameAsOriginals(target, settings, source),
        FolderLayout.RomM => InRoms(target, SystemOf(source)?.Romm),
        _ => InRoms(target, SystemOf(source) is { } system ? EsdeSystems.FolderOf(system) : null),
    };

    /// <summary><c>roms/&lt;name&gt;</c> in <paramref name="target"/> (no second roms when it is one); the folder itself without a name.</summary>
    private static string InRoms(string target, string? name) => name is null ? target : Path.Combine(CleanupPlanner.RomsFolder(target), name);

    /// <summary><paramref name="source"/>'s system, as Convert detects it; null when it can't be told or read.</summary>
    private static SystemInfo? SystemOf(string source)
    {
        try
        {
            return FormatRegistry.Default.Identify(source) is { } set ? TargetFormats.ById(SystemDetector.SystemOf(set)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="target"/> plus the subfolders <paramref name="source"/> is in below the folder it was added from
    /// (the innermost of the run's added folders and the Library folders that holds it); <paramref name="target"/>
    /// itself for a file added on its own.
    /// </summary>
    private static string SameAsOriginals(string target, AppSettings settings, string source)
    {
        var path = Path.GetFullPath(Path.TrimEndingDirectorySeparator(source));
        if (Path.GetDirectoryName(path) is not { } folder) return target;
        var root = (settings.Output.SourceRoots ?? []).Concat(settings.Library.Folders)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.GetFullPath(Path.TrimEndingDirectorySeparator(r)))
            .Where(r => Inside(path, r))
            .MaxBy(r => r.Length);
        if (root is null) return target;
        var relative = Path.GetRelativePath(root, folder);
        return relative == "." ? target : Path.Combine(target, relative);
    }

    private static bool Inside(string path, string root)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathNames.Comparison);
    }
}
