using CommunityToolkit.Mvvm.ComponentModel;
using NoIntrovert.Core.Dats;
using NoIntrovert.Core.Systems;

namespace NoIntrovert.ViewModels;

/// <summary>What the File section is showing.</summary>
public enum FileFactsMode
{
    /// <summary>Nothing focused: "Select a row to see what it is".</summary>
    Empty,
    /// <summary>One file: system, tags, filename, about file and formats.</summary>
    File,
    /// <summary>Rows from several systems: "3 systems selected" and one line per system.</summary>
    Systems,
}

/// <summary>One line in the several-systems view: "Sony PlayStation · 2 files".</summary>
public sealed record SystemLine(string? SystemId, string Name, int Files)
{
    public string Text => $"{Name} · {(Files == 1 ? "1 file" : $"{Files} files")}";
}

/// <summary>
/// The side panel's File section (docs/requirements/ui-redesign.md, "1. File
/// section"): the focused file's system, tags, filename, a line about the file
/// and the system's formats. The panel calls <see cref="ShowFile(string, string?, Verification, VerifyDatabase?)"/>,
/// <see cref="ShowSystems"/> or <see cref="Clear"/>; everything else is for binding.
/// </summary>
public partial class FileFactsViewModel : ViewModelBase
{
    public const string EmptyText = "Select a row to see what it is";
    public const string RedumpUri = "https://redump.org/";
    public const string NoIntroUri = "https://no-intro.org/";

    private const string MatchMeans = "A match means your file is byte-for-byte identical to a known good copy of the game, so it isn't corrupted or modified.";

    private int _version;

    [ObservableProperty] public partial FileFactsMode Mode { get; private set; } = FileFactsMode.Empty;

    /// <summary>The focused file's full path ("" when none).</summary>
    [ObservableProperty] public partial string Path { get; private set; } = "";

    /// <summary>The focused file's system id from TargetFormats.json, or null when it's unknown.</summary>
    [ObservableProperty] public partial string? SystemId { get; private set; }

    /// <summary>The system's name, e.g. "Sony PlayStation".</summary>
    [ObservableProperty] public partial string Title { get; private set; } = "";

    /// <summary>"Manufacturer · year", or "" for an unknown system.</summary>
    [ObservableProperty] public partial string Subtitle { get; private set; } = "";

    /// <summary>The generation tag ("5th generation"), or null to hide it.</summary>
    [ObservableProperty] public partial string? Generation { get; private set; }

    /// <summary>The media tag ("CD-ROM"), or null to hide it.</summary>
    [ObservableProperty] public partial string? Media { get; private set; }

    /// <summary>The console picture's name: Assets/consoles/icons/&lt;size&gt;/&lt;IconName&gt;.png ("" for none).</summary>
    [ObservableProperty] public partial string IconName { get; private set; } = "";

    /// <summary>What the fallback tile says when there's no picture ("PC-FX").</summary>
    [ObservableProperty] public partial string TileText { get; private set; } = "";

    [ObservableProperty] public partial Verification Verification { get; private set; }

    /// <summary>Which database the badge's tooltip names.</summary>
    [ObservableProperty] public partial VerifyDatabase Database { get; private set; }

    /// <summary>The file's name, e.g. "Final Fantasy VII (USA) (Disc 3).cue".</summary>
    [ObservableProperty] public partial string FileName { get; private set; } = "";

    /// <summary>"BIN/CUE · 2 tracks · 657.9 MB · Disc 3 of 3"; filled in the background.</summary>
    [ObservableProperty] public partial string AboutFile { get; private set; } = "";

    /// <summary>"Formats for PlayStation".</summary>
    [ObservableProperty] public partial string FormatsHeading { get; private set; } = "";

    /// <summary>The system's formats paragraph ("" hides the Formats part).</summary>
    [ObservableProperty] public partial string FormatsText { get; private set; } = "";

    /// <summary>"3 systems selected".</summary>
    [ObservableProperty] public partial string SystemsHeading { get; private set; } = "";

    /// <summary>One line per selected system, most files first.</summary>
    [ObservableProperty] public partial IReadOnlyList<SystemLine> SystemLines { get; private set; } = [];

    public bool IsEmpty => Mode == FileFactsMode.Empty;
    public bool IsFile => Mode == FileFactsMode.File;
    public bool IsSystems => Mode == FileFactsMode.Systems;
    public bool HasSubtitle => Subtitle.Length > 0;
    public bool HasGeneration => Generation is { Length: > 0 };
    public bool HasMedia => Media is { Length: > 0 };
    public bool IsVerified => Verification == Verification.Verified;
    public bool HasFormats => FormatsText.Length > 0;

