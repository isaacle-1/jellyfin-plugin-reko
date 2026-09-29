#!/usr/bin/env python3
"""Add a released version to the repository manifest on gh-pages.

Two manifests, two jobs, and conflating them is how a release silently fails to publish:

  main:manifest.json      the source of truth. Every version that exists or is planned, with its
                          changelog, ABI and timestamp. Edited by hand.

  gh-pages:manifest.json  what Jellyfin reads. Only versions that have actually been released, each
                          carrying the artifact's ``sourceUrl`` and ``checksum``. Generated, never
                          edited by hand.

They diverge by design: main may describe a version that has no artifact yet, and gh-pages must not,
because Jellyfin would offer an install that cannot complete. So the release workflow copies the new
version's metadata across from main and adds only the two fields that cannot be known before the zip
exists.

Jellyfin downloads the version's ``sourceUrl`` and verifies its ``checksum``, and it derives neither
from the repository URL or the version number, so a version without them is listed but not
installable. It also filters the catalog by ``targetAbi``, so a version without one is not offered at
all — silently, which is indistinguishable from a repository that was never added.

Usage:
  tools/set-manifest-release.py publish <version> <md5> <main-manifest.json> <out-manifest.json>
  tools/set-manifest-release.py add <version> <md5> [manifest.json]     # in place, for local work
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any

REPOSITORY = "isaacle-1/jellyfin-plugin-reko"


def artifact_url(version: str) -> str:
    return f"https://github.com/{REPOSITORY}/releases/download/v{version}/reko_{version}.zip"


def check_checksum(checksum: str) -> str:
    checksum = checksum.strip().lower()
    if len(checksum) != 32 or any(c not in "0123456789abcdef" for c in checksum):
        raise SystemExit(f"Not an MD5 hex digest: {checksum!r}")
    return checksum


def load(path: str | Path, default: Any) -> Any:
    file = Path(path)
    if not file.exists():
        return default
    text = file.read_text(encoding="utf-8").strip()
    return json.loads(text) if text else default


def entry_for(manifest: list[dict[str, Any]], version: str) -> dict[str, Any] | None:
    for plugin in manifest:
        for entry in plugin.get("versions", []):
            if entry.get("version") == version:
                return entry
    return None


def publish(version: str, checksum: str, main_path: str, out_path: str) -> int:
    """Copies one version's metadata from main into the published manifest."""
    checksum = check_checksum(checksum)
    source = load(main_path, [])
    entry = entry_for(source, version)

    if entry is None:
        raise SystemExit(f"No version {version} in {main_path}. Is the tag's commit pushed?")

    published = load(out_path, [])
    if not published:
        # First release: start from the main manifest's shape so guid, name and category are right,
        # but with no versions — those are added one released version at a time.
        published = [{k: v for k, v in source[0].items() if k != "versions"} | {"versions": []}]

    for plugin in published:
        existing = entry_for([plugin], version)
        if existing is not None:
            existing.update(entry)
        else:
            plugin.setdefault("versions", []).append(dict(entry))

    for plugin in published:
        for published_entry in plugin.get("versions", []):
            published_entry["sourceUrl"] = artifact_url(published_entry["version"])
            if published_entry["version"] == version:
                published_entry["checksum"] = checksum

    Path(out_path).write_text(json.dumps(published, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{out_path}: published {version} ({len(published[0]['versions'])} released version(s))")
    return 0


def add(version: str, checksum: str, path: str) -> int:
    """Adds a released version to a manifest in place. For local work, not for CI."""
    checksum = check_checksum(checksum)
    manifest = load(path, [])
    updated = 0

    for plugin in manifest:
        for entry in plugin.get("versions", []):
            if entry.get("version") == version:
                entry["sourceUrl"] = artifact_url(version)
                entry["checksum"] = checksum
                updated += 1

    if updated == 0:
        raise SystemExit(f"No version {version} in {path}.")

    Path(path).write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{path}: {updated} version(s) at {version} now point at {artifact_url(version)}")
    return 0


def main(argv: list[str]) -> int:
    if len(argv) < 4:
        print(__doc__, file=sys.stderr)
        return 2

    command = argv[1]
    if command == "publish":
        return publish(argv[2], argv[3], argv[4], argv[5])
    if command == "add":
        return add(argv[2], argv[3], argv[4] if len(argv) > 4 else "manifest.json")

    print(f"Unknown command {command!r}.", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
