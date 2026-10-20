using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NoIntrovert.Core.Jobs;

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

    /// <summary>The system and any warning that goes with the conversion, for the status's tooltip.</summary>
    public string FormatDetails { get; init; } = "";

    /// <summary>What this row becomes; changed by the row's own drop-down.</summary>
    [ObservableProperty] public partial Core.Formats.TargetChoice? Target { get; set; }

    public bool IsFolder { get; }

    partial void OnStatusChanged(JobStatus value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusText));
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
        Result = outcome.Summary;
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

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
