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
    - The system folders have the names in ES-DE's *Supported game systems* list (`nes`, `snes`, `psx`, `saturn`, `dreamcast`, `gc`, `wii`, `wiiu`, `psp`, `ps2`, `ps3`…), in lower case as ES-DE expects them (Linux and macOS ES-DE are case-sensitive). The registry has an `esde` name for each system, checked against ES-DE's own list (FR-6a).
    - Games below a system folder don't keep subfolders. **The only folders allowed at the game level** are games that have to be folders:
      - Wii U NUS titles and Wii U title folders (`code/content/meta`), moved whole under their own name;
      - PS3 games, as `<Game>.ps3` folders (ES-DE's *directories interpreted as files*; a PS3 game folder without `.ps3` gets it);
      - the `hidden/` folder that holds multi-disc games' discs (R7, below).
    - Folder games are moved whole even though no DAT entry names them; they're never renamed, looked inside or flattened.
    - Multi-disc games (R7): the playlist goes directly in the system folder, `psx/<Game>.m3u`, and the discs in `hidden/` there, `psx/hidden/<Game> (Disc N).chd`. The playlist lists them by relative path (`hidden/<Game> (Disc 1).chd`). An empty `psx/hidden/noload.txt` makes ES-DE skip the folder, so the discs don't show as games of their own. The folder's name is the multi-disc folder setting (`hidden` by default), the same one the Convert page uses.
    - Discs from elsewhere (an old `hidden/` beside its playlist, or a `<Game>.m3u/` folder holding the playlist and discs, which an earlier version of the wizard made) move into `psx/hidden/` under their database names; the old playlist goes to the run's trash and an emptied `<Game>.m3u/` folder is removed. A `<Game>.m3u/` folder holding anything else is left, and the step fails with the reason.
  - **Keep the current layout:** the existing file and folder hierarchy stays as it is. Files are only converted and renamed in place; a multi-disc conversion follows R7 (`hidden/` plus `<Game>.m3u` in the discs' folder).
  - A system ES-DE has no folder for, and an unknown system, stay where they are.
  - Nothing is moved until **Run**, and every move is in the run's journal (FR-4).
- **FR-6a ES-DE system list (issue #29).** ES-DE adds and renames systems from release to release, so its folder names come from ES-DE itself, not from strings in the code.
  - **Source:** `resources/systems/unix/es_systems.xml` in ES-DE's GitLab project (`es-de/emulationstation-de`), at the **latest release tag**, not `master`, so the folders match the ES-DE users have installed. Each `<system>` gives the folder (`<name>`), `<fullname>` and `<extension>`; the folder names are the same on every platform, so the `unix` file is enough.
  - **Snapshot:** the app ships that list, parsed (`Core/Esde/es_systems.json`, with its release tag and the commit that last changed the file), so the ES-DE layout works offline and on first run.
  - **Update check:** in the background on app start, at most once a day, when *Check for newer databases when the app starts* is on; and whenever *Check now* is clicked (Settings → Game databases).
    1. Ask the releases API (`/projects/es-de%2Femulationstation-de/releases?per_page=1`) for the latest tag, with the last ETag; a 304 ends the check.
    2. If the tag differs from the list in use, ask the commits API for the last change to `es_systems.xml` at that tag. Only when that commit differs from the list's is the file downloaded (from `/-/raw/<tag>/…`) and parsed; otherwise only the tag is recorded.
    3. The list is cached under the settings folder: `esde/es_systems.json` (tag, commit, systems) and `esde/state.json` (last check time, ETags).
    4. Offline, refused, or a file that doesn't parse: nothing changes, the snapshot or the last cache keeps working, the problem is logged and shown in Settings, and the check is tried again on the next start.
  - **Mapping:** each registry system's `esde` name is looked up in the list in use (the cache, else the snapshot). When upstream has renamed or removed it, this is logged and shown in Settings (with the likely new name: the system with the same full name or extensions), and the snapshot's name is used. A name neither list has is never used: those games stay where they are. A changed mapping is a registry change, made by hand after checking.
  - **Settings** shows *ES-DE systems: release v3.5.0, checked 2 hours ago (195 systems)* beside the game databases' status.
  - **Snapshot guard:** a weekly GitHub Action (`.github/workflows/esde-snapshot.yml`) compares the bundled snapshot's commit with the last change to `es_systems.xml` at ES-DE's latest release and opens an issue when they differ, so releases ship a current snapshot.
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
| 4 | Three discs of one PS1 game | ES-DE | `roms/psx/<Game>.m3u` + `roms/psx/hidden/<Game> (Disc 1..3).chd` + `roms/psx/hidden/noload.txt` |
| 4a | The same | Keep the current layout | `hidden/<Game> (Disc 1..3).chd` + `<Game>.m3u` in the discs' folder |
| 4b | Converted discs in `PS1/hidden/` with `PS1/<Game>.m3u` | ES-DE | Moved into `roms/psx/hidden/`, the playlist written as `roms/psx/<Game>.m3u`; the old one goes to the trash |
| 4c | A `roms/psx/<Game>.m3u/` folder with the playlist and discs (an earlier version) | ES-DE | Discs moved into `roms/psx/hidden/`, `roms/psx/<Game>.m3u` written as a file; the old playlist goes to the trash; undo restores the folder |
| 4c | A Wii U NUS title or title folder; a PS3 game folder | ES-DE | Moved whole to `roms/wiiu/<folder>/`, `roms/ps3/<Game>.ps3/`; nothing inside them changes |
| 4d | The same | Keep the current layout | Left where they are |
| 5 | The same game as a CHD in two folders | ES-DE | One stays in `roms/psx/`; the other goes to the bin |
| 6 | An unmatched or unknown-system file | either | Listed, not touched |
| 7 | A rename onto a name that exists | either | Shown as a clash in the dry run; nothing overwritten |
| 8 | A dry run with nothing ticked off | — | Lists every step and the space saved; writes nothing |
| 9 | Undo right after case 1 | — | The cue/bin are back where they were and the CHD is gone |
| 10 | Undo after the bin was emptied | — | The renames and moves are undone; the conversion is listed as no longer reversible |
| 11 | ES-DE's latest release renames `psx` (FR-6a) | ES-DE | Logged and shown in Settings with the likely new name; the games still go to `roms/psx/` |
| 12 | First run with no network (FR-6a) | ES-DE | The bundled snapshot's folder names are used; the check is tried again on the next start |
