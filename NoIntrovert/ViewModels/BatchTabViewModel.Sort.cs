using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;

namespace NoIntrovert.ViewModels;

// ---- Sorting: a click on a column heading sorts the list by it; another click on it turns the order round ----
// Only the shown rows (VisibleItems) are sorted; a run still works through Items in the order they were added.
// The rows are put in order when the list changes or a heading is clicked, not as a run updates them,
// so rows don't jump about under the pointer mid-run.
public abstract partial class BatchTabViewModel
{
    public FileListSortColumn SortColumn => Settings.General.FileListSort;

    public bool SortDescending => Settings.General.FileListSortDescending;

    [RelayCommand]
    private void SortBy(FileListSortColumn column) => Settings.General.SortFileListBy(column);

    /// <summary>The sort changed (here or on another page's list): re-sorts, keeping the selection.</summary>
    private void OnSortChanged()
    {
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        foreach (var name in ArrowNames) OnPropertyChanged(name);
        var selected = SelectedItems?.OfType<JobItemViewModel>().ToList() ?? [];
        RefreshVisible();
        if (SelectedItems is { } list && selected.Count > 0)
            foreach (var row in selected.Where(r => VisibleItems.Contains(r) && !list.Contains(r))) list.Add(row);
    }

    private static readonly string[] ArrowNames =
    [
        nameof(FileArrow), nameof(TypeArrow), nameof(PresetArrow), nameof(ChangesArrow), nameof(SizeArrow),
        nameof(StatusArrow), nameof(ProgressArrow), nameof(TimeArrow), nameof(ResultArrow),
    ];

    /// <summary>The arrow beside a heading: ▲ ascending or ▼ descending on the column sorted by, nothing on the others.</summary>
    private string Arrow(FileListSortColumn column) => SortColumn != column ? "" : SortDescending ? "▼" : "▲";

    public string FileArrow => Arrow(FileListSortColumn.File);
    public string TypeArrow => Arrow(FileListSortColumn.Type);
    public string PresetArrow => Arrow(FileListSortColumn.Preset);
    public string ChangesArrow => Arrow(FileListSortColumn.Changes);
    public string SizeArrow => Arrow(FileListSortColumn.Size);
    public string StatusArrow => Arrow(FileListSortColumn.Status);
    public string ProgressArrow => Arrow(FileListSortColumn.Progress);
    public string TimeArrow => Arrow(FileListSortColumn.Time);
    public string ResultArrow => Arrow(FileListSortColumn.Result);

    /// <summary>The rows in the chosen order. Ties go by file name (always A to Z), then path, so the order is stable.</summary>
    private IEnumerable<JobItemViewModel> Sorted(IEnumerable<JobItemViewModel> rows)
    {
        if (SortColumn == FileListSortColumn.None) return rows;
        var text = StringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<JobItemViewModel> By<T>(Func<JobItemViewModel, T> key, IComparer<T>? comparer = null) =>
            SortDescending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);
        var ordered = SortColumn switch
        {
            FileListSortColumn.Type => By(r => r.TypeText, text),
            FileListSortColumn.Preset => By(r => r.PresetLabel, text),
            FileListSortColumn.Changes => By(r => r.ChangesTarget, text),
            FileListSortColumn.Size => By(r => r.SizeBytes),
            FileListSortColumn.Status => By(r => r.StatusText, text),
            FileListSortColumn.Progress => By(r => r.Progress),
            FileListSortColumn.Time => By(r => r.Elapsed),
            FileListSortColumn.Result => By(r => r.Result, text),
            _ => By(r => r.Name, text),
        };
        return ordered.ThenBy(r => r.Name, text).ThenBy(r => r.Path, StringComparer.Ordinal);
    }
}
