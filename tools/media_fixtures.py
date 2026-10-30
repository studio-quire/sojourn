"""Sojourn's media tests: the local web server, the grid's fixtures and the client tests (python tools/media.py ...).

Written by Claude Opus 5.5, 2026-10-26.
"""
import http.server
import json
import math
import os
import socketserver
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MEDIA = ROOT / "local" / "media"
SITE = MEDIA / "site"
PORT = 9752
FFMPEG = "ffmpeg"

PAGE = """<!doctype html>
<html><head><meta charset="utf-8"><title>Sojourn media test</title>
<style>
 body { font: 28px/1.3 Segoe UI, sans-serif; margin: 0; background: #f4f1ea; color: #1d2a33; }
 header { background: #1d6f8c; color: white; padding: 24px 32px; font-size: 44px; }
 main { padding: 24px 32px; }
 button { font: inherit; padding: 16px 28px; margin: 8px 12px 8px 0; border-radius: 12px; border: 0; background: #e2a03f; }
 input { font: inherit; padding: 10px; width: 60%; }
 #status { font-family: Consolas, monospace; font-size: 22px; background: white; padding: 12px; margin-top: 16px; }
 #scroller { height: 260px; overflow-y: scroll; background: white; margin-top: 16px; padding: 0 12px; }
 .corner { position: fixed; width: 60px; height: 60px; }
</style></head>
<body>
<div class="corner" style="left:0;top:0;background:#d1342f"></div>
<div class="corner" style="right:0;top:0;background:#2f8ad1"></div>
<div class="corner" style="left:0;bottom:0;background:#3fae4f"></div>
<div class="corner" style="right:0;bottom:0;background:#9b3fd1"></div>
<header>Sojourn media test</header>
<main>
 <button id="press" onclick="pressed()">Press me</button>
 <button id="tone" onclick="tone()">Play a tone</button>
 <a id="next" href="/page2.html">Next page</a> &middot;
 <a id="away" href="http://example.com/">Leave</a> &middot;
 <a id="blank" href="/page2.html" target="_blank">New window</a>
 <p><input id="field" placeholder="type here"> <input id="secret" type="password" placeholder="password" style="width:20%"></p>
 <div id="status">clicks 0</div>
 <div id="scroller">""" + "".join(f"<p>Line {i} of the scroller.</p>" for i in range(1, 41)) + """</div>
</main>
<script>
 let clicks = 0, keys = '', lastX = -1, lastY = -1;
 function show() {
   document.getElementById('status').textContent =
     'clicks ' + clicks + ' at ' + lastX + ',' + lastY + ' typed [' + document.getElementById('field').value + ']' +
     ' scroll ' + Math.round(document.getElementById('scroller').scrollTop) + ' page ' + Math.round(window.scrollY);
 }
 function pressed() { clicks++; show(); }
 document.addEventListener('mousedown', e => { lastX = e.clientX; lastY = e.clientY; show(); });
 document.getElementById('field').addEventListener('input', show);
 document.getElementById('scroller').addEventListener('scroll', show);
 window.addEventListener('scroll', show);
 let ctx = null;
 function tone() {
   ctx = ctx || new AudioContext();
   const o = ctx.createOscillator(), g = ctx.createGain();
   o.frequency.value = 880; g.gain.value = 0.25;
   o.connect(g).connect(ctx.destination); o.start(); o.stop(ctx.currentTime + 1.5);
 }
 window.sojournTest = () => ({ clicks, typed: document.getElementById('field').value,
   scroller: document.getElementById('scroller').scrollTop, page: window.scrollY, x: lastX, y: lastY });
 show();
</script>
</body></html>
"""

PAGE2 = """<!doctype html><html><head><meta charset="utf-8"><title>Page two</title></head>
<body style="font: 40px Segoe UI; background: #233; color: #eef; margin: 40px">
<h1>Page two</h1><p><a href="/page.html">Back to the first page</a></p>
<p><a href="file:///C:/Windows/win.ini">A local file</a></p>
<iframe src="file:///C:/Windows/win.ini" width="400" height="80"></iframe>
<script>fetch('http://127.0.0.1:9720/status').then(r => document.title = 'API reached').catch(e => document.title = 'Page two (API refused)');</script>
</body></html>
"""

BIG = """<!doctype html><html><head><meta charset="utf-8"><title>A long page</title></head>
<body style="font: 30px/1.4 Georgia, serif; margin: 40px; background: #fffdf8">
<h1>A long page to read</h1>""" + "".join(
    f"<p>Paragraph {i}. The quick brown fox jumps over the lazy dog, and the five boxing wizards jump quickly; "
    f"sphinx of black quartz, judge my vow.</p>" for i in range(1, 80)) + "</body></html>"


