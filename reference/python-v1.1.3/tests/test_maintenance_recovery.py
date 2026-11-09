"""Maintenance state regression tests at the real import + filesystem boundary."""
import importlib
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

class MaintenanceStateTests(unittest.TestCase):
    def setUp(self):
        self.mod = importlib.import_module("core.maintenance_state")
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.file = Path(self.temp.name) / "maintenance" / "state.json"
        self.router = {"host": "10.0.0.1", "port": 8728}
        self.rows = [{"id": "*1", "name": "hotspot-a", "interface": "ether3", "disabled": False}]

    def test_write_read_and_restore_roundtrip(self):
        row = self.mod.make_record(self.router, self.rows)
        self.mod.write(self.file, row)
        self.assertEqual(self.mod.read(self.file), row)
        self.mod.set_status(self.file, row, "restored")
        self.assertEqual(self.mod.read(self.file)["status"], "restored")

    def test_reject_duplicate_json_keys(self):
        self.file.parent.mkdir(parents=True, exist_ok=True)
        self.file.write_text('{"status":"pending","status":"restored"}', encoding="utf-8")
        with self.assertRaises(ValueError):
            self.mod.read(self.file)

    def test_failed_replace_preserves_old_journal(self):
        record = self.mod.make_record(self.router, self.rows)
        self.mod.write(self.file, record)
        with patch("core.maintenance_state.os.replace", side_effect=OSError("disk error")):
            with self.assertRaises(OSError):
                self.mod.set_status(self.file, record.copy(), "active")
        self.assertEqual(self.mod.read(self.file)["status"], "pending")
        self.assertEqual([p.name for p in self.file.parent.iterdir()], ["state.json"])

if __name__ == "__main__":
    unittest.main()
