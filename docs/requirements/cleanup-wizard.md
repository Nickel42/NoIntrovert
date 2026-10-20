# Requirement: Cleanup wizard (M7)

Status: **Draft: needs decisions Q1–Q3** (2026-10-19)
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
  3. **move** into the layout (Q1);
  4. **remove** duplicates (Q3).

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
  - A conversion is undone by restoring its original from the bin or quarantine and removing the new file. Q2: or not at all.
- **FR-5 Safety.**
  - Never overwrites: a name clash becomes a step you have to resolve in the dry run.
  - Removals only happen after the new file verifies.
  - Nothing outside the Library folders is written.

## 5. Open questions

- **Q1 Folder layout.** Where each game goes inside a Library folder:
  - **A.** `<System>/<Game>.<ext>`, with readable system names (`PlayStation/`, `Game Boy Advance/`), and multi-disc games as the guide's R7 (`PlayStation/hidden/…` + `.m3u`). *Recommended:* it's what the registry's folder detection already reads, and most frontends accept any folder name.
  - **B.** The same, but with the frontend short names ES-DE, Batocera and RetroBat expect (`psx/`, `gba/`).
  - **C.** The same, but with the DAT names (`Sony - PlayStation/`), as RetroArch's playlists use.
  - **D.** Keep each file in its folder: rename only, never move.

  Whichever is chosen, it's a setting with the others as choices.
- **Q2 Undoing conversions.** Should undo restore the original of a conversion (it's in the Recycle Bin or quarantine, so this works only until that's emptied), or is a conversion final once its new file verifies?
- **Q3 Duplicates.** When two copies of a game exist, which one stays?
  - *Proposed:* the one already in its target format, then the verified one, then the one inside the layout folder. The other goes to the bin.
  - Different revisions or regions aren't duplicates.

## 6. Acceptance criteria

To be written once Q1–Q3 are decided.
