#!/usr/bin/env bash
# Turns one 1024x1024 PNG (tools/make-icon.ps1 draws packaging/macos/icon-1024.png) into a macOS .icns.
#   packaging/macos/make-icns.sh <png1024> <out.icns>
# Needs sips and iconutil, which come with macOS.
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <png1024> <out.icns>" >&2
  exit 2
fi
png=$1
out=$2

width=$(sips -g pixelWidth "$png" | awk '/pixelWidth/ { print $2 }')
height=$(sips -g pixelHeight "$png" | awk '/pixelHeight/ { print $2 }')
if [[ $width != 1024 || $height != 1024 ]]; then
  echo "$png is ${width}x${height}; the icon source must be 1024x1024" >&2
  exit 1
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
iconset=$work/PrinterHelper.iconset
mkdir "$iconset"

# The ten images iconutil expects: 16 to 512 points, each at 1x and 2x.
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  if [[ $double -eq 1024 ]]; then
    cp "$png" "$iconset/icon_${size}x${size}@2x.png"
  else
    sips -z "$double" "$double" "$png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  fi
done

mkdir -p "$(dirname "$out")"
iconutil -c icns "$iconset" -o "$out"
echo "wrote $out"
