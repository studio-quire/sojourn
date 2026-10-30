"""What holds the running client's managed heap: a gcdump, read by tools/heapwho (retained bytes by type over a
spanning tree from the roots, and arrays of 80 KB or more by the chain of types holding them).

  python tools/heapwho.py [--pid PID] [--label LABEL] [--top N]
  python tools/heapwho.py summary FILE.gcdump [--top N]

Needs dotnet-gcdump (`dotnet tool install --global dotnet-gcdump`; it runs on the .NET 8 runtime OpenSim uses, so
DOTNET_ROOT is set to that here). Dumps go to local/heapdumps/. Two or three dumps minutes apart say what grows; the
console's memory part (heap, fragmented, committed, what the last full collection left) says whether it's live at all.

Written by Claude Opus 5.5, 2026-10-28.
"""
import os
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cputrace  # noqa: E402  (the SDK, finding the process)

ROOT = Path(__file__).resolve().parents[1]
ANALYZER = ROOT / "local" / "tools" / "heapwho" / "heapwho.dll"
GCDUMP = Path.home() / ".dotnet" / "tools" / "dotnet-gcdump.exe"
ENV = dict(os.environ, DOTNET_ROOT=str(Path(os.environ["LOCALAPPDATA"]) / "Microsoft" / "dotnet"))


def summary(dump, top):
    if not ANALYZER.exists():
        subprocess.run([str(cputrace.SDK / "dotnet.exe"), "build", str(ROOT / "tools" / "heapwho"), "-c", "Release", "-v:q", "-nologo",
                        "-o", str(ANALYZER.parent)], check=True, capture_output=True)
    return subprocess.run([str(cputrace.SDK / "dotnet.exe"), str(ANALYZER), str(dump), str(top)], capture_output=True, text=True).stdout


def main():
    args = sys.argv[1:]

    def opt(name, default=None):
        return args[args.index(name) + 1] if name in args else default

    top = opt("--top", "30")
    if args[:1] == ["summary"]:
        print(summary(args[1], top))
        return
    pid = int(opt("--pid") or cputrace.sojourn_pid())
    out = ROOT / "local" / "heapdumps"
    out.mkdir(parents=True, exist_ok=True)
    dump = out / f"{opt('--label', 'heap')}-{time.strftime('%H%M%S')}.gcdump"
    subprocess.run([str(GCDUMP), "collect", "-p", str(pid), "-o", str(dump)], env=ENV, check=True)
    print(summary(dump, top))


if __name__ == "__main__":
    main()
