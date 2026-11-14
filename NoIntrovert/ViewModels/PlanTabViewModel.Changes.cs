using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Library;

namespace NoIntrovert.ViewModels;

/// <summary>What a row's CHANGES outcome is; the cell's style follows it.</summary>
public enum ChangesKind
{
    /// <summary>Not a plan page (or no plan yet).</summary>
    None,
    /// <summary>The file ends up at a path the plan worked out.</summary>
    Planned,
    /// <summary>The file hasn't been checked yet: the path is what's likely, its name comes after Verify.</summary>
    Guess,
    /// <summary>Already named and in place: nothing to do.</summary>
    Unchanged,
    /// <summary>Left alone (unknown system, not verified, damaged…).</summary>
    LeftAlone,
    /// <summary>Its steps' box is unticked: it stays as it is.</summary>
    Off,
    /// <summary>A step that would overwrite something: shown, never run.</summary>
    Clash,
    /// <summary>A duplicate that goes to the trash.</summary>
    Trash,
    /// <summary>The plan can't be made (a bad new-library folder…).</summary>
    Problem,
}

/// <summary>
/// A row's CHANGES: the outcome first (<paramref name="Target"/>: where the file ends up, relative to its
/// library folder, or why nothing happens), then the steps that get it there, in run order.
/// </summary>
public sealed record RowChanges(string Target, ChangesKind Kind, IReadOnlyList<string> Steps)
{
    /// <summary>"roms/psx/Crash (USA).chd (Convert · Move)", for logs and tests.</summary>
    public override string ToString() => Steps.Count == 0 ? Target : $"{Target} ({string.Join(" · ", Steps)})";
}

public abstract partial class PlanTabViewModel
{
    /// <summary>The steps' names, in the order a game's steps run.</summary>
    private static readonly string[] StepOrder = ["Verify", "Convert", "Rename", "Move", "Copy", "Remove"];

    /// <summary>
    /// A row's CHANGES from its planned steps: where the file ends up ("roms/psx/Crash (USA).chd") with the
    /// steps that take it there (Convert · Rename · Move), or why nothing happens to it.
    /// </summary>
    public static RowChanges ChangesOf(LibraryItem? item, IReadOnlyList<CleanupStep> steps)
    {
        if (item is null) return new("Not a file the plan can use", ChangesKind.LeftAlone, []);
        if (steps.Count == 0) return new("Already named and in place", ChangesKind.Unchanged, []);
        var runs = steps.Where(s => s.Runs).ToList();
        if (runs.Count == 0)
        {
            var first = steps[0];
            return first.Kind switch
            {
                CleanupKind.Clash => new($"Clash: {ClashText(first)}", ChangesKind.Clash, []),
                CleanupKind.Untouched => new($"Left alone: {Words.LowerFirst(first.Reason)}", ChangesKind.LeftAlone, []),
                _ => new("Stays as it is: its box is unticked", ChangesKind.Off, []),
            };
        }
        if (runs.Any(s => s.Kind == CleanupKind.RemoveDuplicate))
            return new("To the trash (duplicate)", ChangesKind.Trash, ["Remove"]);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in runs) names.UnionWith(StepNames(item, step));
        var last = runs.LastOrDefault(s => s.Target is not null);
        var target = last is null ? Path.GetFileName(item.Path) : TargetText(last);
        return new(target, ChangesKind.Planned, StepOrder.Where(names.Contains).ToList());
    }

    /// <summary>Where a step puts the file, relative to the library folder it writes in ("New library: …" for a new library).</summary>
    private static string TargetText(CleanupStep step) =>
        (step.Copies ? "New library: " : "") + Relative(step.Target!, step.WriteRoot);

    /// <summary>"X.chd already exists", from the clash's reason (without "nothing is overwritten", which the tooltip says).</summary>
    private static string ClashText(CleanupStep step)
    {
        var reason = step.Reason;
        var cut = reason.IndexOf(": nothing", StringComparison.Ordinal);
        return cut > 0 ? reason[..cut] : reason;
    }

    /// <summary>
    /// What one step does to the file, as chips: a conversion that also takes the database name and moves to its
    /// system folder is Convert, Rename and Move.
    /// </summary>
    private static IEnumerable<string> StepNames(LibraryItem item, CleanupStep step)
    {
        if (step.Kind == CleanupKind.Copy) { yield return "Copy"; yield break; }
        if (step.Kind == CleanupKind.Convert) yield return "Convert";
        if (step.Kind == CleanupKind.Rename) { yield return "Rename"; yield break; }
        if (step.Target is null) yield break;
        var source = step.Sources.Count > 0 ? step.Sources[0] : item.Path;
        var named = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(step.Target));
        var was = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(source));
        if (!string.Equals(named, was, StringComparison.Ordinal)) yield return "Rename";
        if (step.Copies) { yield return "Copy"; yield break; }
        var from = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source)) ?? "";
        var to = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(step.Target)) ?? "";
        if (step.Kind == CleanupKind.Move || !Core.PathNames.Same(from, to)) yield return "Move";
    }
}
