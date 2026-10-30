"""Abuse reports (SojournReport) on the local grid, never the main grid: a report sent there reaches Linden Lab.

  python tools/report.py        Sojourn Tester drafts a report about the Fountain (tools/fixtures.py) and its owner,
                                with a picture; the send waits on the confirmation (the window's shot shows it whole),
                                a cancel sends nothing, a posted click on confirm (SOJOURN_TEST_INPUT=1) sends it as
                                the UserReport message (OpenSim has no report caps); refusals for long words, copyright
                                and an unknown draft

Written by Claude Opus 5.5, 2026-10-26.
"""
import json
import os
import subprocess
import sys
import threading
import time
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll"
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
BASE = ROOT / "local" / "tester"
PORT = 9722
W, H = 1280, 720


def api(pump, op, timeout=15, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/cmd", data=body, method="POST", headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 140) as r:
        out = json.loads(r.read() or b"null")
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(out["error"])
    return out


def wait(f, seconds):
    end = time.time() + seconds
    while time.time() < end:
        try:
            if v := f():
                return v
        except Exception:
            pass
        time.sleep(0.5)
    return None


def main():
    if "agni" in " ".join(sys.argv):
        sys.exit("local grid only")
    if subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"], capture_output=True).returncode:
        sys.exit("the client didn't build")
    account = (BASE / "account.txt").read_text().split("/")[1].split()[0]
    env = dict(os.environ, SOJOURN_TEST_INPUT="1")
    console = open(BASE / "report-console.txt", "w", encoding="utf-8")
    proc = subprocess.Popen([str(DOTNET), str(DLL), "--first", "Sojourn", "--last", "Tester", "--password", account,
                             "--bridge-port", str(PORT), "--window", f"{W}x{H}", "--fps", "20", "--no-assistant",
                             "--base-dir", str(BASE), "--start", "uri:Atelier&148&108&22"], cwd=ROOT, env=env,
                            stdout=console, stderr=subprocess.STDOUT)
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)

    def key(vk):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(proc.pid), "-Vk", str(vk)], capture_output=True)

    def click(x, y):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winmouse.ps1"), "-X", str(int(x)), "-Y", str(int(y)),
                        "-Click", "-ProcessId", str(proc.pid)], capture_output=True)

    def shot():
        shots = BASE / "shots"
        before = set(shots.glob("*.png")) if shots.exists() else set()
        key(123)   # F12: the window as drawn, the panel included
        new = wait(lambda: set(shots.glob("*.png")) - before, 10)
        if not new:
            return None, None
        time.sleep(0.3)
        path = sorted(new)[-1]
        return path, np.asarray(Image.open(path).convert("RGB")).astype(int)

    def send_in_background(draft):
        out = {}
        def run():
            try:
                out["r"] = api("SojournReport", "send", timeout=60, draft=draft)
            except Exception as e:
                out["r"] = str(e)
        th = threading.Thread(target=run)
        th.start()
        return th, out

    try:
        if not wait(lambda: json.loads(urllib.request.urlopen(f"http://127.0.0.1:{PORT}/status", timeout=3).read()).get("connected"), 90):
            check("login", False, "didn't log in")
            return 1
        time.sleep(8)   # the scene arrives
        cats = api("SojournReport", "categories")
        check("categories", len(cats["categories"]) >= 10, f"{len(cats['categories'])} from the {cats['source']}")
        fountain = wait(lambda: next((o for o in api("TesselObjects", "nearby", dist=40, query="Fountain").get("objects", []) if o.get("name") == "Fountain"), None), 20)
        check("the Fountain nearby", fountain, fountain and fountain["id"])

        # refusals before anything could be sent
        for name, args, want in [
            ("long summary", dict(summary="x" * 101, category=55), "100 characters"),
            ("long details", dict(summary="spam", details="y" * 281, category=55), "280 characters"),
            ("copyright", dict(summary="they copied my copyright mesh", category=55), "DMCA"),
            ("no category", dict(summary="spam", category="nonsense"), "category"),
        ]:
            try:
                api("SojournReport", "draft", **args)
                check(f"refused: {name}", False, "it was drafted")
            except Exception as e:
                check(f"refused: {name}", want in str(e), str(e)[:120])
        th, out = send_in_background("nosuchdraft")
        th.join(20)
        check("unknown draft refused at the gate", "no such draft" in str(out.get("r")), str(out.get("r"))[:120])

        d = api("SojournReport", "draft", object=fountain["id"], category="Repetitive spam",
                summary="This fountain keeps shouting adverts in local chat every few seconds",
                details="It started when I arrived and hasn't stopped; muting it only hides it from me.", timeout=20)
        check("draft", d.get("draft") and d.get("picture"), json.dumps(d)[:300])

        # cancel: nothing sent
        th, out = send_in_background(d["draft"])
        time.sleep(2.5)
        path, img = shot()
        check("confirmation shown", path is not None, str(path))
        # cancel is the left key: any press cancels; the panel sits at the top right, its keys at its bottom
        mask = (np.abs(img - np.array((0x23, 0x7c, 0x76))).max(axis=2) < 14) if img is not None else np.zeros((1, 1), bool)
        mask[:, :W // 2] = False
        ys, xs = np.nonzero(mask)
        if len(xs) > 50:
            key(27)
            time.sleep(0.4)
            click(xs.mean() - (xs.max() - xs.min()) - 20, ys.mean())   # the cancel key, left of confirm
        th.join(20)
        check("cancel sends nothing", "didn't confirm" in str(out.get("r")), str(out.get("r"))[:160])
        log = (BASE / "report-console.txt").read_text(encoding="utf-8", errors="replace")

        # the same draft again, confirmed
        th, out = send_in_background(d["draft"])
        time.sleep(2.5)
        path2, img = shot()
        mask = (np.abs(img - np.array((0x23, 0x7c, 0x76))).max(axis=2) < 14) if img is not None else np.zeros((1, 1), bool)
        mask[:, :W // 2] = False
        ys, xs = np.nonzero(mask)
        if len(xs) > 50:
            click(xs.mean(), ys.mean())
        th.join(20)
        r = out.get("r")
        check("confirmed and sent", isinstance(r, dict) and r.get("sent"), json.dumps(r)[:200] if isinstance(r, dict) else str(r)[:200])
        th, out = send_in_background(d["draft"])
        th.join(20)
        check("a draft goes once", "no such draft" in str(out.get("r")), str(out.get("r"))[:120])
        print(f"confirmation shot: {path}")
    finally:
        proc.terminate()
        try:
            proc.wait(15)
        except subprocess.TimeoutExpired:
            proc.kill()
        console.close()
    print("all ok" if ok else "some failed")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
