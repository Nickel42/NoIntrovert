using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Cleanup;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Library;
using NoIntrovert.Services;

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
    private readonly IDialogs _dialogs;
    private IReadOnlyList<Game>? _scope;
    private CleanupPlan? _plan;

    public CleanupViewModel(SettingsViewModel settings, LogViewModel log, JobQueue queue, Func<LibraryIndex> index, IDialogs dialogs)
    {
        _settings = settings;
        _log = log;
        _queue = queue;
        _index = index;
        _dialogs = dialogs;
        // The layout can be changed here or in Settings; either way the plan is made again for it.
        _settings.Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LibrarySettingsViewModel.SelectedLayout)) return;
            OnPropertyChanged(nameof(EsDeLayout));
            OnPropertyChanged(nameof(KeepLayout));
            if (HasPlan && !IsBusy) _ = MakePlan();
        };
        _settings.Output.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OutputSettingsViewModel.UseRecycleBin)) OnPropertyChanged(nameof(EmptyTrashTip));
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

    // ---- FR-8: where the plan writes: the Library itself, or a new library in another folder ----

    /// <summary>Tidy the Library where it is (the default): files are converted, renamed and moved in place.</summary>
    public bool InPlace
    {
        get => !BuildNewLibrary;
        set { if (value) BuildNewLibrary = false; }
    }

    /// <summary>
    /// FR-8: build a new library in <see cref="NewLibraryFolder"/> instead. The
    /// plan is the same, but every game is converted or copied there and the
    /// Library is left exactly as it is.
    /// </summary>
    [ObservableProperty] public partial bool BuildNewLibrary { get; set; }

    partial void OnBuildNewLibraryChanged(bool value)
    {
        OnPropertyChanged(nameof(InPlace));
        OnPropertyChanged(nameof(SafetyText));
        ReplanIfShown();
    }

    /// <summary>FR-8: copy files that aren't matched (unknown, not checked, damaged) as they are, rather than leave them out.</summary>
    [ObservableProperty] public partial bool CopyUnmatched { get; set; } = true;

    partial void OnCopyUnmatchedChanged(bool value) => ReplanIfShown();

    /// <summary>The new library's folder (remembered in Settings).</summary>
    public string NewLibraryFolder => _settings.Library.NewLibraryFolder;

    public bool HasNewLibraryFolder => !string.IsNullOrWhiteSpace(NewLibraryFolder);

    public string SafetyText => BuildNewLibrary
        ? "Nothing in the Library changes: originals stay where they are, and duplicates aren't copied. Nothing in the new folder is overwritten, and a run can be undone, which removes what it made there."
        : "Nothing is overwritten. Originals and duplicates go to a .noi-trash folder in their Library folder, so a run can be undone (conversions too) until you empty its trash.";

    [RelayCommand]
    private async Task ChooseNewLibraryFolder()
    {
        var folder = await _dialogs.PickFolderAsync("Where to build the new library");
        if (folder is null) return;
        _settings.Library.NewLibraryFolder = folder;
        OnPropertyChanged(nameof(NewLibraryFolder));
        OnPropertyChanged(nameof(HasNewLibraryFolder));
        if (!BuildNewLibrary) BuildNewLibrary = true; // replans
        else ReplanIfShown();
    }

    private void ReplanIfShown()
    {
        if (HasPlan && !IsBusy) _ = MakePlan();
        else UpdateSummary();
    }

    /// <summary>
    /// Why the new library can't be built in the chosen folder, or null when it can: one has to be
    /// chosen, and it can't be inside a Library folder or hold one, or the Library would change after all.
    /// </summary>
    public static string? NewLibraryProblem(string folder, IEnumerable<string> libraryFolders)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Choose a folder for the new library.";
        static string Full(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        var dest = Full(folder);
        foreach (var lib in libraryFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(Full))
        {
            if (dest.StartsWith(lib, PathNames.Comparison)) return $"The new library can't be inside a Library folder ({lib.TrimEnd(Path.DirectorySeparatorChar)}).";
            if (lib.StartsWith(dest, PathNames.Comparison)) return $"The new library's folder can't hold a Library folder ({lib.TrimEnd(Path.DirectorySeparatorChar)}).";
        }
        return null;
    }

    private string? CurrentProblem => BuildNewLibrary ? NewLibraryProblem(NewLibraryFolder, _settings.Library.Folders) : null;

    public SettingsViewModel Settings => _settings;

    /// <summary>Sends files to Verify &amp; match (set by the main window).</summary>
    public Action<IReadOnlyList<string>>? SendToVerify { get; set; }

    /// <summary>Looks through the Library again after a run or an undo (set by the main window).</summary>
    public Func<Task>? Rescan { get; set; }

    public ObservableCollection<CleanupStepRow> Steps { get; } = [];
    public ObservableCollection<CleanupRunRow> Runs { get; } = [];

    [ObservableProperty] public partial string ScopeText { get; set; } = "The whole Library";
    [ObservableProperty] public partial string Summary { get; set; } = "Make a plan to see what would change. Nothing is written until you run it.";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(MakePlanCommand), nameof(RunCommand), nameof(WholeLibraryCommand), nameof(UndoCommand), nameof(EmptyTrashCommand))]
    [NotifyPropertyChangedFor(nameof(ShowsEmptyHint))]
    public partial bool IsBusy { get; set; }

    /// <summary>What the page is doing while <see cref="IsBusy"/>: "Making the plan…", "Running 12 steps…".</summary>
    [ObservableProperty] public partial string BusyText { get; set; } = "";

    /// <summary>A run is on the queue, so it can be stopped.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    public partial bool IsRunning { get; set; }

    public bool HasPlan => Steps.Count > 0;

    /// <summary>The "choose games" message shows only with no plan and nothing being worked on.</summary>
    public bool ShowsEmptyHint => !HasPlan && !IsBusy;

    /// <summary>Where emptied trash goes, as Settings › Output says.</summary>
    public static string TrashFate(bool useRecycleBin) =>
        useRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently";

    public string EmptyTrashTip =>
        $"This run's trash is {TrashFate(_settings.Output.UseRecycleBin)} (Settings › Output). The run can't be undone after that.";

    // ---- Tick all: the header's box is on when every step that can run is ticked, half when some are ----

    private bool _bulk;

    public bool? AllEnabled
    {
        get
        {
            var runnable = Steps.Where(s => s.CanRun).ToList();
            if (runnable.Count == 0) return false;
            var on = runnable.Count(s => s.Enabled);
            return on == runnable.Count ? true : on == 0 ? false : null;
        }
        set
        {
            var on = value != false;
            _bulk = true;
            try { foreach (var row in Steps.Where(s => s.CanRun)) row.Enabled = on; }
            finally { _bulk = false; }
            OnStepChanged();
        }
    }

    public bool HasRunnableSteps => Steps.Any(s => s.CanRun);

    private void OnStepChanged()
    {
        if (_bulk) return;
        OnPropertyChanged(nameof(AllEnabled));
        UpdateSummary();
    }
    public bool HasRuns => Runs.Count > 0;

    /// <summary>The games the next plan covers (from the Library's Clean up selected); null for the whole Library.</summary>
    public void UseGames(IReadOnlyList<Game>? games)
    {
        _scope = games is { Count: > 0 } ? games : null;
        ScopeText = _scope is null ? "The whole Library" : $"{_scope.Count} selected game{(_scope.Count == 1 ? "" : "s")}";
        _ = MakePlan();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task WholeLibrary()
    {
        _scope = null;
        ScopeText = "The whole Library";
        return MakePlan();
    }

    private bool CanAct => !IsBusy;

    /// <summary>A plan was asked for while the page was busy; it's made when the work is done.</summary>
    private bool _planAgain;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task MakePlan()
    {
        // One thing at a time: a second plan would clear the steps, and IsBusy, under a running one.
        if (IsBusy) { _planAgain = true; return; }
        IsBusy = true;
        BusyText = "Making the plan…";
        try { await PlanAsync(); }
        finally { IsBusy = false; }
        if (_planAgain) await MakePlan();
    }

    private async Task PlanAsync()
    {
        _planAgain = false;
        var settings = _settings.Snapshot();
        var games = _scope ?? _index().Games();
        var keys = games.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The games as the latest index has them (a scan may have run since they were chosen).
        var current = _index().Games().Where(g => keys.Contains(g.Key)).ToList();
        var problem = CurrentProblem;
        var dest = BuildNewLibrary && problem is null ? NewLibraryFolder : null;
        var copyUnmatched = CopyUnmatched;
        if (problem is not null)
        {
            _plan = null;
            Steps.Clear();
            PlanChanged();
            Summary = problem;
            RunCommand.NotifyCanExecuteChanged();
            return;
        }
        _plan = await Task.Run(() => CleanupPlanner.Plan(current, settings, MatchStore.Default, dest, copyUnmatched));
        Steps.Clear();
        foreach (var s in _plan.Steps.OrderBy(s => s.Game, StringComparer.OrdinalIgnoreCase)) Steps.Add(new CleanupStepRow(s, OnStepChanged));
        PlanChanged();
        UpdateSummary();
    }

    private void PlanChanged()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(ShowsEmptyHint));
        OnPropertyChanged(nameof(HasRunnableSteps));
        OnPropertyChanged(nameof(AllEnabled));
    }

    private void UpdateSummary()
    {
        if (CurrentProblem is { } problem)
        {
            Summary = problem;
            RunCommand.NotifyCanExecuteChanged();
            return;
        }
        if (_plan is null) return;
        var runs = _plan.Steps.Count(s => s.Runs);
        var copies = _plan.Steps.Any(s => s.Copies);
        var parts = new[] { CleanupKind.Convert, CleanupKind.Copy, CleanupKind.Rename, CleanupKind.Move, CleanupKind.RemoveDuplicate }
            .Select(k => (Kind: k, Count: _plan.Steps.Count(s => s.Kind == k && s.Runs)))
            .Where(p => p.Count > 0)
            .Select(p => $"{p.Count} {p.Kind switch { CleanupKind.Convert => "to convert", CleanupKind.Copy => "to copy", CleanupKind.Rename => "to rename", CleanupKind.Move => "to move", _ => "duplicate(s) to remove" }}");
        var left = _plan.Count(CleanupKind.Untouched);
        var clashes = _plan.Count(CleanupKind.Clash);
        // A new library's size: what it copies, plus what it converts less the typical saving.
        var size = copies ? _plan.Steps.Where(s => s.Runs).Sum(s => s.Sources.Sum(FileSize)) - _plan.BytesSaved : 0;
        Summary = (runs == 0 ? "Nothing to do." : copies
                      ? $"{string.Join(" · ", parts)} · about {ChdJobs.Size(size)} in the new library"
                      : $"{string.Join(" · ", parts)} · frees about {ChdJobs.Size(_plan.BytesSaved)}")
                  + (clashes > 0 ? $" · {clashes} clash(es) to sort out first" : "")
                  + (left > 0 ? $" · {left} {(copies ? "left out" : "left alone")}" : "");
        RunCommand.NotifyCanExecuteChanged();
    }

    private static long FileSize(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private bool CanRun => !IsBusy && _plan is not null && _plan.Steps.Any(s => s.Runs) && CurrentProblem is null;

    private JobGroup? _group;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        if (_plan is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var runner = new CleanupRunner();
            var jobs = runner.Jobs(_plan).Select(j => new BatchJob(j.Name, new NullJobReport(), j.Work, _ => { }, _ => { }, j.Cost)).ToList();
            BusyText = $"Running {runner.Run.Steps} step{(runner.Run.Steps == 1 ? "" : "s")} for {jobs.Count} game{(jobs.Count == 1 ? "" : "s")}…";
            _log.Info($"Cleanup: running {runner.Run.Steps} step(s) for {jobs.Count} game(s)"
                      + (runner.Run.NewLibrary is { } lib ? $", building a new library in {lib}; the Library itself isn't changed" : ""));
            var group = _group = _queue.Submit("Cleanup", jobs, _settings.Snapshot(), _log);
            IsRunning = true;
            try { await group.Completion; }
            finally { IsRunning = false; _group = null; }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            _log.Info(runner.Run.NewLibrary is { } built
                ? $"Cleanup: finished; the new library is in {built} ({runner.Run.Entries.Count} file(s) made). Undo on the Cleanup page removes them again."
                : $"Cleanup: finished; the run can be undone from the Cleanup page ({runner.Run.Entries.Count} change(s) recorded).");
            RefreshRuns();
            BusyText = "Looking through the Library again…";
            if (Rescan is not null) await Rescan();
            BusyText = "Making the plan…";
            await PlanAsync();
        }
        finally { IsBusy = false; }
    }

    private bool CanCancelRun => IsRunning;

    /// <summary>Stops the page's own run. Steps already done stay done, and the run can still be undone.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private async Task CancelRun()
    {
        if (_group is not { } group) return;
        if (!await _dialogs.ConfirmAsync("Stop the cleanup?",
                "Steps already done stay done, and the run can still be undone. A file being written now is deleted.",
                "Stop", "Keep running", destructive: true)) return;
        if (_group != group) return; // it finished while the question was open
        group.Cancel();
        BusyText = "Stopping…";
        _log.Warn("Cleanup: stopping; steps already done are kept.");
    }

    /// <summary>Sends the files that haven't been checked yet to Verify &amp; match.</summary>
    [RelayCommand]
    private void VerifyUnchecked()
    {
        var paths = _plan?.Steps.Where(s => s.Kind == CleanupKind.Untouched && s.Reason.StartsWith("Not checked")).SelectMany(s => s.Sources.Take(1)).ToList() ?? [];
        if (paths.Count == 0) { _log.Info("Cleanup: everything in the plan has been checked already."); return; }
        SendToVerify?.Invoke(paths);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Undo(CleanupRunRow row)
    {
        if (IsBusy) return;
        var message = row.Run.NewLibrary is { } lib
            ? $"The files this run made in {lib} are removed again."
            : "Every file this run converted, renamed, moved or removed is put back, and the Library is looked through again.";
        if (!await _dialogs.ConfirmAsync("Undo this run?", $"{row.Summary}\n\n{message}", "Undo the run", "Cancel")) return;
        IsBusy = true;
        BusyText = "Undoing the run…";
        try
        {
            var useBin = _settings.Output.UseRecycleBin;
            var problems = await Task.Run(() => CleanupRunner.Undo(row.Run, useBin, _log));
            _log.Info(problems.Count == 0 ? "Cleanup: run undone." : $"Cleanup: run undone, except {problems.Count} item(s) (see above).");
            RefreshRuns();
            BusyText = "Looking through the Library again…";
            if (Rescan is not null) await Rescan();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task EmptyTrash(CleanupRunRow row)
    {
        if (IsBusy) return;
        var useBin = _settings.Output.UseRecycleBin;
        var (files, bytes) = await Task.Run(() => TrashContents(row.Run));
        if (!await _dialogs.ConfirmAsync("Empty this run's trash?", EmptyTrashMessage(row, files, bytes, useBin),
                useBin && FileRemover.CanUseBin ? $"Move to the {FileRemover.BinName}" : "Delete permanently", "Cancel", destructive: true)) return;
        IsBusy = true;
        BusyText = "Emptying the trash…";
        try
        {
            await Task.Run(() => CleanupRunner.EmptyTrash(row.Run, useBin, _log));
            _log.Info("Cleanup: trash emptied; that run's conversions can no longer be undone.");
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

    private sealed class NullJobReport : IJobReport
    {
        public void SetStage(string stage) { }
        public void SetProgress(double fraction) { }
    }
}
