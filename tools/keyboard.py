"""The headset's keyboard in Meta's XR Simulator (python tools/keyboard.py [assistant] [page]).

assistant: SOJOURN_VR_SCRIPT=keyboard presses the panel's keyboard key, types and enters; the panel must hold "ok!".
page: My Media Board (tools/media_fixtures.py) up close through the agent API and its "type here" field clicked, then
the routine presses the board's keyboard key and types; the page's field must hold "ok!" (Enter is the page's).
Both as Media Pilot, with the panel shown (the idle panel carries the keyboard key).

Written by Claude Opus 5.5, 2026-10-26.
"""
import json
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import media_fixtures as mf  # noqa: E402

PORT = 9737


def run(target):
    base = ROOT / "local" / "keyboard" / target
    mf.API_PORT = PORT
    # media's start, with the panel shown (it hides it): the keyboard key is on the idle panel
    real = Path.write_text

    def shown(path, text, *a, **k):
        if path.name == "vr-settings.json":
            text = json.dumps({**json.loads(text), "panelHidden": False})
        return real(path, text, *a, **k)
    Path.write_text = shown
    try:
        proc = mf.start_client(base, PORT, vr=True, minutes=3, env_extra={"SOJOURN_VR_SCRIPT": "keyboard", "SOJOURN_KEYBOARD_TARGET": target})
    finally:
        Path.write_text = real
    log = base / "console.txt"
    ok = False
    try:
        if not mf.logged_in(PORT, 150):
            print(f"FAIL {target}: no login")
            return False
        if target == "page":
            key = None
            end = time.time() + 40
            while key is None and time.time() < end:
                try:
                    key = next((m["key"] for m in mf.api("list")["media"] if m.get("name") == "My Media Board" and m.get("state") == "loaded"), None)
                except Exception:
                    pass
                time.sleep(1)
            mf.say("myboard reset")
            time.sleep(2)
            mf.api("zoom", media=key, on=True)
            mf.api("press", media=key, words="type here")
        end = time.time() + 90
        while time.time() < end and "keyboard script" not in log.read_text(encoding="utf-8", errors="replace") or \
                time.time() < end and not any(l.rstrip().endswith("done") for l in log.read_text(encoding="utf-8", errors="replace").splitlines() if "keyboard script" in l):
            time.sleep(1)
        time.sleep(1.5)
        text = log.read_text(encoding="utf-8", errors="replace")
        if target == "assistant":
            ok = 'the panel heard "ok!", keyboard away' in text
            line = next((l for l in text.splitlines() if "the panel heard" in l), "no result line")
            print(f"{'ok  ' if ok else 'FAIL'} assistant: {line.split('sojourn[0] ')[-1]}")
        else:
            key = next(m["key"] for m in mf.api("list")["media"] if m.get("name") == "My Media Board")
            t = mf.api("read", media=key)["text"]
            state = t[t.find("clicks"):].split("\n")[0]
            ok = "ok!" in state
            print(f"{'ok  ' if ok else 'FAIL'} page: {state}")
        print("     keyboard log:", [l.split("sojourn[0] ")[-1] for l in text.splitlines() if "keyboard:" in l])
    finally:
        proc.terminate()
        try:
            proc.wait(20)
        except subprocess.TimeoutExpired:
            proc.kill()
    return ok


def main(names):
    names = names or ["assistant", "page"]
    if subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"], capture_output=True).returncode:
        print("the client didn't build")
        return 1
    srv = None
    if "page" in names:
        if mf.fixtures():
            return 1
        srv = mf.start_server()
    ok = True
    try:
        for n in names:
            ok &= run(n)
    finally:
        if srv:
            srv.shutdown()
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
