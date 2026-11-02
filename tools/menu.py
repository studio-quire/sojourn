"""The settings menu on the local grid, never the main grid (batch M): every section, group and page drawn as the running
client has them, and rows pressed as the person's press would, each read back from SojournSettings; everything changed
is put back at the end.

  python tools/menu.py [--dll DIR] [--size 0.7]   one desktop client, Sojourn Menu, on :9873 (base dir local/menu/),
                                                  SOJOURN_TEST_MENU=1 (SojournSettings menuShots and menuPress);
                                                  then Esc's layers and F2, by keys posted to its window
                                                  (--lang de: the menu drawn in a language, docs/languages.md)
  python tools/menu.py --headset [--dll DIR]      the camera (Control/Shutter.cs) in Meta's XR Simulator,
                                                  SOJOURN_VR_SCRIPT=camera: token, out, a picture, put away; and
                                                  the headset's menu shots (local/menu/headset/)

Shots go to local/menu/shots-<size>/ (menu-<section>[-<group>]-<page>.png, drawn as the headset's menu at that size).
Prints each check. Written by Claude Opus 5.5, 2026-10-29.
"""
import argparse
import json
import os
import socket
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
BASE = ROOT / "local" / "menu"
PORT = 9873
ok_all = True


def api(pump, op, timeout=20, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/cmd", data=body, method="POST", headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 60) as r:
        out = json.loads(r.read() or b"null")
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(out["error"])
    return out


def wait(f, seconds, step=0.5):
    end = time.time() + seconds
    while time.time() < end:
        try:
            if v := f():
                return v
        except Exception:
            pass
        time.sleep(step)
    return None


def check(name, cond, detail=""):
    global ok_all
    ok_all &= bool(cond)
    print(f"{'ok  ' if cond else 'FAIL'} {name}: {str(detail)[:300]}", flush=True)


def settings():
    return {k: v["value"] for k, v in api("SojournSettings", "get")["settings"].items()}


def press(section, label, part=0, group=None):
    args = {"section": section, "label": label, "part": part}
    if group:
        args["group"] = group
    return api("SojournSettings", "menuPress", **args)["value"]


# (section, group, row's label, part, then what SojournSettings should say: {name: value}); part 3 + i is option i
PRESSES = [
    ("social", None, "chat bubbles", 0, None),                       # a switch: flipped, checked by flipping
    ("social", None, "hover tips", 3 + 1, {"hover_tips": "on", "hover_tip_delay": 0.3}),
    ("social", "radar", "chat range", 3 + 3, {"radar_chat_range": "both"}),
    ("social", "radar", "new accounts", 1, {"radar_age_alert": 1}),
    ("social", "alerts", "keywords", 0, None),
    ("you", "turn away", "teleports", 3 + 1, {"reject_teleports": "not_friends"}),
    ("you", "turn away", "one-to-one calls", 0, None),
    ("you", "AO", "stands", 0, None),
    ("you", "AO", "lock its folder", 0, None),
    # the launcher's graphics presets (GraphicsPresets): light, then full
    ("view", None, "quality", 3 + 0, {"draw_distance": 64, "shadow_resolution": 1024, "reflections": "off", "mirrors": "off",
                                      "far_shadows": 0, "full_avatars": 8, "detail": 1}),
    ("view", None, "quality", 3 + 2, {"draw_distance": 256, "shadow_resolution": 3072, "reflections": "on", "mirrors": "on",
                                      "mirror_resolution": 1, "far_shadows": 2048, "full_avatars": 24, "detail": 2.5}),
    ("view", None, "location line", 3 + 1, {"location_line": "on", "lag_meter": "off"}),
    ("view", None, "location line", 3 + 2, {"location_line": "on", "lag_meter": "on"}),
    ("view", "avatars", "lip sync", 0, None),
    ("view", "avatars", "selection beams", 0, None),
    ("view", "graphics", "load all around", 0, None),
    ("view", "beacons", "beacons", 0, None),
    ("view", "beacons", "highlight transparent", 0, None),
    ("view", "beacons", "tracking beacon", 0, None),
    ("view", "parcel lines", "property lines", 0, None),
    ("view", "parcel lines", "ban lines", 3 + 2, {"ban_lines": "proximity"}),
    ("sound", None, "media first click", 1, {"media_first_click": "any"}),
    ("sound", None, "zoom to media", 0, None),
    ("voice", None, "hear from", 3 + 1, {"voice_hear_from": "avatar"}),
]

# the switch each label flips, where the check is "it changed"
SWITCHES = {"chat bubbles": "chat_bubbles", "keywords": "keyword_alerts", "one-to-one calls": "reject_p2p_calls", "stands": "ao_stands",
            "lock its folder": "ao_lock_folders", "lip sync": "lip_sync", "selection beams": "selection_beams", "load all around": "interest_360",
            "beacons": "beacons", "highlight transparent": "highlight_transparent", "tracking beacon": "tracking_beacon",
            "property lines": "property_lines", "zoom to media": "media_auto_zoom"}


def same(a, b):
    if isinstance(a, bool):
        a = "on" if a else "off"
    try:
        return abs(float(a) - float(b)) < 1e-3
    except (TypeError, ValueError):
        return str(a).lower() == str(b).lower()


def main(dll, size, lang=None):
    sys.path.insert(0, str(ROOT / "tools"))
    import grid
    if "agni" in grid.URL or not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    grid.account("Sojourn", "Menu", "sojourn-local")
    with socket.socket() as so:
        if so.connect_ex(("127.0.0.1", PORT)) == 0:
            sys.exit(f"something already listens on {PORT}")
    BASE.mkdir(parents=True, exist_ok=True)
    log = open(BASE / "console.txt", "w", encoding="utf-8")
    env = dict(os.environ, SOJOURN_TEST_MENU="1")
    proc = subprocess.Popen([str(DOTNET), str(Path(dll) / "SojournVR.dll"), "--first", "Sojourn", "--last", "Menu", "--password", "sojourn-local",
                             "--bridge-port", str(PORT), "--window", "640x360", "--fps", "10", "--no-assistant",
                             "--base-dir", str(BASE / "client"), "--start", "uri:Atelier&124&112&22", "--minutes", "10"]
                            + (["--lang", lang] if lang else []),
                            cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=0x00004000)
    before = None
    try:
        up = wait(lambda: json.loads(urllib.request.urlopen(f"http://127.0.0.1:{PORT}/status", timeout=3).read()).get("connected"), 120)
        check("logged in", up)
        if not up:
            return
        api("SojournSettings", "set", screen_reader="off")   # (a screen reader on the PC would turn reader mode on)
        if size:
            api("SojournSettings", "set", ui_size=size)
        time.sleep(4)
        before = settings()

        shots = BASE / f"shots-{size or 'now'}"
        for old in shots.glob("*.png") if shots.exists() else []:
            old.unlink()
        files = api("SojournSettings", "menuShots", timeout=60, dir=str(shots))["files"]
        check("every section drawn", len(files) >= 10, ", ".join(files))
        for want in ("menu-social-1.png", "menu-you-turn_away-1.png", "menu-you-ao-1.png", "menu-view-graphics-1.png", "menu-view-beacons-1.png",
                     "menu-social-radar-1.png", "menu-view-parcel_lines-1.png", "menu-view-avatars-1.png"):
            check(f"drawn {want}", want in files)

        for section, group, label, part, want in PRESSES:
            was = settings()
            try:
                shown = press(section, label, part, group)
            except Exception as e:
                check(f"{section} {group or ''} {label}", False, e)
                continue
            now = settings()
            if want is None:
                name = SWITCHES[label]
                check(f"{section} > {group + ' > ' if group else ''}{label}", name in now and now[name] != was.get(name),
                      f"{name}: {was.get(name)} -> {now.get(name)} (row shows {shown})")
            else:
                bad = {k: now.get(k) for k, v in want.items() if not same(now.get(k), v)}
                check(f"{section} > {group + ' > ' if group else ''}{label}", not bad, f"{want} (row shows {shown}){' got ' + str(bad) if bad else ''}")

        # beacons on: their kinds appear in the group, and the shots show it
        files = api("SojournSettings", "menuShots", timeout=60, dir=str(shots / "after"))["files"]
        check("beacons' kinds while on", any(f.startswith("menu-view-beacons-") for f in files), ", ".join(f for f in files if "beacons" in f))
        shown = press("you", "skeleton")
        check("skeleton · reset runs", shown in ("…", "done"), shown)
        escape(proc.pid, BASE / "console.txt")
        print(f"     shots: {shots}", flush=True)
    finally:
        if before is not None:
            # put back what changed (as values; refusals for ones a caller can't set are fine)
            now = settings()
            back = {k: v for k, v in before.items() if k in now and now[k] != v and v is not None
                    and k not in ("interface", "away", "screen_reader", "test_lag")}
            for k, v in back.items():
                try:
                    api("SojournSettings", "set", **{k: v})
                except Exception as e:
                    print(f"     (couldn't put back {k}: {e})", flush=True)
            print(f"     put back: {', '.join(back) or 'nothing'}", flush=True)
        proc.terminate()
        try:
            proc.wait(15)
        except subprocess.TimeoutExpired:
            proc.kill()
    print("all ok" if ok_all else "SOME FAILED", flush=True)


VK_ESC, VK_ENTER, VK_F2 = 0x1B, 0x0D, 0x71


def escape(pid, console):
    """Esc layered on the desktop (Program.cs, DeskEscape), by keys posted to the window (tools/winkey.ps1): what's open
    goes first, one a press, then the menu opens; Esc in it closes it; F2 still opens and closes it."""
    def key(vk):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(pid), "-Vk", str(vk)],
                       capture_output=True)
        time.sleep(0.8)

    def lines():
        text = console.read_text(encoding="utf-8", errors="replace")
        return text.count("settings menu open"), text.count("settings menu closed")

    def reader_open():
        return api("SojournReader", "state").get("open")

    opened, closed = lines()
    api("SojournReader", "open", text="Esc closes this before the menu opens.", title="Esc")
    check("(the reader open)", wait(reader_open, 5))
    key(VK_ESC)
    check("Esc closes the reader first, the menu staying shut", not reader_open() and lines()[0] == opened, f"reader open {reader_open()}, {lines()}")
    key(VK_ESC)
    check("Esc again, nothing open: the menu opens", lines()[0] == opened + 1, lines())
    key(VK_ESC)
    check("Esc in the menu closes it", lines()[1] == closed + 1, lines())
    key(VK_ENTER)   # typing to the assistant
    key(VK_ESC)
    check("Esc while typing cancels the typing, the menu staying shut", lines()[0] == opened + 1, lines())
    key(VK_ESC)
    check("then the menu opens", lines()[0] == opened + 2, lines())
    key(VK_F2)
    check("F2 still closes it", lines()[1] == closed + 2, lines())


