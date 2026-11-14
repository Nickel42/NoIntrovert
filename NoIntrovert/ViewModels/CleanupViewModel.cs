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
    }

    public override string Title => "Organize";
    public override string StartLabel => "Organize";
    protected override string DoneVerb => "Organized";
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
        FollowPlan();
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
        FollowPlan();
    }

    // ---- Files not checked yet ----

    /// <summary>"33 files haven't been checked yet. Organize renames and moves only games it recognises."</summary>
    public string UncheckedText => UncheckedCount == 1
        ? "1 file hasn't been checked yet. Organize renames and moves only games it recognises."
        : $"{UncheckedCount} files haven't been checked yet. Organize renames and moves only games it recognises.";

    /// <summary>The plan has rows Verify &amp; match hasn't checked: the callout offers to check them.</summary>
    public bool HasUnchecked => UncheckedCount > 0;

    /// <summary>Runs Verify &amp; match on these files on the Verify page (the main window sets it).</summary>
    public Func<IReadOnlyList<string>, Task>? VerifyRequested { get; set; }

    /// <summary>Shows the Optimize page, which verifies first (the main window sets it).</summary>
    public Action? OpenOptimizeRequested { get; set; }

    protected override void UncheckedChanged()
    {
        OnPropertyChanged(nameof(UncheckedText));
        OnPropertyChanged(nameof(HasUnchecked));
        VerifyNowCommand.NotifyCanExecuteChanged();
    }

    private bool CanVerifyNow => !IsBusy && !IsRunning && UncheckedCount > 0;

    /// <summary>
    /// Verify them now: Verify &amp; match checks the files the plan hasn't got a result for, on the Verify page's
    /// rows and the shared queue, and the plan is made again when it's done. The page stays on Organize.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanVerifyNow))]
    private async Task VerifyNow()
    {
        if (!CanVerifyNow || VerifyRequested is not { } verify) return;
        var paths = UncheckedPaths;
        IsBusy = true;
        BusyText = $"Verifying {Words.Count(paths.Count, "file")}…";
        try
        {
            Log.Info($"Organize: verifying {Words.Count(paths.Count, "file")} it hasn't checked yet");
            await verify(paths);
        }
        finally { IsBusy = false; }
        await MakePlanAsync();
    }

    [RelayCommand]
    private void UseOptimize() => OpenOptimizeRequested?.Invoke();

    // ---- Running ----

    /// <summary>The plan has a ticked step to run.</summary>
    public bool HasWorkToDo => Plan?.Steps.Any(s => s.Runs) == true;

    /// <summary>The run bar's words when there's nothing to run.</summary>
    public const string NothingToOrganize = "Nothing to organize yet";

    protected override bool StartAllowed => !IsBusy && HasWorkToDo;

    /// <summary>The run button follows the plan: off, with the run bar saying so, when it has nothing to run.</summary>
    private void FollowPlan()
    {
        StartCommand.NotifyCanExecuteChanged();
        VerifyNowCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasWorkToDo));
        if (IsRunning) return;
        var idle = Plan is not null && Items.Count > 0 && !HasWorkToDo;
        if (idle && RunStatus.Length == 0) RunStatus = NothingToOrganize;
        else if (!idle && RunStatus == NothingToOrganize) RunStatus = "";
    }

    protected override void BusyChanged() => FollowPlan();

    protected override bool BeforeStart(IReadOnlyList<JobItemViewModel> pending)
    {
        if (!base.BeforeStart(pending)) return false;
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
    }
}
