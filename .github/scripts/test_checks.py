#!/usr/bin/env python3
"""Tests for the two repository checks, so they can go wrong loudly.

These scripts used to be heredocs inside `quality.yml`: unrunnable locally,
untestable, and — in the link checker's case — quietly weaker than the workflow
step's name claimed, because it stripped the `#anchor` before checking. A check
nobody can test is a check nobody can trust, so each behaviour that matters has
a case here, and each case fails if the check stops doing its job.

    python3 -m unittest discover -s .github/scripts -p 'test_*.py'
"""

from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import check_doc_links  # noqa: E402
import check_resources  # noqa: E402
import coverage_gate  # noqa: E402

RESX = """<?xml version="1.0" encoding="utf-8"?>
<root>
{items}
</root>
"""


def write(directory: str, name: str, text: str) -> str:
    path = os.path.join(directory, name)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)
    return path


def resx(*keys: str) -> str:
    return RESX.format(items="\n".join(f'  <data name="{k}"><value>{k}</value></data>' for k in keys))


class SlugTests(unittest.TestCase):
    def test_a_heading_becomes_its_github_anchor(self):
        self.assertEqual("coverage-and-gates", check_doc_links.slugify("Coverage and gates"))

    def test_punctuation_and_code_spans_are_dropped(self):
        self.assertEqual("run-dotnet-format-first", check_doc_links.slugify("Run `dotnet format` first!"))

    def test_repeated_headings_get_numbered_anchors(self):
        anchors = check_doc_links.anchors_of("## Context\n\n## Context\n")
        self.assertEqual({"context", "context-1"}, anchors)


class DocLinkTests(unittest.TestCase):
    def test_a_resolving_link_is_accepted(self):
        with tempfile.TemporaryDirectory() as root:
            write(root, "docs/testing.md", "# Testing\n\n## Coverage\n")
            write(root, "README.md", "See [coverage](docs/testing.md#coverage).\n")
            self.assertEqual([], check_doc_links.check(root))

    def test_a_missing_file_is_reported(self):
        with tempfile.TemporaryDirectory() as root:
            write(root, "README.md", "See [gone](docs/missing.md).\n")
            problems = check_doc_links.check(root)
            self.assertEqual(1, len(problems))
            self.assertIn("no such file", problems[0])

    def test_a_missing_anchor_is_reported(self):
        """The gap the inline version had: the file existed, the heading did not."""
        with tempfile.TemporaryDirectory() as root:
            write(root, "docs/testing.md", "# Testing\n\n## Gates\n")
            write(root, "README.md", "See [coverage](docs/testing.md#coverage).\n")
            problems = check_doc_links.check(root)
            self.assertEqual(1, len(problems))
            self.assertIn("no heading '#coverage'", problems[0])

    def test_an_explicit_html_anchor_counts(self):
        with tempfile.TemporaryDirectory() as root:
            write(root, "docs/testing.md", '<a id="sec-1"></a>\n\n# Testing\n')
            write(root, "README.md", "See [one](docs/testing.md#sec-1).\n")
            self.assertEqual([], check_doc_links.check(root))

    def test_external_links_are_not_followed(self):
        with tempfile.TemporaryDirectory() as root:
            write(root, "README.md", "[home](https://example.com/nope#nowhere)\n")
            self.assertEqual([], check_doc_links.check(root))

    def test_a_same_file_fragment_is_checked_against_that_file(self):
        with tempfile.TemporaryDirectory() as root:
            write(root, "README.md", "# Title\n\n[up](#title) and [away](#elsewhere)\n")
            problems = check_doc_links.check(root)
            self.assertEqual(1, len(problems))
            self.assertIn("#elsewhere", problems[0])


class ResourceTests(unittest.TestCase):
    def test_matching_files_pass(self):
        with tempfile.TemporaryDirectory() as root:
            english = write(root, "a.resx", resx("One", "Two"))
            german = write(root, "a.de.resx", resx("One", "Two"))
            self.assertEqual([], check_resources.check((english, german)))

    def test_a_missing_translation_is_reported(self):
        with tempfile.TemporaryDirectory() as root:
            english = write(root, "a.resx", resx("One", "Two"))
            german = write(root, "a.de.resx", resx("One"))
            problems = check_resources.check((english, german))
            self.assertEqual(1, len(problems))
            self.assertIn("missing key 'Two'", problems[0])

    def test_a_duplicate_key_is_reported(self):
        with tempfile.TemporaryDirectory() as root:
            english = write(root, "a.resx", resx("One", "One"))
            german = write(root, "a.de.resx", resx("One"))
            problems = check_resources.check((english, german))
            self.assertTrue(any("duplicate key 'One'" in p for p in problems), problems)

    def test_malformed_xml_is_reported_not_raised(self):
        with tempfile.TemporaryDirectory() as root:
            english = write(root, "a.resx", "<root><data name='One'></root>")
            german = write(root, "a.de.resx", resx("One"))
            problems = check_resources.check((english, german))
            self.assertEqual(1, len(problems))
            self.assertIn("a.resx", problems[0])


COBERTURA = """<?xml version="1.0"?>
<coverage>
  <packages>
    <package name="WorkPlanStudio.Scheduling" line-rate="{rate}" branch-rate="0.8" />
  </packages>
</coverage>
"""


class CoverageGateTests(unittest.TestCase):
    """The project's advertised quality gate. It is only a gate if it can say no."""

    def run_gate(self, rate: str, *thresholds: str) -> int:
        with tempfile.TemporaryDirectory() as root:
            report = write(root, "cov.xml", COBERTURA.format(rate=rate))
            return coverage_gate.main(["coverage_gate.py", report, *thresholds])

    def test_coverage_above_the_threshold_passes(self):
        self.assertEqual(0, self.run_gate("0.955", "WorkPlanStudio.Scheduling=90"))

    def test_coverage_below_the_threshold_fails(self):
        self.assertEqual(1, self.run_gate("0.884", "WorkPlanStudio.Scheduling=90"))

    def test_an_assembly_missing_from_the_report_fails_closed(self):
        """A run that produced no data must not be indistinguishable from a good one."""
        self.assertEqual(1, self.run_gate("0.99", "WorkPlanStudio.WorkingTime=90"))

    def test_a_badge_is_written_with_the_measured_number(self):
        with tempfile.TemporaryDirectory() as root:
            report = write(root, "cov.xml", COBERTURA.format(rate="0.912"))
            badges = os.path.join(root, "badges")
            code = coverage_gate.main(
                ["coverage_gate.py", report, "WorkPlanStudio.Scheduling=90", "--badge", badges])
            self.assertEqual(0, code)
            with open(os.path.join(badges, "WorkPlanStudio.Scheduling.json"), encoding="utf-8") as handle:
                badge = json.load(handle)
            self.assertEqual("91.2%", badge["message"])
            self.assertEqual("brightgreen", badge["color"])


if __name__ == "__main__":
    unittest.main()
