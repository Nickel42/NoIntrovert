# Themes

Settings › Appearance picks a **dark theme** and a **light theme**. *Theme* (System / Dark / Light) chooses which
of the two is showing; System follows the operating system and switches with it.

Built in:

| Dark | Light |
|---|---|
| **NoIntrovert Dark**: the design's navy with the logo's lilac and orange (the default) | **NoIntrovert Light**: the design's warm grey, lilac and orange (the default) |
| **NoIntrovert Ember**: warm charcoal, the logo's orange door as primary, its lilac ring as accent | **NoIntrovert Sunrise**: cream and the orange door, lilac accent |
| **NoIntrovert Cartridge**: the deep teal of the cartridge's screen, orange accent | **NoIntrovert Seafoam**: cool sea-glass greys and teal, orange accent |

Fifteen classic themes are built in as well, NoIntrovert's own theme files of well-known palettes
(`NoIntrovert/Themes/Classic`):

| Dark | Light |
|---|---|
| Catppuccin Mocha, Dracula, GitHub Dark, Gruvbox Dark, Monokai, Nord, One Dark, Rosé Pine, Solarized Dark, Tokyo Night | Catppuccin Latte, GitHub Light, Gruvbox Light, Solarized Light, Tokyo Night Light |

Each uses its palette's own colours. Where a palette colour doesn't reach WCAG AA as text (4.5:1) against the
theme's cards and window, the theme picks the palette's next shade (Solarized's text is base2/base02, not
base0/base00) or sets a lighter or darker tint of it for that one use only: `DangerText` is error text, so Nord's,
Dracula's, Monokai's and others' reds still fill danger buttons but error messages use a readable tint. A test
checks every classic theme's text, secondary text, headings, button and rail text, error and warning text, the
Verified badge and settings fields at 4.5:1, and the accent outline at 3:1.

The lists show NoIntrovert's own themes first, then your custom and imported ones, then the classic themes A to Z.
*Find a theme* (under More themes…) narrows both lists by name or author; a theme's tooltip says where it comes from and who
made it. A setting saved by an older version that chose one of the Base16 schemes it used to ship (`base16-nord`)
finds the classic theme of the same name (`nord`), or else the kind's NoIntrovert theme.

*Light brightness* (100% down to 70%) dims whichever light theme is chosen, for when its whites are too bright.
Every colour of the theme is scaled toward black by the same amount, so text and borders keep their contrast with
the background; dark themes aren't touched.

*Customize…* opens the theme editor on the chosen theme: a colour for each of 14 roles, previewed in the whole
app as you change them. Saving asks for a name and adds the theme to its list; a built-in theme (NoIntrovert's or
a classic one) is copied, never changed. Custom and imported themes live in `%AppData%\NoIntrovert\themes` (*Open themes folder*). A file you put
there yourself shows up after *Reload*.

## The theme file: an Avalonia ResourceDictionary

Avalonia has no theme-file standard beyond its own `ResourceDictionary`, so that's the format: any Avalonia app
can merge one of these files as it is.

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <x:String x:Key="ThemeName">NoIntrovert Ember</x:String>
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
rail), `Border`, `ControlBorder`, `Text`, `SubtleText`, `SectionText`, `Primary` (selection, tints), `Accent`
(the run button, focus, the selected page's bar), `Danger`, `Warning`, `Success`. A role left out is worked out
from the others. Every other colour (row tints, tags, SukiUI's control colours) is worked out from the roles in
`NoIntrovert/Core/Themes/ThemeResources.cs`, unless the file sets that key itself.

### Where the accent goes

The accent is the one strong colour on screen: it fills the run button (Convert, Optimize, Verify, Organize; also
*Save theme*), with `AccentForeground` text, dark or white, whichever reads at 4.5:1 or better. The selected page
in the rail is not filled with it: it's the `PrimaryTint` with a 4 px accent bar at its left and the normal text
colour, as Windows 11's navigation draws it, and the bar shows on the icon-only rail too.

### Contrast is kept, whatever the theme says

So that an imported or hand-made theme can't come out unreadable, the colours actually used are nudged lighter
(on a dark theme) or darker (on a light one), keeping their hue, until they read on the cards and the window:

| What | At least |
|---|---|
| `Text`, `SubtleText` (hints, folder paths) | 4.5:1 |
| `ControlBorder` (checkbox, radio, text box and button outlines; Settings' fields) | 3:1 |
| Status dots and log glyphs (`StatusOk`, `StatusWarn`, `StatusBad`, `StatusBusy`, `StatusMuted`) | 3:1, on the log too |
| Status words (`StatusOkText`, `StatusBadText`, e.g. PASS/FAIL) | 4.5:1 |

A colour already that far is left as it is. Where nothing can get there (a scheme with a bright card on a dark
window), the colour is whichever of black and white reads best. The editor shows a role's colour as chosen.

The status colours come from the roles: bad is `Danger`, warning is `Warning`, done is `Success` when it's a
green (as in Base16 schemes) and a green of the app's own otherwise (NoIntrovert's `Success` is the Verified
badge's yellow); running is a blue, nothing-yet is a grey from `SubtleText`. A theme file can set any of these keys.

`FocusedRowFill` (the row the keyboard is on) is the primary's tint, as strong as it can be while the accent
outline around it still reads at 3:1, selected or not. Where the accent is under 3:1 on the cards to begin with
(an orange on white), the fill takes almost nothing more away, and the row is told apart by its outline and its
accent bar.

The theme button in the title bar cycles *System* → *Dark* → *Light* → *System* (a monitor, a moon, a sun), and
its tooltip says the mode and what a click gives ("Theme: System (follows Windows). Click for Dark.").

The animated border around the job list while a run goes is drawn only when the OS allows animations (Windows:
Settings › Accessibility › Visual effects › Animation effects; macOS: Reduce motion; GNOME: enable-animations).
Nothing pulses while idle.

The importer also reads a dictionary with `ResourceDictionary.ThemeDictionaries` (`Dark` and `Light`), giving
one theme of each. It also reads a dictionary made only of the app's own keys (`AppBackground`, `PanelBackground`,
`SukiText`…).

The file is only read, never run: no objects are created from it, a DTD is refused, and nothing over 1 MB is read.

## More from the web: Base16 and Base24

Base16 and Base24 colour schemes (a small `.yaml` of 16 or 24 colours) can be imported; none are shipped.
*Import file…* takes their `.yaml`. To download one, paste its address into *More themes…* and choose *Import from web*.
Base16 names are tidied into theme names ("Gruvbox dark, hard" → "Gruvbox Dark Hard"). A GitHub page's
address (`github.com/…/blob/…`) works too and is read from raw.githubusercontent.com. Both the current layout
(`system`/`name`/`variant`/`palette`) and the older one (`scheme:`, colours without `#`) are read.

How the 16 colours map: `base00`/`base01` are the window and the cards (swapped on a light scheme, so cards stay the
lighter), `base02` borders, `base03` control borders, `base04` secondary text, `base05` text, `base0D` primary,
`base09` accent, `base08` danger, `base0A` warning, `base0B` success.

*Export dark…* / *Export light…* save the chosen theme as an `.axaml` file to share or reuse.
