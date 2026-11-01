#!/bin/sh
# builds the diagnostic mod next to this repo, in mods/ReShadeForRimWorldDiagnostic, ready to upload
# uses the native libraries already in 1.7/Native
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/../../ReShadeForRimWorldDiagnostic}"
dotnet build "$HERE/../Source" -c Release -p:Diagnostic=true
mkdir -p "$OUT/About" "$OUT/1.7"
# keeps PublishedFileId.txt from the first upload
cp "$HERE/About/About.xml" "$HERE/About/Preview.png" "$OUT/About/"
cp "$HERE/../LICENSE" "$OUT/"
rm -rf "$OUT/1.7/Native"
cp -r "$HERE/../1.7/Native" "$OUT/1.7/"
echo "built $OUT"
