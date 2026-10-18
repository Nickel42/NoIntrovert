# libchdman

MAME 0.289's CHD (Compressed Hunks of Data) tooling, the code behind
`chdman`, packaged as a library with a stable C ABI for Windows 11
(MSVC, x64 and ARM64) and macOS (universal arm64 + x86_64). It is built
from the upstream MAME sources at tag `mame0289`, not from a decompilation.

- `include/chdman.h`: C ABI with opaque handles, status codes plus a message
  getter, typed option structs, a progress callback with cancellation, and a
  log callback. No exceptions, `printf`, or `exit` cross the API.
- Covers `info`, `verify` (with fix), create raw/hd/cd/dvd/ld, extract
  raw (also hd/dvd)/cd/ld, `copy`, add/delete/dump metadata, and the list of
  hard disk templates.
- `chdman-cli`: a chdman-compatible command line tool written in C on top of
  the C API.
- Output is byte-identical to upstream chdman (see [Verification](#verification)).

## Layout

```
include/chdman.h            public C header (+ module.modulemap for Swift)
src/chdman_core.{h,cpp}     chdman's do_* logic as a C++ library core
src/chdman_capi.cpp         C ABI layer (catches everything, maps to status codes)
cli/chdman_cli.c            chdman-compatible CLI (pure C, uses only chdman.h)
tests/                      round-trip/determinism tests, upstream parity test
examples/swift, examples/csharp
vendor/mame/                vendored MAME 0.289 subset (see vendor/PATCHES.md)
vendor/osd_shim.cpp         minimal OSD layer replacing MAME's osdlib
THIRD_PARTY_NOTICES.md      licenses of everything bundled
```

## Building

Requires CMake 3.20+ and a C++20 compiler (MAME 0.289 uses C++20).

### macOS (universal dylib + static library)

```bash
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release "-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64"
```

```bash
cmake --build build
```

```bash
ctest --test-dir build --output-on-failure
```

This produces `build/libchdman.dylib` (install name `@rpath/libchdman.1.dylib`),
`build/libchdman.a` and `build/chdman-cli`, all universal. The deployment
target defaults to macOS 11.0 (`-DCMAKE_OSX_DEPLOYMENT_TARGET=...` to change).
Without Ninja, use `-G Xcode` or the default Makefiles.

The dylib exports only the `chdman_*` functions. The static archive is
prelinked into a single object per architecture in which every bundled
symbol (zlib, zstd, FLAC, LZMA, MAME) is private, so it can't clash with
copies your app links itself. Set `-DCHDMAN_STATIC_PRELINK=OFF` to get an
ordinary archive instead. Apps linking the static library must also link
`libc++` (`-lc++`, or the `CChdman` module map does it for Swift).

To package it for Xcode as an XCFramework:

```bash
mkdir -p build/xcf-headers && cp include/chdman.h include/module.modulemap build/xcf-headers/
```

```bash
xcodebuild -create-xcframework -library build/libchdman.a -headers build/xcf-headers -output build/Chdman.xcframework
```

### Windows 11 (MSVC x64 and ARM64)

Use Visual Studio 2022 **17.5 or newer** (MAME's C++20 code and the
`/Zc:enumTypes` / `/Zc:templateScope` switches MAME's build uses need it),
with the "Desktop development with C++" workload and, for ARM64, the
"MSVC ARM64 build tools" component.

x64, from any prompt:

```bash
cmake -S . -B build-x64 -G "Visual Studio 17 2022" -A x64
```

```bash
cmake --build build-x64 --config Release
```

```bash
ctest --test-dir build-x64 -C Release --output-on-failure
```

ARM64 (cross-compiles from an x64 machine; run the tests on an ARM64 device):

```bash
cmake -S . -B build-arm64 -G "Visual Studio 17 2022" -A ARM64
```

```bash
cmake --build build-arm64 --config Release
```

Or with Ninja, from the matching "x64 Native Tools" / "ARM64 Native Tools"
developer prompt:

```bash
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
```

```bash
cmake --build build
```

Outputs: `chdman.dll` + `chdman.lib` (import library), `chdman_static.lib`
and `chdman-cli.exe`. When linking `chdman_static.lib`, define
`CHDMAN_STATIC` before including `chdman.h` (the CMake target does this for
you). The CRT is linked statically by default, so `chdman.dll` has no VC++
redistributable dependency. To use the DLL CRT instead, pass
`-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded$<$<CONFIG:Debug>:Debug>DLL`.

### CMake options

| Option | Default | |
|---|---|---|
| `CHDMAN_BUILD_SHARED` | ON | dylib / DLL |
| `CHDMAN_BUILD_STATIC` | ON | static library |
| `CHDMAN_BUILD_CLI` | ON | `chdman-cli` |
| `CHDMAN_BUILD_TESTS` | ON | test suite (`ctest`) |
| `CHDMAN_STATIC_PRELINK` | ON | Apple: hide non-API symbols in the static library |
| `CHDMAN_BUILD_UPSTREAM_REFERENCE` | OFF | build unmodified upstream `chdman` and add the parity test |

`cmake --install build --prefix <dir>` installs the libraries, header, module
map, license files and a `chdmanConfig.cmake` (`find_package(chdman)`, then
link `chdman::chdman_shared` or `chdman::chdman_static`).

## Using the C API

```c
#include "chdman.h"

static int CHDMAN_CALL on_progress(void *user, chdman_phase phase, double fraction, double ratio)
{
    printf("%.0f%%\r", fraction * 100.0);
    return 0;                      /* non-zero cancels */
}

chdman_context *ctx;
chdman_context_create(&ctx);
chdman_context_set_progress(ctx, on_progress, NULL);

chdman_create_cd_options o;
chdman_create_cd_options_init(&o);   /* always init: sets struct_size and defaults */
o.input = "game.cue";
o.out.output = "game.chd";
o.out.compression = NULL;            /* default: cdlz,cdzl,cdfl */

if (chdman_create_cd(ctx, &o) != CHDMAN_OK)
    fprintf(stderr, "failed: %s\n", chdman_last_error(ctx));

chdman_context_destroy(ctx);
```

Rules:

- One context per thread. Separate contexts can run in parallel.
  `chdman_context_cancel()` may be called from any thread; the running
  operation returns `CHDMAN_ERR_CANCELLED` and deletes its partial output.
- All strings are UTF-8, including Windows paths.
- Unset numeric options are `0` (sizes) or `CHDMAN_UNSET` (offsets and
  lengths); the `*_init` functions set these.
- Defaults match chdman 0.289 (and the reverse-engineered spec): raw, hd and dvd
  use `lzma,zlib,huff,flac`; cd uses `cdlz,cdzl,cdfl`; ld uses `avhu`. The default
  hunk size is the largest multiple of the unit size that is ≤ 4096 for raw/hd,
  4096 for dvd, 19584 (8 frames of 2448 bytes) for cd, and one frame for ld.
- `chdman_verify` returns `CHDMAN_OK` whenever verification ran. The verdict
  is in `result.outcome` (`OK`, `RAW_MISMATCH`, `OVERALL_MISMATCH`, `FIXED`,
  `NOT_APPLICABLE`).
- Informational lines chdman would print (e.g. "Compression: ...") go to
  the log callback, so a GUI can show or ignore them.

## Using from Swift (macOS)

`include/module.modulemap` defines the `CChdman` module. Point
`SWIFT_INCLUDE_PATHS` / `-I` at `include` (or use the XCFramework above) and
link `libchdman`. `examples/swift/Chdman.swift` is a small wrapper that
turns callbacks into closures and errors into Swift `throws`:

```swift
import CChdman

let chdman = try Chdman()
chdman.onProgress = { phase, fraction, ratio in
    DispatchQueue.main.async { progressView.doubleValue = fraction }
    return isCancelled                // true cancels
}
try chdman.createCD(input: "/path/game.cue", output: "/path/game.chd")
let info = try chdman.info("/path/game.chd")
print(info.mediaType, info.compression, info.rawSHA1)
```

Build and run the bundled example (verified with Swift 6.4, against both the
dylib and the static library):

```bash
cd examples/swift && swiftc -I ../../include -L ../../build -lchdman -Xlinker -rpath -Xlinker ../../build Chdman.swift main.swift -o chdman-swift && ./chdman-swift
```

Run long operations off the main thread (each `Chdman` instance owns one
context). Call `cancel()` from anywhere.

## Using from C# / .NET (P/Invoke)

`examples/csharp/LibChdman.cs` contains `[DllImport("chdman")]` declarations
for the whole API, struct layouts that mirror `chdman.h` (UTF-8 string
marshaling), and a disposable `Chdman` wrapper. `DllImport("chdman")`
resolves to `chdman.dll` on Windows and `libchdman.dylib` on macOS.

```csharp
using LibChdman;

using var chdman = new Chdman();
chdman.OnProgress = (phase, fraction, ratio) => { Report(fraction); return token.IsCancellationRequested; };
chdman.CreateCd(@"C:\games\game.cue", @"C:\games\game.chd");
var info = chdman.Info(@"C:\games\game.chd");
Console.WriteLine($"{info.mediaType} {info.compression} {info.rawSha1}");
```

```bash
cd examples/csharp && dotnet run -p:ChdmanLibDir=../../build
```

(Pass the directory holding `chdman.dll` or `libchdman.dylib`; with the Visual Studio
generator that is `build-x64/Release`.) For a NuGet package, put the
binaries under `runtimes/win-x64/native/chdman.dll`,
`runtimes/win-arm64/native/chdman.dll` and `runtimes/osx/native/libchdman.dylib`
(the universal dylib serves both macOS architectures). The wrapper keeps its
callback delegates alive for the context's lifetime, so do the same if you
call `Native.*` directly. The example targets `net8.0`. On macOS it was
built and run with the .NET 10 SDK (`TargetFramework` set to `net10.0`),
and the marshaled struct sizes match the C `sizeof` values.

## CLI

`chdman-cli` accepts the same commands, long/short options and output
format as chdman 0.289:

```bash
./build/chdman-cli createcd -i game.cue -o game.chd
```

```bash
./build/chdman-cli info -i game.chd -v
```

```bash
./build/chdman-cli extractcd -i game.chd -o out.cue
```

Ctrl-C cancels cleanly (the partial output is removed).

## Behavior notes and differences from chdman

- The CHD bytes written are identical to upstream chdman's. Differences are
  confined to the tool layer:
  - Read errors on raw/hd/dvd input files abort compression with an error.
    Upstream ignores them (marked FIXME there) and could write garbage.
  - `addmeta --valuetext "..."` strips one pair of surrounding quotes
    (upstream intends this but writes the unstripped string). The library
    API takes text verbatim.
  - Write/flush errors at the end of extraction are reported.
  - Progress callbacks are throttled to 10 per second (chdman: 2).
  - The thread limit (`chdman_context_set_num_processors`) is per context
    rather than a process-wide global.
- Metadata can only be added or removed in **uncompressed** CHDs. MAME
  opens compressed v5 CHDs read-only, and upstream `chdman addmeta`/`delmeta`
  fail the same way. Use `copy -c none` first if needed.
- `extracthd` and `extractdvd` are the same operation as `extractraw`
  (`chdman_extract_raw`), as in upstream.

## Verification

Run on macOS 27 (Apple silicon), universal Release build:

- `chdman_tests` / `chdman_tests_static` (197 checks each, against the
  dylib and the static library):
  - Raw round trip: a generated 7 MB image with random, ramp, zero and
    repeated regions. Default codecs and hunk size are checked, and so is
    dedup ("Copy from self"). Extraction is byte-exact, including partial
    extraction by hunk and byte.
  - Header SHA-1: the data SHA-1 equals an independent SHA-1 of the input.
    Single-threaded and multi-threaded runs produce byte-identical CHD
    files. The raw and overall SHA-1s are pinned to golden values, so any
    platform or compiler that differs fails the test.
  - CD: a synthetic two-track cue/bin (MODE1/2352 plus a 151-frame AUDIO
    track, which forces track padding) goes through createcd, then
    extractcd to cue/bin (byte-identical .bin), then createcd again (same
    header SHA-1s). Split-bin extraction and copy are covered too, and the
    CD header SHA-1s are pinned.
  - DVD, HD (explicit CHS, blank from template), LD (an AVI generated with
    MAME's own aviio: createld, extractld, then createld again with equal
    SHA-1, plus a frame sub-range), copy with recompression and a new hunk
    size, metadata add/get/dump/delete, verify plus fix after corrupting the
    header SHA-1.
  - Error handling (existing output, bad codec, bad hunk size, conflicting
    ranges, ABI mismatch, NULL context), cancellation from the callback
    and via `chdman_context_cancel()` (partial output is removed), and the
    log callback.
- `parity_upstream` (with `-DCHDMAN_BUILD_UPSTREAM_REFERENCE=ON`): runs 14
  commands through unmodified upstream `chdman.cpp` and through
  `chdman-cli`. All 17 outputs are byte-identical: CHDs from every create
  mode, copies, cue/toc/gdi extraction, raw and AVI extraction.
- The dylib exports exactly the 60 `chdman_*` symbols on both slices. The
  prelinked static archive defines no other global symbols and links into a
  C program together with the system zlib.
- Windows: the whole tree (library, DLL, CLI, tests, upstream reference)
  cross-compiles and links with mingw-w64 GCC 16, and the DLL exports
  exactly the 60 API functions. **MSVC builds and the tests have not been run
  on Windows yet.** Run the commands above (and `ctest`) on a Windows 11
  machine before shipping. The pinned SHA-1s in the tests catch any
  platform difference.
- The x86_64 slice of the universal build was compiled but not executed:
  this Mac has no Rosetta 2.

## Licensing

libchdman's own code is BSD-3-Clause (`LICENSE`). Every bundled component
is BSD-3-Clause, MIT, Zlib or public domain; no GPL code is compiled in. The
per-file audit and full license texts are in `THIRD_PARTY_NOTICES.md`.
Applications that ship libchdman must reproduce those notices. Local changes
to the vendored MAME sources (two small ones) are listed in
`vendor/PATCHES.md`, which also describes how to move to a newer MAME release.
