#!/bin/sh
# Takes PDF Modifier out of the application menu again. The app's folder, your settings and the downloaded
# language data are left alone.
set -eu

data=${XDG_DATA_HOME:-$HOME/.local/share}

rm -f "$data/applications/pdfmodifier.desktop" \
      "$data/icons/hicolor/256x256/apps/pdfmodifier.png" \
      "$data/icons/hicolor/scalable/apps/pdfmodifier.svg"

if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database "$data/applications" || true; fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q -t "$data/icons/hicolor" || true; fi

echo "PDF Modifier is no longer in the application menu."
