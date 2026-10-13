#!/bin/sh
# Makes the two Linux downloads from a published build: an archive for any distribution, and a package for Debian,
# Ubuntu, Mint and their relatives. Run by .github/workflows/downloads.yml:
#
#   sh packaging/linux/pack.sh <folder with the published program> <folder for the downloads>
set -eu

built=$1
out=$2
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/PdfModifier/PdfModifier.csproj" | head -n 1)
mkdir -p "$out"

# The archive: the program, which is one file, and the licences next to it.
stage=$(mktemp -d)
install -m 755 "$built/MasterPdfModifier" "$stage/MasterPdfModifier"
install -m 644 "$root/LICENSE" "$stage/LICENSE.txt"
install -m 644 "$root/THIRD-PARTY-NOTICES.md" "$stage/THIRD-PARTY-NOTICES.md"
tar -czf "$out/MasterPdfModifier-Linux.tar.gz" --owner=0 --group=0 -C "$stage" MasterPdfModifier LICENSE.txt THIRD-PARTY-NOTICES.md

# The package: the same program under /opt, a command of its name, and the menu entry with its icon. Text
# recognition comes along as something the package depends on, so installing it is all there is to do.
package=$(mktemp -d)
chmod 755 "$package"
install -D -m 755 "$built/MasterPdfModifier" "$package/opt/masterpdfmodifier/MasterPdfModifier"
mkdir -p "$package/usr/bin"
ln -s /opt/masterpdfmodifier/MasterPdfModifier "$package/usr/bin/masterpdfmodifier"
install -D -m 644 "$here/masterpdfmodifier.desktop" "$package/usr/share/applications/masterpdfmodifier.desktop"
install -D -m 644 "$root/PdfModifier/Assets/app.png" "$package/usr/share/icons/hicolor/256x256/apps/masterpdfmodifier.png"
install -D -m 644 "$root/PdfModifier/Assets/app.svg" "$package/usr/share/icons/hicolor/scalable/apps/masterpdfmodifier.svg"
install -D -m 644 "$root/LICENSE" "$package/usr/share/doc/masterpdfmodifier/copyright"
install -D -m 644 "$root/THIRD-PARTY-NOTICES.md" "$package/usr/share/doc/masterpdfmodifier/THIRD-PARTY-NOTICES.md"
size=$(du -sk "$package" | cut -f1)
mkdir -p "$package/DEBIAN"
cat > "$package/DEBIAN/control" <<EOF
Package: masterpdfmodifier
Version: $version
Section: graphics
Priority: optional
Architecture: amd64
Installed-Size: $size
Depends: libc6, libfontconfig1, libx11-6, libice6, libsm6, tesseract-ocr
Maintainer: Master PDF Modifier contributors <noreply@github.com>
Homepage: https://github.com/ZeronTwoi/MasterPdfModifier
Description: Edit, organise, clean up and compress PDF files
 A desktop app for working on PDF files, scans included: edit text and
 images, add and remove watermarks, merge, split, reorder and crop pages,
 clean up poor scans, make them searchable and compress them.
EOF
dpkg-deb --build --root-owner-group "$package" "$out/MasterPdfModifier.deb"
