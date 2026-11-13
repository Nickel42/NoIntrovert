# Target formats

What NoIntrovert recommends as the best format and container for each system's games, and why.
These are the defaults. Any of them can be changed in **Settings → Target formats** (see
[Overriding the defaults](#overriding-the-defaults)).

Researched 2026-10-19 from the libretro docs, the libretro-core-info files, each core's
`retro_get_system_info()` source and RetroArch's `tasks/task_content.c`. Reviewed and approved
2026-10-19.

## How the target is chosen

Each file is matched to a system (by DAT, header, extension or folder), then given that system's
target. A file is **Optimal** when it's already in its target format and container. Otherwise the
app offers to convert it.

**How RetroArch opens an archive.** RetroArch handles a .zip or .7z in one of three ways,
depending on the core:

| Core setting | What RetroArch does | Effect |
|---|---|---|
| `need_fullpath = false` | Decompresses the file straight into RAM | Archives work well. This covers most cartridge cores. |
| `need_fullpath = true` | Extracts the selected file to a temp folder on every launch | Slow for large files, and a cue+bin set inside one archive breaks |
| `block_extract = true` | Passes the archive to the core unchanged | Works only if the core reads that archive type itself (MAME, FBNeo, VICE, PUAE). Otherwise it fails (Dolphin, LRPS2, Flycast discs). |

That's why cartridges go in a .7z while disc images become bare CHD, RVZ or WUA.

## Rules

| Rule | When the input is… | Target |
|---|---|---|
| R1 | One cartridge ROM (loose, or alone in a .zip/.7z) | A .7z holding just that ROM, with its canonical extension (`Game.nes` in `Game.7z`) |
| R2 | A .zip/.7z holding a disc image (cue/bin, iso, img, gdi, ccd, mds) | Extract, then a bare `.chd` |
| R3 | A .zip/.7z holding a Wii U image (.wux/.wud), or loose Wii U files | Extract, then a bare `.wua`, with the update and DLC merged in when present |
| R4 | A GameCube/Wii image (.iso/.gcm/.gcz/.wia/.tgc), loose or archived | A bare `.rvz` (zstd level 5, 128 KiB blocks). .wbfs and .ciso are left as is: they don't record the disc's exact size, so they can't be converted yet (see [Future.md](../Future.md)). |
| R5 | An arcade romset (MAME, FBNeo, NAOMI, Atomiswave, Game & Watch) | Left as its .zip (TorrentZip). Never extracted or repacked; CHDs stay bare in the set folder. |
| R6 | One archive holding several unrelated games | Split into one file per game |
| R7 | A multi-disc game | One bare file per disc, plus an `.m3u` playlist; see [Multi-disc games](#multi-disc-games-r7) |
| R8 | Any archive the app writes | 7z uses LZMA2 only and zip uses Deflate only. RetroArch can't open zstd, PPMd or Deflate64. |
| R9 | Computer systems whose cores read zip themselves (C64, DOS, Amiga, ST, CPC, Atari 8-bit, MSX disks) | `.zip`, not .7z |
| R10 | A file already in its target format and container | Nothing to do (Optimal) |
| R11 | A target that changes the bytes on disk (CHD, RVZ, WUA, NSZ, XISO, ZAR, ZCCI) | The original No-Intro/Redump hashes are kept in the library index (no extra files), so the file can still be verified. RVZ, NSZ and CHD convert back exactly (an NSZ gives back its NSP byte for byte, file order and header included). WUA, XISO, ZAR and ZCCI don't. |
| R12 | A wrong byte order or copier header (.v64/.n64, .smc with a 512-byte header, interleaved .smd) | Converted to the No-Intro form (.z64, headerless .sfc, .md), then R1. Off unless turned on (Decision D6). |
| R13 | An already-compressed format (.nsz/.xcz/.chd/.rvz/.wua/.cso) | Never put inside a zip or 7z |

### Multi-disc games (R7)

The playlist sits in the system folder with the rest of that system's games. The discs move into a
subfolder named `hidden` in the same folder, so a frontend lists the game once:

```
PlayStation/
  Final Fantasy VII (USA).m3u
  hidden/
    Final Fantasy VII (USA) (Disc 1).chd
    Final Fantasy VII (USA) (Disc 2).chd
    Final Fantasy VII (USA) (Disc 3).chd
```

The `.m3u` lists the discs by relative path (`hidden/Final Fantasy VII (USA) (Disc 1).chd`), one
per line, in disc order. The folder name can be changed in Settings.

**In the Cleanup wizard's ES-DE format** (docs/requirements/cleanup-wizard.md, FR-6) it's the same,
in `roms/<system>/`: the `.m3u` directly in the system folder and the discs in `hidden/` there. An
empty `noload.txt` in `hidden/` tells ES-DE to skip that folder, so the discs aren't listed as games
of their own beside the playlist (ES-DE user guide, "noload.txt"):

```
roms/
  psx/
    Final Fantasy VII (USA).m3u
    hidden/
      noload.txt
      Final Fantasy VII (USA) (Disc 1).chd
      Final Fantasy VII (USA) (Disc 2).chd
      Final Fantasy VII (USA) (Disc 3).chd
```

## Decisions

| ID | Question | Decision |
|---|---|---|
| D1 | Large Nintendo DS ROMs (256–512 MB) | Same as other cartridges: .7z. Power users can set a size above which .nds files stay bare (off by default). |
| D2 | Nintendo 3DS | A bare decrypted `.cci`, the raw file, not in an archive. The output is never named `.3ds`: `.3ds` files are renamed to `.cci`. Encrypted images, `.cia` games, Azahar's `.zcci`/`.zcia` and 3DS games in a 7z or zip are converted by lib-3ds-kit with the user's own keys. `.zcci` (Azahar only) is the one alternative. A 3DS game is never packed into a 7z or zip: that isn't a supported way to keep one, and a setting for every system that says 7z or zip doesn't apply to it. |
| D3 | PSP | `.chd` made with `createdvd` and 2048-byte hunks. Keep `.cso` with an override if you also play on a real PSP. |
| D4 | PS2 CD vs DVD | Detected automatically: `createdvd` for DVD images, `createcd` for CD-based games |
| D5 | Computer systems | `.zip` (Rule R9) |
| D6 | Header and byte-order fixes (R12) | Off by default; turned on in Settings |
| D7 | Dreamcast `.cdi` | Left as is; chdman can't convert DiscJuggler images reliably |
| D8 | Systems the app can't convert to yet (PS3, PS Vita) | For now the app shows the recommendation without a Convert button. A converter for each system comes later as its own requirement. |
| D9 | 7z settings | LZMA2, Ultra (`-mx9`), non-solid for a single file |
| D10 | Arcade (MAME/FBNeo) | Out of scope: reported only, never changed |

### Notes from the review

- **Switch:** `.nsz` is the default for everything. NSZ and XCZ are just zstd-compressed NSP and
  XCI. Updates and DLC only exist as NSP, so making the base game `.nsz` too keeps one format
  throughout. An XCI made into an NSZ loses the cartridge-only data (the card header), so the
  result can't be turned back into the No-Intro XCI. Choose `.xcz` in Settings to keep cartridge
  dumps as cartridge images. lib-nx-kit does the conversions, with the user's own `prod.keys`.
- **PS Vita:** the target is a `.zip` of a properly dumped NoNpDrm game folder, which Vita3K installs
  with File → Install .zip. A `.pkg` plus its zRIF key converts to that folder first (pkg2zip). As
  an option, the app also writes a small frontend launcher file named after the game that holds its
  title ID.

## Per-system targets

"Alternatives" lists the other targets you can pick for a system in Settings. **ID** is the key
used in the settings file.

### Cartridge

| System | ID | Target | Container | Convert from | Alternatives (override) | Notes |
|---|---|---|---|---|---|---|
| NES / Famicom | `nes-famicom` | .nes | 7z | Headerless .nes (No-Intro headerless DAT) → add iNES/NES 2.0 header; keep .unf/.unif only when no iNES equivalent | Single-file .zip (Deflate) | No-Intro publishes headered and headerless DATs; emulators need the header, so store headered .nes. |
| Famicom Disk System | `famicom-disk-system` | .fds | 7z | Headerless .fds → add 16-byte fwNES header if emulator requires; .qd → .fds | Single-file .zip (Deflate) | Requires disksys.rom BIOS. Disk writes go to a separate save file, so 7z is safe. Check headered vs headerless No-Intro DAT. |
| SNES / Super Famicom (incl. Satellaview .bs, Sufami Turbo .st) | `snes-super-famicom` | .sfc | 7z | .smc/.swc/.fig with 512-byte copier header → strip header → .sfc | Single-file .zip (Deflate) | MSU-1 packs (.msu + .pcm + manifest) must stay a bare folder. Sufami Turbo needs BIOS via subsystem. |
| Nintendo 64 | `nintendo-64` | .z64 | 7z | .v64 (byteswapped) / .n64 (little-endian) → .z64 (big-endian, No-Intro canonical) | Single-file .zip (Deflate) | Cores auto-detect byte order, but DAT/CRC matching requires .z64. 7z fine (max 64MB, decompresses in ~1s). ares standalone reads zip but not 7z. |
| Nintendo 64DD | `nintendo-64dd` | .ndd | 7z | .d64 (MAME format) → .ndd where a No-Intro match exists | Single-file .zip (Deflate) | Needs 64DD IPL BIOS. Disk writes stored separately. |
| Game Boy | `game-boy` | .gb | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | Battery saves are separate files. |
| Game Boy Color | `game-boy-color` | .gbc | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| Game Boy Advance | `game-boy-advance` | .gba | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | Saves are separate .srm/.sav named after archive basename - 7z is fine. mGBA standalone reads zip/7z too. |
| Nintendo DS | `nintendo-ds` | .nds | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate); bare .nds for 256-512MB titles if launch latency matters | Large ROMs (up to 512MB) take several seconds to decompress from LZMA 7z every launch; zip (deflate) decompresses several times faster at ~10% worse ratio. Saves separate. Do not trim (breaks No-Intro CRC). |
| Nintendo DSi (enhanced carts & DSiWare) | `nintendo-dsi` | .nds (decrypted) | 7z | Encrypted DSiWare .app/.tad → decrypted .nds (No-Intro 'DSi (Decrypted)' / 'DSi (Digital)') | Single-file .zip (Deflate) | DSi mode needs DSi BIOS + NAND image (NAND must stay bare). Full-fidelity DSiWare usually installed to NAND. |
| Nintendo 3DS | `nintendo-3ds` | .cci (decrypted) | bare | Encrypted .3ds/.cci → decrypted; .3ds renamed to .cci (never .3ds); .cia games → .cci; a game in a 7z or zip → unpacked .cci | .zcci (Azahar only) | Must be decrypted (Azahar dropped encrypted support). Never output .3ds. Never packed into a 7z or zip. |
| Virtual Boy | `virtual-boy` | .vb | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| Pokemon Mini | `pokemon-mini` | .min | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| Game & Watch / LCD handhelds | `game-watch-lcd-handhelds` | .zip (MAME romset) | zip | - | 7z romset (MAME supports); .mgw for gw core | Never extract/repack per file. .mgw (MADrigal) is a different format, not interchangeable. |
| Switch | `switch` | .nsz | bare | .nsp → .nsz; .xci → repack to .nsp → .nsz; update & DLC .nsp → .nsz | .xcz (keeps a cartridge XCI as an exact compressed copy); .nsp/.xci uncompressed | NSZ/XCZ are just zstd-compressed NSP/XCI. NSZ is the default so the base game, update and DLC share one format. An NSZ made from an XCI can't be turned back into the No-Intro XCI (R11 keeps the original hashes); choose .xcz to keep cartridge dumps exact. Needs prod.keys. Never wrap in 7z. |
| Master System / Mark III | `master-system-mark-iii` | .sms | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| Game Gear | `game-gear` | .gg | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| SG-1000 / SC-3000 | `sg-1000-sc-3000` | .sg | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| Mega Drive / Genesis | `mega-drive-genesis` | .md | 7z | .smd (interleaved) → de-interleave to .md; .gen/.bin → .md | Single-file .zip (Deflate) | BlastEm (need_fullpath=true) extracts to temp - still works. |
| 32X | `32x` | .32x | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| Pico | `pico` | .md | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| Atari 2600 | `atari-2600` | .a26 | 7z | .bin → .a26 (rename) | Single-file .zip (Deflate) | - |
| Atari 5200 | `atari-5200` | .a52 | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| Atari 7800 | `atari-7800` | .a78 (headered) | 7z | Headerless .bin → add 128-byte A78 header (No-Intro headered DAT) | Single-file .zip (Deflate) | - |
| Atari Lynx | `atari-lynx` | .lnx (headered) | 7z | .lyx (headerless) → .lnx via make_lnx / headered DAT | Single-file .zip (Deflate) | Header carries rotation/bank info; needs lynxboot.img. |
| Atari Jaguar | `atari-jaguar` | .j64 | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | Virtual Jaguar is low-compat. |
| PC Engine / TurboGrafx-16 | `pc-engine-turbografx-16` | .pce | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| PC Engine SuperGrafx | `pc-engine-supergrafx` | .sgx | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| Neo Geo Pocket / Color | `neo-geo-pocket-color` | .ngp / .ngc | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| WonderSwan / Color / SwanCrystal | `wonderswan-color-swancrystal` | .ws / .wsc | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| ColecoVision | `colecovision` | .col | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | Needs colecovision.rom. |
| Intellivision | `intellivision` | .int | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | Needs exec.bin/grom.bin. |
| Vectrex | `vectrex` | .vec | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| Odyssey2 / Videopac(+) | `odyssey2-videopac` | .bin | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | Needs o2rom.bin. |
| Channel F | `channel-f` | .bin | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | Needs sl31253.bin/sl31254.bin. |
| Supervision | `supervision` | .sv | 7z | Loose ROM, single-file .zip (Rule R1) | Single-file .zip (Deflate) | - |
| Arcadia 2001 (and clones) | `arcadia-2001` | .bin in 7z (archival); MAME softlist .zip for play | 7z (archive) / zip (MAME softlist) | - | zip | No-Intro naming != MAME softlist naming. |
| MSX / MSX2 / MSX2+ / turboR | `msx-msx2-msx2-turbor` | .rom (carts) in 7z; .dsk/.cas in zip | 7z (carts) / zip (disks) | Multi-disk → .m3u of disks | zip for everything | blueMSX needs Machines/Databases folders. Disk images the game writes to must stay bare or writes are lost with the temp copy. |

### Computer

| System | ID | Target | Container | Convert from | Alternatives (override) | Notes |
|---|---|---|---|---|---|---|
| Commodore 64 / 128 / VIC-20 / PET | `commodore-64-128-vic-20-pet` | .d64/.g64/.crt/.tap in .zip | zip | .nib → .g64 (nibconv); 7z → zip | bare; .m3u for multi-disk | Override any global 7z default to zip for C64. Disks written to (saves) should be bare. |
| Amiga (ADF floppies / WHDLoad) | `amiga` | .adf in .zip; WHDLoad .lha bare | zip / bare (.lha) | .dms → .adf (xdms); multi-disk → .m3u | 7z (PUAE lists 7z); .adz (gzipped ADF) | IPF needs capsimg. Standalone FS-UAE/Amiberry handle zip more reliably than 7z. |
| Amiga CD32 / CDTV | `amiga-cd32-cdtv` | .chd | bare | .cue/.bin, .iso → .chd | cue/bin | Needs CD32/CDTV Kickstart + extended ROM. UNSURE on FS-UAE/Amiberry CHD support per version. |
| ZX Spectrum / ZX81 | `zx-spectrum-zx81` | .tzx/.tap/.z80 in .7z or .zip | 7z or zip | - | bare | +3 .dsk written to loses changes when archived. |
| Amstrad CPC / GX4000 | `amstrad-cpc-gx4000` | .dsk/.cdt in .zip | zip | Multi-disk → .m3u | bare; 7z | Disk writes lost when loaded via temp extraction. |
| Atari ST / STE / Falcon | `atari-st-ste-falcon` | .st/.msa/.stx in .zip | zip | - | bare | Needs tos.img. |
| Atari 8-bit (400/800/XL/XE) | `atari-8-bit` | .atr/.xex/.car in .zip | zip | .xfd → .atr (optional) | 7z (works via temp extract) | Needs ATARIXL.ROM etc. |
| PC-88 / PC-98 | `pc-88-pc-98` | .d88 in .zip; HDD images bare | zip / bare | - | bare | Needs BIOS sets. |
| X68000 / X1 | `x68000-x1` | .dim/.xdf in .zip; .hdf bare | zip / bare | Multi-disk → .m3u | bare | Needs iplrom/cgrom. |
| MS-DOS | `ms-dos` | .zip (game folder) / .dosz; CD-ROM → .chd | zip | Installed game folder → zip; CD .cue/.bin/.iso → .chd | folder; .dosz | Standalone DOSBox needs extracted folder. |
| ScummVM-supported adventure games | `scummvm-supported-adventure-games` | folder | folder | - | - | Keep extracted. |

### Arcade

| System | ID | Target | Container | Convert from | Alternatives (override) | Notes |
|---|---|---|---|---|---|---|
| MAME (arcade) | `mame` | .zip romset (+ CHD in <set>/ subfolder) | zip (TorrentZip); CHDs bare in set folder | Romsets from other MAME versions → rebuild | 7z romsets (MAME supports; slower, little gain) | NEVER repack to 7z per-file or single-file archives; never extract. Romset version must match the core (mame2003_plus = 0.78 sets). MAME CHDs stay as distributed. |
| FinalBurn Neo | `finalburn-neo` | .zip romset | zip (TorrentZip) | Romsets from other emulators → rebuild | 7z (FBNeo reads 7z) | Do not repack per file. Neo Geo needs neogeo.zip. |
| NAOMI / NAOMI 2 / Atomiswave | `naomi-naomi-2-atomiswave` | .zip (MAME naming) + .chd for GD-ROM titles | zip; CHD alongside | - | 7z | Needs naomi.zip / awbios.zip. |

### Disc

| System | ID | Target | Container | Convert from | Alternatives (override) | Notes |
|---|---|---|---|---|---|---|
| PlayStation | `playstation` | .chd | bare | .cue/.bin, .ccd/.img/.sub, .iso, .ecm (unecm first) → .chd; multi-disc → .m3u | PBP (PSN eboots; DuckStation & PCSX ReARMED support) | LibCrypt titles: keep redump .sbi beside the .chd with same basename. Multi-disc: .m3u listing CHDs. CCD subchannel is dropped (use .sbi). |
| PlayStation 2 | `playstation-2` | .chd | bare | DVD .iso → chd (createdvd); CD-based .cue/.bin → chd (createcd); .cso/.zso → iso → chd | CSO/ZSO (PCSX2 + OPL on real PS2); bare .iso | Use createdvd for DVD images; createcd-made CHDs of DVD ISOs perform badly or fail in PCSX2. OPL on real PS2 needs ISO/ZSO. |
| PlayStation 3 | `playstation-3` | Decrypted disc folder (PS3_GAME layout); PSN installed via .pkg + .rap | folder | .iso (Redump, encrypted) → decrypt with .dkey → extract to folder | Decrypted ISO (UNSURE - recent RPCS3 ISO loading) | Keep encrypted ISO + dkey separately if Redump verification matters. |
| PlayStation Portable | `playstation-portable` | .chd | bare | .iso/.cso → .chd; PSN .pkg → .iso/.pbp (pkg2zip) | CSO (maxcso) for real PSP / Adrenaline / older PPSSPP; .pbp for PSN titles | Use createdvd with 2048-byte hunks; createcd/default-hunk CHDs load slower. Keep CSO if also used on real hardware. |
| PlayStation Vita | `playstation-vita` | .zip of a NoNpDrm game folder | zip | .pkg + zRIF → NoNpDrm folder (pkg2zip) → .zip; loose NoNpDrm folder → .zip | Bare NoNpDrm folder; .vpk | Vita3K installs it with File > Install .zip. Optionally writes a frontend launcher file named after the game holding its title ID (e.g. PCSE00000). |
| Mega-CD / Sega CD | `mega-cd-sega-cd` | .chd | bare | .cue/.bin, .iso+wav → .chd | cue/bin | Needs region BIOS. |
| Saturn | `saturn` | .chd | bare | .cue/.bin, .ccd → .chd; multi-disc → .m3u | cue/bin (Mednafen standalone has no CHD) | Mednafen standalone does not read CHD. ST-V arcade = MAME zip. |
| Dreamcast | `dreamcast` | .chd | bare | .gdi, Redump .cue/.bin → .chd | bare .gdi set in folder; .cdi for TOSEC/homebrew | Leave .cdi as-is (chdman cannot reliably convert DiscJuggler). Re-convert old Dreamcast CHDs made with early chdman (track-offset bugs). |
| PC Engine CD / TurboGrafx-CD | `pc-engine-cd-turbografx-cd` | .chd | bare | .cue/.bin, .ccd → .chd | cue/bin | Needs syscard3.pce. |
| PC-FX | `pc-fx` | .chd | bare | .cue/.bin → .chd | cue/bin | Needs pcfx.rom. |
| Neo Geo CD | `neo-geo-cd` | .chd | bare | .cue/.bin → .chd | cue/bin | Needs neocd BIOS. |
| 3DO | `3do` | .chd | bare | .cue/.bin, .iso → .chd | cue/bin, .iso | Needs panafz10.bin etc. |
| CD-i | `cd-i` | .chd | bare | .cue/.bin → .chd | cue/bin | For MAME softlist use, CHD goes in cdi/<setname>/. |
| Jaguar CD | `jaguar-cd` | .chd (UNSURE) | bare | .cue/.bin → .chd | cue/bin (safest); .cdi | UNSURE: verify BigPEmu CHD support; cue/bin is the safe default. |
| Playdia | `playdia` | .chd | bare | .cue/.bin → .chd | cue/bin | Not playable in mainstream emulators. |
| Other Redump CD/DVD systems (FM Towns, PC-98 CD, Pippin, Mac, PC CD-ROM, arcade CDs) | `other-redump-cd-dvd-systems` | .chd | bare | .cue/.bin, .iso → .chd | cue/bin, iso | Many standalone emulators (e.g. Tsugaru) need cue/bin. |
| GameCube | `gamecube` | .rvz | bare | .iso/.gcm/.gcz/.wia/.tgc → .rvz; .ciso can't be converted; .nkit.iso → restore to .iso with NKit first | bare .iso (Nintendont/Swiss on real hardware) | Nintendont/Swiss cannot read RVZ. NKit is lossy/irreversible without NKit tool - don't use as source. |
| Wii (discs) | `wii` | .rvz | bare | .iso/.gcz/.wia → .rvz; .wbfs/.ciso can't be converted | bare .wbfs/.iso (USB loaders) | .wbfs and .ciso don't record the disc's exact size, so lib-wii-gamecube-kit (like Dolphin) won't convert them and they're left as is. Convert from the ISO. |
| WiiWare / Virtual Console (Wii digital) | `wiiware-virtual-console` | .wad | bare | - | 7z for cold storage only | Install to emulated NAND for save compatibility. |
| Wii U | `wii-u` | .wua | bare | .wux/.wud (a .wux inside .7z must be extracted first) + update + DLC → one .wua; NUS/loadiine folder → .wua | .wux (disc-verifiable compressed image); loadiine folder | Converting encrypted .wud/.wux needs keys.txt/disc key. Cemu cannot launch from .7z. WUA is not Redump-verifiable - keep hash map or the .wux. |
| Xbox | `xbox` | .iso (XISO game partition) | bare | Redump .iso → XISO; XISO → CCI on request | CCI for Cerbios (modded hardware only - not xemu), split below 4 GiB | xemu supports no compressed format. XISO is not Redump-verifiable, so the Redump hashes are kept (R11). lib-xiso-kit trims only: files keep their sectors, no XBE is patched. |
| Xbox 360 | `xbox-360` | .iso | bare | Redump or XISO .iso → .zar on request | .zar (Xenia Canary); extracted XEX folder; GOD | Xenia plays the .iso directly. Xenia Canary reads ZArchive (.zar): lib-xiso-kit reads the disc's files and lib-wii-u-kit writes the archive; it holds the files, not the disc, so the Redump hashes are kept (R11). XBLA, DLC, title update and Games on Demand packages (CON/LIVE/PIRS, usually no extension) are recognised by their header and left as is. Don't archive ISOs. |


## Overriding the defaults

**Settings → Target formats** (in the Advanced section) lets you change the target for a whole
system or for one file type:

- **By system:** *Recommended* (the table above), *Leave as is*, or any alternative listed for that
  system that NoIntrovert can produce. Alternatives it can't produce yet are shown greyed out.
- **By file type:** the same choices for one extension, either within a system (for example, Nintendo DS
  `.nds` → zip) or for every system (for example, `.cso` → leave as is).
- The most specific setting wins: a file type within a system, then the system, then a file type
  for every system, then the recommended target.
- Rows you've changed are marked, and you can reset one row or all of them.
- Overrides are saved in `settings.json` and can be exported and imported as JSON.

The rule options are in the same place: the multi-disc folder name (default `hidden`), header and
byte-order fixes (R12), the size above which large DS ROMs stay bare (D1), hash sidecars (R11), and
the PS Vita launcher file.
