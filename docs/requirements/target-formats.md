# Requirement: Target formats and per-system overrides

Status: **Approved, ready to implement** (review finished 2026-10-19)
Guidelines (the user doc this requirement implements): [docs/target-formats.md](../target-formats.md)
Review record: Google Sheet "NoIntrovert Target Formats - Review"

## 1. Goal

For every file it sees, NoIntrovert knows the best format and container for that game's system.
It marks the file **Optimal** or offers to convert it to that target. The user can override the
target for a whole system or for one file type in Advanced settings, including "leave it alone".

## 2. Scope

**In scope:**
- A target-format registry covering every system in the guidelines.
- Detecting the system for each file.
- A status for each file.
- Rules R1–R13.
- Decisions D1–D10.
- The override settings.
- The docs.

**Out of scope here (D8):** converters for formats the app can't produce yet: PS3 decrypted
folders and PS Vita `.pkg` → NoNpDrm. (3DS `.zcci`, Switch NSZ/XCZ and Xbox XISO were on this list;
FR-9, FR-11 and FR-12 now cover them.) For these systems the app
shows the recommendation with no Convert button, and each converter gets its own requirement later.

## 3. Today's code (starting point)

- `Core/Formats/ConvertTargets.cs` `For()` decides each input's choices and default from its format
  and a few settings. It doesn't know about systems: every cartridge gets 7z or zip.
- `Core/Library/LibraryScanner.cs` `SystemOf()` works out a display string such as "Game Boy" or
  "PlayStation 2", from the extension, the folder name and disc headers. There are no system IDs.
- `Core/AppSettings.cs` saves settings as JSON (schema 2) in sections. Power-user options are shown
  only when `General.PowerUserMode` is on.

## 4. Functional requirements

### Registry and detection

- **FR-1 Registry.** One embedded data file, `Core/Formats/TargetFormats.json`, holds one entry per
  system:
  - `id`, a stable slug matching the **ID** column of the doc;
  - display name and category;
  - source extensions;
  - target (`format`, `container`: `bare` / `7z` / `zip` / `folder` / `leave`);
  - alternatives (each with a `producible` flag);
  - the rules that apply.
  
  The registry is the single source of truth. A test fails if `docs/target-formats.md` and the
  registry disagree on any system's ID, target or container.
- **FR-2 System detection.** `SystemOf()` returns a registry `id`, not a display string. It is
  extended to the systems it doesn't know yet, using the same order as today: header/magic, then
  extension, then folder name. If a file stays ambiguous (e.g. `.bin`), it is reported as
  *Unknown system* and left alone unless the user picks a system.
- **FR-3 Look inside archives.** For a .zip or .7z, the app reads the member list (using the
  existing `ArchivePeek`), plus the compression method of each member. It applies the rules to the
  contents, not to the archive.

### Status and conversion

- **FR-4 Status.** Every file shown on the Convert page and the Library page gets one of these
  statuses:

  | Status | Meaning |
  |---|---|
  | Optimal | Already in its target format and container |
  | Convert → *target* | The app can produce the target |
  | Recommended: *target* (not yet supported) | D8 systems; no Convert button |
  | Left as is | Arcade romsets (R5, D10), Dreamcast `.cdi` (D7), WBFS/CISO images (Q3), or a user override of *Leave as is* |
  | Unknown system | See FR-2 |

- **FR-5 Convert defaults.** `ConvertTargets.For()` takes the resolved target (FR-12) as the row's
  default. Its choice list is that target, plus the producible alternatives, plus *Leave as is*.
- **FR-6 Rules.** The app follows rules R1–R13 exactly as written in the guidelines. Specifically:
  - **R1, R8, D9:** a single ROM goes into its own 7z: LZMA2, Ultra, non-solid. An existing .7z
    whose member uses any method other than LZMA/LZMA2 is recompressed.
  - **R2, R3, R4:** archived disc images are extracted to the temp folder first, then converted to
    a bare CHD, WUA or RVZ (with the existing jobs).
  - **R5:** romsets are never touched. A file counts as a romset when its folder or DAT says MAME,
    FBNeo, NAOMI or Atomiswave.
  - **R6:** an archive that holds several games is split into one output per game.
  - **R9:** for the computer systems, 7z is converted to zip.
  - **R13:** CHD, RVZ, WUA, NSZ, XCZ and CSO files found inside an archive are extracted bare,
    never re-archived.
