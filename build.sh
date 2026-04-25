#!/usr/bin/env bash
set -euo pipefail

PROJECT="Jellyfin.Plugin.NativeTrickplay"
VERSION="1.0.2.0"
PLUGIN_GUID="6911edf0-840d-4a71-965d-1319cbb2efd1"
PLUGIN_NAME="NativeTrickplay"

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJ_DIR="$REPO_ROOT/$PROJECT"
OUT_DIR="$REPO_ROOT/out"
PLUGINS_DIR="$HOME/Library/Application Support/jellyfin/plugins"
INSTALL_DIR="$PLUGINS_DIR/${PROJECT}_${VERSION}"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

dotnet publish "$PROJ_DIR/$PROJECT.csproj" \
  -c Release -f net9.0 \
  -o "$OUT_DIR" \
  --nologo

# Generate meta.json
TS=$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)
cat > "$OUT_DIR/meta.json" <<EOF
{
  "guid": "$PLUGIN_GUID",
  "name": "Native Trickplay",
  "description": "Generates HLS I-frame playlists so AVPlayer (Apple TV) gets native scrubbing thumbnails.",
  "owner": "local",
  "category": "General",
  "overview": "Native HLS trickplay for AVPlayer-based clients",
  "targetAbi": "10.11.0.0",
  "version": "$VERSION",
  "changelog": "Initial release",
  "timestamp": "$TS",
  "status": 0,
  "autoUpdate": false,
  "imagePath": null,
  "assemblies": ["${PROJECT}.dll"]
}
EOF

mkdir -p "$INSTALL_DIR"
# Only ship our DLL + meta + pdb; not the framework reference DLLs (Private=false)
cp "$OUT_DIR/${PROJECT}.dll" "$INSTALL_DIR/"
cp "$OUT_DIR/meta.json" "$INSTALL_DIR/"
[ -f "$OUT_DIR/${PROJECT}.pdb" ] && cp "$OUT_DIR/${PROJECT}.pdb" "$INSTALL_DIR/" || true

echo "Installed to: $INSTALL_DIR"
ls -la "$INSTALL_DIR"
