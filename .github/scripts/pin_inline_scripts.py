#!/usr/bin/env python3
"""Replace 'unsafe-inline' in the published page's script-src with hashes.

The Content-Security-Policy in index.html allowed every inline script, because
Blazor writes a fingerprinted <script type="importmap"> whose content changes on
every build: a hash written into the source goes stale immediately, and a static
host cannot mint a nonce. That reasoning holds for the *source*. It does not hold
for the *published* page, which no longer changes once the deploy has finished
editing it - so the deploy can hash what is actually there.

    python3 .github/scripts/pin_inline_scripts.py release/wwwroot/index.html

For every inline <script> (no src attribute, any type - an import map is subject
to script-src too) it computes 'sha256-...' over the exact bytes between the
tags, and rewrites script-src to list those hashes in place of 'unsafe-inline'.
Nothing else in the policy moves: style-src keeps 'unsafe-inline', which Blazor's
per-render element styles still need.

It fails rather than guesses: no policy, no script-src, no 'unsafe-inline' to
replace, or no inline script at all are each a build error, because each means
the page is not the page this script was written for.

Run it after the last step that edits index.html. A later edit to an inline
script would leave a hash that no longer matches, and the browser would refuse
that script - which is the point of the hash, and why the deploy runs the
browser check again afterwards.
"""

from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import re
import sys
from pathlib import Path

INLINE_SCRIPT = re.compile(r"<script(?![^>]*\bsrc\s*=)[^>]*>(.*?)</script>", re.IGNORECASE | re.DOTALL)
COMMENT = re.compile(r"<!--.*?-->", re.DOTALL)
POLICY = re.compile(r'(<meta\s+http-equiv="Content-Security-Policy"\s+content=")([^"]*)(")', re.IGNORECASE)
SCRIPT_SRC = re.compile(r"(script-src)([^;]*)(;?)")


def inline_script_hashes(html: str) -> list[str]:
    """Every inline script's CSP source expression, in document order, deduplicated."""
    seen: list[str] = []
    # Comments first: the page's own comment explaining this policy quotes
    # `<script type="importmap">`, and a search that does not skip comments
    # starts there and hashes the comment's tail together with the real map.
    # Measured: the browser refused the import map and Blazor never started.
    for body in INLINE_SCRIPT.findall(COMMENT.sub("", html)):
        digest = hashlib.sha256(body.encode("utf-8")).digest()
        token = "'sha256-" + base64.b64encode(digest).decode("ascii") + "'"
        if token not in seen:
            seen.append(token)
    return seen


def pin(html: str) -> tuple[str, list[str]]:
    """The page with script-src pinned to its inline scripts, and the hashes used."""
    hashes = inline_script_hashes(html)
    if not hashes:
        raise ValueError("no inline <script> found; this is not the page the policy was written for")

    policy_match = POLICY.search(html)
    if policy_match is None:
        raise ValueError("no Content-Security-Policy <meta> found")

    policy = policy_match.group(2)
    script_match = SCRIPT_SRC.search(policy)
    if script_match is None:
        raise ValueError("the policy has no script-src directive")

    sources = script_match.group(2)
    if "'unsafe-inline'" not in sources:
        raise ValueError("script-src has no 'unsafe-inline' to replace; already pinned?")

    pinned_sources = sources.replace("'unsafe-inline'", " ".join(hashes))
    new_policy = policy[: script_match.start(2)] + pinned_sources + policy[script_match.end(2):]
    new_html = html[: policy_match.start(2)] + new_policy + html[policy_match.end(2):]
    return new_html, hashes


def refresh_compressed_siblings(page: Path) -> None:
    """Keep pre-compressed copies of the page in step with it.

    A host that serves `.gz`/`.br` siblings (the Performance workflow's server
    does; GitHub Pages does not) would otherwise serve the page as it was before
    the policy was pinned. The standard library has gzip but not Brotli, so the
    gzip sibling is rewritten and a Brotli one is removed rather than left stale;
    such a host then falls back to gzip.
    """
    gz = page.with_name(page.name + ".gz")
    br = page.with_name(page.name + ".br")
    if gz.exists() or br.exists():
        gz.write_bytes(gzip.compress(page.read_bytes(), compresslevel=9, mtime=0))
    if br.exists():
        br.unlink()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("pages", type=Path, nargs="+", help="published HTML pages to pin")
    args = parser.parse_args(argv)

    for page in args.pages:
        html = page.read_text(encoding="utf-8")
        try:
            pinned, hashes = pin(html)
        except ValueError as error:
            print(f"{page}: {error}", file=sys.stderr)
            return 1
        page.write_text(pinned, encoding="utf-8", newline="")
        print(f"{page}: script-src pinned to {len(hashes)} inline script(s), 'unsafe-inline' removed")
        refresh_compressed_siblings(page)
    return 0


if __name__ == "__main__":
    sys.exit(main())