- **FR-7 Multi-disc (R7).** When two or more discs of one game are converted:
  1. Each disc's output goes into `<system folder>/<MultiDiscFolder>/` (default `hidden`).
  2. `<Game>.m3u` is written in the system folder, listing the discs by relative path with forward
     slashes, in disc order.
  3. Disc order comes from the `(Disc N)` tag in the names.
  4. An existing `.m3u` that points at the same discs is updated, not duplicated.
- **FR-8 PS2 and PSP (D3, D4).** PS2 DVD images use `createdvd`; PS2 CD games use `createcd` (the
  existing `IsoProbe`). PSP uses `createdvd` with 2048-byte hunks.
- **FR-9 3DS (D2).**
  - The output is the raw decrypted `<Game>.cci`, not packed into an archive (a decrypted `.3ds` is
    just renamed); it's never named `.3ds`.
  - lib-3ds-kit (`native/lib-3ds-kit`) converts what isn't a decrypted image first; the result is
    always a decrypted `.cci`, in its target container:
    - an encrypted image (NCCH flag `NoCrypto` not set) is decrypted, keeping its layout (trimmed or
      not), so the decrypted dump still matches its DAT entry;
    - a `.cia` of a game becomes a `.cci` (trimmed). An update or DLC `.cia` can't be a cartridge
      image and is left as is;
    - Azahar's `.zcci` and `.zcia` (Z3DS) are read directly, without unpacking first.
  - The targets are bare `.cci` (recommended) and `.zcci` (Azahar's Z3DS, which Azahar plays
    without unpacking). A 3DS game is never packed into a 7z or zip: neither is offered, a setting
    for every system that says 7z or zip doesn't apply, and a row that asks for one anyway is
    skipped. A 7z or zip holding one 3DS game is unpacked into the bare `.cci` (or `.zcci`),
    decrypted on the way when it needs to be; one holding several is left for unpacking first.
  - Keys are the user's own: `boot9.bin` and `aes_keys.txt` (and `seeddb.bin` for eShop titles),
    found in Azahar's and Citra's folders, or a folder chosen in Settings › Keys. Without them the
    job fails and says where they go; nothing is written.
  - The new file is checked against the hashes stored inside it (with *Check each file after
    converting*, or Replace the originals). The dump's own hashes are recorded (R11), and a `.zcci`
    or `.zcia` is hashed as the file it holds, so Verify & match still recognises both.
- **FR-12 Xbox.**
  - lib-xiso-kit (`native/lib-xiso-kit`) does every conversion; NoIntrovert only picks the output's
    name and folder, keeps the hashes, and removes the original when asked.
  - A Redump `.iso` (told apart from an XISO by where its XDVDFS header is) becomes an XISO: the game
    partition only, which xemu reads. An XISO is already optimal.
  - CCI (Cerbios) is the alternative, for modded consoles: compressed, and split below the FATX 4 GiB
    limit. An XISO can become a CCI too.
  - The conservative mode is used: files keep their original sectors, nothing is removed, no XBE is
    patched. lib-xiso-kit re-reads the new image and checks it before anything is replaced.
  - The Redump image's hashes are kept for Verify & match (R11), since an XISO can't match Redump.
- **FR-12b Xbox 360.**
  - An Xbox 360 disc is told from an original Xbox one by where its XDVDFS volume is (XGD2, XGD3 or
    the XGD2 hybrid), or, in an XISO, by a `default.xex` at its root.
  - The bare `.iso` (Redump or XISO) is the target and already optimal: Xenia plays it as it is.
  - `.zar` is the alternative, for Xenia Canary: lib-xiso-kit reads the disc's files and
    lib-wii-u-kit writes them into a ZArchive (Cemu's format). The archive is re-read (its own
    SHA-256 and every file's SHA-1 against the disc's) before it's moved into place. A `.zar` holds
    the files, not the disc, so it can't become the `.iso` again; the Redump hashes are kept (R11).
  - Xbox 360 packages (STFS or SVOD with a CON, LIVE or PIRS header: XBLA games, DLC, title updates,
    Games on Demand) are recognised by their header, since they usually have no extension, and are
    left as they are, which is what Xenia installs. A Games on Demand header takes its `.data`
    folder with it.
  - An XISO has the same name as its Redump image: with Replace the originals it takes the
    original's place once it's checked; otherwise it gets " (2)", like any taken name.