def make_site():
    SITE.mkdir(parents=True, exist_ok=True)
    (SITE / "page.html").write_text(PAGE, encoding="utf-8")
    (SITE / "page2.html").write_text(PAGE2, encoding="utf-8")
    (SITE / "big.html").write_text(BIG, encoding="utf-8")
    video = SITE / "video.mp4"
    if not video.exists():
        # 20 s of a moving test pattern with a 440 Hz tone on the left and 660 Hz on the right
        subprocess.run([FFMPEG, "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30",
                        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-f", "lavfi",
                        "-i", "sine=frequency=660:sample_rate=48000", "-filter_complex", "[1:a][2:a]amerge=inputs=2[a]",
                        "-map", "0:v", "-map", "[a]", "-t", "20", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast",
                        "-c:a", "aac", "-b:a", "128k", str(video)], check=True, creationflags=0x00004000)
    tone = SITE / "tone.mp3"
    if not tone.exists():
        subprocess.run([FFMPEG, "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=523:sample_rate=48000",
                        "-t", "30", "-ac", "2", "-c:a", "libmp3lame", "-b:a", "128k", str(tone)], check=True, creationflags=0x00004000)


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=str(SITE), **kw)

    def log_message(self, fmt, *args):
        pass

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        self.send_header("Accept-Ranges", "bytes")
        super().end_headers()

    def do_GET(self):
        # byte ranges, as real servers answer them (players seek through files with them)
        rng = self.headers.get("Range")
        path = Path(self.translate_path(self.path))
        if not rng or not rng.startswith("bytes=") or not path.is_file():
            return super().do_GET()
        size = path.stat().st_size
        a, _, b = rng[6:].split(",")[0].partition("-")
        start = int(a) if a else max(0, size - int(b))
        end = int(b) if a and b else size - 1
        if start >= size:
            self.send_response(416)
            self.send_header("Content-Range", f"bytes */{size}")
            self.end_headers()
            return
        end = min(end, size - 1)
        self.send_response(206)
        self.send_header("Content-Type", self.guess_type(str(path)))
        self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.send_header("Content-Length", str(end - start + 1))
        self.end_headers()
        try:
            with open(path, "rb") as f:
                f.seek(start)
                left = end - start + 1
                while left > 0:
                    chunk = f.read(min(left, 1 << 16))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    left -= len(chunk)
        except (ConnectionError, OSError):
            pass

    def copyfile(self, source, outputfile):
        try:
            super().copyfile(source, outputfile)
        except (ConnectionError, OSError):
            pass


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def start_server():
    make_site()
    srv = Server(("127.0.0.1", PORT), Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv


def serve():
    start_server()
    print(f"serving {SITE} on http://127.0.0.1:{PORT}/ (Ctrl+C stops)")
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        return 0


# ---- the fixtures: four objects on Atelier's north lawn, west of the Sound Box, loaded once from an OAR made here ----

TAG = "tools/media_fixtures.py 5"   # in their descriptions: older ones are replaced
SPOT = (126.0, 238.0, 31.6)         # the row's middle; the test stands south of it, facing north
FACE = 1                            # the face toward -y on a box, where the media goes
PARCEL_TEX = "6d1e3f77-0c44-4f0e-9a51-6a3e2f0d7b11"   # no such asset: only parcel media draws on it
PLYWOOD = "89556747-24cb-43ed-920b-47caed15465f"
BLANK = "5748decc-f629-461c-9a36-a35a221fe21f"
SITE_URL = f"http://127.0.0.1:{PORT}"

# name, owner ("land": the parcel's owner, "pilot": the test account), x offset, size, media settings (None: the
# parcel's screen); tag: the first word of its commands on channel 8
OBJECTS = [
    ("Media Board", "land", -6, (2.0, 0.05, 1.0), dict(tag="board", url="/page.html", w=1024, h=512, control="owner", whitelist=True, autozoom=True)),
    ("My Media Board", "pilot", -2, (2.0, 0.05, 1.0), dict(tag="myboard", url="/page.html", w=1024, h=512, control="anyone", whitelist=False, firstclick=True)),
    ("Media TV", "land", 2, (1.6, 0.05, 0.9), dict(tag="tv", url="/video.mp4", w=0, h=0, control="anyone", whitelist=False, autoscale=True, loop=True)),
    ("Parcel Screen", "land", 6, (1.6, 0.05, 1.0), None),
]

BOARD = """// %(name)s (tools/media_fixtures.py): media on face %(face)d; told over channel 8 ("%(tag)s url <url>", "%(tag)s reset")
setup()
{
    llSetPrimitiveParams([PRIM_TEXTURE, %(face)d, "%(blank)s", <%(ru)s, %(rv)s, 0>, <%(ou)s, %(ov)s, 0>, 0, PRIM_FULLBRIGHT, %(face)d, TRUE]);
    llSetPrimMediaParams(%(face)d, [PRIM_MEDIA_CURRENT_URL, "%(url)s", PRIM_MEDIA_HOME_URL, "%(url)s", PRIM_MEDIA_AUTO_PLAY, TRUE,
        PRIM_MEDIA_AUTO_LOOP, %(loop)s, PRIM_MEDIA_AUTO_SCALE, %(autoscale)s, PRIM_MEDIA_AUTO_ZOOM, %(autozoom)s,
        PRIM_MEDIA_FIRST_CLICK_INTERACT, %(firstclick)s, PRIM_MEDIA_WIDTH_PIXELS, %(w)d, PRIM_MEDIA_HEIGHT_PIXELS, %(h)d,
        PRIM_MEDIA_PERMS_INTERACT, PRIM_MEDIA_PERM_ANYONE, PRIM_MEDIA_PERMS_CONTROL, %(control)s,
        PRIM_MEDIA_WHITELIST_ENABLE, %(wl)s, PRIM_MEDIA_WHITELIST, "127.0.0.1:%(port)d"]);
}
default
{
    state_entry() { setup(); llListen(8, "", NULL_KEY, ""); }
    on_rez(integer p) { llResetScript(); }
    listen(integer ch, string name, key id, string msg)
    {
        list a = llParseString2List(msg, [" "], []);
        if (llList2String(a, 0) != "%(tag)s") return;
        string c = llList2String(a, 1);
        if (c == "url") llSetPrimMediaParams(%(face)d, [PRIM_MEDIA_CURRENT_URL, llList2String(a, 2)]);
        else if (c == "reset") setup();
    }
}
"""

SCREEN = """// Parcel Screen (tools/media_fixtures.py): shows the parcel's media texture; sets the parcel's media over channel 8
// ("screen on <url> <type>", "screen off", "screen play", "screen stop"), and offers a page to whoever says
// "screen offer <url>" (llLoadURL)
default
{
    state_entry() { llListen(8, "", NULL_KEY, ""); }
    on_rez(integer p) { llResetScript(); }
    listen(integer ch, string name, key id, string msg)
    {
        list a = llParseString2List(msg, [" "], []);
        if (llList2String(a, 0) != "screen") return;
        string c = llList2String(a, 1);
        if (c == "on") llParcelMediaCommandList([PARCEL_MEDIA_COMMAND_URL, llList2String(a, 2), PARCEL_MEDIA_COMMAND_TEXTURE, (key)"%(tex)s",
            PARCEL_MEDIA_COMMAND_TYPE, llList2String(a, 3), PARCEL_MEDIA_COMMAND_SIZE, 1024, 1024]);
        else if (c == "off") llParcelMediaCommandList([PARCEL_MEDIA_COMMAND_URL, "", PARCEL_MEDIA_COMMAND_TYPE, "none/none"]);
        else if (c == "play") llParcelMediaCommandList([PARCEL_MEDIA_COMMAND_PLAY]);
        else if (c == "stop") llParcelMediaCommandList([PARCEL_MEDIA_COMMAND_STOP]);
        else if (c == "offer") llLoadURL(id, "A page for the media test", llList2String(a, 2));
    }
}
"""


def uid(*parts):
    """Fresh ids for each version of the fixtures: a replaced object or script never shares one with the old."""
    import uuid
    return str(uuid.uuid5(uuid.UUID("5b0d7e1c-8a64-4f2b-9c3e-1d7f0a2b6c94"), "/".join(map(str, (TAG,) + parts))))


def accounts():
    import sqlite3
    import grid
    c = sqlite3.connect(f"file:{grid.BIN / 'userprofiles.db'}?mode=ro", uri=True)
    try:
        return {f"{r[1]} {r[2]}": r[0] for r in c.execute("select PrincipalID, FirstName, LastName from UserAccounts")}
    finally:
        c.close()


def loaded():
    import sqlite3
    import grid
    c = sqlite3.connect(f"file:{grid.BIN / 'OpenSim.db'}?mode=ro", uri=True)
    try:
        names = tuple(o[0] for o in OBJECTS)
        return c.execute(f"select Name, OwnerID, Description from prims where Name in ({','.join('?' * len(names))})", names).fetchall()
    finally:
        c.close()


def board_script(name, m):
    # the content sits in the texture's corner at (0, 0), its sides each rounded up to a power of two; the face shows
    # just the content
    def pow2(v):
        p = 1
        while p < v:
            p *= 2
        return p
    scaled = m["w"] and not m.get("autoscale")
    ru = m["w"] / pow2(m["w"]) if scaled else 1.0
    rv = m["h"] / pow2(m["h"]) if scaled else 1.0
    return BOARD % dict(name=name, face=FACE, port=PORT, tag=m["tag"], blank=BLANK, url=SITE_URL + m["url"], w=m["w"], h=m["h"],
                        ru=f"{ru:g}", rv=f"{rv:g}", ou=f"{(ru - 1) / 2:g}", ov=f"{(rv - 1) / 2:g}",
                        loop="TRUE" if m.get("loop") else "FALSE", autoscale="TRUE" if m.get("autoscale") else "FALSE",
                        autozoom="TRUE" if m.get("autozoom") else "FALSE", firstclick="TRUE" if m.get("firstclick") else "FALSE",
                        control="PRIM_MEDIA_PERM_OWNER" if m["control"] == "owner" else "PRIM_MEDIA_PERM_ANYONE",
                        wl="TRUE" if m["whitelist"] else "FALSE")


def build_oar(land, pilot):
    import tarfile
    import audio
    import forest
    out = MEDIA / "fixtures.oar"
    with tarfile.open(out, "w:gz") as tar:
        forest.add(tar, "archive.xml", forest.archive_xml())
        for n, (name, who, dx, size, m) in enumerate(OBJECTS):
            owner = land if who == "land" else pilot
            pid = uid("part", name)
            te = forest.te_bytes(PLYWOOD, {FACE: PARCEL_TEX} if m is None else None, color=(0.85, 0.85, 0.85, 1))
            xml = forest.part(pid, 100 + n, name, (SPOT[0] + dx, SPOT[1], SPOT[2]), (0, 0, 0, 1), size, te)
            xml = xml.replace(forest.OWNER, owner).replace("<Description />", f"<Description>{TAG}</Description>")
            script = SCREEN % dict(tex=PARCEL_TEX) if m is None else board_script(name, m)
            item = audio.task_item(uid("item", name), uid("asset", name), "controller", 10, pid, owner)
            xml = xml.replace("<InventorySerial>0</InventorySerial>", "<InventorySerial>1</InventorySerial>")
            xml = xml.replace("</SceneObjectPart>", "<TaskInventory>" + item + "</TaskInventory></SceneObjectPart>")
            forest.add(tar, f"objects/{name.replace(' ', '_')}__{pid}.xml", forest.group([xml]).encode())
            forest.add(tar, f"assets/{uid('asset', name)}_script.lsl", script.encode())
    return out


def fixtures():
    sys.path.insert(0, str(ROOT / "tools"))
    import audio
    import grid
    if not grid.start():
        sys.exit("the grid didn't start")
    print(grid.account("Media", "Pilot", "sojourn-local"))
    land = audio.land_owner()
    pilot = accounts()["Media Pilot"]
    want = {(o[0], land if o[1] == "land" else pilot, TAG) for o in OBJECTS}
    have = set(loaded())
    if have == want:
        print("the media fixtures are there")
        return 0
    grid.console('change region "Atelier"', wait=1)
    for name in {h[0] for h in have}:
        grid.console(f'delete object name "{name}"', wait=2, answers=("y",))
    path = build_oar(land, pilot).resolve()
    print("\n".join(grid.console(f'load oar --merge "{path}"', wait=5)[-3:]))
    end = time.time() + 120
    while time.time() < end and set(loaded()) != want:
        grid.console("backup", wait=3)   # the region writes new objects out lazily: now, please
        time.sleep(2)
    ok = set(loaded()) == want
    print("the media fixtures are loaded" if ok else f"the media fixtures didn't all load: {loaded()} (local/grid/console.log)")
    return 0 if ok else 1


# ---- the test: a client (Media Pilot) against the fixtures, through the agent API and the window ---------------------

API_PORT = 9734
PILOT = MEDIA / "pilot"
STAND = (124.0, 231.0)   # in front of My Media Board, 7 m south of the row
CASES = "autoplay read press type password scroll look face click settings autoscale focus navigate region whitelist video sound parcel zoom offer stop".split()


def api(op, timeout=30, pump="SojournMedia", **args):
    import urllib.request
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{API_PORT}/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 20) as r:
        out = json.loads(r.read())
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def say(msg):
    api("sendChat", pump="LLChatBar", message=msg, channel=8)


