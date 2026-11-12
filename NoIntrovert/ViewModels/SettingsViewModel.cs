using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using LibWiiGameCubeKit;
using NoIntrovert.Core;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Emulators;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The app's settings, one view model per section (General, Output,
/// Performance, Log, Library, then the advanced ones: presets, CHD,
/// GameCube/Wii, archives, databases, target formats, keys, diagnostics),
/// each bound two-way and saved as it changes: the Settings page has no Save
/// button. Batches run on a snapshot (<see cref="Snapshot"/>), so edits during
/// a run apply to the next one.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _s;

    public SettingsViewModel(AppSettings settings, IDialogs dialogs)
    {
        _s = settings;
        General = new GeneralSettingsViewModel(settings);
        Output = new OutputSettingsViewModel(settings, dialogs);
        Performance = new PerformanceSettingsViewModel(settings, dialogs);
        Log = new LogSettingsViewModel(settings, dialogs);
        Chd = new ChdSettingsViewModel(settings);
        Nintendo = new NintendoSettingsViewModel(settings);
        Archives = new ArchiveSettingsViewModel(settings);
        Keys = new KeySettingsViewModel(settings, dialogs);
        Diagnostics = new DiagnosticsSettingsViewModel(settings, dialogs);
        Library = new LibrarySettingsViewModel(settings, dialogs);
        Dats = new DatSettingsViewModel(settings, dialogs);
        TargetFormats = new TargetFormatSettingsViewModel(settings, dialogs);
        Presets = new PresetsViewModel(settings, this);
        About = new AboutViewModel(Diagnostics, dialogs);
    }

    public GeneralSettingsViewModel General { get; }
    public OutputSettingsViewModel Output { get; }
    public PerformanceSettingsViewModel Performance { get; }
    public ChdSettingsViewModel Chd { get; }
    public NintendoSettingsViewModel Nintendo { get; }
    public ArchiveSettingsViewModel Archives { get; }
    public KeySettingsViewModel Keys { get; }
    public DiagnosticsSettingsViewModel Diagnostics { get; }
    public LibrarySettingsViewModel Library { get; }
    public DatSettingsViewModel Dats { get; }
    public TargetFormatSettingsViewModel TargetFormats { get; }
    public PresetsViewModel Presets { get; }
    public LogSettingsViewModel Log { get; }
    public AboutViewModel About { get; }

    /// <summary>The Default presets card: one preset per system, then the saved presets with Manage….</summary>
    public DefaultPresetsViewModel DefaultPresets => _defaultPresets ??= new DefaultPresetsViewModel(_s);

    private DefaultPresetsViewModel? _defaultPresets;

    /// <summary>Every section, in the order the Settings page shows them.</summary>
    public IReadOnlyList<SettingsSectionViewModel> Sections => [General, Output, Performance, Log, Library, Presets, Nintendo, Archives, Dats, TargetFormats, Keys, Chd, Diagnostics];

    public AppSettings Snapshot() => _s.Clone();
    public void Save() => _s.Save();
}

/// <summary>
/// One section of the settings. Only real changes are saved: the window's
/// two-way bindings write their current values back when it opens, and that
/// mustn't rewrite the file.
/// </summary>
public abstract class SettingsSectionViewModel(AppSettings settings) : ViewModelBase
{
    protected AppSettings S { get; } = settings;

    /// <summary>The section's name on the Settings page.</summary>
    public abstract string Title { get; }

    /// <summary>Applies a change and saves it; returns false (no save, no event) when nothing changed.</summary>
    protected bool Set<T>(T value, Action<AppSettings, T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Change(s => apply(s, value))) return false;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>Runs <paramref name="apply"/> and saves only if the settings' JSON is different afterwards.</summary>
    protected bool Change(Action<AppSettings> apply)
    {
        var before = S.ToJson();
        apply(S);
        if (S.ToJson() == before) return false;
        S.Save();
        return true;
    }
}

public sealed class GeneralSettingsViewModel(AppSettings settings) : SettingsSectionViewModel(settings)
{
    public override string Title => "General";

    public static IReadOnlyList<Choice<AppTheme>> Themes { get; } =
    [
        new(AppTheme.System, "System"),
        new(AppTheme.Dark, "Dark"),
        new(AppTheme.Light, "Light"),
    ];

    /// <summary>The app's colours; App applies a change at once.</summary>
    public Choice<AppTheme> SelectedTheme
    {
        get => Themes.FirstOrDefault(t => t.Value == S.General.Theme) ?? Themes[0];
        set { if (value is not null) ChooseTheme(value.Value); }
    }

    public AppTheme Theme => S.General.Theme;

    // The Theme segmented control: one radio button per choice.
    public bool IsSystemTheme { get => Theme == AppTheme.System; set { if (value) ChooseTheme(AppTheme.System); } }
    public bool IsDarkTheme { get => Theme == AppTheme.Dark; set { if (value) ChooseTheme(AppTheme.Dark); } }
    public bool IsLightTheme { get => Theme == AppTheme.Light; set { if (value) ChooseTheme(AppTheme.Light); } }

    private void ChooseTheme(AppTheme theme)
    {
        if (!Set(theme, (s, v) => s.General.Theme = v, nameof(SelectedTheme))) return;
        foreach (var name in new[] { nameof(Theme), nameof(IsSystemTheme), nameof(IsDarkTheme), nameof(IsLightTheme) }) OnPropertyChanged(name);
    }

    private IReadOnlyList<StartPageOption> _startPages = [new("", "Automatic")];

    /// <summary>
    /// The pages the app can open on, by page id; "" (Automatic) picks Optimize
    /// once the library has folders, otherwise Convert. The main window fills
    /// it from its page list (<see cref="MainViewModel.StartPages"/>), so the
    /// names match the rail.
    /// </summary>
    public IReadOnlyList<StartPageOption> StartPages
    {
        get => _startPages;
        set
        {
            if (!SetProperty(ref _startPages, value)) return;
            OnPropertyChanged(nameof(SelectedStartPage));
        }
    }

