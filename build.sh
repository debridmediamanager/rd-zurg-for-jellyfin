#!/usr/bin/env bash
# Builds the plugin and lays it out the way Jellyfin expects to find it.
#
#   ./build.sh                       -> artifacts/rd-zurg_<version>/
#   ./build.sh /path/to/jellyfin/config   -> also installs it there
set -euo pipefail

VERSION="${VERSION:-1.0.0.0}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$ROOT/artifacts/rd-zurg_$VERSION"

dotnet build "$ROOT/src/Jellyfin.Plugin.RdZurg" -c Release

rm -rf "$OUT"
mkdir -p "$OUT"
cp "$ROOT/src/Jellyfin.Plugin.RdZurg/bin/Release/net10.0/Jellyfin.Plugin.RdZurg.dll" "$OUT/"

cat > "$OUT/meta.json" <<JSON
{
  "category": "Metadata",
  "changelog": "",
  "description": "Your Real-Debrid library in Jellyfin, without a mount.",
  "guid": "4d0b1a37-1f1c-4a3e-9f5c-2e6a7b8c9d01",
  "name": "RD zurg",
  "overview": "Serves a Real-Debrid account as a Jellyfin library",
  "owner": "debridmediamanager",
  "targetAbi": "12.0.0.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false
}
JSON

echo "built $OUT"

if [[ $# -ge 1 ]]; then
  DEST="$1/plugins/rd-zurg_$VERSION"
  mkdir -p "$DEST"
  cp "$OUT"/* "$DEST/"
  echo "installed into $DEST (restart Jellyfin to load it)"
fi
