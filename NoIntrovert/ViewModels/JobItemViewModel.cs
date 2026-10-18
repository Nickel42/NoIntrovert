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
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Folder = System.IO.Path.GetDirectoryName(path) ?? "";
        TypeText = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        long size = 0;
        try { size = Core.Formats.DiscFiles.SetFiles(path).Where(File.Exists).Sum(f => new FileInfo(f).Length); }
        catch (IOException) { }
        SizeText = ChdJobs.Size(size);
    }

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
