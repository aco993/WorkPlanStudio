#!/usr/bin/env python3
"""Tests for the cold-start transfer budget.

A size gate that silently stops measuring is worse than no gate: the number it
prints still looks like a measurement. So each thing this check has to get right
— preferring the compressed sibling, never billing the siblings themselves,
billing one ICU shard rather than three, and failing in both directions — has a
case here.

    python3 -m unittest discover -s .github/scripts -p 'test_*.py'
"""

from __future__ import annotations

import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import transfer_budget  # noqa: E402


def write(directory: str, name: str, size: int) -> None:
    path = os.path.join(directory, name)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(b"\0" * size)


KB = 1024


class TransferredSizeTests(unittest.TestCase):
    def test_a_file_with_no_sibling_is_billed_raw(self):
        with tempfile.TemporaryDirectory() as directory:
            write(directory, "app.css", 40 * KB)
            total, _, _ = transfer_budget.measure(transfer_budget.Path(directory))
            self.assertEqual(total, 40 * KB)

    def test_the_gzip_sibling_is_what_a_browser_receives(self):
        with tempfile.TemporaryDirectory() as directory:
            write(directory, "app.css", 40 * KB)
            write(directory, "app.css.gz", 9 * KB)
            total, _, _ = transfer_budget.measure(transfer_budget.Path(directory))
            self.assertEqual(total, 9 * KB, "the compressed size, not the raw one")

    def test_siblings_are_never_billed_on_their_own(self):
        with tempfile.TemporaryDirectory() as directory:
            write(directory, "app.css", 40 * KB)
            write(directory, "app.css.gz", 9 * KB)
            write(directory, "app.css.br", 7 * KB)
            total, files, _ = transfer_budget.measure(transfer_budget.Path(directory))
            self.assertEqual(total, 9 * KB)
            self.assertEqual(len(files), 1, "one request, not three")


class IcuShardTests(unittest.TestCase):
    """All three shards are published; a visitor fetches exactly one."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        write(self.directory.name, "_framework/icudt_EFIGS.aaa.dat", 215 * KB)
        write(self.directory.name, "_framework/icudt_CJK.bbb.dat", 351 * KB)
        write(self.directory.name, "_framework/icudt_no_CJK.ccc.dat", 340 * KB)

    def test_only_one_shard_is_billed(self):
        total, _, excluded = transfer_budget.measure(transfer_budget.Path(self.directory.name))
        self.assertEqual(total + excluded, (215 + 351 + 340) * KB, "every shard accounted for")
        self.assertEqual(total, 351 * KB, "the largest, so the budget holds for every visitor")
        self.assertEqual(excluded, (215 + 340) * KB)

    def test_a_shard_growing_still_moves_the_number(self):
        """The exclusion must not become a blind spot for the shards themselves."""
        before, _, _ = transfer_budget.measure(transfer_budget.Path(self.directory.name))
        write(self.directory.name, "_framework/icudt_CJK.bbb.dat", 500 * KB)
        after, _, _ = transfer_budget.measure(transfer_budget.Path(self.directory.name))
        self.assertGreater(after, before)


class BudgetTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        write(self.directory.name, "_framework/app.wasm", 1000 * KB)

    def run_check(self, *args: str) -> int:
        return transfer_budget.main([self.directory.name, *args])

    def test_reporting_without_a_budget_never_fails(self):
        self.assertEqual(self.run_check(), 0)

    def test_under_the_budget_passes(self):
        self.assertEqual(self.run_check("--budget-kb", "1100"), 0)

    def test_over_the_budget_fails(self):
        self.assertEqual(self.run_check("--budget-kb", "900"), 1)

    def test_a_budget_that_has_drifted_far_above_the_truth_fails(self):
        """Same idea as GATE_SLACK: a gate with 900 KB of room defends nothing."""
        self.assertEqual(self.run_check("--budget-kb", "1900", "--slack-kb", "100"), 1)

    def test_slack_is_not_checked_when_it_is_not_asked_for(self):
        self.assertEqual(self.run_check("--budget-kb", "1900"), 0)

    def test_a_missing_directory_is_reported_rather_than_raised(self):
        self.assertEqual(transfer_budget.main([os.path.join(self.directory.name, "nope")]), 2)


if __name__ == "__main__":
    unittest.main()
