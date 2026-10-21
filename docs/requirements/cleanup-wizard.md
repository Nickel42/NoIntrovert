# Requirement: Cleanup wizard (M7)

Status: **Approved to implement** (Q1–Q3 decided 2026-10-19)
From the UI plan's M7: "the planner, dry-run diff, Journal-backed run and undo".

## 1. Goal

One guided pass that leaves the Library tidy:
- every game in its target format (docs/target-formats.md);
- named as No-Intro or Redump name it (M6);
- in a predictable folder;
- duplicates gone.

Nothing happens until you've seen the whole plan, and everything the wizard did can be undone.

## 2. Scope

**In scope:**
- A planner that turns the Library into a list of steps: convert, rename, move, remove a duplicate.
- A dry run that shows every step and the space saved.
- Running the steps on the shared queue.
- A journal, so a run can be undone.

**Out of scope:**
- Downloading anything other than the DATs M6 already fetches.
- Box art and frontend metadata.

## 3. What it builds on

- The **target formats** (status per file, `TargetResolver`, the Convert jobs).
- **Verify & match** (the expected name, the match results, `originals.json`).
- The **Library index** (games, discs, duplicates).

## 4. Functional requirements

- **FR-1 Plan.** For the games you select (or the whole Library), the planner lists steps in this order:
  1. **convert** what isn't optimal (the Convert page's own jobs, with your target overrides);
  2. **rename** to the expected name (Verified, name differs);
  3. **move** into the layout (FR-6);
  4. **remove** duplicates (FR-7).

  Unknown-system and unmatched files are listed, never touched.
- **FR-2 Dry run.**
  - The plan is shown as a list of before → after, grouped by game, with the space saved. Both sides are paths in the Library folder, so you see where each file ends up (`NES/Mario.nes → roms/nes/Super Mario Bros. (World).7z`).
  - Every step can be unticked.
  - Nothing is written until **Run**.
- **FR-3 Run.**
  - Steps go to the shared queue.
  - A step that depends on another (rename after convert) waits for it, and is skipped if it fails.
- **FR-4 Journal and undo.**
  - Each run writes a journal beside the settings: every move and rename with its before and after paths, and every removed file with where it went.
  - Removed files (originals of conversions, duplicates) go to the run's trash, `.noi-trash/<run>/` in their Library folder: on the same drive, so it's a rename, and restorable without depending on the Recycle Bin. **Empty trash** sends a run's trash to the Recycle Bin.
  - **Undo run** reverses the run in reverse order, skipping anything changed since.
  - A conversion is undone by restoring its original from the trash and removing the new file (Q2). This works until the run's trash is emptied; a step that can no longer be undone is listed, not guessed at.
- **FR-5 Safety.**
  - Never overwrites: a name clash becomes a step you have to resolve in the dry run.
  - Removals only happen after the new file verifies.
  - Nothing outside the Library folders is written.
- **FR-6 Layout (Q1, issue #30).** A setting, chosen on the Cleanup page itself (*File layout*) and in *Settings → Library → Folder layout*. Changing it makes the plan again.
  - **ES-DE format** (the default): a top-level `roms` folder in each Library folder, one folder per system inside it, and the games' files inside those: `<Library folder>/roms/<es-de name>/<Game>.<ext>`. If the Library folder is itself called `roms`, the system folders go straight inside it.
    - The system folders have the names in ES-DE's *Supported game systems* list (`nes`, `snes`, `psx`, `saturn`, `dreamcast`, `gc`, `wii`, `wiiu`, `psp`, `ps2`, `ps3`…), in lower case as ES-DE expects them (Linux and macOS ES-DE are case-sensitive). The registry has an `esde` name for each system.
    - Games below a system folder don't keep subfolders. **The only folders allowed at the game level** are games that have to be folders:
      - Wii U NUS titles and Wii U title folders (`code/content/meta`), moved whole under their own name;
      - PS3 games, as `<Game>.ps3` folders (ES-DE's *directories interpreted as files*; a PS3 game folder without `.ps3` gets it);
      - a multi-disc game's `<Game>.m3u` folder (R7 in ES-DE form, below).
    - Folder games are moved whole even though no DAT entry names them; they're never renamed, looked inside or flattened.
    - Multi-disc games: the discs and the playlist share a folder named after the playlist, `psx/<Game>.m3u/<Game>.m3u` plus `psx/<Game>.m3u/<Game> (Disc N).chd`. ES-DE shows the folder as one game and launches the playlist inside it. The playlist lists the discs by file name. An older playlist (`psx/<Game>.m3u` beside a `hidden/` folder) goes to the run's trash.
  - **Keep the current layout:** the existing file and folder hierarchy stays as it is. Files are only converted and renamed in place; a multi-disc conversion follows R7 (`hidden/` plus `<Game>.m3u` in the discs' folder).
  - A system ES-DE has no folder for, and an unknown system, stay where they are.
  - Nothing is moved until **Run**, and every move is in the run's journal (FR-4).
- **FR-7 Duplicates (Q3).** When the same game (same DAT entry, or the same title, disc and system) exists more than once, the copy that stays is:
  1. the one already in its target format;
  2. then the verified one;
  3. then the one already in its layout folder.

  The others go to the Recycle Bin. Different regions or revisions aren't duplicates.

## 5. Decisions

- **Q1 Folder layout:** ES-DE format (`roms/<system>/`) by default, with "keep the current layout" (convert and rename only) as the alternative.
- **Q2 Undoing conversions:** yes, while the original can still be restored from the run's trash.
- **Q3 Duplicates:** the best copy stays (FR-7); the others go to the bin.

## 6. Acceptance criteria

| # | Library | Setting | Plan and result |
|---|---|---|---|
| 1 | `Games/Crash Bandicoot.cue` + `.bin` (PS1, matches the DAT) | ES-DE | Convert to CHD, rename and move: `Games/roms/psx/Crash Bandicoot (USA).chd`; the cue/bin go to the bin |
| 2 | The same | Keep the current layout | `Games/Crash Bandicoot (USA).chd`, in place |
| 3 | `Games/Mario.nes`, loose and verified | ES-DE | `Games/roms/nes/Super Mario Bros. (World).7z` |
| 4 | Three discs of one PS1 game | ES-DE | `roms/psx/<Game>.m3u/<Game>.m3u` + `roms/psx/<Game>.m3u/<Game> (Disc 1..3).chd` |
| 4a | The same | Keep the current layout | `hidden/<Game> (Disc 1..3).chd` + `<Game>.m3u` in the discs' folder |
| 4b | Converted discs in `psx/hidden/` with `psx/<Game>.m3u` | ES-DE | Moved into `roms/psx/<Game>.m3u/`, the playlist rewritten there; the old one goes to the bin |
| 4c | A Wii U NUS title or title folder; a PS3 game folder | ES-DE | Moved whole to `roms/wiiu/<folder>/`, `roms/ps3/<Game>.ps3/`; nothing inside them changes |
| 4d | The same | Keep the current layout | Left where they are |
| 5 | The same game as a CHD in two folders | ES-DE | One stays in `roms/psx/`; the other goes to the bin |
| 6 | An unmatched or unknown-system file | either | Listed, not touched |
| 7 | A rename onto a name that exists | either | Shown as a clash in the dry run; nothing overwritten |
| 8 | A dry run with nothing ticked off | — | Lists every step and the space saved; writes nothing |
| 9 | Undo right after case 1 | — | The cue/bin are back where they were and the CHD is gone |
| 10 | Undo after the bin was emptied | — | The renames and moves are undone; the conversion is listed as no longer reversible |