- **FR-11 Switch.**
  - lib-nx-kit (`native/lib-nx-kit`) does every conversion; NoIntrovert only picks the output's name
    and folder, and removes the original when asked.
  - `.nsp`, `.xci` and `.xcz` become `.nsz` by default. `.xcz` keeps a cartridge image a cartridge
    image. "Uncompressed" turns an `.nsz` back into an `.nsp` and an `.xcz` into an `.xci`.
  - Every content file (NCA) is checked against its metadata hash as it's written. An `.nsz` turned
    back into an `.nsp` is the original `.nsp` byte for byte: lib-nx-kit keeps the files' order and
    the header's padding.
  - A package holding several games becomes one file per game, named like
    `Game Name [0100…][v0].nsz`. Replace the originals removes the original only when every title
    in it was written.
  - Keys are the user's own `prod.keys` (and `title.keys` beside it), found where Ryujinx and the
    yuzu-family emulators keep them, or chosen in Settings › Keys. Without them the job fails, says
    where the keys go, and writes nothing.
- **FR-10 PS Vita.**
  - A NoNpDrm game folder (`sce_sys/param.sfo` present) becomes `<Title> (<TitleID>).zip`.
  - The title and title ID come from `param.sfo`.
  - When the launcher option is on, the app also writes `<Title> (<TitleID>)<ext>`, containing the
    title ID, next to the zip.
  - `.pkg` + zRIF conversion is a separate later requirement (D8).
- **FR-11 Hashes (R11) and headers (R12).**
  - **R11:** when an output's bytes differ from the No-Intro/Redump original, the app records the
    original's name, size, CRC32, MD5 and SHA-1 in the library index (Q1). Verify then checks a
    converted file against those values.
  - **R12:** header and byte-order fixes (`.v64`/`.n64` → `.z64`, removing the SNES copier header,
    de-interleaving `.smd`) run only when the setting is on.

### Overrides (Advanced settings)

- **FR-12 Resolving a target.** For each file the app uses the first of these that is set:
  1. a file-type override for this system;
  2. a system override;
  3. a file-type override for every system (`system: "*"`);
  4. the registry target.
  
  D1 then applies: when the DS size threshold is set and a `.nds` file is bigger, it stays bare.
- **FR-13 Settings model.** A new section is added to `AppSettings`. Old settings files load with
  its defaults, so the schema version doesn't change.

  ```csharp
  public TargetFormatSettings TargetFormats { get; set; } = new();

  public sealed class TargetFormatSettings
  {
      public List<TargetOverride> Overrides { get; set; } = [];
      public string MultiDiscFolder { get; set; } = "hidden";
      public bool NormalizeHeaders { get; set; }            // R12, D6
      public int DsBareAboveMb { get; set; }                // D1, 0 = off
      public bool RecordOriginalHashes { get; set; } = true; // R11
      public bool WriteVitaLauncher { get; set; }
      public string VitaLauncherExtension { get; set; } = ".psvita"; // Q2
  }

  public sealed record TargetOverride(
      string System,          // registry id, or "*" for every system
      string? Extension,      // ".nds", or null for the whole system
      string Target);         // "recommended", "leave", or an alternative id ("zip", "7z", "bare", "cso", ...)
  ```

  On load, an override that names an unknown system or target is ignored, and a warning is written
  to the log.
- **FR-14 Settings UI.** A new **TARGET FORMATS** section on the Settings page, inside its
  *Advanced* expander:
  - **Systems grid:** System | Recommended | Your choice | Reset.
    - The *Your choice* drop-down lists *Recommended*, *Leave as is*, and the system's alternatives.
      Alternatives the app can't produce yet are greyed out, labelled "(not yet supported)".
    - Overridden rows are marked.
    - A filter box searches by system name or extension.
  - **File-type rules:** a list of extension rules (system or "All systems", extension, target),
    with Add and Remove.
  - **Rule options:** multi-disc folder name; header and byte-order fixes; record original hashes;
    and the Vita launcher file (on/off plus extension). The DS bare-above size is a power-user
    option (shown when `PowerUserMode` is on).
  - **Buttons:** Reset all; Export overrides… / Import overrides… (JSON in the `TargetOverride`
    shape).
  - **Effect:** changes apply to new rows on the Convert page and to the Library statuses after a
    rescan. Jobs already running are not changed (they already use a `Clone()` of the settings).

### Docs

- **FR-15 Docs.**
  - `docs/target-formats.md` is the user-facing guide; it holds the rules, decisions and
    per-system table.
  - The README gets a short **Target formats** section that links to it.
  - The Settings section shows a link to the guide.
  - A test keeps the guide and the registry in step (FR-1).

## 5. Acceptance criteria

