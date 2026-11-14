using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NoIntrovert.Core;
using NoIntrovert.Core.Jobs;

namespace NoIntrovert.Views;

/// <summary>
/// Colours for job status and log levels. They come from the theme showing (ThemeResources' Status* keys, made to
/// read at 3:1 on its cards, 4.5:1 for the *Text ones), and follow it when the theme or Dark/Light changes.
/// </summary>
public static class Palette
{
    private static readonly SolidColorBrush OkBrush = new(Color.Parse("#2EA66B"));
    private static readonly SolidColorBrush WarnBrush = new(Color.Parse("#D49A1E"));
    private static readonly SolidColorBrush BadBrush = new(Color.Parse("#E0524A"));
    private static readonly SolidColorBrush BusyBrush = new(Color.Parse("#3B8EEA"));
    private static readonly SolidColorBrush MutedBrush = new(Color.Parse("#8A8F98"));
    private static readonly SolidColorBrush OkTextBrush = new(Color.Parse("#2EA66B"));
    private static readonly SolidColorBrush BadTextBrush = new(Color.Parse("#E0524A"));

    public static IBrush Ok { get { Hook(); return OkBrush; } }
    public static IBrush Warn { get { Hook(); return WarnBrush; } }
    public static IBrush Bad { get { Hook(); return BadBrush; } }
    public static IBrush Busy { get { Hook(); return BusyBrush; } }
    public static IBrush Muted { get { Hook(); return MutedBrush; } }
    /// <summary>For words (PASS, FAIL), not only a dot or a glyph: 4.5:1.</summary>
    public static IBrush OkText { get { Hook(); return OkTextBrush; } }
    public static IBrush BadText { get { Hook(); return BadTextBrush; } }

    private static bool _hooked;

    /// <summary>
    /// Once there's an Application: read the colours now, and again whenever its resources change (App announces
    /// every theme change with one top-level key) or the OS switches Dark/Light under System.
    /// </summary>
    private static void Hook()
    {
        if (_hooked || Avalonia.Application.Current is not { } app) return;
        _hooked = true;
        app.ResourcesChanged += (_, _) => Refresh();
        app.ActualThemeVariantChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Reads the Status* colours of the theme showing into the brushes every converter hands out.</summary>
    public static void Refresh()
    {
        if (Avalonia.Application.Current is not { } app) return;
        void Read(string key, SolidColorBrush brush)
        {
            if (app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is ISolidColorBrush b && brush.Color != b.Color) brush.Color = b.Color;
        }
        Read("StatusOk", OkBrush);
        Read("StatusWarn", WarnBrush);
        Read("StatusBad", BadBrush);
        Read("StatusBusy", BusyBrush);
        Read("StatusMuted", MutedBrush);
        Read("StatusOkText", OkTextBrush);
        Read("StatusBadText", BadTextBrush);
    }

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

    public static readonly IValueConverter PassBrush = new FuncValueConverter<bool, IBrush>(p => p ? OkText : BadText);

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
