#!/usr/bin/env bash
# Builds PrinterHelper.app from a self-contained publish and signs it ad hoc:
#   dotnet publish src/PrinterHelper -c Release -r osx-arm64 --self-contained -p:PublishReadyToRun=true -p:DebugType=none -o out/pub
#   packaging/macos/make-app.sh out/pub out 2.0.0 [PrinterHelper.icns]
# Without the icns argument the icon is made from icon-1024.png next to this script. The license files of the
# bundled packages come from the app's restore (packaging/copy-notices.py), so publish from this checkout first.
# Needs Xcode's command line tools (codesign, dwarfdump, lipo, plutil) and python3.
set -euo pipefail

if [[ $# -lt 3 || $# -gt 4 ]]; then
  echo "usage: $0 <publishDir> <outDir> <version> [icns]" >&2
  exit 2
fi
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
pub=$(cd "$1" && pwd)
mkdir -p "$2"
out=$(cd "$2" && pwd)
version=$3
icns=${4:-}
exe=PrinterHelper

# Both bundle versions allow only up to three integers, so a "-beta.1" or "+build" suffix is dropped there.
plist_version=${version%%[-+]*}
version_pattern='^[0-9]+(\.[0-9]+){0,2}$'
if [[ ! $plist_version =~ $version_pattern ]]; then
  echo "version '$version' must start with up to three numbers, like 2.0.0" >&2
  exit 2
fi
if [[ ! -f $pub/$exe || ! -f $pub/$exe.dll ]]; then
  echo "$pub has no $exe apphost: publish with -r osx-arm64 or -r osx-x64" >&2
  exit 1
fi
if [[ ! -f $pub/libcoreclr.dylib ]]; then
  echo "$pub is not a self-contained publish (no libcoreclr.dylib)" >&2
  exit 1
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

app=$out/$exe.app
contents=$app/Contents
macos=$contents/MacOS
resources=$contents/Resources
main=$macos/$exe

rm -rf "$app"
mkdir -p "$macos" "$resources/ko.lproj"
ditto "$pub" "$macos"

plist=$contents/Info.plist
cp "$here/Info.plist" "$plist"
plutil -replace CFBundleShortVersionString -string "$plist_version" "$plist"
plutil -replace CFBundleVersion -string "$plist_version" "$plist"
plutil -lint "$plist"
bundle_id=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$plist")
for key in CFBundleExecutable CFBundleName CFBundleDisplayName; do
  # The base names must be the bundle's file name, or Finder ignores the Korean name in ko.lproj.
  if [[ $(/usr/libexec/PlistBuddy -c "Print :$key" "$plist") != "$exe" ]]; then
    echo "Info.plist $key is not $exe" >&2
    exit 1
  fi
done
printf 'APPL????' > "$contents/PkgInfo"

if [[ -z $icns ]]; then
  icns=$work/$exe.icns
  "$here/make-icns.sh" "$here/icon-1024.png" "$icns"
fi
cp "$icns" "$resources/$exe.icns"
cp "$here/ko.lproj/InfoPlist.strings" "$resources/ko.lproj/InfoPlist.strings"
plutil -lint "$resources/ko.lproj/InfoPlist.strings"

arch=$(lipo -archs "$main")
case $arch in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) echo "unexpected apphost architecture: $arch" >&2; exit 1 ;;
esac

# The notices the bundled components ask for: ours, the PDFium texts and the packages' own license files.
cp "$repo/LICENSE" "$repo/THIRD-PARTY-NOTICES.md" "$resources/"
python3 -I "$repo/packaging/copy-notices.py" "$rid" "$resources/licenses"

chmod +x "$main"
if [[ -f $macos/createdump ]]; then chmod +x "$macos/createdump"; fi

# Finder info or resource forks make codesign refuse to sign. Some attributes (com.apple.provenance) cannot be
# removed and do not matter; the verify at the end is the real check.
xattr -cr "$app" 2>/dev/null || true

# The SDK copies the apphost from a template, so every .NET app of that version has the same LC_UUID. macOS
# tells programs apart by the main executable's UUID for local network privacy (NECP; Apple's TN3179 and its
# Local Network Privacy FAQ), and the Local Network permission of one .NET app could then stick to another.
uuid_of() { dwarfdump --uuid "$1" | awk '{ print $2 }'; }
main_uuid=$(uuid_of "$main")
nuget=${NUGET_PACKAGES:-$HOME/.nuget/packages}
dotnet_root=${DOTNET_ROOT:-}
if [[ -z $dotnet_root ]] && command -v dotnet >/dev/null; then
  dotnet_root=$(dirname "$(python3 -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "$(command -v dotnet)")")
fi
# The SDK takes the template from its own packs folder when it has that version, else from the NuGet cache.
template_uuids=
for template in "$nuget/microsoft.netcore.app.host.$rid"/*/runtimes/"$rid"/native/apphost \
                "$dotnet_root/packs/Microsoft.NETCore.App.Host.$rid"/*/runtimes/"$rid"/native/apphost; do
  if [[ -f $template ]]; then template_uuids="$template_uuids $(uuid_of "$template")"; fi
done
if [[ -z $template_uuids ]]; then
  echo "no apphost template for $rid found; giving $exe its own LC_UUID anyway"
  rewrite=1
elif [[ " $template_uuids " == *" $main_uuid "* ]]; then
  echo "$exe has the apphost template's LC_UUID $main_uuid; replacing it"
  rewrite=1
else
  echo "$exe already has its own LC_UUID $main_uuid"
  rewrite=0
fi
if [[ $rewrite -eq 1 ]]; then
  # Before signing: the new bytes change the page hashes. The UUID is derived from the bundle id, version and
  # architecture, so the same build always gets the same one.
  python3 -I - "$main" "$bundle_id/$version" <<'PY'
import struct
import sys
import uuid

path, name = sys.argv[1], sys.argv[2]
data = bytearray(open(path, 'rb').read())
arch_names = {0x01000007: 'x86_64', 0x0100000C: 'arm64'}

if struct.unpack_from('>I', data, 0)[0] == 0xCAFEBABE:  # universal binary: fat_arch entries hold the offsets
    count = struct.unpack_from('>I', data, 4)[0]
    slices = [struct.unpack_from('>I', data, 8 + 20 * i + 8)[0] for i in range(count)]
else:
    slices = [0]

found = 0
for base in slices:
    if struct.unpack_from('<I', data, base)[0] != 0xFEEDFACF:
        sys.exit('not a 64-bit Mach-O at offset %d' % base)
    cputype, _, _, ncmds = struct.unpack_from('<4I', data, base + 4)
    arch = arch_names.get(cputype, hex(cputype))
    offset = base + 32  # load commands follow the 32-byte mach_header_64
    for _ in range(ncmds):
        cmd, size = struct.unpack_from('<2I', data, offset)
        if cmd == 0x1B:  # LC_UUID: cmd, cmdsize, then the 16 UUID bytes
            new = uuid.uuid5(uuid.NAMESPACE_URL, name + '/' + arch)
            data[offset + 8:offset + 24] = new.bytes
            print('%s: LC_UUID is now %s' % (arch, str(new).upper()))
            found += 1
        offset += size
if found != len(slices):
    sys.exit('expected one LC_UUID per architecture, found %d' % found)
with open(path, 'r+b') as f:
    f.write(data)
PY
  main_uuid=$(uuid_of "$main")
  if [[ " $template_uuids " == *" $main_uuid "* ]]; then
    echo "LC_UUID is still the template's" >&2
    exit 1
  fi
fi

# Ad hoc, inside out, without the hardened runtime (.NET would then need JIT entitlements). codesign treats
# every file in Contents/MacOS as nested code, the .dll and .json files too (their signatures go into extended
# attributes), and refuses to sign the bundle while one of them is unsigned. The bundle signature covers the
# main executable and seals Resources.
find "$macos" -type f ! -path "$main" -print0 | xargs -0 codesign --force --sign - --timestamp=none
codesign --force --sign - --timestamp=none "$app"
if ! codesign --verify --deep --strict --verbose=2 "$app"; then
  echo "signature check failed for $app" >&2
  exit 1
fi
codesign --display --verbose=2 "$app" 2>&1 | grep -E '^(Identifier|Format|CodeDirectory|Signature)' || true

echo "built $app ($bundle_id $version, $arch, LC_UUID $main_uuid)"
