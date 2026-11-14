# NoIntrovert: file information and presets panel (requirements)

Design mock-up: Penpot file "New File 1", page **Page 3**. It is a copy of Page 2 with the right-hand column redesigned.
Researched 2 Nov 2026.

## Goal

The right-hand column should explain the **selected job row** and make **presets** the main way to choose how files are converted:

1. What the file is (system, facts, formats) and why the app picked its default target.
2. Which preset will be used, what it does, and which emulators can play the result.
3. The preset's detailed settings, which can be changed only when the preset is **Custom**.

## Layout

- The column grows from 320 px to **440 px** and runs the full height of the window body, beside both the job list and the log. The job list and log get narrower.
- The column scrolls as one panel. Its sections are, in order: **File**, **Preset**, **Preset settings** (only when the preset is Custom). Where files are saved is in the run bar (see "Save to").
- The old "CONVERT" summary ("6 → CHD · 1 → RVZ") and the "Pack a folder…" button move out of the column. The summary is already shown in the run bar and toolbar. Pack a folder… (now **Add folder as one archive…**) sits next to Add folder… in the toolbar.
- When nothing is selected, the File section shows "Select a row to see what it is", and Preset shows the presets for the most common target in the list.
- When rows from more than one system are selected, File shows "3 systems selected" with one line per system.

## Navigation rail (revised 14 Nov 2026)

The rail has **no logo**. The title bar right above it already shows the app icon and "NoIntrovert"; the 170 px
illustration only repeated them and took a quarter of the rail's height. The full illustration
(`/Assets/nointrovert-logo.png`, kept in the project) belongs in Settings › About and the empty job list. The version
is in Settings › About.

The rail's pages are in labelled groups, each under a small, muted uppercase header (11 px bold, lined up with the
buttons' icons), with Settings set apart at the bottom:

- **LIBRARY**: one button, **Optimize**, with small text under it: "Verify, convert, organize". It runs all of those
  steps over the whole library. It's drawn like every other item (no card of its own); the line under its title is
  what explains it.
- **FILES**: **Convert**, **Verify** (was "Verify & match") and **Organize** (was "Cleanup", with a folder icon).
- **TOOLS**: **Library tests**, in Power-user mode only; the header is hidden with it.
- **Settings** sits at the bottom of the rail, below a divider.
- The **CHD info** page is removed.

Every item is selected the same way: a tint with a 4 px accent bar at its left, the text in the normal colour. Each
button's tooltip names it with its shortcut ("Convert (Ctrl+2)"). The groups come from the page list
(`PageViewModel.Group`, `MainViewModel.RailGroups`), not from positions in the view.

Below 1100 px the rail shows only icons (64 px wide): the headers are hidden and a thin divider separates the groups
(none above the first).

## Settings page (mock-up on "Page 4 · Settings", 4 Nov 2026; section list and Appearance, 14 Nov 2026)

One page in the main area, titled "Settings · Changes are saved as you make them". It has no Save button. One column of cards (at most 880 px wide), with a **section list** at its left, like Windows Settings: one row per card, Advanced's own cards indented under it. Choosing a row scrolls its card to the top (opening Advanced for one of its cards), and as the page scrolls the row of the card at the top is the one highlighted (scroll-spy). The list is in the tab order: Up and Down choose the previous or next card and scroll to it, Enter moves focus into the card. Below a 1000 px window the list becomes a **Jump to…** drop-down above the cards.

