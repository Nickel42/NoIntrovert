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
/// UI thread a few times a second and also appended to a session log file.
/// </summary>
public partial class LogViewModel : ViewModelBase, ILog
{
    private const int MaxVisible = 5000;

    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly List<LogEntry> _all = [];
    private readonly StreamWriter? _file;
    private readonly IDialogs _dialogs;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    [ObservableProperty] public partial bool ShowDebug { get; set; }
    [ObservableProperty] public partial int ErrorCount { get; set; }
    [ObservableProperty] public partial int WarningCount { get; set; }

    public string LogFilePath { get; } = "";

    public event Action? EntriesAdded;

    public LogViewModel(IDialogs dialogs, bool showDebug)
    {
        _dialogs = dialogs;
        ShowDebug = showDebug;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoIntrovert", "logs");
            Directory.CreateDirectory(dir);
            LogFilePath = Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _file = new StreamWriter(LogFilePath, append: false) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _file = null;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) => Drain();
        timer.Start();
    }

    public void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        _pending.Enqueue(entry);
        try { lock (_pending) _file?.WriteLine(entry.ToString()); }
        catch (IOException) { }
    }

    private void Drain()
    {
        if (_pending.IsEmpty) return;
        var added = false;
        while (_pending.TryDequeue(out var e))
        {
            _all.Add(e);
            if (e.Level == LogLevel.Error) ErrorCount++;
            if (e.Level == LogLevel.Warning) WarningCount++;
            if (e.Level == LogLevel.Debug && !ShowDebug) continue;
            Entries.Add(e);
            added = true;
        }
        while (Entries.Count > MaxVisible) Entries.RemoveAt(0);
        if (_all.Count > MaxVisible * 4) _all.RemoveRange(0, _all.Count - MaxVisible * 4);
        if (added) EntriesAdded?.Invoke();
    }

    partial void OnShowDebugChanged(bool value)
    {
        Entries.Clear();
        foreach (var e in _all.Where(e => value || e.Level != LogLevel.Debug).TakeLast(MaxVisible))
            Entries.Add(e);
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

    [RelayCommand]
    private async Task Copy() =>
        await _dialogs.SetClipboardAsync(string.Join(Environment.NewLine, Entries.Select(e => e.ToString())));

    [RelayCommand]
    private async Task Save()
    {
        var path = await _dialogs.SaveFileAsync("Save log", $"NoIntrovert-{DateTime.Now:yyyyMMdd-HHmmss}.log", "Log file", "*.log");
        if (path is null) return;
        await File.WriteAllLinesAsync(path, _all.Select(e => e.ToString()));
        Write(LogLevel.Info, $"Log saved to {path}");
    }
}
