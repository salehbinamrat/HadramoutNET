"""Structural and syntax tests only; not network, license or runtime integration."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

class SourceRecoverySmokeTests(unittest.TestCase):
    def test_essential_sources(self):
        for name in ("app.py", "core/github_license.py", "core/trial_runtime.py",
                     "ui/login.py", "assets/hadramout_icon.ico"):
            with self.subTest(file=name):
                self.assertTrue((ROOT / name).is_file(), "Missing: " + name)

    def test_python_syntax(self):
        paths = [ROOT / "app.py", *sorted((ROOT / "core").glob("*.py")),
                 *sorted((ROOT / "ui").glob("*.py"))]
        self.assertEqual(len(paths), 31, "Incomplete recovery")
        for path in paths:
            with self.subTest(file=str(path)):
                compile(path.read_bytes(), str(path), "exec")

    def test_image_assets(self):
        assets = [p for p in (ROOT / "assets").rglob("*") if p.is_file()]
        self.assertEqual(len(assets), 17, "Incomplete assets")

if __name__ == "__main__":
    unittest.main()
