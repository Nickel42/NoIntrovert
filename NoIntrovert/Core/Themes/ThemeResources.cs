using Avalonia.Media;
using static NoIntrovert.Core.Themes.ThemeRoles;

namespace NoIntrovert.Core.Themes;

/// <summary>
/// Every colour resource the app's pages and SukiUI's controls look up, worked out from a theme's roles.
/// App puts them in Application.Resources' Dark or Light theme dictionary, so a page's
/// <c>{DynamicResource PanelBackground}</c> follows whichever theme is chosen for what's showing.
/// </summary>
public static class ThemeResources
{
    /// <summary>Keys the pages use as brushes (Background="{DynamicResource …}"); every other key is a Color.</summary>
    public static readonly IReadOnlySet<string> BrushKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "AppBackground", "PanelBackground", "PanelRaised", "PanelBorder", "ControlBorder", "RowAlt", "SubtleText",
        "SectionText", "HeaderBackground", "LogBackground", "PrimaryTint", "WindowButtonBackground", "DangerTint",
        "DangerText", "PrimaryForeground", "AccentBrush", "AccentForeground", "AccentHover", "SelectionFill", "SelectionHoverFill", "FocusedRowFill",
        // Job and log status (dots, glyphs; the *Text ones are for words)
        "StatusOk", "StatusWarn", "StatusBad", "StatusBusy", "StatusMuted", "StatusOkText", "StatusBadText",
        // The Convert page's side panel
        "PanelHeadText", "PanelGroupText", "PanelTile", "PanelTileBorder", "TagGeneration", "TagMedia", "TagText",
        "WarningFill", "WarningText", "OutlineButton",
        // The Settings page
        "SettingsFieldBackground", "SettingsFieldBorder", "SettingsFieldText", "SettingsLink", "SettingsSegmentHover",
        "SettingsAccent", "SettingsWarning",
        // The Verified badge
        "VerifiedFill", "VerifiedFillHover", "VerifiedFillPressed", "VerifiedText",
    };

    /// <summary>The colour of every key (brush keys and Color keys alike), with the theme file's own keys applied last.</summary>
    public static Dictionary<string, Color> Colors(ThemeDefinition theme)
    {
        var dark = theme.Kind == ThemeKind.Dark;
        Color R(ThemeRole role) => theme[role];
        var bg = R(ThemeRole.Background);
        var surface = R(ThemeRole.Surface);
        var raised = R(ThemeRole.Raised);
        var border = R(ThemeRole.Border);
        // Whatever a theme (or an imported Base16 scheme) chose, text and secondary text read at 4.5:1 and a
        // checkbox's, radio's or field's outline at 3:1 (WCAG 1.4.3 and 1.4.11), on the cards and on the window.
        var text = Readable(R(ThemeRole.Text), 4.5, surface, bg);
        var controlBorder = Readable(R(ThemeRole.ControlBorder), 3, surface, bg);
        var subtle = Readable(R(ThemeRole.SubtleText), 4.5, surface, bg);
        var section = R(ThemeRole.SectionText);
        var primary = R(ThemeRole.Primary);
        var accent = R(ThemeRole.Accent);
        var danger = R(ThemeRole.Danger);
        var warning = R(ThemeRole.Warning);
        var success = R(ThemeRole.Success);
        var rowAlt = Mix(surface, raised, 0.5);
        var primaryTint = Mix(surface, primary, dark ? 0.25 : 0.15);
        var windowButton = Mix(raised, primary, dark ? 0.12 : 0.08);
        var field = Mix(surface, bg, 0.6);

        var c = new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["AppBackground"] = bg,
            ["PanelBackground"] = surface,
            ["PanelRaised"] = raised,
            ["PanelBorder"] = border,
            ["ControlBorder"] = controlBorder,
            ["RowAlt"] = rowAlt,
            ["SubtleText"] = subtle,
            ["SectionText"] = section,
            ["HeaderBackground"] = R(ThemeRole.Header),
            ["LogBackground"] = dark ? Mix(bg, surface, 0.35) : raised,
            ["PrimaryTint"] = primaryTint,
            ["WindowButtonBackground"] = windowButton,
            ["DangerTint"] = Mix(surface, danger, dark ? 0.2 : 0.18),
            ["DangerText"] = danger,
            ["PrimaryForeground"] = TextOn(primary, text),
            ["AccentBrush"] = accent,
            // Text on the accent (the run button): dark or light, whichever reads.
            ["AccentForeground"] = TextOn(accent, text),
            // The run button under the pointer: a step away from its text colour, so the text reads even better.
            ["AccentHover"] = Mix(accent, Luminance(TextOn(accent, text)) > 0.5 ? Avalonia.Media.Colors.Black : Avalonia.Media.Colors.White, 0.14),
            ["SelectionFill"] = WithAlpha(primary, 0.18),
            ["SelectionHoverFill"] = WithAlpha(primary, 0.24),
            ["SystemAccentColor"] = accent,

            ["PanelHeadText"] = section,
            ["PanelGroupText"] = Mix(subtle, surface, 0.15),
            ["PanelTile"] = Mix(surface, primary, dark ? 0.08 : 0.06),
            ["PanelTileBorder"] = Mix(surface, primary, dark ? 0.25 : 0.18),
            ["TagGeneration"] = Mix(surface, primary, dark ? 0.25 : 0.14),
            ["TagMedia"] = Mix(surface, Mix(primary, danger, 0.25), dark ? 0.4 : 0.16),
            ["TagText"] = dark ? Mix(text, primary, 0.1) : text,
            ["WarningFill"] = Mix(surface, warning, dark ? 0.12 : 0.1),
            ["WarningText"] = dark ? Mix(warning, Avalonia.Media.Colors.White, 0.6) : Mix(warning, Avalonia.Media.Colors.Black, 0.55),
            ["OutlineButton"] = dark ? Mix(primary, text, 0.25) : primary,

            ["SettingsFieldBackground"] = field,
            ["SettingsFieldBorder"] = controlBorder, // segmented controls, steppers, path fields: 3:1 like any control
            ["SettingsFieldText"] = Mix(text, subtle, 0.4),
            ["SettingsLink"] = section,
            ["SettingsSegmentHover"] = rowAlt,
            ["SettingsAccent"] = primary,
            ["SettingsWarning"] = accent,

            ["VerifiedFill"] = success,
            ["VerifiedFillHover"] = Mix(success, Avalonia.Media.Colors.White, 0.35),
            ["VerifiedFillPressed"] = Mix(success, Avalonia.Media.Colors.Black, 0.08),
            ["VerifiedText"] = TextOn(success, text),

            ["SukiStrongBackground"] = raised,
            ["SukiCardBackground"] = surface,
            ["SukiPopupBackground"] = surface,
            ["SukiGlassCardBackground"] = surface,
            ["SukiGlassCardOpaqueBackground"] = surface,
            ["ControlSukiGlassCardBackground"] = surface,
            ["SukiControlTouchBackground"] = windowButton,
            ["SukiBorderBrush"] = border,
            ["SukiControlBorderBrush"] = controlBorder,
            ["SukiMediumBorderBrush"] = controlBorder,
            ["SukiLightBorderBrush"] = border,
            ["SukiMenuBorderBrush"] = border,
            ["GlassBorderBrush"] = controlBorder, // SukiUI draws checkbox and radio outlines with it
            ["SukiLiquid1"] = border,
            ["SukiLiquid2"] = border,
            ["SukiText"] = text,
            ["SukiLowText"] = Mix(text, subtle, 0.2),
            ["SukiMuteText"] = subtle,
            ["SukiDisabledText"] = Mix(surface, subtle, 0.45),
            ["SukiDangerColor"] = danger,
            ["SukiWarningColor"] = warning,
            ["SukiSuccessColor"] = success,
        };
        foreach (var (key, o) in theme.Overrides) c[key] = o.Color;

        // Worked out after the file's own keys, from what's really showing (a file can still set them).
        var logBg = c["LogBackground"];
        if (!theme.Overrides.ContainsKey("FocusedRowFill")) c["FocusedRowFill"] = FocusedRowFill(primary, accent, surface, c["PrimaryTint"], c["SelectionFill"]);
        Color Status(string key, Color hue, double target)
        {
            if (!theme.Overrides.ContainsKey(key)) c[key] = Readable(hue, target, surface, bg, logBg);
            return c[key];
        }
        // A Base16 scheme's success is green; NoIntrovert's own is yellow (the Verified badge), so "done" keeps a green.
        var ok = IsGreen(success) ? success : Color.Parse(dark ? "#3DBE7E" : "#2EA66B");
        Status("StatusOk", ok, 3);
        Status("StatusWarn", warning, 3);
        Status("StatusBad", danger, 3);
        Status("StatusBusy", Color.Parse(dark ? "#4C9BF0" : "#2F7FD6"), 3);
        Status("StatusMuted", Mix(subtle, surface, 0.2), 3);
        Status("StatusOkText", ok, 4.5);
        Status("StatusBadText", danger, 4.5);
        return c;
    }

    /// <summary>
    /// The focused row's fill: the primary's tint, as strong as it can be (up to 20%) while the accent outline around
    /// it still reads at 3:1, on its own and on a row that's also selected (a list row takes PrimaryTint, then
    /// SelectionFill). Where the accent is under 3:1 without the fill anyway, the fill may take no more than a tenth
    /// of what's left; at worst there's no extra fill, only the outline and the bar.
    /// </summary>
    private static Color FocusedRowFill(Color primary, Color accent, Color surface, Color selectedRow, Color selection)
    {
        var selected = Over(Over(surface, selectedRow), selection);
        var goal = Goal(Contrast(accent, selected));
        for (var step = 10; step > 0; step--)
        {
            var fill = WithAlpha(primary, step * 0.02);
            if (Contrast(accent, Over(selected, fill)) >= goal && Contrast(accent, Over(surface, fill)) >= Goal(Contrast(accent, surface)))
                return fill;
        }
        return WithAlpha(primary, 0);
    }

    /// <summary>What the focused row's outline must keep against its fill, given what it has without one.</summary>
    private static double Goal(double withoutFill) => withoutFill >= 3 ? 3 : withoutFill * 0.9;

    private static bool IsGreen(Color c)
    {
        var hsl = c.ToHsl();
        return hsl.H is >= 75 and <= 170 && hsl.S > 0.25;
    }

    /// <summary>The resources themselves: a brush for <see cref="BrushKeys"/> (and keys a theme file wrote as brushes), a Color otherwise.</summary>
    public static Dictionary<string, object> Build(ThemeDefinition theme)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, color) in Colors(theme))
        {
            var brush = BrushKeys.Contains(key) || (theme.Overrides.TryGetValue(key, out var o) && o.IsBrush && !IsKnownColorKey(key));
            result[key] = brush ? new SolidColorBrush(color) : color;
        }
        return result;
    }

    private static bool IsKnownColorKey(string key) => key.StartsWith("Suki", StringComparison.Ordinal) || key is "SystemAccentColor"
        or "GlassBorderBrush" or "ControlSukiGlassCardBackground";
}
