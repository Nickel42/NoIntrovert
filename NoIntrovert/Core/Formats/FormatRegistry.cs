namespace NoIntrovert.Core.Formats;

/// <summary>One kind of input the app recognises: a file extension (or several), or a kind of folder.</summary>
public sealed class FormatInfo(string id, string name, params string[] extensions)
{
    public string Id { get; } = id;
    public string Name { get; } = name;

    /// <summary>The extensions this format uses, with the dot; empty for folder formats.</summary>
    public IReadOnlyList<string> Extensions { get; } = extensions;

    public bool HasExtension(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public override string ToString() => Id;
}

/// <summary>
/// One input as the scan found it: the path to work on and every file that
/// belongs to it (a cue and its bins, a .001 and its parts). For an archive,
/// <paramref name="Inside"/> lists the formats of the files in it; null when
/// it isn't an archive or its list couldn't be read.
/// </summary>
public sealed record FileSet(string Primary, IReadOnlyList<string> Members, FormatInfo Format, IReadOnlyList<FormatInfo>? Inside = null)
{
    /// <summary>Whether this is an archive known to hold one of <paramref name="formats"/>.</summary>
    public bool Holds(IEnumerable<FormatInfo> formats) => Inside is { } inside && inside.Any(formats.Contains);
}

/// <summary>
/// Knows one family of formats: how to recognise a path, and which of the
/// files and folders in a scan belong together.
/// </summary>
public interface IFormatHandler
{
    string Id { get; }

    /// <summary>Handlers claim in descending priority, so a cue takes its bins before images are looked at.</summary>
    int Priority { get; }

    IReadOnlyList<FormatInfo> Formats { get; }

    /// <summary>The format of one file, by name (and, where needed, a quick look at it).</summary>
    FormatInfo? Recognize(string file);

    /// <summary>The format of a folder that is one input as a whole (a Wii U title), or null.</summary>
    FormatInfo? RecognizeFolder(string folder) => null;

    /// <summary>Claims what it understands among the unclaimed files and folders of <paramref name="set"/>.</summary>
    IEnumerable<FileSet> Claim(ScanSet set);
}

/// <summary>
/// The files (and folders) one scan step looks at. Paths are made full and
/// compared without case once, here, so no handler compares raw strings.
/// </summary>
public sealed class ScanSet
{
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);

    public ScanSet(IEnumerable<string> files, IEnumerable<string>? folders = null)
    {
        Files = files.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Folders = (folders ?? []).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> Files { get; }
    public IReadOnlyList<string> Folders { get; }

    public IEnumerable<string> UnclaimedFiles => Files.Where(f => !_claimed.Contains(f));
    public IEnumerable<string> UnclaimedFolders => Folders.Where(f => !_claimed.Contains(f));

    public bool IsClaimed(string path) => _claimed.Contains(Normalize(path));

    public void Claim(string path) => _claimed.Add(Normalize(path));

    public static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}

/// <summary>
/// Every format handler, and the one place that decides what a path is and
/// which files in a folder are inputs.
/// </summary>
public sealed class FormatRegistry
{
    public static FormatRegistry Default { get; } = new(
    [
        new WiiUHandler(),
        new DiscDescriptorHandler(),
        new SplitFileHandler(),
        new ImageHandler(),
        new ChdHandler(),
        new ArchiveHandler(),
        new CartridgeHandler(),
    ]);

    private readonly List<IFormatHandler> _handlers;

    public FormatRegistry(IEnumerable<IFormatHandler> handlers)
    {
        _handlers = handlers.OrderByDescending(h => h.Priority).ToList();
    }

    public IReadOnlyList<IFormatHandler> Handlers => _handlers;

    /// <summary>What <paramref name="path"/> is: a whole-folder format for a folder, else by file.</summary>
    public FormatInfo? Detect(string path)
    {
        if (Directory.Exists(path))
            return _handlers.Select(h => h.RecognizeFolder(path)).FirstOrDefault(f => f is not null);
        return _handlers.Select(h => h.Recognize(path)).FirstOrDefault(f => f is not null);
    }

    /// <summary>One path as a scan would see it on its own (an archive looked into), or null.</summary>
    public FileSet? Identify(string path)
    {
        var full = ScanSet.Normalize(path);
        if (Directory.Exists(full))
            return _handlers.Select(h => h.RecognizeFolder(full)).FirstOrDefault(f => f is not null) is { } folder
                ? new FileSet(full, [full], folder) : null;
        return Claim(new ScanSet([full])).FirstOrDefault(s => s.Primary.Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Lets every handler claim from <paramref name="set"/>, highest priority first, sorted by path.</summary>
    public List<FileSet> Claim(ScanSet set) =>
        _handlers.SelectMany(h => h.Claim(set).ToList())
            .OrderBy(s => s.Primary, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The inputs in <paramref name="folder"/>: the folder itself if it is one
    /// input (a Wii U title), else what each folder level's files and
    /// subfolders hold, never looking inside a claimed folder.
    /// <paramref name="maxDepth"/> counts folder levels, the top one included.
    /// </summary>
    public List<FileSet> Scan(string folder, bool recursive, int maxDepth = int.MaxValue)
    {
        var root = ScanSet.Normalize(folder);
        if (_handlers.Select(h => h.RecognizeFolder(root)).FirstOrDefault(f => f is not null) is { } whole)
            return [new FileSet(root, [root], whole)];
        var found = new List<FileSet>();
        ScanInto(root, recursive ? maxDepth : 1, found, isRoot: true);
        return found;
    }

    private void ScanInto(string folder, int depth, List<FileSet> found, bool isRoot)
    {
        List<string> files, dirs;
        try
        {
            files = Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            dirs = Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (!isRoot && ex is IOException or UnauthorizedAccessException)
        {
            return; // an unreadable subfolder is skipped; an unreadable top folder is the caller's to report
        }

        var set = new ScanSet(files, dirs);
        var claimed = Claim(set);
        var byPath = claimed.ToDictionary(s => s.Primary, StringComparer.OrdinalIgnoreCase);
        found.AddRange(set.Files.Where(byPath.ContainsKey).Select(f => byPath[f]));
        foreach (var dir in set.Folders)
        {
            if (byPath.TryGetValue(dir, out var whole)) found.Add(whole);
            else if (depth > 1) ScanInto(dir, depth - 1, found, isRoot: false);
        }
    }
}
