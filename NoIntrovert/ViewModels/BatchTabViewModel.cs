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
    private JobGroup? _group;
    private readonly DispatcherTimer _progressTimer;
    private readonly Coalesced _summary;
    private readonly Coalesced _visible;
    private CancellationTokenSource? _scanCts;

    protected BatchTabViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs, JobQueue queue)
    {
        Settings = settings;
        Log = log;
        Dialogs = dialogs;
        Queue = queue;
        _summary = new Coalesced(RefreshSummary);
        _visible = new Coalesced(RefreshVisible);
        Items.CollectionChanged += (_, _) =>
        {
            _summary.Request();
            _visible.Request();
        };
        settings.Output.PropertyChanged += (_, _) => RefreshRunOptions();
        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => RefreshProgress());
    }

    // ---- This run: the Output defaults from Settings, optionally overridden for this tab's runs ----

    private string? _runFolder;
    private bool? _runOverwrite;

    /// <summary>Asks the window to show the Settings page (set by the main view model).</summary>
    public Action? OpenSettingsRequested { get; set; }

    public string RunFolderText => _runFolder ?? Settings.Output.FolderText;
    public bool HasRunOverrides => _runFolder is not null || _runOverwrite is not null;

    public string RunOptionsNote => HasRunOverrides
        ? "Changed for this tab's runs only; the defaults in Settings are untouched."
        : "The defaults from Settings.";

    /// <summary>Overwrite for this tab's runs: the Settings default until it's changed here.</summary>
    public bool RunOverwrite
    {
        get => _runOverwrite ?? Settings.Output.Overwrite;
        set
        {
            _runOverwrite = value == Settings.Output.Overwrite ? null : value;
            RefreshRunOptions();
        }
    }

    [RelayCommand]
    private async Task ChooseRunFolder()
    {
        var folder = await Dialogs.PickFolderAsync($"Where {Title} results go (this tab only)");
        if (folder is null) return;
        _runFolder = folder;
        RefreshRunOptions();
    }

    [RelayCommand]
    private void ResetRunOptions()
    {
        _runFolder = null;
        _runOverwrite = null;
        RefreshRunOptions();
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke();

    private void RefreshRunOptions()
    {
        OnPropertyChanged(nameof(RunFolderText));
        OnPropertyChanged(nameof(RunOverwrite));
        OnPropertyChanged(nameof(HasRunOverrides));
        OnPropertyChanged(nameof(RunOptionsNote));
    }

    /// <summary>The settings a run uses: the saved ones, with this tab's overrides on top.</summary>
    protected AppSettings RunSettings()
    {
        var snapshot = Settings.Snapshot();
        if (_runFolder is not null) snapshot.Output.Folder = _runFolder;
        if (_runOverwrite is { } overwrite) snapshot.Output.Overwrite = overwrite;
        return snapshot;
    }

    /// <summary>The app's shared queue: this tab's runs go in it alongside every other tab's.</summary>
    public JobQueue Queue { get; }

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
        InputFormats is null || (FormatRegistry.Default.Identify(path) is { } set && Takes(set));

    /// <summary>Whether one input the registry found belongs in this tab: by default, by its format.</summary>
    protected virtual bool Takes(FileSet set) => InputFormats?.Contains(set.Format) ?? true;

    /// <summary>Whether rows show a TARGET column (the Convert page).</summary>
    public virtual bool ShowsTargets => false;

    /// <summary>The row for one input; the Convert page gives each row its targets.</summary>
    protected virtual JobItemViewModel CreateItem(string path) => new(path);

    /// <summary>What a folder added to the list becomes: by default the inputs the registry finds in it.</summary>
    protected virtual IEnumerable<string> ItemsForFolder(string folder)
    {
        if (InputFormats is not { } formats) return [folder];
        return FormatRegistry.Default.Scan(folder, Settings.Output.ScanSubfolders, ScanDepth)
            .Where(Takes)
            .Select(s => s.Primary)
            .ToList();
    }

    protected abstract JobOutcome Work(JobContext context, JobItemViewModel item);

    /// <summary>How pending rows become jobs: by default one job per row.</summary>
    protected virtual IReadOnlyList<IReadOnlyList<JobItemViewModel>> GroupItems(IReadOnlyList<JobItemViewModel> pending) =>
        pending.Select(i => (IReadOnlyList<JobItemViewModel>)[i]).ToList();

    /// <summary>The work for one job; a job covering several rows reports through the first.</summary>
    protected virtual JobOutcome WorkGroup(JobContext context, IReadOnlyList<JobItemViewModel> group) => Work(context, group[0]);

    public BulkObservableCollection<JobItemViewModel> Items { get; } = [];

    /// <summary>The rows the list shows: all of them, or those matching <see cref="Filter"/>. Runs always use <see cref="Items"/>.</summary>
    public BulkObservableCollection<JobItemViewModel> VisibleItems { get; } = [];

    /// <summary>Shows only rows whose name, folder, target or status contains this text (every word, any order).</summary>
    [ObservableProperty] public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => _visible.Request();

    [ObservableProperty] public partial string FilterNote { get; set; } = "";

    /// <summary>There are rows, but the filter hides them all.</summary>
    public bool NoMatches => Items.Count > 0 && VisibleItems.Count == 0;

    private void RefreshVisible()
    {
        var words = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            VisibleItems.ReplaceAll(Items);
            FilterNote = "";
            OnPropertyChanged(nameof(NoMatches));
            return;
        }
        bool Match(JobItemViewModel i) => words.All(w =>
            i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Folder.Contains(w, StringComparison.OrdinalIgnoreCase) ||
            (i.Target?.Label.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) ||
            i.StatusText.Contains(w, StringComparison.OrdinalIgnoreCase) || i.TypeText.Contains(w, StringComparison.OrdinalIgnoreCase));
        VisibleItems.ReplaceAll(Items.Where(Match));
        OnPropertyChanged(nameof(NoMatches));
        var hidden = Items.Count - VisibleItems.Count;
        FilterNote = hidden == 0 ? "" : $"{VisibleItems.Count:N0} of {Items.Count:N0} shown";
    }

    [RelayCommand]
    private void ClearFilter() => Filter = "";
    [ObservableProperty] public partial System.Collections.IList? SelectedItems { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle)), NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>A folder scan is running in the background (rows appear when it's done).</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty] public partial string ScanStatus { get; set; } = "";

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

    /// <summary>Adds files and folders (from pickers or drag and drop), skipping duplicates. Returns at once; see <see cref="AddPathsAsync"/>.</summary>
    public void AddPaths(IEnumerable<string> paths) => _ = AddPathsAsync(paths);

    /// <summary>
    /// Scans folders and builds the new rows on a background thread (a big
    /// library can take a while, more so on a network or cloud drive), then
    /// adds them all at once. The window stays responsive and shows a count;
    /// <see cref="CancelScanCommand"/> stops it.
    /// </summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        var existing = new HashSet<string>(Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var token = cts.Token;
        var found = 0;
        IsScanning = true;
        ScanStatus = "Looking through the files…";
        PrepareForAdding();
        var ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => ScanStatus = $"Looking through the files… {Volatile.Read(ref found):N0} found");
        ticker.Start();
        try
        {
            var (items, ignored, rar, problems) = await Task.Run(() =>
            {
                var made = new List<JobItemViewModel>();
                var skipped = 0;
                var rars = 0;
                var unreadable = new List<string>();
                foreach (var path in list)
                {
                    token.ThrowIfCancellationRequested();
                    IEnumerable<string> candidates;
                    if (Directory.Exists(path))
                    {
                        try { candidates = ItemsForFolder(path).ToList(); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            unreadable.Add($"Couldn't read {path}: {ex.Message}");
                            continue;
                        }
                    }
                    else if (File.Exists(path) && Accepts(path))
                    {
                        candidates = [path];
                    }
                    else
                    {
                        if (Core.Formats.ArchiveExtractor.IsRar(path)) rars++;
                        else skipped++;
                        continue;
                    }
                    foreach (var file in candidates)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!existing.Add(file)) continue;
                        made.Add(CreateItem(file));
                        Interlocked.Increment(ref found);
                    }
                }
                return (made, skipped, rars, unreadable);
            }, token);

            Items.AddRange(items);
            foreach (var p in problems) Log.Warn(p);
            if (items.Count > 0) Log.Info($"{Title}: added {items.Count:N0} file(s)");
            if (ignored > 0) Log.Warn($"{Title}: ignored {ignored} item(s) this page can't use");
            if (rar > 0) Log.Warn($"{Title}: ignored {rar} RAR file(s). {Core.Formats.ArchiveExtractor.RarNotSupported}");
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"{Title}: stopped looking through the files; nothing was added.");
        }
        finally
        {
            ticker.Stop();
            if (_scanCts == cts)
            {
                IsScanning = false;
                ScanStatus = "";
                _scanCts = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>Called on the UI thread before a background scan, to take what rows need from settings.</summary>
    protected virtual void PrepareForAdding() { }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private void RemoveSelected()
    {
        if (IsRunning || SelectedItems is null) return;
        var doomed = new HashSet<JobItemViewModel>(SelectedItems.Cast<JobItemViewModel>());
        Items.RemoveWhere(doomed.Contains);
    }

    [RelayCommand]
    private void ClearFinished()
    {
        if (IsRunning) return;
        Items.RemoveWhere(i => i.Status is JobStatus.Succeeded or JobStatus.Warning or JobStatus.Skipped);
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (!IsRunning) Items.Clear();
    }

    private bool CanStart => !IsRunning && !IsScanning;

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

        var snapshot = RunSettings();
        if (HasRunOverrides)
            Log.Info($"{Title}: for this run, saving to {(string.IsNullOrWhiteSpace(snapshot.Output.Folder) ? "each source's folder" : snapshot.Output.Folder)}, overwrite {(snapshot.Output.Overwrite ? "on" : "off")}");
        foreach (var item in pending) item.Reset();
        IsRunning = true;
        OverallProgress = 0;
        var busy = Queue.IsBusy;
        RunStatus = busy ? $"Queued {pending.Count} item(s) behind the jobs already running…" : $"Working on {pending.Count} item(s)…";
        Log.Info($"{Title}: starting {pending.Count} item(s) with {snapshot.Performance.ParallelJobs} parallel job(s)" + (busy ? ", after the jobs already in the queue" : ""));
        var started = DateTime.Now;

        var jobs = GroupItems(pending).Select(group => new BatchJob(
            group.Count == 1 ? group[0].Name : $"{group[0].Name} (+{group.Count - 1})",
            group[0],
            ctx => WorkGroup(ctx, group),
            _ => Dispatcher.UIThread.Post(() => { foreach (var item in group) item.Started(); }),
            outcome => Dispatcher.UIThread.Post(() =>
            {
                foreach (var item in group) item.Finished(outcome);
                _summary.Request();
                if (Filter.Length > 0) _visible.Request(); // a status filter ("failed") follows the run
            }),
            CostBytes: group.Sum(i => i.SizeBytes))).ToList();

        try
        {
            _group = Queue.Submit(Title, jobs, snapshot, Log);
            _progressTimer.Start();
            await _group.Completion;
            // Let the queued finish callbacks land before summing up.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally
        {
            _progressTimer.Stop();
            RefreshProgress();
            IsRunning = false;
            _group = null;
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
        _group?.Cancel();
        RunStatus = "Cancelling…";
        Log.Warn($"{Title}: cancelling; partial outputs are deleted");
    }

    /// <summary>This run's share of the queue, weighted by size like the overall bar.</summary>
    private void RefreshProgress()
    {
        if (_group is not { } g || g.Jobs.Count == 0) return;
        var total = g.Jobs.Sum(j => (double)j.Cost);
        OverallProgress = 100 * g.Jobs.Sum(j => j.DoneBytes) / total;
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(HasItems));
        var done = Items.Count(i => i.Status is JobStatus.Succeeded or JobStatus.Warning);
        var failed = Items.Count(i => i.Status == JobStatus.Failed);
        Summary = Items.Count == 0 ? "" : $"{Items.Count} item(s) · {done} done · {failed} failed";
    }
}