| # | Input | Settings | Expected |
|---|---|---|---|
| 1 | `Game.nes` (loose) | defaults | `Game.7z` (LZMA2, Ultra) holding `Game.nes` |
| 2 | `Game.zip` holding `Game.nes` | defaults | `Game.7z` holding `Game.nes` |
| 3 | `Game.7z` holding `Game.nes` (LZMA2) | defaults | Optimal, no job |
| 4 | `Game.7z` holding `Game.nes` (zstd) | defaults | Recompressed to LZMA2 |
| 5 | `Game.7z` holding `Game.wux` | defaults | Bare `Game.wua`; no archive left |
| 6 | `Game.zip` holding PS1 `.cue` + 3 `.bin` | defaults | Bare `Game.chd` |
| 7 | 3 PS1 discs `Game (Disc 1..3).cue/bin` | defaults | 3 CHDs in `PlayStation/hidden/` and `PlayStation/Game.m3u` listing them |
| 8 | GameCube `Game.iso` | defaults | Bare `Game.rvz` |
| 9 | C64 `Game.7z` holding `Game.d64` | defaults | `Game.zip` holding `Game.d64` |
| 10 | `mame/sf2.zip` | defaults | Left as is (arcade) |
| 11 | Decrypted `Game.3ds` | defaults | Bare `Game.cci` (the same bytes) |
| 11a | Decrypted `Game.3ds` | every system → `7z` | Bare `Game.cci`; no 7z is made |
| 12 | Encrypted `Game.3ds` | defaults | Bare decrypted `Game.cci`; without keys the job fails and says where keys go |
| 12a | `Game.cia` (a game) | defaults | Bare `Game.cci` |
| 12b | `Game.cia` | system `nintendo-3ds` → `zcci` | Bare `Game.zcci` |
| 12c | `Game.zcci` | defaults | Bare decrypted `Game.cci` |
| 12d | `Game.7z` holding an encrypted `Game.3ds` | defaults | Bare decrypted `Game.cci` |
| 13 | `Game.nds` | system `nintendo-ds` → `zip` | `Game.zip` |
| 14 | `Game.cso` (PSP) | `*` / `.cso` → `leave` | Left as is |
| 15 | `Game.cso` (PSP) | defaults | `Game.chd` (createdvd, 2048-byte hunks) |
| 16 | NoNpDrm folder `PCSE00000` | launcher on | `Title (PCSE00000).zip` + `Title (PCSE00000).psvita` |
| 17 | `Game.nsp` | defaults | `Game.nsz`; back to the same `Game.nsp`, byte for byte |
| 17b | `Game.xci` | defaults / `switch` → `xcz` | Defaults: `Game.nsz`. With the override: `Game.xcz` |
| 17c | `Game.nsp` | no `prod.keys` | The job fails and says where `prod.keys` goes; nothing is written |
| 17d | Redump Xbox `Game.iso` | defaults | XISO `Game.iso` (its Redump hashes kept); CHD isn't offered |
| 17e | Redump Xbox `Game.iso` | system `xbox` → `cci` | `Game.cci` (Cerbios), split below 4 GiB |
| 17f | Redump Xbox 360 `Game.iso` | defaults / `xbox-360` → `zar` | Defaults: optimal, `.zar` offered. With the override: `Game.zar` (its Redump hashes kept); CHD isn't offered |
| 17g | XBLA package `58410A5D…` (no extension) | defaults | Xbox 360, optimal, nothing offered |
| 18 | `Game.v64` | header fixes off / on | Off: `Game.7z` holding `Game.v64`. On: `Game.7z` holding `Game.z64` |
| 19 | Exported overrides imported on another PC | — | Same rows are marked as overridden |

## 6. Phases

1. **Registry, detection and status:** FR-1 to FR-5, FR-15. Plus R1, R2–R5, R8–R10 and R13 using
   the existing CHD, RVZ, WUA and archive jobs.
2. **Overrides:** FR-12 to FR-14.
3. **Per-system extras:** FR-7 (multi-disc), FR-8, FR-9 (3DS), FR-10 (Vita zip and launcher),
   FR-11 (hashes and header fixes), R6 (splitting).
4. **Later, a separate requirement each (D8):** Vita `.pkg` + zRIF, PS3.

## 7. Resolved questions

- **Q1 Where original hashes are kept (R11):** in the library index, not in sidecar files.
  Sidecar files would show up as junk in frontends that scan the folder. The hashes can be
  exported, as a DAT or CSV, from the Library page.
- **Q2 Vita launcher extension:** the default is `.psvita`, which is what ES-DE's PS Vita system
  expects (the review said `.vita`). It can be changed in Settings.
- **Q3 WBFS/CISO → RVZ:** deferred. These containers don't record the disc's exact size, and
  lib-wii-gamecube-kit (like Dolphin) refuses to convert them, so for now they are *Left as is* with no
  other choice. The plan for converting them, with a warning that the result won't match Redump,
  is in [Future.md](../../Future.md).
