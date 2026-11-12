using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Media;

namespace NoIntrovert.Core.Themes;

/// <summary>
/// Reads and writes theme files. A NoIntrovert theme is an Avalonia <c>ResourceDictionary</c> (.axaml):
/// <c>x:String</c> ThemeName / ThemeVariant / ThemeAuthor, a <c>Color</c> per role (ThemeBackground…), and
/// optionally any of the app's own keys (<c>&lt;SolidColorBrush x:Key="PanelTile" Color="…"/&gt;</c>) to set one colour
/// exactly. Any Avalonia app can merge the file as it is. A dictionary with Dark and Light
/// <c>ThemeDictionaries</c> gives one theme of each.
///
/// Base16 and Base24 colour schemes (tinted-theming's YAML, hundreds of them online) are read too, and
/// turned into a theme: see <see cref="Base16"/>.
///
/// The XML is only read, never run: no types are created from it, so a downloaded file can't do anything
/// but set colours.
/// </summary>
public static class ThemeFile
{
    public const string AvaloniaNs = "https://github.com/avaloniaui";
    public const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The largest theme file read (a real one is a few kilobytes).</summary>
    public const int MaxBytes = 1 << 20;

    /// <summary>Reads every theme in <paramref name="text"/>: a ResourceDictionary, or a Base16/Base24 scheme.</summary>
    /// <exception cref="FormatException">It's neither, or has no colours.</exception>
    public static IReadOnlyList<ThemeDefinition> Parse(string text, string id, string? fallbackName = null, bool builtIn = false, string? path = null)
    {
        if (text.Length > MaxBytes) throw new FormatException("The file is too big to be a theme.");
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('<')) return ParseXaml(trimmed, id, fallbackName, builtIn, path);
        if (Base16.Looks(trimmed)) return [Base16.Parse(trimmed, id, fallbackName, path)];
        throw new FormatException("This isn't a theme file: expected an Avalonia ResourceDictionary (.axaml) or a Base16/Base24 scheme (.yaml).");
    }

    private static IReadOnlyList<ThemeDefinition> ParseXaml(string text, string id, string? fallbackName, bool builtIn, string? path)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("The theme file isn't valid XML: " + ex.Message, ex);
        }
        var root = doc.Root!;
        if (root.Name.LocalName != "ResourceDictionary") throw new FormatException("A theme file's root has to be a ResourceDictionary.");

        var top = ReadEntries(root);
        var name = top.Strings.GetValueOrDefault("ThemeName") ?? fallbackName ?? "Imported theme";
        var author = top.Strings.GetValueOrDefault("ThemeAuthor") ?? "";

        // <ResourceDictionary.ThemeDictionaries> with Dark and Light: one theme each.
        var variants = root.Elements().Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .SelectMany(e => e.Elements().Where(d => d.Name.LocalName == "ResourceDictionary"))
            .Select(d => (Key: (string?)d.Attribute(XName.Get("Key", XamlNs)), Dict: d))
            .Where(v => v.Key is "Dark" or "Light")
            .ToList();
        if (variants.Count > 0)
        {
            var themes = new List<ThemeDefinition>();
            foreach (var (key, dict) in variants)
            {
                var entries = ReadEntries(dict);
                entries.Merge(top);
                var kind = key == "Dark" ? ThemeKind.Dark : ThemeKind.Light;
                themes.Add(Make(entries, $"{id}#{key!.ToLowerInvariant()}", $"{name} {key}", kind, author, builtIn, path));
            }
            return themes;
        }

        if (top.Colors.Count == 0) throw new FormatException("The theme file sets no colours.");
        ThemeKind k;
        if (top.Strings.GetValueOrDefault("ThemeVariant") is { } variant)
            k = variant.Equals("Light", StringComparison.OrdinalIgnoreCase) ? ThemeKind.Light : ThemeKind.Dark;
        else
            k = GuessKind(top.Colors);
        return [Make(top, id, name, k, author, builtIn, path)];
    }

    private static ThemeKind GuessKind(Dictionary<string, ThemeOverride> colors)
    {
        foreach (var key in new[] { ThemeRoles.Key(ThemeRole.Background), "AppBackground", ThemeRoles.Key(ThemeRole.Surface), "PanelBackground" })
            if (colors.TryGetValue(key, out var c)) return ThemeRoles.Luminance(c.Color) < 0.4 ? ThemeKind.Dark : ThemeKind.Light;
        return ThemeKind.Dark;
    }

    private static ThemeDefinition Make(Entries e, string id, string name, ThemeKind kind, string author, bool builtIn, string? path)
    {
        var roles = new Dictionary<ThemeRole, Color>();
        var overrides = new Dictionary<string, ThemeOverride>(StringComparer.Ordinal);
        foreach (var (key, value) in e.Colors)
        {
            if (key.StartsWith("Theme", StringComparison.Ordinal) && Enum.TryParse<ThemeRole>(key[5..], out var role) && ThemeRoles.Key(role) == key)
                roles[role] = value.Color;
            else overrides[key] = value;
        }
        // A file written for the app's own keys alone (a copy of App.axaml's dictionaries) still gives its roles.
        void From(ThemeRole role, string key) { if (!roles.ContainsKey(role) && overrides.TryGetValue(key, out var o)) roles[role] = o.Color; }
        From(ThemeRole.Background, "AppBackground");
        From(ThemeRole.Surface, "PanelBackground");
        From(ThemeRole.Raised, "PanelRaised");
        From(ThemeRole.Header, "HeaderBackground");
        From(ThemeRole.Border, "PanelBorder");
        From(ThemeRole.ControlBorder, "ControlBorder");
        From(ThemeRole.Text, "SukiText");
        From(ThemeRole.SubtleText, "SubtleText");
        From(ThemeRole.SectionText, "SectionText");
        From(ThemeRole.Primary, "SukiPrimaryColor");
        From(ThemeRole.Accent, "SystemAccentColor");
        From(ThemeRole.Danger, "SukiDangerColor");
        From(ThemeRole.Warning, "SukiWarningColor");
        From(ThemeRole.Success, "SukiSuccessColor");
        return new ThemeDefinition
        {
            Id = id, Name = name.Trim(), Kind = kind, Author = author, IsBuiltIn = builtIn, FilePath = path,
            Roles = ThemeRoles.Complete(kind, roles), Overrides = overrides,
        };
    }

    private sealed class Entries
    {
        public Dictionary<string, string> Strings { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ThemeOverride> Colors { get; } = new(StringComparer.Ordinal);

        /// <summary>Takes the keys of <paramref name="outer"/> this one doesn't set (a variant dictionary wins over the shared one).</summary>
        public void Merge(Entries outer)
        {
            foreach (var (k, v) in outer.Strings) Strings.TryAdd(k, v);
            foreach (var (k, v) in outer.Colors) Colors.TryAdd(k, v);
        }
    }

    private static Entries ReadEntries(XElement dict)
    {
        var e = new Entries();
        foreach (var el in dict.Elements())
        {
            if (el.Attribute(XName.Get("Key", XamlNs))?.Value is not { Length: > 0 } key) continue;
            switch (el.Name.LocalName)
            {
                case "String":
                    e.Strings[key] = el.Value;
                    break;
                case "Color":
                    if (Color.TryParse(el.Value.Trim(), out var c)) e.Colors[key] = new(c, false);
                    break;
                case "SolidColorBrush":
                    var value = (string?)el.Attribute("Color") ?? el.Value;
                    if (Color.TryParse(value.Trim(), out var b)) e.Colors[key] = new(b, true);
                    break;
            }
        }
        return e;
    }

    /// <summary>The .axaml for <paramref name="theme"/>: its roles, and the keys it sets directly.</summary>
    public static string Write(ThemeDefinition theme)
    {
        XNamespace av = AvaloniaNs;
        XNamespace x = XamlNs;
        var root = new XElement(av + "ResourceDictionary",
            new XAttribute("xmlns", AvaloniaNs),
            new XAttribute(XNamespace.Xmlns + "x", XamlNs));
        root.Add(new XComment(" A NoIntrovert theme: an Avalonia ResourceDictionary. Each ThemeXxx colour is one of the theme's roles;\n"
            + "       the app works out the rest from them. Any other key sets one of the app's own colours exactly. "));
        root.Add(new XElement(x + "String", new XAttribute(x + "Key", "ThemeName"), theme.Name));
        root.Add(new XElement(x + "String", new XAttribute(x + "Key", "ThemeVariant"), theme.Kind.ToString()));
        if (theme.Author.Length > 0) root.Add(new XElement(x + "String", new XAttribute(x + "Key", "ThemeAuthor"), theme.Author));
        foreach (var role in ThemeRoles.All)
            root.Add(new XElement(av + "Color", new XAttribute(x + "Key", ThemeRoles.Key(role)), ThemeRoles.Hex(theme[role])));
        foreach (var (key, o) in theme.Overrides.OrderBy(o => o.Key, StringComparer.Ordinal))
            root.Add(o.IsBrush
                ? new XElement(av + "SolidColorBrush", new XAttribute(x + "Key", key), new XAttribute("Color", ThemeRoles.Hex(o.Color)))
                : new XElement(av + "Color", new XAttribute(x + "Key", key), ThemeRoles.Hex(o.Color)));
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "    ", OmitXmlDeclaration = true, NewLineOnAttributes = false }))
            root.WriteTo(w);
        return sb.ToString() + Environment.NewLine;
    }

    /// <summary>A file name from a theme's name: "My Theme!" → "my-theme".</summary>
    public static string Slug(string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "theme" : slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }
}
