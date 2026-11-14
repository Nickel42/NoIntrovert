using LibChdKit;
using LibWiiGameCubeKit;

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
/// What a job gets to work with: its worker's lib-chd-kit context (and
/// lib-wii-gamecube-kit context, made the first time a job needs it), the settings
/// snapshot and the log.
/// </summary>
public sealed class JobContext(Chdman chdman, AppSettings settings, ILog log, IJobReport report, CancellationToken token,
    Lazy<WiiGameCubeKit>? rvz = null)
{
    public Chdman Chdman { get; } = chdman;

    /// <summary>The worker's lib-wii-gamecube-kit context, shared by its jobs (one runs at a time).</summary>
    internal Lazy<WiiGameCubeKit> RvzSource { get; } = rvz ?? new Lazy<WiiGameCubeKit>(() => new WiiGameCubeKit(settings.Performance.EffectiveThreadsPerJob));

    public WiiGameCubeKit Rvz
    {
        get
        {
            var r = RvzSource.Value;
            r.Log = (level, message) =>
            {
                switch (level)
                {
                    case WiiGameCubeKitLogLevel.Error: Log.Error("lib-wii-gamecube-kit: " + message); break;
                    case WiiGameCubeKitLogLevel.Warning: Log.Warn("lib-wii-gamecube-kit: " + message); break;
                    default: if (Settings.General.VerboseLog) Log.Debug("lib-wii-gamecube-kit: " + message.TrimEnd()); break;
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

    /// <summary>The same worker, report and cancellation with different settings (one row's own target, say).</summary>
    public JobContext WithSettings(AppSettings other) => new(Chdman, other, Log, Report, Token, RvzSource);

    private sealed class BandReport(IJobReport whole, double start, double span) : IJobReport
    {
        public void SetStage(string stage) => whole.SetStage(stage);
        public void SetProgress(double fraction) => whole.SetProgress(start + Math.Clamp(fraction, 0, 1) * span);
    }
    public CancellationToken Token { get; } = token;

    /// <summary>Routes lib-chd-kit's progress callbacks to the job's progress bar for one operation.</summary>
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

    /// <summary>Routes lib-wii-gamecube-kit's progress callbacks to the job's progress bar for one operation.</summary>
    public void TrackRvz(string stage)
    {
        Report.SetStage(stage);
        Report.SetProgress(0);
        var verbose = Settings.General.LogProgress;
        var lastLogged = -1;
        Rvz.Progress = new InlineProgress<WiiGameCubeKitProgress>(p =>
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
    /// <summary>
    /// The folder results for <paramref name="source"/> go in: the source's own, or in the Save to folder where its
    /// <see cref="OutputSettings.FolderLayout"/> puts it (<see cref="FolderLayouts"/>).
    /// </summary>
    public static string FolderFor(AppSettings settings, string source)
    {
        var folder = settings.Output.TargetFolder is { } target
            ? FolderLayouts.FolderIn(target, settings, source)
            : Path.GetDirectoryName(source)!;
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// The path to write <paramref name="path"/> to: itself when nothing is
    /// there or <paramref name="overwrite"/> is on, otherwise the first free
    /// "name (2).ext", "name (3).ext"… A folder's name is numbered whole
    /// (<paramref name="isFolder"/>), since dots in it aren't an extension.
    /// Inside a running job (<see cref="WithClaims{T}"/>) the name is claimed
    /// until the job ends, so a job running alongside never gets the same one,
    /// even with Overwrite on.
    /// </summary>
    public static string Available(string path, bool overwrite, bool isFolder = false)
    {
        lock (ClaimsGate)
        {
            if ((overwrite || !Taken(path)) && !ClaimedByAnother(path)) return Claim(path);
            var folder = Path.GetDirectoryName(path) ?? "";
            var name = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            var ext = isFolder ? "" : Path.GetExtension(path);
            for (var i = 2; ; i++)
            {
                var candidate = Path.Combine(folder, $"{name} ({i}){ext}");
                if (!Taken(candidate) && !ClaimedByAnother(candidate)) return Claim(candidate);
            }
        }
    }

    /// <summary>
    /// The name (no extension) to write several files under, such as a cue and
    /// its bins: <paramref name="name"/> when none of <paramref name="extensions"/>
    /// is taken under it in <paramref name="folder"/> (or <paramref name="overwrite"/>
    /// is on), otherwise "name (2)", "name (3)"… Claimed like <see cref="Available"/>.
    /// </summary>
    public static string AvailableName(string folder, string name, IReadOnlyList<string> extensions, bool overwrite)
    {
        bool Claimed(string n) => extensions.Any(e => ClaimedByAnother(Path.Combine(folder, n + e)));
        bool Free(string n) => !extensions.Any(e => Taken(Path.Combine(folder, n + e)))
                               && !(Directory.Exists(folder) && Directory.EnumerateFiles(folder, n + " (Track *").Any());
        string ClaimAll(string n)
        {
            foreach (var e in extensions) Claim(Path.Combine(folder, n + e));
            return n;
        }
        lock (ClaimsGate)
        {
            if ((overwrite || Free(name)) && !Claimed(name)) return ClaimAll(name);
            for (var i = 2; ; i++)
                if (Free($"{name} ({i})") && !Claimed($"{name} ({i})")) return ClaimAll($"{name} ({i})");
        }
    }

    private static bool Taken(string path) => File.Exists(path) || Directory.Exists(path);

    // ---- Names claimed by running jobs, so parallel jobs that map to the same name don't both pick it ----

    private static readonly object ClaimsGate = new();
    private static readonly Dictionary<string, object> Claims = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<object?> Owner = new();

    /// <summary>
    /// Runs one job's <paramref name="work"/> as the owner of the output names
    /// it picks; they're released when it returns or throws (by then a result
    /// exists, so its name is taken anyway, or it failed and the name is free).
    /// </summary>
    public static T WithClaims<T>(Func<T> work)
    {
        var previous = Owner.Value;
        var owner = new object();
        Owner.Value = owner;
        try { return work(); }
        finally
        {
            Owner.Value = previous;
            lock (ClaimsGate)
                foreach (var key in Claims.Where(kv => kv.Value == owner).Select(kv => kv.Key).ToList())
                    Claims.Remove(key);
        }
    }

    private static bool ClaimedByAnother(string path) =>
        Claims.TryGetValue(Path.GetFullPath(path), out var owner) && owner != Owner.Value;

    /// <summary>Claims <paramref name="path"/> for the running job; outside a job nothing is claimed.</summary>
    private static string Claim(string path)
    {
        if (Owner.Value is { } owner) Claims[Path.GetFullPath(path)] = owner;
        return path;
    }

    /// <summary>How a log line names a result that got a number because its name was taken.</summary>
    public static string Renamed(string wanted, string actual) =>
        $"  {Path.GetFileName(wanted)} already exists, so this one is {Path.GetFileName(actual)}";

    public static string Describe(Exception ex) => ex switch
    {
        ChdmanException ce => $"{ce.Message} ({ce.Status})",
        WiiGameCubeKitException de => $"{de.Message} ({de.Status})",
        DllNotFoundException when ex.Message.Contains("lib-wii-gamecube-kit") =>
            "lib-wii-gamecube-kit isn't next to the app, so GameCube/Wii images can't be handled. Build native/lib-wii-gamecube-kit (a git submodule) with its scripts/.",
        _ => ex.Message,
    };
}
