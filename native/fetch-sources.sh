#!/bin/bash
# Downloads the exact 7-Zip and bit7z sources the archive engine is built
# from, checks their SHA-256, and unpacks them into native/_deps.
# Needs curl, tar (with xz) and sha256sum or shasum.
set -euo pipefail

SEVENZIP_VERSION=26.04
SEVENZIP_URL="https://github.com/ip7z/7zip/releases/download/${SEVENZIP_VERSION}/7z2604-src.tar.xz"
SEVENZIP_SHA256=9691944c0fe0d01bb49373a704fb983fd33bc98b1738695179dfbf99ac1734f6

BIT7Z_VERSION=4.1.0
BIT7Z_URL="https://api.github.com/repos/rikyoz/bit7z/tarball/v${BIT7Z_VERSION}"
BIT7Z_SHA256=29c1b9b0dda1a4426fd3d27ba49d53a3565cbf13236a191ee0ec17de1599f5d2

here="$(cd "$(dirname "$0")" && pwd)"
deps="$here/_deps"
mkdir -p "$deps/dl-7zip" "$deps/dl-bit7z"

sha256() {
    if command -v sha256sum > /dev/null; then sha256sum "$1" | cut -d' ' -f1
    else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

fetch() { # url file expected-sha256
    if [ ! -f "$2" ] || [ "$(sha256 "$2")" != "$3" ]; then
        curl -fsSL --retry 3 -o "$2" "$1"
    fi
    local got
    got="$(sha256 "$2")"
    if [ "$got" != "$3" ]; then
        echo "SHA-256 mismatch for $1: got $got, expected $3" >&2
        exit 1
    fi
}

fetch "$SEVENZIP_URL" "$deps/dl-7zip/7z2604-src.tar.xz" "$SEVENZIP_SHA256"
fetch "$BIT7Z_URL" "$deps/dl-bit7z/bit7z-${BIT7Z_VERSION}.tar.gz" "$BIT7Z_SHA256"

rm -rf "$deps/7zip-src" "$deps/bit7z-src"
mkdir -p "$deps/7zip-src" "$deps/bit7z-src"
tar -xJf "$deps/dl-7zip/7z2604-src.tar.xz" -C "$deps/7zip-src"

# No RAR. 7-Zip's RAR decoder is under the unRAR license restriction, which is
# not compatible with NoIntrovert's GPL. The gcc makefiles take DISABLE_RAR=1
# (build-linux.sh, build-mac.sh); 7-Zip's nmake files have no such switch, so
# the RAR handler, codec and crypto objects are dropped from Arc.mak here.
arc_mak="$deps/7zip-src/CPP/7zip/Bundles/Format7zF/Arc.mak"
sed -E -i.orig -e '/^RAR_OBJS = /d' -e '/^  \$O.Rar[A-Za-z0-9]*\.obj /d' "$arc_mak"
rm -f "$arc_mak.orig"
if grep -q 'Rar' "$arc_mak"; then
    echo "error: RAR objects are still listed in $arc_mak" >&2
    exit 1
fi
tar -xzf "$deps/dl-bit7z/bit7z-${BIT7Z_VERSION}.tar.gz" -C "$deps/bit7z-src" --strip-components=1
echo "7-Zip $SEVENZIP_VERSION and bit7z $BIT7Z_VERSION ready in $deps"
