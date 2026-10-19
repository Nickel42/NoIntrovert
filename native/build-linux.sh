#!/bin/bash
# Builds 7-Zip's 7z.so and libnoi_archive.so for Linux x64 into
# native/out/linux-x64.
#
#   native/build-linux.sh [7-Zip source] [bit7z source]
#
# Defaults to the trees native/fetch-sources.sh unpacks. Needs gcc/g++, make
# and CMake. Build on the oldest distribution the app should run on: the
# libraries need that glibc (and libstdc++) or newer.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
src7z="$(cd "${1:-$here/_deps/7zip-src}" && pwd)"
srcbit7z="$(cd "${2:-$here/_deps/bit7z-src}" && pwd)"
out="$here/out/linux-x64"
jobs="$(nproc)"
mkdir -p "$out"

# 7z.so: 7-Zip's full format library, with its plain gcc makefile (portable C,
# no assembler needed), into Format7zF/b/g.
bundle="$src7z/CPP/7zip/Bundles/Format7zF"
(cd "$bundle" && make -j"$jobs" -f ../../cmpl_gcc.mak DISABLE_RAR=1)
cp "$bundle/b/g/7z.so" "$out/"

# libnoi_archive.so: our C interface over bit7z.
cmake -S "$here/archive" -B "$here/build-linux-x64" -DCMAKE_BUILD_TYPE=Release \
    "-DNOI_BIT7Z_DIR=$srcbit7z" "-DNOI_7ZIP_DIR=$src7z"
cmake --build "$here/build-linux-x64" --parallel "$jobs"
cp "$here/build-linux-x64/libnoi_archive.so" "$out/"
strip --strip-unneeded "$out/libnoi_archive.so"

file "$out/7z.so" "$out/libnoi_archive.so"
echo "Built $out/7z.so and $out/libnoi_archive.so"
