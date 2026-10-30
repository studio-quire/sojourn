"""Sojourn and the person's own screen reader (docs/accessibility.md > A1; python tools/screenreader.py ...).

  python tools/screenreader.py setup   NVDA's controller client (nvdaControllerClient.dll, NV Access, LGPL-2.1) into
                                       local/nvda/: the x64 DLL, its licence and readme, from NV Access's release zip
                                       (pinned, checked against its SHA-256). Without it Sojourn speaks to NVDA by UI
                                       Automation only, which NVDA reads less reliably.
  python tools/screenreader.py test    a client (Sojourn Reader) with read-out on, on the local grid's desktop, and a
                                       UI Automation listener standing in for Narrator (tools/uia_listen.ps1): chat,
                                       emotes, whispers, objects (a repeated line once), IMs, group chat, links, notices,
                                       a confirmation by keys (Escape, Enter) with what arrives meanwhile held behind it,
                                       the Notices Harp's menu by Ctrl+N and number keys and its next page read by
                                       itself, Delete, Ctrl+R, Ctrl+[, F1, the assistant's replies and choices, the
                                       parcel's song title, incognito, and off. Posted keys (SOJOURN_TEST_INPUT=1). The
                                       world's sound goes to a file, not the speakers.

The NVDA path itself needs NVDA running, which this test doesn't start (it would speak aloud and hook the keyboard).

Written by Claude Opus 5.5, 2026-10-27.
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request
import uuid
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

NVDA_VERSION = "2026.3"
NVDA_URL = f"https://download.nvaccess.org/releases/{NVDA_VERSION}/nvda_{NVDA_VERSION}_controllerClient.zip"
NVDA_SHA256 = "e38a00689eab422f13aaed9aaaacdd3ae96411643b6f2f5fbf86222660ac7938"
NVDA_DIR = ROOT / "local" / "nvda"

BASE = ROOT / "local" / "screenreader"
API_PORT = 9877
HARP = (66, 30, 24)


def setup():
    NVDA_DIR.mkdir(parents=True, exist_ok=True)
    archive = NVDA_DIR / f"nvda_{NVDA_VERSION}_controllerClient.zip"
    if not archive.exists():
        print(f"fetching {NVDA_URL}")
        urllib.request.urlretrieve(NVDA_URL, archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != NVDA_SHA256:
        archive.unlink()
        sys.exit(f"{archive.name}: checksum mismatch (removed; NV Access may have rebuilt it: check and update NVDA_SHA256)")
    with zipfile.ZipFile(archive) as z:
        for name, dest in (("x64/nvdaControllerClient.dll", "x64/nvdaControllerClient.dll"), ("license.txt", "license.txt"),
                           ("readme.md", "readme.md"), ("x64/nvdaController.h", "x64/nvdaController.h")):
            out = NVDA_DIR / dest
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_bytes(z.read(name))
    print(f"{(NVDA_DIR / 'x64' / 'nvdaControllerClient.dll').relative_to(ROOT)}: NVDA {NVDA_VERSION}'s controller client (LGPL-2.1)")
    return 0


# ---- the test ------------------------------------------------------------------------------------------------------

def api(pump, op, timeout=30, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{API_PORT}/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 20) as r:
        out = json.loads(r.read())
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def event(kind, data):
    body = json.dumps({"kind": kind, "data": data}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{API_PORT}/event", data=body, headers={"Content-Type": "application/json"})
    urllib.request.urlopen(req, timeout=10).read()


def wait(f, secs, step=0.3):
    end = time.time() + secs
    while time.time() < end:
        try:
            v = f()
            if v:
                return v
        except Exception:
            pass
        time.sleep(step)
    return None


KEYS_PS = r"""
param([int]$ProcessId, [string]$Keys)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class K {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
}
"@
$h = (Get-Process -Id $ProcessId).MainWindowHandle
# each step: +VK down, -VK up
foreach ($step in $Keys.Split(' ')) {
  $vk = [int]$step.Substring(1)
  $scan = [K]::MapVirtualKey([uint32]$vk, 0)
  if ($vk -eq 0x2E) { $scan = $scan -bor 0x100 }   # Delete is an extended key (bit 24 of lParam)
  if ($step[0] -eq '+') { [void][K]::PostMessage($h, 0x0100, [IntPtr]$vk, [IntPtr](1 -bor ($scan -shl 16))) }
  else { [void][K]::PostMessage($h, 0x0101, [IntPtr]$vk, [IntPtr]([int64]1 -bor ([int64]$scan -shl 16) -bor (3L -shl 30))) }
  Start-Sleep -Milliseconds 60
}
"""

VK = {"enter": 0x0D, "esc": 0x1B, "ctrl": 0x11, "shift": 0x10, "n": 0x4E, "r": 0x52, "[": 0xDB, "]": 0xDD, "f1": 0x70,
      "del": 0x2E, "p": 0x50, "1": 0x31, "2": 0x32, "3": 0x33, "f2": 0x71, "f12": 0x7B}


def test():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    import grid
    import notices
    import audio
    if notices.fixture():
        print("the Notices Harp isn't in the world")
        return 1
    audio.fixtures()
    print(grid.account("Sojourn", "Reader"))
    import socket
    with socket.socket() as probe:
        if probe.connect_ex(("127.0.0.1", API_PORT)) == 0:
            print(f"port {API_PORT} is taken (another client's agent API?): the test would talk to it")
            return 1
    radio = audio.serve(background=True)
    dll = Path(os.environ.get("SOJOURN_DLL") or ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll")
    shutil.rmtree(BASE, ignore_errors=True)
    (BASE / "client").mkdir(parents=True)
    (BASE / "client" / "vr-settings.json").write_text(json.dumps({"screenReader": {"mode": "on"}, "panelHidden": True}))
    keys_ps = BASE / "keys.ps1"
    keys_ps.write_text(KEYS_PS)
    heard = BASE / "uia.jsonl"
    env = dict(os.environ, SOJOURN_TEST_INPUT="1", SOJOURN_VOICE_OUT_FILE=str(BASE / "world.wav"))
    dotnet = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
    log = open(BASE / "console.txt", "w", encoding="utf-8")
    proc = subprocess.Popen([str(dotnet), str(dll), "--window", "800x500", "--fps", "20", "--no-assistant", "--no-voice",
                             "--first", "Sojourn", "--last", "Reader", "--password", "sojourn-local", "--bridge-port", str(API_PORT),
                             "--base-dir", str(BASE / "client"), "--start", "uri:Atelier&%d&%d&%d" % HARP, "--minutes", "9", "--sky", "noon"],
                            cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, text=True,
                            creationflags=0x00004000)
    listener = subprocess.Popen(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "uia_listen.ps1"), "-Out", str(heard),
                                 "-Seconds", "600", "-ProcessId", str(proc.pid)], stdin=subprocess.DEVNULL,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=0x00004000)
    ok = True
    seen = 0   # how many of the listener's lines were looked at

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)

    def lines():
        try:
            return [json.loads(x) for x in heard.read_text(encoding="utf-8").splitlines() if x.strip()]
        except (OSError, ValueError):
            return []

    def heard_since(start):
        return [x for x in lines()[start:] if "text" in x]

    def mark():
        return len(lines())

    def expect(name, words, start, secs=8, absent=False):
        """A line containing all these words heard since start (or, absent, none within secs)."""
        def find():
            return next((x for x in heard_since(start) if all(w in x["text"] for w in words)), None)
        got = wait(find, secs) if not absent else (time.sleep(secs) or find())
        if absent:
            check(name, got is None, "nothing" if got is None else got["text"])
        else:
            check(name, got, (got or {}).get("text", "not heard; heard: " + " | ".join(x["text"] for x in heard_since(start))[-300:]))
        return got

    def keys(*names):
        """Posted keys: "ctrl+n" is a chord, others one after another."""
        steps = []
        for n in names:
            parts = n.split("+")
            steps += ["+%d" % VK[p] for p in parts] + ["-%d" % VK[p] for p in reversed(parts)]
        subprocess.run(["powershell", "-NoProfile", "-File", str(keys_ps), "-ProcessId", str(proc.pid), "-Keys", " ".join(steps)],
                       capture_output=True)

    who = str(uuid.uuid4())

    def chat(msg, source=1, chat_type=1, style=0, name="Kestrel Tester", fid=who):
        event("LLChat", {"chat_type": chat_type, "source": source, "from": name, "from_id": fid, "message": msg, "chat_style": style})

    def im(msg, kind="p2p", group="Builders"):
        event("TesselEvents", {"event": "im", "dir": "in", "session_type": kind, "from_id": who, "from": "Kestrel Tester",
                               "to_id": "", "session_id": str(uuid.uuid4()) if kind == "p2p" else "6a1f0f0e-0000-4000-8000-00000000b11d",
                               "session_name": "Kestrel Tester" if kind == "p2p" else group, "message": msg})

    try:
        up = wait(lambda: api("LLAgent", "getPosition", timeout=5), 120, 1)
        check("logged in", up, str(up)[:80])
        check("the listener is up", wait(lambda: lines() and lines()[0].get("ready"), 20))
        # the greeting came as it logged in, before the listener could hear; off and on again says it
        api("SojournSettings", "set", screen_reader="off")
        time.sleep(4)
        api("SojournSettings", "set", screen_reader="on")
        expect("a greeting on starting to read out", ["Sojourn. F1 for keys."], 0, 10)
        st = api("SojournScreenReader", "status")
        check("status: reading out, through UI Automation (no NVDA here)", st.get("reading") and st.get("through") == "UI Automation", str(st))

        # what arrives, in order, who first
        m = mark()
        chat("hello there")
        chat("waves", style=1)
        chat("whispers: psst", chat_type=0)
        chat("shouts: over here", chat_type=2)
        chat("Welcome to Atelier", source=2, name="Greeter Board", fid=str(uuid.uuid4()))
        chat("Welcome to Atelier", source=2, name="Greeter Board", fid=str(uuid.uuid4()))
        im("are you there?")
        im("meeting at five", kind="group")
        chat("see https://www.example.com/page?x=1 and secondlife://Bay%20City/10/20/30")
        last = expect("the last of them", ["Kestrel Tester", "link"], m, 12)
        got = [x["text"] for x in heard_since(m)]
        want = ["Kestrel Tester: hello there", "Kestrel Tester waves", "Kestrel Tester whispers: psst", "Kestrel Tester shouts: over here",
                "Greeter Board: Welcome to Atelier", "Kestrel Tester, IM: are you there?", "Kestrel Tester, Builders: meeting at five",
                "Kestrel Tester: see a link to example.com and a place, Bay City"]
        check("chat, emote, whisper, shout, object, IM, group, links: each as said, in order", got == want, " | ".join(got))
        check("queued, not cutting each other off", all(x["processing"] == "All" for x in heard_since(m)), str({x["processing"] for x in heard_since(m)}))

        # a notice with nothing to answer
        m = mark()
        api("LLNotifications", "requestAdd", name="SystemMessage", substitutions={"MESSAGE": "Region restarting in 5 minutes"})
        n = expect("a notice", ["Region restarting in 5 minutes"], m)
        check("with nothing to answer, no key hint", n and "Control N" not in n["text"], (n or {}).get("text", ""))

        # a confirmation: the assistant's touch on someone else's harp waits; held lines follow it
        harp = wait(lambda: next((o["id"] for o in api("TesselObjects", "nearby", dist=60, query=notices.NAME)["objects"]
                                  if o.get("name") == notices.NAME), None), 60, 1)
        check("the harp is in view", harp, str(harp))
        m = mark()
        touch = subprocess.Popen([sys.executable, "-c", f"import sys; sys.path.insert(0, {str(ROOT / 'tools')!r}); import screenreader as s; "
                                  f"print(s.api('LLAgent', 'requestTouch', timeout=60, obj_uuid={harp!r}))"],
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        c = expect("the confirmation read out at once, with its keys", ["Enter confirms, Escape cancels"], m, 15)
        check("cutting in", c and c["processing"] == "ImportantMostRecent", (c or {}).get("processing", ""))
        m2 = mark()
        chat("while it waits")
        time.sleep(1.5)
        check("what arrives meanwhile waits behind it", not any("while it waits" in x["text"] for x in heard_since(m2)))
        keys("esc")
        expect("Escape cancels it", ["Cancelled."], m2)
        expect("and then the held line", ["while it waits"], m2)
        out = touch.communicate(timeout=60)[0]
        check("the touch was refused", "error" in out.lower() or "confirm" in out.lower(), out.strip()[-120:])

        m = mark()
        touch = subprocess.Popen([sys.executable, "-c", f"import sys; sys.path.insert(0, {str(ROOT / 'tools')!r}); import screenreader as s; "
                                  f"print(s.api('LLAgent', 'requestTouch', timeout=60, obj_uuid={harp!r}))"],
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        expect("again", ["Enter confirms"], m, 15)
        time.sleep(1.2)   # armed (0.8 s on screen)
        keys("enter")
        expect("Enter confirms it", ["Confirmed."], m)
        touch.communicate(timeout=60)
        menu = expect("the harp's menu arrives, with the key to answer it the first time", ["Notices Harp", "choose a piece", "Control N"], m, 20)
        time.sleep(1)

        # by keys: Ctrl+N reads it whole with numbered keys; 1 is More >; its next page is read by itself
        m = mark()
        keys("ctrl+n")
        expect("Ctrl+N reads the newest waiting notice whole with its keys", ["1 of 2.", "choose a piece", "1, More >", "minus, ", "Delete puts it away"], m)
        m = mark()
        keys("1")
        expect("1 presses More >", ["More >, done."], m)
        expect("the next page read by itself", ["more pieces", "1, < Back", "2, Lullaby"], m, 12)
        m = mark()
        keys("2")
        expect("2 presses Lullaby", ["Lullaby, done."], m)
        expect("and the harp's answer, as an object", ["Notices Harp: playing Lullaby"], m, 12)

        # Ctrl+R, Ctrl+[, F1
        m = mark()
        keys("ctrl+r")
        expect("Ctrl+R says the newest again", ["playing Lullaby"], m)
        m = mark()
        keys("ctrl+[", "ctrl+[")
        expect("Ctrl+[ steps back", ["Lullaby, done."], m)
        m = mark()
        keys("f1")
        expect("F1 says the keys", ["Control N", "Enter confirms"], m)

        # Delete puts one away
        m = mark()
        api("LLNotifications", "requestAdd", name="TeleportOffered", substitutions={"NAME": "Kestrel Tester", "MESSAGE": "come over"},
            payload={"from_id": who, "from_name": "Kestrel Tester"})
        offer = expect("an offer", ["Kestrel Tester", "come over"], m)
        m = mark()
        keys("ctrl+n")
        expect("picked, newest first", ["come over", "1 of 2"], m)
        m = mark()
        keys("ctrl+n")
        expect("the next is the older alert, with only Delete", ["2 of 2", "Region restarting", "Delete puts it away"], m)
        m = mark()
        keys("ctrl+shift+n")
        expect("Ctrl+Shift+N back to the offer", ["come over", "1 of 2"], m)
        m = mark()
        keys("del")
        expect("Delete puts it away", ["Put away"], m)

        # the assistant's replies and choices
        m = mark()
        api("SojournAssistant", "panel", result="The bar is to your left, about four metres.")
        expect("a reply", ["The bar is to your left"], m)
        m = mark()
        api("SojournAssistant", "panel", prompt="Which one?", choices=[{"id": "a", "label": "the red chair"}, {"id": "b", "label": "the sofa"}])
        expect("choices, numbered", ["Which one?", "1, the red chair", "2, the sofa"], m)
        api("SojournAssistant", "panel", clear=True)

        # the parcel's song, when it changes
        api("LLAgent", "requestTeleport", regionname="Atelier", x=audio.HOME[0], y=audio.HOME[1] - 6, z=32, skip_confirmation=True)
        time.sleep(3)
        m = mark()
        audio.PORT, saved = API_PORT, audio.PORT
        try:
            audio.box(f"music http://127.0.0.1:{audio.RADIO}/mp3")
            expect("the song's title as it starts", ["Now playing: Sojourn Test - mp3"], m, 20)
            m = mark()
            audio.box(f"music http://127.0.0.1:{audio.RADIO}/redirect")
            expect("and when it changes", ["Now playing: Sojourn Test - aac"], m, 20)
            audio.box("nomusic")
        finally:
            audio.PORT = saved

        # incognito: read out as the screen shows it, and nothing of it kept afterwards
        m = mark()
        keys("p")
        expect("incognito on, said", ["Incognito on."], m)
        chat("a secret line")
        expect("read out during it (the desktop shows it too)", ["a secret line"], m)
        m = mark()
        keys("p")
        expect("incognito off, said", ["Incognito off."], m)
        m = mark()
        keys("ctrl+[", "ctrl+[", "ctrl+[", "ctrl+[")
        time.sleep(2)
        check("the spell's lines are gone from the history", not any("secret" in x["text"] for x in heard_since(m)),
              " | ".join(x["text"] for x in heard_since(m)))

        # the menu, for a look at its tabs (a shot in local/screenreader/client/shots)
        keys("f2")
        time.sleep(1.5)
        keys("f12")
        time.sleep(1.5)
        keys("f2")

        # off
        api("SojournSettings", "set", screen_reader="off")
        time.sleep(4)
        m = mark()
        chat("not read out")
        expect("off: nothing read", ["not read out"], m, 4, absent=True)
        check("status says so", not api("SojournScreenReader", "status").get("reading"))
        api("SojournSettings", "set", announce_nearby=False, screen_reader="on")
        time.sleep(4)
        m = mark()
        chat("nearby off")
        im("ims still on")
        expect("a kind switched off isn't read", ["nearby off"], m, 4, absent=True)
        expect("the others are", ["ims still on"], m)
    finally:
        proc.terminate()
        try:
            proc.wait(20)
        except subprocess.TimeoutExpired:
            proc.kill()
        listener.kill()
        radio.shutdown()
    print("all good" if ok else "something failed (local/screenreader/console.txt, uia.jsonl)")
    return 0 if ok else 1


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "test"
    sys.exit({"setup": setup, "test": test}[cmd]())
