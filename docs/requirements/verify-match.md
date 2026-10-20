# Requirement: Verify & match (M6)

Status: **Approved to implement** (2026-10-19). The DAT source was decided with the user on
2026-10-19; the rest follows the M6 milestone in the UI plan ("integrity checks from the handlers,
DAT import, hashing with the cache, and match results with expected names").

## 1. Goal

NoIntrovert tells you whether each game is a good dump and what it should be called. It checks the
file's own integrity, hashes what's inside, and looks the hashes up in the No-Intro and Redump
databases. The user never has to find, download or update a DAT.

## 2. Scope

**In scope:**
- Fetching and caching DATs from libretro-database.
- An optional folder of the user's own DATs.
- Hashing every input kind the app knows, with a cache.
- Matching, with the expected name.
- Integrity checks (CHD, 7z/zip, RVZ).
- The Verify & match page, the Library's per-file results and the Settings card.

**Out of scope:**
- Renaming and moving files: that's M7 (Cleanup wizard).
- Keeping the original hashes of converted files: that's target formats Phase 3 (R11).
- Wii U: there are no Redump-style hashes for its titles.

## 3. Today's code (starting point)

- The Verify page checks CHDs only (`ChdJobs.Verify`, libchdman's verify).
- The target-format registry (`TargetFormats.json`) gives each system an id, and
  `SystemDetector` finds each input's system.
- `ArchivePeek` lists archive members without unpacking them, and `ArchiveEngine.Test` tests an
  archive.
- libdolphinrvz can hash a decoded GameCube/Wii disc (`Hash`), from any container it reads.

## 4. Functional requirements

### DATs

- **FR-1 Source.** DATs come from `github.com/libretro/libretro-database`, folders
  `metadat/no-intro` and `metadat/redump` (ClrMamePro format).
  - The registry lists, for each system, the DAT files that cover it (`dats`, e.g.
    `no-intro/Nintendo - Nintendo Entertainment System`).
  - No DATs are shipped with the app.
- **FR-2 Updating.**
  - On each start, in the background, the app checks for newer DATs, unless the setting is off.
  - The check uses the GitHub contents API for the two folders, with `If-None-Match`. An unchanged
    folder costs one "304 Not Modified" reply.
  - Only DATs for systems in the Library are downloaded, and only when their git blob SHA differs
    from the cached copy.
  - A system that first appears in the Library is fetched at the next check, or straight away with
    **Update now**.
  - Downloads go to `<settings folder>/dats/<no-intro|redump>/`, written to a temp file first.
  - Offline, or when GitHub refuses, the app keeps using the cache and says when it last updated.
  - Startup is never blocked.
- **FR-3 Own DATs.** In Settings, an optional folder of `.dat` files (ClrMamePro or Logiqx XML,
  e.g. a No-Intro daily pack). A DAT there whose header name matches a registry DAT replaces the
  downloaded one.
- **FR-4 Status.** The Verify & match page and Settings show the source and age, e.g. "Game
  databases: libretro-database, updated 2 hours ago (12 systems)", with **Update now**.

### Hashing and matching

- **FR-5 What is hashed.**

  | Input | Hashed |
  |---|---|
  | Loose ROM | The file. A headered ROM is also hashed without its header (NES/FDS 16 bytes, Lynx 64, Atari 7800 128, a SNES copier header of 512). |
  | zip / 7z | Each member's size and CRC32, read from the archive's headers without unpacking |
  | cue/bin, gdi, ccd/img | Each track file |
  | iso, bin, img | The file |
  | CHD | The tracks, extracted to the temp folder (cue/bin with one bin per track, or iso) |
  | GameCube/Wii (iso, rvz, wia, gcz) | The decoded disc, through libdolphinrvz |
  | Wii U, CSO/ZSO, others | Not hashed ("can't be checked yet") |

  Size, CRC32 and SHA-1 are computed in one pass.
- **FR-6 Hash cache.** Results are kept in `<settings folder>/hashes.json`, keyed by path, size and
  modification time, so unchanged files are never hashed twice.
- **FR-7 Matching.**
  - A file matches a DAT entry when the size and CRC32 match, and the SHA-1 too when it's known.
    Archive members are matched on size and CRC32 alone.
  - The input's system decides which DATs are searched. An unknown system searches every cached
    DAT, and a match then names the system.
  - libretro's Redump DATs list one track per disc (the first). A disc matches when any of its
    tracks matches an entry.
- **FR-8 Result.** Each input gets one result:

  | Result | Meaning |
  |---|---|
  | Verified | Matches an entry, and the name is the DAT's |
  | Verified, name differs | Matches; the expected name is shown |
  | Not in the database | Hashed, but no entry matches |
  | No database | No DAT for this system yet (offline, or unknown system) |
  | Can't be checked yet | A format that isn't hashed (FR-5) |
  | Damaged | The integrity check failed |

  - The expected name is the DAT game's name plus the input's own extension (`.7z`, `.chd`…).
  - For a converted file (CHD, RVZ), the match is on the decoded tracks, so a CHD of a good dump
    is Verified.
- **FR-9 Integrity.** Before matching:
  - CHDs are verified (libchdman), with the existing "fix a wrong header SHA-1" option.
  - 7z and zip archives are tested (7-Zip checks every CRC).
  - GameCube/Wii containers are checked by decoding them.

### Pages

- **FR-10 Verify & match page.**
  - The Verify page becomes **Verify & match** and takes every input the Library knows.
  - Each row shows its result and the expected name; the details show the DAT, the game and the
    hashes.
  - **Verify selected** on the Library sends the chosen games here.
- **FR-11 Library.**
  - Results are remembered (`matches.json`, keyed like the hash cache) and shown per file in the
    game details.
  - The game details show a summary, e.g. "2 of 2 files verified".
- **FR-12 Settings.**
  - A **GAME DATABASES** card shows the FR-4 status and **Update now**.
  - It has a "Check for newer databases when the app starts" switch (on by default).
  - Under *Advanced*: the own-DAT folder (FR-3).

## 5. Acceptance criteria

| # | Input | Expected |
|---|---|---|
| 1 | First start with NES and PS1 in the Library | The two DATs are downloaded, nothing else |
| 2 | Second start, nothing changed upstream | One 304 per folder, no downloads |
| 3 | Upstream NES DAT changed | Only that DAT is downloaded again |
| 4 | Offline start | The cached DATs are used; the status gives their age |
| 5 | Headered `Game.nes` named as in the DAT | Verified |
| 6 | The same ROM as `Wrong.nes` | Verified, name differs; expected `Game (USA).nes` |
| 7 | `Game.zip` holding the ROM | Verified, on the CRC32 from the zip's header |
| 8 | A ROM not in the DAT | Not in the database |
| 9 | cue/bin, and the CHD made from it | Both Verified against the Redump entry for track 01 |
| 10 | A damaged CHD, a damaged 7z | Damaged |
| 11 | A file hashed once, then checked again unchanged | Not hashed the second time |
| 12 | A DAT in the own-DAT folder with the same name | Used instead of the downloaded one |

## 6. Notes

- libretro's copies can be a few weeks behind No-Intro and Redump; the own-DAT folder covers
  anyone who needs the latest.
- GitHub allows 60 unauthenticated API requests an hour; a normal start uses two, and 304
  replies don't count.