    public StartPageOption SelectedStartPage
    {
        get => StartPages.FirstOrDefault(p => p.Id == S.General.StartPage) ?? StartPages[0];
        set { if (value is not null) Set(value.Id, (s, v) => s.General.StartPage = v); }
    }

    /// <summary>Shows Expert options, lib-chd-kit's own messages and the Library tests tab.</summary>
    public bool PowerUserMode { get => S.General.PowerUserMode; set => Set(value, (s, v) => s.General.PowerUserMode = v); }

    public bool VerboseLog { get => S.General.VerboseLog; set => Set(value, (s, v) => s.General.VerboseLog = v); }
    public bool LogProgress { get => S.General.LogProgress; set => Set(value, (s, v) => s.General.LogProgress = v); }

    // Remembered on close (the window saves once), not on every resize.
    public double WindowWidth { get => S.General.WindowWidth; set => S.General.WindowWidth = value; }
    public double WindowHeight { get => S.General.WindowHeight; set => S.General.WindowHeight = value; }
}

public sealed partial class OutputSettingsViewModel(AppSettings settings, IDialogs dialogs) : SettingsSectionViewModel(settings)
{
    public override string Title => "Output";

    public string Folder
    {
        get => S.Output.Folder;
        set { if (Set(value, (s, v) => s.Output.Folder = v)) { OnPropertyChanged(nameof(UseSourceFolder)); OnPropertyChanged(nameof(FolderText)); OnPropertyChanged(nameof(SaveFolderText)); } }
    }

    public bool UseSourceFolder => string.IsNullOrWhiteSpace(S.Output.Folder);
    public string FolderText => UseSourceFolder ? "Next to each source file" : S.Output.Folder;

    /// <summary>The run bar's default choice. Replace the originals is the only one that removes files.</summary>
    public static IReadOnlyList<Choice<SaveTo>> SaveToChoices { get; } =
    [
        new(SaveTo.NextToOriginals, "Next to the originals"),
        new(SaveTo.Folder, "A folder"),
        new(SaveTo.ReplaceOriginals, "Replace the originals ⚠"),
    ];

    public Choice<SaveTo> SelectedSaveTo
    {
        get => SaveToChoices.FirstOrDefault(c => c.Value == S.Output.SaveTo) ?? SaveToChoices[0];
        set { if (value is not null) _ = ChooseSaveToAsync(value.Value); }
    }

    /// <summary>
    /// Asks the user to confirm Replace the originals the first time it's
    /// chosen, as the run bar does (set by the Settings view). Without it,
    /// Replace can't be chosen until it has been confirmed once.
    /// </summary>
    public Func<Task<bool>>? ConfirmReplace { get; set; }

    /// <summary>
    /// Makes <paramref name="choice"/> the default Save to, after the one-time
    /// confirmation for Replace (shared with the run bar through
    /// <see cref="ReplaceConfirmed"/>). When that's refused, the dropdown goes
    /// back to the previous choice.
    /// </summary>
    public async Task ChooseSaveToAsync(SaveTo choice)
    {
        var ok = choice != SaveTo.ReplaceOriginals || S.Output.ReplaceConfirmed || await ConfirmReplaceOnceAsync();
        if (ok && Set(choice, (s, v) => s.Output.SaveTo = v, nameof(SaveTo)))
        {
            OnPropertyChanged(nameof(SelectedSaveTo));
            OnPropertyChanged(nameof(SavesToFolder));
            OnPropertyChanged(nameof(ReplacesOriginals));
            OnPropertyChanged(nameof(SavesNextToOriginals));
        }
        // The radio buttons have already shown the refused choice; put them back after the click is done.
        if (!ok) Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(SelectedSaveTo));
            OnPropertyChanged(nameof(SavesNextToOriginals));
            OnPropertyChanged(nameof(SavesToFolder));
            OnPropertyChanged(nameof(ReplacesOriginals));
        });
    }

    private async Task<bool> ConfirmReplaceOnceAsync()
    {
        if (ConfirmReplace is null || !await ConfirmReplace()) return false;
        ReplaceConfirmed = true;
        return true;
    }

    // The Save to radio buttons; checking one chooses it (Replace asks first).
    public bool SavesNextToOriginals { get => S.Output.SaveTo == SaveTo.NextToOriginals; set { if (value) _ = ChooseSaveToAsync(SaveTo.NextToOriginals); } }
    public bool SavesToFolder { get => S.Output.SaveTo == SaveTo.Folder; set { if (value) _ = ChooseSaveToAsync(SaveTo.Folder); } }
    public bool ReplacesOriginals { get => S.Output.SaveTo == SaveTo.ReplaceOriginals; set { if (value) _ = ChooseSaveToAsync(SaveTo.ReplaceOriginals); } }

    /// <summary>The folder Save to › A folder uses.</summary>
    public string SaveFolderText => string.IsNullOrWhiteSpace(S.Output.Folder) ? "No folder chosen yet" : S.Output.Folder;

    public bool Overwrite { get => S.Output.Overwrite; set => Set(value, (s, v) => s.Output.Overwrite = v); }
    public bool ScanSubfolders { get => S.Output.ScanSubfolders; set => Set(value, (s, v) => s.Output.ScanSubfolders = v); }
    /// <summary>The run bar's default Save to choice; the run bar changes it for its own runs only.</summary>
    public SaveTo SaveTo { get => S.Output.SaveTo; set => Set(value, (s, v) => s.Output.SaveTo = v); }

    /// <summary>Set once Replace the originals has been confirmed, so it's asked only the first time.</summary>
    public bool ReplaceConfirmed { get => S.Output.ReplaceConfirmed; set => Set(value, (s, v) => s.Output.ReplaceConfirmed = v); }
    public bool UseRecycleBin { get => S.Output.UseRecycleBin; set => Set(value, (s, v) => s.Output.UseRecycleBin = v); }
    public string RecycleBinLabel => $"Removed files go to the {FileRemover.BinName} (not deleted permanently)";
    public bool CanUseRecycleBin => FileRemover.CanUseBin;

    [RelayCommand]
    private async Task BrowseFolder()
    {
        var folder = await dialogs.PickFolderAsync("Choose where results go");
        if (folder is null) return;
        Folder = folder;
        await ChooseSaveToAsync(SaveTo.Folder);
    }

    [RelayCommand]
    private void UseSourceFolders() => Folder = "";
}

