#!/bin/bash
# Wraps a `dotnet publish -r osx-*` folder in NoIntrovert.app, signs it ad hoc
# (Apple silicon refuses to run unsigned code) and zips it.
#
#   make-app.sh <publish-dir> <output-zip> <version> [extra files to put next to the app...]
set -euo pipefail

publish="$1"
zip="$2"
version="$3"
shift 3

here="$(cd "$(dirname "$0")" && pwd)"
stage="$(mktemp -d)"
app="$stage/NoIntrovert.app"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish"/. "$app/Contents/MacOS/"
sed "s/@VERSION@/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
cp "$here/NoIntrovert.icns" "$app/Contents/Resources/NoIntrovert.icns"
chmod +x "$app/Contents/MacOS/NoIntrovert"

# Debug symbols aren't needed in a release download.
find "$app" -name '*.pdb' -delete

# codesign keeps the signature of a non-Mach-O file in Contents/MacOS in
# extended attributes, which plain zip tools drop, so the app would arrive
# "not signed". Publish single-file so only executables and dylibs land here.
stray=""
for f in "$app/Contents/MacOS"/*; do
    file -b "$f" | grep -q 'Mach-O' || stray="$stray $(basename "$f")"
done
if [ -n "$stray" ]; then
    echo "Not Mach-O, can't be signed inside Contents/MacOS:$stray" >&2
    exit 1
fi

# Sign the libraries first (dylibs, and 7-Zip's 7z.so), then the app, which seals everything inside it.
for f in "$app/Contents/MacOS"/*.dylib "$app/Contents/MacOS"/*.so; do
    [ -e "$f" ] && codesign --force --sign - "$f"
done
codesign --force --sign - "$app"
codesign --verify --deep --strict "$app"

for extra in "$@"; do
    cp "$extra" "$stage/"
done

rm -f "$zip"
(cd "$stage" && ditto -c -k --sequesterRsrc . "$zip")
rm -rf "$stage"
echo "wrote $zip"
