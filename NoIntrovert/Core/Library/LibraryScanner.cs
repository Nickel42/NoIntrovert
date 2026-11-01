using System.Text.RegularExpressions;
using LibChdKit;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.WiiU;

namespace NoIntrovert.Core.Library;

/// <summary>
/// Looks through the library folders and works out, for each input, which
/// system it's for (a registry id), its title, which game it belongs to and
/// where it stands against its system's target format. Inputs whose
/// files haven't changed since the last scan (same size and date) are taken
/// from the old index without being read again, archives included.
/// </summary>
public static partial class LibraryScanner
{
    public static LibraryIndex Scan(IReadOnlyList<string> roots, LibraryIndex? previous, IProgress<int>? progress, CancellationToken token,
        AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        // Statuses depend on your target overrides: when they changed, every item is described again
        // (archives still aren't reopened: their contents are remembered below).
        var key = TargetSettingsKey(settings);
        var reuse = previous?.TargetSettingsKey == key;
        var old = (previous?.Items ?? []).GroupBy(i => i.Path, PathNames.Comparer)
            .ToDictionary(g => g.Key, g => g.First(), PathNames.Comparer);
        // Archives that haven't changed keep what they were seen to hold, so they're not opened again.
        foreach (var item in old.Values.Where(i => i.Inside is not null))
            ArchivePeek.Remember(item.Path, item.Size, item.ModifiedTicks, item.Inside!);

        var index = new LibraryIndex { ScannedUtc = DateTime.UtcNow, TargetSettingsKey = key };
        Chdman? chdman = null;
        try
        {
            foreach (var root in roots.Select(ScanSet.Normalize).Distinct(PathNames.Comparer))
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(root))
                {
                    index.MissingFolders.Add(root);
                    continue;
                }
                List<FileSet> sets;
                try { sets = FormatRegistry.Default.Scan(root, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    index.MissingFolders.Add(root);
                    continue;
                }
                foreach (var set in sets)
                {
                    token.ThrowIfCancellationRequested();
                    var (size, ticks) = Stamp(set);
                    if (reuse && old.TryGetValue(set.Primary, out var known) && known.Size == size && known.ModifiedTicks == ticks
                        && known.FormatId == set.Format.Id && string.Equals(known.Root, root, PathNames.Comparison))
                        index.Items.Add(known);
                    else
                        index.Items.Add(Describe(set, root, size, ticks, settings, ref chdman));
                    progress?.Report(index.Items.Count);
                }
            }
        }
        finally
        {
            chdman?.Dispose();
        }
        return index;
    }

    /// <summary>The settings a status depends on, as text: an index made under others is described again.</summary>
    public static string TargetSettingsKey(AppSettings settings) =>
        System.Text.Json.JsonSerializer.Serialize(new { settings.TargetFormats.Overrides, settings.TargetFormats.DsBareAboveMb, settings.Nintendo.Target });

    /// <summary>Total size and newest change of an input's files (a cue and its bins, a title folder's files).</summary>
    private static (long Size, long Ticks) Stamp(FileSet set)
    {
        long size = 0, ticks = 0;
        foreach (var m in set.Members)
        {
            try
            {
                if (Directory.Exists(m))
                {
                    var dir = new DirectoryInfo(m);
                    foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        size += f.Length;
                        ticks = Math.Max(ticks, f.LastWriteTimeUtc.Ticks);
                    }
                }
                else
                {
                    var f = new FileInfo(m);
                    if (!f.Exists) continue;
                    size += f.Length;
                    ticks = Math.Max(ticks, f.LastWriteTimeUtc.Ticks);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (size, ticks);
    }

    internal static LibraryItem Describe(FileSet set, string root, long size, long ticks, AppSettings settings, ref Chdman? chdman)
    {
        var system = SystemOf(set, root, ref chdman);
        var (title, disc) = TitleOf(set.Primary);
        var key = $"{system ?? "unknown"}|{title}";

        // Wii U: base game, update and DLC share the low half of their title ID, so they're one game.
        if (system == "wii-u" && WiiUIdentity(set) is { } wiiu)
        {
            key = $"wii-u|{wiiu.BaseId:X16}";
            if (wiiu.Name.Length > 0) title = wiiu.Name;
            disc = "";
        }
        var plan = TargetResolver.Resolve(set, TargetFormats.ById(system), settings);
        var target = ConvertTargets.For(set, settings, plan).Default.Kind;
        return new LibraryItem(set.Primary, set.Members, set.Format.Id, set.Inside?.Select(f => f.Id).ToList(), size, ticks,
            root, system, title, key.ToLowerInvariant(), disc, target, plan.Status, plan.Text);
    }

    // ---- Titles ----

    /// <summary>
    /// The game's title from a file name, with disc markers taken off (and
    /// returned separately), so "Game (USA) (Disc 2).cue" is disc 2 of
    /// "Game (USA)". A generic name (disc.gdi, track01.bin) uses its folder's.
    /// </summary>
    public static (string Title, string Disc) TitleOf(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var stem = Directory.Exists(path) ? name : Path.GetFileNameWithoutExtension(name);
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) || stem.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        if (GenericName().IsMatch(stem) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is { } parent)
            stem = Path.GetFileName(parent);
        var disc = "";
        var m = DiscMarker().Match(stem);
        if (m.Success)
        {
            disc = $"Disc {int.Parse(m.Groups["n"].Value)}";
            stem = stem.Remove(m.Index, m.Length);
        }
        return (Spaces().Replace(stem, " ").Trim(), disc);
    }

    [GeneratedRegex(@"^(disc|disk|game|image|rom|default|cd|dvd|track\s*0*1|\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericName();

    [GeneratedRegex(@"\s*(?:[\(\[](?:disc|disk|cd|dvd)\s*(?<n>\d+)(?:\s*of\s*\d+)?[\)\]]|-\s*(?:disc|disk|cd)\s*(?<n>\d+)\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex DiscMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // ---- Systems ----

    /// <summary>
    /// The registry id of the input's system (see <see cref="SystemDetector"/>),
    /// or null for Unknown system. A CHD whose folder says nothing is opened to
    /// find a Dreamcast GD-ROM.
    /// </summary>
    public static string? SystemOf(FileSet set, string root, ref Chdman? chdman)
    {
        Chdman? opened = chdman;
        var system = SystemDetector.SystemOf(set, root, chd =>
        {
            try
            {
                opened ??= new Chdman();
                return opened.Info(chd, hunkStats: false).Metadata.Any(m => m.TagString == "CHGD") ? "dreamcast" : null;
            }
            catch (Exception ex) when (ex is ChdmanException or IOException or DllNotFoundException) { return null; }
        });
        chdman = opened;
        return system;
    }

    /// <summary>A Wii U input's base title ID and name, where they can be read without keys.</summary>
    private static (ulong BaseId, string Name)? WiiUIdentity(FileSet set)
    {
        try
        {
            if (set.Format == KnownFormats.Nus)
            {
                var tmd = WiiUTicket.ParseTmd(File.ReadAllBytes(Path.Combine(set.Primary, "title.tmd")));
                return (0x0005000000000000UL | (tmd.TitleId & 0xFFFFFFFFUL), "");
            }
            if (set.Format == KnownFormats.WiiUFolder || set.Format == KnownFormats.Wua)
            {
                var titles = WiiUTitles.Read(set.Primary);
                if (titles.Count == 0) return null;
                var main = titles.FirstOrDefault(t => t.Kind == WiiUTitleKind.Game) ?? titles[0];
                return (main.BaseId, titles.FirstOrDefault(t => t.Name.Length > 0)?.Name ?? "");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or KeyNotFoundException or NotSupportedException or FormatException) { }
        return null;
    }
}
