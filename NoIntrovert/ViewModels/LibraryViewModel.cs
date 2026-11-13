using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Library;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// One file (or folder) of a game, as the details panel lists it, with its
/// target-format status and its last Verify &amp; match result (empty until checked).
/// </summary>
public sealed record GameFileRow(string Name, string Format, string Size, string Folder, string Disc, string Path, string FormatStatus, string Match)
{
    public bool HasMatch => Match.Length > 0;
}

/// <summary>What the details panel shows for a game, read off the UI thread (<see cref="GameRowViewModel.ReadDetails"/>).</summary>
public sealed record GameDetails(IReadOnlyList<GameFileRow> Files, string MatchSummary, string? ChdPath);

/// <summary>
/// One game in the Library list. Its texts are worked out once, here, so
/// searching and sorting thousands of games doesn't rebuild them on every key.
/// </summary>
public sealed partial class GameRowViewModel : ObservableObject
{
    public GameRowViewModel(Game game)
    {
        Game = game;
        Title = game.Title;
        System = game.SystemName;
        Formats = game.Formats;
        Size = game.Size;
        SizeText = ChdJobs.Size(Size);
        var discs = game.Discs;
        DiscsText = discs > 1 ? $"{discs} discs" : "";
        Status = game.Status;
    }

    public Game Game { get; }
    public string Title { get; }
    public string System { get; }
    public string Formats { get; }
    public long Size { get; }
    public string DiscsText { get; }
    public string SizeText { get; }
    public GameStatus Status { get; }

    public string StatusText => Status switch
    {
        GameStatus.NeedsConverting => "Needs converting",
        GameStatus.OriginalsRemain => "Originals still here",
        GameStatus.Converted => "Converted",
        GameStatus.Duplicate => "Duplicate",
        _ => "Ready",
    };

    private IReadOnlyList<GameFileRow>? _files;

    /// <summary>The details panel's file rows (made when first shown); their match results arrive with <see cref="ShowDetails"/>.</summary>
    public IReadOnlyList<GameFileRow> Files => _files ??= FileRows(Game, matches: null);

    /// <summary>"2 of 2 files verified", or how many haven't been checked (FR-11); empty until <see cref="ShowDetails"/>.</summary>
    [ObservableProperty] public partial string MatchSummary { get; private set; } = "";

    internal void ShowDetails(GameDetails details)
    {
        _files = details.Files;
        OnPropertyChanged(nameof(Files));
        MatchSummary = details.MatchSummary;
    }

    private static List<GameFileRow> FileRows(Game game, IReadOnlyList<Core.Dats.MatchResult?>? matches) =>
        game.Items.Select((i, n) => new GameFileRow(
            global::System.IO.Path.GetFileName(global::System.IO.Path.TrimEndingDirectorySeparator(i.Path)), i.FormatLabel, ChdJobs.Size(i.Size),
            global::System.IO.Path.GetDirectoryName(i.Path) ?? "", i.Disc, i.Path, i.FormatStatusText,
            matches?[n]?.Text ?? "")).ToList();

    /// <summary>
    /// The game's file rows with their stored match results, the match summary and its first CHD on disk.
    /// Each item's match is looked up once (a lookup stats the files, a Wii U title folder's thousands of them),
    /// so this runs off the UI thread.
    /// </summary>
    internal static GameDetails ReadDetails(Game game)
    {
        var results = game.Items.Select(i => Core.Dats.MatchStore.Default.Get(i.Path)).ToList();
        var checkedCount = results.Count(r => r is not null);
        string summary;
        if (checkedCount == 0) summary = "Not checked against the databases yet (Verify selected).";
        else
        {
            var good = results.Count(r => r?.Status is Core.Dats.MatchStatus.Verified or Core.Dats.MatchStatus.NameDiffers);
            var text = $"{good} of {results.Count} file{(results.Count == 1 ? "" : "s")} verified";
            summary = checkedCount < results.Count ? $"{text}; {results.Count - checkedCount} not checked yet." : text + ".";
        }
        var chd = game.Items.FirstOrDefault(i => i.FormatId == "chd" && File.Exists(i.Path))?.Path;
        return new GameDetails(FileRows(game, results), summary, chd);
    }
}

