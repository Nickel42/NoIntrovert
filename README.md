# NoIntrovert

A desktop CHD and RVZ batch tool built with Avalonia, used as a test bench for
[lib-chd-kit](native/lib-chd-kit) and [lib-wii-gamecube-kit](native/lib-wii-gamecube-kit). Every CHD operation goes through the LibChdKit .NET
binding (`chdkit.dll`); `chdman.exe` is only run when you ask the Library tests
page to compare against it.

Workflow on every page: **pick files → choose a preset → choose where to save → run the batch.** Each action
has a recommended preset; its individual options appear only under Custom ([presets](docs/requirements/presets.md)).
The window's left rail has two sections, **Library** (Optimize) and **Files** (Convert, Verify, Organize), with
Settings at the bottom ([information architecture](docs/information-architecture.md)).

| Page | What it does |
|---|---|
| Optimize | Your library: the folders you add, scanned into a list of games with their system, formats, size and status. From a selection (or the whole library) it hands games to Verify, Convert and Organize |
| Convert | Each row has its own target and preset: disc images → CHD (GameCube/Wii discs → RVZ, or CHD if you choose), Wii U → .wua, CHD → cue/bin (one bin per track or a single bin), gdi, iso or raw img, RVZ/WIA/GCZ → iso, files and folders → .7z or .zip (Pack a folder…), zip ↔ 7z, and unpacking zip/7z. New files are verified (archives tested) |
| Verify | Checks each game for damage (CHDs against their stored SHA-1, 7z and zip by testing every file), then looks it up in the No-Intro and Redump databases: a good dump, a misnamed one (with the name it should have), or not in the database. The databases come from [libretro-database](https://github.com/libretro/libretro-database), only for the systems in your Library, checked for updates when the app starts ([requirement](docs/requirements/verify-match.md)) |
| Organize | The Cleanup wizard: for the selected games or the whole Library, a dry run of converting to the target formats, renaming to the No-Intro/Redump names, moving into ES-DE system folders (or keeping each game where it is) and removing duplicates. Every step can be unticked; a run can be undone, conversions included, until its trash is emptied ([requirement](docs/requirements/cleanup-wizard.md)) |
| Library tests | Power-user mode only. Round trip (create → verify → extract → compare with the original data) and comparison with a chdman.exe (byte-for-byte output, timings); CSV export |
| Settings | One page of cards, saved as you change them: General, Output, Performance, Log, Default presets, Library and About, with Advanced below them |

The side panel on Convert describes the focused row: its system, the file, the formats for the system, the
row's preset and the emulators that can play the result ("Works with"). The emulator list comes from
[libretro-core-info](https://github.com/libretro/libretro-core-info) and [ES-DE](https://gitlab.com/es-de/emulationstation-de)
(both MIT), bundled with the app; Settings › Library › Check now downloads a newer copy
([ui-redesign](docs/requirements/ui-redesign.md)).

### Save to

Where new files go is chosen in the run bar, next to the Convert button: **Next to the originals** (the
default), **Folder** (the last folder used, with Change…), or **Replace the originals ⚠**. Replace is the only
way the app removes originals, and each one goes only after its new file verifies (or its archive tests OK);
if that fails, the original stays and the row says why. It asks for confirmation the first time it's chosen.
Removed originals go to the Recycle Bin (or the Trash) unless that's turned off. Settings › Output sets the
run bar's starting choice and **Overwrite existing files**: when that's off, a name that's already taken gets
" (2)".

### The log

The log sits under the job list, collapsed to one bar with the error and warning counts (red when there's an
error) and **Show log**. It opens by itself when a run has errors (Settings › Log). Each session's log is also
written to a file (see "Build and run").

## Input formats

Read by lib-chd-kit directly: cue/bin (any number of bins), gdi, toc, nrg, iso, hard disk img.

Converted first, in the temp folder:

| Format | How |
|---|---|
| ECM (`.bin.ecm`, or a cue whose bins are `.ecm`) | Decoded, with the stored checksum verified |
| CSO / ZSO | Deflate / LZ4 blocks decoded to an ISO |
| ISZ (UltraISO) | zlib / bzip2 / raw / zero blocks decoded to an ISO or bin |
| PBP (PS1 EBOOT, single or multi-disc) | Each disc rebuilt as bin + cue; multi-disc sets give one CHD per disc |
| CloneCD `.ccd/.img/.sub` | Cue written that points at the `.img` (no copy) |
| Alcohol `.mds/.mdf` | DVDs used as-is; CDs get a cue, and subchannel data (2448-byte sectors) is stripped first |
| zip, 7z, tar, tar.gz | Unpacked, then every disc inside is converted |
| Split files `name.ext.001, .002, …` | Joined, then converted |
| Loose `.bin` with no cue | One-track cue written from the first sector's mode |

### GameCube and Wii

GameCube and Wii discs (iso and gcm, recognised by their disc header; gcz,
wia, rvz and tgc) go through lib-wii-gamecube-kit, which is Dolphin's own disc code
(the same as `DolphinTool convert`). By default they become **RVZ**, Dolphin's
format: Zstandard level 5 with 128 KiB blocks, as in Dolphin's convert dialog,
and adjustable on the Convert page. Wii partitions are stored decrypted, and
Nintendo's junk padding as seeds, so an RVZ is far smaller than the ISO and
still decodes to the exact original disc. Verifying an RVZ decodes it again
and compares its SHA-1 with the source. Choose CHD instead to store them as
DVD CHDs (a gcz/wia/rvz is decoded to ISO first).

WBFS and CISO images can't be converted: like Dolphin, lib-wii-gamecube-kit only
converts images whose container records the disc's exact size. NKit images
convert, but stay NKit.

ISOs in Auto mode are treated as DVDs when they hold a UDF file system, are
PSP UMD images, or are larger than an 80-minute CD; otherwise as CDs.

Known limits: password-protected or multi-part ISZ, encrypted (PSN) PBPs and
password-protected archives are refused with a message. Multi-session Alcohol
CDs are written as one session (as chdman does).

## Target formats

Each system has a recommended format and container: for example, cartridge ROMs
go in a single-file .7z; disc images become a bare CHD, RVZ or .wua; and arcade
romsets are left alone. [docs/target-formats.md](docs/target-formats.md) lists
the rules, the decisions and the target for every system. Overriding these per
system or file type in Settings → Target formats is planned; see
[docs/requirements/target-formats.md](docs/requirements/target-formats.md).

## Platforms and downloads

Releases have four zips and a tar.gz, built by `.github/workflows/release.yml`
when a `v*` tag is pushed:

| Download | For |
|---|---|
| `win-x64` | Windows 10/11, Intel or AMD (64-bit) |
| `win-arm64` | Windows 11 on ARM |
| `osx-arm64` | macOS 12+, Apple silicon |
| `osx-x64` | macOS 12+, Intel |
| `linux-x64` (tar.gz) | Linux x64 with glibc 2.35+ (Ubuntu 22.04, Debian 12, Fedora 36 or newer) |

Windows builds are one self-contained `NoIntrovert.exe`. macOS builds are a
self-contained `NoIntrovert.app`, signed ad hoc but not notarised: right-click
→ Open the first time. On macOS, removed originals go to the Trash. The Linux
build is a folder with the `NoIntrovert` executable and its `.so` libraries
beside it; removed originals go to the desktop's Trash (the freedesktop.org
one, `~/.local/share/Trash`, or the disk's own `.Trash-<uid>` for other drives).
It runs on X11 or XWayland and uses the libraries any desktop install has
(ICU, fontconfig, libX11, libICE, libSM); on a minimal system such as a fresh
WSL Ubuntu, install `libicu`, `libfontconfig1`, `libice6` and `libsm6` first.

The workflow builds lib-chd-kit, lib-wii-gamecube-kit and lib-7z-zip-kit from
source on each platform (and runs their tests on x64 Windows, macOS and Linux,
plus the app's own quick self-test on Linux). Everything it builds is in this
repository, except lib-wii-gamecube-kit and lib-7z-zip-kit, which it checks out
as submodules.

## Archive engine

Convert's 7z and zip targets use 7-Zip's own library (`7z.dll` / `7z.so`, built from
7-Zip 26.04's source, without RAR support) through [bit7z](https://github.com/rikyoz/bit7z) 4.1.0,
by way of lib-7z-zip-kit, its own repository
([Nickel42/lib-7z-zip-kit.](https://github.com/Nickel42/lib-7z-zip-kit.)), checked
out as a git submodule in `native/lib-7z-zip-kit`. Its small C library,
sevenzipkit, is what the C# code calls; its managed reader (SharpCompress)
unpacks and lists archives for the rest of the app. To build the native engine locally:

```
bash native/lib-7z-zip-kit/scripts/fetch-sources.sh
native\lib-7z-zip-kit\scripts\build-win.cmd x64
```

(`bash native/lib-7z-zip-kit/scripts/build-mac.sh` on a Mac,
`bash native/lib-7z-zip-kit/scripts/build-linux.sh` on Linux, which needs gcc,
make and CMake.) The results go in `native/lib-7z-zip-kit/artifacts/native/<platform>`,
which the app build copies next to the executable. `fetch-sources.sh` pins
both downloads by SHA-256. See `packaging/THIRD_PARTY_ARCHIVES.md` for their
licenses.

## Wii U (.wua)

Convert's Wii U target builds Cemu's `.wua` format: a [ZArchive](https://github.com/Exzap/ZArchive)
(zstd in 64 KiB blocks, random access) holding each title in a
`<16-hex title ID>_v<version>` folder. It writes one `.wua` per game with the
base game, its newest update and its DLC, from any mix of:

- extracted title folders (`code`, `content`, `meta`)
- NUS / installable titles (`title.tmd`, `title.tik`, `.app`)
- WUD and WUX disc images, including split `game_part1.wud` dumps
- existing `.wua` files (to merge in an update or DLC, or to repack)

Encrypted titles and discs need your own `keys.txt`, in Cemu's format (one
32-digit hex key per line, `#` comments). NoIntrovert ships no keys. It
looks in its own settings folder and Cemu's, or uses a file you choose; a
disc key can also sit next to its image as `game.key`. Every key is tried
and the one that decrypts a valid header is used, so the order doesn't
matter. Personalised (console-bound) tickets aren't supported.

Data is checked as it's decrypted (every hashed block against its H0 hash,
NUS raw contents against their TMD hash), and every new `.wua` is reopened,
its SHA-256 checked and every file compared with its source before it
replaces anything. The Wii U code, ZArchive writer and reader included, is
lib-wii-u-kit, a managed .NET library in its own repository
([Nickel42/lib-wii-u-kit](https://github.com/Nickel42/lib-wii-u-kit)),
checked out as a git submodule in `native/lib-wii-u-kit`; NoIntrovert adds
its settings and Save to on top
(`NoIntrovert/Core/WiiU/WuaJobs.cs`). It is checked in CI against Exzap's reference
`zarchive` tool in both directions. The encrypted-format tests use synthetic
images with made-up keys, so try a real dump of your own before relying on
it.

## App icon

A pixel-art retro joystick, generated by `packaging/icon/make_icon.py`
(no image libraries needed) into the Windows `.ico`, the macOS `.icns` and a
PNG of every size. On Linux, run `./install-desktop-entry.sh` from the
unpacked folder to add NoIntrovert to the app menu with its icon
(`--uninstall` removes it).

## Build and run

Needs the .NET 10 SDK and the native libraries built:

- lib-chd-kit (`native/lib-chd-kit`, see its README) with its native library in
  `native/lib-chd-kit/artifacts/native/win-x64`, `win-arm64`, `osx` or
  `linux-x64` (see `Directory.Build.props`). On Linux, copy its
  `build/libchdkit.so` (with `cp -L`, as it is a symlink) there.
- lib-wii-gamecube-kit, the GameCube/Wii library. It is its own repository
  ([Nickel42/lib-wii-gamecube-kit](https://github.com/Nickel42/lib-wii-gamecube-kit)),
  checked out as a git submodule in `native/lib-wii-gamecube-kit`: run
  `git submodule update --init` after cloning, then its build script
  (`scriptsuild-win.cmd x64`, `scripts/build-mac.sh` or
  `scripts/build-linux.sh`), which puts the native library in its
  `artifacts/native/<platform>`.
- lib-7z-zip-kit, the archive engine, also a submodule (`native/lib-7z-zip-kit`),
  with its native build in its `artifacts/native/<platform>` (see "Archive engine" above).
- lib-wii-u-kit, the Wii U library, also a submodule (`native/lib-wii-u-kit`).
  It is managed code only, so it needs no build of its own.

```
dotnet run --project NoIntrovert
```

To make a release build by hand:

```
dotnet publish NoIntrovert -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Paths given on the command line are added to the list, as if dropped on the
window; `--page optimize|convert|verify|organize|settings|tests` picks the
starting page (the older `--tab NAME` still works). Without it the app opens
on Settings › General › Start on.

Settings live in `%AppData%\NoIntrovert\settings.json` (macOS and Linux:
`~/.config/NoIntrovert`); each session's log is written to its `logs` folder
as `NoIntrovert-yyyyMMdd-HHmmss.log` and kept for 30 days (Settings › Log).

## Tests

`NoIntrovert.Tests` is a self-checking console program. It reads discs from
`..\test-roms` (never writing there), builds a sample of every input format in
a scratch folder, converts each through the app's own code and lib-chd-kit, and
checks the CHD's data SHA-1 against a CHD made from the original. It also
checks verify, extract, cancel, Overwrite (" (2)" names) and Replace the originals, and
compares output with `native\lib-chd-kit\build-x64\Release\chdman-upstream.exe` when
that exists.

```
dotnet run --project NoIntrovert.Tests -c Release -- --quick
```

Leave out `--quick` to also round-trip the full discs in test-roms (several
minutes). Without test-roms (or with `--synthetic`) the quick tests run on
generated stand-in discs and the full-disc ones are skipped; CI runs it that way
on Linux. `--keep` keeps the scratch folder; `--roms` and `--upstream` point
at other locations.

The ISZ, PBP, CloneCD and Alcohol samples are made by the test program's own
writers, which follow the same layouts the decoders read, so those tests check
the conversion logic but not compatibility with files from the original tools.
Try real files from those tools on the Convert page to confirm.

## License

NoIntrovert is free software: you can redistribute it and/or modify it under
the terms of the GNU General Public License, version 3 or (at your option) any
later version, as published by the Free Software Foundation. See `LICENSE`.

The bundled libraries keep their own licenses (all GPL-compatible): lib-chd-kit
is BSD-3-Clause, lib-7z-zip-kit and lib-wii-u-kit MIT, 7-Zip (built without RAR) LGPL-2.1+, bit7z MPL-2.0. See
`packaging/THIRD_PARTY_ARCHIVES.md` and lib-chd-kit's `THIRD_PARTY_NOTICES.md`.
The bundled emulator data is derived from libretro-core-info and ES-DE, both MIT;
their notices are in `THIRD_PARTY_NOTICES-emulator-data.txt` in every download
(`NoIntrovert/Core/Emulators/Data/THIRD_PARTY_NOTICES.txt` in the source).
RAR archives aren't supported, because 7-Zip's RAR decoder is under the unRAR
license restriction, which the GPL doesn't allow.
