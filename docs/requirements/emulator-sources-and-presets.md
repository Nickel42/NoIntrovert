# NoIntrovert: emulator data sources, three open questions, and per-system presets

Research date: 2 November 2026. Every claim has a link. **Unverified** means I could not confirm it from a primary source.
I read the NoIntrovert source but did not change it.

---

## 0. What NoIntrovert supports today (from its source)

| Fact | Where |
|---|---|
| Disc systems and their targets live in a data file (`TargetFormats.json`). Each entry already has an `esde` key holding the ES-DE system id (for example `psx`, `ps2`, `gc`). | `NoIntrovert/Core/Formats/TargetFormats.json` |
| Formats the app can **produce**: CHD (chdman `createcd` / `createdvd`), RVZ, WUA, 7z/zip, and CHD → cue/bin, gdi or iso. `pbp` (PS1, PSP) and `cso` (PS2, PSP) are listed as alternatives with `"producible": false`. The app can **read** PBP, CSO/ZSO and ISZ but can't write them. | `TargetFormats.json`; `Core/Formats/PbpExtractor.cs`, `CsoDecoder.cs` |
| The bundled chdman is built from MAME tag `mame0289`. Its default codecs are `cdlz, cdzl, cdfl` for CD and `lzma, zlib, huff, flac` for DVD/raw. **Zstandard is never chosen by default.** The CHD presets leave compression empty (`Compression = ""` means chdman's default), so every CHD the app makes today is a non-Zstandard CHD. | `native/lib-chd-kit/NOTES.md`; `native/lib-chd-kit/src/chdman_core.cpp` lines 71–74; `Core/Presets/ActionPresets.cs` `Chd()` |
| Settings that the existing presets pin down: `Chd.Compression`, `Chd.HunkSize`, `Chd.IsoMode`, `Chd.VerifyAfterCreate`, `Output.RemoveOriginals`; RVZ: `Nintendo.Compression` (Zstd), `CompressionLevel` (1/5/19), `BlockSize` (128 KiB), `Scrub` (false); extract: `Chd.ExtractFormat`, `Chd.SplitBin`. | `Core/Presets/ActionPresets.cs` |
| GameCube/Wii have a `NintendoTarget.Chd` option. **Dolphin has no CHD reader** (see 3.9), so a GameCube/Wii CHD can't be played in Dolphin. | `Core/Formats/TargetResolver.cs` line 113 |

Systems covered below: PlayStation, PS2, PSP, Dreamcast, Saturn, Mega-CD/Sega CD, PC Engine CD, GameCube, Wii and 3DO. The registry also has PC-FX, Neo Geo CD, CD-i, Jaguar CD, Amiga CD32 and others. Those follow the Sega CD / PC Engine CD pattern: CHD ★ and cue/bin.

---

## 1. Machine-readable sources for standalone emulators

### 1.1 Candidates compared

| Source | Format | Fields | Per-emulator extensions? | Windows-specific? | Update cadence | Licence / can we bundle a snapshot? |
|---|---|---|---|---|---|---|
| **ES-DE** `resources/systems/windows/es_systems.xml` + `es_find_rules.xml` ([GitLab](https://gitlab.com/es-de/emulationstation-de/-/tree/master/resources/systems/windows)) | XML | `es_systems.xml`: `<system>` with `<name>` (e.g. `psx`), `<fullname>`, `<path>`, `<extension>` (one list for the **whole system**), one `<command label="DuckStation (Standalone)">%EMULATOR_DUCKSTATION% -batch %ROM%</command>` per emulator, `<platform>`, `<theme>`. `es_find_rules.xml`: `<emulator name="DUCKSTATION">` with `<rule type="systempath|staticpath|winregistrypath">` and `<entry>` exe names and paths. | **No.** One extension list per system. The list is the union for all of that system's emulators. | Yes. There are separate files for windows, linux, macos, android and others. Labels say "(Standalone)" or name a RetroArch core. | Very active. The lead dev committed changes to the Windows `es_systems.xml` on 2, 4, 5, 15 and 22 Sep 2026 ([commit history via mirror](https://github.com/RetroDECK/ES-DE/commits/retrodeck-main/resources/systems/windows/es_systems.xml)). | **MIT** ([mirror repo metadata: MIT](https://github.com/RetroDECK/ES-DE); upstream [LICENSE](https://gitlab.com/es-de/emulationstation-de/-/blob/master/LICENSE)). You can bundle a snapshot if you keep the copyright notice. |
| **Batocera** `package/batocera/emulators/<emu>/<emu>.emulator.yml` (+ `*.core.yml`, `es_systems.yml`) ([format doc](https://github.com/batocera-linux/batocera.linux/blob/master/python-src/batocera-es-system/FORMATS.md)) | YAML 1.2 | `systems:` (system ids), `file_extensions:` (per emulator, per core, or per system, with `add`/`remove` overlays), features. `es_systems.yml` holds catalog metadata only. | **Yes.** For example [`pcsx2.emulator.yml`](https://github.com/batocera-linux/batocera.linux/blob/master/package/batocera/emulators/pcsx2/pcsx2.emulator.yml) lists `iso mdf nrg bin img dump gz cso chd zso`, and [`ppsspp.emulator.yml`](https://github.com/batocera-linux/batocera.linux/blob/master/package/batocera/emulators/ppsspp/ppsspp.emulator.yml) lists `iso cso pbp chd zip`. | No, it's Linux builds. Extension support is mostly the same across OSes, but there are no Windows-only emulators (ePSXe, SSF, Demul, Redream for Windows). | Very active (repo pushed 2 Nov 2026, [GitHub](https://github.com/batocera-linux/batocera.linux)). | **LGPL-3.0-or-later**, and the notice must be kept ([LICENSE-NOTICE](https://github.com/batocera-linux/batocera.linux/blob/master/LICENSE-NOTICE)). Bundling data files is allowed with attribution and the licence text. Treat the snapshot as LGPL content and ship it as a separate, replaceable file. |
| **RetroBat** `system/templates/emulationstation/es_systems.cfg` ([GitHub](https://github.com/RetroBat-Official/retrobat/blob/main/system/templates/emulationstation/es_systems.cfg)) | XML (Batocera-ES dialect) | `<system>` with `<name>`, `<extension>` (whole system), `<emulators><emulator name="duckstation"/>…<emulator name="libretro"><cores><core>…` | **No** (system union). For example `psx` lists `mednafen` with an extension list that includes `.chd`, which standalone Mednafen can't open. | **Yes**, it's a Windows distribution. | Very active (pushed 1 Nov 2026). | **LGPL v3** for the team's code, plus a non-commercial and no-resale clause ([license.txt](https://github.com/RetroBat-Official/retrobat/blob/main/license.txt)). Usable for a free app. The clause makes it a weaker choice than MIT. |
| **EmuDeck** ([GitHub](https://github.com/dragoonDorise/EmuDeck), [Windows edition](https://github.com/EmuDeck/emudeck-we)) | Shell / PowerShell install scripts plus ES-DE and Pegasus configs | No standalone database. It installs emulators and reuses ES-DE's files. | No | Partly | Active | GPL-3.0 (repo metadata). Not a data source. |
| **Pegasus** ([GitHub](https://github.com/mmatyas/pegasus-frontend)) | Per-collection `metadata.pegasus.txt` written by the user | No emulator catalog | No | n/a | Active | Licence not stated by GitHub's API (NOASSERTION). Not useful. |
| **LaunchBox** | Proprietary `Metadata.zip` / `Metadata.xml` from its Games DB ([forum](https://forums.launchbox-app.com/topic/33370-where-is-the-gamess-metadata-stored/)) | Emulator and platform entries exist, but their element names are **unverified** | Unverified | Yes | Unverified | Proprietary, no redistribution licence found. **Don't bundle.** |
| **Emulation General Wiki** ([site](https://emulation.gametechwiki.com/)) | MediaWiki prose and tables | Good human comparisons, no schema | Prose only | n/a | Active | Licence page returned 403, so **unverified**. Not machine-readable. Use it for research only. |
| **Wikidata** | RDF/SPARQL | `P1072` "readable file format", `P400` platform | Barely. Of 91 items typed "video game console emulator" (Q1196126), **7** have any P1072 value, and [DuckStation (Q114472130)](https://www.wikidata.org/wiki/Q114472130) has none (SPARQL count run 2 Nov 2026 on [query.wikidata.org](https://query.wikidata.org/)). | n/a | Community | **CC0** ([Wikidata licensing](https://www.wikidata.org/wiki/Wikidata:Licensing)). Fine to bundle, but too sparse to use. |

### 1.2 The gap no source fills

None of these sources records codec-level facts: whether an emulator reads Zstandard CHDs, PBP made by a given tool, or CSO v2/ZSO. ES-DE and RetroBat don't even record extensions per emulator. Those facts come only from each emulator's source code (section 2 and 3).

### 1.3 Recommendation

1. **Primary: ES-DE `es_systems.xml` (Windows) + `es_find_rules.xml`, MIT.**
   - The registry already stores ES-DE system ids (`esde` key), so the join is free.
   - It's Windows-specific, labels each command "(Standalone)" or a RetroArch core, and is maintained almost weekly by one careful maintainer.
   - `es_find_rules.xml` also tells the app how to **detect installed emulators**: exe names, `%ESPATH%\Emulators\…` static paths, registry keys.
   - Bundle a dated snapshot and refresh it at build time from GitLab. GitLab rate-limited me (HTTP 429) during this research. The [RetroDECK GitHub mirror](https://github.com/RetroDECK/ES-DE/tree/retrodeck-main/resources/systems/windows) carries the upstream author's commits and works as a CI fallback, but it is a "RetroDECK Custom Build" branch, so prefer upstream.
2. **Fallback / extension detail: Batocera `*.emulator.yml` `file_extensions`, LGPL-3.0.**
   - Use it to narrow ES-DE's per-system union down to a per-emulator list.
   - Map emulator names with a small alias table (ES-DE `DUCKSTATION` ↔ Batocera `duckstation`).
   - Windows-only emulators (ePSXe, SSF, Demul, Redream on Windows) will have no Batocera entry. Show the system-level list for them with a "per-emulator formats unknown" note.
3. **A small, cited "capabilities" overlay file maintained alongside the app**, for facts no catalog has (Zstandard CHD, "no CHD"). Each row carries the URL of the source line it came from. Most rows can be refreshed by a CI script that downloads each emulator's bundled `libchdr_chd.c` and greps for `cdzs`, which is exactly how section 2 was verified. This keeps the facts as data with provenance instead of logic.

---

## 2. The three unknowns

### 2.1 Does standalone DuckStation open Zstandard CHDs (`cdzs`)? **Yes.**
- DuckStation vendors libchdr in [`dep/libchdr`](https://github.com/stenzek/duckstation/tree/master/dep/libchdr). Its current [`src/libchdr_chd.c`](https://github.com/stenzek/duckstation/blob/master/dep/libchdr/src/libchdr_chd.c) `#include <zstd.h>` (line 51) and implements `zstd_codec_decompress` and `cdzs_codec_decompress` (lines 401–425). Its [CMakeLists](https://github.com/stenzek/duckstation/blob/master/dep/libchdr/CMakeLists.txt) links `zstd::libzstd_shared`.
- The codec arrived in commit [`0e6a9f637b` "dep/libchdr: Rebase to upstream 2a1119c"](https://github.com/stenzek/duckstation/commit/0e6a9f637b) on **18 Feb 2024**. The file at the previous libchdr bump ([`4b70853daa`](https://github.com/stenzek/duckstation/commit/4b70853daa), Aug 2023) has 0 `cdzs` references; at 0e6a9f637b it has 32.
- So any DuckStation build from **after 18 Feb 2024** reads `cdzs`/`zstd` CHDs. Older builds don't.
- Side note: DuckStation's licence is now **CC BY-NC-ND 4.0** ([LICENSE](https://github.com/stenzek/duckstation/blob/master/LICENSE)). That doesn't affect NoIntrovert, which only links to it.

### 2.2 Does standalone DuckStation open plain `.iso` for PS1? **It opens the file, but most PS1 games won't run.**
- [`src/util/cd_image.cpp`](https://github.com/stenzek/duckstation/blob/master/src/util/cd_image.cpp) lines 84–88 send `.bin`, `.img`, `.iso` and `.ecm` to `OpenBinImage`.
- In [`cd_image_cue.cpp`](https://github.com/stenzek/duckstation/blob/master/src/util/cd_image_cue.cpp) (around lines 880–910), the single-file opener reads the first 16 bytes. If it doesn't find a raw-sector sync header, it **assumes Mode 1 / 2048-byte sectors**.
- PS1 discs are Mode 2 / XA. A 2048-byte ISO has no XA subheaders, no Form 2 sectors (FMV/streamed audio) and no CD-DA tracks. So the file loads, but most commercial games break or lose music.
  - The "most games" part is an **inference** from the sector format. I didn't find a DuckStation document that states it.
- DuckStation's [README](https://github.com/stenzek/duckstation/blob/master/README.md) (line 39) lists bin/cue, CHD, single-track ECM, MDS/MDF, CCD and unencrypted PBP, and doesn't mention ISO. This supports the ⚠ on "ISO · Data track only".

### 2.3 Does standalone Mednafen open `.chd`? **No, including 1.30, 1.31 and 1.32.x.**
- The [Mednafen documentation](https://mednafen.github.io/documentation/) ("Compact Disc Images") says: Mednafen supports "CUE" sheets, CloneCD "CCD/IMG/SUB", and cdrdao "TOC" files. CHD isn't mentioned anywhere in the page.
- The [ChangeLog](https://mednafen.github.io/documentation/ChangeLog.txt) has **no** occurrence of "chd" in its full text. I grepped the whole 465 KB file. Its newest entry is **1.32.1, 15 March 2024**.
- [Releases](https://mednafen.github.io/releases/) lists 1.32.1 as the newest.
- So the belief that CHD support arrived in 1.30+ is **not supported** by the primary sources. The CHD support people remember is the **libretro Beetle** forks, not standalone Mednafen.
- This applies to every Mednafen module: PSX, Saturn (`ss`), PCE CD and PC-FX.

---

## 3. Standalone emulators per system and their formats

Key: ✅ supported · ❌ not supported · ? unverified · — not applicable.
"Zstd CHD" means CHDs made with chdman's `zstd`/`cdzs` codecs. I verified it by looking for the `cdzs` codec in the libchdr the emulator bundles or pins.

### 3.1 PlayStation

| Emulator | CHD | Zstd CHD | PBP | ISO | CUE/BIN | Other | Sources |
|---|---|---|---|---|---|---|---|
| DuckStation | ✅ | ✅ (builds after 18 Feb 2024) | ✅ (unencrypted) | opens, but assumes Mode 1/2048, so most games break | ✅ | ECM, MDS/MDF, CCD, M3U, PPF | §2.1, §2.2, [README](https://github.com/stenzek/duckstation/blob/master/README.md) |
| Mednafen | ❌ | ❌ | ❌ ? (not in docs) | ❌ alone (via CUE only) | ✅ | CCD/IMG/SUB, TOC, M3U | §2.3 |
| ePSXe 2.0.18 (23 Dec 2025) | ✅ "Support for ISOs in CHD format" | ? (closed source) | ✅ (since 1.8.0) | ✅ (Android feature list) | ✅ | MDS/MDF, CCD, ECM, NRG | [epsxe.com](https://www.epsxe.com/), [Android features](https://epsxe.com/android/features.html) |
| PCSX-Redux | ❌ (no CHD reader in [`src/cdrom`](https://github.com/pcsx-redux/pcsx-redux/tree/main/src/cdrom): only cbin, ccd, cue, ecm, mds, pbp, toc) | ❌ | ✅ (`cdriso-pbp.cc`) | ✅ (detects Mode 1/2048 in [`cdriso.cc`](https://github.com/pcsx-redux/pcsx-redux/blob/main/src/cdrom/cdriso.cc) ~l.317), same game-breakage caveat | ✅ | ECM, MDS, CCD, TOC, CBIN | links in cells |
| ares | ✅ | ✅ (`libchdr_codec_cdzs.c` in [thirdparty/libchdr](https://github.com/ares-emulator/ares/tree/master/thirdparty/libchdr/src)) | ? | ? | ✅ | ES-DE launches it with `--system "PlayStation"` ([es_systems.xml](https://github.com/RetroDECK/ES-DE/blob/retrodeck-main/resources/systems/windows/es_systems.xml)) | |

### 3.2 PlayStation 2

| Emulator | CHD | Zstd CHD | ISO | CSO | ZSO | CUE/BIN | Other | Sources |
|---|---|---|---|---|---|---|---|---|
| PCSX2 (2.x) | ✅. Added Dec 2020 in 1.7 dev, commit [`0599e675a0` "CDVD: Add support for CHD format"](https://github.com/PCSX2/pcsx2/commit/0599e675a0). | ✅ (`cdzs` in [3rdparty/libchdr](https://github.com/PCSX2/pcsx2/blob/master/3rdparty/libchdr/src/libchdr_chd.c)) | ✅ | ✅ | ✅ | ✅ (bin/img) | gz, mdf, nrg, dump | [Batocera pcsx2.emulator.yml](https://github.com/batocera-linux/batocera.linux/blob/master/package/batocera/emulators/pcsx2/pcsx2.emulator.yml) |
| Play! | ? | ? | ✅ ? | ? | ? | ? | ES-DE lists "Play! (Standalone)" with `--disc` | [es_systems.xml](https://github.com/RetroDECK/ES-DE/blob/retrodeck-main/resources/systems/windows/es_systems.xml) |
| OPL on a real PS2 (not an emulator) | ❌ | ❌ | ✅ | ❌ ? | ✅ | — | | per NoIntrovert `docs/target-formats.md`; not re-verified |

PCSX2 note (from the app's own doc D4): DVD images should use `createdvd`. CHDs of DVD ISOs made with `createcd` perform badly.

### 3.3 PSP

| Emulator | CHD | Zstd CHD | ISO | CSO | PBP | Sources |
|---|---|---|---|---|---|---|
| PPSSPP | ✅ since **1.17** ([1.17 announcement](https://www.ppsspp.org/blog/1-17-release-announcement)); `.chd` handled in [`Core/Loaders.cpp`](https://github.com/hrydgard/ppsspp/blob/master/Core/Loaders.cpp) l.112–117 | ✅. The `ext/libchdr` submodule is pinned to rtissera/libchdr@8bba774, which contains `cdzs` ([.gitmodules](https://github.com/hrydgard/ppsspp/blob/master/.gitmodules)). | ✅ | ✅ | ✅ (PSN EBOOT) | links in cells |
| Real PSP / Adrenaline (Vita) | ❌ | ❌ | ✅ | ✅ | ✅ | not re-verified (app doc D3) |

### 3.4 Dreamcast

| Emulator | CHD | Zstd CHD | GDI | CDI | CUE/BIN | Sources |
|---|---|---|---|---|---|---|
| Flycast | ✅ ([`core/imgread/chd.cpp`](https://github.com/flyinghead/flycast/blob/master/core/imgread/chd.cpp)) | ✅. The submodule is pinned to flyinghead/libchdr@5f82799, which contains `cdzs` ([.gitmodules](https://github.com/flyinghead/flycast/blob/master/.gitmodules)). | ✅ | ✅ | ✅ | |
| Redream | ✅ (since 2017, [progress report Jan 2018](https://redream.io/posts/progress-report-january-2018)) | **?**. Closed source. Stable is still v1.5.0, with only dev builds after it ([download](https://redream.io/download)). A stable build that old predates Zstandard CHDs (2024), but dev builds may have it. Unverified. | ✅ | ✅ (discouraged) | ? | |
| Demul | ? | ? | ✅ ? | ✅ ? | ? | listed by ES-DE with `-run=dc -image=` |
| Real hardware (GDEMU and other optical-drive emulators) | ❌ | ❌ | ✅ | ✅ | ❌ ? | not re-verified |

### 3.5 Saturn

| Emulator | CHD | Zstd CHD | CUE/BIN | ISO | Other | Sources |
|---|---|---|---|---|---|---|
| Ymir | ✅ | ✅. The `vendor/libchdr` submodule is pinned to rtissera/libchdr@47d7941, which contains `cdzs`. | ✅ | ✅ | IMG/CCD, MDF/MDS | [README](https://github.com/StrikerX3/Ymir/blob/main/README.md), [.gitmodules](https://github.com/StrikerX3/Ymir/blob/main/.gitmodules) |
| Mednafen (`ss`) | ❌ | ❌ | ✅ | ❌ | CCD, TOC, M3U | §2.3 |
| SSF | ? | ? | ✅ ? | ? | | unverified |
| Kronos / Yaba Sanshiro 2 | ? | ? | ✅ ? | ? | | unverified (both listed by ES-DE) |

### 3.6 Mega-CD / Sega CD

| Emulator | CHD | Zstd CHD | CUE/BIN | ISO(+WAV) | Sources |
|---|---|---|---|---|---|
| jgenesis | ✅ | ✅ (Rust `chd` crate 0.3 with feature `fast_zstd`) | ✅ | ? | [common/cdrom/Cargo.toml](https://github.com/jsgroth/jgenesis/blob/master/common/cdrom/Cargo.toml), [reader/chd.rs](https://github.com/jsgroth/jgenesis/blob/master/common/cdrom/src/reader/chd.rs) |
| ares | ✅ | ✅ | ✅ | ? | §3.1 |
| BlastEm | ✅ in current source ([cdimage.c `cd_chd_seek`](https://www.retrodev.com/repos/blastem/file/tip/cdimage.c)); whether a **release** build has Sega CD/CHD is unverified | ? | ✅ ? | ? | |
| Kega Fusion | ? | ? | ✅ ? | ✅ ? | unverified |

### 3.7 PC Engine CD / TurboGrafx-CD

| Emulator | CHD | Zstd CHD | CUE/BIN | Sources |
|---|---|---|---|---|
| Geargrafx | ✅ | ✅ (`cdzs` in [bundled libchdr](https://github.com/drhelius/Geargrafx/blob/main/platforms/shared/dependencies/libchdr/src/libchdr_chd.c)) | ✅ (bin/img/iso tracks, WAV/Ogg audio) | [README](https://github.com/drhelius/Geargrafx/blob/main/README.md) |
| Mednafen (`pce`) | ❌ | ❌ | ✅ (also CCD, TOC) | §2.3 |
| Mesen 2 | ❌. [`Core/Shared/CdReader.cpp`](https://github.com/SourMesen/Mesen2/blob/master/Core/Shared/CdReader.cpp) only has `LoadCue`. | ❌ | ✅ | |
| ares | ✅ | ✅ | ✅ | §3.1 |

### 3.8 3DO

| Emulator | CHD | Zstd CHD | CUE/BIN | ISO | Sources |
|---|---|---|---|---|---|
| Opera (RetroArch core only; ES-DE for Windows lists **no** standalone) | ✅ (ES-DE system extensions `.bin .chd .cue .iso`) | handled on the RetroArch side | ✅ | ✅ | [es_systems.xml](https://github.com/RetroDECK/ES-DE/blob/retrodeck-main/resources/systems/windows/es_systems.xml) |
| Phoenix (standalone, listed by RetroBat) | ? | ? | ? | ? | [RetroBat es_systems.cfg](https://github.com/RetroBat-Official/retrobat/blob/main/system/templates/emulationstation/es_systems.cfg) |

### 3.9 GameCube and Wii

| Emulator | RVZ | WIA | GCZ | ISO/GCM | WBFS | CISO | CHD | Sources |
|---|---|---|---|---|---|---|---|---|
| Dolphin (and PrimeHack) | ✅ (introduced in the [May–June 2020 progress report](https://dolphin-emu.org/blog/2020/07/05/dolphin-progress-report-may-and-june-2020/); build number 5.0-12188 is unverified) | ✅ | ✅ | ✅ | ✅ | ✅ | **❌**. The `BlobType` enum in [`DiscIO/Blob.h`](https://github.com/dolphin-emu/dolphin/blob/master/Source/Core/DiscIO/Blob.h) is PLAIN, DRIVE, DIRECTORY, GCZ, CISO, WBFS, TGC, WIA, RVZ, MOD_DESCRIPTOR, NFS, SPLIT_PLAIN. | |
| Nintendont / Swiss / USB loaders (real hardware) | ❌ | ❌ | ❌ | ✅ | ✅ (Wii) | ✅ ? | ❌ | not re-verified (app doc) |

---

## 4. Proposed presets per system

Every CHD preset keeps the app's current choice of chdman's default codecs (no Zstandard) and checks the CHD after making it. Default codecs keep CHDs readable by older readers whose libchdr has no `cdzs` (Redream stable, older DuckStation, possibly ePSXe and Demul). Zstandard is offered only under **Custom**, with an ⓘ note ("needs an emulator built after early 2024"). Marking it ⚠ would be wrong, because it doesn't lose data.

Legend: ★ = recommended (first in list). ⚠ = loses data or breaks most games for this system. 🔒 = needs a writer the app doesn't have yet (`"producible": false` in `TargetFormats.json`). Shared settings: **verify** = `Chd.VerifyAfterCreate=true` (or RVZ verify); **keep** = `Output.RemoveOriginals=false`; **replace** = remove originals only after the output verifies.

### 4.1 PlayStation (`psx`)

| Preset | Fixed settings | Notes |
|---|---|---|
| **CHD · Standard ★** | `createcd`, default codecs (cdlz/cdzl/cdfl), default hunk, verify, keep; multi-disc → `.m3u` | Every PS1 standalone except Mednafen and PCSX-Redux reads it (DuckStation, ePSXe 2.0.18, ares). Lossless, with subchannel data in the CHD. Keep the `.sbi` beside it for LibCrypt titles. |
| CHD · Replace the originals | as above + replace | |
| PBP · Standard 🔒 | one disc per EBOOT, popstation compression level (e.g. 5), keep | DuckStation, ePSXe, PCSX-Redux and a real PSP/Vita read it. Mednafen doesn't. |
| PBP · Multi-disc in one file 🔒 | all discs in one EBOOT (max 5), keep | No `.m3u` needed. Same readers as above. |
| BIN/CUE · Redump layout | `ExtractFormat=Auto`, `SplitBin=true` (one .bin per track) | Readable by every emulator including Mednafen and PCSX-Redux, and matches the Redump DAT. |
| ISO · Data track only ⚠ | data track as 2048-byte sectors | ⚠ Loses XA Form 2 data and CD-DA. DuckStation and PCSX-Redux treat .iso as Mode 1/2048 (§2.2), so most games break. |
| Custom | | Zstandard (`cdzs`) is available here with the ⓘ note. |

### 4.2 PlayStation 2 (`ps2`)

| Preset | Fixed settings | Notes |
|---|---|---|
| **CHD · Standard ★** | `IsoMode=Auto`: `createdvd` for DVD games, `createcd` for CD games; default codecs; default hunk; verify; keep | PCSX2 has read CHD since Dec 2020 and is the only mainstream PS2 emulator. Big size savings. |
| CHD · Replace the originals | as above + replace | |
| CSO / ZSO · For OPL & real PS2 🔒 | ZSO (LZ4), 2048 block, keep | OPL on a real PS2 reads ISO/ZSO but not CHD. PCSX2 also reads CSO and ZSO. |
| ISO · Plain | extract DVD CHD → .iso; CD games → cue/bin | Largest, but readable everywhere (PCSX2, OPL, Play!). |
| Custom | | |

No ⚠: every PS2 option is lossless. Warn in text only if someone forces `createcd` on a DVD image (performance issues, per app doc D4).

### 4.3 PSP (`psp`)

| Preset | Fixed settings | Notes |
|---|---|---|
| **CHD · Standard ★** | `createdvd`, **2048-byte hunk** (app doc D3), default codecs, verify, keep | PPSSPP 1.17+ reads CHD. Smallest lossless option for an emulator-only setup. |
| CHD · Replace the originals | as above + replace | |
| CSO · For real PSP / Adrenaline 🔒 | CSO v1 (deflate), 2048 block, keep | Real hardware can't read CHD. Older PPSSPP too. |
| ISO · Plain | extract → .iso | Universal |
| Custom | | |

No ⚠. PBP is only for PSN titles. It's not a conversion target for UMD dumps.

### 4.4 Dreamcast (`dreamcast`)

| Preset | Fixed settings | Notes |
|---|---|---|
| **CHD · Standard ★** | `createcd` from .gdi or Redump .cue, default codecs, verify, keep; `.cdi` left as is (D7) | Flycast (Zstandard OK) and Redream (default codecs needed) both read it. |
| CHD · Replace the originals | as above + replace | |
| GDI · For GDEMU / Redream / Demul | `ExtractFormat=Gdi` (Auto chooses gdi for GD-ROM), split tracks | For optical-drive emulators on real hardware, and the safest choice for closed-source emulators. |
| BIN/CUE · Redump layout | Redump cue, split bins | Matches the Redump DAT. Flycast reads it. |
| Custom | | |

⚠ candidate: none to produce. Mention in text that **CDI** (an input only) is a lossy TOSEC/scene format.

### 4.5 Saturn (`saturn`)

| Preset | Fixed settings | Notes |
|---|---|---|
| **CHD · Standard ★** | `createcd`, default codecs, verify, keep; multi-disc → `.m3u` | Ymir, ares and the RetroArch Beetle Saturn / Kronos cores read it. **The side panel must show that standalone Mednafen can't read it** (§2.3). |
| CHD · Replace the originals | as above + replace | |
| BIN/CUE · Redump layout | `SplitBin=true` | Needed for standalone Mednafen (and probably SSF). |
| ISO · Data track only ⚠ | | ⚠ Loses CD-DA, which most Saturn games use for music. Mednafen won't load a bare ISO. |
| Custom | | |

### 4.6 Mega-CD / Sega CD (`segacd`)

| Preset | Fixed settings | Notes |
|---|---|---|
| **CHD · Standard ★** | `createcd`, default codecs, verify, keep | jgenesis, ares, BlastEm (source) and Genesis Plus GX / PicoDrive cores read it. |
| CHD · Replace the originals | as above + replace | |
| BIN/CUE · Redump layout | split tracks | Universal, and Kega Fusion likely needs it (unverified). |
| ISO · Data track only ⚠ | | ⚠ Loses the CD-DA soundtrack that most Sega CD games stream. |
| Custom | | |

### 4.7 PC Engine CD (`pcenginecd`)

| Preset | Fixed settings | Notes |
|---|---|---|
| **CHD · Standard ★** | `createcd`, default codecs, verify, keep | Geargrafx, ares and the Beetle PCE cores read it. **Mednafen and Mesen 2 standalone can't** (§3.7), so the side panel should say so. |
| CHD · Replace the originals | as above + replace | |
| BIN/CUE · Redump layout | split tracks | Required for Mednafen and Mesen 2 standalone. |
| ISO · Data track only ⚠ | | ⚠ PCE CD games put audio on track 1 and data on track 2 onward, with lots of CD-DA. A bare ISO breaks them. |
| Custom | | |

### 4.8 GameCube (`gc`) and 4.9 Wii (`wii`)

These presets map onto the existing RVZ presets.

| Preset | Fixed settings | Notes |
|---|---|---|
| **RVZ · Balanced ★** | Zstd level 5, 128 KiB blocks, no scrubbing, verify, keep | Lossless and can be turned back into the Redump ISO. Dolphin (and PrimeHack) read it. |
| RVZ · Smallest | Zstd 19 | |
| RVZ · Fastest | Zstd 1 | |
| RVZ · Replace the originals | Balanced + replace | |
| ISO · For Nintendont / Swiss / USB loaders | decode → .iso | Real hardware can't read RVZ. |
| CHD ⚠ | `NintendoTarget.Chd` | ⚠ **Dolphin has no CHD reader** (§3.9), so no mainstream GameCube/Wii emulator can play it. Hide it, or keep it with ⚠ as "archive only". |
| Custom | | Scrubbing (`Scrub=true`) belongs here with ⚠: it removes padding, so the Redump hash can't be restored. |

Wii only: WBFS for USB loaders is 🔒 (the app leaves `.wbfs` as is).

### 4.10 3DO (`3do`)

| Preset | Fixed settings | Notes |
|---|---|---|
| **CHD · Standard ★** | `createcd`, default codecs, verify, keep | Opera (RetroArch) reads it, and it's the only well-maintained 3DO emulator. |
| CHD · Replace the originals | as above + replace | |
| BIN/CUE · Redump layout | split tracks | Safest for standalone Phoenix (formats unverified). |
| ISO · Data track only ⚠ | | ⚠ only for discs with CD-DA tracks. Many 3DO discs are data-only, so show ⚠ per disc when the CHD/cue has audio tracks, if possible. |
| Custom | | |

### 4.11 Other systems in the registry

PC-FX, Neo Geo CD, CD-i, Jaguar CD, Amiga CD32 and FM Towns use the same pattern as Sega CD: **CHD · Standard ★ / Replace / BIN/CUE · Redump layout / ISO ⚠ (where CD-DA exists) / Custom**. PC-FX standalone is Mednafen only, so its BIN/CUE preset matters most there.

---

## 5. Follow-ups for the NoIntrovert team

1. `TargetFormats.json` marks PS1 `pbp` and PSP/PS2 `cso` as not producible. The PBP and CSO presets need writers first (🔒).
2. Rethink `NintendoTarget.Chd` for GameCube/Wii: Dolphin can't read it.
3. `docs/target-formats.md` says "Mednafen standalone has no CHD" for Saturn only. The same is true for PSX, PCE CD and PC-FX.
4. Snapshot pipeline:
   - Fetch ES-DE `es_systems.xml` and `es_find_rules.xml` for Windows (MIT) and Batocera `*.emulator.yml` (LGPL-3.0) at build time.
   - Run a `cdzs` grep over each emulator's libchdr to refresh the Zstandard column.
   - Ship the results as data files with their source URL and commit SHA.
