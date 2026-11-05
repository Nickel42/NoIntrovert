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
            if (FocusedItem is { } focused && !Items.Contains(focused)) FocusedItem = null;
        };
        settings.Output.PropertyChanged += (_, _) => RefreshSaveTo();
        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => RefreshProgress());
    }

    // ---- Save to: the run bar's choice of where results go, starting from the Settings default ----

    private SaveTo? _runSaveTo;

    /// <summary>Asks the window to show the Settings page (set by the main view model).</summary>
    public Action? OpenSettingsRequested { get; set; }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke();

    /// <summary>
    /// Asks the user to confirm Replace the originals the first time it's
    /// chosen (set by the run bar's view). Without it, Replace can't be chosen
    /// until it has been confirmed once.
    /// </summary>
    public Func<Task<bool>>? ConfirmReplace { get; set; }

    /// <summary>Whether this page writes new files, so its run bar shows Save to (Convert does; Verify and Tests don't).</summary>
    public virtual bool ShowsSaveTo => false;

    /// <summary>This page's Save to choice: the Settings default until it's changed in the run bar.</summary>
    public SaveTo RunSaveTo => _runSaveTo ?? Settings.Output.SaveTo;

    // The three radio buttons. A button that's unchecked writes false, which changes nothing.
    public bool SaveNextToOriginals { get => RunSaveTo == SaveTo.NextToOriginals; set { if (value) _ = ChooseSaveToAsync(SaveTo.NextToOriginals); } }
    public bool SaveInFolder { get => RunSaveTo == SaveTo.Folder; set { if (value) _ = ChooseSaveToAsync(SaveTo.Folder); } }
    public bool SaveReplacingOriginals { get => RunSaveTo == SaveTo.ReplaceOriginals; set { if (value) _ = ChooseSaveToAsync(SaveTo.ReplaceOriginals); } }

    /// <summary>The last folder used for Save to, or a hint when there isn't one yet.</summary>
    public string SaveFolderText => string.IsNullOrWhiteSpace(Settings.Output.Folder) ? "none chosen yet" : Settings.Output.Folder;

    /// <summary>
    /// Makes <paramref name="choice"/> this page's Save to, after what it needs:
    /// a folder for Folder, and the one-time confirmation for Replace. When
    /// that's refused, the run bar goes back to the previous choice.
    /// </summary>
    public async Task ChooseSaveToAsync(SaveTo choice)
    {
        if (choice == RunSaveTo) return;
        var ok = choice switch
        {
            SaveTo.Folder => !string.IsNullOrWhiteSpace(Settings.Output.Folder) || await PickSaveFolderAsync(),
            SaveTo.ReplaceOriginals => Settings.Output.ReplaceConfirmed || await ConfirmReplaceOnceAsync(),
            _ => true,
        };
        if (ok) _runSaveTo = choice == Settings.Output.SaveTo ? null : choice;
        RefreshSaveTo();
        // The radio button the user clicked has already checked itself; undo that after the click is done.
        if (!ok) Dispatcher.UIThread.Post(RefreshSaveTo);
    }

    private async Task<bool> ConfirmReplaceOnceAsync()
    {
        if (ConfirmReplace is null || !await ConfirmReplace()) return false;
        Settings.Output.ReplaceConfirmed = true;
        return true;
    }

    /// <summary>Change… beside Folder: picks the folder (remembered as the last one used) and chooses Folder.</summary>
    [RelayCommand]
    private async Task ChangeSaveFolder()
    {
        if (await PickSaveFolderAsync()) await ChooseSaveToAsync(SaveTo.Folder);
    }

    private async Task<bool> PickSaveFolderAsync()
    {
        var folder = await Dialogs.PickFolderAsync($"Where {Title} saves its results");
        if (folder is null) return false;
        Settings.Output.Folder = folder;
        return true;
    }

    private void RefreshSaveTo()
    {
        OnPropertyChanged(nameof(RunSaveTo));
        OnPropertyChanged(nameof(SaveNextToOriginals));
        OnPropertyChanged(nameof(SaveInFolder));
        OnPropertyChanged(nameof(SaveReplacingOriginals));
        OnPropertyChanged(nameof(SaveFolderText));
    }

    /// <summary>The run log's description of a Save to choice.</summary>
    public static string DescribeSaveTo(OutputSettings output) => output.SaveTo switch
    {
        SaveTo.Folder when output.TargetFolder is { } folder => $"saving to {folder}",
        SaveTo.ReplaceOriginals => "saving next to the originals and replacing them: each original is " +
                                   (output.UseRecycleBin && FileRemover.CanUseBin ? $"moved to the {FileRemover.BinName}" : "deleted permanently") +
                                   " once its new file verifies",
        _ => "saving next to the originals",
    } + (output.Overwrite ? ", overwriting files in the way" : "");

    /// <summary>The action whose preset this tab's runs use, when there's one for the whole tab (Verify).</summary>
    protected virtual Core.Presets.PresetAction? RunPreset => null;

    /// <summary>The settings a run uses: the saved ones, with this tab's preset and Save to choice on top.</summary>
    protected AppSettings RunSettings()
    {
        var snapshot = RunPreset is { } action ? Core.Presets.ActionPresets.Effective(Settings.Snapshot(), action) : Settings.Snapshot();
        // A page without Save to never removes originals, and Folder without a folder is next to the originals.
        snapshot.Output.SaveTo = ShowsSaveTo ? RunSaveTo : SaveTo.NextToOriginals;
        if (snapshot.Output.SaveTo == SaveTo.Folder && snapshot.Output.TargetFolder is null) snapshot.Output.SaveTo = SaveTo.NextToOriginals;
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
            i.PresetLabel.Contains(w, StringComparison.OrdinalIgnoreCase) ||
            i.StatusText.Contains(w, StringComparison.OrdinalIgnoreCase) || i.TypeText.Contains(w, StringComparison.OrdinalIgnoreCase));
        VisibleItems.ReplaceAll(Items.Where(Match));
        OnPropertyChanged(nameof(NoMatches));
        var hidden = Items.Count - VisibleItems.Count;
        FilterNote = hidden == 0 ? "" : $"{VisibleItems.Count:N0} of {Items.Count:N0} shown";
    }

    [RelayCommand]
    private void ClearFilter() => Filter = "";
    [ObservableProperty] public partial System.Collections.IList? SelectedItems { get; set; }

    // ---- Selection and focus: the checkboxes select, the panel follows the focused row ----

    /// <summary>The row the side panel describes: the last one clicked or moved to with the keyboard (null when none).</summary>
    [ObservableProperty] public partial JobItemViewModel? FocusedItem { get; set; }

    partial void OnFocusedItemChanged(JobItemViewModel? oldValue, JobItemViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsFocusedRow = false;
        if (newValue is not null) newValue.IsFocusedRow = true;
        OnFocusChanged();
    }

    /// <summary>The selected rows, in list order.</summary>
    public IReadOnlyList<JobItemViewModel> Selected =>
        SelectedItems is { Count: > 0 } list ? Items.Where(new HashSet<JobItemViewModel>(list.OfType<JobItemViewModel>()).Contains).ToList() : [];

    public int SelectedCount => SelectedItems?.Count ?? 0;

    /// <summary>The select-all checkbox: true when every shown row is selected, false when none, null (–) when some.</summary>
    public bool? AllSelected => SelectedCount == 0 ? false : SelectedCount >= VisibleItems.Count ? true : null;

    /// <summary>Called by the list whenever its selection changes.</summary>
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(Selected));
        _summary.Request();
        OnSelectionChanged();
    }

    /// <summary>For pages whose side panel follows the selection.</summary>
    protected virtual void OnSelectionChanged() { }

    /// <summary>For pages whose side panel follows the focused row.</summary>
    protected virtual void OnFocusChanged() { }

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
        // Replace the originals can arrive as the default without having been confirmed (a migrated settings file): ask now.
        if (ShowsSaveTo && RunSaveTo == SaveTo.ReplaceOriginals && !Settings.Output.ReplaceConfirmed && !await ConfirmReplaceOnceAsync())
        {
            Log.Warn($"{Title}: not started. Replace the originals wasn't confirmed; choose where to save in the run bar.");
            return;
        }
        if (!BeforeStart(pending)) return;

        var snapshot = RunSettings();
        if (ShowsSaveTo)
            Log.Info($"{Title}: for this run, {DescribeSaveTo(snapshot.Output)}");
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
        RunStatus = $"Finished in {took:h\\:mm\\:ss} · {ok} done, {failed} failed" + (cancelled > 0 ? $", {cancelled} cancelled" : "");
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
        var selected = SelectedCount > 0 ? $"{SelectedCount} selected · " : "";
        Summary = Items.Count == 0 ? "" : $"{selected}{Items.Count} {(Items.Count == 1 ? "item" : "items")} · {done} done · {failed} failed";
    }
}
