#!/usr/bin/env python3
"""Fast pre-check that the two resource files are well-formed and in step.

A missing key does not throw at runtime, it renders the key name — so drift is
invisible until someone reads a German page. `LocalizationTests` asserts parity
properly; this is the cheap version that runs before the slower suites start, so
a malformed or half-translated .resx fails in seconds rather than minutes.

    python3 .github/scripts/check_resources.py
"""

from __future__ import annotations

import sys
import xml.etree.ElementTree as ElementTree

FILES = (
    "src/WorkPlanStudio/Resources/SharedResource.resx",
    "src/WorkPlanStudio/Resources/SharedResource.de.resx",
)


def keys_of(path: str) -> list[str]:
    """Every <data name="..."> in the file, in document order."""
    root = ElementTree.parse(path).getroot()
    return [element.get("name") or "" for element in root.findall("data")]


def check(files: tuple[str, ...] = FILES) -> list[str]:
    """Returns one message per problem; empty means both files are in step."""
    problems: list[str] = []
    parsed: dict[str, list[str]] = {}

    for path in files:
        try:
            parsed[path] = keys_of(path)
        except ElementTree.ParseError as error:
            problems.append(f"{path}: {error}")
        except OSError as error:
            problems.append(f"{path}: {error}")

    if len(parsed) < 2:
        return problems

    reference, translation = (parsed[path] for path in files)
    for key in sorted(set(reference) - set(translation)):
        problems.append(f"{files[1]}: missing key '{key}'")
    for key in sorted(set(translation) - set(reference)):
        problems.append(f"{files[0]}: missing key '{key}'")

    for path, names in parsed.items():
        duplicates = sorted({name for name in names if names.count(name) > 1})
        for name in duplicates:
            problems.append(f"{path}: duplicate key '{name}'")

    return problems


def main(argv: list[str]) -> int:
    files = tuple(argv) if argv else FILES
    problems = check(files)
    if problems:
        for entry in problems:
            print(entry)
        return 1
    for path in files:
        print(f"{path}: {len(keys_of(path))} entries")
    print("Both resource files parse and carry the same keys.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