/// <summary>A choice in the Library's status filter.</summary>
public sealed record StatusFilter(string Label, GameStatus? Status)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Library page: the folders it keeps track of, every game found in
/// them (searchable, filtered by system and status), and the selected
/// game's files. Scans run in the background and only reread what changed;
/// the index is saved between runs.
/// </summary>
public partial class LibraryViewModel : ViewModelBase
{
    private readonly SettingsViewModel _settings;
    private readonly LogViewModel _log;
    private readonly IDialogs _dialogs;
    private readonly Coalesced _visible;
    private LibraryIndex _index = new();
    private CancellationTokenSource? _scanCts;

    public LibraryViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs)
    {
        _settings = settings;
        _log = log;
        _dialogs = dialogs;
        _visible = new Coalesced(RefreshVisible);
        ChdInfo = new InfoViewModel(log, dialogs);
        _ = LoadAsync();
    }

    public string Title => "Library";

    /// <summary>Set by the main view model: hand paths to the Convert or Verify page and show it.</summary>
    public Action<IReadOnlyList<string>>? SendToConvert { get; set; }
    public Action<IReadOnlyList<string>>? SendToVerify { get; set; }

    /// <summary>Opens the Cleanup wizard for these games (set by the main window).</summary>
    public Action<IReadOnlyList<Game>>? SendToCleanup { get; set; }

    /// <summary>The index as last loaded or scanned.</summary>
    public LibraryIndex Index => _index;

    /// <summary>Looks through the folders again (after the Cleanup wizard moved things).</summary>
    public Task RescanNowAsync() => RescanAsync();

    public IReadOnlyList<string> Folders => _settings.Library.Folders;
    public bool HasFolders => Folders.Count > 0;

    /// <summary>
    /// The "add the folders where you keep your games" message: only for an empty
    /// Library, never over a list of games (the index can outlive its folders).
    /// </summary>
    public bool ShowsEmptyMessage => !HasFolders && _games.Count == 0;

    /// <summary>Tells the page the folder list changed (added, removed, or edited in Settings).</summary>
    public void RefreshFolders()
    {
        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(ShowsEmptyMessage));
    }

    public BulkObservableCollection<GameRowViewModel> VisibleGames { get; } = [];
    private List<GameRowViewModel> _games = [];

    /// <summary><see cref="_games"/> in the chosen order: sorted again only when the sort or the index changes, not per search.</summary>
    private List<GameRowViewModel> _sorted = [];

    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial string SelectedSystem { get; set; } = AllSystems;
    [ObservableProperty] public partial StatusFilter SelectedStatus { get; set; } = StatusFilters[0];
    [ObservableProperty] public partial IReadOnlyList<string> Systems { get; set; } = [AllSystems];
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string MissingNote { get; set; } = "";
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string ScanStatus { get; set; } = "";
    [ObservableProperty] public partial GameRowViewModel? SelectedGame { get; set; }
    [ObservableProperty] public partial System.Collections.IList? SelectedGames { get; set; }

    public const string AllSystems = "All systems";

    public static IReadOnlyList<StatusFilter> StatusFilters { get; } =
    [
        new("Any status", null),
        new("Needs converting", GameStatus.NeedsConverting),
        new("Originals still here", GameStatus.OriginalsRemain),
        new("Duplicates", GameStatus.Duplicate),
        new("Converted", GameStatus.Converted),
        new("Ready (nothing to convert)", GameStatus.Ready),
    ];

    /// <summary>The selected game's CHD, read for its header details (the first CHD among its files).</summary>
    public InfoViewModel ChdInfo { get; }

    [ObservableProperty] public partial bool ShowsChdInfo { get; set; }

    partial void OnSearchChanged(string value) => _visible.Request();
    partial void OnSelectedSystemChanged(string value) => _visible.Request();
    partial void OnSelectedStatusChanged(StatusFilter value) => _visible.Request();

    partial void OnSelectedGameChanged(GameRowViewModel? value)
    {
        // Until the files are looked at (off the UI thread), the CHD panel shows for a game that has one.
        ShowsChdInfo = value?.Game.Items.Any(i => i.FormatId == "chd") == true;
        if (value is not null) _ = ShowDetailsAsync(value);
    }

    /// <summary>The latest details read for the selection (tests wait on it).</summary>
    internal Task DetailsLoaded { get; private set; } = Task.CompletedTask;

    private Task ShowDetailsAsync(GameRowViewModel game) => DetailsLoaded = ShowDetailsCoreAsync(game);

    private async Task ShowDetailsCoreAsync(GameRowViewModel game)
    {
        GameDetails details;
        try { details = await Task.Run(() => GameRowViewModel.ReadDetails(game.Game)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        game.ShowDetails(details);
        if (SelectedGame != game) return; // another game was picked meanwhile
        ShowsChdInfo = details.ChdPath is not null;
        if (details.ChdPath is { } chd) _ = ChdInfo.LoadAsync(chd);
    }

    // ---- Loading, scanning and saving ----

    private async Task LoadAsync()
    {
        _index = await Task.Run(LibraryIndex.Load);
        Show(_index);
        if (HasFolders && _settings.Library.RescanOnStart) await RescanAsync();
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        var folder = await _dialogs.PickFolderAsync("Add a folder of games to the Library");
        if (folder is not null) await AddFoldersAsync([folder]);
    }

    /// <summary>Adds folders to the Library (from the picker, or dropped on the page) and scans.</summary>
    public async Task AddFoldersAsync(IReadOnlyList<string> folders)
    {
        var added = _settings.Library.AddFolders(folders.Where(Directory.Exists));
        RefreshFolders();
        if (added.Count == 0) return;
        _log.Info($"Library: added {string.Join(", ", added)}");
        await RescanAsync();
    }

    [RelayCommand]
    private async Task RemoveFolder(string folder)
    {
        _settings.Library.RemoveFolder(folder);
        RefreshFolders();
        await RescanAsync();
    }

    [RelayCommand]
    private Task Rescan() => RescanAsync();

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private async Task RescanAsync()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var folders = Folders.ToList();
        var previous = _index;
        var found = 0;
        IsScanning = true;
        ScanStatus = "Looking through the library…";
        var ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => ScanStatus = $"Looking through the library… {Volatile.Read(ref found):N0} files");
        ticker.Start();
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var progress = new InlineProgress<int>(n => Volatile.Write(ref found, n));
            var index = await Task.Run(() =>
            {
                var scanned = LibraryScanner.Scan(folders, previous, progress, cts.Token, _settings.Snapshot());
                scanned.Save();
                return scanned;
            }, cts.Token);
            _index = index;
            Show(index);
            _log.Info($"Library: {index.Items.Count:N0} files in {folders.Count} folder(s) looked through in {sw.Elapsed.TotalSeconds:F1}s");
            foreach (var missing in index.MissingFolders) _log.Warn($"Library: couldn't read {missing} (not connected, or offline?)");
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Library: scan stopped; the list shows the last complete scan.");
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

    /// <summary>Raised when a new index is shown (loaded at start, or after a scan), with the systems in it.</summary>
    public event Action<LibraryIndex>? IndexShown;

    private void Show(LibraryIndex index)
    {
        IndexShown?.Invoke(index);
        var keep = SelectedGame?.Game.Key;
        _games = index.Games().Select(g => new GameRowViewModel(g)).ToList();
        _sorted = Sorted(_games).ToList();
        OnPropertyChanged(nameof(ShowsEmptyMessage));
        Systems = [AllSystems, .. _games.Select(g => g.System).Distinct().Order(StringComparer.OrdinalIgnoreCase)];
        if (!Systems.Contains(SelectedSystem)) SelectedSystem = AllSystems;
        MissingNote = index.MissingFolders.Count == 0 ? "" : $"Not reachable at the last scan: {string.Join(", ", index.MissingFolders)}";
        RefreshVisible();
        SelectedGame = keep is null ? null : VisibleGames.FirstOrDefault(g => g.Game.Key == keep);
    }

    private void RefreshVisible()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var status = SelectedStatus.Status;
        var system = SelectedSystem;
        var allSystems = system == AllSystems;
        bool Match(GameRowViewModel g) =>
            (allSystems || g.System == system) &&
            (status is null || g.Status == status) &&
            words.All(w => g.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || g.Formats.Contains(w, StringComparison.OrdinalIgnoreCase));
        // _sorted is already in the chosen order, so filtering keeps it.
        VisibleGames.ReplaceAll(_sorted.Where(Match));
        var size = VisibleGames.Sum(g => g.Size);
        var toConvert = VisibleGames.Count(g => g.Status == GameStatus.NeedsConverting);
        Summary = _games.Count == 0 ? "" :
            $"{VisibleGames.Count:N0} of {_games.Count:N0} games · {ChdJobs.Size(size)} · {toConvert:N0} need converting";
    }

    // ---- Sorting: a click on a column heading sorts by it; another click on it turns the order round ----

    public LibrarySortColumn SortColumn => _settings.Library.SortColumn;

    public bool SortDescending => _settings.Library.SortDescending;

    /// <summary>Sorts by <paramref name="column"/>, A to Z (smallest first); the column already sorted by turns round.</summary>
    [RelayCommand]
    private void SortBy(LibrarySortColumn column)
    {
        if (_settings.Library.SortColumn == column) _settings.Library.SortDescending = !_settings.Library.SortDescending;
        else
        {
            _settings.Library.SortColumn = column;
            _settings.Library.SortDescending = false;
        }
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        foreach (var name in new[] { nameof(GameArrow), nameof(SystemArrow), nameof(FormatsArrow), nameof(SizeArrow), nameof(StatusArrow) })
            OnPropertyChanged(name);
        var selected = SelectedGame;
        _sorted = Sorted(_games).ToList();
        RefreshVisible();
        SelectedGame = selected is null ? null : VisibleGames.FirstOrDefault(g => g.Game.Key == selected.Game.Key);
    }

    /// <summary>The arrow beside a heading: ▲ ascending or ▼ descending on the column sorted by, nothing on the others.</summary>
    private string Arrow(LibrarySortColumn column) => SortColumn != column ? "" : SortDescending ? "▼" : "▲";

    public string GameArrow => Arrow(LibrarySortColumn.Game);
    public string SystemArrow => Arrow(LibrarySortColumn.System);
    public string FormatsArrow => Arrow(LibrarySortColumn.Formats);
    public string SizeArrow => Arrow(LibrarySortColumn.Size);
    public string StatusArrow => Arrow(LibrarySortColumn.Status);

    /// <summary>The games in the chosen order. Ties go by title (always A to Z), then by the game's key, so the order is stable.</summary>
    private IEnumerable<GameRowViewModel> Sorted(IEnumerable<GameRowViewModel> games)
    {
        var text = StringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<GameRowViewModel> By<T>(Func<GameRowViewModel, T> key, IComparer<T>? comparer = null) =>
            SortDescending ? games.OrderByDescending(key, comparer) : games.OrderBy(key, comparer);
        var ordered = SortColumn switch
        {
            LibrarySortColumn.System => By(g => g.System, text),
            LibrarySortColumn.Formats => By(g => g.Formats, text),
            LibrarySortColumn.Size => By(g => g.Size),
            LibrarySortColumn.Status => By(g => g.StatusText, text),
            _ => By(g => g.Title, text),
        };
        return ordered.ThenBy(g => g.Title, text).ThenBy(g => g.Game.Key, StringComparer.Ordinal);
    }

    // ---- Acting on games ----

    private List<GameRowViewModel> Chosen() =>
        SelectedGames?.OfType<GameRowViewModel>().ToList() is { Count: > 0 } many ? many
        : SelectedGame is { } one ? [one] : [];

    /// <summary>Sends the chosen games' sources (what still needs converting) to the Convert page.</summary>
    [RelayCommand]
    private void ConvertSelected()
    {
        var paths = Chosen().SelectMany(g => g.Game.Items).Where(i => i.IsSource || i.FormatStatus == TargetStatus.Convert).Select(i => i.Path).Distinct().ToList();
        if (paths.Count == 0) { _log.Warn("Library: nothing in the selected games needs converting."); return; }
        SendToConvert?.Invoke(paths);
    }

    /// <summary>Sends the chosen games' CHDs to the Verify page.</summary>
    [RelayCommand]
    private void VerifySelected()
    {
        var paths = Chosen().SelectMany(g => g.Game.Items).Select(i => i.Path).Distinct().ToList();
        if (paths.Count == 0) { _log.Warn("Library: no games are selected."); return; }
        SendToVerify?.Invoke(paths);
    }

    /// <summary>Plans a cleanup of the chosen games (M7).</summary>
    [RelayCommand]
    private void CleanupSelected()
    {
        var games = Chosen().Select(g => g.Game).ToList();
        if (games.Count == 0) { _log.Warn("Library: no games are selected."); return; }
        SendToCleanup?.Invoke(games);
    }

    [RelayCommand]
    private async Task ShowInFolder(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (folder is not null) await _dialogs.OpenFolderAsync(folder);
    }
}
