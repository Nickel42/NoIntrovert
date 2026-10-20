using NoIntrovert.Core.Dats;
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
    public Dictionary<string, string> Renames { get; init; } = new(StringComparer.OrdinalIgnoreCase);

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
/// isn't optimal, rename to the database name, move into the layout (FR-6),
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

        // FR-7 in R7 form: two or more discs to convert together, into hidden/ with an .m3u.
        var discs = matched.Where(d => d.Item.Disc.Length > 0 && d.Item.FormatStatus == TargetStatus.Convert && d.Item.Target is TargetKind.Chd or TargetKind.Rvz).ToList();
        if (discs.Select(d => d.Item.Disc).Distinct().Count() >= 2)
        {
            var first = discs[0].Item;
            var folder = FolderFor(first, system, settings);
            var ext = first.Target == TargetKind.Rvz ? ".rvz" : ".chd";
            var step = Step(CleanupKind.Convert, first, discs.Select(d => d.Item.Path).ToList(), Path.Combine(folder, game.Title + ".m3u"),
                $"{discs.Count} discs → {Folder(settings)}/{ext}, with a playlist", Estimate(discs.Sum(d => d.Item.Size), ext), first.Target);
            foreach (var (item, name) in discs) step.Renames[Path.GetFileNameWithoutExtension(item.Path) + ext] = name + ext;
            steps.Add(step);
            foreach (var d in discs) handled.Add(d.Item);
        }

        foreach (var (item, name) in matched.Where(d => !handled.Contains(d.Item)))
        {
            var folder = FolderFor(item, system, settings);
            // R7: the discs of a multi-disc game live in the hidden folder beside its playlist.
            if (settings.Library.Layout == LibraryLayout.EsDe && game.Discs >= 2 && item.Disc.Length > 0)
                folder = Path.Combine(folder, MultiDiscFolder(settings));
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

    /// <summary>FR-6: the system's ES-DE folder in the item's library folder, or where it is now.</summary>
    public static string FolderFor(LibraryItem item, SystemInfo system, AppSettings settings) =>
        settings.Library.Layout == LibraryLayout.EsDe && system.Esde is { } esde
            ? Path.Combine(item.Root, esde)
            : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(item.Path))!;

    public static string MultiDiscFolder(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.TargetFormats.MultiDiscFolder) ? "hidden" : settings.TargetFormats.MultiDiscFolder.Trim();

    /// <summary>Whether two paths name the same file or folder: without case on Windows and macOS, whose file systems ignore it.</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static bool InLayout(LibraryItem item, AppSettings settings) =>
        TargetFormats.ById(item.System) is { } system
        && string.Equals(Path.GetDirectoryName(item.Path), FolderFor(item, system, settings), StringComparison.OrdinalIgnoreCase);

    private static string? Folder(AppSettings settings) => settings.Library.Layout == LibraryLayout.EsDe ? "its system folder" : null;

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
            .SelectMany(s => s.Sources).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
