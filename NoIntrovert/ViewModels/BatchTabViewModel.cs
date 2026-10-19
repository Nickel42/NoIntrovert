using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// The shared pick files → choose options → run → read the log workflow.
/// Each tab supplies which files it accepts and what to do with each one.
/// </summary>
public abstract partial class BatchTabViewModel : ViewModelBase
{
    private CancellationTokenSource? _cts;

    protected BatchTabViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs)
    {
        Settings = settings;
        Log = log;
        Dialogs = dialogs;
        Items.CollectionChanged += (_, _) => RefreshSummary();
    }

    public SettingsViewModel Settings { get; }
    public LogViewModel Log { get; }
    protected IDialogs Dialogs { get; }

    public abstract string Title { get; }
    public abstract string StartLabel { get; }
    public abstract string EmptyHint { get; }
    protected abstract string FilterName { get; }

    /// <summary>
    /// The formats this tab takes. The format registry then decides which
    /// files are accepted and what a folder scan finds (a cue claims its bins,
    /// a Wii U title folder is one row). Null for a tab that takes anything.
    /// </summary>
    protected abstract IReadOnlyList<FormatInfo>? InputFormats { get; }

    /// <summary>How many folder levels a scan looks through when subfolders are on, the added folder included.</summary>
    protected virtual int ScanDepth => int.MaxValue;

    protected virtual IReadOnlyList<string> Patterns => InputFormats is { } formats ? KnownFormats.Patterns(formats) : ["*.*"];

    /// <summary>Whether a file belongs in this tab's list.</summary>
    protected virtual bool Accepts(string path) =>
        InputFormats is not { } formats || (FormatRegistry.Default.Detect(path) is { } format && formats.Contains(format));

    /// <summary>What a folder added to the list becomes: by default the inputs the registry finds in it.</summary>
    protected virtual IEnumerable<string> ItemsForFolder(string folder)
    {
        if (InputFormats is not { } formats) return [folder];
        return FormatRegistry.Default.Scan(folder, Settings.ScanSubfolders, ScanDepth)
            .Where(s => formats.Contains(s.Format))
            .Select(s => s.Primary)
            .ToList();
    }

    protected abstract JobOutcome Work(JobContext context, JobItemViewModel item);

    /// <summary>How pending rows become jobs: by default one job per row.</summary>
    protected virtual IReadOnlyList<IReadOnlyList<JobItemViewModel>> GroupItems(IReadOnlyList<JobItemViewModel> pending) =>
        pending.Select(i => (IReadOnlyList<JobItemViewModel>)[i]).ToList();

    /// <summary>The work for one job; a job covering several rows reports through the first.</summary>
    protected virtual JobOutcome WorkGroup(JobContext context, IReadOnlyList<JobItemViewModel> group) => Work(context, group[0]);

    public ObservableCollection<JobItemViewModel> Items { get; } = [];
    [ObservableProperty] public partial System.Collections.IList? SelectedItems { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle)), NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    public bool IsIdle => !IsRunning;
    public bool HasItems => Items.Count > 0;

    [ObservableProperty] public partial double OverallProgress { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string RunStatus { get; set; } = "";

    [RelayCommand]
    private async Task AddFiles()
    {
        var files = await Dialogs.PickFilesAsync($"Add files to {Title}", FilterName, Patterns);
        AddPaths(files);
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        var folder = await Dialogs.PickFolderAsync($"Add a folder to {Title}");
        if (folder is not null) AddPaths([folder]);
    }

    /// <summary>Adds files and folders (from pickers or drag and drop), skipping duplicates.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var ignored = 0;
        var rar = 0;
        foreach (var path in paths)
        {
            IEnumerable<string> candidates;
            if (Directory.Exists(path))
            {
                try
                {
                    candidates = ItemsForFolder(path).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Couldn't read {path}: {ex.Message}");
                    continue;
                }
            }
            else if (File.Exists(path) && Accepts(path))
            {
                candidates = [path];
            }
            else
            {
                if (Core.Formats.ArchiveExtractor.IsRar(path)) rar++;
                else ignored++;
                continue;
            }

            foreach (var file in candidates)
            {
                if (!existing.Add(file)) continue;
                Items.Add(new JobItemViewModel(file));
                added++;
            }
        }
        if (added > 0) Log.Info($"{Title}: added {added} file(s)");
        if (ignored > 0) Log.Warn($"{Title}: ignored {ignored} item(s) this tab can't use");
        if (rar > 0) Log.Warn($"{Title}: ignored {rar} RAR file(s). {Core.Formats.ArchiveExtractor.RarNotSupported}");
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (IsRunning || SelectedItems is null) return;
        foreach (var item in SelectedItems.Cast<JobItemViewModel>().ToList())
            Items.Remove(item);
    }

    [RelayCommand]
    private void ClearFinished()
    {
        if (IsRunning) return;
        foreach (var item in Items.Where(i => i.Status is JobStatus.Succeeded or JobStatus.Warning or JobStatus.Skipped).ToList())
            Items.Remove(item);
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (!IsRunning) Items.Clear();
    }

    private bool CanStart => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        var pending = Items.Where(i => i.Status != JobStatus.Succeeded).ToList();
        if (pending.Count == 0)
        {
            Log.Warn(Items.Count == 0 ? $"{Title}: add some files first." : $"{Title}: everything in the list is already done. Clear it or add more files.");
            return;
        }
        if (!BeforeStart(pending)) return;

        var snapshot = Settings.Snapshot();
        foreach (var item in pending) item.Reset();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        OverallProgress = 0;
        RunStatus = $"Working on {pending.Count} item(s)…";
        Log.Info($"{Title}: starting {pending.Count} item(s) with {snapshot.ParallelJobs} parallel job(s)");
        var started = DateTime.Now;
        var finished = 0;

        var jobs = GroupItems(pending).Select(group => new BatchJob(
            group[0].Name,
            group[0],
            ctx => WorkGroup(ctx, group),
            _ => Dispatcher.UIThread.Post(() => { foreach (var item in group) item.Started(); }),
            outcome => Dispatcher.UIThread.Post(() =>
            {
                foreach (var item in group) item.Finished(outcome);
                finished += group.Count;
                OverallProgress = 100.0 * finished / pending.Count;
                RefreshSummary();
            }))).ToList();

        try
        {
            await BatchRunner.RunAsync(jobs, snapshot, Log, _cts.Token);
            // Let the queued finish callbacks land before summing up.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }

        var took = DateTime.Now - started;
        var ok = pending.Count(i => i.Status is JobStatus.Succeeded or JobStatus.Warning);
        var failed = pending.Count(i => i.Status == JobStatus.Failed);
        var cancelled = pending.Count(i => i.Status == JobStatus.Cancelled);
        RunStatus = $"Finished in {took:h\\:mm\\:ss}: {ok} done, {failed} failed" + (cancelled > 0 ? $", {cancelled} cancelled" : "");
        (failed > 0 ? (Action<string>)Log.Warn : Log.Success)($"{Title}: {RunStatus}");
        AfterRun(pending);
    }

    /// <summary>Last chance to stop a run (for example, missing settings). Return false to cancel.</summary>
    protected virtual bool BeforeStart(IReadOnlyList<JobItemViewModel> pending) => true;

    protected virtual void AfterRun(IReadOnlyList<JobItemViewModel> items) { }

    private bool CanCancel => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        RunStatus = "Cancelling…";
        Log.Warn($"{Title}: cancelling; partial outputs are deleted");
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(HasItems));
        var done = Items.Count(i => i.Status is JobStatus.Succeeded or JobStatus.Warning);
        var failed = Items.Count(i => i.Status == JobStatus.Failed);
        Summary = Items.Count == 0 ? "" : $"{Items.Count} item(s) · {done} done · {failed} failed";
    }
}
