using LibChdman;
using LibDolphinRvz;

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

/// <summary>
/// What a job gets to work with: its worker's libchdman context (and
/// libdolphinrvz context, made the first time a job needs it), the settings
/// snapshot and the log.
/// </summary>
public sealed class JobContext(Chdman chdman, AppSettings settings, ILog log, IJobReport report, CancellationToken token,
    Lazy<DolphinRvz>? rvz = null)
{
    public Chdman Chdman { get; } = chdman;

    /// <summary>The worker's libdolphinrvz context, shared by its jobs (one runs at a time).</summary>
    internal Lazy<DolphinRvz> RvzSource { get; } = rvz ?? new Lazy<DolphinRvz>(() => new DolphinRvz(settings.Performance.ThreadsPerJob));

    public DolphinRvz Rvz
    {
        get
        {
            var r = RvzSource.Value;
            r.Log = (level, message) =>
            {
                switch (level)
                {
                    case DrvzLogLevel.Error: Log.Error("libdolphinrvz: " + message); break;
                    case DrvzLogLevel.Warning: Log.Warn("libdolphinrvz: " + message); break;
                    default: if (Settings.General.VerboseLog) Log.Debug("libdolphinrvz: " + message.TrimEnd()); break;
                }
            };
            return r;
        }
    }
    public AppSettings Settings { get; } = settings;
    public ILog Log { get; } = log;
    /// <summary>Where the job reports; after <see cref="Band"/>, progress lands in that slice of the bar.</summary>
    public IJobReport Report { get; private set; } = report;

    /// <summary>
    /// Maps the progress of the next step(s) into <paramref name="start"/> ..
    /// start + <paramref name="span"/> of the job's bar, so a job with several
    /// steps (compress, then verify) fills the bar once instead of restarting it.
    /// </summary>
    public void Band(double start, double span) => Report = new BandReport(_whole, start, span);

    private readonly IJobReport _whole = report;

    private sealed class BandReport(IJobReport whole, double start, double span) : IJobReport
    {
        public void SetStage(string stage) => whole.SetStage(stage);
        public void SetProgress(double fraction) => whole.SetProgress(start + Math.Clamp(fraction, 0, 1) * span);
    }
    public CancellationToken Token { get; } = token;

    /// <summary>Routes libchdman's progress callbacks to the job's progress bar for one operation.</summary>
    public void TrackChdman(string stage)
    {
        Report.SetStage(stage);
        Report.SetProgress(0);
        var verbose = Settings.General.LogProgress;
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

    /// <summary>Routes libdolphinrvz's progress callbacks to the job's progress bar for one operation.</summary>
    public void TrackRvz(string stage)
    {
        Report.SetStage(stage);
        Report.SetProgress(0);
        var verbose = Settings.General.LogProgress;
        var lastLogged = -1;
        Rvz.Progress = new InlineProgress<DrvzProgress>(p =>
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
        Compression = string.IsNullOrWhiteSpace(Settings.Chd.Compression) ? null : Settings.Chd.Compression.Trim(),
        HunkSize = Settings.Chd.HunkSize,
        Force = Settings.Output.Overwrite,
    };
}

/// <summary>One job's work, run on a pool worker.</summary>
public delegate JobOutcome JobWork(JobContext context);

public static class OutputPaths
{
    /// <summary>The folder results for <paramref name="source"/> go in: the chosen output folder, or the source's own.</summary>
    public static string FolderFor(AppSettings settings, string source)
    {
        var folder = string.IsNullOrWhiteSpace(settings.Output.Folder) ? Path.GetDirectoryName(source)! : settings.Output.Folder;
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string Describe(Exception ex) => ex switch
    {
        ChdmanException ce => $"{ce.Message} ({ce.Status})",
        DolphinRvzException de => $"{de.Message} ({de.Status})",
        DllNotFoundException when ex.Message.Contains("dolphinrvz") =>
            "libdolphinrvz isn't next to the app, so GameCube/Wii images can't be handled. Build native/libdolphinrvz.",
        _ => ex.Message,
    };
}
