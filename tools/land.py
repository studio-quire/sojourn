"""Land and region (batch B5, SojournLand) on the local grid, never the main grid: the region's info and covenant, the
estate's lists, top scripts and colliders, a region message, the debug switches, About Land and its access and ban
lists, dividing, selling, buying, reclaiming, abandoning and joining, a pass, freezing, terraforming, land holdings,
map items, land search, and sending someone home; every gated op pressed (or cancelled) as tools/people.py does.

  python tools/land.py [--dll DIR] [--only NAME,...]

Two clients in Atelier Northeast: Estate Keeper (the Atelier estate's owner and every parcel's; its password set to
the grid's usual one on first run) on :9871 and Sojourn Friend on :9872, both with SOJOURN_TEST_INPUT=1 so posted
clicks answer their confirmations. What it changes it changes back: the parcel divided is joined again, the east half
comes back to the Keeper, list entries are removed, the land raised is lowered by as much. What only Second Life
shows is the user's checklist in docs/land.md. Written by Claude Opus 5.5, 2026-10-28.
"""
import json
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import people as P  # noqa: E402  (api, tryapi, wait, check, client)

ROOT = P.ROOT
P.BASE = ROOT / "local" / "land"
BASE = P.BASE
A, B = 9871, 9872
W, H = 1280, 720
api, tryapi, wait, check = P.api, P.tryapi, P.wait, P.check
START = "uri:Atelier Northeast&128&128&30"


