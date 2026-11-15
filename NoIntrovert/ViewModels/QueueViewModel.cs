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
/// for everything in the queue (counted by item, see <see cref="OverallFraction"/>),
/// plus the job list in Activity's Queue tab. Refreshed from the UI thread when
/// the queue changes, and a few times a second only while jobs are queued or
/// running: an idle queue costs nothing.
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

    private readonly Coalesced _changed;

    public QueueViewModel(JobQueue queue)
    {
        Queue = queue;
        _timer = new DispatcherTimer(Tick, DispatcherPriority.Background, (_, _) => Refresh());
        _changed = new Coalesced(Refresh);
        // Raised on worker threads: a job added, started or finished. Refresh starts the timer when there's work.
        queue.Changed += _changed.Request;
        Refresh();
    }

    /// <summary>Whether the progress timer is running: only while jobs are queued or running.</summary>
    internal bool IsTicking => _timer.IsEnabled;

    public JobQueue Queue { get; }

    /// <summary>Set by the window once it has a platform handle.</summary>
    public IProgressIndicator? Indicator { get; set; }

    public BulkObservableCollection<QueueRowViewModel> Rows { get; } = [];

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial double OverallPercent { get; set; }

    /// <summary>The status bar's summary: "1 running · 4 queued · 3 done · 7 min left · 52 MB/s", or "Queue empty".</summary>
    [ObservableProperty] public partial string StatusText { get; set; } = "Queue empty";

    /// <summary><see cref="StatusText"/> without the time left and speed, for a narrow window (the tooltip has them).</summary>
    [ObservableProperty] public partial string ShortStatusText { get; set; } = "Queue empty";
    [ObservableProperty] public partial bool HasRows { get; set; }

    /// <summary>Jobs running or waiting: the count on the status bar's Activity button.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasActive))] public partial int ActiveCount { get; set; }

    public bool HasActive => ActiveCount > 0;

    /// <summary>Finished jobs that failed and are still in the queue (Clear finished drops them).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFailures), nameof(FailedText))] public partial int FailedCount { get; set; }

    /// <summary>Whether the status bar shows its failure badge.</summary>
    public bool HasFailures => FailedCount > 0;

    /// <summary>The status bar's failure badge: "2 failed".</summary>
    public string FailedText => $"{FailedCount:N0} failed";

    /// <summary>Whether the status bar shows the progress bar and percentage: not when the queue is empty.</summary>
    [ObservableProperty] public partial bool ShowsProgress { get; set; }

    /// <summary>
    /// The queue's overall progress, 0..1: (finished items + the running items'
    /// fractions) ÷ all items in the queue. Skipped items don't count on either
    /// side; null when nothing counts (an empty queue, or only skipped items).
    /// </summary>
    internal static double? OverallFraction(IEnumerable<(QueuedState State, JobStatus? Outcome, double Fraction)> jobs)
    {
        double done = 0;
        var count = 0;
        foreach (var (state, outcome, fraction) in jobs)
        {
            if (outcome == JobStatus.Skipped) continue;
            count++;
            done += state switch
            {
                QueuedState.Finished => 1,
                QueuedState.Running => Math.Clamp(fraction, 0, 1),
                _ => 0,
            };
        }
        return count == 0 ? null : done / count;
    }

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
        var overall = OverallFraction(jobs.Select(j => (j.State, j.Outcome?.Status, j.Fraction)));
        OverallPercent = 100 * (overall ?? 0);
        ShowsProgress = overall is not null;
        IsBusy = running + queued > 0;
        ActiveCount = running + queued;
        FailedCount = failed;

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
            ShortStatusText = string.Join(" · ", parts);
            // Only once the rate has settled: a few seconds in, with something moving.
            if ((now - _busySince).TotalSeconds >= 5 && _bytesPerSecond > 0)
            {
                if (TimeLeft(total - done, _bytesPerSecond) is { } left) parts.Add(Eta(left));
                parts.Add($"{_bytesPerSecond / MiB:F0} MB/s");
            }
            StatusText = string.Join(" · ", parts);
        }
        else
        {
            // The failures are on the status bar's badge beside this.
            StatusText = finished.Count == 0 ? "Queue empty" : $"Idle · {finished.Count} done";
            ShortStatusText = StatusText;
        }
        Indicator?.Show(IsBusy ? overall ?? 0 : null, failed > 0);

        // Progress moves on its own only while something is queued or running; the queue's Changed starts it again.
        if (IsBusy && !_timer.IsEnabled) _timer.Start();
        else if (!IsBusy && _timer.IsEnabled) _timer.Stop();
    }

    /// <summary>
    /// The time left at this rate, or null when there's no telling: the smoothed rate decays towards
    /// (but never reaches) zero while a long step reports no progress, and dividing by it would
    /// overflow a TimeSpan. Under 1 KB/s, or past <see cref="MaxTimeLeft"/>, no estimate is shown.
    /// </summary>
    internal static TimeSpan? TimeLeft(double bytesLeft, double bytesPerSecond)
    {
        if (bytesPerSecond < 1024 || !double.IsFinite(bytesLeft)) return null;
        var seconds = Math.Max(0, bytesLeft) / bytesPerSecond;
        return seconds <= MaxTimeLeft.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
    }

    private static readonly TimeSpan MaxTimeLeft = TimeSpan.FromDays(7);

    internal static string Eta(TimeSpan left) => left.TotalSeconds switch
    {
        < 60 => "under a minute left",
        < 3600 => $"{Math.Ceiling(left.TotalMinutes):F0} min left",
        _ => $"{(int)left.TotalHours} h {left.Minutes} min left",
    };
}

/// <summary>One job in Activity's Queue tab.</summary>
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
