using LibChdKit;
using LibWiiGameCubeKit;

namespace NoIntrovert.Core.Jobs;

/// <summary>
/// One job to queue: its work, where its row reports to, and how big it is
/// (<paramref name="CostBytes"/> weights it in the overall progress).
/// <paramref name="After"/> lists jobs that must succeed first; if one
/// doesn't, this job is skipped. <paramref name="Sources"/> are the files it reads, so Automatic threads per job
/// can stay low when they, or the Save to folder, are on a spinning hard drive.
/// </summary>
public sealed record BatchJob(string Name, IJobReport Report, JobWork Work, Action<JobStatus> OnStart, Action<JobOutcome> OnFinish,
    long CostBytes = 0, IReadOnlyList<BatchJob>? After = null, IReadOnlyList<string>? Sources = null);

public enum QueuedState { Queued, Running, Finished }

/// <summary>A job inside the queue, with what it last reported.</summary>
public sealed class QueuedJob : IJobReport
{
    private readonly CancellationTokenSource _cts;
    private double _fraction;
    private string _stage = "";

    internal QueuedJob(BatchJob spec, JobGroup group)
    {
        Spec = spec;
        Group = group;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(group.Token);
    }

    public BatchJob Spec { get; }
    public JobGroup Group { get; }
    public string Name => Spec.Name;

    /// <summary>At least 1, so empty and unknown-size jobs still count.</summary>
    public long Cost => Math.Max(1, Spec.CostBytes);

    public QueuedState State { get; internal set; }
    public JobOutcome? Outcome { get; internal set; }
    public double Fraction => Volatile.Read(ref _fraction);
    public string Stage => Volatile.Read(ref _stage);
    internal CancellationToken Token => _cts.Token;

    /// <summary>How much of <see cref="Cost"/> is done: all of it once finished, whatever the result.</summary>
    public double DoneBytes => State == QueuedState.Finished ? Cost : Cost * Fraction;

    public void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { } // already finished
    }

    void IJobReport.SetStage(string stage)
    {
        Volatile.Write(ref _stage, stage);
        Spec.Report.SetStage(stage);
    }

    void IJobReport.SetProgress(double fraction)
    {
        Volatile.Write(ref _fraction, Math.Clamp(fraction, 0, 1));
        Spec.Report.SetProgress(fraction);
    }

    internal void Dispose() => _cts.Dispose();
}

/// <summary>Jobs submitted together (one tab's run), sharing a settings snapshot and a cancel button.</summary>
public sealed class JobGroup
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<QueuedJob> _jobs = [];

    internal JobGroup(string title, AppSettings settings, ILog log)
    {
        Title = title;
        Settings = settings;
        Log = log;
    }

    public string Title { get; }
    public AppSettings Settings { get; }
    public ILog Log { get; }
    public IReadOnlyList<QueuedJob> Jobs => _jobs;
    internal CancellationToken Token => _cts.Token;

    /// <summary>Completes when every job in the group has finished (or was cancelled or skipped).</summary>
    public Task Completion => _done.Task;

    public bool IsFinished => _done.Task.IsCompleted;

    internal void Add(QueuedJob job) => _jobs.Add(job);
    internal void Complete() => _done.TrySetResult();

    public void Cancel() => _cts.Cancel();
}

/// <summary>
/// The app's one job queue. Every tab submits into it; a single concurrency
/// budget decides how many jobs run at once. Each worker keeps its own
/// lib-chd-kit (and, when a job needs it, lib-wii-gamecube-kit) context while it has
/// work, since a context runs one operation at a time.
/// </summary>
public sealed class JobQueue
{
    private readonly object _gate = new();
    private readonly List<QueuedJob> _queued = [];
    private readonly List<QueuedJob> _all = [];
    private readonly Func<int> _concurrency;
    private int _workers;
    private int _nextWorker;

    /// <param name="concurrency">How many jobs may run at once; read again whenever a worker looks for work.</param>
    public JobQueue(Func<int> concurrency)
    {
        _concurrency = concurrency;
    }

    /// <summary>Raised (on a worker thread, or the caller's) whenever a job is added, starts or finishes.</summary>
    public event Action? Changed;

    public int Concurrency => Math.Clamp(_concurrency(), 1, Math.Max(1, Environment.ProcessorCount));

    public bool IsBusy
    {
        get { lock (_gate) return _all.Any(j => j.State != QueuedState.Finished); }
    }

    /// <summary>Every job since the list was last cleared, in the order they were submitted.</summary>
    public IReadOnlyList<QueuedJob> Jobs
    {
        get { lock (_gate) return _all.ToList(); }
    }

