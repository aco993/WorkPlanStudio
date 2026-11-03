#!/usr/bin/env python3
"""Fail the build when a cold start grows past its budget.

Why this gates while the Lighthouse score does not: a Lighthouse number moves
with whatever else the shared runner is doing, so a red one teaches the author
to re-run rather than look. This is a sum of file sizes. It is identical on
every machine, so when it moves, something in the build moved.

What it measures. A cold start fetches index.html, the stylesheet, the two
scripts and everything under _framework that the loader asks for; the only large
published files it does *not* ask for are the two ICU shards the visitor's
browser did not match. So the budget is every published file at the size a
browser would receive it, minus the unmatched shards.

GitHub Pages compresses on the fly and does not serve the .br siblings, so the
.gz sibling is the honest number for this deployment; a file with no sibling is
counted raw.

    python3 .github/scripts/transfer_budget.py publish/wwwroot --budget-kb 7600

Print the breakdown without asserting anything by leaving --budget-kb off, which
is what makes this usable while working on the size rather than only in CI.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

# Published, not fetched: the loader picks one shard from the browser's
# languages. Counting all three would bill every visitor for scripts no single
# visitor downloads.
ICU_SHARDS = ("icudt_CJK", "icudt_no_CJK", "icudt_EFIGS")

# Pre-compressed siblings are not themselves requested.
SIBLING_SUFFIXES = (".br", ".gz")


def transferred_size(path: Path) -> int:
    """What a browser receives for this file: its .gz sibling if one exists."""
    gzipped = path.with_name(path.name + ".gz")
    return gzipped.stat().st_size if gzipped.exists() else path.stat().st_size


def measure(root: Path) -> tuple[int, list[tuple[str, int]], int]:
    """Returns (budgeted bytes, the biggest contributors, bytes excluded as unmatched shards)."""
    files: list[tuple[str, int]] = []
    excluded = 0
    matched_shard_seen = False

    for path in sorted(root.rglob("*")):
        if not path.is_file() or path.name.endswith(SIBLING_SUFFIXES):
            continue

        relative = path.relative_to(root).as_posix()
        size = transferred_size(path)

        if any(shard in path.name for shard in ICU_SHARDS):
            # Exactly one shard is fetched. Which one depends on the reader, and
            # they differ in size, so bill the largest: a budget that passes only
            # for the cheapest visitor is not a budget.
            if not matched_shard_seen:
                matched_shard_seen = True
                files.append((relative + "  (largest ICU shard, one is fetched)", size))
                continue
            excluded += size
            continue

        files.append((relative, size))

    total = sum(size for _, size in files)
    return total, sorted(files, key=lambda item: -item[1])[:12], excluded


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path, help="the published wwwroot")
    parser.add_argument("--budget-kb", type=int, default=None,
                        help="fail above this; omit to report only")
    parser.add_argument("--slack-kb", type=int, default=None,
                        help="also fail when the budget sits more than this far above "
                             "the measurement, i.e. when it has stopped defending anything")
    args = parser.parse_args(argv)

    if not args.root.is_dir():
        print(f"{args.root}: not a directory - publish first", file=sys.stderr)
        return 2

    total, biggest, excluded = measure(args.root)
    total_kb = total // 1024

    print(f"Cold start, as GitHub Pages would serve it: {total_kb:,} KB")
    print(f"Published but not fetched (unmatched ICU shards): {excluded // 1024:,} KB")
    print()
    print("Largest contributors:")
    for name, size in biggest:
        print(f"  {size // 1024:>6,} KB  {name}")

    if args.budget_kb is None:
        return 0

    print()
    if total_kb > args.budget_kb:
        print(f"OVER BUDGET: {total_kb:,} KB against a budget of {args.budget_kb:,} KB "
              f"(+{total_kb - args.budget_kb:,} KB).", file=sys.stderr)
        print("Either find the bytes or raise the budget deliberately, in the workflow, "
              "with the reason in the commit message.", file=sys.stderr)
        return 1

    room = args.budget_kb - total_kb
    print(f"Within budget: {total_kb:,} KB of {args.budget_kb:,} KB ({room:,} KB of room).")

    # The same idea as GATE_SLACK in ci.yml, applied to bytes. A budget that
    # drifts far above what is actually shipped stops being a budget: the size
    # could double before anyone heard about it. So the gap is asserted in both
    # directions, and winning back a lot of bytes means lowering the number.
    if args.slack_kb is not None and room > args.slack_kb:
        print(f"BUDGET HAS GONE STALE: {room:,} KB of room against a slack of "
              f"{args.slack_kb:,} KB. Lower --budget-kb towards {total_kb:,} KB.", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
