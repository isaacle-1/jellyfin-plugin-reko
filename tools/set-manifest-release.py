#!/usr/bin/env python3
"""Point a version in manifest.json at its release asset.

A Jellyfin repository manifest is only installable once each version entry names its own artifact.
Jellyfin downloads the version's ``sourceUrl`` and verifies its ``checksum``, and it derives neither
from the repository URL or the version number, so a manifest without them lists a plugin nobody can
install.

The checksum is an MD5 hex digest, which is what Jellyfin's ``PackageVersionInfo.Checksum`` holds and
what the official repository's manifest carries. It cannot be known before the zip exists, so the
release workflow runs this after the release rather than anyone editing the manifest by hand.

Usage:  tools/set-manifest-release.py <version> <md5> [path/to/manifest.json]
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

REPOSITORY = "isaacle-1/jellyfin-plugin-reko"


def main(argv: list[str]) -> int:
    if len(argv) < 3:
        print(__doc__, file=sys.stderr)
        return 2

    version, checksum = argv[1], argv[2].strip().lower()
    path = Path(argv[3] if len(argv) > 3 else "manifest.json")

    if len(checksum) != 32 or any(c not in "0123456789abcdef" for c in checksum):
        print(f"Not an MD5 hex digest: {checksum!r}", file=sys.stderr)
        return 1

    manifest = json.loads(path.read_text(encoding="utf-8"))
    url = (
        f"https://github.com/{REPOSITORY}/releases/download/"
        f"v{version}/reko_{version}.zip"
    )

    updated = 0
    for plugin in manifest:
        for entry in plugin.get("versions", []):
            if entry.get("version") == version:
                entry["sourceUrl"] = url
                entry["checksum"] = checksum
                updated += 1

    if updated == 0:
        print(f"No version {version} in {path}.", file=sys.stderr)
        return 1

    path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{path}: {updated} version(s) at {version} now point at {url}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