    /// <summary>Queues <paramref name="jobs"/> as one group and starts workers as needed.</summary>
    public JobGroup Submit(string title, IReadOnlyList<BatchJob> jobs, AppSettings settings, ILog log)
    {
        var group = new JobGroup(title, settings, log);
        lock (_gate)
        {
            foreach (var spec in jobs)
            {
                var job = new QueuedJob(spec, group);
                group.Add(job);
                _queued.Add(job);
                _all.Add(job);
            }
            if (jobs.Count == 0) group.Complete();
            StartWorkers();
        }
        group.Token.Register(() => DropQueued(j => j.Group == group));
        Changed?.Invoke();
        return group;
    }

    /// <summary>Cancels one job: a queued one never starts, a running one is asked to stop.</summary>
    public void Cancel(QueuedJob job)
    {
        job.Cancel();
        DropQueued(j => j == job);
    }

    public void CancelAll()
    {
        List<JobGroup> groups;
        lock (_gate) groups = _all.Select(j => j.Group).Distinct().ToList();
        foreach (var g in groups) g.Cancel();
    }

    /// <summary>
    /// Forgets every finished job, also in a batch still running, so the overall progress starts again from the
    /// jobs still to do. A finished job a queued one waits on (a disc before its .m3u) stays until that one is
    /// done too: the queue reads its outcome to decide whether the next step runs.
    /// </summary>
    public void ClearFinished()
    {
        lock (_gate)
        {
            var needed = _all.Where(j => j.State != QueuedState.Finished && j.Spec.After is { Count: > 0 })
                .SelectMany(j => j.Spec.After!).ToHashSet(ReferenceEqualityComparer.Instance);
            _all.RemoveAll(j => j.State == QueuedState.Finished && !needed.Contains(j.Spec));
        }
        Changed?.Invoke();
    }

    /// <summary>How many finished groups the queue keeps before it forgets the oldest ones (<see cref="TrimFinished"/>).</summary>
    public const int KeepFinishedGroups = 20;

    /// <summary>
    /// Forgets the oldest finished groups beyond the newest <paramref name="keep"/>,
    /// so a long session's queue doesn't grow forever. Groups with a failed job
    /// stay until Clear finished, and nothing still queued or running is touched.
    /// Returns how many groups were forgotten. Runs on its own each time a group finishes.
    /// </summary>
    public int TrimFinished(int keep = KeepFinishedGroups)
    {
        lock (_gate)
        {
            var finished = _all.Select(j => j.Group).Distinct().Where(g => g.IsFinished).ToList();
            var old = finished.Take(Math.Max(0, finished.Count - keep))
                .Where(g => !g.Jobs.Any(j => j.Outcome?.Status == JobStatus.Failed)).ToHashSet();
            if (old.Count > 0) _all.RemoveAll(j => old.Contains(j.Group));
            return old.Count;
        }
    }

    private void DropQueued(Func<QueuedJob, bool> which)
    {
        List<QueuedJob> dropped;
        lock (_gate)
        {
            dropped = _queued.Where(which).ToList();
            foreach (var j in dropped) _queued.Remove(j);
        }
        foreach (var j in dropped) Finish(j, new JobOutcome(JobStatus.Cancelled, "Cancelled before it started"));
    }

    // Called with _gate held.
    private void StartWorkers()
    {
        // Workers busy with a job count toward _workers too, so a job queued behind a running one
        // gets a worker of its own when Concurrency allows.
        var running = _all.Count(j => j.State == QueuedState.Running);
        var wanted = Math.Min(Concurrency, running + _queued.Count);
        while (_workers < wanted)
        {
            _workers++;
            var index = _nextWorker++;
            new Thread(() => WorkerLoop(index)) { IsBackground = true, Name = $"NoIntrovert job worker {index + 1}" }.Start();
        }
    }

    /// <summary>
    /// The next job whose dependencies have all finished, or null. Jobs whose
    /// dependencies failed come back in <paramref name="blocked"/> to be skipped.
    /// Called with _gate held.
    /// </summary>
    private QueuedJob? TakeNext(List<QueuedJob> blocked)
    {
        foreach (var job in _queued.ToList())
        {
            var after = job.Spec.After;
            if (after is null || after.Count == 0) { _queued.Remove(job); return job; }
            var deps = _all.Where(j => after.Any(a => ReferenceEquals(a, j.Spec))).ToList();
            if (deps.Any(d => d.State != QueuedState.Finished)) continue;
            _queued.Remove(job);
            if (deps.All(d => d.Outcome?.Status is JobStatus.Succeeded or JobStatus.Warning)) return job;
            blocked.Add(job);
        }
        return null;
    }

