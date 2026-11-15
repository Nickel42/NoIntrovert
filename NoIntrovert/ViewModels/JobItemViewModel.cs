using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NoIntrovert.Core.Jobs;
using NoIntrovert.Core.Presets;

namespace NoIntrovert.ViewModels;

/// <summary>One row in a batch list. Workers report through <see cref="IJobReport"/>; updates are throttled onto the UI thread.</summary>
public partial class JobItemViewModel : ViewModelBase, IJobReport
{
    private readonly Stopwatch _sinceUpdate = Stopwatch.StartNew();
    private readonly Stopwatch _elapsed = new();

    /// <summary>How long the row's work has taken so far (what TIME shows), for sorting by it.</summary>
    public TimeSpan Elapsed => _elapsed.Elapsed;
    private double _latestProgress;
    private string _latestStage = "";
    private int _postPending;

    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }
    public string SizeText { get; }
    public string TypeText { get; }

    [ObservableProperty] public partial JobStatus Status { get; set; } = JobStatus.Queued;
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string Stage { get; set; } = "";
    [ObservableProperty] public partial string Result { get; set; } = "";
    [ObservableProperty] public partial string ElapsedText { get; set; } = "";
    [ObservableProperty] public partial string SpeedText { get; set; } = "";
    [ObservableProperty] public partial string Details { get; set; } = "";

    public bool IsRunning => Status == JobStatus.Running;

    /// <summary>
    /// Whether a run has put this row on the shared queue. A row that was only
    /// added is <see cref="JobStatus.Queued"/> too, but reads "Ready" until then.
    /// </summary>
    [ObservableProperty] public partial bool IsSubmitted { get; set; }

    partial void OnIsSubmittedChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsQueued));
    }

    /// <summary>Waiting on the shared queue for its turn (not merely added to the list).</summary>
    public bool IsQueued => Status == JobStatus.Queued && IsSubmitted;

    /// <summary>The row's progress bar shows once it's running or finished, not while it's ready or waiting.</summary>
    public bool ShowsProgress => Status != JobStatus.Queued;

    /// <summary>A failed row's Result is its reason, shown in red.</summary>
    public bool IsFailed => Status == JobStatus.Failed;

    /// <summary>A row that failed for missing keys: it offers a link to Settings › Keys.</summary>
    public bool NeedsKeys => IsFailed && (KeysLink.PointsToKeys(Result) || KeysLink.PointsToKeys(Details));

    partial void OnResultChanged(string value) => OnPropertyChanged(nameof(NeedsKeys));
    partial void OnDetailsChanged(string value)
    {
        OnPropertyChanged(nameof(NeedsKeys));
        OnPropertyChanged(nameof(ResultTip));
    }

    public string StatusText => Status switch
    {
        JobStatus.Queued => IsSubmitted ? "Queued" : "Ready",
        JobStatus.Running => "Working",
        JobStatus.Succeeded => "Done",
        JobStatus.Warning => "Done*",
        JobStatus.Failed => "Failed",
        JobStatus.Skipped => "Skipped",
        _ => "Cancelled",
    };

    public JobItemViewModel(string path)
    {
        Path = System.IO.Path.TrimEndingDirectorySeparator(path);
        Name = System.IO.Path.GetFileName(Path);
        Folder = System.IO.Path.GetDirectoryName(Path) ?? "";
        IsFolder = Directory.Exists(Path);
        TypeText = IsFolder ? "FOLDER" : System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
        long size = 0;
        try
        {
            size = IsFolder
                ? Core.Archives.ArchiveJobs.SizeOf(Path)
                : Core.Formats.DiscFiles.SetFiles(Path).Where(File.Exists).Sum(f => new FileInfo(f).Length);
        }
        catch (IOException) { }
        SizeBytes = size;
        SizeText = ChdJobs.Size(size);
    }

    /// <summary>The input's size on disk, which weights this row in the overall progress.</summary>
    public long SizeBytes { get; }

    /// <summary>What this row can become (Convert page only; empty elsewhere).</summary>
    public IReadOnlyList<Core.Formats.TargetChoice> Targets { get; init; } = [];

    public bool HasTargets => Targets.Count > 1;

    /// <summary>Where the input stands against its system's target ("Optimal", "Convert → .7z"…; Convert page only).</summary>
    public string FormatStatus { get; init; } = "";

    /// <summary>The system's name, for the status's tooltip.</summary>
    public string FormatDetails { get; init; } = "";

    /// <summary>What this row becomes; follows the row's preset on the Convert page.</summary>
    [ObservableProperty] public partial Core.Formats.TargetChoice? Target { get; set; }

    public bool IsFolder { get; }

    // ---- Selection and focus ----

    /// <summary>The row the side panel describes: the last one clicked or moved to with the keyboard.</summary>
    [ObservableProperty] public partial bool IsFocusedRow { get; set; }

    // ---- The row's format preset (Convert page only) ----

    /// <summary>The input's system id (TargetFormats.json), or null when it's unknown.</summary>
    public string? SystemId { get; init; }

    /// <summary>The target the rules chose for the input, which decides the preset a row starts on.</summary>
    public Core.Formats.TargetKind PlannedTarget { get; init; } = Core.Formats.TargetKind.Leave;

    /// <summary>The row's Preset dropdown (empty off the Convert page).</summary>
    [ObservableProperty] public partial IReadOnlyList<PresetListItem> PresetOptions { get; set; } = [];

    public bool HasPresets => PresetOptions.Count > 0;

    partial void OnPresetOptionsChanged(IReadOnlyList<PresetListItem> value) => OnPropertyChanged(nameof(HasPresets));

    private PresetListItem? _presetChoice;

    /// <summary>
    /// The row's preset. A disabled entry, a divider or null (a dropdown
    /// clearing itself while its list is replaced) is ignored. Choosing Custom
    /// copies the current preset's values, so nothing jumps.
    /// </summary>
    public PresetListItem? PresetChoice
    {
        get => _presetChoice;
        set
        {
            if (value is null || !value.IsPreset || !value.IsEnabled || value == _presetChoice)
            {
                // Put the dropdown back on the row's real choice.
                if (value != _presetChoice) Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(PresetChoice)));
                return;
            }
            var old = _presetChoice?.Preset;
            if (value.Preset!.IsCustom && old is { IsCustom: false })
            {
                var from = old.IsLeave ? FirstConvertible()?.Preset : old;
                CustomBase = from;
                CustomValues = (from?.Values ?? Core.Presets.FormatPresets.StandardFor(Core.Formats.TargetKind.Chd).Values).Copy();
            }
            _presetChoice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Preset));
            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(PresetLabel));
            OnPropertyChanged(nameof(IsNotRecommended));
            SyncTarget();
        }
    }

    /// <summary>The row's preset, or null off the Convert page.</summary>
    public FormatPreset? Preset => _presetChoice?.Preset;

    public bool IsCustom => Preset?.IsCustom == true;

    /// <summary>The preset's name, as the filter and the panel read it.</summary>
    public string PresetLabel => Preset?.Label ?? "";

    /// <summary>The row's own values while it's on Custom (copied from the preset it was on).</summary>
    [ObservableProperty] public partial PresetValues? CustomValues { get; set; }

    partial void OnCustomValuesChanged(PresetValues? value) => SyncTarget();

    /// <summary>The preset Custom was copied from: "Reset to CHD · Standard" goes back to it.</summary>
    public FormatPreset? CustomBase { get; private set; }

    /// <summary>The values the row's job runs with; null for Leave as is (and off the Convert page).</summary>
    public PresetValues? Values => Preset switch
    {
        null or { IsLeave: true } => null,
        { IsCustom: true } => CustomValues,
        var p => p.Values,
    };

    /// <summary>The first preset this file can be converted with (Custom starts from it when the row was left as is).</summary>
    private PresetListItem? FirstConvertible() =>
        PresetOptions.FirstOrDefault(i => i.IsPreset && i.IsEnabled && i.Preset!.Kind is PresetKind.Standard or PresetKind.Saved);

    /// <summary>The list entry for <paramref name="id"/>, when this row can use it.</summary>
    public PresetListItem? OptionFor(string id) => PresetOptions.FirstOrDefault(i => i.IsPreset && i.IsEnabled && i.Preset!.Id == id);

    /// <summary>Whether this row can be converted with <paramref name="values"/>' target.</summary>
    public bool Offers(Core.Formats.TargetKind kind) => Targets.Any(t => t.Kind == kind);

    private void SyncTarget()
    {
        var kind = Values?.Target ?? Core.Formats.TargetKind.Leave;
        Target = Targets.FirstOrDefault(t => t.Kind == kind) ?? Core.Formats.TargetChoice.Leave;
        OnPropertyChanged(nameof(Values));
        OnPropertyChanged(nameof(PresetNote));
        OnPropertyChanged(nameof(PresetSuffix));
        OnPropertyChanged(nameof(PresetTip));
    }

    // ---- How the Preset cell reads in a dense list (docs/requirements/ui-redesign.md, "Row density") ----

    /// <summary>
    /// The entry this row would be on if nobody had chosen: Leave as is when the
    /// rules leave the file, else the system's recommended (★) preset. Null when
    /// there's nothing to recommend (an unknown system, or ★ isn't usable for this file).
    /// </summary>
    public FormatPreset? RecommendedPreset =>
        PlannedTarget == Core.Formats.TargetKind.Leave
            ? OptionFor(FormatPreset.LeaveId)?.Preset
            : FormatPresets.Recommended(SystemId) is { } star ? OptionFor(star.Id)?.Preset : null;

    /// <summary>The row's preset isn't the recommended one: the list marks it with an accent dot, to be spotted in a long list.</summary>
    public bool IsNotRecommended => Preset is not null && RecommendedPreset is { } r && r.Id != Preset.Id;

    /// <summary>
    /// The muted text after the preset's name in a dense row, only when it says
    /// something the name doesn't (<see cref="SuffixFor"/>); the full note is in the tooltip.
    /// </summary>
    public string PresetSuffix => SuffixFor(Preset, CustomValues, SystemId, PlannedTarget, FormatStatus);

    /// <summary>
    /// <see cref="PresetSuffix"/>'s rule. A file left as is because the rules
    /// leave it says why ("Optimal", "Left as is (your setting)"); a preset whose
    /// name doesn't say the extension it makes (Custom, a saved preset named
    /// "My PS1") says it ("→ .rvz"). "CHD · Standard" making a .chd, or Leave
    /// as is chosen by hand, says nothing more.
    /// </summary>
    public static string SuffixFor(FormatPreset? preset, PresetValues? customValues, string? systemId,
        Core.Formats.TargetKind planned, string formatStatus)
    {
        if (preset is null) return "";
        if (preset.IsLeave) return planned == Core.Formats.TargetKind.Leave ? formatStatus : "";
        var ext = (preset.IsCustom ? customValues?.OutputExtension(systemId) : preset.OutputExtension(systemId))?.TrimStart('.');
        if (string.IsNullOrEmpty(ext) || preset.Label.Contains(ext, StringComparison.OrdinalIgnoreCase)) return "";
        return $"→ .{ext}";
    }

    /// <summary>The Preset dropdown's tooltip: what the row becomes, the system, and whether the preset is the recommended one.</summary>
    public string PresetTip
    {
        get
        {
            var lines = new List<string>();
            var note = PresetNote;
            lines.Add(FormatDetails.Length > 0 && note.Length > 0 ? $"{note} · {FormatDetails}" : note.Length > 0 ? note : FormatDetails);
            if (IsNotRecommended) lines.Add($"Not the recommended preset ({RecommendedPreset!.Label}).");
            lines.Add("This file's preset: its format and settings.");
            return string.Join("\n", lines.Where(l => l.Length > 0));
        }
    }

    /// <summary>The Result cell's tooltip: the details, and the speed (a dense row has no line for it).</summary>
    public string? ResultTip => string.Join("\n", new[] { Details, SpeedText.Length > 0 ? $"Speed: {SpeedText}" : "" }.Where(s => s.Length > 0)) is { Length: > 0 } tip ? tip : null;

    partial void OnSpeedTextChanged(string value) => OnPropertyChanged(nameof(ResultTip));

    partial void OnStageChanged(string value) => OnPropertyChanged(nameof(StageTip));

    /// <summary>The progress cell's tooltip: the stage the job is at (a dense row has no line for it).</summary>
    public string? StageTip => Stage.Length > 0 ? Stage : null;

    /// <summary>
    /// The line under the row's Preset dropdown: "Convert → .chd" for the
    /// row's preset, or for Leave as is, "Optimal" when the file is already in
    /// its best format ("Left as is" when it isn't). Off the Convert page, the
    /// format status.
    /// </summary>
    public string PresetNote => NoteFor(Preset, CustomValues, SystemId, PlannedTarget, FormatStatus);

    /// <summary><see cref="PresetNote"/>'s rule.</summary>
    public static string NoteFor(FormatPreset? preset, PresetValues? customValues, string? systemId,
        Core.Formats.TargetKind planned, string formatStatus)
    {
        if (preset is null) return formatStatus;
        if (preset.IsLeave) return planned == Core.Formats.TargetKind.Leave && formatStatus.Length > 0 ? formatStatus : "Left as is";
        var extension = preset.IsCustom ? customValues?.OutputExtension(systemId) : preset.OutputExtension(systemId);
        return extension is { Length: > 0 } ext ? $"Convert → {(ext.StartsWith('.') ? ext : "." + ext)}" : formatStatus;
    }

    /// <summary>Rebuilds the dropdown (saved presets changed), keeping the row's choice when it's still there.</summary>
    public void ReplacePresetOptions(IReadOnlyList<PresetListItem> options, Func<IReadOnlyList<PresetListItem>, PresetListItem> start)
    {
        var id = Preset?.Id;
        PresetOptions = options;
        var keep = id is null ? null : OptionFor(id);
        _presetChoice = null;
        PresetChoice = keep ?? start(options);
    }

    partial void OnStatusChanged(JobStatus value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(NeedsKeys));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(ShowsProgress));
    }

    // ---- Optimize and Organize: what the run does to this file ----

    /// <summary>
    /// The CHANGES column leads with the outcome: where the file ends up, relative to its library folder
    /// ("roms/gc/Baldur's Gate - Dark Alliance (USA).rvz"), or why nothing happens ("Left alone: not verified yet",
    /// "Clash: … already exists", "To the trash (duplicate)"). Empty on the other pages.
    /// </summary>
    [ObservableProperty] public partial string ChangesTarget { get; set; } = "";

    /// <summary>What kind of outcome <see cref="ChangesTarget"/> is: it sets the cell's style (a path, muted, a clash).</summary>
    [ObservableProperty] public partial ChangesKind ChangesKind { get; set; }

    /// <summary>The steps that get it there, in run order ("Verify", "Convert", "Rename", "Move"): the cell's chips.</summary>
    [ObservableProperty] public partial IReadOnlyList<string> ChangesSteps { get; set; } = [];

    /// <summary>The planned steps one per line, with where each file ends up and why (the column's tooltip).</summary>
    [ObservableProperty] public partial string ChangesDetails { get; set; } = "";

    /// <summary>The steps as one line, "Verify · Convert · Move" (the cell's accessible help text).</summary>
    public string ChangesStepsText => string.Join(" · ", ChangesSteps);

    public bool HasChangesSteps => ChangesSteps.Count > 0;

    /// <summary>
    /// The outcome is a path: trimmed at its start, so the file name stays in view. (A guess, "roms/nes/….7z, name after
    /// Verify", has no name yet: it's trimmed at its end, so the folder stays in view.)
    /// </summary>
    public bool IsChangesPath => ChangesKind is ChangesKind.Planned;

    /// <summary>Nothing happens to the file (left alone, already in place, its box unticked): drawn muted.</summary>
    public bool IsChangesMuted => ChangesKind is ChangesKind.LeftAlone or ChangesKind.Unchanged or ChangesKind.Off;

    /// <summary>A clash, or a plan that can't be made: the warning colour and glyph.</summary>
    public bool IsChangesWarning => ChangesKind is ChangesKind.Clash or ChangesKind.Problem;

    /// <summary>Shows <paramref name="changes"/> in the CHANGES column, with <paramref name="details"/> in its tooltip.</summary>
    public void SetChanges(RowChanges changes, string details)
    {
        ChangesTarget = changes.Target;
        ChangesKind = changes.Kind;
        ChangesSteps = changes.Steps;
        ChangesDetails = details;
    }

    partial void OnChangesKindChanged(ChangesKind value)
    {
        OnPropertyChanged(nameof(IsChangesPath));
        OnPropertyChanged(nameof(IsChangesMuted));
        OnPropertyChanged(nameof(IsChangesWarning));
    }

    partial void OnChangesStepsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(ChangesStepsText));
        OnPropertyChanged(nameof(HasChangesSteps));
    }

    /// <summary>
    /// The row for this file's new path (<paramref name="moved"/>, made for it by its page) after Optimize or
    /// Organize moved, renamed or converted it, keeping what the run said about it.
    /// </summary>
    public JobItemViewModel MovedTo(JobItemViewModel moved)
    {
        moved.Status = Status;
        moved.IsSubmitted = IsSubmitted;
        moved.Progress = Progress;
        moved.Result = Result;
        moved.Details = Details;
        moved.ElapsedText = ElapsedText;
        moved.SpeedText = SpeedText;
        moved.ChangesTarget = ChangesTarget;
        moved.ChangesKind = ChangesKind;
        moved.ChangesSteps = ChangesSteps;
        moved.ChangesDetails = ChangesDetails;
        return moved;
    }

    /// <summary>Clears what the last run said and marks the row as handed to the queue ("Queued").</summary>
    public void Reset()
    {
        Status = JobStatus.Queued;
        IsSubmitted = true;
        Progress = 0;
        Stage = "";
        Result = "";
        ElapsedText = "";
        SpeedText = "";
        Details = "";
    }

    // Called on the UI thread by the batch runner callbacks.
    internal void Started()
    {
        _elapsed.Restart();
        Status = JobStatus.Running;
        Result = "";
    }

    internal void Finished(JobOutcome outcome)
    {
        _elapsed.Stop();
        Status = outcome.Status;
        Result = ResultText(outcome);
        Details = outcome.Details ?? "";
        Stage = "";
        Progress = outcome.Status is JobStatus.Succeeded or JobStatus.Warning ? 100 : Progress;
        ElapsedText = FormatElapsed(_elapsed.Elapsed);
        SpeedText = outcome.Stats is { MegabytesPerSecond: > 0 } s ? $"{s.MegabytesPerSecond:F1} MB/s" : "";
    }

    // IJobReport: called on worker threads.
    public void SetStage(string stage)
    {
        _latestStage = stage;
        Post(force: true);
    }

    public void SetProgress(double fraction)
    {
        _latestProgress = Math.Clamp(fraction, 0, 1);
        Post(force: false);
    }

    private void Post(bool force)
    {
        if (!force && _sinceUpdate.ElapsedMilliseconds < 80) return;
        _sinceUpdate.Restart();
        if (Interlocked.Exchange(ref _postPending, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _postPending, 0);
            if (Status != JobStatus.Running) return;
            Stage = _latestStage;
            Progress = _latestProgress * 100;
            ElapsedText = FormatElapsed(_elapsed.Elapsed);
        });
    }

    /// <summary>
    /// A row's Result: the outcome's summary, with how big the new file is
    /// next to the original ("Game.rvz · 41%") when the job made a file and
    /// its summary doesn't already say so.
    /// </summary>
    public static string ResultText(JobOutcome outcome) =>
        outcome is { Status: JobStatus.Succeeded or JobStatus.Warning, Stats: { InputBytes: > 0, OutputBytes: > 0 } s }
        && !outcome.Summary.Contains('%') && System.IO.Path.HasExtension(outcome.Summary)
            ? $"{outcome.Summary} · {s.Ratio:P0}"
            : outcome.Summary;

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
