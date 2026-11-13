using System.Net;
using Avalonia.Media;
using NoIntrovert.Core;
using NoIntrovert.Core.Themes;
using NoIntrovert.Services;
using NoIntrovert.ViewModels;

namespace NoIntrovert.Tests;

/// <summary>Themes: the built-in ones, theme files and Base16 schemes, the themes folder, and Settings' theme lists and editor.</summary>
static class ThemeTests
{
    private const string Gruvbox = """
        system: "base16"
        name: "Gruvbox dark, hard"
        author: "Dawid Kurek (dawikur@gmail.com), morhetz (https://github.com/morhetz/gruvbox)"
        variant: "dark"
        palette:
          base00: "#1d2021" # ----
          base01: "#3c3836" # ---
          base02: "#504945" # --
          base03: "#665c54" # -
          base04: "#bdae93" # +
          base05: "#d5c4a1" # ++
          base06: "#ebdbb2" # +++
          base07: "#fbf1c7" # ++++
          base08: "#fb4934" # red
          base09: "#fe8019" # orange
          base0A: "#fabd2f" # yellow
          base0B: "#b8bb26" # green
          base0C: "#8ec07c" # aqua/cyan
          base0D: "#83a598" # blue
          base0E: "#d3869b" # purple
          base0F: "#d65d0e" # brown
        """;

    // The older layout: scheme:, colours at the top level without '#', no variant.
    private const string SolarizedLightOld = """
        scheme: "Solarized Light"
        author: "Ethan Schoonover (modified by aramisgithub)"
        base00: "fdf6e3"
        base01: "eee8d5"
        base02: "93a1a1"
        base03: "839496"
        base04: "657b83"
        base05: "586e75"
        base06: "073642"
        base07: "002b36"
        base08: "dc322f"
        base09: "cb4b16"
        base0A: "b58900"
        base0B: "859900"
        base0C: "2aa198"
        base0D: "268bd2"
        base0E: "6c71c4"
        base0F: "d33682"
        """;

