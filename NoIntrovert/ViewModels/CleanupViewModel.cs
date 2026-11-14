using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// Organize (the Cleanup wizard, M7, docs/requirements/cleanup-wizard.md): the files in the list are
/// converted, named as in the database and moved into the layout, using what Verify &amp; match already
/// found (files it hasn't checked are left alone; Optimize checks them first). Each row's CHANGES column
/// is the dry run; a box per kind of step turns that kind on or off. A run can be undone, or its trash
/// emptied, afterwards.
/// </summary>
public partial class CleanupViewModel : PlanTabViewModel
{
    /// <summary>The kinds of step turned off with their boxes; a new plan keeps them off.</summary>
    private readonly HashSet<CleanupKind> _off = [];

    public CleanupViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs, JobQueue queue, OrganizeOptions options)
        : base(settings, log, dialogs, queue, options)
    {
        Settings.Output.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OutputSettingsViewModel.UseRecycleBin)) OnPropertyChanged(nameof(EmptyTrashTip));
        };
        RefreshRuns();
    }

    public override string Title => "Organize";
    public override string StartLabel => "Organize";
    public override string EmptyHint =>
        "Drop games here, or a folder of them.\n" +
        "Files Verify & match recognised are converted, named as in the database and moved into their system's folder; " +
        "the CHANGES column shows where each one ends up. Nothing changes until you click Organize, and a run can be undone.";
    protected override string FilterName => "Games";

    // ---- The plan's steps and a box per kind ----

    /// <summary>Every step of the plan, with its tick (off when its kind's box is off).</summary>
    public ObservableCollection<CleanupStepRow> Steps { get; } = [];

    /// <summary>One box per kind of step the plan can run (Convert, Rename, Move…), with its count.</summary>
    public ObservableCollection<CleanupKindToggle> Kinds { get; } = [];

    public bool HasRunnableSteps => Steps.Any(s => s.CanRun);

    private bool _bulk;

    protected override void PlanShown(CleanupPlan? plan)
    {
        _bulk = true;
        Steps.Clear();
        try
        {
            foreach (var s in plan?.Steps ?? [])
            {
                s.Enabled = !_off.Contains(s.Kind);
                Steps.Add(new CleanupStepRow(s, OnStepChanged));
            }
        }
        finally { _bulk = false; }
        Kinds.Clear();
        foreach (var kind in CleanupKindToggle.RunnableKinds)
        {
            var rows = Steps.Where(s => s.CanRun && s.Step.Kind == kind).ToList();
            if (rows.Count > 0) Kinds.Add(new CleanupKindToggle(kind, rows, SetEnabled));
        }
        OnPropertyChanged(nameof(HasRunnableSteps));
    }

    /// <summary>A kind's box: ticks or unticks every step of that kind, and remembers it for the next plan.</summary>
    private void SetEnabled(IEnumerable<CleanupStepRow> rows, bool on)
    {
        _bulk = true;
        try
        {
            foreach (var row in rows)
            {
                row.Enabled = on;
                if (on) _off.Remove(row.Step.Kind);
                else _off.Add(row.Step.Kind);
            }
        }
        finally { _bulk = false; }
        OnStepChanged();
    }

    private void OnStepChanged()
    {
        if (_bulk) return;
        foreach (var kind in Kinds) kind.Refresh();
        ShowChanges();
    }

    /// <summary>The rows' CHANGES and the summary again, for the ticks as they are now.</summary>
    private void ShowChanges()
    {
        foreach (var row in Items)
        {
            if (row.IsRunning) continue;
            var steps = StepsByRow.GetValueOrDefault(row.Path) ?? [];
            var item = ItemsByRow.GetValueOrDefault(row.Path);
            row.Changes = ChangesFor(item, steps);
            row.ChangesDetails = DetailsFor(item, steps);
        }
        if (Plan is { } plan) PlanSummary = Summarize(plan);
    }

    // ---- Running ----

    /// <summary>Undo or Empty trash is under way: the page waits for it.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(EmptyTrashCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>What the page is doing while <see cref="IsBusy"/>: "Undoing the run…".</summary>
    [ObservableProperty] public partial string BusyText { get; set; } = "";

    protected override bool BeforeStart(IReadOnlyList<JobItemViewModel> pending)
    {
        if (IsBusy) return false;
        if (Options.BuildNewLibrary && Options.Problem(Workspace?.Folders ?? []) is { } problem)
        {
            Log.Warn($"Organize: {problem}");
            return false;
        }
        return true;
    }

    protected override Task RunJobsAsync(IReadOnlyList<JobItemViewModel> pending, AppSettings snapshot) => RunPlanAsync(pending, snapshot);

    protected override void RunFinished(CleanupRun run)
    {
        Log.Info(run.NewLibrary is { } built
            ? $"Organize: finished; the new library is in {built} ({run.Entries.Count} file(s) made). Undo under Past runs removes them again."
            : $"Organize: finished; the run can be undone under Past runs ({run.Entries.Count} change(s) recorded).");
        RefreshRuns();
    }

    // ---- Past runs: undo, empty trash ----

    public ObservableCollection<CleanupRunRow> Runs { get; } = [];
    public bool HasRuns => Runs.Count > 0;

    /// <summary>Where emptied trash goes, as Settings › Output says.</summary>
    public static string TrashFate(bool useRecycleBin) =>
        useRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently";

    public string EmptyTrashTip =>
        $"This run's trash is {TrashFate(Settings.Output.UseRecycleBin)} (Settings › Output). The run can't be undone after that.";

    private bool CanAct => !IsBusy && !IsRunning;

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
            Log.Info(problems.Count == 0 ? "Organize: run undone." : $"Organize: run undone, except {problems.Count} item(s) (see above).");
            // The rows go back to the files' old paths, on every page.
            var (moves, _) = MovesOf(row.Run, row.Run.Entries.Select(e => e.From).OfType<string>());
            var back = moves.GroupBy(m => m.Value, PathNames.Comparer).ToDictionary(g => g.Key, g => g.First().Key, PathNames.Comparer);
            ReplaceRows(back);
            Workspace?.Move(back);
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
            Log.Info("Organize: trash emptied; that run's conversions can no longer be undone.");
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

    private void RefreshRuns()
    {
        Runs.Clear();
        foreach (var run in CleanupRunner.Runs().Take(20)) Runs.Add(new CleanupRunRow(run));
        OnPropertyChanged(nameof(HasRuns));
    }
}