public sealed partial class PerformanceSettingsViewModel(AppSettings settings, IDialogs dialogs) : SettingsSectionViewModel(settings)
{
    public override string Title => "Performance";

    public decimal ParallelJobs
    {
        get => S.Performance.ParallelJobs;
        set
        {
            if (!Set((int)value, (s, v) => s.Performance.ParallelJobs = Math.Clamp(v, 1, MaxJobs))) return;
            DecreaseJobsCommand.NotifyCanExecuteChanged();
            IncreaseJobsCommand.NotifyCanExecuteChanged();
        }
    }

    public decimal ThreadsPerJob { get => S.Performance.ThreadsPerJob; set => Set((int)value, (s, v) => s.Performance.ThreadsPerJob = Math.Max(0, v)); }
    public int MaxThreads => Environment.ProcessorCount;

    /// <summary>The Jobs at once stepper's limit: one job per processor, at least 2.</summary>
    public int MaxJobs => Math.Max(2, MaxThreads);

    // The Jobs at once stepper (− 2 +).
    private bool CanDecreaseJobs => ParallelJobs > 1;
    private bool CanIncreaseJobs => ParallelJobs < MaxJobs;

    [RelayCommand(CanExecute = nameof(CanDecreaseJobs))]
    private void DecreaseJobs() => ParallelJobs--;

    [RelayCommand(CanExecute = nameof(CanIncreaseJobs))]
    private void IncreaseJobs() => ParallelJobs++;

    /// <summary>The working folder as typed; empty means the default, shown as the box's placeholder.</summary>
    public string TempFolder
    {
        get => S.Performance.TempFolder;
        set { if (Set(value?.Trim() ?? "", (s, v) => s.Performance.TempFolder = v)) OnPropertyChanged(nameof(TempFolderHint)); }
    }

    public string TempFolderHint => S.Performance.EffectiveTempFolder;

    /// <summary>"%TEMP%\NoIntrovert": the default working folder, as the empty box shows it.</summary>
    public static string DefaultTempFolderText => OperatingSystem.IsWindows() ? @"%TEMP%\NoIntrovert" : Path.Combine(Path.GetTempPath(), "NoIntrovert");

    [RelayCommand]
    private async Task BrowseTempFolder()
    {
        var folder = await dialogs.PickFolderAsync("Choose a folder for temporary files");
        if (folder is not null) TempFolder = folder;
    }

    [RelayCommand]
    private void UseDefaultTempFolder() => TempFolder = "";
}

/// <summary>The log: when it opens by itself, how long saved logs are kept, and where they are.</summary>
public sealed partial class LogSettingsViewModel(AppSettings settings, IDialogs dialogs) : SettingsSectionViewModel(settings)
{
    public override string Title => "Log";

    public bool OpenLogOnErrors { get => S.General.OpenLogOnErrors; set => Set(value, (s, v) => s.General.OpenLogOnErrors = v); }

    /// <summary>Days a saved log is kept (1 to 365).</summary>
    public decimal KeepLogsDays
    {
        get => S.General.KeepLogsDays;
        set { if (Set((int)value, (s, v) => s.General.KeepLogsDays = Math.Clamp(v, 1, 365))) OnPropertyChanged(nameof(SelectedKeepLogs)); }
    }

    private static readonly int[] KeepLogsPresets = [7, 14, 30, 90, 365];

    /// <summary>The Keep logs for dropdown: a week to a year, plus the saved value if it's none of those.</summary>
    public IReadOnlyList<Choice<int>> KeepLogsChoices =>
        KeepLogsPresets.Append(S.General.KeepLogsDays).Distinct().Order().Select(d => new Choice<int>(d, DaysText(d))).ToList();

    public Choice<int> SelectedKeepLogs
    {
        get => new(S.General.KeepLogsDays, DaysText(S.General.KeepLogsDays));
        set { if (value is not null) KeepLogsDays = value.Value; }
    }

    /// <summary>"1 day", "30 days", "1 year".</summary>
    internal static string DaysText(int days) => days switch { 1 => "1 day", 365 => "1 year", _ => $"{days} days" };