def project(cam, point, w, h):
    """A region point to window pixels for the desktop camera (getCamera: position, focus, vertical fov)."""
    import numpy as np
    pos, focus = np.array(cam["camera_pos"]), np.array(cam["focus_pos"])
    f = focus - pos
    f /= np.linalg.norm(f)
    r = np.cross(f, [0, 0, 1.0])
    r /= np.linalg.norm(r)
    u = np.cross(r, f)
    d = np.array(point) - pos
    z = d @ f
    t = math.tan(cam["fov"] / 2)
    x, y = (d @ r) / z / (t * w / h), (d @ u) / z / t
    return (x + 1) / 2 * w, (1 - y) / 2 * h


def face_point(frame, cw, ch, px, py):
    """A page pixel on a face (status's face_frame) in the region."""
    import numpy as np
    c, n = np.array(frame["centre"]), np.array(frame["normal"])
    right = np.cross([0, 0, 1.0], n)
    right /= np.linalg.norm(right)
    up = np.cross(n, right)
    sw, sh = frame["size"]
    return c + right * (px / cw - 0.5) * sw + up * (0.5 - py / ch) * sh + n * 0.03


def test(names):
    import base64
    import shutil
    import numpy as np
    sys.path.insert(0, str(ROOT / "tools"))
    import audio
    import grid
    names = set(names or CASES)
    if not os.environ.get("SOJOURN_CLIENT_DLL") and subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"], capture_output=True).returncode:
        print("the client didn't build: python tools/client.py build")
        return 1
    if subprocess.run([sys.executable, str(ROOT / "tools" / "media.py"), "build"]).returncode:
        return 1
    if fixtures():
        return 1
    srv = start_server()
    # a fresh base dir: the person's settings as they'd start, but touches on others' things not confirmed (the
    # confirmation itself is checked by hand: posted clicks can't press it)
    shutil.rmtree(PILOT, ignore_errors=True)
    PILOT.mkdir(parents=True)
    (PILOT / "vr-settings.json").write_text(json.dumps({"confirmTouches": False, "panelHidden": True}))
    heard = PILOT / "heard.wav"
    env = dict(os.environ, SOJOURN_VOICE_OUT_FILE=str(heard), SOJOURN_TEST_INPUT="1")
    dotnet = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
    client = Path(os.environ.get("SOJOURN_CLIENT_DLL") or ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll")   # SOJOURN_CLIENT_DLL: built elsewhere, not rebuilt
    log = open(PILOT / "console.txt", "w", encoding="utf-8")
    W, H = 960, 540
    proc = subprocess.Popen([str(dotnet), str(client), "--window", f"{W}x{H}", "--fps", "30", "--no-assistant", "--no-voice",
                             "--first", "Media", "--last", "Pilot", "--password", "sojourn-local", "--bridge-port", str(API_PORT),
                             "--base-dir", str(PILOT), "--start", f"uri:Atelier&{STAND[0]:.0f}&{STAND[1]:.0f}&31", "--minutes", "10",
                             "--sky", "noon"],
                            cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, text=True,
                            creationflags=0x00004000)
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)

    def wait(f, secs, step=0.5):
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

    def items():
        out = {}
        for m in api("list")["media"]:
            out[m["key"]] = m
            if m["kind"] == "prim" and m.get("name"):
                out[m["name"]] = m
        return out

    def item(name):
        return items().get(name)

    def status(name):
        return api("status", media=item(name)["key"])

    def page_state(name):
        """The test page's own counters (window.sojournTest), through read's text."""
        t = api("read", media=item(name)["key"])["text"]
        return t[t.find("clicks"):].split("\n")[0]

    def key(vk):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(proc.pid), "-Vk", str(vk)],
                       capture_output=True)

    def click(x, y):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winmouse.ps1"), "-X", str(int(x)), "-Y", str(int(y)),
                        "-Click", "-ProcessId", str(proc.pid)], capture_output=True)

    def shot():
        shots = PILOT / "shots"
        before = set(shots.glob("*.png")) if shots.exists() else set()
        key(123)   # F12: the window's image as drawn, overlay included
        new = wait(lambda: set(shots.glob("*.png")) - before, 10)
        if not new:
            return None
        time.sleep(0.3)
        from PIL import Image
        return np.asarray(Image.open(sorted(new)[-1]).convert("RGB")).astype(int)

    try:
        if not wait(lambda: json.loads(__import__("urllib.request").request.urlopen(f"http://127.0.0.1:{API_PORT}/status", timeout=3).read()).get("connected"), 90):
            check("logged in", False, "no login in 90 s")
            return 1
        say("screen off")
        say("myboard reset")
        say("board reset")
        api("lookAt", pump="LLAgent", position=[SPOT[0] - 2, SPOT[1], SPOT[2]])
        api("set", pump="SojournSettings", camera="first_person")
        have = wait(lambda: {"Media Board", "My Media Board", "Media TV"} <= set(items()) and items(), 60)
        check("found", have and {"Media Board", "My Media Board", "Media TV"} <= set(have), f"{sorted(have or [])}")
        if not have:
            return 1

        if "autoplay" in names:
            mine = wait(lambda: item("My Media Board")["state"] == "loaded" and item("My Media Board"), 30)
            other, tv = item("Media Board"), item("Media TV")
            check("autoplay", mine and other["state"] == "waiting to be started" and tv["state"] == "waiting to be started",
                  f"mine {mine and mine['state']}, others {other['state']} / {tv['state']} (yours only, by default)")

        board = item("My Media Board")["key"]
        if "read" in names:
            r = api("read", media=board)
            check("read", r["title"] == "Sojourn media test" and "Press me" in r["buttons"] and any(f["label"] == "type here" for f in r["fields"])
                  and not any(f.get("filled") for f in r["fields"]), f"title {r['title']!r}, buttons {r['buttons']}, fields {r['fields']}")

        if "press" in names:
            r = api("press", media=board, words="press me")
            st = wait(lambda: "clicks 1" in page_state("My Media Board") and page_state("My Media Board"), 5)
            check("press", r.get("pressed") == "Press me" and st, f"{r} -> {st}")

        if "type" in names:
            r = api("type", media=board, field="type here", text="hello sojourn")
            st = wait(lambda: "typed [hello sojourn]" in page_state("My Media Board") and page_state("My Media Board"), 5)
            check("type", st, f"{r} -> {st}")

        if "password" in names:
            try:
                api("type", media=board, field="password", text="hunter2")
                check("password", False, "typed into a password field")
            except RuntimeError as e:
                check("password", "password" in str(e), str(e))

        if "scroll" in names:
            r = api("scroll", media=board, screens=1)
            r2 = api("scroll", media=board, to="top")
            check("scroll", r.get("scroll", 0) > 0 and r2.get("scroll") == 0, f"down {r}, top {r2}")

        if "look" in names:
            r = api("look", media=board)
            from PIL import Image
            import io
            img = np.asarray(Image.open(io.BytesIO(base64.b64decode(r["png"]))).convert("RGB")).astype(int)
            h, w = img.shape[:2]
            corners = {"top left red": img[8, 8], "top right blue": img[8, w - 28], "bottom left green": img[h - 8, 8], "bottom right purple": img[h - 8, w - 28]}
            want = {"top left red": (0xd1, 0x34, 0x2f), "top right blue": (0x2f, 0x8a, 0xd1), "bottom left green": (0x3f, 0xae, 0x4f), "bottom right purple": (0x9b, 0x3f, 0xd1)}
            bad = [k for k, v in corners.items() if np.abs(v - want[k]).max() > 30]
            check("look", (w, h) == (1024, 512) and not bad, f"{w}x{h}, corners off: {bad or 'none'}")

        if "face" in names:
            # the page on its face, the right way up: the corners' colours where the face's corners project
            s = status("My Media Board")
            img = shot()
            cam = api("getCamera", pump="LLAgent")
            got = {}
            for k, (px, py, col) in {"top left": (30, 30, (0xd1, 0x34, 0x2f)), "bottom right": (994, 482, (0x9b, 0x3f, 0xd1))}.items():
                x, y = project(cam, face_point(s["face_frame"], 1024, 512, px, py), W, H)
                c = img[int(round(y)), int(round(x))] if img is not None and 0 <= x < W and 0 <= y < H else None
                got[k] = (round(x), round(y), None if c is None else tuple(int(v) for v in c), c is not None and np.abs(c - np.array(col)).max() < 70)
            check("face", all(v[3] for v in got.values()), f"{got}" + ("" if all(v[3] for v in got.values()) else f"; camera {cam}, face {s.get('face_frame')}"))

        if "click" in names:
            # the person's own click, in the window, on the page's button
            api("scroll", media=board, to="top")
            before = page_state("My Media Board")
            s = status("My Media Board")
            cam = api("getCamera", pump="LLAgent")
            x, y = project(cam, face_point(s["face_frame"], 1024, 512, 115, 155), W, H)
            key(27)   # Esc: the mouse free of looking about (first person), so the pointer follows it
            time.sleep(0.5)
            click(x, y)
            n0 = int(before.split()[1])
            st = wait(lambda: f"clicks {n0 + 1}" in page_state("My Media Board") and page_state("My Media Board"), 5)
            check("click", st, f"at {x:.0f},{y:.0f}: {before} -> {st or page_state('My Media Board')}")

        if "settings" in names:
            # batch B10: a face's media settings read, and changes refused on an object the person can't modify
            mine = api("settings", media=board)
            other = api("settings", media=item("Media Board")["key"])
            try:
                api("configure", media=item("Media Board")["key"], auto_loop=True)
                refused = None
            except RuntimeError as e:
                refused = str(e)
            check("settings", mine.get("has_media") and mine.get("can_edit") and mine.get("first_click_interact") and mine.get("width") == 1024
                  and other.get("auto_zoom") and not other.get("can_edit") and refused and "modify" in refused,
                  f"mine {json.dumps({k: mine.get(k) for k in ('can_edit', 'first_click_interact', 'width', 'perms_control')})}, "
                  f"other {json.dumps({k: other.get(k) for k in ('can_edit', 'auto_zoom', 'whitelist')})}, configure: {refused}")

        if "autoscale" in names:
            # batch B10: configure on the person's own face (no confirmation: nobody's permissions change): 1000x500,
            # then auto-scale lays the page out at the power-of-two size it's drawn at
            r = api("configure", media=board, width=1000, height=500, auto_scale=False)
            plain = wait(lambda: (lambda l: (l["width"], l["height"]) == (1000, 500) and l)(api("look", media=board)), 20, 1)
            api("configure", media=board, auto_scale=True)
            scaled = wait(lambda: (lambda l: (l["width"], l["height"]) == (1024, 512) and l)(api("look", media=board)), 20, 1)
            say("myboard reset")
            wait(lambda: api("settings", media=board).get("width") == 1024 and not api("settings", media=board).get("auto_scale"), 15, 1)
            check("autoscale", r.get("confirmed_by_region") and plain and scaled,
                  f"region {r.get('confirmed_by_region')}, 1000x500 drawn as {plain and (plain['width'], plain['height'])}, "
                  f"auto-scaled {scaled and (scaled['width'], scaled['height'])}")

        if "focus" in names:
            # batch B10: Media Board (the land owner's, no first click flag, auto zoom): the person's first click only
            # focuses it and brings it up close (no mouse down reaches the page: its "at" stays); once focused, a
            # click goes in. In third person, facing a little right of the board so the avatar doesn't hide it, and
            # clicking its right half (trees stand by its left edge)
            other = item("Media Board")["key"]
            api("play", media=other)
            wait(lambda: status("Media Board").get("title") == "Sojourn media test", 15)
            api("scroll", media=other, to="top")
            api("set", pump="SojournSettings", camera="third_person")
            frame = status("Media Board")["face_frame"]
            api("lookAt", pump="LLAgent", position=list(face_point(frame, 1024, 512, 115, 155) + np.array([2.5, 0, 0])))
            time.sleep(2)
            cam = api("getCamera", pump="LLAgent")
            before = page_state("Media Board")
            # a point of the board the pointer really lands on (trees and the avatar stand about): projected
            # candidates, each checked with list's under_pointer before the click
            x = y = None
            for px, py in [(800, 300), (600, 250), (900, 120), (700, 420), (450, 300), (300, 400), (115, 155)]:
                cx, cy = project(cam, face_point(frame, 1024, 512, px, py), W, H)
                if not (0 <= cx < W and 0 <= cy < H):
                    continue
                subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winmouse.ps1"), "-X", str(int(cx)), "-Y",
                                str(int(cy)), "-ProcessId", str(proc.pid)], capture_output=True)
                if wait(lambda: api("list").get("under_pointer") == other, 1.5, 0.2):
                    x, y = cx, cy
                    break
            if x is None:
                check("focus", False, f"no point of Media Board under the pointer (camera {cam})")
                x, y = W / 2, H / 2
            click(x, y)
            up = wait(lambda: api("list").get("up_close") == other, 5)
            first = page_state("Media Board")
            key(27)   # the up-close view put away; the face stays focused
            wait(lambda: not api("list").get("up_close"), 5)
            time.sleep(0.5)
            click(x, y)
            second = wait(lambda: " at -1,-1" not in page_state("Media Board") and page_state("Media Board"), 5)
            check("focus", up and first == before and " at -1,-1" in before and second,
                  f"at {x:.0f},{y:.0f}: first click: up close {bool(up)}, page {first}; second: {second or page_state('Media Board')}")
            api("stop", media=other)
            api("set", pump="SojournSettings", camera="first_person")
            api("lookAt", pump="LLAgent", position=[SPOT[0] - 2, SPOT[1], SPOT[2]])
            time.sleep(1)

        if "navigate" in names:
            api("go", media=board, url=f"{SITE_URL}/page2.html")
            t = wait(lambda: (status("My Media Board").get("title") or "").startswith("Page two") and status("My Media Board")["title"], 8)
            # sent to the region for everyone: its stored media has the new address
            stored = wait(lambda: (grid.console("backup", wait=1), "page2.html" in (prim_media("My Media Board") or ""))[1] and "stored", 15)
            api("back", media=board)
            b = wait(lambda: status("My Media Board").get("title") == "Sojourn media test", 8)
            check("navigate", t and stored and b, f"went: {t}, region: {stored}, back: {bool(b)}")

        if "region" in names:
            # the region's own change (a script) is followed, not sent back
            say(f"myboard url {SITE_URL}/big.html")
            t = wait(lambda: status("My Media Board").get("title") == "A long page" and "followed", 12)
            say("myboard reset")
            wait(lambda: status("My Media Board").get("title") == "Sojourn media test", 12)
            check("region", t, f"{t or status('My Media Board').get('title')}")

        if "whitelist" in names:
            other = item("Media Board")["key"]
            api("play", media=other)
            loaded = wait(lambda: status("Media Board").get("title") == "Sojourn media test", 15)
            try:
                api("go", media=other, url="http://example.com/")
                refused = None
            except RuntimeError as e:
                refused = str(e)
            api("press", media=other, words="Leave")   # a link off the whitelist: the page stays
            blocked = wait(lambda: "whitelist" in (status("Media Board").get("refused") or "") and status("Media Board")["refused"], 6)
            check("whitelist", loaded and refused and blocked, f"loaded {bool(loaded)}, go: {refused}, link: {blocked}")

        if "video" in names:
            tv = item("Media TV")["key"]
            api("play", media=tv)
            p = wait(lambda: status("Media TV").get("state") == "playing" and status("Media TV"), 15)
            f0 = p and p["frames"]
            time.sleep(2)
            f1 = status("Media TV")["frames"]
            api("pause", media=tv)
            paused = wait(lambda: status("Media TV").get("state") == "paused", 5)
            api("seek", media=tv, seconds=12)
            api("play", media=tv)
            t = wait(lambda: status("Media TV").get("time", 0) >= 11.5 and status("Media TV")["time"], 6)
            look = api("look", media=tv)
            check("video", p and f1 > f0 and paused and t and (look["width"], look["height"]) == (1280, 720),
                  f"frames {f0} -> {f1} in 2 s, paused {bool(paused)}, seek -> {t}, picture {look['width']}x{look['height']}")

        if "sound" in names:
            # the TV's tones (440 Hz left, 660 Hz right) in what the client plays, gone at media volume 0
            tv = item("Media TV")["key"]
            api("play", media=tv)
            t0 = api("status", pump="SojournAudio")["engine"]["render_file_t0"] / 1000
            time.sleep(3)
            on = (time.time() - t0 - 2.5, time.time() - t0)
            api("set", pump="SojournSettings", media_volume=0)
            time.sleep(1.5)
            off = (time.time() - t0 - 1, time.time() - t0)
            api("set", pump="SojournSettings", media_volume=1)
            stats = api("stats")
            rate, x = audio.read_wav(heard)

            def band(span, f):
                a, b = int(span[0] * rate), int(span[1] * rate)
                seg = x[a:b].mean(axis=1) if b > a else np.zeros(1)
                spec = np.abs(np.fft.rfft(seg * np.hanning(len(seg))))
                k = int(round(f * len(seg) / rate))
                return float(spec[max(0, k - 2):k + 3].max() / (np.median(spec) + 1e-9))
            check("sound", band(on, 440) > 30 and band(on, 660) > 30 and band(off, 440) < 10,
                  f"440 Hz x{band(on, 440):.0f}, 660 Hz x{band(on, 660):.0f} over the floor; at volume 0 x{band(off, 440):.0f}; mixer {stats['mixer']}")

        if "parcel" in names:
            say(f"screen on {SITE_URL}/big.html text/html")
            p = wait(lambda: item("parcel"), 10)
            waiting = p and p["state"] == "waiting to be started"
            api("set", pump="SojournSettings", media_autoplay="all")
            played = wait(lambda: item("parcel")["state"] == "loaded" and item("parcel"), 15)
            api("set", pump="SojournSettings", media_autoplay="mine")
            say("screen off")
            gone = wait(lambda: item("parcel") is None, 10)
            check("parcel", waiting and played and played.get("title") == "A long page" and gone,
                  f"came: {p and p['state']}, with autoplay all: {played and played.get('title')}, off: {bool(gone)}")

        if "zoom" in names:
            api("zoom", media=board)
            time.sleep(1.5)
            img = shot()
            # the page's header colour across the middle of the window: the sheet is up
            band_ = img[int(H * 0.1):int(H * 0.5), int(W * 0.2):int(W * 0.8)] if img is not None else np.zeros((1, 1, 3))
            header = (np.abs(band_ - np.array((0x1d, 0x6f, 0x8c))).max(axis=2) < 25).mean()
            key(27)   # Esc puts it away
            time.sleep(1)
            down = not api("list").get("up_close")
            check("zoom", header > 0.05 and down, f"header colour on {header:.0%} of the middle; Esc put it away: {down}")

        if "offer" in names:
            # llLoadURL: a notice; "Go to page" waits for the person's press on the confirm key, then the page opens up close
            say(f"screen offer {SITE_URL}/page.html")
            note = wait(lambda: next((n for n in api("listChannelNotifications", pump="LLNotifications", channel="Visible").get("notifications", []) if n.get("name") == "LoadWebPage"), None), 10)
            import threading
            answered = {}
            def respond():
                try:
                    answered["r"] = api("respond", pump="LLNotifications", timeout=60, uuid=note["id"], response={"Gotopage": True})
                except Exception as e:
                    answered["r"] = str(e)
            th = threading.Thread(target=respond)
            if note:
                th.start()
            time.sleep(2)   # the confirm key arms after 0.8 s on screen
            img = shot()
            # the confirm key: the panel's accent, solid
            mask = (np.abs(img - np.array((0x23, 0x7c, 0x76))).max(axis=2) < 14) if img is not None else np.zeros((1, 1), bool)
            mask[:, :W // 2] = False   # the panel is at the top right
            ys, xs = np.nonzero(mask)
            if len(xs) > 50:
                key(27)   # a cursor rather than looking about (first person)
                time.sleep(0.4)
                click(xs.mean(), ys.mean())
            th.join(20) if note else None
            page = wait(lambda: item("page") or next((m for m in api("list")["media"] if m["kind"] == "page"), None), 10)
            up = api("list").get("up_close")
            api("zoom", on=False)
            check("offer", note and page and up == "page", f"notice {bool(note)}, key found {len(xs) > 50}, answer {answered.get('r')}, page {page and page.get('url')}, up close {up}")

        if "stop" in names:
            api("stop", media=board)
            s = wait(lambda: item("My Media Board")["state"] == "stopped" and "stopped", 5)
            api("play", media=board)
            back = wait(lambda: item("My Media Board")["state"] == "loaded" and "loaded", 15)
            check("stop", s and back, f"{s}, then {back}")
        log_lines = (PILOT / "console.txt").read_text(encoding="utf-8", errors="replace").splitlines()
        errors = [l for l in log_lines if "media" in l and ("Exception" in l or "copy:" in l or "error" in l.lower())]
        check("log", not errors, "; ".join(errors[:5]) or "no media errors")
    finally:
        try:
            say("screen off")
        except Exception:
            pass
        proc.terminate()
        try:
            proc.wait(15)
        except subprocess.TimeoutExpired:
            proc.kill()
        srv.shutdown()
    print("all passed" if ok else "some failed")
    return 0 if ok else 1


def prim_media(name):
    """A fixture's media as the region stores it."""
    import sqlite3
    import grid
    c = sqlite3.connect(f"file:{grid.BIN / 'OpenSim.db'}?mode=ro", uri=True)
    try:
        r = c.execute("select s.Media from prims p join primshapes s on s.UUID = p.UUID where p.Name = ?", (name,)).fetchone()
        return r and r[0]
    finally:
        c.close()


# ---- the headset: the left hand and the page board in Meta's XR Simulator, and what media costs a frame ------------

def start_client(base, port, vr, minutes, env_extra=None, args_extra=(), stand=None):
    import shutil
    for _ in range(10):   # (the last client's files may take a moment to let go)
        shutil.rmtree(base, ignore_errors=True)
        if not base.exists():
            break
        time.sleep(1)
    base.mkdir(parents=True, exist_ok=True)
    (base / "vr-settings.json").write_text(json.dumps({"confirmTouches": False, "panelHidden": True}))
    env = dict(os.environ, **(env_extra or {}))
    args = ["--no-assistant", "--no-voice", "--first", "Media", "--last", "Pilot", "--password", "sojourn-local",
            "--bridge-port", str(port), "--base-dir", str(base), "--start", f"uri:Atelier&{(stand or STAND)[0]:.0f}&{(stand or STAND)[1]:.0f}&31",
            "--minutes", str(minutes), "--sky", "noon", *args_extra]
    if vr:
        sims = sorted((ROOT / "local" / "xrsim").glob("*/meta_openxr_simulator.json"))
        if not sims:
            sys.exit("no Meta XR Simulator in local/xrsim (docs/vr.md)")
        env["XR_RUNTIME_JSON"] = str(sims[-1])
        args = ["--vr", "--xr-wait", "90", "--shadow-cap", "1536", *args]
    else:
        args = ["--window", "960x540", "--fps", "30", *args]
    dotnet = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
    client = Path(os.environ.get("SOJOURN_CLIENT_DLL") or ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll")   # SOJOURN_CLIENT_DLL: built elsewhere, not rebuilt
    log = open(base / "console.txt", "w", encoding="utf-8")
    return subprocess.Popen([str(dotnet), str(client), *args], cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=log,
                            stderr=subprocess.STDOUT, text=True, creationflags=0x00004000)


def logged_in(port, secs):
    import urllib.request
    end = time.time() + secs
    while time.time() < end:
        try:
            if json.loads(urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=3).read()).get("connected"):
                return True
        except OSError:
            pass
        time.sleep(1)
    return False


def headset():
    """SOJOURN_VR_SCRIPT=media in the simulator (Xr/VrScript.Media.cs): a tap and a drag on My Media Board with the
    left hand, a double tap bringing it up close, a tap on the board with the right, ×."""
    global API_PORT
    if not os.environ.get("SOJOURN_CLIENT_DLL") and subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"], capture_output=True).returncode:
        print("the client didn't build")
        return 1
    if fixtures():
        return 1
    srv = start_server()
    base = MEDIA / "headset"
    API_PORT = 9735
    proc = start_client(base, API_PORT, vr=True, minutes=4, env_extra={"SOJOURN_VR_SCRIPT": "media", "SOJOURN_MEDIA_DEBUG": "1"})
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)

    def state():
        key = next(m["key"] for m in api("list")["media"] if m.get("name") == "My Media Board")
        t = api("read", media=key)["text"]
        return t[t.find("clicks"):].split("\n")[0]

    try:
        if not logged_in(API_PORT, 150):
            check("logged in", False, "no login in 150 s")
            return 1
        say("myboard reset")
        before = None
        end = time.time() + 30
        while time.time() < end and before is None:
            try:
                before = state()
            except Exception:
                time.sleep(1)
        # the script starts 5 s after arriving and runs 12 s
        log = base / "console.txt"
        end = time.time() + 60
        while time.time() < end and "media script" not in log.read_text(encoding="utf-8", errors="replace") or \
                time.time() < end and "done" not in [l.split(": ")[-1] for l in log.read_text(encoding="utf-8", errors="replace").splitlines() if "media script" in l]:
            time.sleep(1)
        time.sleep(1)
        after = state()
        print(f"     page before: {before}; after: {after}")
        lines = [l for l in log.read_text(encoding="utf-8", errors="replace").splitlines() if "media" in l]
        n0, n1 = int(before.split()[1]), int(after.split()[1])
        page = int(after.split("page ")[1].split()[0])
        up = any("up close" in l for l in lines)
        down = not api("list").get("up_close")
        check("headset", n1 - n0 == 2 and page > 0 and up and down,
              f"clicks {n0} -> {n1} (a tap on the face, one on the board), page scrolled to {page}, up close {up}, put away {down}")
        errors = [l for l in lines if "failed" in l or "Exception" in l]
        check("log", not errors, "; ".join(errors[:3]) or "no media errors")
    finally:
        proc.terminate()
        try:
            proc.wait(20)
        except subprocess.TimeoutExpired:
            proc.kill()
        srv.shutdown()
    return 0 if ok else 1


