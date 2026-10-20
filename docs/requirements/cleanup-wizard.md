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
  - The plan is shown as a list of before → after, grouped by game, with the space saved.
  - Every step can be unticked.
  - Nothing is written until **Run**.
- **FR-3 Run.**
  - Steps go to the shared queue.
  - A step that depends on another (rename after convert) waits for it, and is skipped if it fails.
- **FR-4 Journal and undo.**
  - Each run writes a journal beside the settings: every move and rename with its before and after paths, and every removed file with where it went (the Recycle Bin, or a quarantine folder where there's no bin).
  - **Undo run** reverses the run in reverse order, skipping anything changed since.
  - A conversion is undone by restoring its original from the bin or quarantine and removing the new file (Q2). This works until the bin is emptied; a step that can no longer be undone is listed, not guessed at.
- **FR-5 Safety.**
  - Never overwrites: a name clash becomes a step you have to resolve in the dry run.
  - Removals only happen after the new file verifies.
  - Nothing outside the Library folders is written.
- **FR-6 Layout (Q1).** A setting, *Settings → Library → Folder layout*:
  - **ES-DE system folders** (the default): `<Library folder>/<es-de name>/<Game>.<ext>`, with the folder names ES-DE uses (`psx`, `gba`, `megadrive`…). The registry gets an `esde` name for each system. Multi-disc games follow the guide's R7: `psx/hidden/…` plus `psx/<Game>.m3u`.
  - **Keep in their own folders:** nothing is moved; files are only renamed (and converted).
  - A system ES-DE has no folder for, and an unknown system, stay where they are.
- **FR-7 Duplicates (Q3).** When the same game (same DAT entry, or the same title, disc and system) exists more than once, the copy that stays is:
  1. the one already in its target format;
  2. then the verified one;
  3. then the one already in its layout folder.

  The others go to the Recycle Bin. Different regions or revisions aren't duplicates.

## 5. Decisions

- **Q1 Folder layout:** ES-DE's short system names by default, with "keep in their own folders" (rename only) as the alternative.
- **Q2 Undoing conversions:** yes, while the original can still be restored from the bin or quarantine.
- **Q3 Duplicates:** the best copy stays (FR-7); the others go to the bin.

## 6. Acceptance criteria

| # | Library | Setting | Plan and result |
|---|---|---|---|
| 1 | `Games/Crash Bandicoot.cue` + `.bin` (PS1, matches the DAT) | ES-DE | Convert to CHD, rename and move: `Games/psx/Crash Bandicoot (USA).chd`; the cue/bin go to the bin |
| 2 | The same | Keep in their own folders | `Games/Crash Bandicoot (USA).chd`, in place |
| 3 | `Games/Mario.nes`, loose and verified | ES-DE | `Games/nes/Super Mario Bros. (World).7z` |
| 4 | Three discs of one PS1 game | ES-DE | `psx/hidden/… (Disc 1..3).chd` + `psx/<Game>.m3u` |
| 5 | The same game as a CHD in two folders | ES-DE | One stays in `psx/`; the other goes to the bin |
| 6 | An unmatched or unknown-system file | either | Listed, not touched |
| 7 | A rename onto a name that exists | either | Shown as a clash in the dry run; nothing overwritten |
| 8 | A dry run with nothing ticked off | — | Lists every step and the space saved; writes nothing |
| 9 | Undo right after case 1 | — | The cue/bin are back where they were and the CHD is gone |
| 10 | Undo after the bin was emptied | — | The renames and moves are undone; the conversion is listed as no longer reversible |
