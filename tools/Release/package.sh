#!/usr/bin/env bash
# Builds the downloads for one platform: the desktop app and the command line, self-contained (no .NET needed), each
# packed the platform's way. Used by .github/workflows/release.yml; runs locally too (docs/releasing.md).
#
#   tools/Release/package.sh <runtime> <version> [output folder]
#   tools/Release/package.sh win-x64 0.1.0 dist
#
# Runtimes: win-x64, win-arm64, osx-arm64, osx-x64, linux-x64, linux-arm64. Writes into the output folder (default dist):
#   ClassicMac-<version>-<runtime>.zip         Windows: the app's folder, ClassicMac.exe
#   ClassicMac-<version>-<runtime>.zip         macOS: ClassicMac.app (with its icon and Info.plist)
#   ClassicMac-<version>-<runtime>.tar.gz      Linux: the app, its icons, a .desktop file and install.sh
#   ClassicMac-CLI-<version>-<runtime>.zip|tar.gz  the command line, one file (classicmac or classicmac.exe)
set -euo pipefail

rid=${1:?runtime, e.g. win-x64}
version=${2:?version, e.g. 0.1.0}
out=${3:-dist}
root=$(cd "$(dirname "$0")/../.." && pwd)
here="$root/tools/Release"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"
out=$(cd "$out" && pwd)

# The first Python that runs (Windows may have a python3 that only opens the Store).
for python in python3 python; do
  if "$python" -c "" 2> /dev/null; then
    break
  fi
done

# A zip holding the folder (its name at the top), the same on every runner; file modes kept.
zip_folder() {
  local folder=$1 zip=$2
  "$python" - "$folder" "$zip" <<'EOF'
import os, sys, zipfile
folder, target = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
    base = os.path.dirname(folder)
    for dirpath, _, names in os.walk(folder):
        for name in sorted(names):
            path = os.path.join(dirpath, name)
            info = zipfile.ZipInfo.from_file(path, os.path.relpath(path, base))
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(path, "rb") as f:
                z.writestr(info, f.read())
EOF
}

publish() {
  dotnet publish "$root/src/$1" -c Release -r "$rid" --self-contained -p:Version="$version" -o "$2" "${@:3}"
}

# The command line: one file.
cli="$work/ClassicMac-CLI-$version-$rid"
publish ClassicMac.Cli "$cli" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
rm -f "$cli"/*.pdb "$cli"/*.xml
cp "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$cli/"

# The app: a folder (its native libraries beside it).
app="$work/ClassicMac-$version-$rid"
publish ClassicMac.App "$app" -p:DebugType=none
rm -f "$app"/*.pdb

case "$rid" in
  win-*)
    cp "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$app/"
    zip_folder "$app" "$out/ClassicMac-$version-$rid.zip"
    zip_folder "$cli" "$out/ClassicMac-CLI-$version-$rid.zip"
    ;;
  osx-*)
    bundle="$work/mac/ClassicMac.app"
    mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
    cp -R "$app"/. "$bundle/Contents/MacOS/"
    cp "$root/design/icon/classicmac.icns" "$bundle/Contents/Resources/classicmac.icns"
    cp "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$bundle/Contents/Resources/"
    sed "s/@VERSION@/$version/g" "$here/Info.plist" > "$bundle/Contents/Info.plist"
    chmod +x "$bundle/Contents/MacOS/ClassicMac"
    # ditto keeps the bundle's permissions and attributes, as Finder's Compress does.
    if command -v ditto > /dev/null; then
      (cd "$work/mac" && ditto -c -k --keepParent ClassicMac.app "$out/ClassicMac-$version-$rid.zip")
    else
      zip_folder "$bundle" "$out/ClassicMac-$version-$rid.zip"
    fi
    chmod +x "$cli/classicmac"
    tar -C "$work" -czf "$out/ClassicMac-CLI-$version-$rid.tar.gz" "ClassicMac-CLI-$version-$rid"
    ;;
  linux-*)
    linux="$work/linux/ClassicMac-$version-$rid"
    mkdir -p "$linux/lib" "$linux/share/applications" "$linux/share/icons/hicolor/scalable/apps"
    cp -R "$app"/. "$linux/lib/"
    chmod +x "$linux/lib/ClassicMac"
    for n in 16 24 32 48 64 128 256 512; do
      mkdir -p "$linux/share/icons/hicolor/${n}x${n}/apps"
      cp "$root/design/icon/png/classicmac-$n.png" "$linux/share/icons/hicolor/${n}x${n}/apps/classicmac.png"
    done
    cp "$root/design/icon/source/classicmac.svg" "$linux/share/icons/hicolor/scalable/apps/classicmac.svg"
    cp "$here/classicmac.desktop" "$linux/share/applications/classicmac.desktop"
    cp "$here/install.sh" "$linux/install.sh"
    chmod +x "$linux/install.sh"
    cp "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$linux/"
    tar -C "$work/linux" -czf "$out/ClassicMac-$version-$rid.tar.gz" "ClassicMac-$version-$rid"
    chmod +x "$cli/classicmac"
    tar -C "$work" -czf "$out/ClassicMac-CLI-$version-$rid.tar.gz" "ClassicMac-CLI-$version-$rid"
    ;;
  *)
    echo "Unknown runtime $rid" >&2
    exit 2
    ;;
esac

ls -l "$out"
