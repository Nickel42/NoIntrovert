namespace NoIntrovert.Core;

/// <summary>
/// A per-job scratch folder under the configured temp root, deleted when the
/// job finishes. Converters write rebuilt images and cue sheets here.
/// </summary>
internal sealed class WorkDirectory : IDisposable
{
    private readonly List<string> _strayFiles = [];

    public string Path { get; }

    public WorkDirectory(string root)
    {
        Path = System.IO.Path.Combine(root, "job-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// Picks where to write a cue sheet that must point at <paramref name="targets"/>.
    /// chdman joins a cue's FILE names onto the cue's own folder, so absolute
    /// paths don't work; relative ones do, but only on the same drive. When a
    /// target is on another drive, the cue goes next to it as a hidden temp
    /// file instead, and is deleted with this work directory.
    /// </summary>
    public string CuePathFor(string cueName, IReadOnlyCollection<string> targets)
    {
        var workRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(Path));
        var foreign = targets.FirstOrDefault(t =>
            !string.Equals(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(t)), workRoot, StringComparison.OrdinalIgnoreCase));
        if (foreign is null)
            return System.IO.Path.Combine(Path, cueName);

        var stray = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(foreign)!,
            $".noi-{Guid.NewGuid().ToString("N")[..8]}-{cueName}");
        _strayFiles.Add(stray);
        return stray;
    }

    /// <summary>How a cue at <paramref name="cuePath"/> should name <paramref name="target"/>.</summary>
    public static string CueReference(string cuePath, string target)
    {
        var relative = System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(cuePath)!, target);
        if (System.IO.Path.IsPathRooted(relative))
            throw new NotSupportedException($"The files of this disc are on different drives ('{target}'). Put them in one folder.");
        return relative;
    }

    public void Dispose()
    {
        foreach (var f in _strayFiles)
        {
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a file may still be open; the next start-up sweep removes it */ }
        catch (UnauthorizedAccessException) { DeleteReadOnly(); }
    }

    // Files unpacked from an archive can keep a read-only attribute, which stops Directory.Delete on Windows.
    private void DeleteReadOnly()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class WorkPaths
{
    public static string DefaultTempRoot => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoIntrovert");

    /// <summary>Removes job folders left behind by a run that crashed or was killed; returns how many went.</summary>
    public static int SweepStale(string root)
    {
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(root, "job-*"))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-12))
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }
}