    public static void Run(Action<string, Func<string?>> test, Func<string, string> dir)
    {
        test("Themes: three dark and three light themes of NoIntrovert's own are built in, each kind's NoIntrovert one first", () =>
        {
            var all = ThemeLibrary.BuiltIn.Where(t => t.Collection == ThemeCollection.NoIntrovert).ToList();
            var dark = all.Where(t => t.Kind == ThemeKind.Dark).Select(t => t.Id).ToList();
            var light = all.Where(t => t.Kind == ThemeKind.Light).Select(t => t.Id).ToList();
            if (!dark.SequenceEqual(["nointrovert-dark", "ember", "cartridge"])) return "dark: " + string.Join(", ", dark);
            if (!light.SequenceEqual(["nointrovert-light", "sunrise", "seafoam"])) return "light: " + string.Join(", ", light);
            if (all.FirstOrDefault(t => !t.Name.StartsWith("NoIntrovert ", StringComparison.Ordinal)) is { } unnamed) return unnamed.Name + " isn't named NoIntrovert …";
            return all.All(t => t.IsBuiltIn && t.Author == "NoIntrovert") ? null : "a built-in theme isn't marked as one";
        });

        test("Themes: every NoIntrovert theme's text, buttons and focus colours are readable", () =>
        {
            var problems = new List<string>();
            foreach (var t in ThemeLibrary.BuiltIn.Where(t => t.Collection == ThemeCollection.NoIntrovert))
            {
                var c = ThemeResources.Colors(t);
                // The design's own light theme, kept exactly as it was, is a hair under two of these.
                var original = t.Id == ThemeLibrary.DefaultLightId;
                void Need(string what, Color a, Color b, double ratio)
                {
                    if (original) ratio = Math.Min(ratio, what switch { "secondary text on cards" => 4.3, "the accent outline against cards" => 2.3, _ => ratio });
                    var r = ThemeRoles.Contrast(a, b);
                    if (r < ratio) problems.Add($"{t.Name}: {what} {r:F1}:1 (wants {ratio}:1)");
                }
                Need("text on cards", t[ThemeRole.Text], t[ThemeRole.Surface], 7);
                Need("text on the window", t[ThemeRole.Text], t[ThemeRole.Background], 7);
                Need("secondary text on cards", t[ThemeRole.SubtleText], t[ThemeRole.Surface], 4.5);
                Need("section headings on cards", t[ThemeRole.SectionText], t[ThemeRole.Surface], 4.5);
                Need("a primary button's text", c["PrimaryForeground"], t[ThemeRole.Primary], 3);
                Need("the selected rail item's text", c["PrimaryForeground"], t[ThemeRole.Primary], 3);
                Need("the accent outline against cards", t[ThemeRole.Accent], t[ThemeRole.Surface], 2.5);
                Need("a warning's text", c["WarningText"], c["WarningFill"], 4.5);
            }
            return problems.Count == 0 ? null : string.Join("; ", problems);
        });

        test("Themes: the NoIntrovert themes keep the design's colours from before themes", () =>
        {
            var dark = ThemeResources.Colors(ThemeLibrary.BuiltIn.First(t => t.Id == ThemeLibrary.DefaultDarkId));
            var light = ThemeResources.Colors(ThemeLibrary.BuiltIn.First(t => t.Id == ThemeLibrary.DefaultLightId));
            var expected = new (Dictionary<string, Color> Theme, string Key, string Hex)[]
            {
                (dark, "AppBackground", "#191923"), (dark, "PanelBackground", "#262635"), (dark, "PrimaryTint", "#3B3C58"),
                (dark, "RowAlt", "#2B2B3B"), (dark, "SukiLowText", "#E2E1F0"), (dark, "SukiSuccessColor", "#FFF275"),
                (dark, "PanelTile", "#2E2E40"), (dark, "TagMedia", "#5A5272"), (dark, "SettingsFieldBackground", "#1E1E2A"),
                (dark, "SettingsAccent", "#7C7EA6"), (dark, "SystemAccentColor", "#FF8C42"), (dark, "SelectionFill", "#2E8F86D9"),
                (light, "AppBackground", "#F5F2F5"), (light, "DangerText", "#E02B27"), (light, "SukiDangerColor", "#FF3C38"),
                (light, "PanelHeadText", "#55568A"), (light, "SettingsAccent", "#7C7EA6"), (light, "SettingsLink", "#5A5C8A"),
                (light, "SukiDisabledText", "#BDB8C3"), (light, "HeaderBackground", "#FFFFFF"),
            };
            var wrong = expected.Where(e => ThemeRoles.Hex(e.Theme[e.Key]) != e.Hex)
                .Select(e => $"{e.Key} is {ThemeRoles.Hex(e.Theme[e.Key])}, not {e.Hex}").ToList();
            return wrong.Count == 0 ? null : string.Join("; ", wrong);
        });

        test("Themes: every resource key a page uses comes from every theme", () =>
        {
            var keys = new[] { "AppBackground", "PanelBackground", "HeaderBackground", "PrimaryForeground", "AccentBrush", "PanelTile",
                "TagMedia", "WarningFill", "SettingsFieldBackground", "SettingsAccent", "SukiText", "SukiControlTouchBackground", "SystemAccentColor" };
            foreach (var t in ThemeLibrary.BuiltIn)
            {
                var r = ThemeResources.Build(t);
                if (keys.FirstOrDefault(k => !r.ContainsKey(k)) is { } missing) return $"{t.Name} has no {missing}";
                if (r["AppBackground"] is not ISolidColorBrush) return "AppBackground isn't a brush";
                if (r["SukiText"] is not Color) return "SukiText isn't a Color";
            }
            return null;
        });

        test("Themes: a theme written as .axaml reads back the same", () =>
        {
            var ember = ThemeLibrary.BuiltIn.First(t => t.Id == "ember");
            var text = ThemeFile.Write(ember);
            if (!text.Contains("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\"") || !text.Contains("x:Key=\"ThemePrimary\">#E0612C<"))
                return "not a ResourceDictionary with the roles: " + text[..Math.Min(300, text.Length)];
            var back = ThemeFile.Parse(text, "x").Single();
            if (back.Name != "NoIntrovert Ember" || back.Kind != ThemeKind.Dark) return $"read back {back.Name} ({back.Kind})";
            var lilac = ThemeLibrary.BuiltIn.First(t => t.Id == ThemeLibrary.DefaultLightId);
            var lilacBack = ThemeFile.Parse(ThemeFile.Write(lilac), "y").Single();
            if (!ThemeResources.Colors(lilac).OrderBy(k => k.Key).SequenceEqual(ThemeResources.Colors(lilacBack).OrderBy(k => k.Key)))
                return "a theme that sets the app's own keys changed when written and read";
            return ThemeRoles.All.All(r => back[r] == ember[r]) ? null : "a role's colour changed";
        });

        test("Themes: a ResourceDictionary of the app's own keys, with Dark and Light dictionaries, gives a theme of each", () =>
        {
            const string xaml = """
                <ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <x:String x:Key="ThemeName">Twins</x:String>
                  <ResourceDictionary.ThemeDictionaries>
                    <ResourceDictionary x:Key="Dark">
                      <SolidColorBrush x:Key="AppBackground" Color="#101010" />
                      <SolidColorBrush x:Key="PanelBackground">#202020</SolidColorBrush>
                      <Color x:Key="SukiText">#F0F0F0</Color>
                    </ResourceDictionary>
                    <ResourceDictionary x:Key="Light">
                      <SolidColorBrush x:Key="AppBackground" Color="#F0F0F0" />
                      <Color x:Key="ThemePrimary">#225588</Color>
                    </ResourceDictionary>
                  </ResourceDictionary.ThemeDictionaries>
                </ResourceDictionary>
                """;
            var themes = ThemeFile.Parse(xaml, "user:twins");
            if (themes.Count != 2) return $"{themes.Count} themes";
            var (dark, light) = (themes.Single(t => t.Kind == ThemeKind.Dark), themes.Single(t => t.Kind == ThemeKind.Light));
            if (dark.Name != "Twins Dark" || light.Name != "Twins Light") return $"named {dark.Name} / {light.Name}";
            if (dark[ThemeRole.Background] != Color.Parse("#101010") || dark[ThemeRole.Surface] != Color.Parse("#202020") || dark[ThemeRole.Text] != Color.Parse("#F0F0F0"))
                return "the dark one's app keys didn't become its roles";
            return light[ThemeRole.Primary] == Color.Parse("#225588") ? null : "the light one's primary wasn't read";
        });

        test("Themes: a file that isn't a theme, or brings a DTD, is refused", () =>
        {
            string? Refused(string text)
            {
                try { ThemeFile.Parse(text, "x"); return "read: " + text[..Math.Min(40, text.Length)]; }
                catch (FormatException) { return null; }
            }
            return Refused("hello")
                ?? Refused("<Window xmlns=\"https://github.com/avaloniaui\" />")
                ?? Refused("<!DOCTYPE r [<!ENTITY e \"x\">]><ResourceDictionary><Color>&e;</Color></ResourceDictionary>")
                ?? Refused("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\" />")
                ?? Refused(new string(' ', ThemeFile.MaxBytes + 1));
        });

        test("Themes: a Base16 scheme becomes a theme (tinted-theming's current layout)", () =>
        {
            var t = ThemeFile.Parse(Gruvbox, "user:gruvbox").Single();
            if (t.Name != "Gruvbox Dark Hard" || t.Kind != ThemeKind.Dark || !t.Author.StartsWith("Dawid")) return $"{t.Name} ({t.Kind}) by {t.Author}";
            if (t[ThemeRole.Background] != Color.Parse("#1d2021") || t[ThemeRole.Surface] != Color.Parse("#3c3836")) return "base00/base01 aren't the window and the cards";
            if (t[ThemeRole.Text] != Color.Parse("#d5c4a1") || t[ThemeRole.Primary] != Color.Parse("#83a598") || t[ThemeRole.Accent] != Color.Parse("#fe8019"))
                return "text, primary or accent isn't base05/base0D/base09";
            return t[ThemeRole.Danger] == Color.Parse("#fb4934") ? null : "danger isn't base08";
        });

        test("Themes: an older Base16 scheme (scheme:, no '#', no variant) is a light theme when its background is light", () =>
        {
            var t = ThemeFile.Parse(SolarizedLightOld, "user:sol").Single();
            if (t.Name != "Solarized Light" || t.Kind != ThemeKind.Light) return $"{t.Name} ({t.Kind})";
            // On a light scheme base00 is the lightest: the cards, with the window a shade darker.
            return t[ThemeRole.Surface] == Color.Parse("#fdf6e3") && t[ThemeRole.Background] == Color.Parse("#eee8d5") ? null : "the backgrounds are the wrong way round";
        });

        test("Themes: GitHub file pages are read from raw.githubusercontent.com; only web addresses are taken", () =>
        {
            var raw = ThemeLibrary.RawUri("https://github.com/tinted-theming/schemes/blob/spec-0.11/base16/gruvbox-dark-hard.yaml");
            if (raw.ToString() != "https://raw.githubusercontent.com/tinted-theming/schemes/spec-0.11/base16/gruvbox-dark-hard.yaml") return raw.ToString();
            try { ThemeLibrary.RawUri("file:///C:/Windows/win.ini"); return "a file: address was taken"; }
            catch (FormatException) { return null; }
        });

        test("Themes: the themes folder keeps custom and imported themes, and lists a file it can't read", () =>
        {
            var folder = dir("themes-folder");
            var lib = new ThemeLibrary(folder);
            if (lib.Themes.Count != ThemeLibrary.BuiltIn.Count) return "a new folder has themes";
            var saved = lib.Save(ThemeLibrary.Copy(ThemeLibrary.BuiltIn.First(t => t.Id == "sunrise"), "Peach Fizz", null));
            if (saved.Id != "user:peach-fizz" || !File.Exists(Path.Combine(folder, "peach-fizz.axaml"))) return $"saved as {saved.Id}";
            var again = lib.Save(ThemeLibrary.Copy(saved, "Peach Fizz", null));
            if (again.Id != "user:peach-fizz-2") return "a second theme of the same name overwrote the first";
            var imported = lib.ImportText(Gruvbox, "gruvbox");
            if (imported.Single().Id != "user:gruvbox-dark-hard") return $"imported as {imported.Single().Id}";
            File.WriteAllText(Path.Combine(folder, "broken.axaml"), "<nope");
            lib.Reload();
            if (!lib.Problems.Any(p => p.StartsWith("broken.axaml"))) return "the broken file wasn't reported";
            if (lib.Find("user:gruvbox-dark-hard")?.Kind != ThemeKind.Dark) return "the import isn't in the list after a reload";
            lib.Delete(lib.Find("user:peach-fizz-2")!);
            if (lib.Find("user:peach-fizz-2") is not null || File.Exists(Path.Combine(folder, "peach-fizz-2.axaml"))) return "delete left the theme";
            lib.Delete(ThemeLibrary.BuiltIn[0]);
            lib.Delete(lib.Find("base16-nord")!);
            if (lib.Find("base16-nord") is null) return "a built-in Base16 scheme was deleted";
            return lib.Find(ThemeLibrary.BuiltIn[0].Id) is null ? "a built-in theme was deleted" : null;
        });

        test("Themes: a missing or wrong-kind theme setting falls back to the kind's NoIntrovert theme", () =>
        {
            var lib = new ThemeLibrary(dir("themes-resolve"));
            return lib.Resolve("user:gone", ThemeKind.Dark).Id == ThemeLibrary.DefaultDarkId
                && lib.Resolve("sunrise", ThemeKind.Dark).Id == ThemeLibrary.DefaultDarkId
                && lib.Resolve("cartridge", ThemeKind.Dark).Id == "cartridge"
                ? null : "didn't fall back";
        });

        test("Themes: every Base16 scheme from tinted-theming is built in, each with its own name and its author", () =>
        {
            var schemes = ThemeLibrary.BuiltIn.Where(t => t.Collection == ThemeCollection.Base16).ToList();
            if (schemes.Count < 370) return $"only {schemes.Count} Base16 schemes";
            if (schemes.Count(t => t.Kind == ThemeKind.Light) < 100) return "fewer than 100 light ones";
            if (schemes.Any(t => !t.IsBuiltIn || !t.Id.StartsWith(ThemeLibrary.Base16Prefix))) return "a scheme isn't a built-in base16- theme";
            var clash = schemes.GroupBy(t => (t.Kind, t.Name.ToLowerInvariant())).FirstOrDefault(g => g.Count() > 1);
            if (clash is not null) return $"two {clash.Key.Kind} themes called {clash.First().Name}";
            // A few schemes leave author empty in tinted-theming's own files (Seti UI); the rest credit theirs.
            if (schemes.Count(t => t.Author.Length == 0) > 5) return $"{schemes.Count(t => t.Author.Length == 0)} schemes have no author";
            if (schemes.FirstOrDefault(t => t.Name.Contains(',') || char.IsLower(t.Name[0]) && !t.Name.StartsWith("iA ")) is { } untidy) return $"untidy name: {untidy.Name}";
            var nord = schemes.FirstOrDefault(t => t.Id == "base16-nord");
            if (nord is not { Name: "Nord", Kind: ThemeKind.Dark }) return "Nord isn't there as a dark theme";
            return schemes.Any(t => t.Id == "base16-cyberpunk") ? null : "the .yml scheme (cyberpunk) is missing";
        });

        test("Themes: scheme names are tidied into theme names", () =>
        {
            var cases = new (string In, string Out)[]
            {
                ("Gruvbox dark, hard", "Gruvbox Dark Hard"), ("darkmoss", "Darkmoss"), ("selenized-white", "Selenized White"),
                ("boo-shnickle-light", "Boo Shnickle Light"), ("Arroz con Dulce", "Arroz con Dulce"), ("Shades of Purple", "Shades of Purple"),
                ("iA Dark", "iA Dark"), ("UwUnicorn", "UwUnicorn"), ("Black Metal (Khold)", "Black Metal (Khold)"), ("Humanoid dark", "Humanoid Dark"),
            };
            var wrong = cases.Where(c => Base16.TidyName(c.In) != c.Out).Select(c => $"{c.In} → {Base16.TidyName(c.In)}").ToList();
            return wrong.Count == 0 ? null : string.Join("; ", wrong);
        });

        test("Themes: the lists put NoIntrovert's themes first, then custom ones, then Base16 A to Z; search narrows both", () => WithThemes(dir("themes-lists"), null, (t, _) =>
        {
            t.Customize("Dark");
            t.Editor!.Name = "Zebra";
            t.Editor.SaveCommand.Execute(null);
            var order = t.DarkThemes.Select(o => o.Theme.Collection).ToList();
            if (order.Take(3).Any(c => c != ThemeCollection.NoIntrovert) || order[3] != ThemeCollection.Custom || order[4] != ThemeCollection.Base16)
                return "out of order: " + string.Join(", ", t.DarkThemes.Take(6).Select(o => o.Name));
            var base16 = t.DarkThemes.Where(o => o.Theme.Collection == ThemeCollection.Base16).Select(o => o.Name).ToList();
            if (!base16.SequenceEqual(base16.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))) return "the Base16 schemes aren't A to Z";
            if (!t.SearchHint.StartsWith("Search ")) return t.SearchHint;
            t.Search = "gruvbox material";
            if (t.DarkThemes.Count != 4 || t.DarkThemes.Skip(1).Any(o => !o.Name.StartsWith("Gruvbox Material Dark")))
                return "searching found " + string.Join(", ", t.DarkThemes.Select(o => o.Name));
            if (t.SelectedDark?.Name != "Zebra") return "the chosen theme left the list";
            t.SelectedDark = t.DarkThemes.First(o => o.Name == "Gruvbox Material Dark Hard");
            if (t.ActiveDark.Id != "base16-gruvbox-material-dark-hard") return "choosing a search result didn't use it";
            t.Search = "";
            return t.DarkThemes.Count > 250 ? null : "clearing the search didn't bring the list back";
        }));

