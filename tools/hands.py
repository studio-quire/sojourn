"""The person's hands over the headset's menus (docs/vr.md > Your own body > Hands over the menus), checked in Meta's
XR Simulator on the local grid: scripted hands in front of, through and behind the assistant's panel and the settings
menu (Xr/VrScript.Motion.cs: rhand, panel, shot), with the eyes saved as each pose settles. The eyes carry the menus
too (SOJOURN_PANEL_IN_EYES=1, where the cut applies as the headset's layers do), so the shots show what the headset
composites; the headset's own layers aren't in them.

  python tools/hands.py run [panel|menu|beam|feed|reader ...]   routines (default all); shots in
                                                     local/hands/shots/NAME.png (the eyes) and NAME-mirror.png (the
                                                     PC's window, with the headset's layers: the feed and reader are
                                                     only there)
  python tools/hands.py cost [SECONDS]               the GPU's frame with the hands' cut on and off, a hand held in
                                                     front of the settings menu, the eyes at Link's full size
                                                     (2448x2672); the console's VR lines and the "hands" pass

Runs as Sojourn Hands (its own account, base dir local/hands, agent API :9783). SOJOURN_DLL picks the build (default:
the usual Release build).
"""
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
DLL = Path(os.environ.get("SOJOURN_DLL", ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll"))
BASE = ROOT / "local" / "hands"
PORT = 9783
XR_SIM = ROOT / "local" / "xrsim"

# offsets from the head in the room (OpenXR: x right, y up, z back). The panel is put 0.6 m off, low and a little
# right, within an SL arm's reach (it rests 1.1 m off, beyond it); the right hand goes along the eye's ray through it: in front, its
# fingers through, and behind (the forearm then crosses it).
PANEL = (0.10, -0.42, -0.42)


def along(k, at=PANEL):
    n = sum(c * c for c in at) ** 0.5
    return " ".join(f"{c * k:.3f}" for c in at) if n else "0 0 0"


ROUTINES = {
    "panel": f"wait 4; press x 0.2; panel {' '.join(map(str, PANEL))}; rhand 0.25 -0.55 -0.15 0.5; wait 2; shot panel-none; "
             f"rhand {along(0.62)} 1.2; wait 1.5; shot panel-front; "
             f"rhand {along(0.95)} 1.2; wait 1.5; shot panel-through; "
             f"rhand {along(1.2)} 1.2; wait 1.5; shot panel-behind; "
             f"rhand 0.25 -0.55 -0.15 1; wait 1",
    # the menu opens 1.1 m off a little below the eyes; a step forward (within its leash) brings it into reach
    "menu": "wait 4; press menu 0.2; wait 2; head 0 0 -0.33 1; rhand 0.25 -0.55 -0.15 0.5; wait 1.5; shot menu-none; "
            "rhand 0.06 -0.12 -0.42 1.2; wait 1.5; shot menu-front; "
            "rhand 0.06 -0.17 -0.66 1.2; wait 1.5; shot menu-through; "
            "rhand 0.25 -0.55 -0.15 1; press menu 0.2; wait 1",
    # the pointer on, its beam from a hand in front of the panel onto it
    "beam": f"wait 4; press x 0.2; panel {' '.join(map(str, PANEL))}; press a 0.2; rhand {along(0.5)} 1; raim 0 -20; wait 1.5; shot beam-front; "
            f"raim 8 -35; wait 1; shot beam-front2; press a 0.2; wait 1",
    # the pointer over the chat feed (low on the left; lines said meanwhile fill it) and then the panel, for comparing:
    # the beam and its dot should show over both (the user once lost them over the feed and the reader)
    "feed": "wait 10; press a 0.2; rhand 0.05 -0.35 -0.25 1; "
            "raim 30 -8; wait 1.2; shot feed-a; raim 40 -12; wait 1.2; shot feed-b; raim 50 -5; wait 1.2; shot feed-c; "
            "raim 38 -20; wait 1.2; shot feed-d; raim 0 -25; wait 1.2; shot feed-panel; press a 0.2; wait 1",
    # the pointer over the reader (opened through SojournReader with a long text; it sits just above the panel), with
    # the pointer on and off (the reader takes the ray either way)
    "reader": "wait 10; rhand 0.05 -0.35 -0.25 1; raim 0 2; wait 1.5; shot reader-a; raim -8 6; wait 1.2; shot reader-b; "
              "raim 10 -2; wait 1.2; shot reader-c; press a 0.2; raim 0 4; wait 1.2; shot reader-pointer; press a 0.2; wait 1",
}


def api(op_pump, op, timeout=10, **args):
    import json
    import urllib.request
    body = json.dumps({"pump": op_pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/cmd", data=body, headers={"Content-Type": "application/json"})
    return json.loads(urllib.request.urlopen(req, timeout=timeout + 10).read())


def _until_connected(limit=90):
    import json
    import urllib.request
    end = time.time() + limit
    while time.time() < end:
        try:
            if json.loads(urllib.request.urlopen(f"http://127.0.0.1:{PORT}/status", timeout=3).read()).get("connected"):
                return True
        except OSError:
            pass
        time.sleep(1)
    return False


def _feed_lines():
    if not _until_connected():
        return
    for i in range(6):
        try:
            api("LLChatBar", "sendChat", message=f"feed line {i} for the pointer check")
        except OSError:
            pass
        time.sleep(1)


def _open_reader():
    if not _until_connected():
        return
    time.sleep(3)
    text = "\n\n".join(f"Paragraph {i}: the reader holds long text the person scrolls through by hand; the pointer's beam "
                       f"and dot should show over it as they do over the panel." for i in range(12))
    try:
        print("  reader:", api("SojournReader", "open", text=text, title="Pointer check"))
    except OSError as e:
        print("  reader: couldn't open:", e)


# what a routine needs set up while it runs (a thread started with the client)
SETUP = {"feed": _feed_lines, "reader": _open_reader}


def client(minutes, env_extra, extra_args=()):
    env = dict(os.environ)
    env.update(env_extra)
    manifests = sorted(XR_SIM.glob("*/meta_openxr_simulator.json"))
    env["XR_RUNTIME_JSON"] = str(manifests[-1])
    args = [str(DOTNET), str(DLL), "--first", "Sojourn", "--last", "Hands", "--password", "sojourn-local",
            "--base-dir", str(BASE), "--bridge-port", str(PORT), "--no-voice", "--no-media",
            "--start", "uri:Atelier&128&128&25", "--minutes", f"{minutes:.2f}",
            "--vr", "--xr-wait", "90", "--shadow-cap", "1536", *extra_args]
    BASE.mkdir(parents=True, exist_ok=True)
    return subprocess.Popen(args, cwd=ROOT, env=env, stdout=open(BASE / "console.log", "w"), stderr=subprocess.STDOUT,
                            creationflags=0x00004000)


def stop(p):
    p.terminate()
    try:
        p.wait(20)
    except subprocess.TimeoutExpired:
        p.kill()


def run(names):
    (BASE / "shots").mkdir(parents=True, exist_ok=True)
    for name in names:
        settings = BASE / "vr-settings.json"
        if settings.exists():
            settings.unlink()
        routine = "wait 12; " + ROUTINES[name]   # arriving and loading
        env = {"SOJOURN_VR_SCRIPT": "motion", "SOJOURN_MOTION": routine, "SOJOURN_PANEL_IN_EYES": "1"}
        p = client(4, env, ["--no-assistant"])
        if name in SETUP:
            import threading
            threading.Thread(target=SETUP[name], daemon=True).start()
        log = BASE / "console.log"
        t0 = time.time()
        done = False
        while time.time() - t0 < 240:
            time.sleep(2)
            text = log.read_text(errors="replace") if log.exists() else ""
            if "motion done" in text:
                time.sleep(2)
                done = True
                break
            if p.poll() is not None:
                break
        stop(p)
        (BASE / f"{name}-console.log").write_text(log.read_text(errors="replace") if log.exists() else "")
        print(f"--- {name}: {'done' if done else 'NOT DONE'}; shots in {BASE / 'shots'}")


def cost(seconds=60):
    """The GPU's frame time with the cut on and off, a hand held in front of the settings menu the whole time."""
    hold = f"wait 12; press menu 0.2; wait 2; head 0 0 -0.33 1; rhand 0.06 -0.12 -0.42 1; wait {seconds}"
    for label, on in (("on", "1"), ("off", "0"), ("on", "1")):
        env = {"SOJOURN_VR_SCRIPT": "motion", "SOJOURN_MOTION": hold, "SOJOURN_HANDS_OVER_MENUS": on}
        p = client((seconds + 70) / 60, env, ["--no-assistant", "--xr-scale", "1.46"])
        log = BASE / "console.log"
        t0 = time.time()
        while time.time() - t0 < seconds + 120:
            time.sleep(2)
            if "motion done" in (log.read_text(errors="replace") if log.exists() else "") or p.poll() is not None:
                break
        stop(p)
        text = log.read_text(errors="replace") if log.exists() else ""
        (BASE / f"cost-{label}-console.log").write_text(text)
        gpu = [float(m) for m in re.findall(r"VR \d+/\d+ fps, cpu [\d.]+ ms .*?gpu ([\d.]+) ms", text)]
        passes = re.findall(r"gpu passes: (.*)", text)
        settled = gpu[3:] or gpu
        print(f"hands {label}: gpu {sum(settled) / max(1, len(settled)):.2f} ms over {len(settled)} windows {settled}")
        hands_ms = [float(m) for m in re.findall(r"gpu passes: .*?hands ([\d.]+)", text)]
        if hands_ms:
            print(f"  the cut's passes ('hands'): {sum(hands_ms) / len(hands_ms):.2f} ms a frame over {len(hands_ms)} windows")
        if passes:
            print("  last passes:", passes[-1])


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "run"
    if cmd == "run":
        run(sys.argv[2:] or list(ROUTINES))
    elif cmd == "cost":
        cost(float(sys.argv[2]) if len(sys.argv) > 2 else 60)
    else:
        sys.exit(__doc__)
