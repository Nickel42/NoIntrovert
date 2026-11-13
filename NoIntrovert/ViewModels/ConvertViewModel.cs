using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Core.Formats;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Presets;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// The one Convert page: every input the format registry recognises (disc
/// images, CHDs, GameCube/Wii and Wii U games, archives) plus any other files
/// and folders to pack. Each row has its own format preset (a target plus
/// all its settings, docs/requirements/ui-redesign.md), starting on its
/// system's default; the side panel describes the focused row and edits its
/// preset, with every setting under Custom.
/// </summary>
public partial class ConvertViewModel : BatchTabViewModel
{
    // Filled and read by background scans (ItemsForFolder, then CreateItem), and by Pack a folder.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileSet> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<JobItemViewModel, PlannedJob> _jobs = [];
    private readonly HashSet<JobItemViewModel> _watched = [];
    private readonly Coalesced _panel;
    private AppSettings? _addSettings;
    private ConvertPlan? _plan;

    public ConvertViewModel(SettingsViewModel settings, LogViewModel log, IDialogs dialogs, JobQueue queue)
        : base(settings, log, dialogs, queue)
    {
        Panel = new SidePanelViewModel(this, settings.DefaultPresets);
        _panel = new Coalesced(Panel.Refresh);
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
            _panel.Request();
        };
        // A preset saved, renamed or deleted, or a new default: every row's list follows.
        settings.DefaultPresets.Changed += RefreshPresetLists;
    }

    /// <summary>The side panel: File, Preset and Preset settings for the focused row.</summary>
    public SidePanelViewModel Panel { get; }

    /// <summary>
    /// The CHD converter (chdkit) didn't load at start: the page shows a banner
    /// that says so and what to do, as the log's error (<see cref="ChdMissingText"/>) does.
    /// </summary>
    [ObservableProperty] public partial bool ChdConverterMissing { get; set; }

    /// <summary>The error logged at start when the CHD converter didn't load.</summary>
    public const string ChdMissingText = "The CHD converter (chdkit) is missing or blocked, so CHD files can't be made or checked. " +
                                         "Reinstall NoIntrovert, or check whether your antivirus quarantined chdkit.";

    protected override void OnFocusChanged() => _panel.Request();

    protected override void OnSelectionChanged() => _panel.Request();

    public override string Title => "Convert";
    public override string StartLabel => "Convert";
    public override bool ShowsTargets => true;
    public override bool ShowsSaveTo => true;

    public override string EmptyHint =>
        "Drop disc images, GameCube/Wii and Wii U games, or archives here — or any files and folders to pack into 7z or zip.\n" +
        "Folders bring in what isn't in its system's target format yet; drop a CHD or RVZ on its own to extract it.\n" +
        "Each row's PRESET says what it becomes and how; change it per row, or in the panel for the focused row.";

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
        var settings = _addSettings ?? Settings.Snapshot();
        var options = ConvertTargets.For(set, settings);
        var plan = options.Plan;
        var item = new JobItemViewModel(path)
        {
            Targets = options.Choices, Target = options.Default, FormatStatus = plan.Text,
            FormatDetails = plan.SystemName, SystemId = plan.System, PlannedTarget = options.Default.Kind,
        };
        item.ReplacePresetOptions(PresetListFor(item, settings.Presets), list => StartFor(item, list, settings.Presets));
        return item;
    }

    /// <summary>A row's Preset dropdown: its system's presets, as far as this file can become them.</summary>
    private static IReadOnlyList<PresetListItem> PresetListFor(JobItemViewModel item, PresetSettings presets) =>
        FormatPresets.ListFor(item.SystemId, item.Targets.Select(t => t.Kind).ToList(), presets);

    private static PresetListItem StartFor(JobItemViewModel item, IReadOnlyList<PresetListItem> list, PresetSettings presets) =>
        FormatPresets.Start(list, item.SystemId, item.PlannedTarget, presets);

    /// <summary>Rebuilds every row's dropdown after the saved presets or the defaults change; rows keep their choice.</summary>
    private void RefreshPresetLists()
    {
        var presets = Settings.DefaultPresets.Current;
        foreach (var item in Items.Where(i => i.HasPresets))
            item.ReplacePresetOptions(PresetListFor(item, presets), list => StartFor(item, list, presets));
        _panel.Request();
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

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JobItemViewModel.PresetChoice) or nameof(JobItemViewModel.CustomValues)) _panel.Request();
    }

    // ---- Running: the planner turns rows and targets into queue jobs ----

    protected override bool BeforeStart(IReadOnlyList<JobItemViewModel> pending)
    {
        // Each row runs with its own preset's values (its Custom values when it's on Custom). Whether new files are
        // checked is Settings' choice, except in Custom and saved presets, which carry the user's own.
        var rows = pending.Select(i => new PlanInput(i.Path, i.Target?.Kind ?? TargetKind.Leave, i.SizeBytes, i.Values,
            OwnChecks: i.Preset is { IsCustom: true } or { IsSaved: true })).ToList();
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