        test("Themes: Import from web downloads a scheme and uses it for its kind", () => WithThemes(dir("themes-web"), new FakeWeb(Gruvbox), (t, file) =>
        {
            t.ImportUrl = "https://github.com/tinted-theming/schemes/blob/spec-0.11/base16/gruvbox-dark-hard.yaml";
            t.ImportFromUrlCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            if (t.SelectedDark?.Id != "user:gruvbox-dark-hard") return $"chose {t.SelectedDark?.Id}; {t.Status}";
            if (!t.Status.StartsWith("Imported Gruvbox Dark Hard (dark)")) return t.Status;
            return File.ReadAllText(file).Contains("\"DarkTheme\": \"user:gruvbox-dark-hard\"") ? null : "the choice wasn't saved";
        }));

        test("Themes: a download that isn't a theme says so and changes nothing", () => WithThemes(dir("themes-web-bad"), new FakeWeb("<html>Not found</html>"), (t, _) =>
        {
            t.ImportUrl = "https://example.com/theme.yaml";
            t.ImportFromUrlCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            return t.Status.StartsWith("Couldn't import it") && t.SelectedDark?.Id == ThemeLibrary.DefaultDarkId ? null : t.Status;
        }));

        test("Themes: choosing a theme saves it and tells the app", () => WithThemes(dir("themes-choose"), null, (t, file) =>
        {
            var changes = 0;
            t.Changed += () => changes++;
            t.SelectedLight = t.LightThemes.First(o => o.Id == "seafoam");
            if (changes != 1 || t.ActiveLight.Id != "seafoam") return "the app wasn't told";
            if (t.DarkThemes.Count(o => o.Theme.Collection == ThemeCollection.NoIntrovert) != 3) return "the dark list hasn't NoIntrovert's three";
            return File.ReadAllText(file).Contains("\"LightTheme\": \"seafoam\"") ? null : "not saved";
        }));

