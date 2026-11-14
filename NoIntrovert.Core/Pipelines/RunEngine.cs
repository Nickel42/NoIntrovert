using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Core.Pipelines;

public enum RunJobState { Queued, Running, Finished }

/// <summary>
/// One queue job of a headless run (the command line's): what it's called, the inputs it covers, how big it
/// is, and what it last reported. Progress calls arrive on worker threads; readers poll the properties.
/// </summary>
public sealed class RunJob(string name, IReadOnlyList<string> paths, long cost, JobWork work, IReadOnlyList<RunJob>? after = null) : IJobReport
{
    private double _fraction;
    private string _stage = "";
    private long _startedTicks;
    private long _finishedTicks;
    private int _state;

    public string Name { get; } = name;

    /// <summary>The inputs this job covers (several for a multi-disc game, a Wii U title, one archive of many files).</summary>
    public IReadOnlyList<string> Paths { get; } = paths;

    /// <summary>At least 1, so empty and unknown-size jobs still count in the overall progress.</summary>
    public long Cost { get; } = Math.Max(1, cost);

    public JobWork Work { get; } = work;

    /// <summary>Jobs that must succeed first; if one doesn't, this one is skipped.</summary>
    public IReadOnlyList<RunJob>? After { get; } = after;

    /// <summary>Anything the pipeline wants to carry with the job (the Organize steps it runs, say).</summary>
    public object? Tag { get; init; }

    public RunJobState State => (RunJobState)Volatile.Read(ref _state);
    public JobOutcome? Outcome { get; private set; }
    public string Stage => Volatile.Read(ref _stage);
    public double Fraction => State == RunJobState.Finished ? 1 : Volatile.Read(ref _fraction);

    /// <summary>How much of <see cref="Cost"/> is done.</summary>
    public double DoneBytes => Cost * Fraction;

    public TimeSpan Elapsed
    {
        get
        {
            var start = Interlocked.Read(ref _startedTicks);
            if (start == 0) return TimeSpan.Zero;
            var end = Interlocked.Read(ref _finishedTicks);
            return TimeSpan.FromTicks((end == 0 ? DateTime.UtcNow.Ticks : end) - start);
        }
    }

    void IJobReport.SetStage(string stage) => Volatile.Write(ref _stage, stage);
    void IJobReport.SetProgress(double fraction) => Volatile.Write(ref _fraction, Math.Clamp(fraction, 0, 1));

    internal void MarkStarted()
    {
        Interlocked.Exchange(ref _startedTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _state, (int)RunJobState.Running);
    }

    internal void MarkFinished(JobOutcome outcome)
    {
        Outcome = outcome;
        if (Interlocked.Read(ref _startedTicks) == 0) Interlocked.Exchange(ref _startedTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _finishedTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _state, (int)RunJobState.Finished);
    }
}

/// <summary>What a run tells whoever shows it, as each job starts and finishes (on worker threads).</summary>
public interface IRunObserver
{
    void Started(RunJob job);
    void Finished(RunJob job);
}

/// <summary>
/// Runs a list of <see cref="RunJob"/>s on a queue of its own, the same <see cref="JobQueue"/> the app's pages
/// use, <see cref="PerformanceSettings.ParallelJobs"/> at a time. Cancelling <paramref name="token"/> stops
/// queued jobs and asks running ones to stop (they delete what they were writing).
/// </summary>
public static class RunEngine
{
    public static async Task RunAsync(string title, IReadOnlyList<RunJob> jobs, AppSettings settings, ILog log,
        IRunObserver? observer = null, CancellationToken token = default)
    {
        if (jobs.Count == 0) return;
        var queue = new JobQueue(() => settings.Performance.ParallelJobs);
        var specs = new Dictionary<RunJob, BatchJob>(ReferenceEqualityComparer.Instance);
        BatchJob Spec(RunJob job)
        {
            if (specs.TryGetValue(job, out var spec)) return spec;
            spec = new BatchJob(job.Name, job, job.Work,
                _ =>
                {
                    job.MarkStarted();
                    observer?.Started(job);
                },
                outcome =>
                {
                    job.MarkFinished(outcome);
                    observer?.Finished(job);
                },
                job.Cost, job.After?.Select(Spec).ToList(), job.Paths);
            specs[job] = spec;
            return spec;
        }
        var batch = jobs.Select(Spec).ToList();
        var group = queue.Submit(title, batch, settings, log);
        await using (token.Register(group.Cancel))
            await group.Completion.ConfigureAwait(false);
    }
}
