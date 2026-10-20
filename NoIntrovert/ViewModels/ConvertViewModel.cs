using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Archives;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>A named set of compression choices the Convert page offers instead of every knob.</summary>
public sealed record ConvertPreset(string Name, string Summary, string ChdCompression, LibDolphinRvz.RvzCompression RvzCompression,
    int RvzLevel, ArchiveLevel ArchiveLevel)
{
    public static readonly ConvertPreset Balanced = new("Balanced", "Each format's usual default",
        "", LibDolphinRvz.RvzCompression.Zstd, 5, ArchiveLevel.Normal);
    public static readonly ConvertPreset Smallest = new("Smallest", "Slower, a little smaller",
        "", LibDolphinRvz.RvzCompression.Zstd, 19, ArchiveLevel.Ultra);
    public static readonly ConvertPreset Fastest = new("Fastest", "Quicker, a little bigger",
        "", LibDolphinRvz.RvzCompression.Zstd, 1, ArchiveLevel.Fast);

    public static IReadOnlyList<ConvertPreset> All { get; } = [Balanced, Smallest, Fastest];

    public bool Matches(AppSettings s) =>
        s.Chd.Compression == ChdCompression && s.Nintendo.Compression == RvzCompression &&
        s.Nintendo.CompressionLevel == RvzLevel && s.Archives.Level == ArchiveLevel;
}

/// <summary>
/// The one Convert page: every input the format registry recognises (disc
/// images, CHDs, GameCube/Wii and Wii U games, archives) plus any other files
/// and folders to pack. Each row picks its own target; the options panel
/// shows only what the chosen targets need.
/// </summary>
public partial class ConvertViewModel : BatchTabViewModel
{
    // Filled and read by background scans (ItemsForFolder, then CreateItem), and by Pack a folder.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileSet> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<JobItemViewModel, PlannedJob> _jobs = [];
    private readonly HashSet<JobItemViewModel> _watched = [];
    private readonly Coalesced _sections;
    private AppSettings? _addSettings;
    private ConvertPlan? _plan;

