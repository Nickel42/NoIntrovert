using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Views;

/// <summary>Colours for job status and log levels, tuned to read on both light and dark themes.</summary>
public static class Palette
{
    public static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#2EA66B"));
    public static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#D49A1E"));
    public static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#E0524A"));
    public static readonly IBrush Busy = new SolidColorBrush(Color.Parse("#3B8EEA"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A8F98"));

    public static readonly IValueConverter StatusBrush = new FuncValueConverter<JobStatus, IBrush>(s => s switch
    {
        JobStatus.Succeeded => Ok,
        JobStatus.Warning => Warn,
        JobStatus.Failed => Bad,
        JobStatus.Running => Busy,
        _ => Muted,
    });

    /// <summary>A log line's level glyph colour; info and debug are muted.</summary>
    public static readonly IValueConverter LevelBrush = new FuncValueConverter<LogLevel, IBrush>(l => l switch
    {
        LogLevel.Success => Ok,
        LogLevel.Warning => Warn,
        LogLevel.Error => Bad,
        _ => Muted,
    });

    /// <summary>A log line's level as a glyph, so it doesn't rest on colour alone: ✕ error, ⚠ warning, ✓ success, i info, · debug.</summary>
    public static readonly IValueConverter LevelGlyph = new FuncValueConverter<LogLevel, string>(l => l switch
    {
        LogLevel.Error => "✕",
        LogLevel.Warning => "⚠",
        LogLevel.Success => "✓",
        LogLevel.Debug => "·",
        _ => "i",
    });

    /// <summary>A log line's level in words, for screen readers.</summary>
    public static readonly IValueConverter LevelName = new FuncValueConverter<LogLevel, string>(l => l switch
    {
        LogLevel.Error => "Error",
        LogLevel.Warning => "Warning",
        LogLevel.Success => "Success",
        LogLevel.Debug => "Debug",
        _ => "Info",
    });

    /// <summary>Plain text uses the theme's foreground; only debug lines are dimmed.</summary>
    public static readonly IValueConverter LevelOpacity = new FuncValueConverter<LogLevel, double>(l => l == LogLevel.Debug ? 0.65 : 1.0);

    public static readonly IValueConverter PassBrush = new FuncValueConverter<bool, IBrush>(p => p ? Ok : Bad);

    /// <summary>A Library game's status dot: orange to convert, blue with originals left, green converted, red duplicate, grey nothing to do.</summary>
    public static readonly IValueConverter GameStatusBrush = new FuncValueConverter<Core.Library.GameStatus, IBrush>(s => s switch
    {
        Core.Library.GameStatus.NeedsConverting => Warn,
        Core.Library.GameStatus.OriginalsRemain => Busy,
        Core.Library.GameStatus.Converted => Ok,
        Core.Library.GameStatus.Duplicate => Bad,
        _ => Muted,
    });

    /// <summary>SVG path data (a page's rail icon) as a geometry to draw.</summary>
    public static readonly IValueConverter Geometry = new FuncValueConverter<string, Avalonia.Media.Geometry?>(d =>
        string.IsNullOrEmpty(d) ? null : StreamGeometry.Parse(d));
    public static readonly IValueConverter PassText = new FuncValueConverter<bool, string>(p => p ? "PASS" : "FAIL");

    public static readonly IValueConverter Seconds = new FuncValueConverter<TimeSpan?, string>(t =>
        t is { } v ? v.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture) + " s" : "—");

    public static readonly IValueConverter Bytes = new FuncValueConverter<long?, string>(b => b is { } v ? ChdJobs.Size(v) : "—");
}
