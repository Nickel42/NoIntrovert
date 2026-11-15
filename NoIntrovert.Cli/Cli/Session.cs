using System.CommandLine;
using NoIntrovert.Cli.Output;

namespace NoIntrovert.Cli.Cli;

/// <summary>noi's exit codes (docs/requirements/cli.md §7).</summary>
public static class Exit
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int Damaged = 3;
    public const int Missing = 4;
    public const int Blocked = 5;
    public const int Cancelled = 130;

    /// <summary>When several apply, the most important wins: 2, 130, 5, 1, 4, 3, 0.</summary>
    public static int Combine(params int[] codes)
    {
        int[] order = [Usage, Cancelled, Blocked, Failed, Missing, Damaged];
        foreach (var code in order)
            if (codes.Contains(code)) return code;
        return Ok;
    }
}

/// <summary>A problem with how noi was called: exit code 2, the message, and what to type instead.</summary>
public sealed class UsageException(string message, params string[] hints) : Exception(message)
{
    public IReadOnlyList<string> Hints { get; } = hints;
}

/// <summary>
/// One run of noi: what it prints (<see cref="Ui"/>), its log, the settings it runs with, and Ctrl-C. Built from the
/// global options, the same for every command.
/// </summary>
public sealed class Session : IDisposable
{
    private Session(Ui ui, CliLog log, AppSettings settings, CancellationToken token)
    {
        Ui = ui;
        Log = log;
        Settings = settings;
        Token = token;
    }

    public Ui Ui { get; }
    public CliLog Log { get; }
    public AppSettings Settings { get; }
    public CancellationToken Token { get; }
    public bool DryRun { get; private init; }
    public bool Yes { get; private init; }
    public bool NoInput { get; private init; }
    public bool Offline { get; private init; }
    public bool UsesAppSettings { get; private init; }

    /// <summary>Whether noi may ask a question: a person at a terminal, and nothing said not to.</summary>
    public bool CanAsk => !NoInput && Ui.IsHuman && Ui.Term.StdinIsTerminal;

