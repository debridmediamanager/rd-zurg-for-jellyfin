#!/usr/bin/env bash
# ./build.sh [Jellyfin data directory containing plugins/]
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="${VERSION:-$(python3 -c 'import sys,xml.etree.ElementTree as E; print(E.parse(sys.argv[1]).findtext(".//Version"))' "$ROOT/src/Jellyfin.Plugin.RdZurg/Jellyfin.Plugin.RdZurg.csproj")}"
# Validate before constructing paths or passing properties to MSBuild.
python3 - "$VERSION" <<'CHECK'
import re, sys
version = sys.argv[1]
if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version) or any(int(x) > 65534 for x in version.split('.')):
    raise SystemExit('VERSION must contain four integer components between 0 and 65534')
CHECK

dotnet build "$ROOT/src/Jellyfin.Plugin.RdZurg" -c Release -p:Version="$VERSION" -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION"
python3 "$ROOT/scripts/package.py" "$ROOT" "$VERSION"

if [[ $# -gt 1 ]]; then
  echo "Usage: ./build.sh [Jellyfin data directory containing plugins/]" >&2
  exit 2
fi
if [[ $# -eq 1 ]]; then
  DEST="$1/plugins/rd-zurg_$VERSION"
  mkdir -p "$DEST"
  cp "$ROOT/artifacts/rd-zurg_$VERSION/Jellyfin.Plugin.RdZurg.dll" "$ROOT/artifacts/rd-zurg_$VERSION/meta.json" "$DEST/"
  echo "Installed into $DEST. Restart Jellyfin, then run the RD zurg sync."
fi
