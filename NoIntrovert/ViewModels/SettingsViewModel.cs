using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The options shown on the tabs, bound two-way and saved as they change.
/// Batches run on a snapshot (<see cref="Snapshot"/>), so edits during a
/// run apply to the next one.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _s;
    private readonly IDialogs _dialogs;

    public SettingsViewModel(AppSettings settings, IDialogs dialogs)
    {
        _s = settings;
        _dialogs = dialogs;
        SelectedCompression = CompressionChoices.FirstOrDefault(c => c.Value == _s.Compression) ?? CompressionChoices[0];
        if (SelectedCompression.Value != _s.Compression) CustomCompression = _s.Compression;
    }

    public AppSettings Snapshot() => _s.Clone();
    public void Save() => _s.Save();

    private void Set<T>(T value, Action<AppSettings, T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        apply(_s, value);
        _s.Save();
        OnPropertyChanged(name);
    }

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

    [ObservableProperty] public partial Choice<string> SelectedCompression { get; set; }
    [ObservableProperty] public partial string CustomCompression { get; set; } = "";
    public bool IsCustomCompression => SelectedCompression.Value == "custom";

    partial void OnSelectedCompressionChanged(Choice<string> value)
    {
        OnPropertyChanged(nameof(IsCustomCompression));
        _s.Compression = value.Value == "custom" ? CustomCompression : value.Value;
        _s.Save();
    }

    partial void OnCustomCompressionChanged(string value)
    {
        if (!IsCustomCompression) return;
        _s.Compression = value.Trim();
        _s.Save();
    }

    public Choice<IsoMode> SelectedIsoMode
    {
        get => IsoModes.First(m => m.Value == _s.IsoMode);
        set => Set(value.Value, (s, v) => s.IsoMode = v);
    }

    public Choice<ExtractFormat> SelectedExtractFormat
    {
        get => ExtractFormats.First(m => m.Value == _s.ExtractFormat);
        set => Set(value.Value, (s, v) => s.ExtractFormat = v);
    }

    public Choice<uint> SelectedHunkSize
    {
        get => HunkSizes.FirstOrDefault(h => h.Value == _s.HunkSize) ?? HunkSizes[0];
        set => Set(value.Value, (s, v) => s.HunkSize = v);
    }

    public string OutputFolder { get => _s.OutputFolder; set => Set(value, (s, v) => s.OutputFolder = v); }
    public bool UseSourceFolder { get => string.IsNullOrWhiteSpace(_s.OutputFolder); }
    public bool Overwrite { get => _s.Overwrite; set => Set(value, (s, v) => s.Overwrite = v); }
    public bool ScanSubfolders { get => _s.ScanSubfolders; set => Set(value, (s, v) => s.ScanSubfolders = v); }
    public bool VerifyAfterCreate { get => _s.VerifyAfterCreate; set => Set(value, (s, v) => s.VerifyAfterCreate = v); }
    public bool DeleteOriginals { get => _s.DeleteOriginals; set => Set(value, (s, v) => s.DeleteOriginals = v); }
    public bool UseRecycleBin { get => _s.UseRecycleBin; set => Set(value, (s, v) => s.UseRecycleBin = v); }
    public string RecycleBinLabel => $"…to the {Core.Jobs.FileRemover.BinName} (not permanently)";
    public bool CanUseRecycleBin => Core.Jobs.FileRemover.CanUseBin;
    public bool SplitBin { get => _s.SplitBin; set => Set(value, (s, v) => s.SplitBin = v); }
    public bool FixHeaderSha1 { get => _s.FixHeaderSha1; set => Set(value, (s, v) => s.FixHeaderSha1 = v); }
    public decimal ParallelJobs { get => _s.ParallelJobs; set => Set((int)value, (s, v) => s.ParallelJobs = Math.Max(1, v)); }
    public decimal ThreadsPerJob { get => _s.ThreadsPerJob; set => Set((int)value, (s, v) => s.ThreadsPerJob = Math.Max(0, v)); }
    public int MaxThreads => Environment.ProcessorCount;
    public string TempFolder { get => _s.TempFolder; set => Set(value, (s, v) => s.TempFolder = v); }
    public string TempFolderHint => _s.EffectiveTempFolder;
    public string ChdmanExePath { get => _s.ChdmanExePath; set => Set(value, (s, v) => s.ChdmanExePath = v); }
    public bool VerboseLog { get => _s.VerboseLog; set => Set(value, (s, v) => s.VerboseLog = v); }
    public bool LogProgress { get => _s.LogProgress; set => Set(value, (s, v) => s.LogProgress = v); }

    public double WindowWidth { get => _s.WindowWidth; set => _s.WindowWidth = value; }
    public double WindowHeight { get => _s.WindowHeight; set => _s.WindowHeight = value; }

    [RelayCommand]
    private async Task BrowseOutputFolder()
    {
        var folder = await _dialogs.PickFolderAsync("Choose where results go");
        if (folder is null) return;
        OutputFolder = folder;
        OnPropertyChanged(nameof(UseSourceFolder));
    }

    [RelayCommand]
    private void UseSourceFolders()
    {
        OutputFolder = "";
        OnPropertyChanged(nameof(UseSourceFolder));
    }

    [RelayCommand]
    private async Task BrowseTempFolder()
    {
        var folder = await _dialogs.PickFolderAsync("Choose a folder for temporary files");
        if (folder is null) return;
        TempFolder = folder;
        OnPropertyChanged(nameof(TempFolderHint));
    }

    [RelayCommand]
    private async Task BrowseChdmanExe()
    {
        var file = await _dialogs.PickFileAsync("Find chdman", "chdman", OperatingSystem.IsWindows() ? ["chdman*.exe", "*.exe"] : ["chdman*"]);
        if (file is not null) ChdmanExePath = file;
    }
}
