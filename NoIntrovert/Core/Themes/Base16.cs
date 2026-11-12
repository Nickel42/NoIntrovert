using System.Text.RegularExpressions;
using Avalonia.Media;
using static NoIntrovert.Core.Themes.ThemeRoles;

namespace NoIntrovert.Core.Themes;

/// <summary>
/// Base16 and Base24 colour schemes (https://github.com/tinted-theming/schemes): sixteen colours with
/// set meanings (base00 the background, base05 the text, base0D blue…), in a small YAML file. Both the
/// current layout (system/name/variant, then a palette: block) and the older one (scheme: and the colours
/// at the top level, without '#') are read. Base24's extra colours are ignored.
/// </summary>
public static partial class Base16
{
    [GeneratedRegex(@"^\s*(?<key>[A-Za-z0-9_\-]+)\s*:\s*(?<value>.*)$")]
    private static partial Regex Line();

    /// <summary>Whether <paramref name="text"/> looks like a Base16/Base24 scheme.</summary>
    public static bool Looks(string text) => Regex.IsMatch(text, @"^\s*base00\s*:", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static ThemeDefinition Parse(string text, string id, string? fallbackName = null, string? path = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var m = Line().Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            var value = Unquote(m.Groups["value"].Value.Trim());
            if (value.Length > 0) values.TryAdd(m.Groups["key"].Value, value);
        }

        Color Base(int n)
        {
            var key = $"base{n:X2}";
            if (!values.TryGetValue(key, out var v)) throw new FormatException($"The scheme has no {key}.");
            if (!v.StartsWith('#')) v = "#" + v;
            return Color.TryParse(v, out var c) ? c : throw new FormatException($"{key} isn't a colour: {v}");
        }

        var b = Enumerable.Range(0, 16).Select(Base).ToArray();
        var kind = values.GetValueOrDefault("variant") is { } variant
            ? variant.Equals("light", StringComparison.OrdinalIgnoreCase) ? ThemeKind.Light : ThemeKind.Dark
            : Luminance(b[0]) < 0.4 ? ThemeKind.Dark : ThemeKind.Light;
        var dark = kind == ThemeKind.Dark;

        // base00 is the default background and base01 the next shade (lighter on a dark scheme, darker on a light one):
        // NoIntrovert's cards stand out from the window, lighter on dark and white-ish on light.
        var roles = new Dictionary<ThemeRole, Color>
        {
            [ThemeRole.Background] = dark ? b[0x00] : b[0x01],
            [ThemeRole.Surface] = dark ? b[0x01] : b[0x00],
            [ThemeRole.Raised] = dark ? Mix(b[0x01], b[0x02], 0.35) : Mix(b[0x00], b[0x01], 0.5),
            [ThemeRole.Header] = dark ? Mix(b[0x00], b[0x01], 0.5) : b[0x00],
            [ThemeRole.Border] = b[0x02],
            [ThemeRole.ControlBorder] = b[0x03],
            [ThemeRole.Text] = b[0x05],
            [ThemeRole.SubtleText] = b[0x04],
            [ThemeRole.SectionText] = Mix(b[0x05], b[0x0D], 0.55),
            [ThemeRole.Primary] = b[0x0D],
            [ThemeRole.Accent] = b[0x09],
            [ThemeRole.Danger] = b[0x08],
            [ThemeRole.Warning] = b[0x0A],
            [ThemeRole.Success] = b[0x0B],
        };
        var name = values.GetValueOrDefault("name") ?? values.GetValueOrDefault("scheme") ?? fallbackName ?? "Base16 scheme";
        return new ThemeDefinition
        {
            Id = id, Name = name, Kind = kind, Author = values.GetValueOrDefault("author") ?? "", FilePath = path,
            Roles = Complete(kind, roles),
        };
    }

    /// <summary>A YAML scalar without its quotes or trailing comment.</summary>
    private static string Unquote(string v)
    {
        if (v.Length > 0 && (v[0] == '"' || v[0] == '\''))
        {
            var end = v.IndexOf(v[0], 1);
            return end > 0 ? v[1..end] : v[1..];
        }
        // Unquoted: a " #" starts a comment (a bare "#1d2021" at the start is taken as the colour, leniently).
        var hash = v.IndexOf(" #", StringComparison.Ordinal);
        return (hash >= 0 ? v[..hash] : v).Trim();
    }
}