| Card | Settings |
|---|---|
| General | Start on (the page the app opens to) |
| Appearance | Theme (System / Dark / Light), Dark theme and Light theme (each with Customize…, the theme editor), Light brightness, Row density (Compact / Comfortable). **More themes…** (collapsed): find a theme in the two lists, import a file or a web address, export the dark or light theme, open the themes folder, reload |
| Output | Save to (the default for the run-bar choice: next to the originals, a folder, or replace the originals ⚠), Folder layout, Overwrite existing files (off: a taken name gets " (2)"), Working folder for unpacking (default %TEMP%\NoIntrovert) |
| Performance | Jobs at once (stepper, default 2), Check each file after converting (on; required before originals are replaced) |
| Log | Open the log when a run has errors (on), Keep logs for (30 days), Open log folder |
| Default presets | One preset dropdown per system (PlayStation, PS2, PSP, Dreamcast, Saturn, GameCube/Wii…), then Saved presets with Manage… |
| Library | Library folders that Optimize scans (add/remove), Emulator data source and date with Check now |
| Keys | Wii U, 3DS, Switch, PS3 and PS Vita keys. A row that failed for missing keys has **Set up keys**, which opens Settings with this card at the top and focus on its first button |
| Advanced (collapsed) | Power user · Archives · Files and folders · Game databases · Target formats · Diagnostics (Power-user mode only) |
| About | The NoIntrovert illustration (120 px), version, bundled tool versions, Check for updates, Licenses |

Links into the page name a card (`SettingsViewModel.ShowSection("Keys")`; `ShowKeys()` for Set up keys); the page scrolls it to the top and focuses its first control.

## Activity: the status bar and its drawer (replaces the log bar and the queue drawer, 14 Nov 2026)

A two-hour batch needs one answer: how far along it is, how long is left, and what failed. That answer is in one
place, the status bar at the bottom of the window. The queue and the log are in one drawer above it, closed by
default, so no log card or queue drawer takes height from the page.

**Status bar**, left to right:

- The queue summary: "Queue 1 running · 4 queued · 3 done · 7 min left · 52 MB/s"; "Idle · 3 done" once it's
  finished, and only "Queue empty" when there's nothing in it. Below 1100 px wide it drops the time left and the
  speed (its tooltip keeps them).
