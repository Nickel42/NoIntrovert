using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MainViewModel(AppSettings settings, IDialogs dialogs)
    {
        Settings = new SettingsViewModel(settings, dialogs);
        Log = new LogViewModel(dialogs, settings);
        var queue = new JobQueue(() => (int)Settings.Performance.ParallelJobs);
        Queue = new QueueViewModel(queue);
        Convert = new ConvertViewModel(Settings, Log, dialogs, queue);
        Verify = new VerifyViewModel(Settings, Log, dialogs, queue);
        Tests = new TestsViewModel(Settings, Log, dialogs, queue);
        Library = new LibraryViewModel(Settings, Log, dialogs);
        Library.SendToConvert = paths => { Show("convert"); Convert.AddPaths(paths); };
        Library.SendToVerify = paths => { Show("verify"); Verify.AddPaths(paths); };
        Cleanup = new CleanupViewModel(Settings, Log, queue, () => Library.Index, dialogs)
        {
            SendToVerify = paths => { Show("verify"); Verify.AddPaths(paths); },
            Rescan = () => Library.RescanNowAsync(),
        };
        Library.SendToCleanup = games => { Show("cleanup"); Cleanup.UseGames(games); };
        Library.IndexShown += index => _ = UpdateDatsAsync(index.Items.Select(i => i.System), startup: true);
        // The index already in memory (loaded at start, or the last scan), not library.json read again on the UI thread.
        Settings.Dats.UpdateRequested = () => UpdateDatsAsync(Library.Index.Items.Select(i => i.System), startup: false);
        Library.IndexShown += index => _ = CheckEsdeAsync(startup: true);
        Settings.Dats.EsdeCheckRequested = () => CheckEsdeAsync(startup: false);
        Settings.Library.AddFoldersRequested = Library.AddFoldersAsync;
        Settings.Library.RemoveFolderRequested = Library.RemoveFolderCommand.ExecuteAsync;
        Settings.Library.EmulatorDataCheckRequested = CheckEmulatorDataAsync;
        Settings.Library.CanCheckEmulatorData = true;

        // The one place pages are listed: the rail, --page and drops all follow it.
        Pages =
        [
            new("library", "Optimize", "M4 5h4v14H4zM10 5h4v14h-4zM16 6l3.5-1 3 13.5-3.5 1z", Library, async p =>
            {
                // Folders become library folders; loose files go to Convert, which is where they'd be used.
                var folders = p.Where(Directory.Exists).ToList();
                if (folders.Count > 0) await Library.AddFoldersAsync(folders);
                var files = p.Where(File.Exists).ToList();
                if (files.Count > 0) { Show("convert"); Convert.AddPaths(files); }
            }, RailSection.Library, "Verify, Rename, Convert, Organize"),
            new("convert", "Convert", "M4 8h13l-3-3M20 16H7l3 3", Convert, p => { Convert.AddPaths(p); return Task.CompletedTask; }),
            new("verify", "Verify", "M5 12l4 4L19 6", Verify, p => { Verify.AddPaths(p); return Task.CompletedTask; }),
            new("cleanup", "Organize", "M4 6a1 1 0 0 1 1-1h5l2 2h7a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1zM4 10h16", Cleanup),
            new("tests", "Library tests", "M9 3h6M10 3v6l-5 9a2 2 0 0 0 2 3h10a2 2 0 0 0 2-3l-5-9V3", Tests, p => { Tests.AddPaths(p); return Task.CompletedTask; }),
            new("settings", "Settings", "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6zM4 12h2M18 12h2M12 4v2M12 18v2M6.3 6.3l1.4 1.4M16.3 16.3l1.4 1.4M6.3 17.7l1.4-1.4M16.3 7.7l1.4-1.4", Settings,
                section: RailSection.Bottom),
        ];
        LibraryPages = Pages.Where(p => p.Section == RailSection.Library).ToList();
        FilePages = Pages.Where(p => p.Section == RailSection.Files).ToList();
        BottomPages = Pages.Where(p => p.Section == RailSection.Bottom).ToList();
        StartPages = StartPagesFor(Pages);
        Settings.General.StartPages = StartPages;
        foreach (var tab in new BatchTabViewModel[] { Convert, Verify, Tests })
            tab.OpenSettingsRequested = () => Show("settings");

        // The Library tests are a developer bench: only in Power-user mode.
        UpdateVisibility();
        Settings.General.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GeneralSettingsViewModel.PowerUserMode)) UpdateVisibility();
        };
        SelectedPage = Pages.First(p => p.Id == StartPageId(settings.General.StartPage, settings.Library.Folders.Count > 0));

        try
        {
            LibraryVersion = $"{LibChdKit.Chdman.Version} · ABI {LibChdKit.Chdman.AbiVersion}";
            Log.Info($"Loaded {LibraryVersion}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LibraryVersion = "lib-chd-kit not loaded";
            Log.Error($"chdkit.dll could not be loaded: {ex.Message}. Build lib-chd-kit and rebuild this app.");
        }
        try
        {
            RvzLibraryVersion = $"{LibWiiGameCubeKit.WiiGameCubeKit.Version} · ABI {LibWiiGameCubeKit.WiiGameCubeKit.AbiVersion}";
            Log.Info($"Loaded {RvzLibraryVersion}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            RvzLibraryVersion = "lib-wii-gamecube-kit not loaded";
            Log.Warn($"lib-wii-gamecube-kit could not be loaded ({ex.Message}); GameCube/Wii images can't be converted.");
        }
        Settings.Diagnostics.LibraryVersions = $"{LibraryVersion}\n{RvzLibraryVersion}";

        // Job folders left by a crashed run are deleted in the background, so the window doesn't wait for them.
        var tempFolder = settings.Performance.EffectiveTempFolder;
        _ = Task.Run(() => WorkPaths.SweepStale(tempFolder));
        foreach (var problem in Core.Formats.TargetOverrides.Problems(settings.TargetFormats)) Log.Warn($"Target formats: {problem}");
    }

    public SettingsViewModel Settings { get; }
    public LogViewModel Log { get; }

    private bool _checkedDats;

    /// <summary>
    /// Brings the DATs for these systems up to date (FR-2): at start when the
    /// setting is on (once, and again when a scan finds a system with no DAT
    /// yet), and whenever Update now is clicked. Never blocks the window.
    /// </summary>
    private async Task UpdateDatsAsync(IEnumerable<string?> systems, bool startup)
    {
        var keys = systems.Distinct().Select(Core.Formats.TargetFormats.ById).OfType<Core.Formats.SystemInfo>()
            .SelectMany(s => s.Dats ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var store = Core.Dats.DatStore.Default;
        if (startup)
        {
            if (!Settings.Dats.CheckOnStart) return;
            var missing = keys.Any(k => !File.Exists(store.PathOf(k)));
            if (_checkedDats && !missing) return;
        }
        if (Settings.Dats.IsUpdating) return;
        _checkedDats = true;
        Settings.Dats.IsUpdating = true;
        try
        {
            var result = await Task.Run(() => new Core.Dats.DatUpdater(store).UpdateAsync(keys));
            if (result.Changed) Log.Info($"Game databases: downloaded {string.Join(", ", result.Downloaded.Select(k => k[(k.IndexOf('/') + 1)..]))}");
            if (result.Problem is { } problem) Log.Warn($"Game databases: couldn't check for updates ({problem}); using the copies already here.");
            Settings.Dats.RefreshStatus(result.Problem);
        }
        finally
        {
            Settings.Dats.IsUpdating = false;
        }
    }

    private bool _checkedEsde;

    /// <summary>
    /// Brings ES-DE's system folders up to date (the Cleanup wizard's ES-DE layout):
    /// at start when the database check is on, at most once a day, and whenever
    /// Check now is clicked. Folders ES-DE dropped or renamed are logged.
    /// </summary>
    private async Task CheckEsdeAsync(bool startup)
    {
        var updater = new Core.Esde.EsdeUpdater(Core.Esde.EsdeStore.Default);
        if (startup && (_checkedEsde || !Settings.Dats.CheckOnStart || !updater.IsDue(DateTime.UtcNow))) return;
        if (Settings.Dats.IsCheckingEsde) return;
        _checkedEsde = true;
        Settings.Dats.IsCheckingEsde = true;
        try
        {
            var result = await Task.Run(() => updater.UpdateAsync());
            if (result.Downloaded) Log.Info($"ES-DE systems: updated to release {result.Ref}.");
            if (result.Problem is { } problem) Log.Warn($"ES-DE systems: couldn't check for updates ({problem}); using the list already here.");
            foreach (var missing in result.Missing) Log.Warn(missing);
            Settings.Dats.RefreshEsdeStatus(result.Problem);
        }
        finally
        {
            Settings.Dats.IsCheckingEsde = false;
        }
    }

    /// <summary>
    /// Settings › Library › Check now: downloads newer emulator data (Works
    /// with's cores and emulators) if there is any, and logs what happened.
    /// </summary>
    private async Task CheckEmulatorDataAsync()
    {
        if (Settings.Library.IsCheckingEmulatorData) return;
        Settings.Library.IsCheckingEmulatorData = true;
        try
        {
            var result = await Task.Run(() => Core.Emulators.EmulatorCatalog.CheckNowAsync());
            if (result.Updated)
            {
                Log.Info($"Emulator data: updated ({result.Source}).");
                Settings.DefaultPresets.EmulatorDataChanged(); // the "N of M" counts were worked out from the old data
            }
            else if (result.Problem is null) Log.Info($"Emulator data: already up to date ({result.Source}).");
            if (result.Problem is { } problem) Log.Warn($"Emulator data: couldn't check for updates ({problem}); using the data already here.");
            Settings.Library.RefreshEmulatorDataStatus(result.Source, result.Problem);
        }
        finally
        {
            Settings.Library.IsCheckingEmulatorData = false;
        }
    }

    /// <summary>The shared job queue, shown in the status bar and the queue drawer.</summary>
    public QueueViewModel Queue { get; }
    public LibraryViewModel Library { get; }
    public CleanupViewModel Cleanup { get; }
    public ConvertViewModel Convert { get; }
    public VerifyViewModel Verify { get; }
    public TestsViewModel Tests { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>The rail's LIBRARY section (Optimize).</summary>
    public IReadOnlyList<PageViewModel> LibraryPages { get; }

    /// <summary>The rail's FILES section (Convert, Verify, Organize, and Library tests in Power-user mode).</summary>
    public IReadOnlyList<PageViewModel> FilePages { get; }

    /// <summary>The pages below the rail's divider (Settings).</summary>
    public IReadOnlyList<PageViewModel> BottomPages { get; }

    /// <summary>
    /// The pages the app can open on, for Settings › General › Start on:
    /// "Automatic" (id "") first, then every page but Settings and Library tests.
    /// </summary>
    public IReadOnlyList<StartPageOption> StartPages { get; }

    /// <summary>Automatic, then every page in <paramref name="pages"/> a user can start on (not Settings or Library tests).</summary>
    internal static IReadOnlyList<StartPageOption> StartPagesFor(IEnumerable<PageViewModel> pages) =>
        [new("", "Automatic"), .. pages.Where(p => p.Id is not ("settings" or "tests")).Select(p => new StartPageOption(p.Id, p.Title))];

    /// <summary>
    /// The page to open on: the chosen start page when it's one a user can
    /// start on, otherwise (automatic, or an id that no longer exists) Optimize
    /// once the library has folders and Convert until then, where a first drop
    /// does something.
    /// </summary>
    internal static string StartPageId(string? chosen, bool libraryHasFolders)
    {
        var id = string.IsNullOrWhiteSpace(chosen) ? "" : PageFor(chosen.Trim());
        return id is "library" or "convert" or "verify" or "cleanup" ? id : libraryHasFolders ? "library" : "convert";
    }

    [ObservableProperty] public partial PageViewModel SelectedPage { get; set; }

    partial void OnSelectedPageChanged(PageViewModel oldValue, PageViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false; // null on the first selection
        newValue.IsSelected = true;
    }

    public string LibraryVersion { get; } = "";
    public string RvzLibraryVersion { get; } = "";

    /// <summary>The app's own version, for Settings › About.</summary>
    public string AppVersion { get; } = typeof(MainViewModel).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "";

    public bool IsAnyRunning => Queue.Queue.IsBusy;

    [RelayCommand]
    private void SelectPage(PageViewModel page) => SelectedPage = page;

    /// <summary>Shows the page with this id (or an old tab name), if it exists and is visible.</summary>
    public bool Show(string id)
    {
        var page = Pages.FirstOrDefault(p => p.Id == PageFor(id));
        if (page is null || !page.IsVisible) return false;
        SelectedPage = page;
        return true;
    }

    /// <summary>
    /// Old and alternative names map to the page that took them over: tab names
    /// from before the rail (--tab extract, ...), the removed CHD info page, and
    /// rail titles that differ from their page id (organize, optimize).
    /// </summary>
    internal static string PageFor(string name) => name.ToLowerInvariant() switch
    {
        "extract" or "archive" or "wiiu" or "info" => "convert",
        "organize" => "cleanup",
        "optimize" => "library",
        var other => other,
    };

    private void UpdateVisibility()
    {
        var power = Settings.General.PowerUserMode;
        var tests = Pages.First(p => p.Id == "tests");
        tests.IsVisible = power;
        if (!power && SelectedPage == tests) Show("convert");
    }

    /// <summary>
    /// Handles the command line: file and folder paths are added to the
    /// starting page (as if dropped), and --page NAME (or the older --tab
    /// NAME) picks that page.
    /// </summary>
    public async Task ApplyCommandLineAsync(IReadOnlyList<string> args)
    {
        var paths = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--page" or "--tab" && i + 1 < args.Count)
                Show(args[++i]);
            else if (File.Exists(args[i]) || Directory.Exists(args[i]))
                paths.Add(Path.GetFullPath(args[i]));
        }
        if (paths.Count > 0) await DropAsync(paths);
    }

    /// <summary>Drops go to the page that's showing; a page that doesn't take files hands them to Convert.</summary>
    public async Task DropAsync(IReadOnlyList<string> paths)
    {
        if (SelectedPage.Accept is null) Show("convert");
        await SelectedPage.Accept!(paths);
    }
}
