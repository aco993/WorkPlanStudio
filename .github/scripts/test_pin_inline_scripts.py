import base64
import gzip
import hashlib
import tempfile
import unittest
from pathlib import Path

import pin_inline_scripts as pin


def sha(text: str) -> str:
    return "'sha256-" + base64.b64encode(hashlib.sha256(text.encode("utf-8")).digest()).decode() + "'"


PAGE = """<!DOCTYPE html><html><head>
<meta http-equiv="Content-Security-Policy" content="
      default-src 'none';
      script-src 'self' 'wasm-unsafe-eval' 'unsafe-inline';
      style-src 'self' 'unsafe-inline';
      base-uri 'self'" />
<script type="importmap">{"imports":{"a":"./a.js"}}</script>
</head><body>
<script src="js/app.js"></script>
<script>
    Blazor.start();
</script>
</body></html>"""


class PinTests(unittest.TestCase):
    def test_every_inline_script_is_hashed_and_external_ones_are_not(self):
        hashes = pin.inline_script_hashes(PAGE)
        self.assertEqual([sha('{"imports":{"a":"./a.js"}}'), sha("\n    Blazor.start();\n")], hashes)

    def test_script_src_loses_unsafe_inline_and_style_src_keeps_it(self):
        pinned, hashes = pin.pin(PAGE)
        script_src = next(line for line in pinned.splitlines() if "script-src" in line)
        style_src = next(line for line in pinned.splitlines() if "style-src" in line)
        self.assertNotIn("'unsafe-inline'", script_src)
        for h in hashes:
            self.assertIn(h, script_src)
        self.assertIn("'wasm-unsafe-eval'", script_src)
        self.assertIn("'unsafe-inline'", style_src)

    def test_nothing_outside_the_policy_changes(self):
        pinned, _ = pin.pin(PAGE)
        self.assertEqual(PAGE.split("<script type")[1], pinned.split("<script type")[1])

    def test_a_page_already_pinned_is_refused(self):
        pinned, _ = pin.pin(PAGE)
        with self.assertRaisesRegex(ValueError, "already pinned"):
            pin.pin(pinned)

    def test_a_page_without_a_policy_is_refused(self):
        with self.assertRaisesRegex(ValueError, "Content-Security-Policy"):
            pin.pin("<html><script>x()</script></html>")

    def test_a_page_without_inline_scripts_is_refused(self):
        with self.assertRaisesRegex(ValueError, "no inline"):
            pin.pin(PAGE.replace('<script type="importmap">{"imports":{"a":"./a.js"}}</script>', "")
                        .replace("<script>\n    Blazor.start();\n</script>", ""))

    def test_a_script_tag_quoted_inside_a_comment_is_not_a_script(self):
        comment = '<!-- Blazor writes a <script type="importmap"> here. -->\n'
        commented = PAGE.replace("<meta http-equiv", comment + "<meta http-equiv")
        self.assertEqual(pin.inline_script_hashes(PAGE), pin.inline_script_hashes(commented))

    def test_identical_scripts_are_listed_once(self):
        doubled = PAGE.replace("</body>", "<script>\n    Blazor.start();\n</script></body>")
        self.assertEqual(2, len(pin.inline_script_hashes(doubled)))

    def test_the_command_rewrites_the_file_and_reports_failure_by_exit_code(self):
        with tempfile.TemporaryDirectory() as directory:
            page = Path(directory) / "index.html"
            page.write_text(PAGE, encoding="utf-8")
            self.assertEqual(0, pin.main([str(page)]))
            self.assertNotIn("'wasm-unsafe-eval' 'unsafe-inline'", page.read_text(encoding="utf-8"))
            self.assertEqual(1, pin.main([str(page)]))

    def test_stale_compressed_copies_are_brought_in_step(self):
        with tempfile.TemporaryDirectory() as directory:
            page = Path(directory) / "index.html"
            page.write_text(PAGE, encoding="utf-8")
            (Path(directory) / "index.html.gz").write_bytes(gzip.compress(b"old"))
            (Path(directory) / "index.html.br").write_bytes(b"old")
            self.assertEqual(0, pin.main([str(page)]))
            self.assertEqual(page.read_bytes(), gzip.decompress((Path(directory) / "index.html.gz").read_bytes()))
            self.assertFalse((Path(directory) / "index.html.br").exists())


if __name__ == "__main__":
    unittest.main()
