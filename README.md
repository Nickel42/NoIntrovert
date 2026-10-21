# NoIntrovert

A desktop CHD and RVZ batch tool built with Avalonia, used as a test bench for
[libchdman](native/libchdman) and [libdolphinrvz](native/libdolphinrvz). Every CHD operation goes through the LibChdman .NET
binding (`chdman.dll`); `chdman.exe` is only run when you ask the Library tests
tab to compare against it.

Workflow on every tab: **pick files → choose a preset → run the batch → read the log.** Each action has a
recommended preset; its individual options appear only under Custom ([presets](docs/requirements/presets.md)).

| Tab | What it does |
|---|---|
| Convert | Any supported image → CHD (GameCube/Wii discs → RVZ, or CHD if you choose), optional verify, optional removal of the originals (Recycle Bin by default, and only after the CHD verifies) |
| Verify & match | Checks each game for damage (CHDs against their stored SHA-1, 7z and zip by testing every file), then looks it up in the No-Intro and Redump databases: a good dump, a misnamed one (with the name it should have), or not in the database. The databases come from [libretro-database](https://github.com/libretro/libretro-database), only for the systems in your Library, checked for updates when the app starts ([requirement](docs/requirements/verify-match.md)) |
| Extract | CHD → cue/bin (one bin per track or a single bin), gdi, iso (DVDs, and single-track data CDs) or raw img; RVZ/WIA/GCZ → iso |
| Archive | Files and folders → .7z or .zip (one archive per item, one per file/subfolder in a folder, or one for everything), zip ↔ 7z conversion, and extracting zip/7z (into a folder per archive or straight into the output folder); tests each archive and can remove the originals |
| Cleanup | The Cleanup wizard: for the selected games or the whole Library, a dry run of converting to the target formats, renaming to the No-Intro/Redump names, moving into ES-DE system folders (or keeping each game where it is) and removing duplicates. Every step can be unticked; a run can be undone, conversions included, until its trash is emptied ([requirement](docs/requirements/cleanup-wizard.md)) |
| CHD Info | Header fields, metadata (copy, save an item to a file), hunk counts per codec |
| Library tests | Round trip (create → verify → extract → compare with the original data) and comparison with a chdman.exe (byte-for-byte output, timings); CSV export |

## Input formats

Read by libchdman directly: cue/bin (any number of bins), gdi, toc, nrg, iso, hard disk img.

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
wia, rvz and tgc) go through libdolphinrvz, which is Dolphin's own disc code
(the same as `DolphinTool convert`). By default they become **RVZ**, Dolphin's
format: Zstandard level 5 with 128 KiB blocks, as in Dolphin's convert dialog,
and adjustable on the Convert tab. Wii partitions are stored decrypted, and
Nintendo's junk padding as seeds, so an RVZ is far smaller than the ISO and
still decodes to the exact original disc. Verifying an RVZ decodes it again
and compares its SHA-1 with the source. Choose CHD instead to store them as
DVD CHDs (a gcz/wia/rvz is decoded to ISO first).

WBFS and CISO images can't be converted: like Dolphin, libdolphinrvz only
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

The workflow builds libchdman and libdolphinrvz from source on each
platform (and runs their tests on x64 Windows, macOS and Linux, plus the app's
own quick self-test on Linux). Everything it builds is in this repository.

## Archive engine

The Archive tab uses 7-Zip's own library (`7z.dll` / `7z.so`, built from
7-Zip 26.04's source, without RAR support) through [bit7z](https://github.com/rikyoz/bit7z) 4.1.0,
wrapped in a small C library, `native/archive` (noi_archive), that the C# code
calls. To build it locally:

```
bash native/fetch-sources.sh
native\build-win.cmd x64 native\_deps\7zip-src native\_deps\bit7z-src
```

(`bash native/build-mac.sh` on a Mac, `bash native/build-linux.sh` on Linux,
which needs gcc, make and CMake.) The results go in `native/out/<platform>`,
which the app build copies next to the executable. `fetch-sources.sh` pins
both downloads by SHA-256. See `packaging/THIRD_PARTY_ARCHIVES.md` for their
licenses.

## Wii U (.wua)

The Wii U tab builds Cemu's `.wua` format: a [ZArchive](https://github.com/Exzap/ZArchive)
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
replaces anything. The ZArchive writer and reader are NoIntrovert's own C#
(`NoIntrovert/Core/WiiU/`), checked in CI against Exzap's reference
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

- libchdman (`native/libchdman`, see its README) with its native library in
  `native/libchdman/artifacts/native/win-x64`, `win-arm64`, `osx` or
  `linux-x64` (see `Directory.Build.props`). On Linux, copy its
  `build/libchdman.so` (with `cp -L`, as it is a symlink) there.
- the archive engine and libdolphinrvz into `native/out/<platform>` (see
  "Archive engine" above and `native/libdolphinrvz/README.md`).

```
dotnet run --project NoIntrovert
```

To make a release build by hand:

```
dotnet publish NoIntrovert -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Paths given on the command line are added to the list, as if dropped on the
window; `--tab convert|verify|extract|info|tests` picks the starting tab.

Settings live in `%AppData%\NoIntrovert\settings.json` (macOS and Linux:
`~/.config/NoIntrovert`); each session's log is written to
`%LocalAppData%\NoIntrovert\logs` (macOS and Linux: `~/.local/share/NoIntrovert/logs`).

## Tests

`NoIntrovert.Tests` is a self-checking console program. It reads discs from
`..\test-roms` (never writing there), builds a sample of every input format in
a scratch folder, converts each through the app's own code and libchdman, and
checks the CHD's data SHA-1 against a CHD made from the original. It also
checks verify, extract, cancel, overwrite and delete-originals behaviour, and
compares output with `native\libchdman\build-x64\Release\chdman-upstream.exe` when
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
Try real files from those tools on the Convert tab to confirm.

## License

NoIntrovert is free software: you can redistribute it and/or modify it under
the terms of the GNU General Public License, version 3 or (at your option) any
later version, as published by the Free Software Foundation. See `LICENSE`.

The bundled libraries keep their own licenses (all GPL-compatible): libchdman
is BSD-3-Clause, 7-Zip (built without RAR) LGPL-2.1+, bit7z MPL-2.0. See
`packaging/THIRD_PARTY_ARCHIVES.md` and libchdman's `THIRD_PARTY_NOTICES.md`.
RAR archives aren't supported, because 7-Zip's RAR decoder is under the unRAR
license restriction, which the GPL doesn't allow.
