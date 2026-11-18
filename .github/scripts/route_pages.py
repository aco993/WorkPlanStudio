#!/usr/bin/env python3
"""Give every top-level route of the app a page of its own on GitHub Pages.

A static host knows nothing about Blazor's router. GitHub Pages answered a
deep link such as /schedule by serving 404.html - a copy of index.html - so the
application ran, but the response said 404: a crawler, a link checker or a
monitoring probe saw a broken page, and the browser console logged a failed
document load on every direct visit.

GitHub Pages does serve `schedule.html` for `/schedule`, with status 200 and
without a redirect. (A `schedule/index.html` would not do: the host answers
`/schedule` with a 301 to `/schedule/`, and the router would see a different
address.) So the deploy writes one copy of the finished index.html per route:

    python3 .github/scripts/route_pages.py release/wwwroot src/WorkPlanStudio/Pages

The routes are read from the `@page` directives, so a new page cannot be
forgotten. Left out on purpose:
  - "/" - that is index.html itself;
  - routes with a parameter ("/work-plans/{Id:int}") - no file can stand for
    every value; they keep the 404.html fallback;
  - routes below another route ("/work-plans/new") - writing a folder
    `work-plans/` next to `work-plans.html` would invite the same redirect;
  - "/not-found" - a not-found page answering 200 would be the opposite lie.

Run it after the last edit to index.html: the copies are taken as they are.
"""

from __future__ import annotations

import argparse
import re
import shutil
import sys
from pathlib import Path

PAGE_DIRECTIVE = re.compile(r'^@page\s+"([^"]*)"', re.MULTILINE)
EXCLUDED = {"not-found"}


def routes(pages_dir: Path) -> list[str]:
    """Top-level, parameter-free routes declared under pages_dir, sorted."""
    found: set[str] = set()
    for razor in pages_dir.rglob("*.razor"):
        for route in PAGE_DIRECTIVE.findall(razor.read_text(encoding="utf-8")):
            name = route.strip("/")
            if not name or "/" in name or "{" in name or name in EXCLUDED:
                continue
            found.add(name)
    return sorted(found)


def write_route_pages(wwwroot: Path, names: list[str]) -> list[Path]:
    index = wwwroot / "index.html"
    if not index.is_file():
        raise FileNotFoundError(f"{index} does not exist")
    written = []
    for name in names:
        target = wwwroot / f"{name}.html"
        if target.exists():
            raise FileExistsError(f"{target} already exists; a published file would be overwritten")
        shutil.copyfile(index, target)
        written.append(target)
    return written


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("wwwroot", type=Path, help="the published wwwroot, after its last edit")
    parser.add_argument("pages", type=Path, help="the folder holding the app's .razor pages")
    args = parser.parse_args(argv)

    names = routes(args.pages)
    if not names:
        print(f"no routes found under {args.pages}", file=sys.stderr)
        return 1
    try:
        written = write_route_pages(args.wwwroot, names)
    except (FileNotFoundError, FileExistsError) as error:
        print(error, file=sys.stderr)
        return 1
    print(f"route pages written: {', '.join(p.name for p in written)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