    /// <summary>Opens the folder the session logs are saved in, creating it first.</summary>
    [RelayCommand]
    private async Task OpenLogFolder()
    {
        try { Directory.CreateDirectory(AppSettings.LogDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        await dialogs.OpenFolderAsync(AppSettings.LogDirectory);
    }
}

public sealed class ChdSettingsViewModel : SettingsSectionViewModel
{
    public ChdSettingsViewModel(AppSettings settings) : base(settings)
    {
        // The custom text goes in first, so picking "Custom…" keeps the saved string instead of blanking it.
        var saved = S.Chd.Compression;
        var choice = CompressionChoices.FirstOrDefault(c => c.Value == saved) ?? CompressionChoices[^1];
        if (choice.Value != saved) _customCompression = saved;
        _selectedCompression = choice;
    }

    public override string Title => "CHD";

    public static IReadOnlyList<Choice<string>> CompressionChoices { get; } =
    [
        new("", "Default for the media type"),
        new("cdlz,cdzl,cdfl", "CD: LZMA + Deflate + FLAC (chdman default)"),
        new("cdzs,cdzl,cdfl", "CD: Zstandard + Deflate + FLAC"),
        new("cdzl,cdfl", "CD: Deflate + FLAC (fast)"),
        new("lzma,zlib,huff,flac", "DVD/HD: LZMA + Deflate + Huffman + FLAC (default)"),
        new("zstd", "DVD/HD: Zstandard only (fast)"),
        new("zlib", "Deflate only"),
        new("none", "No compression"),
        new("custom", "Custom…"),
    ];

    public static IReadOnlyList<Choice<IsoMode>> IsoModes { get; } =
    [
        new(IsoMode.Auto, "Auto (detect CD or DVD from the image)"),
        new(IsoMode.Dvd, "Always DVD (createdvd)"),
        new(IsoMode.Cd, "Always CD (createcd)"),
    ];

    public static IReadOnlyList<Choice<ExtractFormat>> ExtractFormats { get; } =
    [
        new(ExtractFormat.Auto, "Auto (cue/bin, gdi, iso or img by media)"),
        new(ExtractFormat.CueBin, "cue/bin"),
        new(ExtractFormat.Gdi, "gdi (GD-ROM)"),
        new(ExtractFormat.Iso, "iso"),
        new(ExtractFormat.Raw, "Raw image (iso/img/raw)"),
    ];

    public static IReadOnlyList<Choice<uint>> HunkSizes { get; } =
    [
        new(0u, "Default"),
        new(2448u * 4, "9,792 (CD default)"),
        new(2048u * 2, "4,096 (DVD default)"),
        new(2048u * 8, "16,384"),
        new(2448u * 8, "19,584"),
        new(2048u * 32, "65,536"),
    ];

    private Choice<string> _selectedCompression;
    private string _customCompression = "";

    public Choice<string> SelectedCompression
    {
        get => _selectedCompression;
        set
        {
            if (!SetProperty(ref _selectedCompression, value)) return;
            OnPropertyChanged(nameof(IsCustomCompression));
            Change(s => s.Chd.Compression = value.Value == "custom" ? _customCompression.Trim() : value.Value);
        }
    }

    public string CustomCompression
    {
        get => _customCompression;
        set
        {
            if (!SetProperty(ref _customCompression, value) || !IsCustomCompression) return;
            Change(s => s.Chd.Compression = value.Trim());
        }
    }

    public bool IsCustomCompression => _selectedCompression.Value == "custom";

    public Choice<IsoMode> SelectedIsoMode
    {
        get => IsoModes.First(m => m.Value == S.Chd.IsoMode);
        set => Set(value.Value, (s, v) => s.Chd.IsoMode = v);
    }

    public Choice<ExtractFormat> SelectedExtractFormat
    {
        get => ExtractFormats.First(m => m.Value == S.Chd.ExtractFormat);
        set => Set(value.Value, (s, v) => s.Chd.ExtractFormat = v);
    }

    public Choice<uint> SelectedHunkSize
    {
        get => HunkSizes.FirstOrDefault(h => h.Value == S.Chd.HunkSize) ?? HunkSizes[0];
        set => Set(value.Value, (s, v) => s.Chd.HunkSize = v);
    }

    public bool VerifyAfterCreate { get => S.Chd.VerifyAfterCreate; set => Set(value, (s, v) => s.Chd.VerifyAfterCreate = v); }
    public bool SplitBin { get => S.Chd.SplitBin; set => Set(value, (s, v) => s.Chd.SplitBin = v); }
    public bool FixHeaderSha1 { get => S.Chd.FixHeaderSha1; set => Set(value, (s, v) => s.Chd.FixHeaderSha1 = v); }
}

public sealed class NintendoSettingsViewModel(AppSettings settings) : SettingsSectionViewModel(settings)
{
    public override string Title => "GameCube / Wii";

    public static IReadOnlyList<Choice<NintendoTarget>> Targets { get; } =
    [
        new(NintendoTarget.Rvz, "RVZ (Dolphin's format, smallest)"),
        new(NintendoTarget.Chd, "CHD (as a DVD)"),
    ];

    public static IReadOnlyList<Choice<RvzCompression>> Compressions { get; } =
    [
        new(RvzCompression.Zstd, "Zstandard (Dolphin's default)"),
        new(RvzCompression.Lzma2, "LZMA2 (smaller, much slower)"),
        new(RvzCompression.Lzma, "LZMA"),
        new(RvzCompression.Bzip2, "bzip2"),
        new(RvzCompression.None, "No compression"),
    ];

    public static IReadOnlyList<Choice<uint>> BlockSizes { get; } =
    [
        new(0x8000u, "32 KiB"),
        new(0x10000u, "64 KiB"),
        new(0x20000u, "128 KiB (Dolphin's default)"),
        new(0x40000u, "256 KiB"),
        new(0x80000u, "512 KiB"),
        new(0x100000u, "1 MiB"),
        new(0x200000u, "2 MiB"),
    ];

    public Choice<NintendoTarget> SelectedTarget
    {
        get => Targets.FirstOrDefault(t => t.Value == S.Nintendo.Target) ?? Targets[0];
        set { if (Set(value.Value, (s, v) => s.Nintendo.Target = v)) OnPropertyChanged(nameof(IsRvzTarget)); }
    }

    public bool IsRvzTarget => S.Nintendo.Target == NintendoTarget.Rvz;

    public Choice<RvzCompression> SelectedCompression
    {
        get => Compressions.FirstOrDefault(c => c.Value == S.Nintendo.Compression) ?? Compressions[0];
        set
        {
            // Keep the level inside the new method's range (Dolphin's dialog uses the same ranges).
            if (!Set(value.Value, (s, v) =>
                {
                    s.Nintendo.Compression = v;
                    s.Nintendo.CompressionLevel = Math.Clamp(s.Nintendo.CompressionLevel, (int)LevelMinimum, (int)LevelMaximum);
                })) return;
            OnPropertyChanged(nameof(CompressionLevel));
            OnPropertyChanged(nameof(LevelMaximum));
            OnPropertyChanged(nameof(HasLevel));
        }
    }

    public bool HasLevel => S.Nintendo.Compression != RvzCompression.None;
    public decimal LevelMinimum => 1;
    public decimal LevelMaximum => S.Nintendo.Compression == RvzCompression.Zstd ? 22 : 9;

    public decimal CompressionLevel
    {
        get => S.Nintendo.CompressionLevel;
        set => Set((int)value, (s, v) => s.Nintendo.CompressionLevel = Math.Clamp(v, 1, (int)LevelMaximum));
    }

    public Choice<uint> SelectedBlockSize
    {
        get => BlockSizes.FirstOrDefault(b => b.Value == S.Nintendo.BlockSize) ?? BlockSizes[2];
        set => Set(value.Value, (s, v) => s.Nintendo.BlockSize = v);
    }

    public bool Scrub { get => S.Nintendo.Scrub; set => Set(value, (s, v) => s.Nintendo.Scrub = v); }
}

public sealed class ArchiveSettingsViewModel(AppSettings settings) : SettingsSectionViewModel(settings)
{
    public override string Title => "Archives";

    public static IReadOnlyList<Choice<ArchiveAction>> Actions { get; } =
    [
        new(ArchiveAction.Compress, "Compress files and folders"),
        new(ArchiveAction.Convert, "Convert archives (zip ↔ 7z)"),
        new(ArchiveAction.Extract, "Extract archives (zip, 7z)"),
    ];

    public static IReadOnlyList<Choice<ArchiveFormat>> Formats { get; } =
    [
        new(ArchiveFormat.SevenZip, "7z (LZMA2, smallest)"),
        new(ArchiveFormat.Zip, "zip (Deflate, opens anywhere)"),
    ];

    public static IReadOnlyList<Choice<ArchiveLevel>> Levels { get; } =
    [
        new(ArchiveLevel.Store, "Store (no compression)"),
        new(ArchiveLevel.Fastest, "Fastest"),
        new(ArchiveLevel.Fast, "Fast"),
        new(ArchiveLevel.Normal, "Normal"),
        new(ArchiveLevel.Maximum, "Maximum"),
        new(ArchiveLevel.Ultra, "Ultra (slowest)"),
    ];

    public static IReadOnlyList<Choice<ArchiveGrouping>> Groupings { get; } =
    [
        new(ArchiveGrouping.PerItem, "One archive per file or folder in the list"),
        new(ArchiveGrouping.PerEntryInFolders, "One archive per file/subfolder inside each folder"),
        new(ArchiveGrouping.Single, "Everything into one archive"),
    ];

    public static IReadOnlyList<Choice<ConvertTarget>> ConvertTargets { get; } =
    [
        new(ConvertTarget.Swap, "Swap: zip → 7z, 7z → zip"),
        new(ConvertTarget.ToSevenZip, "Everything to 7z"),
        new(ConvertTarget.ToZip, "Everything to zip"),
    ];

    public Choice<ArchiveAction> SelectedAction
    {
        get => Actions.First(a => a.Value == S.Archives.Action);
        set
        {
            if (!Set(value.Value, (s, v) => s.Archives.Action = v)) return;
            OnPropertyChanged(nameof(IsCompress));
            OnPropertyChanged(nameof(IsConvert));
            OnPropertyChanged(nameof(IsExtract));
            OnPropertyChanged(nameof(IsWriting));
        }
    }

    public bool IsCompress => S.Archives.Action == ArchiveAction.Compress;
    public bool IsConvert => S.Archives.Action == ArchiveAction.Convert;
    public bool IsExtract => S.Archives.Action == ArchiveAction.Extract;

    /// <summary>Compressing and converting write archives; extracting only reads them.</summary>
    public bool IsWriting => !IsExtract;

    public bool ExtractToOwnFolder { get => S.Archives.ExtractToOwnFolder; set => Set(value, (s, v) => s.Archives.ExtractToOwnFolder = v); }

    public Choice<ArchiveFormat> SelectedFormat
    {
        get => Formats.First(f => f.Value == S.Archives.Format);
        set { if (Set(value.Value, (s, v) => s.Archives.Format = v)) OnPropertyChanged(nameof(IsSevenZip)); }
    }

    public bool IsSevenZip => S.Archives.Format == ArchiveFormat.SevenZip;

    public Choice<ArchiveLevel> SelectedLevel
    {
        get => Levels.First(l => l.Value == S.Archives.Level);
        set => Set(value.Value, (s, v) => s.Archives.Level = v);
    }

    public Choice<ArchiveGrouping> SelectedGrouping
    {
        get => Groupings.First(g => g.Value == S.Archives.Grouping);
        set { if (Set(value.Value, (s, v) => s.Archives.Grouping = v)) OnPropertyChanged(nameof(IsSingle)); }
    }

    public bool IsSingle => S.Archives.Grouping == ArchiveGrouping.Single;

    public Choice<ConvertTarget> SelectedConvertTarget
    {
        get => ConvertTargets.First(t => t.Value == S.Archives.ConvertTarget);
        set => Set(value.Value, (s, v) => s.Archives.ConvertTarget = v);
    }

    public bool Solid { get => S.Archives.Solid; set => Set(value, (s, v) => s.Archives.Solid = v); }
    public string SingleName { get => S.Archives.SingleName; set => Set(value, (s, v) => s.Archives.SingleName = v); }
    public bool TestAfter { get => S.Archives.TestAfter; set => Set(value, (s, v) => s.Archives.TestAfter = v); }
}

/// <summary>
/// The game databases: where they come from and how fresh they are, the
/// startup check, and the user's own DAT folder. <see cref="UpdateRequested"/>
/// is set by the main window, which knows the Library's systems.
/// </summary>
public sealed partial class DatSettingsViewModel(AppSettings settings, IDialogs dialogs) : SettingsSectionViewModel(settings)
{
    public override string Title => "Game databases";

    public bool CheckOnStart { get => S.Dats.CheckOnStart; set => Set(value, (s, v) => s.Dats.CheckOnStart = v); }

    public string OwnFolder
    {
        get => S.Dats.OwnFolder;
        set { if (Set(value, (s, v) => s.Dats.OwnFolder = v)) OnPropertyChanged(nameof(OwnFolderText)); }
    }

    public string OwnFolderText => string.IsNullOrWhiteSpace(S.Dats.OwnFolder) ? "None (only the downloaded databases)" : S.Dats.OwnFolder;

    /// <summary>"Game databases: libretro-database, updated 2 hours ago (12 databases)."</summary>
    [ObservableProperty] public partial string Status { get; set; } = Core.Dats.DatStore.Default.Describe();

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    public partial bool IsUpdating { get; set; }

    /// <summary>Runs an update for the Library's systems; set by the main window.</summary>
    public Func<Task>? UpdateRequested { get; set; }

    public void RefreshStatus(string? problem = null) =>
        Status = Core.Dats.DatStore.Default.Describe() + (problem is null ? "" : $" Last check failed: {problem}");

    private bool CanUpdateNow => !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanUpdateNow))]
    private async Task UpdateNow()
    {
        if (UpdateRequested is not null) await UpdateRequested();
    }

    /// <summary>"ES-DE systems: release v3.5.0, checked 2 hours ago (195 systems)." (the Cleanup wizard's folder names).</summary>
    [ObservableProperty] public partial string EsdeStatus { get; set; } = Core.Esde.EsdeStore.Default.Describe();

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckEsdeNowCommand))]
    public partial bool IsCheckingEsde { get; set; }

    /// <summary>Checks ES-DE's latest release for changed system folders; set by the main window.</summary>
    public Func<Task>? EsdeCheckRequested { get; set; }

    public void RefreshEsdeStatus(string? problem = null) =>
        EsdeStatus = Core.Esde.EsdeStore.Default.Describe() + (problem is null ? "" : $" Last check failed: {problem}");

    private bool CanCheckEsdeNow => !IsCheckingEsde;

    [RelayCommand(CanExecute = nameof(CanCheckEsdeNow))]
    private async Task CheckEsdeNow()
    {
        if (EsdeCheckRequested is not null) await EsdeCheckRequested();
    }

    [RelayCommand]
    private async Task BrowseOwnFolder()
    {
        var folder = await dialogs.PickFolderAsync("Choose a folder of your own DAT files");
        if (folder is not null) OwnFolder = folder;
    }

    [RelayCommand]
    private void ClearOwnFolder() => OwnFolder = "";
}

