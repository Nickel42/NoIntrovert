#!/bin/bash
# Adds NoIntrovert to your desktop's app menu, with its icon, for the current
# user only (no root needed). Run it from the unpacked NoIntrovert folder:
#
#   ./install-desktop-entry.sh             install (or update after moving the folder)
#   ./install-desktop-entry.sh --uninstall remove the menu entry and icons
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
apps="$data/applications"
icons="$data/icons/hicolor"
entry="$apps/nointrovert.desktop"

refresh() {
    command -v update-desktop-database > /dev/null && update-desktop-database -q "$apps" || true
    command -v gtk-update-icon-cache > /dev/null && gtk-update-icon-cache -q -t "$icons" || true
}

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$entry"
    for dir in "$icons"/*/apps; do rm -f "$dir/nointrovert.png"; done
    refresh
    echo "Removed NoIntrovert from the app menu."
    exit 0
fi

if [ ! -x "$here/NoIntrovert" ]; then
    echo "Run this from the NoIntrovert folder (NoIntrovert not found next to it)." >&2
    exit 1
fi

# Icons: the hicolor theme folders every desktop looks in.
for src in "$here"/icons/hicolor/*/apps/nointrovert.png; do
    size_dir="$(basename "$(dirname "$(dirname "$src")")")"
    mkdir -p "$icons/$size_dir/apps"
    cp "$src" "$icons/$size_dir/apps/nointrovert.png"
done

# Menu entry, pointing at this folder's NoIntrovert.
mkdir -p "$apps"
exe="$here/NoIntrovert"
quoted="\"${exe//\"/\\\"}\""
sed -e "s|^Exec=.*|Exec=$quoted %F|" -e "/^Exec=/a TryExec=$exe" "$here/nointrovert.desktop" > "$entry"
chmod 644 "$entry"
refresh
echo "Added NoIntrovert to the app menu ($entry)."
echo "If you move this folder, run the script again."
