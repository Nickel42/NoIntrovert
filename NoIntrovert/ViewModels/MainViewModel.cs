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

        // The one place pages are listed: the rail, --page and drops all follow it.
        Pages =
        [
            new("convert", "Convert", "M4 8h13l-3-3M20 16H7l3 3", Convert, p => { Convert.AddPaths(p); return Task.CompletedTask; }),
            new("verify", "Verify", "M5 12l4 4L19 6", Verify, p => { Verify.AddPaths(p); return Task.CompletedTask; }),
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
        SelectedPage = Pages[0];

        try
        {
            LibraryVersion = $"{LibChdman.Chdman.Version} · ABI {LibChdman.Chdman.AbiVersion}";
            Log.Info($"Loaded {LibraryVersion}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LibraryVersion = "libchdman not loaded";
            Log.Error($"chdman.dll could not be loaded: {ex.Message}. Build libchdman and rebuild this app.");
        }
        try
        {
            RvzLibraryVersion = $"{LibDolphinRvz.DolphinRvz.Version} · ABI {LibDolphinRvz.DolphinRvz.AbiVersion}";
            Log.Info($"Loaded {RvzLibraryVersion}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            RvzLibraryVersion = "libdolphinrvz not loaded";
            Log.Warn($"libdolphinrvz could not be loaded ({ex.Message}); GameCube/Wii images can't be converted.");
        }
        Settings.Diagnostics.LibraryVersions = $"{LibraryVersion}\n{RvzLibraryVersion}";

        WorkPaths.SweepStale(settings.Performance.EffectiveTempFolder);
    }

    public SettingsViewModel Settings { get; }
    public LogViewModel Log { get; }

    /// <summary>The shared job queue, shown in the status bar and the queue drawer.</summary>
    public QueueViewModel Queue { get; }
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
        if (!power && SelectedPage == tests) SelectedPage = Pages[0];
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
        if (SelectedPage.Accept is null) SelectedPage = Pages[0];
        await SelectedPage.Accept!(paths);
    }
}
