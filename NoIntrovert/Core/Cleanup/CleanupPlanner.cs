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
    /// <summary>A new library (FR-8): copied into it, under its database name when it has one; the original stays.</summary>
    Copy,
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

    /// <summary>The game's system (TargetFormats.json), for SELECTED FILE; null when it's unknown.</summary>
    public string? SystemId { get; init; }

    /// <summary>
    /// FR-8: the new library's folder when the plan builds one. The step then
    /// writes only there (its trash too) and never changes its sources.
    /// </summary>
    public string? Destination { get; init; }

    /// <summary>Whether the step writes into a new library and leaves its sources as they are.</summary>
    public bool Copies => Destination is not null;

    /// <summary>The library folder this step writes in: its trash goes there.</summary>
    public string WriteRoot => Destination ?? Root;

    /// <summary>For a conversion: what each source becomes (the converter's own name → the database name).</summary>
    public Dictionary<string, string> Renames { get; init; } = new(PathNames.Comparer);

    /// <summary>For a conversion: the target the Convert page would pick.</summary>
    public TargetKind ConvertTo { get; init; }

    /// <summary>For an ES-DE multi-disc game: the noload.txt to write in its hidden/ folder, so ES-DE skips the discs.</summary>
    public string? SkipFile { get; init; }

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
/// With a new library (FR-8) the same plan is made in another folder
/// instead: every game is converted or copied into it, named and laid out the
/// same way, and the Library itself is left as it is. A duplicate isn't copied;
/// files that aren't matched are copied as they are, or left out.
/// </summary>
public static class CleanupPlanner
{
    /// <param name="newLibrary">FR-8: the folder to build a new library in; null tidies the Library in place.</param>
    /// <param name="copyUnmatched">FR-8: copy files that aren't matched (unknown, unchecked, damaged) as they are, rather than leave them out.</param>
    public static CleanupPlan Plan(IReadOnlyList<Game> games, AppSettings settings, MatchStore matches, string? newLibrary = null, bool copyUnmatched = true)
    {
        if (newLibrary is not null) newLibrary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newLibrary));
        var steps = new List<CleanupStep>();
        foreach (var game in games) PlanGame(game, settings, matches, newLibrary, copyUnmatched, steps);
        MarkClashes(steps);
        return new CleanupPlan(steps);
    }

    private static void PlanGame(Game game, AppSettings settings, MatchStore matches, string? dest, bool copyUnmatched, List<CleanupStep> steps)
    {
        var copy = dest is not null;
        CleanupStep Step(CleanupKind kind, LibraryItem item, IReadOnlyList<string> sources, string? target, string reason, long saved = 0,
            TargetKind convertTo = TargetKind.Leave, string? skipFile = null) =>
            new() { Kind = kind, GameKey = game.Key, Game = game.Title, SystemId = game.System, Sources = sources, Target = target, Reason = reason,
                    BytesSaved = saved, Root = item.Root, ConvertTo = convertTo, SkipFile = skipFile, Destination = dest };

        var system = TargetFormats.ById(game.System);

        // What isn't touched in place: in a new library it's copied as it is (in its system folder when it has one), or left out.
        void Leave(LibraryItem item, string reason, bool tick = true)
        {
            if (!copy || !copyUnmatched)
            {
                steps.Add(Step(CleanupKind.Untouched, item, item.Members, null, copy ? $"{reason}: not copied" : reason));
                return;
            }
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Path));
            var target = system is not null && !system.Follows("R5") ? Path.Combine(FolderFor(item, system, settings, dest), name) : Mirror(item, item.Path, dest);
            var step = Step(CleanupKind.Copy, item, item.Members, target, $"{reason}: copied as it is");
            step.Enabled = tick;
            steps.Add(step);
        }

        var handled = new HashSet<LibraryItem>();

        // FR-7: the same disc more than once in its final form: the best copy stays (in a new library, only it is copied).
        foreach (var copies in game.Items.Where(i => i.IsFinal).GroupBy(i => (i.Disc, i.FormatId)).Where(g => g.Count() > 1))
        {
            var keep = copies.OrderByDescending(i => i.FormatStatus == TargetStatus.Optimal)
                .ThenByDescending(i => matches.Get(i.Path)?.Status is MatchStatus.Verified or MatchStatus.NameDiffers)
                .ThenByDescending(i => InLayout(i, settings))
                .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .First();
            foreach (var dup in copies.Where(i => i != keep))
            {
                steps.Add(copy
                    ? Step(CleanupKind.Untouched, dup, dup.Members, null, $"A copy of {Path.GetFileName(keep.Path)}: not copied")
                    : Step(CleanupKind.RemoveDuplicate, dup, dup.Members, null, $"A copy of {Path.GetFileName(keep.Path)}", dup.Size));
                handled.Add(dup);
            }
        }

        var items = game.Items.Where(i => !handled.Contains(i)).ToList();
        if (system is null || system.Follows("R5"))
        {
            foreach (var i in items) Leave(i, system is null ? "Unknown system" : "Arcade romset (never changed)");
            return;
        }

        // FR-6: games that have to be folders (Wii U titles, PS3 games) move whole into their system folder under
        // their own name; there's no DAT entry to rename them by. They're never looked inside or flattened.
        foreach (var i in items.Where(IsFolderGame).ToList())
        {
            if (settings.Library.Layout != LibraryLayout.EsDe || EsdeSystems.FolderOf(system) is null) break;
            items.Remove(i);
            var target = Path.Combine(FolderFor(i, system, settings, dest), FolderGameName(i));
            if (!copy && SamePath(target, i.Path) && Path.GetFileName(target) == Path.GetFileName(i.Path)) continue;
            steps.Add(Step(copy ? CleanupKind.Copy : CleanupKind.Move, i, [i.Path], target,
                $"{(copy ? "Copied" : "Moved")} whole into {RelativeFolder(dest ?? i.Root, target)}: this game is a folder"));
        }

        // Only what Verify & match recognised is renamed or moved; the rest is listed.
        var matched = new List<(LibraryItem Item, string Name)>();
        foreach (var i in items)
        {
            var m = matches.Get(i.Path);
            if (m?.Game is { } name && m.Status is MatchStatus.Verified or MatchStatus.NameDiffers) matched.Add((i, name));
            else Leave(i, m switch
            {
                null => "Not checked against the databases yet",
                { Status: MatchStatus.Damaged } => "Damaged",
                _ => m.Text,
            }, tick: m?.Status != MatchStatus.Damaged);
        }

        var esde = settings.Library.Layout == LibraryLayout.EsDe;

        // FR-7 in R7 form: two or more discs to convert together, with an .m3u in the system folder and the
        // discs in its hidden/ folder (in both layouts; ES-DE is told to skip hidden/ with a noload.txt).
        var discs = matched.Where(d => d.Item.Disc.Length > 0 && d.Item.FormatStatus == TargetStatus.Convert && d.Item.Target is TargetKind.Chd or TargetKind.Rvz).ToList();
        if (discs.Select(d => d.Item.Disc).Distinct().Count() >= 2)
        {
            var first = discs[0].Item;
            var folder = FolderFor(first, system, settings, dest);
            var ext = first.Target == TargetKind.Rvz ? ".rvz" : ".chd";
            var playlist = PlaylistIn(folder, game.Title);
            var step = Step(CleanupKind.Convert, first, discs.Select(d => d.Item.Path).ToList(), playlist,
                $"{discs.Count} discs → {ext} in {MultiDiscFolderName(settings)}/, with {game.Title}.m3u beside it",
                Estimate(discs.Sum(d => d.Item.Size), ext), first.Target, esde ? Path.Combine(folder, MultiDiscFolderName(settings), EsdeSkipFile) : null);
            foreach (var (item, name) in discs) step.Renames[Path.GetFileNameWithoutExtension(item.Path) + ext] = name + ext;
            steps.Add(step);
            foreach (var d in discs) handled.Add(d.Item);
        }

        // ES-DE: discs already converted move together into hidden/ in their system folder, and the playlist is
        // written again beside it. This also tidies a <Game>.m3u/ folder an earlier version of the wizard made.
        // A new library gets them this way in either layout, so it has their playlist too.
        var ready = matched.Where(d => !handled.Contains(d.Item) && d.Item.Disc.Length > 0 && d.Item.FormatStatus != TargetStatus.Convert
                                       && d.Item.Members.Count == 1 && File.Exists(d.Item.Path)).ToList();
        if ((esde || copy) && ready.Select(d => d.Item.Disc).Distinct().Count() >= 2)
        {
            var first = ready[0].Item;
            var hidden = MultiDiscFolderName(settings);
            var folder = esde ? FolderFor(first, system, settings, dest) : Mirror(first, SystemFolderOfDisc(first.Path, hidden), dest);
            var playlist = PlaylistIn(folder, game.Title);
            var step = Step(copy ? CleanupKind.Copy : CleanupKind.Move, first, ready.Select(d => d.Item.Path).ToList(), playlist,
                $"Into {hidden}/, with {game.Title}.m3u beside it", skipFile: esde ? Path.Combine(folder, hidden, EsdeSkipFile) : null);
            // Each disc's place, relative to the system folder: hidden/<database name>.
            foreach (var (item, name) in ready) step.Renames[Path.GetFileName(item.Path)] = $"{hidden}/{Matcher.ExpectedName(item.Path, name)}";
            foreach (var d in ready) handled.Add(d.Item);
            var inPlace = !copy && ready.All(d => SamePath(Path.GetDirectoryName(d.Item.Path)!, Path.Combine(folder, hidden))
                                                  && Path.GetFileName(d.Item.Path) == step.Renames[Path.GetFileName(d.Item.Path)][(hidden.Length + 1)..]);
            if (!(inPlace && File.Exists(playlist) && File.Exists(Path.Combine(folder, hidden, EsdeSkipFile)))) steps.Add(step);
        }

        foreach (var (item, name) in matched.Where(d => !handled.Contains(d.Item)))
        {
            var folder = FolderFor(item, system, settings, dest);
            // R7: the discs of a multi-disc game live in hidden/, beside their playlist.
            if (esde && game.Discs >= 2 && item.Disc.Length > 0)
                folder = Path.Combine(folder, MultiDiscFolderName(settings));
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
            var renamed = newName != Path.GetFileName(item.Path);
            if (copy)
            {
                steps.Add(Step(CleanupKind.Copy, item, item.Members, target, renamed ? "Named as in the database" : "Already named as in the database"));
                continue;
            }
            if (SamePath(target, item.Path) && Path.GetFileName(target) == Path.GetFileName(item.Path)) continue;
            var sameFolder = SamePath(Path.GetDirectoryName(Path.GetFullPath(target))!, Path.GetDirectoryName(Path.GetFullPath(item.Path))!);
            steps.Add(Step(sameFolder ? CleanupKind.Rename : CleanupKind.Move, item, item.Members, target,
                sameFolder ? "Named as in the database" : $"Into {Folder(settings) ?? "its folder"}{(renamed ? ", named as in the database" : "")}"));
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
    /// With a new library (FR-8) the same folder is made there: <c>roms/psx</c> in it, or the item's folder below its library folder.
    /// </summary>
    public static string FolderFor(LibraryItem item, SystemInfo system, AppSettings settings, string? newLibrary = null) =>
        settings.Library.Layout == LibraryLayout.EsDe && EsdeSystems.FolderOf(system) is { } esde
            ? Path.Combine(RomsFolder(newLibrary ?? item.Root), esde)
            : Mirror(item, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(item.Path))!, newLibrary);

    /// <summary>
    /// FR-8: where <paramref name="path"/> (in the item's library folder) goes in the new library:
    /// the same path below it. Itself when there's no new library.
    /// </summary>
    public static string Mirror(LibraryItem item, string path, string? newLibrary)
    {
        if (newLibrary is null) return path;
        var relative = Path.GetRelativePath(item.Root, path);
        if (relative.StartsWith("..") || Path.IsPathRooted(relative)) relative = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return Path.GetFullPath(Path.Combine(newLibrary, relative));
    }

    /// <summary>The folder a converted disc's playlist belongs in: its own, or the one above when the disc is in hidden/ already.</summary>
    private static string SystemFolderOfDisc(string disc, string hidden)
    {
        var dir = Path.GetDirectoryName(disc)!;
        return string.Equals(Path.GetFileName(dir), hidden, PathNames.Comparison) ? Path.GetDirectoryName(dir)! : dir;
    }

    /// <summary>
    /// R7: a multi-disc game's playlist, directly in its system folder
    /// (<c>psx/Game.m3u</c>); its discs are in <see cref="MultiDiscFolderName"/> beside it.
    /// </summary>
    public static string PlaylistIn(string systemFolder, string title) => Path.Combine(systemFolder, title + ".m3u");

    /// <summary>R7: the folder beside a playlist that holds its discs ("hidden" unless Settings say otherwise).</summary>
    public static string MultiDiscFolderName(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.TargetFormats.MultiDiscFolder) ? "hidden" : settings.TargetFormats.MultiDiscFolder.Trim();

    /// <summary>
    /// The empty file that tells ES-DE to skip a folder (its user guide: "noload.txt"), so the discs in
    /// hidden/ aren't listed as games of their own beside their .m3u.
    /// </summary>
    public const string EsdeSkipFile = "noload.txt";

    /// <summary>The games that are folders and stay folders: Wii U NUS titles and title folders, PS3 games.</summary>
    public static bool IsFolderGame(LibraryItem item) =>
        item.FormatId == KnownFormats.Nus.Id || item.FormatId == KnownFormats.WiiUFolder.Id || item.FormatId == KnownFormats.Ps3Folder.Id;

    /// <summary>A folder game's name in its system folder: as it is, except that a PS3 game gets the .ps3 ES-DE needs.</summary>
    private static string FolderGameName(LibraryItem item)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Path));
        return item.FormatId == KnownFormats.Ps3Folder.Id && !name.EndsWith(".ps3", StringComparison.OrdinalIgnoreCase) ? name + ".ps3" : name;
    }

    private static string RelativeFolder(string root, string target) =>
        Path.GetRelativePath(root, Path.GetDirectoryName(target)!).Replace('\\', '/') + "/";

    /// <summary>Whether two paths name the same file or folder, as the OS compares them.</summary>
    public static bool SamePath(string a, string b) => PathNames.Same(a, b);

    private static bool InLayout(LibraryItem item, AppSettings settings) =>
        TargetFormats.ById(item.System) is { } system
        && FolderFor(item, system, settings) is var folder
        && (SamePath(Path.GetDirectoryName(item.Path)!, folder)
            || (item.Disc.Length > 0 && SamePath(Path.GetDirectoryName(item.Path)!, Path.Combine(folder, MultiDiscFolderName(settings)))));

    private static string? Folder(AppSettings settings) => settings.Library.Layout == LibraryLayout.EsDe ? "its ES-DE system folder" : null;

    /// <summary>The extension a conversion's result has; null for targets the wizard leaves to the Convert page (unpacking, extracting).</summary>
    private static string? ConvertedExtension(TargetKind target) => target switch
    {
        TargetKind.Chd => ".chd",
        TargetKind.Rvz => ".rvz",
        TargetKind.Wua => ".wua",
        TargetKind.Cci => ".cci",
        TargetKind.Zcci => ".zcci",
        TargetKind.Nsz => ".nsz",
        TargetKind.Xcz => ".xcz",
        TargetKind.Nsp => ".nsp",
        TargetKind.Xci => ".xci",
        TargetKind.Xiso => ".iso",
        TargetKind.XboxCci => ".cci",
        TargetKind.Zar => ".zar",
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
                    Kind = CleanupKind.Clash, GameKey = s.GameKey, Game = s.Game, SystemId = s.SystemId, Sources = s.Sources, Target = s.Target, Root = s.Root, Destination = s.Destination,
                    Reason = $"{Path.GetFileName(target)} {(exists ? "already exists" : "is another step's target too")}: nothing is overwritten",
                    Enabled = false,
                };
        }
    }
}
