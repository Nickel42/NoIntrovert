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
chmod +x "$app/Contents/MacOS/NoIntrovert"

# Debug symbols aren't needed in a release download.
find "$app" -name '*.pdb' -delete

codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

for extra in "$@"; do
    cp "$extra" "$stage/"
done

rm -f "$zip"
(cd "$stage" && ditto -c -k --sequesterRsrc . "$zip")
rm -rf "$stage"
echo "wrote $zip"
