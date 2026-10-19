using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.ViewModels;

/// <summary>Where the shell's overall progress is shown outside the window (the taskbar button).</summary>
public interface IProgressIndicator
{
    /// <param name="fraction">0..1 while busy; null when idle.</param>
    void Show(double? fraction, bool hasErrors);
}

/// <summary>
/// The shared queue as the shell shows it: one status line and progress bar
/// for everything that's running, weighted by size, plus the job list in the
/// queue drawer. Refreshed from the UI thread a few times a second.
/// </summary>
public partial class QueueViewModel : ViewModelBase
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(400);
    private const double SmoothingSeconds = 8;
    private const double MiB = 1024 * 1024;

    private readonly Dictionary<QueuedJob, QueueRowViewModel> _rows = [];
    private readonly DispatcherTimer _timer;
    private DateTime _lastSample = DateTime.UtcNow;
    private DateTime _busySince;
    private double _lastDone;
    private double _bytesPerSecond;
    private bool _wasBusy;

    public QueueViewModel(JobQueue queue)
    {
        Queue = queue;
        _timer = new DispatcherTimer(Tick, DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
    }

    public JobQueue Queue { get; }

    /// <summary>Set by the window once it has a platform handle.</summary>
    public IProgressIndicator? Indicator { get; set; }

    public BulkObservableCollection<QueueRowViewModel> Rows { get; } = [];

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial double OverallPercent { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Queue empty";
    [ObservableProperty] public partial string ToggleText { get; set; } = "Show queue";
    [ObservableProperty] public partial bool HasRows { get; set; }

    partial void OnIsOpenChanged(bool value) => UpdateToggleText();

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void CancelAll() => Queue.CancelAll();

    [RelayCommand]
    private void ClearFinished()
    {
        Queue.ClearFinished();
        Refresh();
    }

    public void Refresh()
    {
        var jobs = Queue.Jobs;
        var live = new HashSet<QueuedJob>(jobs);
        if (_rows.Count > live.Count)
        {
            var gone = _rows.Where(r => !live.Contains(r.Key)).Select(r => r.Value).ToHashSet();
            Rows.RemoveWhere(gone.Contains);
            foreach (var job in _rows.Keys.Where(j => !live.Contains(j)).ToList()) _rows.Remove(job);
        }
        var added = new List<QueueRowViewModel>();
        foreach (var job in jobs)
        {
            if (!_rows.TryGetValue(job, out var row))
            {
                row = new QueueRowViewModel(job, Queue);
                _rows[job] = row;
                added.Add(row);
            }
            row.Refresh();
        }
        Rows.AddRange(added);
        HasRows = Rows.Count > 0;

        var running = jobs.Count(j => j.State == QueuedState.Running);
        var queued = jobs.Count(j => j.State == QueuedState.Queued);
        var finished = jobs.Where(j => j.State == QueuedState.Finished).ToList();
        var failed = finished.Count(j => j.Outcome?.Status == JobStatus.Failed);
        var total = jobs.Sum(j => (double)j.Cost);
        var done = jobs.Sum(j => j.DoneBytes);
        OverallPercent = total > 0 ? 100 * done / total : 0;
        IsBusy = running + queued > 0;

        // Throughput: bytes done per second, smoothed over several seconds, from running jobs only.
        var now = DateTime.UtcNow;
        var dt = (now - _lastSample).TotalSeconds;
        if (IsBusy && !_wasBusy)
        {
            _busySince = now;
            _bytesPerSecond = 0;
            _lastDone = done;
        }
        else if (IsBusy && dt > 0)
        {
            var progressed = Math.Max(0, done - _lastDone);
            var rate = progressed / dt;
            var alpha = 1 - Math.Exp(-dt / SmoothingSeconds);
            _bytesPerSecond = _bytesPerSecond <= 0 ? rate : _bytesPerSecond + alpha * (rate - _bytesPerSecond);
        }
        _lastSample = now;
        _lastDone = done;
        _wasBusy = IsBusy;

        if (IsBusy)
        {
            var parts = new List<string> { $"{running} running", $"{queued} queued" };
            if (finished.Count > 0) parts.Add($"{finished.Count} done");
            // Only once the rate has settled: a few seconds in, with something moving.
            if ((now - _busySince).TotalSeconds >= 5 && _bytesPerSecond > 0)
            {
                parts.Add(Eta(TimeSpan.FromSeconds((total - done) / _bytesPerSecond)));
                parts.Add($"{_bytesPerSecond / MiB:F0} MB/s");
            }
            StatusText = string.Join(" · ", parts);
        }
        else
        {
            StatusText = finished.Count == 0 ? "Queue empty"
                : $"Idle · {finished.Count} done" + (failed > 0 ? $", {failed} failed" : "");
        }
        UpdateToggleText();
        Indicator?.Show(IsBusy ? done / Math.Max(1, total) : null, failed > 0);
    }

    private void UpdateToggleText() =>
        ToggleText = IsOpen ? "Hide queue" : Rows.Count > 0 ? $"Show queue ({Rows.Count})" : "Show queue";

    internal static string Eta(TimeSpan left) => left.TotalSeconds switch
    {
        < 60 => "under a minute left",
        < 3600 => $"{Math.Ceiling(left.TotalMinutes):F0} min left",
        _ => $"{(int)left.TotalHours} h {left.Minutes} min left",
    };
}

/// <summary>One job in the queue drawer.</summary>
public partial class QueueRowViewModel(QueuedJob job, JobQueue queue) : ViewModelBase
{
    public string Name => job.Name;
    public string GroupTitle => job.Group.Title;

    [ObservableProperty] public partial string Stage { get; set; } = "";
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string PercentText { get; set; } = "";
    [ObservableProperty] public partial JobStatus Status { get; set; } = JobStatus.Queued;
    [ObservableProperty] public partial bool CanCancel { get; set; } = true;

    private bool _settled;

    internal void Refresh()
    {
        if (_settled) return; // finished and shown: nothing about it changes again
        _settled = job.State == QueuedState.Finished;
        CanCancel = job.State != QueuedState.Finished;
        Status = job.State switch
        {
            QueuedState.Queued => JobStatus.Queued,
            QueuedState.Running => JobStatus.Running,
            _ => job.Outcome?.Status ?? JobStatus.Succeeded,
        };
        Stage = job.State switch
        {
            QueuedState.Queued => "Queued",
            QueuedState.Running => job.Stage.Length > 0 ? job.Stage : "Working",
            _ => job.Outcome?.Summary ?? "",
        };
        Percent = job.State == QueuedState.Finished && Status is JobStatus.Succeeded or JobStatus.Warning ? 100 : job.Fraction * 100;
        PercentText = $"{Percent:F0}%";
    }

    [RelayCommand]
    private void Cancel() => queue.Cancel(job);
}
