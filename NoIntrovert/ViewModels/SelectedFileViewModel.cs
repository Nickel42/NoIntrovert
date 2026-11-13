using CommunityToolkit.Mvvm.ComponentModel;
using NoIntrovert.Core.Dats;

namespace NoIntrovert.ViewModels;

/// <summary>
/// SELECTED FILE on the pages without Convert's side panel (Optimize, Verify, Organize): the same file facts
/// Convert shows (<see cref="FileFactsViewModel"/>), with the selection count beside the heading.
/// </summary>
public sealed partial class SelectedFileViewModel : ObservableObject
{
    /// <summary>The selected file's system, format, size and Verify &amp; match result.</summary>
    public FileFactsViewModel Facts { get; } = new();

    /// <summary>Beside the heading: "Focused: row 3 of 40 · 2 selected", "2 selected" or nothing.</summary>
    [ObservableProperty] public partial string Header { get; set; } = "";

    /// <inheritdoc cref="FileFactsViewModel.Follow"/>
    public void Follow(string header, string? path, string? systemId, IReadOnlyCollection<string?> selectedSystems, Func<string, MatchResult?>? match = null)
    {
        Header = header;
        Facts.Follow(path, systemId, selectedSystems, match);
    }

    /// <summary>"Focused: row 3 of 40 · 2 selected" (row is 1-based), "2 selected", or nothing.</summary>
    public static string HeaderFor(int? focusedRow, int rows, int selected) =>
        focusedRow is { } row ? $"Focused: row {row} of {rows} · {selected} selected"
        : selected > 0 ? $"{selected} selected" : "";
}
