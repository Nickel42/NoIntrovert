using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Esde;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Library;

namespace NoIntrovert.Core.Cleanup;

/// <summary>What a step of the Cleanup wizard does (docs/requirements/cleanup-wizard.md, FR-1).</summary>
public enum CleanupKind
{
    Convert,
    Rename,
    Move,
    RemoveDuplicate,
    /// <summary>A step that would overwrite something: shown, never run (FR-5).</summary>
    Clash,
    /// <summary>Listed only: unknown system, not matched, an arcade romset… (FR-1).</summary>
    Untouched,
}

/// <summary>
/// One step of the plan: what it does to which files, where they end up,
/// and why. <see cref="Enabled"/> is the dry run's tick box (FR-2).
/// <see cref="Renames"/> gives a multi-disc conversion each disc's name.
/// </summary>
public sealed class CleanupStep
{
    public required CleanupKind Kind { get; init; }
    public required string GameKey { get; init; }
    public required string Game { get; init; }
    public required IReadOnlyList<string> Sources { get; init; }
    public string? Target { get; set; }
    public string Reason { get; set; } = "";
    public long BytesSaved { get; init; }
    public bool Enabled { get; set; } = true;

    /// <summary>The library folder the files are in: the trash goes there, on the same drive (FR-4).</summary>
    public required string Root { get; init; }

    /// <summary>For a conversion: what each source becomes (the converter's own name → the database name).</summary>
    public Dictionary<string, string> Renames { get; init; } = new(PathNames.Comparer);

    /// <summary>For a conversion: the target the Convert page would pick.</summary>
    public TargetKind ConvertTo { get; init; }

    public bool Runs => Enabled && Kind is not (CleanupKind.Clash or CleanupKind.Untouched);
}

/// <summary>The dry run: every step, in order, grouped by game.</summary>
public sealed class CleanupPlan(List<CleanupStep> steps)
{
    public IReadOnlyList<CleanupStep> Steps { get; } = steps;

    /// <summary>About how much the ticked steps free: duplicates exactly, conversions by typical ratios.</summary>
    public long BytesSaved => Steps.Where(s => s.Runs).Sum(s => s.BytesSaved);

    public int Count(CleanupKind kind) => Steps.Count(s => s.Kind == kind);
}

/// <summary>
/// Turns games from the Library into the wizard's plan (FR-1): convert what
/// isn't optimal, rename to the database name, move into the layout (FR-6:
/// ES-DE's roms/&lt;system&gt;/ folders, or where they are now),
/// remove duplicates (FR-7). Only files Verify &amp; match has matched are
/// touched; everything else is listed as untouched. Name clashes become
/// steps that don't run (FR-5).
/// </summary>
public static class CleanupPlanner
{
    public static CleanupPlan Plan(IReadOnlyList<Game> games, AppSettings settings, MatchStore matches)
    {
        var steps = new List<CleanupStep>();
        foreach (var game in games) PlanGame(game, settings, matches, steps);
        MarkClashes(steps);
        return new CleanupPlan(steps);
    }

