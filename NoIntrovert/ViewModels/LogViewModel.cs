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
/// file in <see cref="AppSettings.LogDirectory"/>. It starts collapsed to one
/// bar (the counts and Show log) unless the user left it expanded.
/// </summary>
public partial class LogViewModel : ViewModelBase, ILog
{
    private const int MaxVisible = 5000;

    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly List<LogEntry> _all = [];
    private readonly SessionLogFile? _file;
    private readonly IDialogs _dialogs;
    private readonly AppSettings _settings;
    private bool _autoExpanding;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    [ObservableProperty] public partial bool ShowDebug { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasErrors), nameof(CountsText))] public partial int ErrorCount { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountsText))] public partial int WarningCount { get; set; }

    /// <summary>The bar's counts: "1 error · 0 warnings".</summary>
    public string CountsText => DescribeCounts(ErrorCount, WarningCount);

    internal static string DescribeCounts(int errors, int warnings) => $"{Words.Count(errors, "error")} · {Words.Count(warnings, "warning")}";

    /// <summary>The full log (Show log) rather than just its bar; remembered in <see cref="GeneralSettings.LogExpanded"/>.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ToggleText))] public partial bool IsExpanded { get; set; }

    /// <summary>Turns the bar's counts red, so problems are noticed while the log is collapsed.</summary>
    public bool HasErrors => ErrorCount > 0;

    public string ToggleText => IsExpanded ? "Hide log" : "Show log";

    /// <summary>This session's log file; empty when it couldn't be created.</summary>
    public string LogFilePath => _file?.Path ?? "";

    public event Action? EntriesAdded;

    public LogViewModel(IDialogs dialogs, AppSettings settings)
    {
        _dialogs = dialogs;
        _settings = settings;
        ShowDebug = settings.General.VerboseLog;
        IsExpanded = settings.General.LogExpanded;
        SessionLogFile.DeleteOld(AppSettings.LogDirectory, settings.General.KeepLogsDays, DateTime.Now);
        // Logs from before they moved to the settings folder get the same rule, so they don't stay forever.
        if (SessionLogFile.LegacyDirectory is { } legacy)
            SessionLogFile.DeleteOld(legacy, settings.General.KeepLogsDays, DateTime.Now, SessionLogFile.LegacyPattern);
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

    private void Drain()
    {
        if (_pending.IsEmpty) return;
        var added = false;
        var errors = false;
        while (_pending.TryDequeue(out var e))
        {
            _all.Add(e);
            if (e.Level == LogLevel.Error) { ErrorCount++; errors = true; }
            if (e.Level == LogLevel.Warning) WarningCount++;
            if (e.Level == LogLevel.Debug && !ShowDebug) continue;
            Entries.Add(e);
            added = true;
        }
        while (Entries.Count > MaxVisible) Entries.RemoveAt(0);
        if (_all.Count > MaxVisible * 4) _all.RemoveRange(0, _all.Count - MaxVisible * 4);
        if (errors && !IsExpanded && _settings.General.OpenLogOnErrors)
        {
            // Opened for the user, not chosen by them: the saved choice stays as it was.
            _autoExpanding = true;
            IsExpanded = true;
            _autoExpanding = false;
        }
        if (added) EntriesAdded?.Invoke();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (_autoExpanding || _settings.General.LogExpanded == value) return;
        _settings.General.LogExpanded = value;
        _settings.Save();
    }

    partial void OnShowDebugChanged(bool value)
    {
        Entries.Clear();
        foreach (var e in _all.Where(e => value || e.Level != LogLevel.Debug).TakeLast(MaxVisible))
            Entries.Add(e);
        EntriesAdded?.Invoke();
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

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
    /// Show debug is on.
    /// </summary>
    internal IEnumerable<string> ExportLines() =>
        _all.Where(e => ShowDebug || e.Level != LogLevel.Debug).Select(e => e.ToString());

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
/// entries arrive. Debug lines are left out. Writing never throws: a log
/// that can't be written just stops.
/// </summary>
internal sealed class SessionLogFile
{
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

    private SessionLogFile(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

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
            return new SessionLogFile(path, new StreamWriter(stream) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(LogEntry entry)
    {
        if (!Keeps(entry.Level)) return;
        try { lock (_writer) _writer.WriteLine(entry.ToString()); }
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
