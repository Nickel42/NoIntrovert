# Themes

Settings › General picks a **dark theme** and a **light theme**. *Theme* (System / Dark / Light) chooses which
of the two is showing; System follows the operating system and switches with it.

Built in:

| Dark | Light |
|---|---|
| **NoIntrovert Dark**: the design's navy with the logo's lilac and orange (the default) | **NoIntrovert Light**: the design's warm grey, lilac and orange (the default) |
| **Ember**: warm charcoal, the logo's orange door as primary, its lilac ring as accent | **Sunrise**: cream and the orange door, lilac accent |
| **Cartridge**: the deep teal of the cartridge's screen, orange accent | **Seafoam**: cool sea-glass greys and teal, orange accent |

Every Base16 scheme from [tinted-theming's collection](https://github.com/tinted-theming/schemes/tree/spec-0.11/base16)
is built in as well: about 270 dark and 110 light (Gruvbox, Nord, Solarized, Catppuccin, Tokyo Night…). The lists show
NoIntrovert's own themes first, then your custom and imported ones, then the Base16 schemes A to Z. The search box
above them narrows both lists by name or author; a theme's tooltip says where it comes from and who made it.

Base16 names are tidied into theme names: words are capitalised, and commas and file-style dashes are dropped
("Gruvbox dark, hard" → "Gruvbox Dark Hard", "selenized-white" → "Selenized White"). Joining words and names cased
on purpose are kept ("Arroz con Dulce", "iA Dark"). A scheme's id is `base16-` plus its file name, so a saved choice
survives a renamed scheme. The schemes sit unchanged in `NoIntrovert/Themes/Base16`;
`python -I scripts/refresh-base16-themes.py` brings them up to date. Their MIT notice
(`Themes/Base16/THIRD_PARTY_NOTICES.txt`) goes into every download as `THIRD_PARTY_NOTICES-base16-themes.txt`.

*Customize…* opens the theme editor on the chosen theme: a colour for each of 14 roles, previewed in the whole
app as you change them. Saving asks for a name and adds the theme to its list; a built-in theme (NoIntrovert's or
a Base16 scheme) is copied, never changed. Custom and imported themes live in `%AppData%\NoIntrovert\themes` (*Open themes folder*). A file you put
there yourself shows up after *Reload*.

## The theme file: an Avalonia ResourceDictionary

Avalonia has no theme-file standard beyond its own `ResourceDictionary`, so that's the format: any Avalonia app
can merge one of these files as it is.

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <x:String x:Key="ThemeName">Ember</x:String>
    <x:String x:Key="ThemeVariant">Dark</x:String>        <!-- Dark or Light; guessed from the background if left out -->
    <x:String x:Key="ThemeAuthor">NoIntrovert</x:String>
    <Color x:Key="ThemeBackground">#1A1614</Color>
    <Color x:Key="ThemeSurface">#26201D</Color>
    <!-- … one Color per role … -->
    <Color x:Key="ThemePrimary">#E0612C</Color>

    <!-- Optional: any of the app's own keys, to set one colour exactly -->
    <SolidColorBrush x:Key="PanelTile" Color="#2E2E40" />
</ResourceDictionary>
```

The roles (`Theme` + the name): `Background`, `Surface` (cards and panels), `Raised`, `Header` (title bar and
rail), `Border`, `ControlBorder`, `Text`, `SubtleText`, `SectionText`, `Primary` (buttons, selection), `Accent`
(focus, highlights), `Danger`, `Warning`, `Success`. A role left out is worked out from the others. Every other
colour (row tints, tags, SukiUI's control colours) is worked out from the roles in
`NoIntrovert/Core/Themes/ThemeResources.cs`, unless the file sets that key itself.

The importer also reads a dictionary with `ResourceDictionary.ThemeDictionaries` (`Dark` and `Light`), giving
one theme of each. It also reads a dictionary made only of the app's own keys (`AppBackground`, `PanelBackground`,
`SukiText`…).

The file is only read, never run: no objects are created from it, a DTD is refused, and nothing over 1 MB is read.

## More from the web: Base16 and Base24

Newer schemes, and Base24 ones, can be imported too. The library of ready-made colour schemes is [tinted-theming's Base16/Base24](https://github.com/tinted-theming/schemes)
(browse them in the [gallery](https://tinted-theming.github.io/tinted-gallery/)). *Import file…* takes their
`.yaml`. To download one, paste its address into *More themes* and choose *Import from web*. A GitHub page's
address (`github.com/…/blob/…`) works too and is read from raw.githubusercontent.com. Both the current layout
(`system`/`name`/`variant`/`palette`) and the older one (`scheme:`, colours without `#`) are read.

How the 16 colours map: `base00`/`base01` are the window and the cards (swapped on a light scheme, so cards stay the
lighter), `base02` borders, `base03` control borders, `base04` secondary text, `base05` text, `base0D` primary,
`base09` accent, `base08` danger, `base0A` warning, `base0B` success.

*Export dark…* / *Export light…* save the chosen theme as an `.axaml` file to share or reuse.
