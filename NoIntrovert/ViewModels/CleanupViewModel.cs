using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Library;

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
        CleanupKind.RemoveDuplicate => "Remove duplicate",
        CleanupKind.Clash => "Clash",
        _ => "Left alone",
    };

    /// <summary>Before → after, as paths in the Library folder, so the dry run shows where each file ends up (FR-2).</summary>
    public string Change => Step.Target is { } target
        ? $"{string.Join(", ", Step.Sources.Take(1).Select(Shorten))}{(Step.Sources.Count > 1 ? $" (+{Step.Sources.Count - 1})" : "")} → {Shorten(target)}"
        : string.Join(", ", Step.Sources.Take(1).Select(Shorten)) + (Step.Kind == CleanupKind.RemoveDuplicate ? " → trash" : "");

    public bool Enabled
    {
        get => Step.Enabled;
        set { if (Step.Enabled == value) return; Step.Enabled = value; OnPropertyChanged(); changed(); }
    }

    private string Shorten(string target) =>
        Path.GetRelativePath(Step.Root, target) is var relative && !relative.StartsWith("..") ? relative.Replace('\\', '/') : target;
}

/// <summary>One past run, with Undo and Empty trash (FR-4).</summary>
public sealed record CleanupRunRow(CleanupRun Run)
{
    public string Summary => Run.Summary;
    public bool CanUndo => !Run.Undone;
    public bool CanEmpty => !Run.Undone && !Run.TrashEmptied && Run.TrashFolders.Any(Directory.Exists);
}

/// <summary>
/// The Cleanup wizard (M7, docs/requirements/cleanup-wizard.md): make a plan
/// for the selected games or the whole Library, review it as a dry run, run
/// it on the shared queue, and undo a run or empty its trash afterwards.
/// </summary>
public partial class CleanupViewModel : ViewModelBase
{
    private readonly SettingsViewModel _settings;
    private readonly LogViewModel _log;
    private readonly JobQueue _queue;
    private readonly Func<LibraryIndex> _index;
    private IReadOnlyList<Game>? _scope;
    private CleanupPlan? _plan;

