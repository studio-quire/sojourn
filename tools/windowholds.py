"""The window's thread's holds in Sojourn Access session logs, grouped by where it was held (WindowWatch.cs).

  python tools/windowholds.py LOG_OR_DIR... [--from 0.15] [--frames 3] [--each]

Each hold is the watch's "its thread was held N s" line, with the stack read during it (the "window: held N s, its thread
in ..." line before it, if one was read). Grouped by the first frames of ours in the stack (types found in src/), so a
framework's wait is named by what of ours called it; --frames how many of ours make the group (3). --each lists every
hold. A hold under --from seconds is left out. For a tester's log, or the scripted test's with SOJOURN_WINDOW_WATCH=120,150
(holds from 0.12 s, a stack at 0.15 s), which finds on this PC what holds the window seconds on a slower one.

Written by Claude Opus 5.5, 2026-11-13.
"""
import argparse
import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HELD = re.compile(r"window: its thread was held (\d+\.\d+) s")
STACK = re.compile(r"window: held (\d+\.\d+) s, its thread in \([^)]*\): (.*)")
TEST_HOLD = "Commands+<RunAsync>"   # (the scripted test's //holdwindow sleeps there)


def our_types():
    names = set()
    for p in (ROOT / "src").rglob("*.cs"):
        if "\\obj\\" in str(p) or "/obj/" in str(p) or "\\bin\\" in str(p):
            continue
        for m in re.finditer(r"\b(?:class|record|struct|interface)\s+([A-Z]\w*)", p.read_text(encoding="utf-8", errors="replace")):
            names.add(m[1])
    return names


def ours(frame, types):
    head = frame.split("+<", 1)[0].split(".", 1)[0]
    return head in types


def holds(path):
    """(seconds, stack frames or None, line number) for each hold in one log."""
    stack = None
    for n, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        if m := STACK.search(line):
            stack = m[2].split(" < ")
        elif m := HELD.search(line):
            yield float(m[1]), stack, n
            stack = None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("paths", nargs="+")
    ap.add_argument("--from", dest="least", type=float, default=0.15)
    ap.add_argument("--frames", type=int, default=3)
    ap.add_argument("--each", action="store_true")
    a = ap.parse_args()
    files = []
    for p in map(Path, a.paths):
        files += sorted([*p.glob("*.log"), *p.glob("*.txt")]) if p.is_dir() else [p]
    types = our_types()
    groups = defaultdict(list)
    for f in files:
        for secs, stack, n in holds(f):
            if secs < a.least:
                continue
            if stack and any(fr.startswith(TEST_HOLD) for fr in stack) and "Thread.Sleep" in stack[:3]:
                continue
            if stack is None:
                key = "(no stack read)"
            else:
                mine = [fr for fr in stack if ours(fr, types)]
                key = " < ".join(mine[:a.frames]) or "(none of ours) " + " < ".join(stack[:3])
            groups[key].append((secs, f.name, n, stack))
    if not groups:
        print("no holds")
        return
    for key, items in sorted(groups.items(), key=lambda kv: -sum(s for s, *_ in kv[1])):
        total = sum(s for s, *_ in items)
        print(f"{len(items):4d} holds, {total:6.2f} s in all, longest {max(s for s, *_ in items):.2f} s: {key}")
        if a.each:
            for secs, name, n, stack in sorted(items, reverse=True):
                print(f"       {secs:.2f} s  {name}:{n}" + (f"  {' < '.join(stack[:12])}" if stack else ""))
    print(f"{sum(len(v) for v in groups.values())} holds of {a.least} s or more in {len(files)} logs")


if __name__ == "__main__":
    sys.exit(main())
