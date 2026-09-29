#!/usr/bin/env bash
# Rebuilds Reko and drops it into the test instance, then restarts the server.
#
#   REPO             the checkout            (default: the parent of this script)
#   REKO_TEST_ROOT   where the instance lives (default: /tmp/reko-test)
#   REKO_TEST_URL    the instance's URL      (default: http://127.0.0.1:8096)
set -euo pipefail

REPO="${REPO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
ROOT="${REKO_TEST_ROOT:-/tmp/reko-test}"
URL="${REKO_TEST_URL:-http://127.0.0.1:8096}"
PLUGIN_DIR="$ROOT/data/plugins/Reko_1.0.0.0"
ARTIFACTS="${TMPDIR:-/tmp}/reko-artifacts"

if ! command -v dotnet > /dev/null 2>&1; then
  echo "dotnet is not on PATH. Install the .NET 10 SDK first." >&2
  exit 1
fi

echo "==> building"

# The version comes from the manifest rather than from here. A pinned copy in a deploy script is a
# papercut that eventually tests a build nobody ships.
VERSION="$(python3 -c "import json; print(json.load(open('$REPO/manifest.json'))[0]['versions'][0]['version'])")"
PLUGIN_DIR="$ROOT/data/plugins/Reko_$VERSION"

rm -rf "$ARTIFACTS"
dotnet publish "$REPO/Jellyfin.Plugin.Reko/Jellyfin.Plugin.Reko.csproj" \
  -c Release -f net10.0 --nologo \
  -p:PublishDir="$ARTIFACTS/" \
  -p:Version="$VERSION" > /dev/null

echo "==> stopping server"
pkill -f "$ROOT/server/jellyfin" 2> /dev/null || true
for _ in $(seq 1 20); do
  if ! pgrep -f "$ROOT/server/jellyfin" > /dev/null 2>&1; then break; fi
  sleep 0.5
done
sleep 1

echo "==> installing Reko $VERSION"
# Every version's directory is cleared, not just this one's: an upgrade that leaves the old one
# behind gives the server two copies of the same plugin.
rm -rf "$ROOT"/data/plugins/Reko_*
mkdir -p "$PLUGIN_DIR"
cp "$ARTIFACTS/Jellyfin.Plugin.Reko.dll" "$PLUGIN_DIR/"

echo "==> starting server"
# setsid detaches the server from this script's process group, so it survives the shell that ran the
# deploy exiting rather than being killed with it.
setsid env REKO_TEST_ROOT="$ROOT" REKO_TEST_URL="$URL" \
  "$REPO/tools/run-test-server.sh" > "$ROOT/server.out" 2>&1 < /dev/null &

for _ in $(seq 1 90); do
  if curl -sf -o /dev/null "$URL/Reko/early.js"; then
    echo "==> up at $URL"
    exit 0
  fi
  sleep 1
done

echo "==> FAILED to start"
tail -20 "$ROOT/server.out"
exit 1
