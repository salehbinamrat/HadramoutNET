"""Validate package contents and executable architecture before Windows CI build."""
import importlib
import struct
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile

class PackagingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder = importlib.import_module("scripts.build_windows")

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def fake_exe(self, machine):
        file = self.root / "Hadramout.exe"
        raw = bytearray(512)
        raw[:2] = b"MZ"
        struct.pack_into("<I", raw, 0x3C, 0x80)
        raw[0x80:0x84] = b"PE\x00\x00"
        struct.pack_into("<H", raw, 0x84, machine)
        file.write_bytes(raw)
        return file

    def test_architectures_are_not_interchangeable(self):
        file = self.fake_exe(0x14C)
        self.builder.require_pe_arch(file, "x86")
        with self.assertRaises(ValueError):
            self.builder.require_pe_arch(file, "x64")
        file = self.fake_exe(0x8664)
        self.builder.require_pe_arch(file, "x64")
        with self.assertRaises(ValueError):
            self.builder.require_pe_arch(file, "x86")

    def test_portable_archive_has_marker(self):
        file = self.fake_exe(0x8664)
        (self.root / "assets").mkdir()
        (self.root / "assets" / "default_card.png").write_bytes(b"test")
        dest = self.root / "portable.zip"
        self.builder.make_portable_zip(self.root, dest)
        with ZipFile(dest) as archive:
            self.assertIsNone(archive.testzip())
            self.assertEqual(archive.read("Hadramout/portable.mode"), b"portable\n")
            self.assertIn("Hadramout/Hadramout.exe", archive.namelist())
        self.assertFalse((self.root / "portable.mode").exists())

    def test_installer_never_contains_portable_marker(self):
        self.fake_exe(0x14C)
        self.builder.require_clean_installer_tree(self.root)
        (self.root / "portable.mode").write_text("portable")
        with self.assertRaises(ValueError):
            self.builder.require_clean_installer_tree(self.root)

    def test_wrong_release_version_is_rejected(self):
        self.builder.require_source_version("1.1.3")
        with self.assertRaises(ValueError):
            self.builder.require_source_version("1.1.4")

if __name__ == "__main__":
    unittest.main()