def headset(dll):
    sys.path.insert(0, str(ROOT / "tools"))
    import grid
    import media_fixtures as mf
    if "agni" in grid.URL or not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    os.environ["SOJOURN_CLIENT_DLL"] = str(Path(dll) / "SojournVR.dll")
    base = BASE / "headset"
    pictures = Path(os.environ["USERPROFILE"]) / "Pictures" / "Sojourn"
    had = set(pictures.glob("*")) if pictures.exists() else set()
    proc = mf.start_client(base, PORT, vr=True, minutes=3, env_extra={"SOJOURN_VR_SCRIPT": "camera", "SOJOURN_TEST_MENU": "1"})
    log = base / "console.txt"
    text = lambda: log.read_text(encoding="utf-8", errors="replace")
    try:
        check("logged in (the simulator)", mf.logged_in(PORT, 150))
        api("SojournSettings", "set", screen_reader="off")
        check("the camera out by its token", wait(lambda: "camera out" in text(), 60))
        taken = wait(lambda: next((l for l in text().splitlines() if "picture:" in l or "picture not taken" in l), None), 30)
        check("a picture taken by the trigger", taken and "picture:" in taken, taken)
        check("put away by the token", wait(lambda: "camera put away" in text(), 30))
        files = api("SojournSettings", "menuShots", timeout=60, dir=str(base / "menu"))["files"]
        check("the headset's menu drawn (moving first)", files and files[0].startswith("menu-moving"), ", ".join(files))
        print("     shots:", sorted(str(p) for p in (base / "shots").glob("camera-*.png")), flush=True)
    finally:
        proc.terminate()
        try:
            proc.wait(20)
        except subprocess.TimeoutExpired:
            proc.kill()
        # the test's pictures out of the person's Pictures\Sojourn
        for f in (set(pictures.glob("*")) if pictures.exists() else set()) - had:
            print(f"     (removed the test picture {f.name})", flush=True)
            f.unlink()
    print("all ok" if ok_all else "SOME FAILED", flush=True)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")   # (rows show ≤ and …)
    ap = argparse.ArgumentParser()
    ap.add_argument("--dll", default=str(ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0"))
    ap.add_argument("--size", type=float, default=None)
    ap.add_argument("--headset", action="store_true")
    ap.add_argument("--lang", default=None)   # the menu in a language (docs/languages.md); rows are still pressed by their English
    a = ap.parse_args()
    headset(a.dll) if a.headset else main(a.dll, a.size, a.lang)
