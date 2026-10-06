#!/usr/bin/env bash
# A release's notes: the CHANGELOG.md section for the version ("## 0.1.0"), or the Unreleased one when there is none.
#
#   tools/Release/notes.sh <version> > notes.md
set -euo pipefail

version=${1:?version, e.g. 0.1.0}
changelog="$(cd "$(dirname "$0")/../.." && pwd)/CHANGELOG.md"

section() {
  awk -v heading="## $1" '
    $0 == heading { inside = 1; next }
    inside && /^## / { exit }
    inside { print }
  ' "$changelog" | sed -e '/./,$!d' | tr -d '\r'
}

notes=$(section "$version")
if [ -z "$notes" ]; then
  notes=$(section "Unreleased")
fi

printf '%s\n\n' "$notes"
cat <<EOF
## Downloads

| | Windows | macOS | Linux |
| --- | --- | --- | --- |
| Desktop app | \`ClassicMac-$version-win-x64.zip\` | \`ClassicMac-$version-osx-arm64.zip\` (Apple silicon), \`-osx-x64.zip\` (Intel) | \`ClassicMac-$version-linux-x64.tar.gz\` (run \`install.sh\`) |
| Command line | \`ClassicMac-CLI-$version-win-x64.zip\` | \`ClassicMac-CLI-$version-osx-arm64.tar.gz\`, \`-osx-x64.tar.gz\` | \`ClassicMac-CLI-$version-linux-x64.tar.gz\` |

Everything is self-contained: no .NET install needed. The builds are not signed yet: on macOS open the app the first
time with right-click ▸ Open; on Windows choose More info ▸ Run anyway if SmartScreen asks.
EOF
