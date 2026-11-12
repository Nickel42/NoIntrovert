using Avalonia.Media;

namespace NoIntrovert.Core.Themes;

/// <summary>Whether a theme is one of the light or the dark ones (Settings › General › Light theme / Dark theme).</summary>
public enum ThemeKind { Dark, Light }

/// <summary>
/// The colours a theme chooses. Every other colour the app uses (row tints, tags, SukiUI's control
/// colours…) is worked out from these in <see cref="ThemeResources"/>, unless the theme file sets it too.
/// In a theme file each one is the <c>Color</c> keyed "Theme" + its name (ThemeBackground, ThemePrimary…).
/// </summary>
public enum ThemeRole
{
    Background,
    Surface,
    Raised,
    Header,
    Border,
    ControlBorder,
    Text,
    SubtleText,
    SectionText,
    Primary,
    Accent,
    Danger,
    Warning,
    Success,
}

/// <summary>One light or dark theme: a built-in one, or one in the themes folder.</summary>
public sealed class ThemeDefinition
{
    /// <summary>A built-in theme's name ("ember"), or "user:" and its file's name for one in the themes folder.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }
    public required ThemeKind Kind { get; init; }
    public string Author { get; init; } = "";
    public bool IsBuiltIn { get; init; }

    /// <summary>The file in the themes folder (null for a built-in theme, or one not saved yet).</summary>
    public string? FilePath { get; init; }

    /// <summary>Every role's colour (missing ones were filled in by <see cref="ThemeRoles.Complete"/>).</summary>
    public required IReadOnlyDictionary<ThemeRole, Color> Roles { get; init; }

    /// <summary>The app's own resource keys the file sets directly, which win over the worked-out colours.</summary>
    public IReadOnlyDictionary<string, ThemeOverride> Overrides { get; init; } = new Dictionary<string, ThemeOverride>();

    public Color this[ThemeRole role] => Roles[role];

    public override string ToString() => Name;
}

/// <summary>A resource key a theme file sets directly: its colour, and whether the file wrote it as a brush.</summary>
public readonly record struct ThemeOverride(Color Color, bool IsBrush);

/// <summary>The roles' names, labels and defaults, and the colour arithmetic the themes use.</summary>
public static class ThemeRoles
{
    public static readonly ThemeRole[] All = Enum.GetValues<ThemeRole>();

    /// <summary>The resource key a role has in a theme file.</summary>
    public static string Key(ThemeRole role) => "Theme" + role;

    public static string Label(ThemeRole role) => role switch
    {
        ThemeRole.Background => "Window background",
        ThemeRole.Surface => "Cards and panels",
        ThemeRole.Raised => "Raised panels",
        ThemeRole.Header => "Title bar and rail",
        ThemeRole.Border => "Borders",
        ThemeRole.ControlBorder => "Button and field borders",
        ThemeRole.Text => "Text",
        ThemeRole.SubtleText => "Secondary text",
        ThemeRole.SectionText => "Section headings",
        ThemeRole.Primary => "Primary (buttons, selection)",
        ThemeRole.Accent => "Accent (focus, highlights)",
        ThemeRole.Danger => "Danger",
        ThemeRole.Warning => "Warning",
        ThemeRole.Success => "Success",
        _ => role.ToString(),
    };

    /// <summary>
    /// Fills in the roles a theme leaves out: from the ones it has where that makes sense (a border a
    /// little away from the surface), otherwise from the default theme of its kind.
    /// </summary>
    public static Dictionary<ThemeRole, Color> Complete(ThemeKind kind, IReadOnlyDictionary<ThemeRole, Color> given)
    {
        var dark = kind == ThemeKind.Dark;
        var r = new Dictionary<ThemeRole, Color>(given);
        Color Get(ThemeRole role) => r[role];
        void Fill(ThemeRole role, Func<Color> make) { if (!r.ContainsKey(role)) r[role] = make(); }

        Fill(ThemeRole.Background, () => Color.Parse(dark ? "#191923" : "#F5F2F5"));
        Fill(ThemeRole.Text, () => Color.Parse(dark ? "#ECEAF2" : "#2B2B3D"));
        Fill(ThemeRole.Primary, () => Color.Parse(dark ? "#7C7EA6" : "#616283"));
        Fill(ThemeRole.Surface, () => dark ? Mix(Get(ThemeRole.Background), Get(ThemeRole.Text), 0.06) : Colors.White);
        Fill(ThemeRole.Raised, () => Mix(Get(ThemeRole.Surface), dark ? Get(ThemeRole.Text) : Get(ThemeRole.Background), dark ? 0.04 : 0.4));
        Fill(ThemeRole.Header, () => dark ? Mix(Get(ThemeRole.Background), Get(ThemeRole.Surface), 0.4) : Get(ThemeRole.Surface));
        Fill(ThemeRole.Border, () => Mix(Get(ThemeRole.Surface), Get(ThemeRole.Text), dark ? 0.12 : 0.1));
        Fill(ThemeRole.ControlBorder, () => Mix(Get(ThemeRole.Surface), Get(ThemeRole.Text), dark ? 0.25 : 0.2));
        Fill(ThemeRole.SubtleText, () => Mix(Get(ThemeRole.Text), Get(ThemeRole.Surface), 0.35));
        Fill(ThemeRole.SectionText, () => Mix(Get(ThemeRole.Text), Get(ThemeRole.Primary), 0.5));
        Fill(ThemeRole.Accent, () => Color.Parse("#FF8C42"));
        Fill(ThemeRole.Danger, () => Color.Parse("#FF3C38"));
        Fill(ThemeRole.Warning, () => Color.Parse("#FF8C42"));
        Fill(ThemeRole.Success, () => Color.Parse(dark ? "#FFF275" : "#E8D84A"));
        return r;
    }

    /// <summary>Between <paramref name="a"/> (0) and <paramref name="b"/> (1), channel by channel; opaque.</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        static byte L(byte x, byte y, double t) => (byte)Math.Clamp(Math.Round(x + (y - x) * t), 0, 255);
        return Color.FromRgb(L(a.R, b.R, t), L(a.G, b.G, t), L(a.B, b.B, t));
    }

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Clamp(Math.Round(alpha * 255), 0, 255), c.R, c.G, c.B);

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>White or the theme's dark text, whichever reads better on <paramref name="fill"/>; white wins a near tie, as the design has it.</summary>
    public static Color TextOn(Color fill, Color darkText)
    {
        var dark = Luminance(darkText) < 0.2 ? darkText : Color.Parse("#1B1B24");
        return Contrast(fill, Colors.White) * 1.35 >= Contrast(fill, dark) ? Colors.White : dark;
    }

    /// <summary>"#RRGGBB" (or "#AARRGGBB" when not opaque).</summary>
    public static string Hex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
