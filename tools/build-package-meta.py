#!/usr/bin/env python3
"""Build the meta.json that goes inside a plugin zip.

This is not manifest.json. The two have the same fields and completely different shapes:

  manifest.json  is a *repository* index: an array of plugins, each with a list of versions, and it
                 is what Jellyfin reads to decide what a repository offers. Its version entries
                 carry ``sourceUrl`` and ``checksum``, because Jellyfin downloads and verifies the
                 artifact named there.

  meta.json      is a *package* descriptor: one flat object describing the single plugin in this
                 zip, at the single version this zip contains.

Shipping the array form as meta.json installs the plugin and then logs a deserialization error on
every install, because PluginManager reconciles the local manifest against the package info and
cannot read an array where it expects an object.

Usage:  tools/build-package-meta.py <version> [manifest.json] [out.json]
"""

from __future__ import annotations

import json
import sys
from pathlib import Path


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__, file=sys.stderr)
        return 2

    version = argv[1]
    manifest_path = Path(argv[2] if len(argv) > 2 else "manifest.json")
    out_path = Path(argv[3] if len(argv) > 3 else "meta.json")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    plugin = manifest[0] if isinstance(manifest, list) else manifest

    entry = next((v for v in plugin.get("versions", []) if v.get("version") == version), None)
    if entry is None:
        print(f"No version {version} in {manifest_path}.", file=sys.stderr)
        return 1

    # sourceUrl and checksum describe where the artifact lives. This file travels *inside* the
    # artifact, so carrying them would be a pointer to itself.
    meta = {
        "category": plugin.get("category", "General"),
        "changelog": entry.get("changelog", ""),
        "description": plugin.get("description", plugin.get("overview", "")),
        "guid": plugin["guid"],
        "name": plugin["name"],
        "overview": plugin.get("overview", plugin.get("description", "")),
        "owner": plugin.get("owner", ""),
        "targetAbi": entry.get("targetAbi", ""),
        "timestamp": entry.get("timestamp", ""),
        "version": version,
    }

    if plugin.get("imageUrl"):
        meta["imageUrl"] = plugin["imageUrl"]

    out_path.write_text(json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
