using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core.Cleanup;

namespace NoIntrovert.ViewModels;

/// <summary>One step in the dry run, with its tick box (FR-2).</summary>
public sealed partial class CleanupStepRow(CleanupStep step, Action changed) : ObservableObject
{
    public CleanupStep Step { get; } = step;
    public string Game => Step.Game;
    public string Reason => Step.Reason;
    public bool CanRun => Step.Kind is not (CleanupKind.Clash or CleanupKind.Untouched);

    public string KindText => Step.Kind switch
    {
        CleanupKind.Convert => "Convert",
        CleanupKind.Rename => "Rename",
        CleanupKind.Move => "Move",
        CleanupKind.Copy => "Copy",
        CleanupKind.RemoveDuplicate => "Remove duplicate",
        CleanupKind.Clash => "Clash",
        _ => Step.Copies ? "Left out" : "Left alone",
    };

    /// <summary>
    /// Before → after, as paths in the Library folder, so the dry run shows where each file ends up (FR-2).
    /// For a new library (FR-8) the after is a path in the new library's folder.
    /// </summary>
    public string Change => Step.Target is { } target
        ? $"{string.Join(", ", Step.Sources.Take(1).Select(Shorten))}{(Step.Sources.Count > 1 ? $" (+{Step.Sources.Count - 1})" : "")} → {Shorten(target, Step.WriteRoot)}"
        : string.Join(", ", Step.Sources.Take(1).Select(Shorten)) + (Step.Kind == CleanupKind.RemoveDuplicate ? " → trash" : "");

    public bool Enabled
    {
        get => Step.Enabled;
        set { if (Step.Enabled == value) return; Step.Enabled = value; OnPropertyChanged(); changed(); }
    }

    private string Shorten(string path) => Shorten(path, Step.Root);

    private static string Shorten(string path, string root) =>
        Path.GetRelativePath(root, path) is var relative && !relative.StartsWith("..") && !Path.IsPathRooted(relative) ? relative.Replace('\\', '/') : path;
}

/// <summary>
/// One kind of step in the plan (Convert, Rename, Move…) with how many steps of
/// that kind can run: its box ticks or unticks them all at once, and is
/// half-ticked when only some are.
/// </summary>
public sealed partial class CleanupKindToggle(CleanupKind kind, IReadOnlyList<CleanupStepRow> rows, Action<IEnumerable<CleanupStepRow>, bool> setEnabled)
    : ObservableObject
{
    /// <summary>The kinds of step that can run, in the order the summary lists them.</summary>
    public static readonly CleanupKind[] RunnableKinds =
        [CleanupKind.Convert, CleanupKind.Copy, CleanupKind.Rename, CleanupKind.Move, CleanupKind.RemoveDuplicate];

    public CleanupKind Kind { get; } = kind;
    public IReadOnlyList<CleanupStepRow> Rows { get; } = rows;
    public int Count => Rows.Count;

    /// <summary>The kind's name on its box: "Convert", "Remove duplicates".</summary>
    public static string NameOf(CleanupKind kind) => kind switch
    {
        CleanupKind.Convert => "Convert",
        CleanupKind.Copy => "Copy",
        CleanupKind.Rename => "Rename",
        CleanupKind.Move => "Move",
        CleanupKind.RemoveDuplicate => "Remove duplicates",
        _ => kind.ToString(),
    };

    /// <summary>"Convert (12)".</summary>
    public string Label => $"{NameOf(Kind)} ({Count})";

    public string Tip => $"Tick or untick every {NameOf(Kind).ToLowerInvariant()} step ({Count})";

    public string AccessibleName => $"Run every {NameOf(Kind).ToLowerInvariant()} step";

    /// <summary>On when every step of this kind is ticked, off when none is, half when some are.</summary>
    public bool? IsChecked
    {
        get
        {
            var on = Rows.Count(r => r.Enabled);
            return on == Rows.Count ? true : on == 0 ? false : null;
        }
        set => setEnabled(Rows, value != false);
    }

    /// <summary>A click: ticks every step of this kind, or, when all are ticked, none.</summary>
    [RelayCommand]
    private void Toggle() => IsChecked = IsChecked != true;

    /// <summary>Called when any step's tick box changed.</summary>
    public void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>One past run, with Undo and Empty trash (FR-4).</summary>
public sealed record CleanupRunRow(CleanupRun Run)
{
    public string Summary => Run.Summary;
    public bool CanUndo => !Run.Undone;
    public bool CanEmpty => !Run.Undone && !Run.TrashEmptied && Run.TrashFolders.Any(Directory.Exists);
}
