import tempfile
import unittest
from pathlib import Path

import route_pages


class RoutePagesTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.pages = root / "Pages"
        (self.pages / "Nested").mkdir(parents=True)
        self.wwwroot = root / "wwwroot"
        self.wwwroot.mkdir()
        (self.wwwroot / "index.html").write_text("<html>app</html>", encoding="utf-8")
        files = {
            "Home.razor": '@page "/"\n<h1>Home</h1>',
            "Schedule.razor": '@page "/schedule"\n',
            "Plans.razor": '@page "/work-plans"\n',
            "PlanNew.razor": '@page "/work-plans/new"\n',
            "Nested/Plan.razor": '@page "/work-plans/{Id:int}"\n',
            "NotFound.razor": '@page "/not-found"\n',
            "Two.razor": '@page "/about"\n@page "/about"\n',
            "Partial.razor": "<p>no route</p>",
        }
        for name, body in files.items():
            (self.pages / name).write_text(body, encoding="utf-8")

    def tearDown(self):
        self.tmp.cleanup()

    def test_only_top_level_parameter_free_routes_are_taken(self):
        self.assertEqual(["about", "schedule", "work-plans"], route_pages.routes(self.pages))

    def test_each_route_gets_a_copy_of_the_finished_index(self):
        self.assertEqual(0, route_pages.main([str(self.wwwroot), str(self.pages)]))
        for name in ["about", "schedule", "work-plans"]:
            self.assertEqual("<html>app</html>", (self.wwwroot / f"{name}.html").read_text(encoding="utf-8"))
        self.assertFalse((self.wwwroot / "not-found.html").exists())
        self.assertFalse((self.wwwroot / "work-plans").exists())

    def test_a_published_file_is_never_overwritten(self):
        (self.wwwroot / "about.html").write_text("something else", encoding="utf-8")
        self.assertEqual(1, route_pages.main([str(self.wwwroot), str(self.pages)]))
        self.assertEqual("something else", (self.wwwroot / "about.html").read_text(encoding="utf-8"))

    def test_no_routes_is_an_error_not_a_quiet_success(self):
        empty = Path(self.tmp.name) / "Empty"
        empty.mkdir()
        self.assertEqual(1, route_pages.main([str(self.wwwroot), str(empty)]))


if __name__ == "__main__":
    unittest.main()
