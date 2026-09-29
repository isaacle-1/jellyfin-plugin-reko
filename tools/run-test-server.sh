#!/usr/bin/env bash
# Starts a Jellyfin 12.1 test instance with Reko installed.
#
# This is a development convenience, not part of the plugin. It runs a private Jellyfin out of a
# throwaway directory so Reko can be exercised end to end on a real server without touching
# anybody's actual install.
#
#   REKO_TEST_ROOT   where the instance lives   (default: /tmp/reko-test)
#   REKO_TEST_URL    the URL it is served on    (default: http://127.0.0.1:8096)
#
# The port is not a command line option: Jellyfin 12 reads it from network.xml in the config
# directory, and passes an unknown flag by printing its help text and exiting.
set -euo pipefail

ROOT="${REKO_TEST_ROOT:-/tmp/reko-test}"
URL="${REKO_TEST_URL:-http://127.0.0.1:8096}"

if [ ! -x "$ROOT/server/jellyfin" ]; then
  echo "No Jellyfin at $ROOT/server/jellyfin." >&2
  echo "Download a 12.1 build and unpack it there, or set REKO_TEST_ROOT." >&2
  echo "The release tarballs are at https://repo.jellyfin.org/files/server/linux/." >&2
  exit 1
fi

cd "$ROOT/server"

exec ./jellyfin \
  --datadir "$ROOT/data" \
  --cachedir "$ROOT/cache" \
  --configdir "$ROOT/config" \
  --logdir "$ROOT/log" \
  --webdir "$ROOT/server/jellyfin-web"
