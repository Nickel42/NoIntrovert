using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NoIntrovert.Core.Emulators;

namespace NoIntrovert.Views;

/// <summary>The "Works with" list; set its DataContext to a <see cref="ViewModels.WorksWithViewModel"/>.</summary>
public partial class WorksWithList : UserControl
{
    public WorksWithList() => InitializeComponent();
}

/// <summary>The ✓ / ✕ / ? icon and dimming for a "Works with" entry.</summary>
public static class WorksWithConverters
{
    /// <summary>✓ is green (#4ADE80), not the brand colour, which reads as a warning next to red.</summary>
    public static readonly IBrush WorksBrush = new SolidColorBrush(Color.Parse("#4ADE80"));

    private static readonly Geometry Check = StreamGeometry.Parse("M2.5,7.5 L5.75,10.75 L11.5,3.75");
    private static readonly Geometry Cross = StreamGeometry.Parse("M3.5,3.5 L10.5,10.5 M10.5,3.5 L3.5,10.5");
    private static readonly Geometry Question = StreamGeometry.Parse("M4.5,5 C4.5,3.4 5.6,2.5 7,2.5 C8.4,2.5 9.5,3.4 9.5,4.8 C9.5,6.6 7,6.7 7,8.7 M7,11.5 L7,11.6");

    public static readonly IValueConverter Glyph = new FuncValueConverter<SupportState, Geometry>(s => s switch
    {
        SupportState.Works => Check,
        SupportState.DoesNot => Cross,
        _ => Question,
    });

    public static readonly IValueConverter Brush = new FuncValueConverter<SupportState, IBrush>(s => s switch
    {
        SupportState.Works => WorksBrush,
        SupportState.DoesNot => Palette.Bad,
        _ => Palette.Muted,
    });

    private static readonly IBrush RedFill = new SolidColorBrush(Color.Parse("#FF3C38"));
    private static readonly IBrush UnknownFill = new SolidColorBrush(Color.Parse("#4A4A60"));
    private static readonly IBrush DarkGlyph = new SolidColorBrush(Color.Parse("#191923"));
    private static readonly IBrush LightGlyph = new SolidColorBrush(Color.Parse("#E2E1F0"));

    /// <summary>The icon's round tile: green, red, or grey for not confirmed.</summary>
    public static readonly IValueConverter Fill = new FuncValueConverter<SupportState, IBrush>(s => s switch
    {
        SupportState.Works => WorksBrush,
        SupportState.DoesNot => RedFill,
        _ => UnknownFill,
    });

    /// <summary>The glyph on the tile: dark on green and red, light on grey.</summary>
    public static readonly IValueConverter GlyphBrush = new FuncValueConverter<SupportState, IBrush>(s =>
        s == SupportState.Unknown ? LightGlyph : DarkGlyph);

    /// <summary>The state in words, for screen readers.</summary>
    public static readonly IValueConverter StateText = new FuncValueConverter<SupportState, string>(s => s switch
    {
        SupportState.Works => "works",
        SupportState.DoesNot => "doesn't work",
        _ => "not known",
    });

    /// <summary>An emulator that can't open the output has its name dimmed.</summary>
    public static readonly IValueConverter NameOpacity = new FuncValueConverter<SupportState, double>(s => s == SupportState.DoesNot ? 0.5 : 1.0);
}
