"""Build two verified Windows package types per interpreter architecture.

Use a matching Windows x86/x64 Python interpreter; this is not a cross-compiler.
"""
from __future__ import annotations
import argparse
import hashlib
import os
import platform
import re
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

ROOT = Path(__file__).resolve().parents[1]
MACHINES = {"x86": 0x14C, "x64": 0x8664}
VERSION = "1.1.3"

def require_source_version(version):
    source = (ROOT / "core" / "github_license.py").read_text(encoding="utf-8")
    match = re.search(r'^APP_VERSION\s*=\s*["\x27]([^"\x27]+)["\x27]', source, re.MULTILINE)
    if version != VERSION or not match or match.group(1) != version:
        raise ValueError("Source version and requested package version differ")

def require_pe_arch(file, arch):
    if arch not in MACHINES:
        raise ValueError("Unknown architecture")
    with Path(file).open("rb") as stream:
        if stream.read(2) != b"MZ":
            raise ValueError("Not an EXE")
        stream.seek(0x3C)
        raw = stream.read(4)
        if len(raw) != 4:
            raise ValueError("Truncated PE")
        offset = struct.unpack("<I", raw)[0]
        if not 0x40 <= offset <= 8_000_000:
            raise ValueError("Invalid PE pointer")
        stream.seek(offset)
        header = stream.read(6)
        if len(header) != 6 or header[:4] != b"PE\x00\x00":
            raise ValueError("Invalid PE signature")
        machine = struct.unpack("<H", header[4:])[0]
        if machine != MACHINES[arch]:
            raise ValueError(f"Expected {arch} executable, got PE machine {machine:04x}")

def require_clean_installer_tree(bundle):
    path = Path(bundle)
    if not (path / "Hadramout.exe").is_file():
        raise ValueError("Missing installed Hadramout.exe")
    if (path / "portable.mode").exists():
        raise ValueError("Portable mode marker cannot be included in installer")

def make_portable_zip(bundle, output):
    bundle, output = Path(bundle), Path(output)
    if not (bundle / "Hadramout.exe").is_file() or (bundle / "portable.mode").exists():
        raise ValueError("Invalid portable app tree")
    output.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(output, "w", compression=ZIP_DEFLATED, compresslevel=6) as z:
        for file in sorted(bundle.rglob("*")):
            if file.is_symlink():
                raise ValueError("Refusing symlink in app bundle")
            if file.is_file():
                z.write(file, "Hadramout/" + file.relative_to(bundle).as_posix())
        z.writestr("Hadramout/portable.mode", b"portable\n")
    with ZipFile(output) as z:
        if z.testzip() is not None or z.read("Hadramout/portable.mode") != b"portable\n":
            raise ValueError("Portable ZIP integrity failed")
        if "Hadramout/Hadramout.exe" not in z.namelist():
            raise ValueError("Portable program is missing")

def find_iscc():
    located = shutil.which("ISCC.exe") or shutil.which("iscc")
    candidates = [
        Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) / "Inno Setup 6" / "ISCC.exe",
        Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Inno Setup 6" / "ISCC.exe",
    ]
    return located or next((str(p) for p in candidates if p.is_file()), None)

def run_command(cmd):
    subprocess.run(cmd, cwd=ROOT, check=True)

def build(arch, version, output):
    if os.name != "nt" or platform.system() != "Windows":
        raise OSError("Windows runner is required")
    if arch not in MACHINES or struct.calcsize("P") * 8 != {"x86":32, "x64":64}[arch]:
        raise ValueError("Python architecture mismatch")
    require_source_version(version)
    for path in ["app.py", "assets/default_card.png", "assets/hadramout_icon.ico",
                 "core/maintenance_state.py", "ui/login.py"]:
        if not (ROOT / path).is_file():
            raise FileNotFoundError(f"Incomplete source: {path}")
    output = Path(output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    portable = output / f"Hadramout-v{version}-{arch}-Portable.zip"
    installer = output / f"Hadramout-v{version}-{arch}-Installer.exe"
    with tempfile.TemporaryDirectory(prefix="hadramout-build-") as directory:
        tmp = Path(directory)
        run_command([
            sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean",
            "--onedir", "--windowed", "--contents-directory", ".",
            "--name", "Hadramout", "--distpath", str(tmp / "dist"),
            "--workpath", str(tmp / "work"), "--specpath", str(tmp / "spec"),
            "--icon", str(ROOT / "assets/hadramout_icon.ico"),
            "--add-data", f'{ROOT / "assets"}{os.pathsep}assets',
            str(ROOT / "app.py"),
        ])
        frozen = tmp / "dist" / "Hadramout"
        require_pe_arch(frozen / "Hadramout.exe", arch)
        if not (frozen / "assets" / "default_card.png").is_file():
            raise ValueError("Artwork missing from frozen bundle")
        require_clean_installer_tree(frozen)
        make_portable_zip(frozen, portable)
        compiler = find_iscc()
        if not compiler:
            raise FileNotFoundError("Inno Setup 6 (ISCC.exe) not installed")
        run_command([
            compiler, f"/DSourceDir={frozen}", f"/DArch={arch}",
            f"/DAppVersion={version}", f"/O{output}",
            f"/F{installer.stem}", "/Q", str(ROOT / "installer" / "Hadramout.iss"),
        ])
        if not installer.is_file() or installer.stat().st_size < 100_000:
            raise ValueError("Installer missing or abnormally small")
        with installer.open("rb") as stream:
            if stream.read(2) != b"MZ":
                raise ValueError("Installer is not a Windows executable")
    for path in (portable, installer):
        if not path.is_file() or path.stat().st_size == 0:
            raise ValueError(f"Missing output: {path}")
    (output / f"SHA256SUMS-{arch}.txt").write_text(
        "".join(hashlib.sha256(p.read_bytes()).hexdigest() + "  " + p.name + "\n"
                for p in (portable, installer)), encoding="utf-8")
    return portable, installer

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--arch", choices=["x86","x64"], required=True)
    parser.add_argument("--version", default=VERSION)
    parser.add_argument("--output", type=Path, default=Path("dist"))
    args = parser.parse_args()
    print(build(args.arch, args.version, args.output))