    public ConvertViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs, JobQueue queue)
        : base(settings, log, dialogs, queue)
    {
        _sections = new Coalesced(RefreshSections);
        Items.CollectionChanged += (_, _) =>
        {
            // Bulk adds arrive as one reset: watch any row not watched yet, and forget removed ones.
            foreach (var item in Items)
                if (_watched.Add(item)) item.PropertyChanged += OnItemChanged;
            if (_watched.Count > Items.Count)
                foreach (var gone in _watched.Except(Items).ToList())
                {
                    gone.PropertyChanged -= OnItemChanged;
                    _watched.Remove(gone);
                }
            _sections.Request();
        };
        foreach (var section in new INotifyPropertyChanged[] { settings.Chd, settings.Nintendo, settings.Archives, settings.Output })
            section.PropertyChanged += (_, _) => RefreshPresets();
    }

    public override string Title => "Convert";
    public override string StartLabel => "Convert";
    public override bool ShowsTargets => true;

    public override string EmptyHint =>
        "Drop disc images, GameCube/Wii and Wii U games, or archives here — or any files and folders to pack into 7z or zip.\n" +
        "Folders bring in what isn't in its system's target format yet; drop a CHD or RVZ on its own to extract it.\n" +
        "Each row's TARGET says what it becomes; change it per row (or for every selected row at once).";

    protected override string FilterName => "Games, archives and files";
    protected override IReadOnlyList<FormatInfo>? InputFormats => null; // anything: what the registry doesn't know can still be packed
    protected override IReadOnlyList<string> Patterns { get; } =
        KnownFormats.Patterns([.. KnownFormats.DiscInputs, KnownFormats.Chd, .. KnownFormats.WiiUInputs]);

    /// <summary>
    /// A folder becomes the inputs found in it that the rules say to convert
    /// (a loose ROM, a zip that should be a 7z), plus those with a natural
    /// conversion whose system is unknown (a disc image in an unnamed folder).
    /// What's optimal or left as is stays out, so a folder of finished 7z
    /// ROMs isn't repacked; a folder with nothing becomes one row to pack.
    /// </summary>
    protected override IEnumerable<string> ItemsForFolder(string folder)
    {
        var scan = FormatRegistry.Default.Scan(folder, Settings.Output.ScanSubfolders);
        var settings = Settings.Snapshot();
        // A .wua only matters next to another source to merge into it (an update or DLC folder).
        var otherWiiU = scan.Any(s => KnownFormats.WiiUBuildable.Contains(s.Format) && s.Format != KnownFormats.Wua);
        bool Natural(FileSet s) =>
            (!IsFinished(s) || (s.Format == KnownFormats.Wua && otherWiiU))
            && !KnownFormats.Cartridges.Contains(s.Format)
            && (!KnownFormats.Archives.Contains(s.Format) || s.Inside is null || s.Holds(KnownFormats.DiscInputs) || s.Holds(KnownFormats.WiiUBuildable));
        var found = scan.Where(s => TargetResolver.Resolve(s, settings).Status switch
            {
                TargetStatus.Convert => true,
                TargetStatus.UnknownSystem => Natural(s),
                _ => s.Format == KnownFormats.Wua && otherWiiU,
            })
            .ToList();
        if (found.Count == 0)
        {
            var whole = ScanSet.Normalize(folder);
            _scanned[whole] = new FileSet(whole, [whole], ConvertTargets.PackedFolder);
            return [whole];
        }
        foreach (var s in found) _scanned[s.Primary] = s;
        return found.Select(s => s.Primary);
    }

    /// <summary>
    /// Already in its final form (a CHD, an RVZ, a .wua): a folder scan leaves
    /// these out, since their only conversion is back. Dropped on their own
    /// they're listed, ready to extract.
    /// </summary>
    private static bool IsFinished(FileSet s) =>
        s.Format == KnownFormats.Chd || s.Format == KnownFormats.Wua || Path.GetExtension(s.Primary).Equals(".rvz", StringComparison.OrdinalIgnoreCase);

    protected override JobItemViewModel CreateItem(string path)
    {
        if (!_scanned.TryRemove(path, out var set))
            set = FormatRegistry.Default.Identify(path)
                  ?? new FileSet(path, [path], Directory.Exists(path) ? ConvertTargets.PackedFolder : ConvertTargets.OtherFile);
        var options = ConvertTargets.For(set, _addSettings ?? Settings.Snapshot());
        var plan = options.Plan;
        return new JobItemViewModel(path)
        {
            Targets = options.Choices, Target = options.Default, FormatStatus = plan.Text,
            FormatDetails = plan.SystemName,
        };
    }

    /// <summary>One settings copy for a whole scan, not one per row.</summary>
    protected override void PrepareForAdding() => _addSettings = Settings.Snapshot();

    /// <summary>Adds a folder as one row, to pack whole into 7z or zip, without looking inside it.</summary>
    [RelayCommand]
    private async Task PackFolder()
    {
        var folder = await Dialogs.PickFolderAsync("Choose a folder to pack into one archive");
        if (folder is null) return;
        var whole = ScanSet.Normalize(folder);
        if (Items.Any(i => i.Path.Equals(whole, StringComparison.OrdinalIgnoreCase))) return;
        _addSettings = null; // use the settings as they are now
        _scanned[whole] = new FileSet(whole, [whole], ConvertTargets.PackedFolder);
        Items.Add(CreateItem(whole));
    }

    // ---- Changing targets: one row's change applies to every selected row that offers it ----

    private bool _spreading;

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JobItemViewModel.Target) || sender is not JobItemViewModel changed) return;
        if (!_spreading && SelectedItems is { Count: > 1 } selected && selected.Contains(changed) && changed.Target is { } target)
        {
            _spreading = true;
            foreach (var other in selected.OfType<JobItemViewModel>().Where(o => o != changed))
                if (other.Targets.FirstOrDefault(t => t.Kind == target.Kind) is { } same) other.Target = same;
            _spreading = false;
        }
        _sections.Request();
    }

    // ---- Which option sections the current targets need ----

    private bool Any(params TargetKind[] kinds) => Items.Any(i => i.Target is { } t && kinds.Contains(t.Kind));

    public bool ShowsChd => Any(TargetKind.Chd);
    public bool ShowsRvz => Any(TargetKind.Rvz);
    public bool ShowsExtract => Any(TargetKind.DiscImage);
    public bool ShowsWua => Any(TargetKind.Wua);
    public bool ShowsArchives => Any(TargetKind.SevenZip, TargetKind.Zip, TargetKind.Files);
    public bool ShowsPacking => Items.Any(i => i.Target?.Kind is TargetKind.SevenZip or TargetKind.Zip && !ArchiveJobs.IsConvertible(i.Path));
    public bool ShowsUnpacking => Any(TargetKind.Files);
    public bool ShowsCompression => ShowsChd || ShowsRvz || ShowsArchives;

    /// <summary>A one-line count of what the list will make, shown above the options.</summary>
    public string TargetSummary
    {
        get
        {
            var parts = Items.Where(i => i.Target is not null).GroupBy(i => i.Target!.Kind)
                .Where(g => g.Key != TargetKind.Leave)
                .Select(g => $"{g.Count()} → {g.First().Target!.Label}");
            var text = string.Join(" · ", parts);
            return text.Length == 0 ? "Nothing to convert yet." : text;
        }
    }

    private void RefreshSections()
    {
        foreach (var name in new[] { nameof(ShowsChd), nameof(ShowsRvz), nameof(ShowsExtract), nameof(ShowsWua), nameof(ShowsArchives),
                     nameof(ShowsPacking), nameof(ShowsUnpacking), nameof(ShowsCompression), nameof(TargetSummary) })
            OnPropertyChanged(name);
    }

    // ---- Presets and the one "remove originals" switch ----

    public IReadOnlyList<ConvertPreset> Presets => ConvertPreset.All;

    /// <summary>The preset the current settings match, or null for "Custom"; picking one applies it.</summary>
    public ConvertPreset? CurrentPreset
    {
        get => ConvertPreset.All.FirstOrDefault(p => p.Matches(Settings.Snapshot()));
        set { if (value is not null && value != CurrentPreset) ApplyPreset(value); }
    }

    public string PresetNote => CurrentPreset is { } p ? p.Summary : "Custom: your own compression choices (see Advanced).";

    [RelayCommand]
    private void ApplyPreset(ConvertPreset preset)
    {
        Settings.Chd.SelectedCompression = ChdSettingsViewModel.CompressionChoices.First(c => c.Value == preset.ChdCompression);
        Settings.Nintendo.SelectedCompression = NintendoSettingsViewModel.Compressions.First(c => c.Value == preset.RvzCompression);
        Settings.Nintendo.CompressionLevel = preset.RvzLevel;
        Settings.Archives.SelectedLevel = ArchiveSettingsViewModel.Levels.First(l => l.Value == preset.ArchiveLevel);
        RefreshPresets();
    }

    private void RefreshPresets()
    {
        OnPropertyChanged(nameof(CurrentPreset));
        OnPropertyChanged(nameof(PresetNote));
        OnPropertyChanged(nameof(RemoveOriginals));
    }

    /// <summary>
    /// Removes the inputs once each new file verifies or tests OK, whatever
    /// the target. Settings keeps separate switches for discs, archives and
    /// Wii U; this sets all three, and shows "mixed" when they differ.
    /// </summary>
    public bool? RemoveOriginals
    {
        get
        {
            var o = Settings.Output;
            return o.RemoveOriginals == o.RemoveArchiveOriginals && o.RemoveOriginals == o.RemoveWiiUOriginals ? o.RemoveOriginals : null;
        }
        set
        {
            var on = value ?? false;
            Settings.Output.RemoveOriginals = on;
            Settings.Output.RemoveArchiveOriginals = on;
            Settings.Output.RemoveWiiUOriginals = on;
            OnPropertyChanged();
        }
    }

    // ---- Running: the planner turns rows and targets into queue jobs ----

    protected override bool BeforeStart(IReadOnlyList<JobItemViewModel> pending)
    {
        var rows = pending.Select(i => new PlanInput(i.Path, i.Target?.Kind ?? TargetKind.Leave, i.SizeBytes)).ToList();
        _plan = ConvertPlanner.Plan(rows, RunSettings(), Log);
        if (_plan.Error is { } error)
        {
            Log.Error($"Convert: {error}");
            return false;
        }
        foreach (var w in _plan.Warnings) Log.Warn($"Convert: {w}");
        if (_plan.Jobs.Count == 0)
        {
            Log.Warn("Convert: every row is set to “Leave as is”.");
            return false;
        }
        return true;
    }

    protected override IReadOnlyList<IReadOnlyList<JobItemViewModel>> GroupItems(IReadOnlyList<JobItemViewModel> pending)
    {
        _jobs.Clear();
        var plan = _plan!;
        foreach (var i in plan.Left) pending[i].Finished(JobOutcome.Skipped("Left as is"));
        var groups = new List<IReadOnlyList<JobItemViewModel>>();
        foreach (var job in plan.Jobs)
        {
            var group = job.Rows.Select(r => pending[r]).ToList();
            _jobs[group[0]] = job;
            groups.Add(group);
        }
        return groups;
    }

    protected override JobOutcome WorkGroup(JobContext context, IReadOnlyList<JobItemViewModel> group) => _jobs[group[0]].Work(context);

    protected override JobOutcome Work(JobContext context, JobItemViewModel item) => WorkGroup(context, [item]);
}
