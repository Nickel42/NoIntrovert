using CommunityToolkit.Mvvm.ComponentModel;
using NoIntrovert.Core;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MainViewModel(AppSettings settings, IDialogs dialogs)
    {
        Settings = new SettingsViewModel(settings, dialogs);
        Log = new LogViewModel(dialogs, settings.VerboseLog);
        Convert = new ConvertViewModel(Settings, Log, dialogs);
        Verify = new VerifyViewModel(Settings, Log, dialogs);
        Extract = new ExtractViewModel(Settings, Log, dialogs);
        Archive = new ArchiveViewModel(Settings, Log, dialogs);
        Info = new InfoViewModel(Log, dialogs);
        Tests = new TestsViewModel(Settings, Log, dialogs);

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
            Log.Info($"Loaded {LibDolphinRvz.DolphinRvz.Version} · ABI {LibDolphinRvz.DolphinRvz.AbiVersion}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Log.Warn($"libdolphinrvz could not be loaded ({ex.Message}); GameCube/Wii images can't be converted.");
        }

        WorkPaths.SweepStale(settings.EffectiveTempFolder);
    }

    public SettingsViewModel Settings { get; }
    public LogViewModel Log { get; }
    public ConvertViewModel Convert { get; }
    public VerifyViewModel Verify { get; }
    public ExtractViewModel Extract { get; }
    public ArchiveViewModel Archive { get; }
    public InfoViewModel Info { get; }
    public TestsViewModel Tests { get; }

    public string LibraryVersion { get; }

    [ObservableProperty] public partial int SelectedTab { get; set; }

    public bool IsAnyRunning => Convert.IsRunning || Verify.IsRunning || Extract.IsRunning || Archive.IsRunning || Tests.IsRunning;

    private static readonly string[] TabNames = ["convert", "verify", "extract", "archive", "info", "tests"];

    /// <summary>
    /// Handles the command line: file and folder paths are added to the
    /// starting tab (as if dropped), and --tab NAME picks that tab.
    /// </summary>
    public async Task ApplyCommandLineAsync(IReadOnlyList<string> args)
    {
        var paths = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--tab" && i + 1 < args.Count)
            {
                var index = Array.IndexOf(TabNames, args[++i].ToLowerInvariant());
                if (index >= 0) SelectedTab = index;
            }
            else if (File.Exists(args[i]) || Directory.Exists(args[i]))
            {
                paths.Add(Path.GetFullPath(args[i]));
            }
        }
        if (paths.Count > 0) await DropAsync(paths);
    }

    /// <summary>Routes dropped CHDs to the CHD Info tab when it's showing; everything else goes to the current batch tab.</summary>
    public async Task DropAsync(IReadOnlyList<string> paths)
    {
        switch (SelectedTab)
        {
            case 0: Convert.AddPaths(paths); break;
            case 1: Verify.AddPaths(paths); break;
            case 2: Extract.AddPaths(paths); break;
            case 3: Archive.AddPaths(paths); break;
            case 4:
                var chd = paths.FirstOrDefault(p => p.EndsWith(".chd", StringComparison.OrdinalIgnoreCase));
                if (chd is not null) await Info.LoadAsync(chd);
                break;
            case 5: Tests.AddPaths(paths); break;
        }
    }
}
