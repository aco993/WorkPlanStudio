#!/usr/bin/env python3
"""Verify that every relative Markdown link resolves — file *and* anchor.

The docs cross-reference ADRs, workflows and source files heavily, and a rename
breaks them silently. Checking only the file part is not enough: the README's
coverage badges point at `docs/TESTING.md#coverage`, so renaming that heading
leaves three badge links that resolve to a file and land nowhere.

Run it from the repository root:

    python3 .github/scripts/check_doc_links.py

Exit code 0 when every link resolves, 1 otherwise, with one line per problem.
"""

from __future__ import annotations

import os
import re
import sys
import urllib.parse

LINK = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$", re.MULTILINE)
EXPLICIT_ANCHOR = re.compile(r"""<a\s+(?:id|name)=["']([^"']+)["']""", re.IGNORECASE)
SKIPPED_DIRECTORIES = {".git", "bin", "obj", "node_modules", "TestResults", "artifacts"}
EXTERNAL_PREFIXES = ("http://", "https://", "mailto:", "tel:")


def slugify(heading: str) -> str:
    """GitHub's heading slug: strip formatting, lowercase, spaces to hyphens.

    Deliberately a close approximation rather than a reimplementation of
    GitHub's exact algorithm — it handles the inline markup this repository
    actually uses (code spans, links, bold) and errs towards accepting.
    """
    text = re.sub(r"`([^`]*)`", r"\1", heading)
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)
    text = re.sub(r"[*_~]", "", text)
    text = text.strip().lower()
    text = re.sub(r"[^\w\s-]", "", text, flags=re.UNICODE)
    return re.sub(r"\s+", "-", text)


def anchors_of(text: str) -> set[str]:
    """Every fragment a reader can link to in one Markdown file."""
    found: set[str] = set()
    seen: dict[str, int] = {}
    for _, heading in HEADING.findall(text):
        slug = slugify(heading)
        if not slug:
            continue
        count = seen.get(slug, 0)
        seen[slug] = count + 1
        found.add(slug if count == 0 else f"{slug}-{count}")
    found.update(EXPLICIT_ANCHOR.findall(text))
    return found


def markdown_files(root: str) -> list[str]:
    files = []
    for directory, subdirectories, names in os.walk(root):
        subdirectories[:] = [d for d in subdirectories if d not in SKIPPED_DIRECTORIES]
        files.extend(os.path.join(directory, n) for n in names if n.endswith(".md"))
    return sorted(files)


def check(root: str = ".") -> list[str]:
    """Returns one message per broken link; empty means everything resolves."""
    anchor_cache: dict[str, set[str]] = {}
    problems: list[str] = []

    for path in markdown_files(root):
        with open(path, encoding="utf-8") as handle:
            text = handle.read()
        anchor_cache[os.path.normpath(path)] = anchors_of(text)

    for path in markdown_files(root):
        directory = os.path.dirname(path)
        with open(path, encoding="utf-8") as handle:
            text = handle.read()

        for raw in LINK.findall(text):
            target = raw.split(" ")[0].strip()
            if not target or target.startswith(EXTERNAL_PREFIXES):
                continue

            file_part, _, fragment = target.partition("#")
            file_part = urllib.parse.unquote(file_part)
            fragment = urllib.parse.unquote(fragment)

            if file_part:
                resolved = os.path.normpath(os.path.join(directory, file_part))
                if not os.path.exists(resolved):
                    problems.append(f"{path} -> {target} (no such file)")
                    continue
            else:
                resolved = os.path.normpath(path)

            if not fragment or not resolved.endswith(".md"):
                continue

            known = anchor_cache.get(resolved)
            if known is None:
                with open(resolved, encoding="utf-8") as handle:
                    known = anchors_of(handle.read())
                anchor_cache[resolved] = known

            if fragment.lower() not in known:
                problems.append(f"{path} -> {target} (no heading '#{fragment}')")

    return problems


def main(argv: list[str]) -> int:
    root = argv[0] if argv else "."
    problems = check(root)
    if problems:
        print("Broken relative links:")
        for entry in problems:
            print("  " + entry)
        return 1
    print("All relative Markdown links and anchors resolve.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