public sealed partial class KeySettingsViewModel(AppSettings settings, IDialogs dialogs) : SettingsSectionViewModel(settings)
{
    public override string Title => "Keys";

    public string WiiUKeysPath
    {
        get => S.Keys.WiiUKeysPath;
        set { if (Set(value, (s, v) => s.Keys.WiiUKeysPath = v)) OnPropertyChanged(nameof(WiiUKeysSummary)); }
    }

    /// <summary>Which keys.txt the Wii U tab will use, and how many keys it holds.</summary>
    public string WiiUKeysSummary
    {
        get
        {
            var keys = Core.WiiU.WuaJobs.LoadKeys(S.Keys.WiiUKeysPath);
            return keys.Files.Count == 0
                ? (string.IsNullOrWhiteSpace(S.Keys.WiiUKeysPath) ? "No keys.txt found in NoIntrovert's or Cemu's folder." : $"{S.Keys.WiiUKeysPath} doesn't exist.")
                : $"{keys.Keys.Count} key(s) in {string.Join(", ", keys.Files)}";
        }
    }

    [RelayCommand]
    private async Task BrowseWiiUKeys()
    {
        var file = await dialogs.PickFileAsync("Choose your keys.txt", "Key files", ["keys.txt", "*.txt"]);
        if (file is not null) WiiUKeysPath = file;
    }