    private static void PlanGame(Game game, AppSettings settings, MatchStore matches, List<CleanupStep> steps)
    {
        CleanupStep Step(CleanupKind kind, LibraryItem item, IReadOnlyList<string> sources, string? target, string reason, long saved = 0,
            TargetKind convertTo = TargetKind.Leave) =>
            new() { Kind = kind, GameKey = game.Key, Game = game.Title, Sources = sources, Target = target, Reason = reason,
                    BytesSaved = saved, Root = item.Root, ConvertTo = convertTo };

        var handled = new HashSet<LibraryItem>();

        // FR-7: the same disc more than once in its final form: the best copy stays.
        foreach (var copies in game.Items.Where(i => i.IsFinal).GroupBy(i => (i.Disc, i.FormatId)).Where(g => g.Count() > 1))
        {
            var keep = copies.OrderByDescending(i => i.FormatStatus == TargetStatus.Optimal)
                .ThenByDescending(i => matches.Get(i.Path)?.Status is MatchStatus.Verified or MatchStatus.NameDiffers)
                .ThenByDescending(i => InLayout(i, settings))
                .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .First();
            foreach (var dup in copies.Where(i => i != keep))
            {
                steps.Add(Step(CleanupKind.RemoveDuplicate, dup, dup.Members, null, $"A copy of {Path.GetFileName(keep.Path)}", dup.Size));
                handled.Add(dup);
            }
        }

        var system = TargetFormats.ById(game.System);
        var items = game.Items.Where(i => !handled.Contains(i)).ToList();
        if (system is null || system.Follows("R5"))
        {
            foreach (var i in items)
                steps.Add(Step(CleanupKind.Untouched, i, i.Members, null, system is null ? "Unknown system" : "Arcade romset (never changed)"));
            return;
        }

        // FR-6: games that have to be folders (Wii U titles, PS3 games) move whole into their system folder under
        // their own name; there's no DAT entry to rename them by. They're never looked inside or flattened.
        foreach (var i in items.Where(IsFolderGame).ToList())
        {
            if (settings.Library.Layout != LibraryLayout.EsDe || EsdeSystems.FolderOf(system) is null) break;
            items.Remove(i);
            var target = Path.Combine(FolderFor(i, system, settings), FolderGameName(i));
            if (SamePath(target, i.Path) && Path.GetFileName(target) == Path.GetFileName(i.Path)) continue;
            steps.Add(Step(CleanupKind.Move, i, [i.Path], target, $"Moved whole into {RelativeFolder(i, target)}: this game is a folder"));
        }

        // Only what Verify & match recognised is renamed or moved; the rest is listed.
        var matched = new List<(LibraryItem Item, string Name)>();
        foreach (var i in items)
        {
            var m = matches.Get(i.Path);
            if (m?.Game is { } name && m.Status is MatchStatus.Verified or MatchStatus.NameDiffers) matched.Add((i, name));
            else steps.Add(Step(CleanupKind.Untouched, i, i.Members, null, m switch
            {
                null => "Not checked against the databases yet",
                { Status: MatchStatus.Damaged } => "Damaged",
                _ => m.Text,
            }));
        }

        var esde = settings.Library.Layout == LibraryLayout.EsDe;

        // FR-7 in R7 form: two or more discs to convert together, with an .m3u. In the ES-DE
        // layout the discs and the playlist share a <Game>.m3u folder; otherwise the discs go in hidden/.
        var discs = matched.Where(d => d.Item.Disc.Length > 0 && d.Item.FormatStatus == TargetStatus.Convert && d.Item.Target is TargetKind.Chd or TargetKind.Rvz).ToList();
        if (discs.Select(d => d.Item.Disc).Distinct().Count() >= 2)
        {
            var first = discs[0].Item;
            var folder = FolderFor(first, system, settings);
            var ext = first.Target == TargetKind.Rvz ? ".rvz" : ".chd";
            var playlist = esde ? PlaylistIn(folder, game.Title) : Path.Combine(folder, game.Title + ".m3u");
            var step = Step(CleanupKind.Convert, first, discs.Select(d => d.Item.Path).ToList(), playlist,
                $"{discs.Count} discs → {ext}, {(esde ? $"in {game.Title}.m3u/ with its playlist" : "with a playlist")}",
                Estimate(discs.Sum(d => d.Item.Size), ext), first.Target);
            foreach (var (item, name) in discs) step.Renames[Path.GetFileNameWithoutExtension(item.Path) + ext] = name + ext;
            steps.Add(step);
            foreach (var d in discs) handled.Add(d.Item);
        }

        // ES-DE: discs already converted move together into their <Game>.m3u folder, whose playlist is written again.
        var ready = matched.Where(d => !handled.Contains(d.Item) && d.Item.Disc.Length > 0 && d.Item.FormatStatus != TargetStatus.Convert
                                       && d.Item.Members.Count == 1 && File.Exists(d.Item.Path)).ToList();
        if (esde && ready.Select(d => d.Item.Disc).Distinct().Count() >= 2)
        {
            var first = ready[0].Item;
            var playlist = PlaylistIn(FolderFor(first, system, settings), game.Title);
            var folder = Path.GetDirectoryName(playlist)!;
            var step = Step(CleanupKind.Move, first, ready.Select(d => d.Item.Path).ToList(), playlist, $"Into {game.Title}.m3u/ with its playlist");
            foreach (var (item, name) in ready) step.Renames[Path.GetFileName(item.Path)] = Matcher.ExpectedName(item.Path, name);
            foreach (var d in ready) handled.Add(d.Item);
            var inPlace = ready.All(d => SamePath(Path.GetDirectoryName(d.Item.Path)!, folder)
                                         && Path.GetFileName(d.Item.Path) == step.Renames[Path.GetFileName(d.Item.Path)]);
            if (!(inPlace && File.Exists(playlist))) steps.Add(step);
        }

        foreach (var (item, name) in matched.Where(d => !handled.Contains(d.Item)))
        {
            var folder = FolderFor(item, system, settings);
            // R7: the discs of a multi-disc game live beside their playlist.
            if (esde && game.Discs >= 2 && item.Disc.Length > 0)
                folder = Path.GetDirectoryName(PlaylistIn(folder, game.Title))!;
            if (item.FormatStatus == TargetStatus.Convert && ConvertedExtension(item.Target) is { } ext)
            {
                steps.Add(Step(CleanupKind.Convert, item, [item.Path], Path.Combine(folder, name + ext),
                    $"{item.FormatStatusText}, named as in the database", Estimate(item.Size, ext), item.Target));
                continue;
            }
            // A set of files (a cue and its bins) moves together under its own names; a single file takes the database name.
            var single = item.Members.Count == 1;
            var newName = single && !Directory.Exists(item.Path) ? Matcher.ExpectedName(item.Path, name) : Path.GetFileName(item.Path);
            var target = Path.Combine(folder, newName);
            if (SamePath(target, item.Path) && Path.GetFileName(target) == Path.GetFileName(item.Path)) continue;
            var sameFolder = SamePath(Path.GetDirectoryName(Path.GetFullPath(target))!, Path.GetDirectoryName(Path.GetFullPath(item.Path))!);
            steps.Add(Step(sameFolder ? CleanupKind.Rename : CleanupKind.Move, item, item.Members, target,
                sameFolder ? "Named as in the database" : $"Into {Folder(settings) ?? "its folder"}{(newName != Path.GetFileName(item.Path) ? ", named as in the database" : "")}"));
        }
    }

