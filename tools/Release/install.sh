#!/usr/bin/env sh
# Installs ClassicMac for the current user: the app in ~/.local/lib/classicmac, a ClassicMac command in ~/.local/bin,
# its icons and its menu entry. Run from the unpacked folder: ./install.sh   (./install.sh --uninstall removes it)
set -eu

here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
lib=$HOME/.local/lib/classicmac
bin=$HOME/.local/bin

if [ "${1:-}" = "--uninstall" ]; then
  rm -rf "$lib" "$bin/ClassicMac" "$data/applications/classicmac.desktop"
  find "$data/icons/hicolor" -name 'classicmac.*' -delete 2>/dev/null || true
  echo "ClassicMac removed."
  exit 0
fi

rm -rf "$lib"
mkdir -p "$lib" "$bin" "$data/applications"
cp -R "$here/lib/." "$lib/"
ln -sf "$lib/ClassicMac" "$bin/ClassicMac"
cp -R "$here/share/icons" "$data/"
sed "s|^Exec=ClassicMac|Exec=$lib/ClassicMac|" "$here/share/applications/classicmac.desktop" > "$data/applications/classicmac.desktop"
command -v update-desktop-database > /dev/null && update-desktop-database "$data/applications" || true
command -v gtk-update-icon-cache > /dev/null && gtk-update-icon-cache -q "$data/icons/hicolor" || true
echo "ClassicMac installed: $lib (run ClassicMac, or find it in the applications menu)."
