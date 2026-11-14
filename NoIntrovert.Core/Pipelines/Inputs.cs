using System.Text.RegularExpressions;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Formats;

namespace NoIntrovert.Core.Pipelines;

/// <summary>Which files a command takes from the paths it's given (noi's --no-recurse, --include, --exclude, --system).</summary>
public sealed record InputFilter
{
    public bool Recurse { get; init; } = true;
    public IReadOnlyList<string> Include { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>System ids (TargetFormats.json) the inputs must be for; empty for any system.</summary>
    public IReadOnlyList<string> Systems { get; init; } = [];
}

/// <summary>One input found: the file set (a cue with its bins, a Wii U title folder…), its system and its size.</summary>
public sealed record InputItem(FileSet Set, string? System, long Size, string Root)
{
    public string Path => Set.Primary;
    public string Name => global::System.IO.Path.GetFileName(global::System.IO.Path.TrimEndingDirectorySeparator(Set.Primary));
}

/// <summary>What the paths given came to: the inputs, the folders they were found in, and what couldn't be used.</summary>
public sealed record InputList(IReadOnlyList<InputItem> Items, IReadOnlyList<string> Folders, IReadOnlyList<string> Problems);

/// <summary>
/// Turns the files and folders given to a command into its inputs, as dropping them on the app's pages does:
/// a folder is scanned by the format registry (a cue claims its bins, a Wii U title folder is one input), a file
/// is identified on its own. <see cref="Kind.Convert"/> scans folders as the Convert page does, bringing in only
/// what isn't in its system's target format yet.
/// </summary>
public static class Inputs
{
    public enum Kind
    {
        /// <summary>Verify, Organize, Optimize: every input the registry recognises.</summary>
        Games,
        /// <summary>Convert: what a folder needs converting; any file given by name (to pack, at least).</summary>
        Convert,
        /// <summary>Library tests: disc images.</summary>
        Discs,
    }