def perf(argv):
    """What media costs a headset frame, in the simulator: the same 25 s standing before the fixtures with media off,
    then with every fixture playing (two pages, the video, the parcel's page) in view; CPU and GPU ms a frame, and the
    helpers' own CPU."""
    import numpy as np
    import psutil
    global API_PORT
    if not os.environ.get("SOJOURN_CLIENT_DLL") and subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"], capture_output=True).returncode:
        print("the client didn't build")
        return 1
    if fixtures():
        return 1
    srv = start_server()
    API_PORT = 9736
    results = {}
    try:
        for label, on, zoom in (("media off", False, False), ("all playing", True, False), ("the video up close too", True, True)):
            base = MEDIA / "perf"
            frames = base / "frames.csv"
            # south-west of the row, facing east as one arrives: the four faces in view, at an angle
            proc = start_client(base, API_PORT, vr=True, minutes=3, env_extra={"SOJOURN_FRAME_LOG": str(frames)},
                                args_extra=() if on else ("--no-media",), stand=(114, 226))
            try:
                if not logged_in(API_PORT, 150):
                    print(f"{label}: no login")
                    return 1
                if on:
                    say("screen on " + SITE_URL + "/big.html text/html")
                    api("set", pump="SojournSettings", media_autoplay="all")
                    time.sleep(8)
                    for m in api("list")["media"]:
                        api("play", media=m["key"])
                    if zoom:
                        api("zoom", media="Media TV")
                time.sleep(15)
                t0 = time.time()
                helpers = [p for p in psutil.Process(proc.pid).children(recursive=True)]
                for p in helpers:
                    try:
                        p.cpu_percent(None)
                    except psutil.Error:
                        pass
                time.sleep(25)
                hcpu = 0.0
                for p in helpers:
                    try:
                        hcpu += p.cpu_percent(None)
                    except psutil.Error:
                        pass
                playing = [m.get("name") or m["key"] for m in api("list")["media"] if m["state"] in ("loaded", "playing")] if on else []
                stats = api("stats") if on else None
            finally:
                if on:
                    try:
                        say("screen off")
                        api("set", pump="SojournSettings", media_autoplay="mine")
                    except Exception:
                        pass
                proc.terminate()
                try:
                    proc.wait(20)
                except subprocess.TimeoutExpired:
                    proc.kill()
            rows = [l.split(",") for l in frames.read_text().splitlines() if l and l[0].isdigit()]
            first = float(rows[0][0])
            t_rel = t0 - (time.time() - 0)   # (the log's clock is the client's; the last 25 s are taken from its end)
            last = float(rows[-1][0])
            win = [r for r in rows if float(r[0]) >= last - 25]
            cpu = np.array([float(r[2]) for r in win])
            dt = np.array([float(r[1]) for r in win])
            gpu = [float(l.split("gpu ")[1].split(" ms")[0]) for l in (base / "console.txt").read_text(encoding="utf-8", errors="replace").splitlines()
                   if " VR " in l and "gpu " in l][-2:]
            results[label] = dict(cpu=np.median(cpu), cpu95=np.percentile(cpu, 95), dt=np.median(dt), late=float((dt > 1000 / 72 * 1.5).mean()),
                                  gpu=np.mean(gpu) if gpu else float("nan"), helpers=hcpu, playing=playing, stats=stats)
            r = results[label]
            print(f"{label}: cpu {r['cpu']:.2f} ms median ({r['cpu95']:.2f} p95), frame {r['dt']:.1f} ms, {r['late']:.1%} late, gpu {r['gpu']:.1f} ms, "
                  f"helpers {r['helpers']:.0f}% of a core; playing {r['playing']}", flush=True)
            if stats:
                print("    tier, scale, frames:", [(p["tier"], p["scale"], p["frames"]) for p in stats["players"]])
    finally:
        srv.shutdown()
    return 0
