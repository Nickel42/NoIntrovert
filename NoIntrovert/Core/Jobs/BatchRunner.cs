using System.Collections.Concurrent;
using LibChdman;

namespace NoIntrovert.Core.Jobs;

/// <summary>A queued job: the work and where it reports to.</summary>
public sealed record BatchJob(string Name, IJobReport Report, JobWork Work, Action<JobStatus> OnStart, Action<JobOutcome> OnFinish);

/// <summary>
/// Runs jobs on a fixed number of workers. Each worker owns one libchdman
/// context for its whole life (a context runs one operation at a time).
/// </summary>
public static class BatchRunner
{
    public static async Task RunAsync(IReadOnlyList<BatchJob> jobs, AppSettings settings, ILog log, CancellationToken token)
    {
        var queue = new ConcurrentQueue<BatchJob>(jobs);
        var workers = Math.Clamp(settings.ParallelJobs, 1, Math.Max(1, Environment.ProcessorCount));
        var tasks = Enumerable.Range(0, Math.Min(workers, Math.Max(1, jobs.Count)))
            .Select(i => Task.Factory.StartNew(() => WorkerLoop(i, queue, settings, log, token),
                token, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Anything never started was cancelled with the batch.
        while (queue.TryDequeue(out var left))
            left.OnFinish(new JobOutcome(JobStatus.Cancelled, "Cancelled before it started"));
    }

    private static void WorkerLoop(int index, ConcurrentQueue<BatchJob> queue, AppSettings settings, ILog log, CancellationToken token)
    {
        using var chdman = new Chdman(settings.ThreadsPerJob);
        var multi = settings.ParallelJobs > 1;

        while (!token.IsCancellationRequested && queue.TryDequeue(out var job))
        {
            var jobLog = multi ? new PrefixedLog(log, $"[{index + 1}] ") : log;
            chdman.Log = (level, message) =>
            {
                switch (level)
                {
                    case ChdmanLogLevel.Error: jobLog.Error("libchdman: " + message); break;
                    case ChdmanLogLevel.Warning: jobLog.Warn("libchdman: " + message); break;
                    default: if (settings.VerboseLog) jobLog.Debug("libchdman: " + message.TrimEnd()); break;
                }
            };
            chdman.Progress = null;

            job.OnStart(JobStatus.Running);
            JobOutcome outcome;
            try
            {
                outcome = job.Work(new JobContext(chdman, settings, jobLog, job.Report, token));
            }
            catch (OperationCanceledException)
            {
                outcome = new JobOutcome(JobStatus.Cancelled, "Cancelled");
            }
            catch (ChdmanException ex) when (ex.Status == ChdmanStatus.Cancelled)
            {
                outcome = new JobOutcome(JobStatus.Cancelled, "Cancelled");
            }
            catch (Exception ex)
            {
                outcome = JobOutcome.Failed(OutputPaths.Describe(ex));
                jobLog.Error($"{job.Name}: {OutputPaths.Describe(ex)}");
                if (settings.VerboseLog) jobLog.Debug(ex.ToString());
            }
            job.OnFinish(outcome);
        }
    }
}