    [RelayCommand]
    private void FindWiiUKeysAutomatically() => WiiUKeysPath = "";

    [RelayCommand]
    private void RefreshWiiUKeys() => OnPropertyChanged(nameof(WiiUKeysSummary));

    /// <summary>The folder with your 3DS keys (boot9.bin, aes_keys.txt, seeddb.bin); empty = Azahar's and Citra's own.</summary>
    public string ThreeDsKeysFolder
    {
        get => S.Keys.ThreeDsKeysFolder;
        set { if (Set(value ?? "", (s, v) => s.Keys.ThreeDsKeysFolder = v)) OnPropertyChanged(nameof(ThreeDsKeysSummary)); }
    }

    /// <summary>Which 3DS key files were found, and whether they're enough for encrypted games and .cia files.</summary>
    public string ThreeDsKeysSummary
    {
        get
        {
            var keys = Core.Ctr.CtrJobs.LoadKeys(S.Keys.ThreeDsKeysFolder, out var problems);
            var text = Core.Ctr.CtrJobs.Describe(keys);
            if (!string.IsNullOrWhiteSpace(S.Keys.ThreeDsKeysFolder) && !Directory.Exists(S.Keys.ThreeDsKeysFolder)) text = $"{S.Keys.ThreeDsKeysFolder} doesn't exist. " + text;
            return problems.Count == 0 ? text : text + " " + string.Join(" ", problems);
        }
    }

    [RelayCommand]
    private async Task BrowseThreeDsKeys()
    {
        var folder = await dialogs.PickFolderAsync("Choose the folder with your boot9.bin and aes_keys.txt");
        if (folder is not null) ThreeDsKeysFolder = folder;
    }

    [RelayCommand]
    private void FindThreeDsKeysAutomatically() => ThreeDsKeysFolder = "";

    [RelayCommand]
    private void RefreshThreeDsKeys() => OnPropertyChanged(nameof(ThreeDsKeysSummary));

    /// <summary>Your Switch prod.keys; empty = where Ryujinx or a yuzu-family emulator keeps it.</summary>
    public string SwitchKeysPath
    {
        get => S.Keys.SwitchKeysPath;
        set { if (Set(value ?? "", (s, v) => s.Keys.SwitchKeysPath = v)) OnPropertyChanged(nameof(SwitchKeysSummary)); }
    }

