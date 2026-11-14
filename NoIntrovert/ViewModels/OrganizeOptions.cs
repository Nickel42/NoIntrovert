using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// How Optimize and Organize lay files out and where they write (cleanup-wizard FR-6 and FR-8), shared
/// by both pages and shown as their run bar's Save to: tidying the files where they are or building a new
/// library in another folder, and the layout (ES-DE, RomM, kept as it is, or no subfolders). A change
/// raises <see cref="Changed"/> so the plans are made again.
/// </summary>
public sealed partial class OrganizeOptions : ObservableObject
{
    private readonly SettingsViewModel _settings;
    private readonly IDialogs _dialogs;

    public OrganizeOptions(SettingsViewModel settings, IDialogs dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        // The layout can be changed here or in Settings; either way the plans are made again for it.
        _settings.Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LibrarySettingsViewModel.SelectedLayout)) return;
            OnPropertyChanged(nameof(SelectedLayout));
            OnPropertyChanged(nameof(Layout));
            OnPropertyChanged(nameof(LayoutDescription));
            OnPropertyChanged(nameof(LayoutExample));
            Changed?.Invoke();
        };
    }

    /// <summary>Raised when anything that changes a plan changed.</summary>
    public event Action? Changed;

    /// <summary>The run bar's Layout list, in short words.</summary>
    public static IReadOnlyList<Choice<LibraryLayout>> Layouts { get; } =
    [
        new(LibraryLayout.EsDe, "ES-DE"),
        new(LibraryLayout.RomM, "RomM"),
        new(LibraryLayout.InPlace, "Keep as is"),
        new(LibraryLayout.Flat, "No subfolders"),
    ];

    /// <summary>The layout (FR-6), as Settings › Library has it: changed here or there, it's the same choice.</summary>
    public Choice<LibraryLayout> SelectedLayout
    {
        get => Layouts.First(l => l.Value == _settings.Library.SelectedLayout.Value);
        set { if (value is not null) Layout = value.Value; }
    }

    public LibraryLayout Layout
    {
        get => _settings.Library.SelectedLayout.Value;
        set => _settings.Library.SelectedLayout = LibrarySettingsViewModel.Layouts.First(l => l.Value == value);
    }

    public const string LayoutTip =
        "How the games are laid out. ES-DE: roms/<system>/ with ES-DE's names. RomM: roms/<platform>/ with RomM's names. " +
        "Keep as is: each game stays in its folder, only converted and renamed. No subfolders: every game straight in the folder it was added from (or the new library).";

    /// <summary>What the chosen layout does, for the side panel.</summary>
    public string LayoutDescription => Layout switch
    {
        LibraryLayout.RomM => "A roms folder in each folder in the list (or in the new library), one folder per platform named as RomM names it, and the games' files inside. A multi-disc game is a folder of its own, with its .m3u and its discs inside; games that have to be folders (Wii U titles, PS3 games) stay folders.",
        LibraryLayout.InPlace => "Files and folders stay where they are. Games are only converted and renamed in place.",
        LibraryLayout.Flat => "Every game straight in the folder it was added from (or the new library), with no system folders. Only a multi-disc game's discs sit in hidden/ beside its .m3u, and games that have to be folders stay folders.",
        _ => "A roms folder in each folder in the list (or in the new library), one folder per system named as ES-DE names it, and the games' files inside. Only games that have to be folders stay folders (Wii U titles, PS3 games); a multi-disc game's .m3u sits in the system folder with its discs in hidden/.",
    };

    /// <summary>An example of the chosen layout, as a small tree; empty for Keep as is.</summary>
    public string LayoutExample => Layout switch
    {
        LibraryLayout.RomM => "roms/\n  nes/Super Mario Bros. (World).7z\n  psx/Crash Bandicoot (USA).chd\n  psx/Final Fantasy VII (USA)/\n    Final Fantasy VII (USA).m3u\n    hidden/Final Fantasy VII (USA) (Disc 1).chd …\n  wiiu/Some NUS Title/",
        LibraryLayout.Flat => "Super Mario Bros. (World).7z\nCrash Bandicoot (USA).chd\nFinal Fantasy VII (USA).m3u\nhidden/Final Fantasy VII (USA) (Disc 1).chd …\nSome NUS Title/",
        LibraryLayout.InPlace => "",
        _ => "roms/\n  nes/Super Mario Bros. (World).7z\n  psx/Crash Bandicoot (USA).chd\n  psx/Final Fantasy VII (USA).m3u\n  psx/hidden/Final Fantasy VII (USA) (Disc 1).chd …\n  wiiu/Some NUS Title/\n  ps3/Gran Turismo 5.ps3/",
    };

    /// <summary>Tidy the files where they are (the default): they're converted, renamed and moved in place.</summary>
    public bool InPlace
    {
        get => !BuildNewLibrary;
        set { if (value) BuildNewLibrary = false; }
    }

    /// <summary>FR-8: build a new library in <see cref="NewLibraryFolder"/> instead; the files stay exactly as they are.</summary>
    [ObservableProperty] public partial bool BuildNewLibrary { get; set; }

    partial void OnBuildNewLibraryChanged(bool value)
    {
        OnPropertyChanged(nameof(InPlace));
        OnPropertyChanged(nameof(SafetyText));
        // Like Save to › Folder: choosing a new library with no folder yet asks for one, and goes back without it.
        if (value && !HasNewLibraryFolder) _ = AskForFolderAsync();
        else Changed?.Invoke();
    }

    private async Task AskForFolderAsync()
    {
        if (!await PickFolderAsync()) Avalonia.Threading.Dispatcher.UIThread.Post(() => BuildNewLibrary = false);
        else Changed?.Invoke();
    }

    private async Task<bool> PickFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Where to build the new library");
        if (folder is null) return false;
        _settings.Library.NewLibraryFolder = folder;
        OnPropertyChanged(nameof(NewLibraryFolder));
        OnPropertyChanged(nameof(HasNewLibraryFolder));
        OnPropertyChanged(nameof(NewLibraryText));
        return true;
    }

    /// <summary>FR-8: copy files that aren't matched (unknown, not checked, damaged) as they are, rather than leave them out.</summary>
    [ObservableProperty] public partial bool CopyUnmatched { get; set; } = true;

    partial void OnCopyUnmatchedChanged(bool value) => Changed?.Invoke();

    /// <summary>The new library's folder (remembered in Settings).</summary>
    public string NewLibraryFolder => _settings.Library.NewLibraryFolder;

    public bool HasNewLibraryFolder => !string.IsNullOrWhiteSpace(NewLibraryFolder);

    /// <summary>The new library's folder in the run bar, or a hint when there isn't one yet.</summary>
    public string NewLibraryText => HasNewLibraryFolder ? NewLibraryFolder : "none chosen yet";

    public string SafetyText => BuildNewLibrary
        ? "Nothing where the files are now changes: originals stay where they are, and duplicates aren't copied. Nothing in the new folder is overwritten, and a run can be undone, which removes what it made there."
        : "Nothing is overwritten. Originals and duplicates go to a .noi-trash folder in their folder, so a run can be undone (conversions too) until you empty its trash.";

    /// <summary>Change… beside New library: picks the folder and chooses New library.</summary>
    [RelayCommand]
    private async Task ChooseNewLibraryFolder()
    {
        if (!await PickFolderAsync()) return;
        if (!BuildNewLibrary) BuildNewLibrary = true; // plans again
        else Changed?.Invoke();
    }

    /// <summary>The folder a plan writes into: the new library's, or null to tidy the files where they are.</summary>
    public string? Destination(IEnumerable<string> folders) => BuildNewLibrary && Problem(folders) is null ? NewLibraryFolder : null;

    /// <summary>Why the plan can't run as chosen, or null when it can (a new library needs a folder outside the loaded ones).</summary>
    public string? Problem(IEnumerable<string> folders) => BuildNewLibrary ? NewLibraryProblem(NewLibraryFolder, folders) : null;

    /// <summary>
    /// Why the new library can't be built in the chosen folder, or null when it can: one has to be chosen,
    /// and it can't be inside a loaded folder or hold one, or the files would change after all.
    /// </summary>
    public static string? NewLibraryProblem(string folder, IEnumerable<string> loadedFolders)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Choose a folder for the new library.";
        static string Full(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        var dest = Full(folder);
        foreach (var lib in loadedFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(Full))
        {
            if (dest.StartsWith(lib, PathNames.Comparison)) return $"The new library can't be inside a folder in the list ({lib.TrimEnd(Path.DirectorySeparatorChar)}).";
            if (lib.StartsWith(dest, PathNames.Comparison)) return $"The new library's folder can't hold a folder in the list ({lib.TrimEnd(Path.DirectorySeparatorChar)}).";
        }
        return null;
    }
}
