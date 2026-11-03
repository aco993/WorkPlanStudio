#!/usr/bin/env python3
"""Tests for the asset-manifest re-seal.

The bug this guards against was invisible in CI and looked like a sandbox quirk
in the browser: an empty cache with the right name. So the cases here are the
three outcomes that matter - the deliberate edit gets a new hash, an
*undeclared* edit fails the build, and a declared edit that did not happen also
fails, because that means the step meant to make it has stopped working.

    python3 -m unittest discover -s .github/scripts -p 'test_*.py'
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import reseal_sw_assets  # noqa: E402


def sha(text: str) -> str:
    return "sha256-" + base64.b64encode(hashlib.sha256(text.encode()).digest()).decode()


class ResealTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

        self.write("index.html", "<base href=\"/\" />")
        self.write("app.css", "body{}")
        self.manifest({"index.html": sha("<base href=\"/\" />"), "app.css": sha("body{}")})

    def write(self, name: str, text: str) -> None:
        (self.root / name).write_text(text, encoding="utf-8")

    def manifest(self, hashes: dict[str, str]) -> None:
        payload = {"version": "testver",
                   "assets": [{"hash": h, "url": u} for u, h in hashes.items()]}
        (self.root / "service-worker-assets.js").write_text(
            "self.assetsManifest = " + json.dumps(payload, indent=2) + ";\n", encoding="utf-8")

    def manifest_hash(self, url: str) -> str:
        text = (self.root / "service-worker-assets.js").read_text(encoding="utf-8")
        body = text.split("=", 1)[1].rsplit(";", 1)[0]
        return next(a["hash"] for a in json.loads(body)["assets"] if a["url"] == url)

    def test_a_declared_edit_gets_the_new_hash(self):
        """What the deploy actually does: rewrite <base href> after publish."""
        self.write("index.html", "<base href=\"/WorkPlanStudio/\" />")
        self.assertEqual(reseal_sw_assets.main([str(self.root), "--resealed", "index.html"]), 0)
        self.assertEqual(self.manifest_hash("index.html"), sha("<base href=\"/WorkPlanStudio/\" />"))

    def test_untouched_files_keep_their_hash(self):
        self.write("index.html", "<base href=\"/WorkPlanStudio/\" />")
        before = self.manifest_hash("app.css")
        reseal_sw_assets.main([str(self.root), "--resealed", "index.html"])
        self.assertEqual(self.manifest_hash("app.css"), before)

    def test_an_undeclared_edit_fails_the_build(self):
        """An integrity manifest that cannot disagree with the disk is not one."""
        self.write("app.css", "body{color:red}")
        self.assertEqual(reseal_sw_assets.main([str(self.root), "--resealed", "index.html"]), 1)

    def test_a_declared_edit_that_never_happened_fails(self):
        """The rewrite step broke, or the flag is stale. Both are worth a red build."""
        self.assertEqual(reseal_sw_assets.main([str(self.root), "--resealed", "index.html"]), 1)

    def test_nothing_declared_and_nothing_changed_passes(self):
        self.assertEqual(reseal_sw_assets.main([str(self.root)]), 0)

    def test_an_asset_missing_from_disk_is_left_alone(self):
        """The upload will not carry it either; inventing a hash helps nobody."""
        self.manifest({"index.html": sha("<base href=\"/\" />"),
                       "app.css": sha("body{}"),
                       "gone.js": sha("whatever")})
        self.assertEqual(reseal_sw_assets.main([str(self.root)]), 0)

    def test_a_missing_manifest_is_reported_rather_than_raised(self):
        (self.root / "service-worker-assets.js").unlink()
        self.assertEqual(reseal_sw_assets.main([str(self.root)]), 2)

    def test_a_manifest_that_is_not_an_assignment_is_reported(self):
        (self.root / "service-worker-assets.js").write_text("{}", encoding="utf-8")
        self.assertEqual(reseal_sw_assets.main([str(self.root)]), 2)


class HashShapeTests(unittest.TestCase):
    def test_the_hash_matches_what_the_sdk_writes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "f.txt"
            path.write_bytes(b"hello")
            expected = "sha256-" + base64.b64encode(hashlib.sha256(b"hello").digest()).decode()
            self.assertEqual(reseal_sw_assets.hash_of(path), expected)
            self.assertTrue(reseal_sw_assets.hash_of(path).startswith("sha256-"))


if __name__ == "__main__":
    unittest.main()