    /// <summary>Which prod.keys Switch conversions will use (found by lib-nx-kit).</summary>
    public string SwitchKeysSummary => Core.Switch.SwitchJobs.Describe(S.Keys.SwitchKeysPath);

    [RelayCommand]
    private async Task BrowseSwitchKeys()
    {
        var file = await dialogs.PickFileAsync("Choose your prod.keys", "Key files", ["prod.keys", "*.keys"]);
        if (file is not null) SwitchKeysPath = file;
    }

    [RelayCommand]
    private void FindSwitchKeysAutomatically() => SwitchKeysPath = "";

    [RelayCommand]
    private void RefreshSwitchKeys() => OnPropertyChanged(nameof(SwitchKeysSummary));
}

public sealed partial class LibrarySettingsViewModel(AppSettings settings, IDialogs dialogs) : SettingsSectionViewModel(settings)
{
    public override string Title => "Library";

    /// <summary>The folders Optimize looks through (a copy, so a list bound to it refreshes).</summary>
    public IReadOnlyList<string> Folders => S.Library.Folders.ToList();

    public bool HasFolders => S.Library.Folders.Count > 0;

    /// <summary>The folder the Cleanup wizard builds a new library in (FR-8), remembered for next time.</summary>
    public string NewLibraryFolder
    {
        get => S.Library.NewLibraryFolder;
        set => Set(value ?? "", (s, v) => s.Library.NewLibraryFolder = v);
    }

    /// <summary>Adds folders and rescans; set by the main window so the Library page follows. Without it the folders are just added.</summary>
    public Func<IReadOnlyList<string>, Task>? AddFoldersRequested { get; set; }

    /// <summary>Removes a folder and rescans; set by the main window. Without it the folder is just removed.</summary>
    public Func<string, Task>? RemoveFolderRequested { get; set; }

    [RelayCommand]
    private async Task AddFolder()
    {
        var folder = await dialogs.PickFolderAsync("Add a folder of games to the library");
        if (folder is null) return;
        if (AddFoldersRequested is not null) await AddFoldersRequested([folder]);
        else AddFolders([folder]);
    }

    [RelayCommand]
    private async Task RemoveFolderFromList(string folder)
    {
        if (RemoveFolderRequested is not null) await RemoveFolderRequested(folder);
        else RemoveFolder(folder);
    }

    /// <summary>"Emulator data: from libretro core info and ES-DE · 4 Nov 2026": where Works with's data comes from and how fresh it is.</summary>
    [ObservableProperty] public partial string EmulatorDataStatus { get; set; } = DescribeEmulatorData(EmulatorCatalog.Current.SourceText);

    /// <summary>Whether Check now can run: false until there is an emulator data source to check.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckEmulatorDataCommand))]
    public partial bool CanCheckEmulatorData { get; set; }

    /// <summary>True while Check now is downloading; the button is disabled meanwhile.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckEmulatorDataCommand))]
    public partial bool IsCheckingEmulatorData { get; set; }

    partial void OnIsCheckingEmulatorDataChanged(bool value)
    {
        if (value) EmulatorDataStatus = CheckingEmulatorData;
        else if (EmulatorDataStatus == CheckingEmulatorData) RefreshEmulatorDataStatus(EmulatorCatalog.Current.SourceText);
    }

    private const string CheckingEmulatorData = "Emulator data: checking for an update…";

    /// <summary>Checks the emulator data source for an update; set by the main window.</summary>
    public Func<Task>? EmulatorDataCheckRequested { get; set; }

    /// <summary>Shows the catalog's source and date, and why the last check failed if it did.</summary>
    public void RefreshEmulatorDataStatus(string sourceText, string? problem = null)
    {
        _emulatorSource = sourceText;
        _emulatorProblem = problem;
        EmulatorDataStatus = DescribeEmulatorData(sourceText) + (problem is null ? "" : $" Last check failed: {problem}");
    }

    private string _emulatorSource = EmulatorCatalog.Current.SourceText;
    private string? _emulatorProblem;

    partial void OnEmulatorDataStatusChanged(string value) => OnPropertyChanged(nameof(EmulatorDataDescription));

    /// <summary>
    /// The line under Emulator data: "Which emulators can open each format.
    /// From libretro core info and ES-DE, updated 2 Nov 2026."
    /// </summary>
    public string EmulatorDataDescription => IsCheckingEmulatorData
        ? EmulatorDataPurpose + " Checking for an update…"
        : DescribeEmulatorSource(_emulatorSource, _emulatorProblem);

    private const string EmulatorDataPurpose = "Which emulators can open each format.";

    /// <summary>"From libretro core info and ES-DE · 2 Nov 2026" becomes the Emulator data description.</summary>
    internal static string DescribeEmulatorSource(string sourceText, string? problem = null)
    {
        var source = sourceText.Trim().Replace(" · ", ", updated ");
        if (source.Length > 0 && !source.EndsWith('.')) source += ".";
        return $"{EmulatorDataPurpose} {source}" + (problem is null ? "" : $" Last check failed: {problem}");
    }

    /// <summary>"From libretro core info · 4 Nov 2026" becomes "Emulator data: from libretro core info · 4 Nov 2026".</summary>
    internal static string DescribeEmulatorData(string sourceText) =>
        "Emulator data: " + (sourceText.StartsWith("From ", StringComparison.Ordinal) ? "from " + sourceText[5..] : sourceText);

    private bool CanCheckEmulatorDataNow => CanCheckEmulatorData && !IsCheckingEmulatorData;

    [RelayCommand(CanExecute = nameof(CanCheckEmulatorDataNow))]
    private async Task CheckEmulatorData()
    {
        if (EmulatorDataCheckRequested is not null) await EmulatorDataCheckRequested();
    }

    public string FoldersSummary => S.Library.Folders.Count switch
    {
        0 => "No folders yet: add them on the Library page.",
        1 => "1 folder, managed on the Library page.",
        var n => $"{n} folders, managed on the Library page.",
    };