    /// <summary>The top-level folder of the ES-DE layout.</summary>
    public const string RomsFolderName = "roms";

    /// <summary>FR-6: <c>roms</c> in a library folder, or the library folder itself when it's called that already.</summary>
    public static string RomsFolder(string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(Path.GetFileName(trimmed), RomsFolderName, StringComparison.OrdinalIgnoreCase) ? trimmed : Path.Combine(trimmed, RomsFolderName);
    }

    /// <summary>
    /// FR-6: the system's ES-DE folder (<c>roms/psx</c>) in the item's library folder, or where it is now.
    /// The folder name comes from ES-DE's own list (<see cref="EsdeSystems"/>), never a made-up one.
    /// </summary>
    public static string FolderFor(LibraryItem item, SystemInfo system, AppSettings settings) =>
        settings.Library.Layout == LibraryLayout.EsDe && EsdeSystems.FolderOf(system) is { } esde
            ? Path.Combine(RomsFolder(item.Root), esde)
            : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(item.Path))!;

    /// <summary>
    /// R7 in ES-DE form: a multi-disc game's playlist inside a folder of the
    /// same name (<c>psx/Game.m3u/Game.m3u</c>), with its discs beside it.
    /// ES-DE shows such a folder as one game ("directories interpreted as files").
    /// </summary>
    public static string PlaylistIn(string systemFolder, string title) => Path.Combine(systemFolder, title + ".m3u", title + ".m3u");

    /// <summary>Whether <paramref name="playlist"/> is in a folder named after it (the ES-DE multi-disc folder).</summary>
    public static bool IsPlaylistFolder(string playlist) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(playlist)), Path.GetFileName(playlist), PathNames.Comparison);

    /// <summary>The games that are folders and stay folders: Wii U NUS titles and title folders, PS3 games.</summary>
    public static bool IsFolderGame(LibraryItem item) =>
        item.FormatId == KnownFormats.Nus.Id || item.FormatId == KnownFormats.WiiUFolder.Id || item.FormatId == KnownFormats.Ps3Folder.Id;

    /// <summary>A folder game's name in its system folder: as it is, except that a PS3 game gets the .ps3 ES-DE needs.</summary>
    private static string FolderGameName(LibraryItem item)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Path));
        return item.FormatId == KnownFormats.Ps3Folder.Id && !name.EndsWith(".ps3", StringComparison.OrdinalIgnoreCase) ? name + ".ps3" : name;
    }

    private static string RelativeFolder(LibraryItem item, string target) =>
        Path.GetRelativePath(item.Root, Path.GetDirectoryName(target)!).Replace('\\', '/') + "/";

    /// <summary>Whether two paths name the same file or folder, as the OS compares them.</summary>
    public static bool SamePath(string a, string b) => PathNames.Same(a, b);

    private static bool InLayout(LibraryItem item, AppSettings settings) =>
        TargetFormats.ById(item.System) is { } system
        && FolderFor(item, system, settings) is var folder
        && (SamePath(Path.GetDirectoryName(item.Path)!, folder)
            || (item.Disc.Length > 0 && SamePath(Path.GetDirectoryName(item.Path)!, Path.GetDirectoryName(PlaylistIn(folder, item.Title))!)));

    private static string? Folder(AppSettings settings) => settings.Library.Layout == LibraryLayout.EsDe ? "its ES-DE system folder" : null;

    /// <summary>The extension a conversion's result has; null for targets the wizard leaves to the Convert page (unpacking, extracting).</summary>
    private static string? ConvertedExtension(TargetKind target) => target switch
    {
        TargetKind.Chd => ".chd",
        TargetKind.Rvz => ".rvz",
        TargetKind.Wua => ".wua",
        TargetKind.SevenZip => ".7z",
        TargetKind.Zip => ".zip",
        _ => null,
    };

    /// <summary>A rough saving for the dry run, from typical ratios (CHD and RVZ about 60%, archives about 65%).</summary>
    private static long Estimate(long size, string ext) => (long)(size * (ext is ".chd" or ".rvz" ? 0.4 : ext is ".7z" or ".zip" ? 0.35 : 0.1));

    /// <summary>FR-5: a step whose target exists already, or that another step also writes, doesn't run.</summary>
    private static void MarkClashes(List<CleanupStep> steps)
    {
        var leaving = steps.Where(s => s.Kind is CleanupKind.Move or CleanupKind.Rename or CleanupKind.RemoveDuplicate or CleanupKind.Convert)
            .SelectMany(s => s.Sources).Select(Path.GetFullPath).ToHashSet(PathNames.Comparer);
        var claimed = new HashSet<string>(PathNames.Comparer);
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            // A playlist is rewritten, not a clash (R7: updated, not duplicated).
            if (s.Target is null || s.Kind is CleanupKind.Untouched or CleanupKind.RemoveDuplicate || s.Target.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.GetFullPath(s.Target);
            var exists = (File.Exists(target) || Directory.Exists(target)) && !leaving.Contains(target);
            if (exists || !claimed.Add(target))
                steps[i] = new CleanupStep
                {
                    Kind = CleanupKind.Clash, GameKey = s.GameKey, Game = s.Game, Sources = s.Sources, Target = s.Target, Root = s.Root,
                    Reason = $"{Path.GetFileName(target)} {(exists ? "already exists" : "is another step's target too")}: nothing is overwritten",
                    Enabled = false,
                };
        }
    }
}
