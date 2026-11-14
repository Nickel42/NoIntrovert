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

    /// <summary>A failed row's Result is its reason, shown in red.</summary>
    public bool IsFailed => Status == JobStatus.Failed;

    /// <summary>A row that failed for missing keys: it offers a link to Settings › Keys.</summary>
    public bool NeedsKeys => IsFailed && (KeysLink.PointsToKeys(Result) || KeysLink.PointsToKeys(Details));

    partial void OnResultChanged(string value) => OnPropertyChanged(nameof(NeedsKeys));
    partial void OnDetailsChanged(string value) => OnPropertyChanged(nameof(NeedsKeys));

    public string StatusText => Status switch
    {
        JobStatus.Queued => "Queued",
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
    }

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
    }

    // ---- Optimize and Organize: what the run does to this file ----

    /// <summary>The steps planned for this file, in the order they run ("Verify → Convert → .chd → Move into roms/psx/"); empty elsewhere.</summary>
    [ObservableProperty] public partial string Changes { get; set; } = "";

    /// <summary>The planned steps one per line, with where each file ends up and why (the column's tooltip).</summary>
    [ObservableProperty] public partial string ChangesDetails { get; set; } = "";

    /// <summary>
    /// The row for this file's new path (<paramref name="moved"/>, made for it by its page) after Optimize or
    /// Organize moved, renamed or converted it, keeping what the run said about it.
    /// </summary>
    public JobItemViewModel MovedTo(JobItemViewModel moved)
    {
        moved.Status = Status;
        moved.Progress = Progress;
        moved.Result = Result;
        moved.Details = Details;
        moved.ElapsedText = ElapsedText;
        moved.SpeedText = SpeedText;
        moved.Changes = Changes;
        moved.ChangesDetails = ChangesDetails;
        return moved;
    }

    public void Reset()
    {
        Status = JobStatus.Queued;
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
