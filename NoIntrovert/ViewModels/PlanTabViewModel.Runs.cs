using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// Optimize and Organize's runs afterwards: the bar over the list that says what the last run did, with
/// Undo, and Past runs (shared by both pages: every run either made), each with Undo and Empty trash.
/// </summary>
public abstract partial class PlanTabViewModel
{
    private void WatchRuns()
    {
        Settings.Output.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OutputSettingsViewModel.UseRecycleBin)) OnPropertyChanged(nameof(EmptyTrashTip));
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IsRunning)) return;
            if (IsRunning) LastRun = null; // another run started: the bar was about the one before
            UndoCommand.NotifyCanExecuteChanged();
            EmptyTrashCommand.NotifyCanExecuteChanged();
        };
        Items.CollectionChanged += (_, _) =>
        {
            if (Items.Count == 0) LastRun = null; // the list was cleared
        };
        RefreshRuns();
    }

    // ---- The last run's bar ----

    /// <summary>The run just finished, while its bar is showing over the list; null once closed.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLastRun))]
    public partial CleanupRunRow? LastRun { get; set; }

    public bool HasLastRun => LastRun is not null;

    /// <summary>What the last run did: "Organized 27 games · 4 left alone".</summary>
    [ObservableProperty] public partial string LastRunText { get; set; } = "";

    /// <summary>"Organized", "Optimized": the bar's first word.</summary>
    protected abstract string DoneVerb { get; }

    /// <summary>"Organized 27 games · 4 left alone · 1 failed".</summary>
    public static string LastRunSummary(string verb, int done, int failed, int leftAlone, string? newLibrary) =>
        $"{verb} {Words.Count(done, "game")}"
        + (newLibrary is null ? "" : $" into {newLibrary}")
        + (leftAlone > 0 ? $" · {leftAlone} left {(newLibrary is null ? "alone" : "out")}" : "")
        + (failed > 0 ? $" · {failed} failed" : "");

    /// <summary>Shows the bar for a run that changed files (moved, renamed, converted, removed or made any).</summary>
    private void ShowLastRun(CleanupRun run, int done, int failed, int leftAlone)
    {
        if (run.Entries.Count == 0) return;
        LastRunText = LastRunSummary(DoneVerb, done, failed, leftAlone, run.NewLibrary);
        LastRun = Runs.FirstOrDefault(r => r.Run.Id == run.Id) ?? new CleanupRunRow(run);
    }

    [RelayCommand]
    private void CloseLastRun() => LastRun = null;

    // ---- Past runs: undo, empty trash ----

    /// <summary>The latest runs, Optimize's and Organize's alike.</summary>
    public ObservableCollection<CleanupRunRow> Runs { get; } = [];
    public bool HasRuns => Runs.Count > 0;

    /// <summary>Undo or Empty trash (or Organize's Verify them now) is under way: the page waits for it.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(EmptyTrashCommand))]
    public partial bool IsBusy { get; set; }

    partial void OnIsBusyChanged(bool value) => BusyChanged();

    /// <summary>Called when <see cref="IsBusy"/> changed (Organize's run button follows it).</summary>
    protected virtual void BusyChanged() { }

    partial void OnUncheckedCountChanged(int value) => UncheckedChanged();

    /// <summary>Called when <see cref="UncheckedCount"/> changed (Organize's callout follows it).</summary>
    protected virtual void UncheckedChanged() { }

    /// <summary>What the page is doing while <see cref="IsBusy"/>: "Undoing the run…".</summary>
    [ObservableProperty] public partial string BusyText { get; set; } = "";

    /// <summary>Where emptied trash goes, as Settings › Output says.</summary>
    public static string TrashFate(bool useRecycleBin) =>
        useRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently";

    public string EmptyTrashTip =>
        $"This run's trash is {TrashFate(Settings.Output.UseRecycleBin)} (Settings › Output). The run can't be undone after that.";

    private bool CanAct => !IsBusy && !IsRunning;

    protected override bool BeforeStart(IReadOnlyList<JobItemViewModel> pending) => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Undo(CleanupRunRow row)
    {
        if (!CanAct) return;
        var message = row.Run.NewLibrary is { } lib
            ? $"The files this run made in {lib} are removed again."
            : "Every file this run converted, renamed, moved or removed is put back.";
        if (!await Dialogs.ConfirmAsync("Undo this run?", $"{row.Summary}\n\n{message}", "Undo the run", "Cancel")) return;
        IsBusy = true;
        BusyText = "Undoing the run…";
        try
        {
            var useBin = Settings.Output.UseRecycleBin;
            var problems = await Task.Run(() => CleanupRunner.Undo(row.Run, useBin, Log));
            Log.Info(problems.Count == 0 ? $"{Title}: run undone." : $"{Title}: run undone, except {Words.Count(problems.Count, "item")} (see above).");
            // The rows go back to the files' old paths, on every page.
            var (moves, _) = MovesOf(row.Run, row.Run.Entries.Select(e => e.From).OfType<string>());
            var back = moves.GroupBy(m => m.Value, PathNames.Comparer).ToDictionary(g => g.Key, g => g.First().Key, PathNames.Comparer);
            ReplaceRows(back);
            Workspace?.Move(back);
            if (LastRun?.Run.Id == row.Run.Id) LastRun = null;
            RefreshRuns();
            RequestPlan();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task EmptyTrash(CleanupRunRow row)
    {
        if (!CanAct) return;
        var useBin = Settings.Output.UseRecycleBin;
        var (files, bytes) = await Task.Run(() => TrashContents(row.Run));
        if (!await Dialogs.ConfirmAsync("Empty this run's trash?", EmptyTrashMessage(row, files, bytes, useBin),
                useBin && FileRemover.CanUseBin ? $"Move to the {FileRemover.BinName}" : "Delete permanently", "Cancel", destructive: true)) return;
        IsBusy = true;
        BusyText = "Emptying the trash…";
        try
        {
            await Task.Run(() => CleanupRunner.EmptyTrash(row.Run, useBin, Log));
            Log.Info($"{Title}: trash emptied; that run's conversions can no longer be undone.");
            RefreshRuns();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Empty trash's question: the run, what its trash holds, and where it goes.</summary>
    public static string EmptyTrashMessage(CleanupRunRow row, int files, long bytes, bool useRecycleBin) =>
        $"{row.Summary}\n\n{files} file{(files == 1 ? "" : "s")} ({ChdJobs.Size(bytes)}) {(files == 1 ? "is" : "are")} {TrashFate(useRecycleBin)}. "
        + "The run can't be undone after that.";

    /// <summary>How many files a run's trash holds, and their size.</summary>
    public static (int Files, long Bytes) TrashContents(CleanupRun run)
    {
        int files = 0;
        long bytes = 0;
        foreach (var folder in run.TrashFolders.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    files++;
                    bytes += new FileInfo(file).Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (files, bytes);
    }

    /// <summary>Past runs as saved now (the other page may have run, undone or emptied one).</summary>
    protected void RefreshRuns()
    {
        Runs.Clear();
        foreach (var run in CleanupRunner.Runs().Take(20)) Runs.Add(new CleanupRunRow(run));
        OnPropertyChanged(nameof(HasRuns));
        // The bar's Undo acts on the run as saved now.
        if (LastRun is { } last) LastRun = Runs.FirstOrDefault(r => r.Run.Id == last.Run.Id) is { Run.Undone: false } now ? now : null;
    }
}
