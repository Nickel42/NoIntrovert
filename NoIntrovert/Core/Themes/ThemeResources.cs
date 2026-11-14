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
        "DangerText", "PrimaryForeground", "AccentBrush", "AccentForeground", "SelectionFill", "SelectionHoverFill", "FocusedRowFill",
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
        var controlBorder = R(ThemeRole.ControlBorder);
        var text = R(ThemeRole.Text);
        var subtle = R(ThemeRole.SubtleText);
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
            // Text and icons on the accent (the selected page in the rail): dark or light, whichever reads.
            ["AccentForeground"] = TextOn(accent, text),
            ["SelectionFill"] = WithAlpha(primary, 0.18),
            ["SelectionHoverFill"] = WithAlpha(primary, 0.24),
            ["FocusedRowFill"] = WithAlpha(primary, 0.2),
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
            ["SettingsFieldBorder"] = border,
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
            ["GlassBorderBrush"] = border,
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
        return c;
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
