using Avalonia;
using Avalonia.Controls;

namespace NoIntrovert.Views;

/// <summary>
/// Width breakpoints for a view: <c>v:Responsive.Below="compact:1000,narrow:760"</c>
/// gives the control the class <c>compact</c> while it's narrower than 1000 px
/// and <c>narrow</c> while it's narrower than 760 px, so its styles can hide
/// columns, narrow side panels or shorten the rail. The classes follow the
/// control's own width (set by its parent), so changing them never feeds back
/// into the width that chose them.
/// </summary>
public static class Responsive
{
    public static readonly AttachedProperty<string?> BelowProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Below", typeof(Responsive));

    public static string? GetBelow(Control control) => control.GetValue(BelowProperty);
    public static void SetBelow(Control control, string? value) => control.SetValue(BelowProperty, value);

    static Responsive()
    {
        BelowProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.OldValue is null) control.SizeChanged += (_, _) => Apply(control);
            Apply(control);
        });
    }

    /// <summary>The breakpoints in a <see cref="BelowProperty"/> value, e.g. "compact:1000,narrow:760".</summary>
    public static IReadOnlyList<(string Class, double Width)> Parse(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(':', StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && double.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(p => (p[0], double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

    /// <summary>Sets each breakpoint's class from the control's current width (none before its first layout).</summary>
    public static void Apply(Control control)
    {
        var width = control.Bounds.Width;
        foreach (var (name, below) in Parse(GetBelow(control)))
            control.Classes.Set(name, width > 0 && width < below);
    }
}
