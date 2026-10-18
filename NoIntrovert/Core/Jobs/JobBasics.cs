using LibChdman;

namespace NoIntrovert.Core.Jobs;

public enum JobStatus { Queued, Running, Succeeded, Warning, Failed, Skipped, Cancelled }

/// <summary>How a job tells the UI what it is doing. Calls arrive on worker threads.</summary>
public interface IJobReport
{
    void SetStage(string stage);
    void SetProgress(double fraction);
}

/// <summary>The end state of one job: a status, a one-line result, and optional figures.</summary>
public sealed record JobOutcome(JobStatus Status, string Summary, OperationStats? Stats = null, string? Details = null)
{
    public static JobOutcome Skipped(string why) => new(JobStatus.Skipped, why);
    public static JobOutcome Failed(string why) => new(JobStatus.Failed, why);
}

/// <summary>What a job gets to work with: its own libchdman context, the settings snapshot and the log.</summary>
public sealed class JobContext(Chdman chdman, AppSettings settings, ILog log, IJobReport report, CancellationToken token)
{
    public Chdman Chdman { get; } = chdman;
    public AppSettings Settings { get; } = settings;
    public ILog Log { get; } = log;
    public IJobReport Report { get; } = report;
    public CancellationToken Token { get; } = token;

    /// <summary>Routes libchdman's progress callbacks to the job's progress bar for one operation.</summary>
    public void TrackChdman(string stage)
    {
        Report.SetStage(stage);
        Report.SetProgress(0);
        var verbose = Settings.LogProgress;
        var lastLogged = -1;
        Chdman.Progress = new InlineProgress<ChdmanProgress>(p =>
        {
            Report.SetProgress(p.Fraction);
            if (!verbose) return;
            var tenth = (int)(p.Fraction * 10);
            if (tenth == lastLogged) return;
            lastLogged = tenth;
            Log.Debug($"progress {p.Phase} {p.Fraction:P0} ratio {p.Ratio:P1}");
        });
    }

    public ChdOutputSettings OutputSettings() => new()
    {
        Compression = string.IsNullOrWhiteSpace(Settings.Compression) ? null : Settings.Compression.Trim(),
        HunkSize = Settings.HunkSize,
        Force = Settings.Overwrite,
    };
}

/// <summary>One job's work, run on a pool worker.</summary>
public delegate JobOutcome JobWork(JobContext context);

public static class OutputPaths
{
    /// <summary>The folder results for <paramref name="source"/> go in: the chosen output folder, or the source's own.</summary>
    public static string FolderFor(AppSettings settings, string source)
    {
        var folder = string.IsNullOrWhiteSpace(settings.OutputFolder) ? Path.GetDirectoryName(source)! : settings.OutputFolder;
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string Describe(Exception ex) => ex switch
    {
        ChdmanException ce => $"{ce.Message} ({ce.Status})",
        _ => ex.Message,
    };
}
