using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Library;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>One file (or folder) of a game, as the details panel lists it.</summary>
public sealed record GameFileRow(string Name, string Format, string Size, string Folder, string Disc, string Path);

/// <summary>One game in the Library list.</summary>
public sealed class GameRowViewModel(Game game)
{
    public Game Game { get; } = game;
    public string Title => Game.Title;
    public string System => Game.System;
    public string Formats => Game.Formats;
    public string DiscsText => Game.Discs > 1 ? $"{Game.Discs} discs" : "";
    public string SizeText => ChdJobs.Size(Game.Size);
    public GameStatus Status { get; } = game.Status;

    public string StatusText => Status switch
    {
        GameStatus.NeedsConverting => "Needs converting",
        GameStatus.OriginalsRemain => "Originals still here",
        GameStatus.Converted => "Converted",
        GameStatus.Duplicate => "Duplicate",
        _ => "Ready",
    };

    public IReadOnlyList<GameFileRow> Files => Game.Items.Select(i => new GameFileRow(
        global::System.IO.Path.GetFileName(global::System.IO.Path.TrimEndingDirectorySeparator(i.Path)), i.FormatLabel, ChdJobs.Size(i.Size),
        global::System.IO.Path.GetDirectoryName(i.Path) ?? "", i.Disc, i.Path)).ToList();
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

    public IReadOnlyList<string> Folders => _settings.Library.Folders;
    public bool HasFolders => Folders.Count > 0;

    public BulkObservableCollection<GameRowViewModel> VisibleGames { get; } = [];
    private List<GameRowViewModel> _games = [];

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
        var chd = value?.Game.Items.FirstOrDefault(i => i.FormatId == "chd" && File.Exists(i.Path));
        ShowsChdInfo = chd is not null;
        if (chd is not null) _ = ChdInfo.LoadAsync(chd.Path);
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
        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(HasFolders));
        if (added.Count == 0) return;
        _log.Info($"Library: added {string.Join(", ", added)}");
        await RescanAsync();
    }

    [RelayCommand]
    private async Task RemoveFolder(string folder)
    {
        _settings.Library.RemoveFolder(folder);
        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(HasFolders));
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
                var scanned = LibraryScanner.Scan(folders, previous, progress, cts.Token);
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

    private void Show(LibraryIndex index)
    {
        var keep = SelectedGame?.Game.Key;
        _games = index.Games().Select(g => new GameRowViewModel(g)).ToList();
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
        bool Match(GameRowViewModel g) =>
            (SelectedSystem == AllSystems || g.System == SelectedSystem) &&
            (status is null || g.Status == status) &&
            words.All(w => g.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || g.Formats.Contains(w, StringComparison.OrdinalIgnoreCase));
        VisibleGames.ReplaceAll(_games.Where(Match));
        var size = VisibleGames.Sum(g => g.Game.Size);
        var toConvert = VisibleGames.Count(g => g.Status == GameStatus.NeedsConverting);
        Summary = _games.Count == 0 ? "" :
            $"{VisibleGames.Count:N0} of {_games.Count:N0} games · {ChdJobs.Size(size)} · {toConvert:N0} need converting";
    }

    // ---- Acting on games ----

    private List<GameRowViewModel> Chosen() =>
        SelectedGames?.OfType<GameRowViewModel>().ToList() is { Count: > 0 } many ? many
        : SelectedGame is { } one ? [one] : [];

    /// <summary>Sends the chosen games' sources (what still needs converting) to the Convert page.</summary>
    [RelayCommand]
    private void ConvertSelected()
    {
        var paths = Chosen().SelectMany(g => g.Game.Items).Where(i => i.IsSource).Select(i => i.Path).Distinct().ToList();
        if (paths.Count == 0) { _log.Warn("Library: nothing in the selected games needs converting."); return; }
        SendToConvert?.Invoke(paths);
    }

    /// <summary>Sends the chosen games' CHDs to the Verify page.</summary>
    [RelayCommand]
    private void VerifySelected()
    {
        var paths = Chosen().SelectMany(g => g.Game.Items).Where(i => i.FormatId == "chd").Select(i => i.Path).ToList();
        if (paths.Count == 0) { _log.Warn("Library: the selected games have no CHDs to verify."); return; }
        SendToVerify?.Invoke(paths);
    }

    [RelayCommand]
    private async Task ShowInFolder(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (folder is not null) await _dialogs.OpenFolderAsync(folder);
    }
}