        test("Themes: Light brightness dims the light theme (every colour, in step) and is saved; dark themes stay as made", () => WithThemes(dir("themes-brightness"), null, (t, file) =>
        {
            var made = t.Library.Find(ThemeLibrary.DefaultLightId)!;
            if (t.LightBrightness != 100 || t.ActiveLight[ThemeRole.Surface] != made[ThemeRole.Surface]) return "not at 100% to start";
            var changes = 0;
            t.Changed += () => changes++;
            t.LightBrightness = 85;
            var shown = t.ActiveLight;
            if (changes != 1 || t.LightBrightnessText != "85%") return "the app wasn't told";
            if (shown[ThemeRole.Surface] != Color.FromRgb(217, 217, 217)) return "white became " + shown[ThemeRole.Surface];
            // The theme file's own shades dim as well, or they'd be bright patches on the grey.
            if (ThemeResources.Colors(shown)["RowAlt"] != Color.FromRgb(210, 207, 210)) return "RowAlt is " + ThemeResources.Colors(shown)["RowAlt"];
            if (ThemeContrast(shown) < 4.5) return "the text got hard to read";
            if (t.ActiveDark[ThemeRole.Background] != t.Library.Find(ThemeLibrary.DefaultDarkId)![ThemeRole.Background]) return "the dark theme changed";
            if (!File.ReadAllText(file).Contains("\"LightBrightness\": 85")) return "not saved";
            t.LightBrightness = 40;
            return t.LightBrightness == ThemesViewModel.MinLightBrightness ? null : "went below the minimum: " + t.LightBrightness;
        }));