    public CleanupViewModel(SettingsViewModel settings, LogViewModel log, JobQueue queue, Func<LibraryIndex> index)
    {
        _settings = settings;
        _log = log;
        _queue = queue;
        _index = index;
        // The layout can be changed here or in Settings; either way the plan is made again for it.
        _settings.Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LibrarySettingsViewModel.SelectedLayout)) return;
            OnPropertyChanged(nameof(EsDeLayout));
            OnPropertyChanged(nameof(KeepLayout));
            if (HasPlan && !IsBusy) _ = MakePlan();
        };
        RefreshRuns();
    }

    /// <summary>FR-6: ES-DE format (the default), shown as a choice on the page.</summary>
    public bool EsDeLayout
    {
        get => _settings.Library.SelectedLayout.Value == LibraryLayout.EsDe;
        set { if (value) UseLayout(LibraryLayout.EsDe); }
    }

    /// <summary>FR-6: keep the current file and folder hierarchy; files are only converted and renamed.</summary>
    public bool KeepLayout
    {
        get => _settings.Library.SelectedLayout.Value == LibraryLayout.InPlace;
        set { if (value) UseLayout(LibraryLayout.InPlace); }
    }

    private void UseLayout(LibraryLayout layout) =>
        _settings.Library.SelectedLayout = LibrarySettingsViewModel.Layouts.First(l => l.Value == layout);

    public SettingsViewModel Settings => _settings;

    /// <summary>Sends files to Verify &amp; match (set by the main window).</summary>
    public Action<IReadOnlyList<string>>? SendToVerify { get; set; }

    /// <summary>Looks through the Library again after a run or an undo (set by the main window).</summary>
    public Func<Task>? Rescan { get; set; }

    public ObservableCollection<CleanupStepRow> Steps { get; } = [];
    public ObservableCollection<CleanupRunRow> Runs { get; } = [];

    [ObservableProperty] public partial string ScopeText { get; set; } = "The whole Library";
    [ObservableProperty] public partial string Summary { get; set; } = "Make a plan to see what would change. Nothing is written until you run it.";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(MakePlanCommand), nameof(RunCommand))]
    public partial bool IsBusy { get; set; }

    public bool HasPlan => Steps.Count > 0;
    public bool HasRuns => Runs.Count > 0;

    /// <summary>The games the next plan covers (from the Library's Clean up selected); null for the whole Library.</summary>
    public void UseGames(IReadOnlyList<Game>? games)
    {
        _scope = games is { Count: > 0 } ? games : null;
        ScopeText = _scope is null ? "The whole Library" : $"{_scope.Count} selected game{(_scope.Count == 1 ? "" : "s")}";
        _ = MakePlan();
    }

    [RelayCommand]
    private Task WholeLibrary()
    {
        _scope = null;
        ScopeText = "The whole Library";
        return MakePlan();
    }

    private bool CanAct => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task MakePlan()
    {
        IsBusy = true;
        try
        {
            var settings = _settings.Snapshot();
            var games = _scope ?? _index().Games();
            var keys = games.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // The games as the latest index has them (a scan may have run since they were chosen).
            var current = _index().Games().Where(g => keys.Contains(g.Key)).ToList();
            _plan = await Task.Run(() => CleanupPlanner.Plan(current, settings, MatchStore.Default));
            Steps.Clear();
            foreach (var s in _plan.Steps.OrderBy(s => s.Game, StringComparer.OrdinalIgnoreCase)) Steps.Add(new CleanupStepRow(s, UpdateSummary));
            OnPropertyChanged(nameof(HasPlan));
            UpdateSummary();
        }
        finally { IsBusy = false; }
    }

    private void UpdateSummary()
    {
        if (_plan is null) return;
        var runs = _plan.Steps.Count(s => s.Runs);
        var parts = new[] { CleanupKind.Convert, CleanupKind.Rename, CleanupKind.Move, CleanupKind.RemoveDuplicate }
            .Select(k => (Kind: k, Count: _plan.Steps.Count(s => s.Kind == k && s.Runs)))
            .Where(p => p.Count > 0)
            .Select(p => $"{p.Count} {p.Kind switch { CleanupKind.Convert => "to convert", CleanupKind.Rename => "to rename", CleanupKind.Move => "to move", _ => "duplicate(s) to remove" }}");
        var left = _plan.Count(CleanupKind.Untouched);
        var clashes = _plan.Count(CleanupKind.Clash);
        Summary = (runs == 0 ? "Nothing to do." : $"{string.Join(" · ", parts)} · frees about {ChdJobs.Size(_plan.BytesSaved)}")
                  + (clashes > 0 ? $" · {clashes} clash(es) to sort out first" : "")
                  + (left > 0 ? $" · {left} left alone" : "");
        RunCommand.NotifyCanExecuteChanged();
    }

    private bool CanRun => !IsBusy && _plan is not null && _plan.Steps.Any(s => s.Runs);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        if (_plan is null) return;
        IsBusy = true;
        try
        {
            var runner = new CleanupRunner();
            var jobs = runner.Jobs(_plan).Select(j => new BatchJob(j.Name, new NullJobReport(), j.Work, _ => { }, _ => { }, j.Cost)).ToList();
            _log.Info($"Cleanup: running {runner.Run.Steps} step(s) for {jobs.Count} game(s)");
            var group = _queue.Submit("Cleanup", jobs, _settings.Snapshot(), _log);
            await group.Completion;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            _log.Info($"Cleanup: finished; the run can be undone from the Cleanup page ({runner.Run.Entries.Count} change(s) recorded).");
            RefreshRuns();
            if (Rescan is not null) await Rescan();
            await MakePlan();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Sends the files that haven't been checked yet to Verify &amp; match.</summary>
    [RelayCommand]
    private void VerifyUnchecked()
    {
        var paths = _plan?.Steps.Where(s => s.Kind == CleanupKind.Untouched && s.Reason.StartsWith("Not checked")).SelectMany(s => s.Sources.Take(1)).ToList() ?? [];
        if (paths.Count == 0) { _log.Info("Cleanup: everything in the plan has been checked already."); return; }
        SendToVerify?.Invoke(paths);
    }

    [RelayCommand]
    private async Task Undo(CleanupRunRow row)
    {
        var useBin = _settings.Output.UseRecycleBin;
        var problems = await Task.Run(() => CleanupRunner.Undo(row.Run, useBin, _log));
        _log.Info(problems.Count == 0 ? "Cleanup: run undone." : $"Cleanup: run undone, except {problems.Count} item(s) (see above).");
        RefreshRuns();
        if (Rescan is not null) await Rescan();
    }

    [RelayCommand]
    private async Task EmptyTrash(CleanupRunRow row)
    {
        var useBin = _settings.Output.UseRecycleBin;
        await Task.Run(() => CleanupRunner.EmptyTrash(row.Run, useBin, _log));
        _log.Info("Cleanup: trash emptied; that run's conversions can no longer be undone.");
        RefreshRuns();
    }

    private void RefreshRuns()
    {
        Runs.Clear();
        foreach (var run in CleanupRunner.Runs().Take(20)) Runs.Add(new CleanupRunRow(run));
        OnPropertyChanged(nameof(HasRuns));
    }

    private sealed class NullJobReport : IJobReport
    {
        public void SetStage(string stage) { }
        public void SetProgress(double fraction) { }
    }
}