    private void WorkerLoop(int index)
    {
        Chdman? chdman = null;
        var chdmanThreads = -1;
        Lazy<WiiGameCubeKit>? rvz = null;
        try
        {
            while (true)
            {
                QueuedJob? job;
                var blocked = new List<QueuedJob>();
                lock (_gate)
                {
                    job = _workers > Concurrency ? null : TakeNext(blocked);
                    if (job is null && blocked.Count == 0)
                    {
                        // Nothing runnable: wait if other jobs this one depends on are still running, else stop.
                        var waiting = _queued.Count > 0 && _workers <= Concurrency && _all.Any(j => j.State == QueuedState.Running);
                        if (!waiting)
                        {
                            _workers--;
                            return;
                        }
                        Monitor.Wait(_gate, 200);
                        continue;
                    }
                    if (job is not null) job.State = QueuedState.Running;
                }
                foreach (var b in blocked)
                    Finish(b, JobOutcome.Skipped("Skipped because an earlier step didn't succeed"));
                if (job is null) continue;

                var settings = SettingsFor(job.Group.Settings, job.Spec.Sources);
                var threads = settings.Performance.EffectiveThreadsPerJob;
                if (chdman is null || chdmanThreads != threads)
                {
                    chdman?.Dispose();
                    chdman = new Chdman(threads);
                    chdmanThreads = threads;
                    if (rvz?.IsValueCreated == true) rvz.Value.Dispose();
                    rvz = new Lazy<WiiGameCubeKit>(() => new WiiGameCubeKit(threads));
                }
                Run(index, job, settings, chdman, rvz!);
            }
        }
        finally
        {
            chdman?.Dispose();
            if (rvz?.IsValueCreated == true) rvz.Value.Dispose();
            lock (_gate) Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// The settings a job runs with: its group's, or with Automatic threads per job a copy capped at
    /// <see cref="PerformanceSettings.SpinningDriveThreads"/> when a source or the Save to folder is on a spinning drive.
    /// </summary>
    internal static AppSettings SettingsFor(AppSettings group, IReadOnlyList<string>? sources)
    {
        var p = group.Performance;
        if (p.ThreadsPerJob > 0 || sources is null or { Count: 0 }) return group;
        var spinning = sources.Any(Drives.IsSpinning) || Drives.IsSpinning(group.Output.TargetFolder);
        var threads = PerformanceSettings.EffectiveThreads(0, p.ParallelJobs, Environment.ProcessorCount, spinning);
        if (threads == p.EffectiveThreadsPerJob) return group;
        var capped = group.Clone();
        capped.Performance.ThreadsPerJob = threads;
        return capped;
    }

    private void Run(int index, QueuedJob job, AppSettings settings, Chdman chdman, Lazy<WiiGameCubeKit> rvz)
    {
        var log = Concurrency > 1 ? new PrefixedLog(job.Group.Log, $"[{index + 1}] ") : job.Group.Log;
        chdman.Log = (level, message) =>
        {
            switch (level)
            {
                case ChdmanLogLevel.Error: log.Error("lib-chd-kit: " + message); break;
                case ChdmanLogLevel.Warning: log.Warn("lib-chd-kit: " + message); break;
                default: if (settings.General.VerboseLog) log.Debug("lib-chd-kit: " + message.TrimEnd()); break;
            }
        };
        chdman.Progress = null;
        if (rvz.IsValueCreated) rvz.Value.Progress = null;

        job.Spec.OnStart(JobStatus.Running);
        Changed?.Invoke();
        JobOutcome outcome;
        try
        {
            job.Token.ThrowIfCancellationRequested();
            // The output names the job picks are its own until it ends, so a parallel job never picks the same one.
            outcome = OutputPaths.WithClaims(() => job.Spec.Work(new JobContext(chdman, settings, log, job, job.Token, rvz)));
        }
        catch (OperationCanceledException)
        {
            outcome = new JobOutcome(JobStatus.Cancelled, "Cancelled");
        }
        catch (ChdmanException ex) when (ex.Status == ChdmanStatus.Cancelled)
        {
            outcome = new JobOutcome(JobStatus.Cancelled, "Cancelled");
        }
        catch (WiiGameCubeKitException ex) when (ex.Status == WiiGameCubeKitStatus.Cancelled)
        {
            outcome = new JobOutcome(JobStatus.Cancelled, "Cancelled");
        }
        catch (Exception ex)
        {
            outcome = JobOutcome.Failed(OutputPaths.Describe(ex));
            log.Error($"{job.Name}: {OutputPaths.Describe(ex)}");
            if (settings.General.VerboseLog) log.Debug(ex.ToString());
        }
        Finish(job, outcome);
    }

    private void Finish(QueuedJob job, JobOutcome outcome)
    {
        lock (_gate)
        {
            job.State = QueuedState.Finished;
            job.Outcome = outcome;
            Monitor.PulseAll(_gate);
        }
        job.Spec.OnFinish(outcome);
        job.Dispose();
        if (job.Group.Jobs.All(j => j.State == QueuedState.Finished))
        {
            Dats.ThrottledJsonFile.FlushAll(); // hashes, matches and originals are on disk before the group reports done
            job.Group.Complete();
            TrimFinished();
        }
        Changed?.Invoke();
        lock (_gate) StartWorkers(); // a dependency finishing can make queued jobs runnable
    }
}
