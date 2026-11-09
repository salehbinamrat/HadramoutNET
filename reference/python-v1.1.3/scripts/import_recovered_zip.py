"""Import verified v1.1.3 Python sources and images; never execute source code."""
from __future__ import annotations
import argparse
import hashlib
from pathlib import Path, PurePosixPath
from zipfile import ZipFile

EXPECTED_SHA256 = "c3d6bb50943882aacd06c544b0ffa9229741f6648f4057c11a3a40fa978003f8"
ROOTS = {"core", "ui", "assets"}
EXTENSIONS = {".py", ".png", ".ico", ".jpg"}

def import_source(archive: Path, destination: Path) -> None:
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    if digest != EXPECTED_SHA256:
        raise ValueError(f"Archive SHA-256 mismatch: {digest}")
    entries = []
    with ZipFile(archive) as zf:
        if zf.testzip() is not None:
            raise ValueError("Archive CRC verification failed")
        for item in zf.infolist():
            if item.is_dir():
                continue
            path = PurePosixPath(item.filename)
            if path.is_absolute() or ".." in path.parts or "\\" in item.filename:
                raise ValueError(f"Unsafe archive entry: {item.filename}")
            allowed = (str(path) == "app.py" or path.parts[0] in ROOTS) and path.suffix.lower() in EXTENSIONS
            if not allowed or item.file_size > 2_000_000:
                continue
            if item.external_attr >> 16 & 0o170000 == 0o120000:
                raise ValueError(f"Symbolic link not allowed: {item.filename}")
            entries.append(item)
        py_count = sum(x.filename.endswith(".py") for x in entries)
        asset_count = len(entries) - py_count
        if (py_count, asset_count) != (31, 17):
            raise ValueError(f"Unexpected file count: {py_count} sources, {asset_count} assets")
        for item in entries:
            out = destination.joinpath(*PurePosixPath(item.filename).parts)
            out.parent.mkdir(parents=True, exist_ok=True)
            data = zf.read(item)
            if out.exists() and out.read_bytes() != data:
                raise ValueError(f"Conflicting existing file: {out}")
            out.write_bytes(data)
    print(f"Imported {py_count} Python sources and {asset_count} image assets")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path)
    parser.add_argument("--destination", type=Path, default=Path("."))
    args = parser.parse_args()
    import_source(args.archive, args.destination)