class Side:
    def __init__(self, port, proc, base, width):
        self.port, self.proc, self.base, self.width = port, proc, base, width

    def key(self, vk):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(self.proc.pid), "-Vk", str(vk)], capture_output=True)

    def mouse(self, x, y, click=False):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winmouse.ps1"), "-X", str(int(x)), "-Y", str(int(y)),
                        *(["-Click"] if click else []), "-ProcessId", str(self.proc.pid)], capture_output=True)

    def shot(self):
        shots = BASE / self.base / "shots"
        before = set(shots.glob("*.png")) if shots.exists() else set()
        self.key(123)
        new = wait(lambda: set(shots.glob("*.png")) - before, 10)
        if not new:
            return None, None
        time.sleep(0.4)
        path = sorted(new)[-1]
        return path, np.asarray(Image.open(path).convert("RGB")).astype(int)

    def confirm_key(self):
        self.mouse(300, 500)
        time.sleep(0.3)
        path, img = self.shot()
        if img is None:
            return path, None
        mask = np.abs(img - np.array((0x23, 0x7c, 0x76))).max(axis=2) < 14
        mask[:, :self.width // 2] = False
        ys, xs = np.nonzero(mask)
        if len(xs) <= 50:
            return path, None
        cy = np.median(ys)
        band = np.abs(ys - cy) < 12
        xs, ys = xs[band], ys[band]
        cx = np.median(xs)
        near = np.abs(xs - cx) < 120
        xs, ys = xs[near], ys[near]
        return path, (xs.mean(), ys.mean(), np.percentile(xs, 98) - np.percentile(xs, 2))

    def call(self, op, **args):
        return tryapi(self.port, "SojournLand", op, timeout=60, **args)

    def gated(self, op, yes=True, pump="SojournLand", **args):
        out = {}
        th = threading.Thread(target=lambda: out.__setitem__("r", tryapi(self.port, pump, op, timeout=60, **args)))
        th.start()
        time.sleep(3.2)
        path, k = self.confirm_key()
        if k is None:
            th.join(2)
            return out.get("r", {"exception": "no confirmation seen"}), path
        x, y, w = k
        if not yes:
            x -= w + 20
        for _ in range(3):
            self.key(27)
            time.sleep(0.4)
            self.mouse(x, y)
            time.sleep(0.9)
            self.mouse(x, y, click=True)
            th.join(5)
            if not th.is_alive():
                break
        th.join(70)
        return out.get("r"), path


def is_err(r):
    return isinstance(r, dict) and ("exception" in r or "error" in r)


def main(dll, only):
    import grid
    if "agni" in grid.URL or not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    marker = BASE / "keeper-password-set"
    if not marker.exists():
        # Estate Keeper is the grid's estate owner and had no known password; give it the usual one
        BASE.mkdir(parents=True, exist_ok=True)
        print("\n".join(grid.console("reset user password Estate Keeper sojourn-local", wait=2)))
        marker.write_text("reset user password Estate Keeper sojourn-local (tools/land.py)\n")
    grid.account("Sojourn", "Friend", "sojourn-local")
    env = dict(os.environ, SOJOURN_TEST_INPUT="1")
    pa = P.client("Estate", "Keeper", A, "a", f"{W}x{H}", 20, dll, START, env)
    pb = P.client("Sojourn", "Friend", B, "b", f"{W}x{H}", 10, dll, "uri:Atelier Northeast&120&120&30", env)
    a, b = Side(A, pa, "a", W), Side(B, pb, "b", W)
    want = lambda name: not only or name in only
    shots = []
    try:
        for port, who in ((A, "Keeper"), (B, "Friend")):
            up = wait(lambda: json.loads(P.urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=3).read()).get("connected"), 120)
            check(f"{who} logged in", up)
            if not up:
                return
        time.sleep(8)
        # a portable NVDA another test may run would turn reader mode on (its keys answer confirmations)
        for port in (A, B):
            tryapi(port, "SojournSettings", "set", screen_reader="off")
        keeper = api(A, "LLAgent", "getID")["id"]
        friend = api(B, "LLAgent", "getID")["id"]

        # --- the region, for anyone -------------------------------------------------------------------------------
        if want("region"):
            r = a.call("region")
            check("region (Keeper)", r.get("name") == "Atelier Northeast" and r.get("i_manage") is True, r)
            r = b.call("region")
            check("region (Friend, not a manager)", r.get("name") == "Atelier Northeast" and r.get("i_manage") is False, r)
            r = b.call("covenant")
            check("covenant", r.get("estate") == "Atelier Estate" and "covenant" in r, r)
            r = b.call("estate")
            check("estate lists refused to a visitor", "owner and managers" in json.dumps(r), r)

        whole = a.call("parcel", at=[128, 128])
        check("About Land", whole.get("owner_id") == keeper and whole.get("area", 0) > 0, {k: whole.get(k) for k in ("name", "owner", "area", "bounds")})
        b0 = whole.get("bounds") or [0, 0, 256, 256]

        # --- the estate's lists ---------------------------------------------------------------------------------
        if want("estate"):
            r = a.call("estate")
            check("estate lists", r.get("estate") == "Atelier Estate" and isinstance(r.get("managers"), list), r)
            r, _ = a.gated("estateAdd", yes=False, list="allowed", to=friend)
            check("estate add cancelled", "didn't confirm" in json.dumps(r), r)
            r, path = a.gated("estateAdd", list="allowed", to=friend)
            check("Friend allowed in the estate (confirmed)", any(x.get("id") == friend for x in (r or {}).get("allowed") or []), r)
            shots.append(("estate add", path))
            r, _ = a.gated("estateRemove", list="allowed", to=friend)
            check("and off again (confirmed)", isinstance(r, dict) and not is_err(r) and not any(x.get("id") == friend for x in r.get("allowed") or []), r)
            r, _ = a.gated("estateAdd", list="managers", to=friend)
            check("Friend a manager (confirmed)", any(x.get("id") == friend for x in (r or {}).get("managers") or []), r)
            r, _ = a.gated("estateRemove", list="managers", to=friend)
            check("not a manager again", isinstance(r, dict) and not is_err(r) and not any(x.get("id") == friend for x in r.get("managers") or []), r)

        if want("top"):
            r = a.call("topScripts")
            check("top scripts", isinstance(r.get("objects"), list), r)
            r = a.call("topColliders")
            check("top colliders", isinstance(r.get("objects"), list), r)

        if want("message"):
            r, path = a.gated("message", text="Sojourn's land test: a message to the region")
            check("region message (confirmed)", isinstance(r, dict) and r.get("sent_to") == "the region", r)
            shots.append(("message", path))

        if want("debug"):
            r, _ = a.gated("regionDebug", scripts_disabled=True)
            check("scripts off (confirmed)", isinstance(r, dict) and r.get("scripts_disabled") is True, r)
            r, _ = a.gated("regionDebug", scripts_disabled=False)
            check("scripts on again", isinstance(r, dict) and r.get("scripts_disabled") is False, r)

        if want("restart"):
            r, _ = a.gated("restart", yes=False, delay=240)
            check("restart cancelled at the gate", "didn't confirm" in json.dumps(r), r)

        # --- About Land: lists, settings ------------------------------------------------------------------------
        if want("access"):
            r = a.call("access", at=[128, 128])
            check("access lists", isinstance(r.get("allowed"), list) and isinstance(r.get("banned"), list), r)
            r, path = a.gated("ban", to=friend, hours=1, at=[128, 128])
            check("Friend banned for an hour (confirmed)", any(x.get("id") == friend and x.get("until") for x in (r or {}).get("banned") or []), r)
            shots.append(("ban", path))
            r, _ = a.gated("unban", to=friend, at=[128, 128])
            check("and unbanned", isinstance(r, dict) and not is_err(r) and not any(x.get("id") == friend for x in r.get("banned") or []), r)
            r, _ = a.gated("allow", to=friend, at=[128, 128])
            check("Friend allowed (confirmed)", any(x.get("id") == friend for x in (r or {}).get("allowed") or []), r)
            r, _ = a.gated("unallow", to=friend, at=[128, 128])
            check("and off the list", isinstance(r, dict) and not is_err(r) and not any(x.get("id") == friend for x in r.get("allowed") or []), r)

        if want("freeze"):
            r, _ = a.gated("freeze", to=friend)
            check("Friend frozen (confirmed)", isinstance(r, dict) and r.get("frozen") is True, r)
            r = a.call("freeze", to=friend, freeze=False)
            check("and freed without asking", isinstance(r, dict) and r.get("frozen") is False, r)

        # --- divide, sell, buy, reclaim, abandon, join ----------------------------------------------------------
        east = [200, 128]
        if want("deals"):
            r, path = a.gated("divide", half="east", at=[128, 128])
            check("divided (confirmed)", isinstance(r, dict) and r.get("new_parcel", {}).get("area"), r)
            shots.append(("divide", path))
            part = a.call("parcel", at=east)
            check("the east half is its own parcel", part.get("local_id") != whole.get("local_id") and part.get("area", 0) < whole.get("area", 0), part)
            r, path = a.gated("sell", price=0, to=friend, at=east)
            check("for sale to Friend for L$0 (confirmed)", (r or {}).get("for_sale", {}).get("buyer_id") == friend, r)
            shots.append(("sell", path))
            items = a.call("mapItems", type="land_for_sale")
            check("map items answer", isinstance(items.get("items"), list), items)
            r = a.call("buy", at=east)
            check("the seller can't buy it", "nothing was bought" in json.dumps(r), r)
            r, path = b.gated("buy", at=east)
            check("Friend bought it (confirmed)", isinstance(r, dict) and r.get("owner_id") == friend, r)
            shots.append(("buy", path))
            r = b.call("holdings")
            check("Friend's holdings", any(p.get("area") for p in r.get("parcels") or []) or isinstance(r.get("parcels"), list), r)
            r, _ = b.gated("set", at=east, name="Friend's corner", options={"fly": True})
            check("Friend renamed it (confirmed)", isinstance(r, dict) and r.get("name") == "Friend's corner", r)
            r, _ = a.gated("reclaim", at=east)
            check("the Keeper reclaimed it (confirmed)", isinstance(r, dict) and r.get("owner_id") == keeper, r)
            r, _ = a.gated("sell", price=0, to=friend, at=east)
            r, _ = b.gated("buy", at=east)
            check("Friend bought it again", isinstance(r, dict) and r.get("owner_id") == friend, r)
            r, path = b.gated("abandon", at=east)
            check("Friend abandoned it (confirmed): the estate's again", isinstance(r, dict) and r.get("abandoned"), r)
            shots.append(("abandon", path))
            if want("pass"):
                r, _ = a.gated("set", at=east, pass_price=0, pass_hours=1, options={"pass_list": True})
                check("a pass on sale (confirmed)", isinstance(r, dict) and r.get("pass"), r)
                r, path = b.gated("buyPass", at=east)
                check("Friend bought a pass (confirmed)", isinstance(r, dict) and r.get("hours") == 1, r)
                shots.append(("pass", path))
                r = a.call("access", at=east)
                check("Friend on the list until it runs out", any(x.get("id") == friend and x.get("until") for x in r.get("allowed") or []), r)
                a.gated("unallow", to=friend, at=east)
                a.gated("set", at=east, options={"pass_list": False})
            r, path = a.gated("join", at=[[b0[0] + 8, 128], [b0[2] - 8, 128]])
            check("joined again (confirmed)", isinstance(r, dict) and r.get("parcel", {}).get("area") == whole.get("area"), r)
            shots.append(("join", path))
            r, _ = a.gated("set", at=[128, 128], name=whole.get("name"), options=whole.get("options"))
            check("its name and options as before", isinstance(r, dict) and r.get("name") == whole.get("name"), r)

        # --- terraform: a stroke up and one down by as much ------------------------------------------------------
        if want("terraform"):
            r = a.call("terraform", action="raise", at=[60, 60], size=2, seconds=0.5)
            up = isinstance(r, dict) and (r.get("height_after") or 0) > (r.get("height_before") or 0) + 0.01
            check("raised", up, r)
            time.sleep(1)
            r2 = a.call("terraform", action="lower", at=[60, 60], size=2, seconds=0.5)
            check("lowered back", isinstance(r2, dict) and abs((r2.get("height_after") or 0) - (r.get("height_before") or 0)) < 0.05, r2)
            r = b.call("terraform", action="raise", at=[60, 60])
            check("a visitor can't terraform the Keeper's land", is_err(r), r)
            r, _ = a.gated("terraform", yes=False, action="flatten", parcel=True, at=[128, 128])
            check("flattening the parcel waits (cancelled)", "didn't confirm" in json.dumps(r), r)

        if want("search"):
            r = tryapi(A, "SojournSearch", "land", timeout=30, max_price=1000)
            check("land search answers (OpenSim has none: empty)", isinstance(r.get("land"), list), r)
            r = a.call("holdings")
            check("the Keeper's holdings", isinstance(r.get("parcels"), list), r)
            r = a.call("mapItems", type="people")
            check("people on the map", isinstance(r.get("items"), list), r)

        if want("home"):
            r, path = a.gated("sendHome", to=friend)
            check("Friend sent home (confirmed)", isinstance(r, dict) and ("sent_home" in r), r)
            shots.append(("send home", path))
    finally:
        for p in (pa, pb):
            p.terminate()
        for p in (pa, pb):
            try:
                p.wait(15)
            except subprocess.TimeoutExpired:
                p.kill()
    for name, path in shots:
        print(f"   {name} confirmation: {path}")
    print("all ok" if P.ok_all else "some failed")


if __name__ == "__main__":
    dll = sys.argv[sys.argv.index("--dll") + 1] if "--dll" in sys.argv else str(ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0")
    only = set(sys.argv[sys.argv.index("--only") + 1].split(",")) if "--only" in sys.argv else set()
    main(dll, only)