    public bool RescanOnStart { get => S.Library.RescanOnStart; set => Set(value, (s, v) => s.Library.RescanOnStart = v); }

    /// <summary>Where the Cleanup wizard puts games (M7 Q1).</summary>
    public static IReadOnlyList<Choice<LibraryLayout>> Layouts { get; } =
    [
        new(LibraryLayout.EsDe, "ES-DE format: roms/<system>/<game>"),
        new(LibraryLayout.InPlace, "Keep the current layout"),
    ];

    public Choice<LibraryLayout> SelectedLayout
    {
        get => Layouts.First(l => l.Value == S.Library.Layout);
        set { if (value is not null) Set(value.Value, (s, v) => s.Library.Layout = v); }
    }

    /// <summary>Adds folders not already there (or inside one that is); returns those added.</summary>
    public List<string> AddFolders(IEnumerable<string> folders)
    {
        var added = new List<string>();
        Change(s =>
        {
            foreach (var f in folders.Select(Core.Formats.ScanSet.Normalize))
            {
                if (s.Library.Folders.Any(have => Covers(have, f))) continue;
                // A new parent folder replaces the folders inside it.
                s.Library.Folders.RemoveAll(have => Covers(f, have));
                s.Library.Folders.Add(f);
                added.Add(f);
            }
        });
        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(FoldersSummary));
        return added;
    }

    public void RemoveFolder(string folder)
    {
        if (!Change(s => s.Library.Folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))) return;
        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(FoldersSummary));
    }

    private static bool Covers(string parent, string child) =>
        string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public sealed partial class DiagnosticsSettingsViewModel(AppSettings settings, IDialogs dialogs) : SettingsSectionViewModel(settings)
{
    public override string Title => "Diagnostics";

    /// <summary>The loaded native libraries and their versions (set once at startup).</summary>
    public string LibraryVersions { get; set; } = "";

    public string ChdmanExePath { get => S.Diagnostics.ChdmanExePath; set => Set(value, (s, v) => s.Diagnostics.ChdmanExePath = v); }

    [RelayCommand]
    private async Task BrowseChdmanExe()
    {
        var file = await dialogs.PickFileAsync("Find chdman", "chdman", OperatingSystem.IsWindows() ? ["chdman*.exe", "*.exe"] : ["chdman*"]);
        if (file is not null) ChdmanExePath = file;
    }
}

/// <summary>
/// Settings › About: the app's and the bundled tools' versions. Check for
/// updates opens the GitHub releases page in the browser (there is no built-in
/// updater and no network call); Licenses opens the folder of licence and
/// notice files that ships next to the app, or the repository's licence when
/// there is none.
/// </summary>
public sealed partial class AboutViewModel(DiagnosticsSettingsViewModel diagnostics, IDialogs dialogs) : ViewModelBase
{
    public const string ReleasesUrl = "https://github.com/Nickel42/NoIntrovert/releases";
    public const string LicenseUrl = "https://github.com/Nickel42/NoIntrovert#license";

    /// <summary>"NoIntrovert 0.9.0", from the assembly's informational version (as the rail shows it).</summary>
    public string Version { get; } = "NoIntrovert " + (typeof(AboutViewModel).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "unknown");

    /// <summary>"chdman from MAME 0.289 · Dolphin 2412": the bundled tools, from the libraries the main window loaded at startup.</summary>
    public string ToolVersions => DescribeTools(diagnostics.LibraryVersions);

    /// <summary>Opens a web page in the browser; set by the view.</summary>
    public Func<string, Task>? OpenUrl { get; set; }

    [RelayCommand]
    private Task CheckForUpdates() => OpenUrl?.Invoke(ReleasesUrl) ?? Task.CompletedTask;

    [RelayCommand]
    private async Task OpenLicenses()
    {
        var folder = FindLicenseFolder(AppContext.BaseDirectory);
        if (folder is not null) await dialogs.OpenFolderAsync(folder);
        else if (OpenUrl is not null) await OpenUrl(LicenseUrl);
    }

    /// <summary>
    /// The folder holding the licence and notice files (LICENSE*,
    /// THIRD_PARTY_NOTICES*), looking in <paramref name="appFolder"/> and, for
    /// a macOS bundle, its Resources folder; null when there are none.
    /// </summary>
    internal static string? FindLicenseFolder(string appFolder)
    {
        foreach (var folder in new[] { appFolder, Path.Combine(appFolder, "..", "Resources") })
        {
            try
            {
                if (Directory.Exists(folder) && Directory.EnumerateFiles(folder).Select(Path.GetFileName)
                        .Any(n => n!.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || n.StartsWith("THIRD_PARTY", StringComparison.OrdinalIgnoreCase)))
                    return Path.GetFullPath(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>
    /// Turns the loaded libraries' versions ("0.289 (mame0289) lib-chd-kit 1 · ABI 1",
    /// "lib-wii-gamecube-kit 0.1.0 (Dolphin 2412) · ABI 1", one per line) into
    /// "chdman from MAME 0.289 · Dolphin 2412". A library that didn't load
    /// keeps its "… not loaded" line; anything unrecognised is shown as it is.
    /// </summary>
    internal static string DescribeTools(string libraryVersions) =>
        string.Join(" · ", libraryVersions.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(DescribeTool));

    private static string DescribeTool(string line)
    {
        var mame = System.Text.RegularExpressions.Regex.Match(line, @"\(mame(\d)(\d+)\)");
        if (mame.Success) return $"chdman from MAME {mame.Groups[1].Value}.{mame.Groups[2].Value}";
        var dolphin = System.Text.RegularExpressions.Regex.Match(line, @"\(Dolphin ([^)]+)\)");
        if (dolphin.Success) return $"Dolphin {dolphin.Groups[1].Value}";
        var abi = line.IndexOf(" · ABI ", StringComparison.Ordinal);
        return abi >= 0 ? line[..abi] : line;
    }
}
