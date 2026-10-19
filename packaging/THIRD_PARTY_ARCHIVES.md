# Archive engine: third-party components

NoIntrovert's Archive tab uses two open-source components. Their full license
texts are in this download as `LICENSE-7-Zip.txt` and `LICENSE-bit7z.txt`.

## 7-Zip 26.04 (`7z.dll` on Windows, `7z.so` on macOS)

Copyright Igor Pavlov. GNU LGPL 2.1 or later, with some parts under the BSD
3-clause license. See `LICENSE-7-Zip.txt` for the exact terms.

Built from the official source release
https://github.com/ip7z/7zip/releases/tag/26.04 (`7z2604-src.tar.xz`)
**without RAR support**: 7-Zip's RAR code is under the unRAR license
restriction, which is not compatible with NoIntrovert's GPL, so it is left
out of the build (`DISABLE_RAR=1` with the gcc makefiles; on Windows the RAR
objects are removed from `Bundles/Format7zF/Arc.mak`, see
`native/fetch-sources.sh`). The unRAR restriction in `LICENSE-7-Zip.txt`
therefore does not apply to this build.
Under the LGPL you may replace this library with your own build of the same
source, or a later compatible version, by swapping the file next to the app.

## bit7z 4.1.0 (inside `noi_archive.dll` / `libnoi_archive.dylib`)

Copyright Riccardo Ostani, under the Mozilla Public License 2.0. Used
unmodified; its source is available at
https://github.com/rikyoz/bit7z/releases/tag/v4.1.0.

`noi_archive` itself (NoIntrovert's C interface over bit7z) is part of
NoIntrovert; its source is in the `native/archive` folder of the NoIntrovert
repository.
