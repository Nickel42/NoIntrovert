using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace NoIntrovert.Controls;

/// <summary>How a <see cref="GlowBorder"/> moves.</summary>
public enum GlowBorderMode
{
    /// <summary>A short gradient beam runs around the border.</summary>
    Travel,
    /// <summary>The whole border breathes, with soft glows along its edge.</summary>
    Pulse,
}

/// <summary>The built-in colors of a <see cref="GlowBorder"/>; each has a light and a dark version.</summary>
public enum GlowBorderPalette
{
    /// <summary>Cool for <see cref="GlowBorderMode.Travel"/>, Warm for <see cref="GlowBorderMode.Pulse"/>.</summary>
    Auto,
    /// <summary>Blue, violet, pink.</summary>
    Cool,
    /// <summary>Orange, red, pink.</summary>
    Warm,
    /// <summary>Shades of the app's accent color.</summary>
    Accent,
}

/// <summary>
/// Wraps one element (a Button, a card Border, anything) and draws an animated
/// border over it: a beam that travels around the edge, or a pulse, with a soft
/// glow on the inside. The beam follows the child's corner radius unless
/// <see cref="CornerRadius"/> is set.
/// </summary>
/// <example>
/// <code>
/// &lt;c:GlowBorder IsActive="{Binding IsRunning}"&gt;
///     &lt;Border Classes="card"&gt;...&lt;/Border&gt;
/// &lt;/c:GlowBorder&gt;
/// </code>
/// </example>
public class GlowBorder : Decorator
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<GlowBorder, bool>(nameof(IsActive), true);

    public static readonly StyledProperty<GlowBorderMode> ModeProperty =
        AvaloniaProperty.Register<GlowBorder, GlowBorderMode>(nameof(Mode));

    public static readonly StyledProperty<GlowBorderPalette> PaletteProperty =
        AvaloniaProperty.Register<GlowBorder, GlowBorderPalette>(nameof(Palette));

    /// <summary>Custom colors, overriding <see cref="Palette"/>. Offsets run 0 (tail) to 1 (head).</summary>
    public static readonly StyledProperty<GradientStops?> GradientStopsProperty =
        AvaloniaProperty.Register<GlowBorder, GradientStops?>(nameof(GradientStops));

    /// <summary>One lap (Travel) or one breath (Pulse).</summary>
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<GlowBorder, TimeSpan>(nameof(Duration), TimeSpan.FromSeconds(3));

    /// <summary>The beam's length as a fraction of the border's perimeter (Travel only).</summary>
    public static readonly StyledProperty<double> BeamLengthProperty =
        AvaloniaProperty.Register<GlowBorder, double>(nameof(BeamLength), 0.3);

    public static readonly StyledProperty<double> BeamThicknessProperty =
        AvaloniaProperty.Register<GlowBorder, double>(nameof(BeamThickness), 1.5);

    /// <summary>How far the inner glow reaches into the child, in pixels; 0 turns it off.</summary>
    public static readonly StyledProperty<double> GlowSizeProperty =
        AvaloniaProperty.Register<GlowBorder, double>(nameof(GlowSize), 22);

    public static readonly StyledProperty<double> GlowOpacityProperty =
        AvaloniaProperty.Register<GlowBorder, double>(nameof(GlowOpacity), 0.45);

    /// <summary>Overrides the corner radius read from the child.</summary>
    public static readonly StyledProperty<CornerRadius?> CornerRadiusProperty =
        AvaloniaProperty.Register<GlowBorder, CornerRadius?>(nameof(CornerRadius));

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public GlowBorderMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public GlowBorderPalette Palette { get => GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public GradientStops? GradientStops { get => GetValue(GradientStopsProperty); set => SetValue(GradientStopsProperty, value); }
    public TimeSpan Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double BeamLength { get => GetValue(BeamLengthProperty); set => SetValue(BeamLengthProperty, value); }
    public double BeamThickness { get => GetValue(BeamThicknessProperty); set => SetValue(BeamThicknessProperty, value); }
    public double GlowSize { get => GetValue(GlowSizeProperty); set => SetValue(GlowSizeProperty, value); }
    public double GlowOpacity { get => GetValue(GlowOpacityProperty); set => SetValue(GlowOpacityProperty, value); }
    public CornerRadius? CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    // Fades the beam in and out when IsActive changes.
    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(350);

    // Both layers sit exactly over the child. The glow is drawn as wide strokes and
    // blurred; its host clips the blur to the child's rounded shape, so it lights
    // the inside only.
    private readonly Panel _glowHost;
    private readonly Layer _glowLayer;
    private readonly Layer _beamLayer;
    private readonly BlurEffect _blur = new();
    private Rect _clipRect;
    private CornerRadius _clipRadius;

    private readonly Stopwatch _clock = new();
    private TimeSpan _lastFrame;
    private double _intensity;
    private bool _frameRequested;

    public GlowBorder()
    {
        _glowLayer = new Layer(this, glow: true) { Effect = _blur };
        _glowHost = new Panel { IsHitTestVisible = false, ZIndex = int.MaxValue - 1, Children = { _glowLayer } };
        _beamLayer = new Layer(this, glow: false) { IsHitTestVisible = false, ZIndex = int.MaxValue };
        VisualChildren.Add(_glowHost);
        VisualChildren.Add(_beamLayer);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty || change.Property == IsVisibleProperty)
            RequestFrame();
        if (change.Property == CornerRadiusProperty)
            InvalidateArrange();
        Invalidate();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _intensity = IsActive ? 1 : 0;
        _clock.Start();
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _clock.Stop();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _glowHost.Measure(availableSize);
        _beamLayer.Measure(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        var over = Child?.Bounds ?? new Rect(size);
        _glowHost.Arrange(over);
        _beamLayer.Arrange(over);
        UpdateClip();
        return size;
    }

    private void UpdateClip()
    {
        var rect = new Rect(_glowHost.Bounds.Size);
        var radius = EffectiveCornerRadius();
        if (rect == _clipRect && radius == _clipRadius) return;
        (_clipRect, _clipRadius) = (rect, radius);
        _glowHost.Clip = RoundedGeometry(rect, radius);
    }

    private void RequestFrame()
    {
        if (_frameRequested || !IsVisible || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameRequested = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        _frameRequested = false;
        if (VisualRoot is null) return;

        var now = _clock.Elapsed;
        var step = (now - _lastFrame).TotalMilliseconds / FadeTime.TotalMilliseconds;
        _lastFrame = now;
        _intensity = Math.Clamp(_intensity + (IsActive ? step : -step), 0, 1);

        // The child's corner radius can change without a new arrange (a style, a pseudo-class).
        UpdateClip();
        var blur = Math.Max(0, GlowSize) * 0.6;
        if (_blur.Radius != blur) _blur.Radius = blur;
        Invalidate();
        // Keep going while there's something to draw; a property change restarts it.
        if (IsActive || _intensity > 0) RequestFrame();
    }

    private void Invalidate()
    {
        _glowLayer.InvalidateVisual();
        _beamLayer.InvalidateVisual();
    }

    private CornerRadius EffectiveCornerRadius() => CornerRadius ?? Child switch
    {
        Border b => b.CornerRadius,
        TemplatedControl t => t.CornerRadius,
        _ => default,
    };

    private void Render(DrawingContext context, bool glow)
    {
        if (_intensity <= 0) return;
        var bounds = new Rect(_beamLayer.Bounds.Size);
        if (bounds.Width < 2 || bounds.Height < 2) return;

        var radius = EffectiveCornerRadius();
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var colors = GradientStops is { Count: > 0 } stops ? stops : DefaultStops(dark);
        var t = _clock.Elapsed.TotalSeconds / Math.Max(0.1, Duration.TotalSeconds);

        if (glow)
        {
            var size = Math.Max(0, GlowSize);
            // Glows read a little stronger against a dark background.
            var opacity = Math.Clamp(GlowOpacity, 0, 1) * (dark ? 1.0 : 0.8) * _intensity;
            if (size <= 0 || opacity <= 0) return;
            // A stroke centered on the edge; the outer half is clipped away.
            var path = new Perimeter(bounds, radius, 0);
            if (path.Length > 0) DrawStroke(context, path, colors, t, size * 0.9, opacity, glow: true);
        }
        else
        {
            var thickness = Math.Max(0.5, BeamThickness);
            var path = new Perimeter(bounds.Deflate(thickness / 2), radius, thickness / 2);
            if (path.Length <= 0) return;
            using (context.PushClip(new RoundedRect(bounds, radius)))
                DrawStroke(context, path, colors, t, thickness, _intensity, glow: false);
        }
    }

    private void DrawStroke(DrawingContext context, Perimeter path, GradientStops colors, double t,
        double thickness, double opacity, bool glow)
    {
        if (Mode == GlowBorderMode.Travel)
        {
            var head = Frac(t) * path.Length;
            var length = Math.Clamp(BeamLength, 0.02, 1) * path.Length;
            // Short flat-capped segments, so neighbors don't overlap and darken.
            var segments = Math.Max(8, (int)Math.Ceiling(length / 2));
            for (var i = 0; i < segments; i++)
            {
                var u = (i + 0.5) / segments;
                var a = path.PointAt(head - length * (1 - (double)i / segments));
                var b = path.PointAt(head - length * (1 - (double)(i + 1) / segments));
                Line(context, a, b, WithAlpha(Sample(colors, u), BeamShape(u) * opacity), thickness);
            }
        }
        else
        {
            var breath = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t);
            // The colors and the glowing patches drift slowly around the edge.
            var drift = t * 0.08;
            var patches = Math.Clamp((int)Math.Round(path.Length / 110), 3, 12);
            var segments = Math.Max(16, (int)Math.Ceiling(path.Length / 3));
            for (var i = 0; i < segments; i++)
            {
                var f0 = (double)i / segments;
                var f1 = (double)(i + 1) / segments;
                var f = Frac(f0 + 0.5 / segments - drift);
                var a = path.PointAt(f0 * path.Length);
                var b = path.PointAt(f1 * path.Length);
                double alpha;
                if (glow)
                {
                    // Each patch breathes on its own phase.
                    var p = f * patches;
                    var own = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (t + Math.Floor(p) * 0.37));
                    alpha = Math.Pow(Math.Sin(Math.PI * Frac(p)), 2) * (0.25 + 0.75 * own);
                }
                else alpha = 0.35 + 0.65 * breath;
                Line(context, a, b, WithAlpha(Sample(colors, Mirror(f)), alpha * opacity), thickness);
            }
        }
    }

    private static void Line(DrawingContext context, Point a, Point b, Color color, double thickness)
    {
        if (color.A == 0) return;
        context.DrawLine(new Pen(new ImmutableSolidColorBrush(color), thickness, lineCap: PenLineCap.Flat), a, b);
    }

    private GradientStops DefaultStops(bool dark)
    {
        var palette = Palette == GlowBorderPalette.Auto
            ? (Mode == GlowBorderMode.Travel ? GlowBorderPalette.Cool : GlowBorderPalette.Warm)
            : Palette;
        Color[] c = palette switch
        {
            GlowBorderPalette.Warm => dark
                ? [Color.Parse("#FB923C"), Color.Parse("#F87171"), Color.Parse("#F472B6")]
                : [Color.Parse("#EA580C"), Color.Parse("#DC2626"), Color.Parse("#DB2777")],
            GlowBorderPalette.Accent => AccentShades(dark),
            _ => dark
                ? [Color.Parse("#60A5FA"), Color.Parse("#A78BFA"), Color.Parse("#F472B6")]
                : [Color.Parse("#2563EB"), Color.Parse("#7C3AED"), Color.Parse("#DB2777")],
        };
        return [new GradientStop(c[0], 0), new GradientStop(c[1], 0.5), new GradientStop(c[2], 1)];
    }

    private Color[] AccentShades(bool dark)
    {
        var accent = this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var v) && v is Color c
            ? c
            : Color.Parse("#E0703A");
        var hsl = accent.ToHsl();
        Color Shade(double dh, double dl) =>
            HslColor.ToRgb((hsl.H + dh + 360) % 360, hsl.S, Math.Clamp(hsl.L + (dark ? dl : -dl), 0.2, 0.85));
        return [Shade(-25, 0.05), accent, Shade(25, 0.12)];
    }

    // Brightest a little ahead of the middle; fades out at both ends.
    private static double BeamShape(double u) => Math.Sin(Math.PI * Math.Pow(u, 0.7));

    private static double Frac(double x) => x - Math.Floor(x);

    // 0 → 1 → 0, so colors wrap around a closed border without a seam.
    private static double Mirror(double f) => 1 - Math.Abs(2 * Frac(f) - 1);

    private static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(c.A * alpha, 0, 255), c.R, c.G, c.B);

    private static Color Sample(GradientStops stops, double u)
    {
        GradientStop? before = null, after = null;
        foreach (var s in stops)
        {
            if (s.Offset <= u && (before is null || s.Offset >= before.Offset)) before = s;
            if (s.Offset >= u && (after is null || s.Offset <= after.Offset)) after = s;
        }
        if (before is null) return after!.Color;
        if (after is null || after.Offset <= before.Offset) return before.Color;
        var k = (u - before.Offset) / (after.Offset - before.Offset);
        byte Lerp(byte a, byte b) => (byte)Math.Round(a + (b - a) * k);
        return Color.FromArgb(Lerp(before.Color.A, after.Color.A), Lerp(before.Color.R, after.Color.R),
            Lerp(before.Color.G, after.Color.G), Lerp(before.Color.B, after.Color.B));
    }

    private static Geometry RoundedGeometry(Rect r, CornerRadius radius)
    {
        var max = Math.Min(r.Width, r.Height) / 2;
        double tl = Math.Min(radius.TopLeft, max), tr = Math.Min(radius.TopRight, max),
            br = Math.Min(radius.BottomRight, max), bl = Math.Min(radius.BottomLeft, max);
        var g = new StreamGeometry();
        using var ctx = g.Open();
        void Corner(double rad, Point to)
        {
            if (rad > 0) ctx.ArcTo(to, new Size(rad, rad), 0, false, SweepDirection.Clockwise);
            else ctx.LineTo(to);
        }
        ctx.BeginFigure(new Point(r.X + tl, r.Y), true);
        ctx.LineTo(new Point(r.Right - tr, r.Y));
        Corner(tr, new Point(r.Right, r.Y + tr));
        ctx.LineTo(new Point(r.Right, r.Bottom - br));
        Corner(br, new Point(r.Right - br, r.Bottom));
        ctx.LineTo(new Point(r.X + bl, r.Bottom));
        Corner(bl, new Point(r.X, r.Bottom - bl));
        ctx.LineTo(new Point(r.X, r.Y + tl));
        Corner(tl, new Point(r.X + tl, r.Y));
        ctx.EndFigure(true);
        return g;
    }

    private sealed class Layer(GlowBorder owner, bool glow) : Control
    {
        public override void Render(DrawingContext context) => owner.Render(context, glow);
    }

    /// <summary>
    /// A rounded rectangle's outline as a path walked clockwise from the end of the
    /// top-left corner, so a distance along it gives a point.
    /// </summary>
    private readonly struct Perimeter
    {
        private readonly Rect _r;
        private readonly double _tl, _tr, _br, _bl;
        private readonly double[] _lengths;

        public double Length { get; }

        public Perimeter(Rect r, CornerRadius radius, double inset)
        {
            _r = r;
            var max = Math.Min(r.Width, r.Height) / 2;
            double Fit(double v) => Math.Clamp(v - inset, 0, max);
            (_tl, _tr, _br, _bl) = (Fit(radius.TopLeft), Fit(radius.TopRight), Fit(radius.BottomRight), Fit(radius.BottomLeft));
            const double q = Math.PI / 2;
            _lengths =
            [
                Math.Max(0, r.Width - _tl - _tr), q * _tr,
                Math.Max(0, r.Height - _tr - _br), q * _br,
                Math.Max(0, r.Width - _br - _bl), q * _bl,
                Math.Max(0, r.Height - _bl - _tl), q * _tl,
            ];
            Length = _lengths.Sum();
        }

        public Point PointAt(double s)
        {
            s -= Math.Floor(s / Length) * Length;
            var i = 0;
            while (i < 7 && s > _lengths[i]) s -= _lengths[i++];
            var f = _lengths[i] > 0 ? s / _lengths[i] : 0;
            var r = _r;
            return i switch
            {
                0 => new Point(r.X + _tl + f * _lengths[0], r.Y),
                1 => Arc(new Point(r.Right - _tr, r.Y + _tr), _tr, -90 + 90 * f),
                2 => new Point(r.Right, r.Y + _tr + f * _lengths[2]),
                3 => Arc(new Point(r.Right - _br, r.Bottom - _br), _br, 90 * f),
                4 => new Point(r.Right - _br - f * _lengths[4], r.Bottom),
                5 => Arc(new Point(r.X + _bl, r.Bottom - _bl), _bl, 90 + 90 * f),
                6 => new Point(r.X, r.Bottom - _bl - f * _lengths[6]),
                _ => Arc(new Point(r.X + _tl, r.Y + _tl), _tl, 180 + 90 * f),
            };
        }

        private static Point Arc(Point center, double radius, double degrees)
        {
            var a = degrees * Math.PI / 180;
            return center + new Vector(Math.Cos(a), Math.Sin(a)) * radius;
        }
    }
}