- The queue's overall progress bar, filling the space, with the percentage at its right end ("45%"). Progress =
  (finished items + the running items' fractions) ÷ all items in the queue; skipped items don't count. Hidden when
  the queue is empty.
- **2 failed**, a red badge, only when a job in the queue failed (the queue's Clear finished removes it). Clicking
  it filters the page showing to its failed rows: the filter box reads `is:failed`, a word that matches the row's
  status and nothing else (plain "failed" would also keep a file or folder with "failed" in its name), and has
  focus, so Escape clears it. When the page showing has no failed rows, the first page that has some is shown and
  filtered.
- The log's counts, "1 error · 3 warnings" (only the counts there are; red with an error), when anything was
  logged at warning or error. Clicking it opens Activity on its Log tab, with Show only errors on when there's an
  error.
- **Activity** at the right, with the number of jobs running or waiting and a chevron. It opens and closes the
  drawer; so does Ctrl+J (⌘J), from anywhere, a text box included. Tooltip: "Show the queue and the log (Ctrl+J)".

**The Activity drawer** opens above the status bar across the page (not the rail). A splitter above it changes its
height (default 260 px), which is remembered; a short window shows it shorter, so the page keeps its run bar.
Its header has two tabs and ✕:

- **Queue**: every job from every page, in the order it was added, each with its page, stage, progress and
  Cancel; Clear finished and Cancel all.
- **Log**: the session's lines with Show debug, Show only errors, Copy, Save… and Clear (and, in Power-user mode,
  lib-chd-kit messages and Progress). Show only errors leaves only the error lines (Show debug is off while it's
  on), and Copy and Save… then write only those; the counts still count every line.

Whether it's open and which tab show are remembered. Settings › Log › "Open the log when a run has errors" opens a
closed drawer on the Log tab when an error is logged; that isn't saved as the user's choice. A settings file from
before the drawer that had the log expanded opens on the Log tab.

Each page's run bar keeps only a one-line status ("Working on 7 items…", then "Finished in 0:12:04 · 40 done,
2 failed"), trimmed with the whole text in its tooltip; the progress is on the status bar and on each row. The beam
around the list while it runs, and the taskbar button's progress, stay as they were.

## Selection and focus (decided 2 Nov 2026)

- The panel always describes the **focused** row: the last row clicked or moved to with the keyboard. Selection and focus are separate.
- Selection is shown with a **checkbox column** at the left of the job list. The header has a select-all checkbox that shows "some selected" (–) when only part of the list is selected. Selected rows get a light tint.
- The focused row gets a stronger fill, a 2 px **orange (#FF8C42, the palette's accent)** outline and a 4 px orange bar on its left edge, so it stands apart from the lilac/grey selection colours. It is also selected.
- The toolbar summary starts with the selection count: "3 selected · 9 items · 2 done · 1 failed".
- The panel header says "Focused: row 3 of 9 · 3 selected".

## Row density (14 Nov 2026)

The job list (Optimize, Convert, Verify, Organize) has two densities, chosen in Settings › Appearance › **Row density** and saved as `General.ListDensity`. Compact is the default for new and existing users. It's a visual change only. The list stays virtualised (a `VirtualizingStackPanel`) at the 5,000-game scale.

- **Compact**: one line per row, about 32 px. At the default 1180×820 window, Convert shows about 13 rows, against about 7 in Comfortable.
  - The folder is not shown under the name. The row's tooltip has the full path.
  - **Preset** is still a dropdown, so the keyboard and screen readers work as before, but it's drawn flat: no border or background at rest, so it reads as text. A chevron shows when the pointer is over the row, when the row is focused, or when the dropdown itself is pointed at or focused. Open, it has the full dropdown look; with keyboard focus it gets an accent outline.
  - "Convert → .chd" moves into the dropdown's tooltip, with the system's name. A muted note follows the preset's name only when it says something the name doesn't:
    - why a file the rules leave is left ("Optimal", "Left as is (your setting)");
    - the extension when the preset's name doesn't contain it ("→ .rvz" for Custom or for a saved preset named "My PS1").
    "CHD · Standard ★" making a .chd, or Leave as is chosen by hand, adds no note.
  - CHANGES (Optimize, Organize) is one line with an ellipsis. Its tooltip has the steps in full.
  - Progress is the bar only, hidden until the row runs. The stage is in the bar's tooltip.
  - Result is one line, with "Set up keys" beside it. The speed is in its tooltip.
- **Comfortable**: the earlier two-line layout. It has the folder under the name, a bordered dropdown with the note under it, the stage under the bar, and the speed and "Set up keys" under the result.
- In both densities, a row whose preset isn't the recommended one gets a small **accent dot** before the preset, and its tooltip says "Not the recommended preset (CHD · Standard ★)." The recommended preset is the system's ★ preset, or Leave as is when the rules leave the file. A row with nothing to recommend (an unknown system, or a ★ preset the file can't use) is never marked. The dot's slot is kept on every row, so the names line up.

## 1. File section (focused row)

| Item | Content | Source |
|---|---|---|
| System image | The console picture from the app's bundled set, drawn on a tile. Use the converted icons in `consoles/icons/{128,256,512}/<name>.png`: transparent background, trimmed, centred on a square with a 6 % margin. They are 1×, 2× and 4× of a 128 px tile, so they work on light and dark themes. `dos.png` had an opaque background that was removed. | Bundled assets |
| Title | System name, e.g. "Sony PlayStation" | Hand-checked system JSON |
| Subtitle | Manufacturer · first release year | System JSON |
| Tags | Generation, media type, and a gold **Verified ✓** badge when the file matches a known good dump (no badge when there's no match or it hasn't been checked). Hovering the badge shows a tooltip: "Checked against Redump, a community database of verified disc dumps. A match means your file is byte-for-byte identical to a known good copy of the game, so it isn't corrupted or modified." The tooltip ends with a "Learn more" link to redump.org. Don't use the word "Redump" in the badge itself | System JSON; badge from the existing Verify & match code |
| Facts | Two rows only: **Filename** (the selected file's name, e.g. "Final Fantasy VII (USA) (Disc 3).cue") and **About file** (format, track count, size, disc number). Release dates, media and the Verified ROM row were removed on 2 Nov 2026 to cut clutter: media is already a tag and verification is the badge. On 3 Nov 2026 Units sold was replaced by Filename | Existing scan code |
| Formats | One short paragraph under "Formats for <system>": the formats available for the system, then one sentence on why the default is best. No per-format rows and no ★ legend | Hand-written format JSON (no external source has the "why" text) plus the existing `TargetResolver` rules |

## 2. Preset section (main interaction)

- The choice is **one dropdown: "Preset (format + settings)"**. Revised again on 2 Nov 2026: there is no separate Target format dropdown.
  - A preset is a **target format plus all of its settings**, so each file needs only one choice.
  - Options are named "Format · Preset", e.g. "CHD · Standard ★". The recommended one is marked ★.
  - Each option shows a one-line hint and how many known emulators play its output, e.g. "6 of 6". An option that loses data for this system carries a warning, e.g. "ISO · Data track only ⚠".
  - **Custom** comes after the normal presets, after a divider. Choosing it shows the Preset settings section, with Target format and every setting editable.
  - **Presets with ⚠ go at the very bottom**, below Custom, under a "⚠ May not play" group label. They stay in the list.
  - Named custom presets the user has saved appear above Custom, marked as theirs.
- **One preset per file.** The job list's Target column is now a **Preset** column: each row has its own preset dropdown, e.g. "CHD · Standard" or "PBP · Standard".
  - The panel's Preset section edits the **focused** row only ("For this file").
  - When other rows are selected, an **"Apply to N selected"** button copies the focused row's preset to them. A hint says whether they already use it, and the button is disabled when they all do.
- Presets for PlayStation discs (6 options):

| Preset | Format | When to use it |
|---|---|---|
| **CHD · Standard** ★ | CHD | Smallest lossless file that most emulators read |
| PBP · Standard | PBP | PSP / PS Vita (Adrenaline), level 9, one file per disc |
| PBP · Multi-disc in one file | PBP | All discs of a game in one .pbp |
| BIN/CUE · Redump layout | BIN/CUE | Raw Redump tracks. Use it to undo a CHD/PBP or for tools that need raw tracks |
| ISO · Data track only ⚠ | ISO | ⚠ For PlayStation it drops CD-audio tracks and XA/FMV sectors, so most games break |
| Custom | any | Pick the format and settings yourself |

- There is no description box under the dropdown; it was removed on 3 Nov 2026 because it repeated the preset name and dropdown descriptions. Codecs live in Custom's Preset settings. Warnings sit next to the setting that causes them, e.g. the Zstandard reader warning under Compression.
- **Works with**: emulators that can open the output. **Every RetroArch core** for the system comes first, under "RetroArch cores", then the **most common standalone emulators** under "Standalone". Kept compact: each group is a two-column list, and each entry is just a small (14 px) status icon and the emulator name. There is no kind tag, since the group heading already says it. Only a ✕ entry has a second line: why it doesn't work, in small subtle text (only that row grows). Each entry is focusable; a click or Enter shows its detail.
  - the icon shows the support state for the **current preset**:
    - ✓ (green, #4ADE80; not the brand yellow, which reads as a warning next to red): the output's extension is supported, and so is the codec the preset uses
    - ✕ (red, name dimmed): not with this preset. Either the extension or the preset's codec isn't supported (e.g. Zstandard CHDs on an old reader)
    - ? (grey): not confirmed, no reliable data
  - hovering an entry shows a tooltip with the detail, e.g. "No CHD support · use BIN/CUE"
  - support is re-checked whenever the preset or its settings change
- Footer: the data source and date, e.g. "From libretro core info · 2 Nov 2026".

### Where the emulator data comes from

| Data | Primary source | Notes |
|---|---|---|
| RetroArch cores, the system each runs, and their extensions | **libretro-core-info** `*.info` files (MIT): `database`, `supported_extensions`, `display_name`, `license` | Match `database` (e.g. "Sony - PlayStation") to the system. Bundle a snapshot and refresh it at release time. |
| Standalone emulators per system | **ES-DE** `resources/systems/windows/es_systems.xml` + `es_find_rules.xml` (MIT; bundle a snapshot with the notice). Fallback for per-emulator extensions: **Batocera** `*.emulator.yml` (LGPL-3.0) | ES-DE is updated about weekly and lists one labelled command per emulator, e.g. "DuckStation (Standalone)". `es_find_rules.xml` also lets the app detect installed emulators. Its extensions are per system, so use Batocera's per-emulator `file_extensions` where they exist. Windows-only emulators (ePSXe, SSF, Demul) aren't in Batocera. NoIntrovert's `TargetFormats.json` already has the ES-DE system ids. |
| Codec support (e.g. CHD Zstandard `cdzs`) | Small data file of facts, each row with a source link, refreshed by CI that checks each emulator's bundled libchdr for the `cdzs` codec | No catalog tracks this. `cdzs` arrived in chdman with MAME 0.262 (Jan 2024). |

Full research, with sources: [research-emulator-sources-and-presets.md](emulator-sources-and-presets.md).
| Per-core documentation link | docs.libretro.com/library/… | Used as "learn more" links only |

Rejected as bundled data:
- **libretro-database**: per-game records only, and CC-BY-SA.
- **ScreenScraper / TheGamesDB / LaunchBox**: online-only or restrictive terms.
- **Emulation General Wiki**: licence unclear, so used only as a reference link.

### PlayStation example used in the mock-up

| Emulator | Kind | .chd | Zstandard CHD | .pbp | .bin/.cue | .iso | Source |
|---|---|---|---|---|---|---|---|
| DuckStation | Standalone | ✓ | ✓ (builds after 18 Feb 2024; bundled libchdr has `cdzs`) | ✓ (unencrypted) | ✓ | opens, but assumes Mode 1/2048, so most games break | DuckStation README and `dep/libchdr` |
| ePSXe | Standalone | ✓ | not confirmed (closed source) | ✓ | ✓ | ✓ (same breakage) | epsxe.com |
| PCSX-Redux | Standalone | ✗ | ✗ | ✓ | ✓ | opens (same breakage) | `src/cdrom` in its repo |
| ares | Standalone | ✓ | ✓ | not confirmed | ✓ | not confirmed | `thirdparty/libchdr` |
| Beetle PSX HW | RetroArch core | ✓ | ✓ (libretro-common rchd + zstd) | ✓ | ✓ | ✗ | mednafen_psx_hw_libretro.info: `cue\|toc\|m3u\|ccd\|exe\|pbp\|chd\|bin` |
| Beetle PSX | RetroArch core | ✓ | ✓ | ✓ | ✓ | ✗ | mednafen_psx_libretro.info (same list) |
| SwanStation | RetroArch core | ✓ | ✓ (libchdr + zstd 1.5.7) | ✓ | ✓ | ✓ | swanstation_libretro.info: `exe\|psexe\|cue\|bin\|img\|iso\|chd\|pbp\|ecm\|mds\|psf\|m3u` |
| PCSX ReARMed | RetroArch core | ✓ | ✓ (libchdr + zstd 1.5.7) | ✓ | ✓ | ✓ | pcsx_rearmed_libretro.info: `bin\|cue\|img\|mdf\|pbp\|toc\|cbn\|m3u\|ccd\|chd\|iso\|exe` |
| Mednafen | Standalone | ✗ (1.32.1; docs and changelog list only CUE, CCD/IMG/SUB and TOC) | ✗ | ✗ | ✓ | ✗ | Mednafen docs and changelog |

The CHD support people remember is in the libretro Beetle cores, not standalone Mednafen.

Extension lists were checked against libretro-core-info `master` on 2 Nov 2026.

## 3. Preset settings section

- The settings change with the target:
  - **PBP**: Compression level (0–9), Combine discs of a set into one file, Game ID and title from the Redump match, Verify.
  - **BIN/CUE**: Track layout (one .bin per track, Redump, or single .bin), File names (Redump or keep the source names), Verify against Redump.
  - **ISO**: Track (data track only, fixed), Sector size (2048, fixed), "Allow audio/XA discs anyway" (off).
- **The section appears only when the preset is Custom** (changed 2 Nov 2026; it used to show locked, read-only values for every preset). For a normal preset, the preset name and Works with say what it does.
- **Target format** is the first setting. It lists every format for the system, and the settings below it change to match.
- It shows every setting for the chosen format. For CHD:
  - Compression
  - ISO images
  - Hunk size
  - Verify after creating
- Choosing Custom copies the current preset's values, so nothing jumps. A "Reset to <preset>" button goes back.
- A custom setting that hurts compatibility (e.g. the Zstandard codecs) shows a warning, and the Works with list updates immediately.
- **Hunk size** is shown only in Custom, with a permanent warning under it: "⚠ Advanced. A non-default hunk size can make CHDs larger, slower to load or unreadable in some emulators. Leave it unless you understand the risks." There is no separate power-user mode.
- **Save as a preset (optional):** at the end of the Custom settings there's a name box ("Name it, e.g. My CHD · Zstandard") and a Save preset button. Saved presets appear in every file's Preset dropdown for that system. Custom works without saving.

## 4. Save to (in the run bar, revised 3 Nov 2026)

The "This run" section is gone from the side panel. Where files go is now chosen in the **run bar**, just left of Cancel / Convert, so it's visible right before the user commits. It's one row of three radio buttons:

- **Next to the originals** (default)
- **Folder: <path> · Change…**: the path shows the last folder used. Change… opens a folder picker. Beside it, **Layout** (13 Nov 2026) arranges the folder: **ES-DE** (default, `roms/<ES-DE system name>`), **RomM** (`roms/<RomM platform name>`), **Same folders** (the subfolders below the folder each original was added from), or **No subfolders**. It's one setting with Settings › Output › Folder layout.
- **Replace the originals ⚠**: each original is deleted **only after its new file verifies**. If verification fails, the original stays and the row shows why. While it's chosen (revised 14 Nov 2026):
  - the run button turns red (the `danger` style) and says how many originals go: **Convert and replace 29 originals** ("1 original" for one). Rows left as is and rows already done don't count.
  - one line under Save to, in the warning colour, says what happens to them: "Originals go to the Recycle Bin after each new file verifies." or, with the Recycle Bin off in Settings › Output, "Originals are deleted after each new file verifies."
  - **every run asks first**, as it starts: "Replace 29 originals?", saying each original is removed only after its new file verifies and whether it then goes to the Recycle Bin or is deleted permanently. Cancel is the default button. Its **Don't ask again until NoIntrovert restarts** checkbox lasts for the session only and is never saved. Picking the radio button itself asks nothing.

The run bar's choice lasts until NoIntrovert closes and is never saved: each session starts from Settings › Output's default. Making Replace the originals that default asks once, in Settings; runs still ask as they start.

This is the **only** place originals can be deleted. The "CHD · Replace the originals" preset and the "Remove the originals" setting in Custom were removed, so presets never decide this. **Overwrite existing files** moves to Settings, because people rarely change it.

## Decisions (2 Nov 2026)

1. The panel follows the **focused** row, and the job list shows selection and focus differently (see "Selection and focus").
2. **One preset per file.**
3. The job list's Target column becomes a **Preset** column.
4. Custom settings **can be saved under a name** for reuse. Saving is optional.
5. Works with lists **every RetroArch core** for the system, then the **most common standalone emulators**. This data comes from maintained external sources, not app logic (see the research file).
6. RetroArch support comes from libretro-core-info and docs.libretro.com. Standalone support needs a source of truth too (see the research file).
7. ⚠ presets stay in the list, at the bottom.
8. The "Formats for <system>" list stays for now. Revised later on 2 Nov 2026: it's a short paragraph, not a list.
9. Hunk size appears only in Custom, with a risk warning.
10. Preset lists are drafted for the other systems (see the research file).

## Found during research (needs action in the app)

- **PBP and CSO can't be produced yet.** `TargetFormats.json` marks them not producible, so the PBP presets (PS1) and CSO presets (PS2, PSP) need writers before they ship.
- **GameCube/Wii CHD output doesn't play in Dolphin**, which has no CHD reader. In the drafted presets it carries ⚠.
- All CHD presets keep chdman's default codecs, never Zstandard, so older readers such as Redream stable can open them. Zstandard is only in Custom, with a note that it needs a recent emulator. It isn't marked ⚠, because it loses no data.
