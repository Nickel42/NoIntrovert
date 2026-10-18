# NoIntrovert

A desktop CHD batch tool built with Avalonia, used as a test bench for
[libchdman](../libchdman). Every CHD operation goes through the LibChdman .NET
binding (`chdman.dll`); `chdman.exe` is only run when you ask the Library tests
tab to compare against it.

Workflow on every tab: **pick files → choose options → run the batch → read the log.**

| Tab | What it does |
|---|---|
| Convert | Any supported image → CHD, optional verify, optional removal of the originals (Recycle Bin by default, and only after the CHD verifies) |
| Verify | Checks CHDs against their stored SHA-1; can fix a wrong header SHA-1 |
| Extract | CHD → cue/bin (one bin per track or a single bin), gdi, iso (DVDs, and single-track data CDs) or raw img |
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
| zip, 7z, rar (incl. multi-volume), tar, tar.gz | Unpacked, then every disc inside is converted |
| Split files `name.ext.001, .002, …` | Joined, then converted |
| Loose `.bin` with no cue | One-track cue written from the first sector's mode |

ISOs in Auto mode are treated as DVDs when they hold a UDF file system, are
PSP UMD images, or are larger than an 80-minute CD; otherwise as CDs.

Known limits: password-protected or multi-part ISZ, encrypted (PSN) PBPs and
password-protected archives are refused with a message. Multi-session Alcohol
CDs are written as one session (as chdman does).

## Build and run

Needs the .NET 10 SDK and libchdman checked out next to this folder
(`..\libchdman`) with `artifacts\native\win-x64\chdman.dll` built.

```
dotnet run --project NoIntrovert
```

Paths given on the command line are added to the list, as if dropped on the
window; `--tab convert|verify|extract|info|tests` picks the starting tab.

Settings live in `%AppData%\NoIntrovert\settings.json`; each session's log is
written to `%LocalAppData%\NoIntrovert\logs`.

## Tests

`NoIntrovert.Tests` is a self-checking console program. It reads discs from
`..\test-roms` (never writing there), builds a sample of every input format in
a scratch folder, converts each through the app's own code and libchdman, and
checks the CHD's data SHA-1 against a CHD made from the original. It also
checks verify, extract, cancel, overwrite and delete-originals behaviour, and
compares output with `..\libchdman\build-x64\Release\chdman-upstream.exe` when
that exists.

```
dotnet run --project NoIntrovert.Tests -c Release -- --quick
```

Leave out `--quick` to also round-trip the full discs in test-roms (several
minutes). `--keep` keeps the scratch folder; `--roms` and `--upstream` point
at other locations.

The ISZ, PBP, CloneCD and Alcohol samples are made by the test program's own
writers, which follow the same layouts the decoders read, so those tests check
the conversion logic but not compatibility with files from the original tools.
Try real files from those tools on the Convert tab to confirm.