    public static InputList Gather(IEnumerable<string> paths, Kind kind, InputFilter filter, AppSettings settings)
    {
        var items = new List<InputItem>();
        var folders = new List<string>();
        var problems = new List<string>();
        var seen = new HashSet<string>(PathNames.Comparer);
        var include = filter.Include.Select(Glob.ToRegex).ToList();
        var exclude = filter.Exclude.Select(Glob.ToRegex).ToList();

        void Add(FileSet set, string root)
        {
            var key = ScanSet.Normalize(set.Primary);
            if (!seen.Add(key)) return;
            if (include.Count > 0 && !include.Any(r => Glob.Matches(r, set.Primary))) return;
            if (exclude.Any(r => Glob.Matches(r, set.Primary))) return;
            string? system;
            try { system = SystemDetector.SystemOf(set, root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { system = null; }
            if (filter.Systems.Count > 0 && (system is null || !filter.Systems.Contains(system, StringComparer.OrdinalIgnoreCase))) return;
            items.Add(new InputItem(set, system, SizeOf(set), root));
        }

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path;
            try { path = ScanSet.Normalize(Path.GetFullPath(raw.Trim())); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                problems.Add($"{raw}: not a valid path");
                continue;
            }
            try
            {
                if (Directory.Exists(path) && !IsSingleInputFolder(path, kind))
                {
                    folders.Add(path);
                    foreach (var set in ScanFolder(path, kind, filter.Recurse, settings)) Add(set, path);
                }
                else if (File.Exists(path) || Directory.Exists(path))
                {
                    var root = Path.GetDirectoryName(path) ?? path;
                    var set = FormatRegistry.Default.Identify(path);
                    if (set is null && kind == Kind.Convert)
                        set = new FileSet(path, [path], Directory.Exists(path) ? ConvertTargets.PackedFolder : ConvertTargets.OtherFile);
                    if (set is null || (kind == Kind.Discs && !KnownFormats.TakesDisc(set)))
                    {
                        problems.Add($"{path}: not a file this command can use");
                        continue;
                    }
                    Add(set, root);
                }
                else problems.Add($"{raw}: no such file or folder");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                problems.Add($"{path}: {ex.Message}");
            }
        }

        // A file named on its own that another input already takes in (a .bin beside its .cue) isn't an input of its own.
        var members = items.SelectMany(i => i.Set.Members.Where(m => !PathNames.Same(m, i.Path))).ToHashSet(PathNames.Comparer);
        items.RemoveAll(i => members.Contains(i.Path));
        return new InputList(items, folders, problems);
    }

    /// <summary>A Wii U title folder, a PS Vita game folder or a PS3 game folder given by name is one input, not a folder to scan.</summary>
    private static bool IsSingleInputFolder(string path, Kind kind)
    {
        if (kind == Kind.Discs) return false;
        var set = FormatRegistry.Default.Identify(path);
        return set is not null && PathNames.Same(set.Primary, path);
    }

    private static IEnumerable<FileSet> ScanFolder(string folder, Kind kind, bool recurse, AppSettings settings) => kind switch
    {
        Kind.Convert => ConvertInputs.InFolder(folder, recurse, settings),
        Kind.Discs => FormatRegistry.Default.Scan(folder, recurse).Where(KnownFormats.TakesDisc),
        _ => FormatRegistry.Default.Scan(folder, recurse),
    };

    /// <summary>An input's size on disk, as a page's row counts it: every file of the set, or a folder's contents.</summary>
    public static long SizeOf(FileSet set)
    {
        try
        {
            if (Directory.Exists(set.Primary)) return ArchiveJobs.SizeOf(set.Primary);
            return set.Members.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}

/// <summary>What the Convert page brings in from a folder (shared with noi convert).</summary>
public static class ConvertInputs
{
    /// <summary>
    /// The inputs found in <paramref name="folder"/> that the rules say to convert (a loose ROM, a zip that
    /// should be a 7z), plus those with a natural conversion whose system is unknown (a disc image in an
    /// unnamed folder). What's optimal or left as is stays out, so a folder of finished 7z ROMs isn't
    /// repacked; a folder with nothing in it to convert becomes one input to pack.
    /// </summary>
    public static IReadOnlyList<FileSet> InFolder(string folder, bool recurse, AppSettings settings)
    {
        var scan = FormatRegistry.Default.Scan(folder, recurse);
        // A .wua only matters next to another source to merge into it (an update or DLC folder).
        var otherWiiU = scan.Any(s => KnownFormats.WiiUBuildable.Contains(s.Format) && s.Format != KnownFormats.Wua);
        bool Natural(FileSet s) =>
            (!IsFinished(s) || (s.Format == KnownFormats.Wua && otherWiiU))
            && !KnownFormats.Cartridges.Contains(s.Format)
            && (!KnownFormats.Archives.Contains(s.Format) || s.Inside is null || s.Holds(KnownFormats.DiscInputs) || s.Holds(KnownFormats.WiiUBuildable));
        var found = scan.Where(s => TargetResolver.Resolve(s, settings).Status switch
            {
                TargetStatus.Convert => true,
                TargetStatus.UnknownSystem => Natural(s),
                _ => s.Format == KnownFormats.Wua && otherWiiU,
            })
            .ToList();
        if (found.Count > 0) return found;
        var whole = ScanSet.Normalize(folder);
        return [new FileSet(whole, [whole], ConvertTargets.PackedFolder)];
    }

    /// <summary>
    /// Already in its final form (a CHD, an RVZ, a .wua): a folder scan leaves these out, since their only
    /// conversion is back. Given on their own they're taken, ready to extract.
    /// </summary>
    public static bool IsFinished(FileSet s) =>
        s.Format == KnownFormats.Chd || s.Format == KnownFormats.Wua || Path.GetExtension(s.Primary).Equals(".rvz", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Shell-style patterns for --include and --exclude: <c>*</c> within a name, <c>**</c> across folders, <c>?</c> one
/// character. A pattern without a slash matches the file's name; one with a slash matches the end of its path.
/// Case-insensitive, with either slash.
/// </summary>
public static class Glob
{
    public static Regex ToRegex(string pattern)
    {
        var p = pattern.Replace('\\', '/').Trim();
        var named = !p.Contains('/');
        var sb = new System.Text.StringBuilder(named ? "(^|/)" : p.StartsWith('/') ? "^" : "(^|/)");
        for (var i = 0; i < p.Length; i++)
        {
            var ch = p[i];
            if (ch == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                i++;
                if (i + 1 < p.Length && p[i + 1] == '/') { i++; sb.Append("(.*/)?"); }
                else sb.Append(".*");
            }
            else if (ch == '*') sb.Append("[^/]*");
            else if (ch == '?') sb.Append("[^/]");
            else if (ch == '/' && i == 0) { }
            else sb.Append(Regex.Escape(ch.ToString()));
        }
        sb.Append(p.EndsWith('/') ? "" : "(/.*)?$");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool Matches(Regex regex, string path) => regex.IsMatch(path.Replace('\\', '/'));

    public static bool Matches(string pattern, string path) => Matches(ToRegex(pattern), path);
}
