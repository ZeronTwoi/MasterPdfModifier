#!/bin/sh
# Puts PDF Modifier into the application menu of the current user, with its icon, and offers it under "Open with"
# for PDF files. Run it from the folder the app was unpacked into:  sh install.sh
# Nothing here needs root, and nothing is copied except the menu entry and the icon: the app stays where it is.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}

# An archive made on Windows does not carry the mark that says "this is a program".
chmod +x "$here/PdfModifier"

mkdir -p "$data/applications" "$data/icons/hicolor/256x256/apps" "$data/icons/hicolor/scalable/apps"
cp "$here/pdfmodifier.png" "$data/icons/hicolor/256x256/apps/pdfmodifier.png"
cp "$here/pdfmodifier.svg" "$data/icons/hicolor/scalable/apps/pdfmodifier.svg"

# The entry names the program by its full path, so it works without the folder being on the PATH. The path is
# written the way such entries want it: in quotes, with the characters that mean something there escaped.
program=$(printf '%s' "$here/PdfModifier" | sed -e 's/\\/\\\\\\\\/g' -e 's/["`$]/\\\\&/g' -e 's/%/%%/g')
{
    grep -v '^Exec=' "$here/pdfmodifier.desktop"
    printf 'Exec="%s" %%F\n' "$program"
} > "$data/applications/pdfmodifier.desktop"

if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database "$data/applications" || true; fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q -t "$data/icons/hicolor" || true; fi

echo "PDF Modifier is in the application menu now. Move this folder and the entry stops working; run this again then."
