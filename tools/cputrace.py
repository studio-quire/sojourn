"""Where the running client's CPU goes: a sampled trace of a few seconds, summarised for the render thread.

  python tools/cputrace.py [SECONDS=20] [--pid PID] [--root NAME] [--label LABEL]
  python tools/cputrace.py summary FILE.speedscope.json [--root NAME]

Attaches dotnet-trace (installed into local/tools/dotnet-trace on first use) to the running Sojourn process (the newest
one, or --pid), takes SECONDS of the dotnet-sampled-thread-time profile, and prints, for the thread that spent longest
inside --root (default FrameRenderer.RenderFrame, the frame's renderer work; FrameRenderer.BuildDrawList for the draw
list alone), the methods by inclusive and exclusive share. Files go to local/cputrace/. Meant for a busy main-grid place
in the headset (Mirromere, a club) while the session runs: it slows the client a little while it samples.

Written by Claude Opus 5.5, 2026-10-26.
"""
import collections
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "local" / "cputrace"
SDK = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk"
TOOL = ROOT / "local" / "tools" / "dotnet-trace" / "dotnet-trace.exe"


def ensure_tool():
    if not TOOL.exists():
        subprocess.run([str(SDK / "dotnet.exe"), "tool", "install", "dotnet-trace", "--tool-path", str(TOOL.parent)], check=True)


def sojourn_pid():
    ps = ("Get-CimInstance Win32_Process -Filter \"name='dotnet.exe'\" | Where-Object { $_.CommandLine -like '*SojournVR.dll*' } | "
          "Sort-Object CreationDate | Select-Object -Last 1 -ExpandProperty ProcessId")
    out = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True).stdout.strip()
    return int(out) if out else None


def short(name):
    return re.sub(r"\(.*", "", name).split("!")[-1][-120:]


def summary(path, root="FrameRenderer.RenderFrame", top=45):
    d = json.load(open(path))
    frames = [f["name"] for f in d["shared"]["frames"]]
    best = None
    for p in d["profiles"]:
        stack, last = [], None
        incl, excl, tot = collections.Counter(), collections.Counter(), 0.0
        for e in p.get("events", []):
            t = e["at"]
            if last is not None and stack:
                names = [short(frames[i]) for i in stack]
                k = next((j for j, n in enumerate(names) if root in n), None)
                if k is not None:
                    dt = t - last
                    sub = names[k:]
                    tot += dt
                    for n in set(sub):
                        incl[n] += dt
                    excl[sub[-1]] += dt
            last = t
            if e["type"] == "O":
                stack.append(e["frame"])
            elif stack and stack[-1] == e["frame"]:
                stack.pop()
            elif e["frame"] in stack:
                stack = stack[:len(stack) - 1 - stack[::-1].index(e["frame"])]
        if best is None or tot > best[0]:
            best = (tot, p["name"], incl, excl)
    if not best or best[0] == 0:
        return f"nothing under {root}"
    tot, name, incl, excl = best
    lines = [f"{name}: {tot:.0f} ms inside {root} (waits included; UNMANAGED_CODE_TIME is mostly the GPU fence and the runtime)",
             "inclusive, % of that:"]
    lines += [f"  {100 * w / tot:5.1f}  {n}" for n, w in incl.most_common(top)]
    lines.append("exclusive:")
    lines += [f"  {100 * w / tot:5.1f}  {n}" for n, w in excl.most_common(25)]
    return "\n".join(lines)


def main():
    args = sys.argv[1:]

    def opt(name, default=None):
        if name in args:
            i = args.index(name)
            v = args[i + 1]
            del args[i:i + 2]
            return v
        return default

    root = opt("--root", "FrameRenderer.RenderFrame")
    if args[:1] == ["summary"]:
        print(summary(args[1], root))
        return
    pid = int(opt("--pid") or 0) or sojourn_pid()
    label = opt("--label", time.strftime("%Y%m%d-%H%M%S"))
    seconds = int(args[0]) if args else 20
    if not pid:
        sys.exit("no Sojourn process running")
    ensure_tool()
    OUT.mkdir(parents=True, exist_ok=True)
    trace = OUT / f"{label}.nettrace"
    env = dict(os.environ, DOTNET_ROOT=str(SDK), DOTNET_ROLL_FORWARD="Major")   # the tool targets .NET 8; the SDK has 10
    subprocess.run([str(TOOL), "collect", "-p", str(pid), "--profile", "dotnet-sampled-thread-time",
                    "--duration", f"00:00:00:{seconds:02d}", "-o", str(trace), "--format", "Speedscope"], env=env, check=True,
                   stdout=subprocess.DEVNULL)
    text = summary(OUT / f"{label}.speedscope.json", root)
    (OUT / f"{label}.txt").write_text(text, encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