    public static Session Create(ParseResult r, CancellationToken token)
    {
        var verbosity = r.GetValue(GlobalOptions.Silent) ? Verbosity.Silent
            : r.GetValue(GlobalOptions.Ndjson) ? Verbosity.Ndjson
            : r.GetValue(GlobalOptions.Json) ? Verbosity.Json
            : r.GetValue(GlobalOptions.Quiet) ? Verbosity.Quiet
            : r.GetValue(GlobalOptions.Verbose) ? Verbosity.Verbose
            : Verbosity.Normal;
        var color = r.GetValue(GlobalOptions.NoColor) ? ColorMode.Never : r.GetValue(GlobalOptions.Color);
        if (Environment.GetEnvironmentVariable("NOI_COLOR") is { Length: > 0 } envColor && r.GetResult(GlobalOptions.Color) is null && !r.GetValue(GlobalOptions.NoColor))
            color = Enum.TryParse<ColorMode>(envColor, true, out var c) ? c : color;
        var progress = r.GetValue(GlobalOptions.Progress);
        var term = Term.Detect(color, progress, r.GetValue(GlobalOptions.Ascii));
        var ui = new Ui(verbosity, term);

        var settingsFile = r.GetValue(GlobalOptions.SettingsFile)
                           ?? (Environment.GetEnvironmentVariable("NOI_SETTINGS") is { Length: > 0 } envFile ? envFile : null);
        var useApp = r.GetValue(GlobalOptions.UseSettings) || settingsFile is not null;
        var settings = SettingsFor(settingsFile, useApp);
        settings.General.VerboseLog = verbosity == Verbosity.Verbose;

        if (r.GetValue(GlobalOptions.Jobs) is { } jobs) settings.Performance.ParallelJobs = jobs;
        else if (Environment.GetEnvironmentVariable("NOI_JOBS") is { } envJobs && int.TryParse(envJobs, out var j) && j > 0) settings.Performance.ParallelJobs = j;
        if (r.GetValue(GlobalOptions.Threads) is { } threads) settings.Performance.ThreadsPerJob = threads;
        if ((r.GetValue(GlobalOptions.Temp) ?? Environment.GetEnvironmentVariable("NOI_TEMP")) is { Length: > 0 } temp) settings.Performance.TempFolder = temp;

        var log = CliLog.Open(r.GetValue(GlobalOptions.LogFile), r.GetValue(GlobalOptions.NoLog), ui);
        var noInput = r.GetValue(GlobalOptions.NoInput) || verbosity is Verbosity.Silent or Verbosity.Json or Verbosity.Ndjson || !term.StdinIsTerminal || Term.InCi;
        var offline = r.GetValue(GlobalOptions.Offline) || Environment.GetEnvironmentVariable("NOI_OFFLINE") is "1" or "true" or "yes";
        log.Write(LogLevel.Debug, $"noi {Program.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        log.Write(LogLevel.Debug, $"settings: {(settingsFile is not null ? settingsFile : useApp ? "the app's" : "recommended defaults")}");
        return new Session(ui, log, settings, token)
        {
            DryRun = r.GetValue(GlobalOptions.DryRun),
            Yes = r.GetValue(GlobalOptions.Yes),
            NoInput = noInput,
            Offline = offline,
            UsesAppSettings = useApp,
        };
    }

    /// <summary>
    /// The settings a run starts from: the recommended defaults (Q3: noi doesn't follow the app's saved choices
    /// unless asked), or the app's settings / a settings file. Presets saved from Custom in the app can always be
    /// named, since naming one is asking for it.
    /// </summary>
    private static AppSettings SettingsFor(string? file, bool useApp)
    {
        if (file is not null)
        {
            if (!File.Exists(file)) throw new UsageException($"No settings file at {file}.", "Leave out --settings to use the recommended values.");
            return AppSettings.Peek(file);
        }
        var app = AppSettings.Peek();
        if (useApp) return app;
        var fresh = new AppSettings();
        fresh.Presets.Saved = app.Presets.Saved;
        // Keys are found where the app finds them, or in the folders chosen in its Settings › Keys.
        fresh.Keys = app.Keys;
        fresh.Dats.OwnFolder = app.Dats.OwnFolder;
        return fresh;
    }

    /// <summary>The same session with other settings (a saved plan runs with the settings it was made with).</summary>
    public Session With(AppSettings settings) =>
        ReferenceEquals(settings, Settings) ? this : new Session(Ui, Log, settings, Token)
        {
            DryRun = DryRun, Yes = Yes, NoInput = NoInput, Offline = Offline, UsesAppSettings = UsesAppSettings,
        };

    /// <summary>Asks, or says why it can't: with --yes the answer is yes; without a terminal it's no.</summary>
    public bool Confirm(string question, bool defaultYes = false)
    {
        if (Yes) return true;
        if (!CanAsk) return false;
        return Ui.Confirm(question, defaultYes);
    }

    public void Dispose() => Log.Dispose();
}

/// <summary>
/// noi's log: every line goes to the session's log file (logs/noi-yyyyMMdd-HHmmss.log beside the app's, kept 30
/// days); the console shows warnings (and, with --verbose, everything), as each mode allows.
/// </summary>
public sealed class CliLog : ILog, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _file;
    private readonly Ui _ui;

    private CliLog(StreamWriter? file, string? path, Ui ui)
    {
        _file = file;
        _ui = ui;
        Path = path;
    }

    public string? Path { get; }
    public int Warnings { get; private set; }
    public int Errors { get; private set; }

    public static CliLog Open(string? path, bool none, Ui ui)
    {
        if (none) return new CliLog(null, null, ui);
        try
        {
            if (path is null)
            {
                var folder = AppSettings.LogDirectory;
                Directory.CreateDirectory(folder);
                Prune(folder);
                path = System.IO.Path.Combine(folder, $"noi-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                if (File.Exists(path)) path = System.IO.Path.Combine(folder, $"noi-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            }
            else if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            return new CliLog(new StreamWriter(stream) { AutoFlush = true }, path, ui);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new CliLog(null, null, ui);
        }
    }

    /// <summary>Logs older than 30 days go, as the app's do.</summary>
    private static void Prune(string folder)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(folder, "noi-*.log").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private bool _closed;

    public void Write(LogLevel level, string message)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (level == LogLevel.Warning) Warnings++;
            if (level == LogLevel.Error) Errors++;
            _file?.WriteLine(new LogEntry(DateTime.Now, level, message).ToString());
        }
        switch (level)
        {
            case LogLevel.Warning:
                // During a run a warning is usually a file's result (its own line says it); --verbose shows them all.
                if (!_ui.InRun || _ui.IsVerbose) _ui.Warn(message);
                break;
            case LogLevel.Error:
                // Normal and quiet modes say a failure on the file's own line; the log's wording shows with --verbose.
                if (_ui.IsVerbose) _ui.Error(message);
                break;
            default:
                if (_ui.IsVerbose) _ui.Debug(message);
                break;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _file?.Dispose();
        }
    }
}
