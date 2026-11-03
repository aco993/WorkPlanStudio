#!/usr/bin/env python3
"""Re-seal the service worker's asset manifest after the deploy edits a file.

The problem this exists for, measured on the published site:

    fetch(new Request('index.html', { integrity: '<hash from the manifest>' }))
    -> TypeError: Failed to fetch

`ServiceWorkerAssetsManifest` records a SHA-256 for every published file at
publish time. The deploy then rewrites `index.html` twice - `<base href>` for
the repository sub-path, and the absolute link-preview URLs - so by the time the
site is uploaded, one of the 106 files the worker precaches no longer matches
the hash recorded for it.

`cache.addAll` is all-or-nothing. One integrity failure rejects the install, the
registration is discarded, and what is left behind is an *empty cache with the
right name* - which looks like a worker that ran, so the failure reads as a
sandbox quirk rather than a bug. Nothing in CI caught it, because it only
happens after the steps that run between publish and upload.

So: recompute the hash of the files that were deliberately edited, and assert
that nothing else moved.

    python3 .github/scripts/reseal_sw_assets.py release/wwwroot --resealed index.html

The assertion is the point. Re-hashing the whole manifest would make this script
agree with whatever is on disk, including a file some future step corrupts by
accident - and an integrity manifest that cannot disagree with the disk is not
an integrity manifest. A file named with --resealed is expected to have changed
and must have; any other mismatch fails the build with both hashes.

Note on the pre-compressed siblings: the SDK leaves `.br` and `.gz` out of the
manifest, so an edited file's stale siblings are not a precache problem. They
would be a problem on a host that serves them - GitHub Pages compresses on the
fly and does not - so if this ever moves to such a host, the siblings have to be
regenerated too, not just re-hashed.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import sys
from pathlib import Path

MANIFEST = "service-worker-assets.js"
PREFIX = re.compile(r"^\s*self\.assetsManifest\s*=\s*")
SUFFIX = re.compile(r";\s*$")


def hash_of(path: Path) -> str:
    """The same shape the SDK writes: sha256- plus standard base64."""
    digest = hashlib.sha256(path.read_bytes()).digest()
    return "sha256-" + base64.b64encode(digest).decode("ascii")


def load(manifest_path: Path) -> tuple[dict, str, str]:
    """Returns (manifest, the text before the object, the text after it)."""
    text = manifest_path.read_text(encoding="utf-8")
    body = text
    head_match = PREFIX.search(body)
    if not head_match:
        raise ValueError(f"{manifest_path}: no 'self.assetsManifest =' assignment")
    head = body[: head_match.end()]
    body = body[head_match.end():]
    tail_match = SUFFIX.search(body)
    tail = body[tail_match.start():] if tail_match else ""
    if tail_match:
        body = body[: tail_match.start()]
    return json.loads(body), head, tail


def reseal(root: Path, resealed: set[str]) -> tuple[list[str], list[str], list[str]]:
    """Returns (updated, unchanged-but-expected-to-change, mismatched)."""
    manifest_path = root / MANIFEST
    manifest, head, tail = load(manifest_path)

    updated: list[str] = []
    untouched: list[str] = []
    mismatched: list[str] = []

    for asset in manifest["assets"]:
        url = asset["url"]
        path = root / url
        if not path.is_file():
            # Nothing to compare against; leave the entry alone rather than
            # invent a hash for a file the upload will not carry either.
            continue

        actual = hash_of(path)
        if actual == asset["hash"]:
            if url in resealed:
                untouched.append(url)
            continue

        if url in resealed:
            asset["hash"] = actual
            updated.append(url)
        else:
            mismatched.append(f"{url}\n    manifest: {asset['hash']}\n    on disk:  {actual}")

    if updated:
        manifest_path.write_text(head + json.dumps(manifest, indent=2) + tail, encoding="utf-8")

    return updated, untouched, mismatched


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path, help="the published wwwroot")
    parser.add_argument("--resealed", action="append", default=[],
                        help="a published path that a later step edited on purpose; repeatable")
    args = parser.parse_args(argv)

    manifest_path = args.root / MANIFEST
    if not manifest_path.is_file():
        print(f"{manifest_path}: not found - is ServiceWorkerAssetsManifest set?", file=sys.stderr)
        return 2

    try:
        updated, untouched, mismatched = reseal(args.root, set(args.resealed))
    except (ValueError, KeyError, json.JSONDecodeError) as error:
        print(f"{manifest_path}: {error}", file=sys.stderr)
        return 2

    for url in updated:
        print(f"re-sealed {url}")

    if untouched:
        print("These were named with --resealed but are byte-identical to what was published:",
              file=sys.stderr)
        for url in untouched:
            print(f"  {url}", file=sys.stderr)
        print("The step that was supposed to edit them did nothing. Either it broke, or the "
              "flag is stale - both are worth knowing before this ships.", file=sys.stderr)
        return 1

    if mismatched:
        print("These changed after publish and nobody said they would:", file=sys.stderr)
        for entry in mismatched:
            print(f"  {entry}", file=sys.stderr)
        print("The service worker precaches with integrity, so each one of these would reject "
              "the install. Add it to --resealed if the edit is deliberate.", file=sys.stderr)
        return 1

    print(f"Asset manifest sealed: {len(updated)} re-hashed, the rest verified against disk.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
