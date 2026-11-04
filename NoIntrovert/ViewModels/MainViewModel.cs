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
        Log = new LogViewModel(dialogs, settings.General.VerboseLog);
        var queue = new JobQueue(() => (int)Settings.Performance.ParallelJobs);
        Queue = new QueueViewModel(queue);
        Convert = new ConvertViewModel(Settings, Log, dialogs, queue);
        Verify = new VerifyViewModel(Settings, Log, dialogs, queue);
        Info = new InfoViewModel(Log, dialogs);
        Tests = new TestsViewModel(Settings, Log, dialogs, queue);
        Library = new LibraryViewModel(Settings, Log, dialogs);
        Library.SendToConvert = paths => { Show("convert"); Convert.AddPaths(paths); };
        Library.SendToVerify = paths => { Show("verify"); Verify.AddPaths(paths); };
        Cleanup = new CleanupViewModel(Settings, Log, queue, () => Library.Index)
        {
            SendToVerify = paths => { Show("verify"); Verify.AddPaths(paths); },
            Rescan = () => Library.RescanNowAsync(),
        };
        Library.SendToCleanup = games => { Show("cleanup"); Cleanup.UseGames(games); };
        Library.IndexShown += index => _ = UpdateDatsAsync(index.Items.Select(i => i.System), startup: true);
        Settings.Dats.UpdateRequested = () => UpdateDatsAsync(Core.Library.LibraryIndex.Load().Items.Select(i => i.System), startup: false);
        Library.IndexShown += _ => CheckEsdeAsync(startup: true);
        Settings.Dats.EsdeCheckRequested = () => CheckEsdeAsync(startup: false);
        Settings.Library.AddFoldersRequested = Library.AddFoldersAsync;
        Settings.Library.RemoveFolderRequested = Library.RemoveFolderCommand.ExecuteAsync;

        // The one place pages are listed: the rail, --page and drops all follow it.
        Pages =
        [
            new("library", "Library", "M4 5h4v14H4zM10 5h4v14h-4zM16 6l3.5-1 3 13.5-3.5 1z", Library, async p =>
            {
                // Folders become library folders; loose files go to Convert, which is where they'd be used.
                var folders = p.Where(Directory.Exists).ToList();
                if (folders.Count > 0) await Library.AddFoldersAsync(folders);
                var files = p.Where(File.Exists).ToList();
                if (files.Count > 0) { Show("convert"); Convert.AddPaths(files); }
            }),
            new("convert", "Convert", "M4 8h13l-3-3M20 16H7l3 3", Convert, p => { Convert.AddPaths(p); return Task.CompletedTask; }),
            new("verify", "Verify & match", "M5 12l4 4L19 6", Verify, p => { Verify.AddPaths(p); return Task.CompletedTask; }),
            new("cleanup", "Cleanup", "M4 7h16M10 11v6M14 11v6M5 7l1 12a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2l1-12M9 7V4h6v3", Cleanup),
            new("info", "CHD info", "M12 8h.01M11 12h1v5h1M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z", Info, async p =>
            {
                if (p.FirstOrDefault(x => x.EndsWith(".chd", StringComparison.OrdinalIgnoreCase)) is { } chd) await Info.LoadAsync(chd);
            }),
            new("tests", "Library tests", "M9 3h6M10 3v6l-5 9a2 2 0 0 0 2 3h10a2 2 0 0 0 2-3l-5-9V3", Tests, p => { Tests.AddPaths(p); return Task.CompletedTask; }),
            new("settings", "Settings", "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6zM4 12h2M18 12h2M12 4v2M12 18v2M6.3 6.3l1.4 1.4M16.3 16.3l1.4 1.4M6.3 17.7l1.4-1.4M16.3 7.7l1.4-1.4", Settings),
        ];
        foreach (var tab in new BatchTabViewModel[] { Convert, Verify, Tests })
            tab.OpenSettingsRequested = () => Show("settings");

        // The Library tests are a developer bench: only in Power-user mode.
        UpdateVisibility();
        Settings.General.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GeneralSettingsViewModel.PowerUserMode)) UpdateVisibility();
        };
        // Open on the Library once it has folders; until then on Convert, where a first drop does something.
        SelectedPage = Pages.First(p => p.Id == (settings.Library.Folders.Count > 0 ? "library" : "convert"));

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

        WorkPaths.SweepStale(settings.Performance.EffectiveTempFolder);
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

    /// <summary>The shared job queue, shown in the status bar and the queue drawer.</summary>
    public QueueViewModel Queue { get; }
    public LibraryViewModel Library { get; }
    public CleanupViewModel Cleanup { get; }
    public ConvertViewModel Convert { get; }
    public VerifyViewModel Verify { get; }
    public InfoViewModel Info { get; }
    public TestsViewModel Tests { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty] public partial PageViewModel SelectedPage { get; set; }

    partial void OnSelectedPageChanged(PageViewModel oldValue, PageViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false; // null on the first selection
        newValue.IsSelected = true;
    }

    public string LibraryVersion { get; } = "";
    public string RvzLibraryVersion { get; } = "";

    /// <summary>The app's own version, for the rail.</summary>
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

    /// <summary>Tab names from before the rail (--tab extract, ...) map to the page that took them over.</summary>
    private static string PageFor(string name) => name.ToLowerInvariant() switch
    {
        "extract" or "archive" or "wiiu" => "convert",
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
