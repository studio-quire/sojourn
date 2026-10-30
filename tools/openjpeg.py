"""Build the J2C decoder the renderer uses, OpenJPEG 2.5.3 (BSD-2-Clause), into src/Sojourn.Render/native/openjp2.dll.

  python tools/openjpeg.py

Downloads the release source from GitHub (checked against its SHA-256), builds it as a DLL with the machine's MinGW
toolchain (WinLibs gcc, cmake and ninja on PATH), linked statically so it needs only KERNEL32 and msvcrt, and copies
it over the vendored one. Work happens in local/openjpeg/. Without the DLL the renderer decodes with CoreJ2K.
"""
import hashlib
import shutil
import subprocess
import sys
import tarfile
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORK = ROOT / "local" / "openjpeg"
VERSION = "2.5.3"
URL = f"https://github.com/uclouvain/openjpeg/archive/refs/tags/v{VERSION}.tar.gz"
SHA256 = "368fe0468228e767433c9ebdea82ad9d801a3ad1e4234421f352c8b06e7aa707"
DEST = ROOT / "src" / "Sojourn.Render" / "native" / "openjp2.dll"


def main():
    WORK.mkdir(parents=True, exist_ok=True)
    archive = WORK / f"openjpeg-{VERSION}.tar.gz"
    if not archive.exists():
        urllib.request.urlretrieve(URL, archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != SHA256:
        sys.exit(f"{archive.name}: checksum mismatch")
    src = WORK / f"openjpeg-{VERSION}"
    if not src.exists():
        with tarfile.open(archive) as t:
            t.extractall(WORK, filter="data")
    build = WORK / "build"
    subprocess.run(["cmake", "-S", str(src), "-B", str(build), "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DBUILD_SHARED_LIBS=ON",
                    "-DBUILD_STATIC_LIBS=OFF", "-DBUILD_CODEC=OFF", "-DCMAKE_C_FLAGS=-O2",
                    "-DCMAKE_SHARED_LINKER_FLAGS=-static-libgcc -static"], check=True)
    subprocess.run(["cmake", "--build", str(build), "-j", "3"], check=True)
    DEST.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(build / "bin" / "libopenjp2.dll", DEST)
    print(f"{DEST.relative_to(ROOT)}: {DEST.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
