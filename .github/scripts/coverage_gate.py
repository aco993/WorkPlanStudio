#!/usr/bin/env python3
"""Fail the build when an assembly's line coverage drops below its threshold.

Usage: coverage_gate.py <cobertura.xml> <Assembly>=<minLinePercent> [...]
                        [--badge <dir>] [--slack <points>]

Reads the Cobertura report the Microsoft Testing Platform writes, prints a
per-assembly table into the job summary, enforces the thresholds given on the
command line, and optionally writes a shields.io endpoint JSON per assembly
(deployed with the site, so the README badges show measured numbers).

--slack enforces the other direction. A threshold is meant to sit just under
what is measured, so that it defends what has been reached; a threshold that
coverage has climbed far above defends nothing and quietly stops being a gate.
With --slack N, a threshold more than N points below the measured value fails
the build the same way a drop does. The README says these gates sit about two
points under what is measured, and this is what makes that sentence a run
rather than a claim.
"""
import json
import os
import sys
import xml.etree.ElementTree as ET


def colour(percent: float) -> str:
    if percent >= 90:
        return "brightgreen"
    if percent >= 80:
        return "green"
    if percent >= 70:
        return "yellowgreen"
    if percent >= 60:
        return "yellow"
    return "orange"


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")   # the table carries check marks; a cp1252 console must not crash it
    if len(argv) < 2:
        print(__doc__)
        return 2

    report = argv[1]
    badge_dir = None
    slack: float | None = None
    thresholds: dict[str, float] = {}
    args = argv[2:]
    while args:
        arg = args.pop(0)
        if arg == "--badge":
            badge_dir = args.pop(0)
        elif arg == "--slack":
            slack = float(args.pop(0))
        else:
            name, minimum = arg.split("=", 1)
            thresholds[name] = float(minimum)

    root = ET.parse(report).getroot()
    measured: dict[str, tuple[float, float]] = {}
    for package in root.iter("package"):
        measured[package.get("name")] = (
            float(package.get("line-rate", 0)) * 100,
            float(package.get("branch-rate", 0)) * 100,
        )

    rows = ["| Assembly | Line | Branch | Required | Result |", "| --- | ---: | ---: | ---: | --- |"]
    failed = False
    for name, minimum in thresholds.items():
        line, branch = measured.get(name, (0.0, 0.0))
        ok = line >= minimum
        slack_ok = slack is None or line - minimum <= slack
        failed |= not ok or not slack_ok
        if not ok:
            verdict = "❌ below threshold"
        elif not slack_ok:
            verdict = f"❌ gate is {line - minimum:.2f} points adrift — raise it"
        else:
            verdict = "✅"
        rows.append(f"| {name} | {line:.2f} % | {branch:.2f} % | {minimum:.0f} % | {verdict} |")
        if badge_dir:
            os.makedirs(badge_dir, exist_ok=True)
            label = name.replace("WorkPlanStudio.", "").replace("WorkPlanStudio", "app").lower() + " coverage"
            with open(os.path.join(badge_dir, f"{name}.json"), "w", encoding="utf-8") as handle:
                json.dump({"schemaVersion": 1, "label": label, "message": f"{line:.1f}%", "color": colour(line)}, handle)

    table = "\n".join(rows)
    print(table)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(f"### Coverage ({os.path.basename(report)})\n\n{table}\n\n")

    if failed:
        print("coverage gate failed", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