        test("Themes: a copy of a built-in NoIntrovert theme is \"My Ember\", not \"My NoIntrovert Ember\"", () => WithThemes(dir("themes-copyname"), null, (t, _) =>
        {
            t.SelectedDark = t.DarkThemes.First(o => o.Id == "ember");
            t.Customize("Dark");
            var ember = t.Editor!.Name;
            t.Editor.CancelCommand.Execute(null);
            t.SelectedDark = t.DarkThemes.First(o => o.Id == ThemeLibrary.DefaultDarkId);
            t.Customize("Dark");
            var dark = t.Editor!.Name;
            t.Editor.CancelCommand.Execute(null);
            return (ember, dark) == ("My Ember", "My NoIntrovert Dark") ? null : $"{ember}; {dark}";
        }));

        test("Themes: customizing a built-in theme previews it, then saves a named copy and uses it", () => WithThemes(dir("themes-edit"), null, (t, file) =>
        {
            t.SelectedDark = t.DarkThemes.First(o => o.Id == "cartridge");
            t.Customize("Dark");
            var e = t.Editor!;
            if (e.Name != "My Cartridge" || e.EditsOwn || t.PreviewKind != ThemeKind.Dark) return "the editor didn't open on a copy";
            var changes = 0;
            t.Changed += () => changes++;
            e.Rows.First(r => r.Role == ThemeRole.Primary).Hex = "7a3cff";
            if (changes != 1 || t.ActiveDark[ThemeRole.Primary] != Color.Parse("#7A3CFF")) return "the change wasn't previewed";
            if (t.Library.Find("cartridge")![ThemeRole.Primary] == Color.Parse("#7A3CFF")) return "the built-in theme changed";
            e.Name = "NoIntrovert Ember"; // taken by a built-in dark theme
            e.SaveCommand.Execute(null);
            if (t.Editor is null || !t.Status.Contains("already")) return "a taken name was saved";
            e.Name = "Grape Soda";
            e.SaveCommand.Execute(null);
            if (t.Editor is not null) return "the editor stayed open: " + t.Status;
            if (t.SelectedDark?.Name != "Grape Soda" || t.ActiveDark[ThemeRole.Primary] != Color.Parse("#7A3CFF")) return "the new theme isn't in use";
            if (t.PreviewKind is not null) return "the preview stayed on";
            return File.ReadAllText(file).Contains("\"DarkTheme\": \"user:grape-soda\"") ? null : "not saved in the settings";
        }));

