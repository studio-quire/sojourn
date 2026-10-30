"""Where the running client's allocations come from: the runtime's allocation ticks (one per ~100 KB, with the
allocating stack) over a few seconds, summarised by type, by the first frame in our code, and by short stacks.

  python tools/alloctrace.py [SECONDS=15] [--pid PID] [--label LABEL] [--top N] [--depth D]
  python tools/alloctrace.py summary FILE.nettrace [--top N] [--depth D]

Attaches dotnet-trace (as tools/cputrace.py installs it) to the newest Sojourn process (or --pid) and reads the trace
with tools/alloctrace (a small TraceEvent program, built into local/tools/alloctrace on first use). Files go to
local/cputrace/. SOJOURN_ALLOC_PROFILE=1 is the cheap always-on view (types only); this one says where from.

Written by Claude Opus 5.5, 2026-10-26.
"""
import os
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cputrace  # noqa: E402  (dotnet-trace, the SDK, finding the process)

ROOT = Path(__file__).resolve().parents[1]
ANALYZER = ROOT / "local" / "tools" / "alloctrace" / "alloctrace.dll"


def ensure_analyzer():
    if not ANALYZER.exists():
        subprocess.run([str(cputrace.SDK / "dotnet.exe"), "build", str(ROOT / "tools" / "alloctrace"), "-c", "Release", "-v:q", "-nologo",
                        "-o", str(ANALYZER.parent)], check=True)


def summary(trace, rest):
    ensure_analyzer()
    return subprocess.run([str(cputrace.SDK / "dotnet.exe"), str(ANALYZER), str(trace), *rest], capture_output=True, text=True).stdout


def main():
    args = sys.argv[1:]

    def opt(name, default=None):
        if name in args:
            i = args.index(name)
            v = args[i + 1]
            del args[i:i + 2]
            return v
        return default

    rest = []
    for name in ("--top", "--depth"):
        if (v := opt(name)) is not None:
            rest += [name, v]
    if args[:1] == ["summary"]:
        print(summary(args[1], rest))
        return
    pid = int(opt("--pid") or 0) or cputrace.sojourn_pid()
    label = opt("--label", time.strftime("%Y%m%d-%H%M%S"))
    seconds = int(args[0]) if args else 15
    if not pid:
        sys.exit("no Sojourn process running")
    cputrace.ensure_tool()
    cputrace.OUT.mkdir(parents=True, exist_ok=True)
    trace = cputrace.OUT / f"{label}-alloc.nettrace"
    env = dict(os.environ, DOTNET_ROOT=str(cputrace.SDK), DOTNET_ROLL_FORWARD="Major")
    # the GC keyword at verbose carries GCAllocationTick; dotnet-trace's rundown names the methods
    subprocess.run([str(cputrace.TOOL), "collect", "-p", str(pid), "--providers", "Microsoft-Windows-DotNETRuntime:0x1:5",
                    "--duration", f"00:00:00:{seconds:02d}", "-o", str(trace)], env=env, check=True, stdout=subprocess.DEVNULL)
    text = summary(trace, rest)
    (cputrace.OUT / f"{label}-alloc.txt").write_text(text, encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