    /// <summary>The badge's tooltip text, without the link.</summary>
    public string VerifiedTip => Database == VerifyDatabase.NoIntro
        ? $"Checked against No-Intro, a community database of verified cartridge and digital dumps. {MatchMeans}"
        : $"Checked against Redump, a community database of verified disc dumps. {MatchMeans}";

    public string LearnMoreText => Database == VerifyDatabase.NoIntro ? "Learn more at no-intro.org" : "Learn more at redump.org";

    public Uri LearnMoreUri => new(Database == VerifyDatabase.NoIntro ? NoIntroUri : RedumpUri);

    /// <summary>
    /// Shows the focused file. <paramref name="database"/> defaults to the one
    /// that covers the system (Redump for discs, No-Intro for cartridges).
    /// </summary>
    public void ShowFile(string path, string? systemId, Verification verification = Verification.Unknown, VerifyDatabase? database = null)
    {
        var version = ++_version;
        var facts = SystemFacts.ForOrFallback(systemId);
        Path = path;
        SystemId = systemId;
        Title = facts.Name;
        Subtitle = facts.Manufacturer.Length > 0 ? facts.Subtitle : "";
        Generation = facts.Generation;
        Media = facts.Media;
        IconName = facts.Icon;
        TileText = SystemFacts.For(systemId) is null ? "?" : facts.Short;
        Database = database ?? FileDescription.DatabaseFor(systemId);
        Verification = verification;
        FileName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        AboutFile = FileDescription.FormatName(path);
        FormatsHeading = $"Formats for {facts.Short}";
        FormatsText = facts.Formats;
        SystemsHeading = "";
        SystemLines = [];
        Mode = FileFactsMode.File;

        // The size, tracks and disc count read the disk, so they arrive a moment later.
        var context = SynchronizationContext.Current;
        Task.Run(() => FileDescription.AboutFile(path)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            void Apply() { if (version == _version) AboutFile = t.Result; }
            if (context is null) Apply();
            else context.Post(_ => Apply(), null);
        }, TaskScheduler.Default);
    }

    /// <summary>Shows the focused file with a Verify &amp; match result (null when it hasn't been checked).</summary>
    public void ShowFile(string path, string? systemId, MatchResult? match) =>
        ShowFile(path, systemId, FileDescription.VerificationOf(match));

    /// <summary>
    /// Shows the several-systems view for the selected rows' systems, one id per
    /// row (null for an unknown system). With one system or none it clears instead,
    /// so call <see cref="ShowFile(string, string?, Verification, VerifyDatabase?)"/> for that case.
    /// </summary>
    public void ShowSystems(IEnumerable<string?> systemIdPerRow)
    {
        var lines = systemIdPerRow
            .GroupBy(id => id ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => new SystemLine(g.Key.Length > 0 ? g.Key : null, SystemFacts.ForOrFallback(g.Key.Length > 0 ? g.Key : null).Name, g.Count()))
            .OrderByDescending(l => l.Files).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (lines.Count < 2)
        {
            Clear();
            return;
        }
        ++_version;
        SystemsHeading = $"{lines.Count} systems selected";
        SystemLines = lines;
        Mode = FileFactsMode.Systems;
    }

    /// <summary>Shows "Select a row to see what it is".</summary>
    public void Clear()
    {
        ++_version;
        Path = "";
        SystemId = null;
        Verification = Verification.Unknown;
        SystemLines = [];
        Mode = FileFactsMode.Empty;
    }

    partial void OnModeChanged(FileFactsMode value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFile));
        OnPropertyChanged(nameof(IsSystems));
    }

    partial void OnSubtitleChanged(string value) => OnPropertyChanged(nameof(HasSubtitle));
    partial void OnGenerationChanged(string? value) => OnPropertyChanged(nameof(HasGeneration));
    partial void OnMediaChanged(string? value) => OnPropertyChanged(nameof(HasMedia));
    partial void OnVerificationChanged(Verification value) => OnPropertyChanged(nameof(IsVerified));
    partial void OnFormatsTextChanged(string value) => OnPropertyChanged(nameof(HasFormats));

    partial void OnDatabaseChanged(VerifyDatabase value)
    {
        OnPropertyChanged(nameof(VerifiedTip));
        OnPropertyChanged(nameof(LearnMoreText));
        OnPropertyChanged(nameof(LearnMoreUri));
    }
}