        test("Themes: a custom theme is changed in place, Cancel puts it back, Delete returns to the default", () => WithThemes(dir("themes-own"), null, (t, _) =>
        {
            t.Customize("Light");
            t.Editor!.Name = "Paper";
            t.Editor.SaveCommand.Execute(null);
            var paper = t.SelectedLight!.Theme;
            t.Customize("Light");
            if (t.Editor is not { EditsOwn: true, Name: "Paper" }) return "a custom theme was copied rather than edited";
            t.Editor.Rows.First(r => r.Role == ThemeRole.Background).Hex = "#ffffff";
            t.Editor.CancelCommand.Execute(null);
            if (t.ActiveLight[ThemeRole.Background] != paper[ThemeRole.Background]) return "Cancel kept the change";
            t.Customize("Light");
            t.Editor!.Rows.First(r => r.Role == ThemeRole.Accent).Hex = "#123456";
            t.Editor.SaveCommand.Execute(null);
            if (t.LightThemes.Count(o => o.Name == "Paper") != 1 || t.ActiveLight[ThemeRole.Accent] != Color.Parse("#123456")) return "saving made a second Paper";
            t.Customize("Light");
            t.Editor!.DeleteCommand.Execute(null);
            return t.SelectedLight?.Id == ThemeLibrary.DefaultLightId && t.LightThemes.All(o => o.Name != "Paper") ? null : "Delete didn't go back to the default";
        }));
    }

    private static double ThemeContrast(ThemeDefinition theme) => ThemeRoles.Contrast(theme[ThemeRole.Text], theme[ThemeRole.Surface]);

    private static string? WithThemes(string folder, HttpMessageHandler? web, Func<ThemesViewModel, string, string?> body)
    {
        var previous = AppSettings.SettingsDirectoryOverride;
        AppSettings.SettingsDirectoryOverride = folder;
        try
        {
            var settings = new AppSettings();
            var t = new ThemesViewModel(settings, new NoDialogs(), new ThemeLibrary(Path.Combine(folder, "themes")), web);
            return body(t, Path.Combine(folder, "settings.json"));
        }
        finally { AppSettings.SettingsDirectoryOverride = previous; }
    }

    private sealed class FakeWeb(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.Host == "raw.githubusercontent.com" || request.RequestUri.Host == "example.com"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
