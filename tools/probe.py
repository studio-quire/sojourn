"""Sojourn.Session without the VR client (docs/access.md > M0): two console hosts (src/Sojourn.SessionProbe) log in to
the local grid as Sojourn Probe (:9760) and Sojourn Echo (:9761), stand together in Atelier, and through their own
agent APIs answer SojournSettings and say a line in local chat every few seconds until each hears the other's.

  python tools/probe.py [--seconds 120] [--voice] [--sounds] [--render]
                                   needs the grid (tools/grid.py start); no GPU unless --render: each also starts the
                                   renderer out of sight (Session's Offscreen/) and takes a picture by the snapshot op
                                   (local/probe/<name>/look.png); take local/gpu.lock for that

Builds the probe first (Release). Each probe's console goes to local/probe/<name>.log; the PASS/FAIL lines are printed.
"""
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
PROJECT = ROOT / "src" / "Sojourn.SessionProbe" / "Sojourn.SessionProbe.csproj"
DLL = ROOT / "src" / "Sojourn.SessionProbe" / "bin" / "Release" / "net10.0" / "Sojourn.SessionProbe.dll"
BASE = ROOT / "local" / "probe"
BELOW_NORMAL = 0x00004000

sys.path.insert(0, str(Path(__file__).resolve().parent))
import grid  # noqa: E402


def main():
    rest = sys.argv[1:]
    seconds = rest[rest.index("--seconds") + 1] if "--seconds" in rest else "120"
    extra = [a for a in ("--voice", "--sounds", "--render") if a in rest]
    if not grid.ready():
        sys.exit("the local grid isn't up: python tools/grid.py start")
    if subprocess.run([str(DOTNET), "build", str(PROJECT), "-c", "Release", "-m:2", "-v:q", "-nologo", "-clp:ErrorsOnly", "-nodeReuse:false"],
                      creationflags=BELOW_NORMAL).returncode != 0:
        sys.exit("build failed")
    pair = [("Probe", 9760, "Atelier&128&118&26"), ("Echo", 9761, "Atelier&130&118&26")]
    for last, _, _ in pair:
        grid.account("Sojourn", last, "sojourn-local")
    BASE.mkdir(parents=True, exist_ok=True)
    procs = []
    for (last, port, start), (other, _, _) in zip(pair, reversed(pair)):
        log = open(BASE / f"{last.lower()}.log", "w", encoding="utf-8", errors="replace")
        args = [str(DOTNET), str(DLL), "--first", "Sojourn", "--last", last, "--port", str(port), "--start", f"uri:{start}",
                "--base", str(BASE / last.lower()), "--say", f"hello from {last.lower()}", "--expect", f"hello from {other.lower()}",
                "--seconds", seconds, *extra]
        procs.append((last, subprocess.Popen(args, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                                             creationflags=BELOW_NORMAL), log))
    ok = True
    for last, p, log in procs:
        try:
            code = p.wait(timeout=float(seconds) + 60)
        except subprocess.TimeoutExpired:
            p.kill()
            code = -1
        log.close()
        lines = (BASE / f"{last.lower()}.log").read_text(encoding="utf-8", errors="replace").splitlines()
        print(f"--- Sojourn {last} (exit {code})")
        for line in lines:
            if line.startswith("probe:") or " fail" in line.lower() or "exception" in line.lower():
                print("  " + line)
        ok &= code == 0
    print("probe: both passed" if ok else "probe: FAILED")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
