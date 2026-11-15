using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoIntrovert.Core;
using NoIntrovert.Services;

namespace NoIntrovert.ViewModels;

/// <summary>
/// The shared log pane. Any thread can write; entries are batched onto the
/// UI thread a few times a second and also appended to this session's log
/// file in <see cref="AppSettings.LogDirectory"/>. It shows in the Activity
/// drawer's Log tab; the status bar shows its counts (see <see cref="ChipText"/>).
/// </summary>
public partial class LogViewModel : ViewModelBase, ILog
{
    internal const int MaxVisible = 5000;

    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly List<LogEntry> _all = [];
    private readonly SessionLogFile? _file;
    private readonly IDialogs _dialogs;

    public BulkObservableCollection<LogEntry> Entries { get; } = [];

    [ObservableProperty] public partial bool ShowDebug { get; set; }

    /// <summary>
    /// Shows only error lines, and Copy and Save write only those. Show debug
    /// makes no difference while it's on; the counts still count every line.
    /// </summary>
    [ObservableProperty] public partial bool ShowOnlyErrors { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasErrors), nameof(HasProblems), nameof(CountsText), nameof(ChipText))] public partial int ErrorCount { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasProblems), nameof(CountsText), nameof(ChipText))] public partial int WarningCount { get; set; }

    /// <summary>The Log tab's counts: "1 error · 0 warnings".</summary>
    public string CountsText => DescribeCounts(ErrorCount, WarningCount);

    internal static string DescribeCounts(int errors, int warnings) => $"{Words.Count(errors, "error")} · {Words.Count(warnings, "warning")}";

    /// <summary>The status bar's chip: only the counts there are, "1 error · 3 warnings" or "2 warnings"; "" with none.</summary>
    public string ChipText => DescribeProblems(ErrorCount, WarningCount);

    internal static string DescribeProblems(int errors, int warnings) => string.Join(" · ",
        new[] { errors > 0 ? Words.Count(errors, "error") : null, warnings > 0 ? Words.Count(warnings, "warning") : null }.OfType<string>());

    /// <summary>Turns the status bar's chip red, so an error is noticed while Activity is closed.</summary>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>Whether the status bar shows the chip: an error or a warning was logged.</summary>
    public bool HasProblems => ErrorCount + WarningCount > 0;

    /// <summary>Raised on the UI thread when lines with at least one error arrive (Settings › Log may open Activity).</summary>
    public event Action? ErrorsLogged;

    /// <summary>This session's log file; empty when it couldn't be created.</summary>
    public string LogFilePath => _file?.Path ?? "";

    public event Action? EntriesAdded;

    public LogViewModel(IDialogs dialogs, AppSettings settings)
    {
        _dialogs = dialogs;
        ShowDebug = settings.General.VerboseLog;
        // Old logs are deleted in the background, so a slow or full log folder doesn't hold up the window.
        var keepDays = settings.General.KeepLogsDays;
        var legacy = SessionLogFile.LegacyDirectory;
        _ = Task.Run(() =>
        {
            SessionLogFile.DeleteOld(AppSettings.LogDirectory, keepDays, DateTime.Now);
            // Logs from before they moved to the settings folder get the same rule, so they don't stay forever.
            if (legacy is not null) SessionLogFile.DeleteOld(legacy, keepDays, DateTime.Now, SessionLogFile.LegacyPattern);
        });
        _file = SessionLogFile.Start(AppSettings.LogDirectory, DateTime.Now);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) => Drain();
        timer.Start();
    }

    public void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        _pending.Enqueue(entry);
        _file?.Write(entry);
    }

    /// <summary>
    /// Moves what was written since the last tick into the list: one change for the batch
    /// (or, past <see cref="MaxVisible"/>, one replace keeping the newest lines), not one per line.
    /// </summary>
    internal void Drain()
    {
        if (_pending.IsEmpty) return;
        var errors = false;
        var batch = new List<LogEntry>();
        while (_pending.TryDequeue(out var e))
        {
            _all.Add(e);
            if (e.Level == LogLevel.Error) { ErrorCount++; errors = true; }
            if (e.Level == LogLevel.Warning) WarningCount++;
            if (!Shows(e)) continue;
            batch.Add(e);
        }
        var added = batch.Count > 0;
        if (Entries.Count + batch.Count <= MaxVisible) Entries.AddRange(batch);
        else if (batch.Count >= MaxVisible) Entries.ReplaceAll(batch.Skip(batch.Count - MaxVisible));
        else Entries.ReplaceAll(Entries.Skip(Entries.Count + batch.Count - MaxVisible).Concat(batch).ToList());
        if (_all.Count > MaxVisible * 4) _all.RemoveRange(0, _all.Count - MaxVisible * 4);
        if (errors) ErrorsLogged?.Invoke();
        if (added) EntriesAdded?.Invoke();
    }

    /// <summary>Whether a line is shown (and copied and saved) with the current Show debug and Show only errors.</summary>
    private bool Shows(LogEntry e) => ShowOnlyErrors ? e.Level == LogLevel.Error : ShowDebug || e.Level != LogLevel.Debug;

    partial void OnShowDebugChanged(bool value) => Refilter();

    partial void OnShowOnlyErrorsChanged(bool value) => Refilter();

    private void Refilter()
    {
        Entries.ReplaceAll(_all.Where(Shows).TakeLast(MaxVisible));
        EntriesAdded?.Invoke();
    }

    [RelayCommand]
    private void Clear()
    {
        _all.Clear();
        Entries.Clear();
        ErrorCount = 0;
        WarningCount = 0;
    }

    /// <summary>
    /// What Copy and Save both write: every line this session has kept (not
    /// just the last <see cref="MaxVisible"/> shown), debug lines only when
    /// Show debug is on, and only errors while Show only errors is on.
    /// </summary>
    internal IEnumerable<string> ExportLines() => _all.Where(Shows).Select(e => e.ToString());

    [RelayCommand]
    private async Task Copy() =>
        await _dialogs.SetClipboardAsync(string.Join(Environment.NewLine, ExportLines()));

    [RelayCommand]
    private async Task Save()
    {
        var path = await _dialogs.SaveFileAsync("Save log", $"NoIntrovert-{DateTime.Now:yyyyMMdd-HHmmss}.log", "Log file", "*.log");
        if (path is null) return;
        try
        {
            await File.WriteAllLinesAsync(path, ExportLines().ToList());
            Write(LogLevel.Info, $"Log saved to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Write(LogLevel.Warning, $"Couldn't save the log to {path}: {ex.Message}");
        }
    }

    /// <summary>Opens the folder of saved session logs (Settings › Log › Open log folder).</summary>
    [RelayCommand]
    private Task OpenLogFolder() => OpenLogFolderAsync(_dialogs, this);

    /// <summary>Opens <see cref="AppSettings.LogDirectory"/> in the file manager, creating it first if needed; a folder that can't be made is logged.</summary>
    public static async Task OpenLogFolderAsync(IDialogs dialogs, ILog? log = null)
    {
        try { Directory.CreateDirectory(AppSettings.LogDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Warn($"Couldn't open the log folder {AppSettings.LogDirectory}: {ex.Message}");
            return;
        }
        await dialogs.OpenFolderAsync(AppSettings.LogDirectory);
    }
}

/// <summary>
/// One app session's log file, NoIntrovert-yyyyMMdd-HHmmss.log, written as
/// entries arrive. Debug lines are left out. Lines are buffered and flushed
/// every <see cref="FlushInterval"/>, and when the app exits, rather than one
/// disk write per line. Writing never throws: a log that can't be written just stops.
/// </summary>
internal sealed class SessionLogFile : IDisposable
{
    /// <summary>How long a written line may wait in the buffer before it reaches the disk.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private const string Prefix = "NoIntrovert-";

    /// <summary>Older versions' session logs: session-yyyyMMdd-HHmmss.log in <see cref="LegacyDirectory"/>.</summary>
    public const string LegacyPattern = "session-*.log";

    /// <summary>
    /// Where older versions saved session logs (%LocalAppData%\NoIntrovert\logs),
    /// or null when the settings folder is moved (tests, a portable copy), so
    /// those never touch the user's own files.
    /// </summary>
    public static string? LegacyDirectory =>
        AppSettings.SettingsDirectoryOverride is null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOINTROVERT_SETTINGS_DIR"))
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoIntrovert", "logs")
            : null;
    private readonly StreamWriter _writer;
    private readonly System.Threading.Timer _flushTimer;
    private bool _dirty;
    private bool _closed;

    private SessionLogFile(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, FlushInterval, FlushInterval);
        // A normal close (and Environment.Exit) runs this, so nothing buffered is lost.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    public string Path { get; }

    /// <summary>The file name for a session started at <paramref name="start"/>.</summary>
    public static string NameFor(DateTime start) => $"{Prefix}{start:yyyyMMdd-HHmmss}.log";

    /// <summary>Info and above go to the file; debug lines only to the window.</summary>
    public static bool Keeps(LogLevel level) => level >= LogLevel.Info;

    /// <summary>Creates the folder and opens a new session file in it; null when that isn't possible.</summary>
    public static SessionLogFile? Start(string directory, DateTime start)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, NameFor(start));
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new SessionLogFile(path, new StreamWriter(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(LogEntry entry)
    {
        if (!Keeps(entry.Level)) return;
        try
        {
            lock (_writer)
            {
                if (_closed) return;
                _writer.WriteLine(entry.ToString());
                _dirty = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>Writes what's buffered to the disk (the timer does this every <see cref="FlushInterval"/>).</summary>
    public void Flush()
    {
        try
        {
            lock (_writer)
            {
                if (!_dirty || _closed) return;
                _writer.Flush();
                _dirty = false;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>Flushes and closes the file; later writes are dropped.</summary>
    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _flushTimer.Dispose();
        try
        {
            lock (_writer)
            {
                if (_closed) return;
                _closed = true;
                _writer.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>
    /// Deletes session logs last written more than <paramref name="keepDays"/>
    /// days before <paramref name="now"/>; 0 or less keeps them all. Only files
    /// named like a session log (<paramref name="pattern"/>, by default this
    /// version's names) are touched. Returns how many were deleted.
    /// </summary>
    public static int DeleteOld(string directory, int keepDays, DateTime now, string pattern = Prefix + "*.log")
    {
        if (keepDays <= 0 || !Directory.Exists(directory)) return 0;
        var cutoff = now - TimeSpan.FromDays(keepDays);
        var deleted = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                try
                {
                    if (File.GetLastWriteTime(file) >= cutoff) continue;
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // in use or protected: try again next start
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return deleted;
    }
}
