#!/bin/bash
# Builds 7-Zip's 7z.so and libnoi_archive.dylib for macOS, each universal
# (arm64 + x86_64), into native/out/osx.
#
#   native/build-mac.sh [7-Zip source] [bit7z source]
#
# Defaults to the trees native/fetch-sources.sh unpacks.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
src7z="$(cd "${1:-$here/_deps/7zip-src}" && pwd)"
srcbit7z="$(cd "${2:-$here/_deps/bit7z-src}" && pwd)"
out="$here/out/osx"
jobs="$(sysctl -n hw.ncpu)"
mkdir -p "$out"

# 7z.so: 7-Zip's full format library, one build per architecture, then merged.
bundle="$src7z/CPP/7zip/Bundles/Format7zF"
for arch in arm64 x64; do
    (cd "$bundle" && make -j"$jobs" -f "../../cmpl_mac_$arch.mak" DISABLE_RAR=1)
done
lipo -create "$bundle/b/m_arm64/7z.so" "$bundle/b/m_x64/7z.so" -output "$out/7z.so"

# libnoi_archive.dylib: our C interface over bit7z.
cmake -S "$here/archive" -B "$here/build-osx" -DCMAKE_BUILD_TYPE=Release \
    "-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64" -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 \
    "-DNOI_BIT7Z_DIR=$srcbit7z" "-DNOI_7ZIP_DIR=$src7z"
cmake --build "$here/build-osx" --parallel "$jobs"
cp "$here/build-osx/libnoi_archive.dylib" "$out/"

lipo -info "$out/7z.so" "$out/libnoi_archive.dylib"
echo "Built $out/7z.so and $out/libnoi_archive.dylib"
