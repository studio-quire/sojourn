"""Sojourn's world audio on the local grid: its fixtures and tests (docs/audio.md).

  python tools/audio.py fixtures       the Sound Box, loaded into Atelier once (an OAR made here: a phantom prim with
                                       test sounds and a script driven over chat channel 7; silent until told)
  python tools/audio.py serve          the test radio on 127.0.0.1:9751, an ICY server like Shoutcast and Icecast:
                                       /mp3 (answers "ICY 200 OK"), /aac (ADTS), /ogg (chained Vorbis), /pls (a
                                       playlist of /mp3), /redirect (to /aac); each with its own StreamTitle
  python tools/audio.py test [NAME..]  a client (Audio Pilot, --no-voice) records what it hears through the engine's
                                       test switch (SOJOURN_VOICE_OUT_FILE) while the box and the radio are driven, and
                                       the recording is checked: balance as the listener turns, distance, gain, cutoff,
                                       stop, trigger, queue, the 30-sound cap, music (three formats, the title, the
                                       volume, switching, off), a sound played by the agent, footsteps, the teleport
                                       sound, and the mixer's CPU. Names: balance distance gain cutoff stop trigger queue
                                       cap music self steps teleport (default: all)
"""
import json
import math
import os
import socketserver
import sqlite3
import subprocess
import sys
import tarfile
import threading
import time
import urllib.request
import uuid
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
OUT = ROOT / "local" / "audio"
ASSETS = OUT / "assets"
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
CLIENT_ROOT = Path(os.environ.get("SOJOURN_CLIENT_ROOT") or ROOT)
CLIENT = CLIENT_ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "Sojourn.dll"
BELOW_NORMAL = 0x00004000
PORT = 9732
RADIO = 9751
NS = uuid.UUID("0c4a7f3e-2b9d-4c61-8e05-7d3a9b1f6e42")
BOX_NAME = "Sound Box"
BOX_VERSION = "tools/audio.py box 4"   # in its description: an older box is replaced
HOME = (150.0, 240.0)   # Atelier's flat north lawn (z 30, above the water), away from the voice tests' spot (128, 118)
SOUNDS = ["noise", "beep", "tone_a", "tone_b"]
FFMPEG = "ffmpeg"


def uid(*parts):
    return str(uuid.uuid5(NS, "/".join(map(str, parts))))


# ---- test sounds ----------------------------------------------------------------------------------------------------

def write_wav(path, x, rate):
    """x: float array, (n,) mono or (n, 2) stereo."""
    x = np.clip(np.asarray(x, dtype=np.float64), -1, 1)
    ch = 1 if x.ndim == 1 else x.shape[1]
    data = (x * 32767).round().astype("<i2").tobytes()
    with open(path, "wb") as f:
        f.write(b"RIFF" + (36 + len(data)).to_bytes(4, "little") + b"WAVEfmt " + (16).to_bytes(4, "little")
                + (1).to_bytes(2, "little") + ch.to_bytes(2, "little") + rate.to_bytes(4, "little")
                + (rate * 2 * ch).to_bytes(4, "little") + (2 * ch).to_bytes(2, "little") + (16).to_bytes(2, "little")
                + b"data" + len(data).to_bytes(4, "little") + data)


def read_wav(path):
    raw = Path(path).read_bytes()
    pos, rate, ch, data = 12, 0, 0, b""
    while pos + 8 <= len(raw):
        tag, n = raw[pos:pos + 4], int.from_bytes(raw[pos + 4:pos + 8], "little")
        if tag == b"fmt ":
            ch, rate = int.from_bytes(raw[pos + 10:pos + 12], "little"), int.from_bytes(raw[pos + 12:pos + 16], "little")
        elif tag == b"data":
            data = raw[pos + 8:pos + 8 + n]
        pos += 8 + n + (n & 1)
    x = np.frombuffer(data[:len(data) // (2 * ch) * 2 * ch], dtype="<i2").astype(np.float64) / 32768
    return rate, x.reshape(-1, ch)


def tone(f, secs, rate=44100, level=0.3):
    t = np.arange(int(secs * rate)) / rate
    env = np.minimum(1, np.minimum(t, secs - t) / 0.005)
    return level * env * np.sin(2 * np.pi * f * t)


def band_noise(secs, lo, hi, rate=44100, rms=0.2, seed=1):
    """Noise shaped in the frequency domain: periodic, so it loops without a seam."""
    n = int(secs * rate)
    rng = np.random.default_rng(seed)
    spec = np.zeros(n // 2 + 1, complex)
    f = np.fft.rfftfreq(n, 1 / rate)
    band = (f >= lo) & (f <= hi)
    spec[band] = np.exp(2j * np.pi * rng.random(band.sum())) / np.sqrt(np.maximum(f[band], 1) / lo)   # pinkish
    x = np.fft.irfft(spec, n)
    return x * rms / np.sqrt(np.mean(x * x))


def ffmpeg(*args):
    r = subprocess.run([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", *map(str, args)], capture_output=True, text=True)
    if r.returncode:
        raise RuntimeError("ffmpeg: " + r.stderr)


def make_assets():
    ASSETS.mkdir(parents=True, exist_ok=True)
    clips = {"noise": band_noise(2.0, 200, 8000), "beep": tone(1000, 0.25, level=0.35),
             "tone_a": tone(600, 0.6), "tone_b": tone(1500, 0.6)}
    for name, x in clips.items():
        ogg = ASSETS / f"{name}.ogg"
        if not ogg.exists():
            write_wav(ASSETS / f"{name}.wav", x, 44100)
            ffmpeg("-i", ASSETS / f"{name}.wav", "-ac", "1", "-c:a", "libvorbis", "-q:a", "5", ogg)
    # the radio's programme: 20 s of a chord (330, 415, 494 Hz) with a little noise, stereo
    src = ASSETS / "music.wav"
    if not src.exists():
        t = np.arange(20 * 44100) / 44100
        chord = sum(np.sin(2 * np.pi * f * t) for f in (330, 415, 494)) * 0.12
        x = np.stack([chord + band_noise(20, 100, 6000, rms=0.02, seed=2), chord + band_noise(20, 100, 6000, rms=0.02, seed=3)], 1)
        write_wav(src, x, 44100)
    for fmt, args in {"mp3": ["-c:a", "libmp3lame", "-b:a", "128k", "-f", "mp3"],
                      "aac": ["-c:a", "aac", "-b:a", "96k", "-f", "adts"],
                      "ogg": ["-c:a", "libvorbis", "-q:a", "4", "-f", "ogg"]}.items():
        p = ASSETS / f"music.{fmt}"
        if not p.exists():
            ffmpeg("-i", src, *args, p)


# ---- the Sound Box ---------------------------------------------------------------------------------------------------

SCRIPT = """// Sound Box (tools/audio.py): silent until told, over chat channel 7
default
{
    state_entry() { llStopSound(); llListen(7, "", NULL_KEY, ""); }
    on_rez(integer p) { llResetScript(); }
    listen(integer ch, string name, key id, string msg)
    {
        list a = llParseString2List(msg, [" "], []);
        string c = llList2String(a, 0);
        string s = llList2String(a, 1);
        float g = (float)llList2String(a, 2);
        if (c == "loop") llLoopSound(s, g);
        else if (c == "play") llPlaySound(s, g);
        else if (c == "trigger") llTriggerSound(s, g);
        else if (c == "stop") llStopSound();
        else if (c == "vol") llAdjustSoundVolume((float)s);
        else if (c == "radius") llSetSoundRadius((float)s);
        else if (c == "queue") llSetSoundQueueing((integer)s);
        else if (c == "pos") llSetRegionPos(<(float)s, g, (float)llList2String(a, 3)>);
        else if (c == "burst") { integer n = (integer)llList2String(a, 2); integer i; for (i = 0; i < n; i++) llTriggerSound(s, 0.5); }
        else if (c == "music") llSetParcelMusicURL(s);
        else if (c == "nomusic") llSetParcelMusicURL("");
        llOwnerSay("done " + msg);
    }
}
"""


def task_item(item, asset, name, inv_type, part_id, owner):
    z = "00000000-0000-0000-0000-000000000000"
    def u(x): return f"<UUID>{x}</UUID>"
    return (f"<TaskInventoryItem><AssetID>{u(asset)}</AssetID><BasePermissions>2147483647</BasePermissions>"
            f"<CreationDate>1792141642</CreationDate><CreatorID>{u(owner)}</CreatorID><Description /><EveryonePermissions>0</EveryonePermissions>"
            f"<Flags>0</Flags><GroupID>{u(z)}</GroupID><GroupPermissions>0</GroupPermissions><InvType>{inv_type}</InvType>"
            f"<ItemID>{u(item)}</ItemID><OldItemID>{u(z)}</OldItemID><LastOwnerID>{u(owner)}</LastOwnerID><Name>{name}</Name>"
            f"<NextPermissions>2147483647</NextPermissions><OwnerID>{u(owner)}</OwnerID><CurrentPermissions>2147483647</CurrentPermissions>"
            f"<ParentID>{u(part_id)}</ParentID><ParentPartID>{u(part_id)}</ParentPartID><PermsGranter>{u(z)}</PermsGranter>"
            f"<PermsMask>0</PermsMask><Type>{inv_type}</Type><OwnerChanged>false</OwnerChanged></TaskInventoryItem>")


def db():
    import grid
    return sqlite3.connect(f"file:{grid.BIN / 'OpenSim.db'}?mode=ro", uri=True)


def land_owner():
    """Atelier's parcel owner: the box is theirs, so its script may set the parcel's music (llSetParcelMusicURL)."""
    import grid
    ini = (grid.BIN / "Regions" / "Regions.ini").read_text()
    region = ini.split("[Atelier]", 1)[1].split("RegionUUID", 1)[1].split("=", 1)[1].split()[0].strip()
    c = db()
    try:
        return c.execute("select OwnerUUID from land where RegionUUID=?", (region,)).fetchone()[0]
    finally:
        c.close()


def build_oar(owner):
    import forest
    make_assets()
    part_id = uid("box")
    items = [task_item(uid("item", n), uid("asset", n), n, 1, part_id, owner) for n in SOUNDS]
    items.append(task_item(uid("item", "script"), uid("asset", "script"), "controller", 10, part_id, owner))
    te = forest.te_bytes("89556747-24cb-43ed-920b-47caed15465f", color=(0.9, 0.6, 0.2, 1))
    xml = forest.part(part_id, 1, BOX_NAME, (HOME[0] + 4, HOME[1], 32.0), (0, 0, 0, 1), (0.5, 0.5, 0.5), te)
    xml = xml.replace(forest.OWNER, owner).replace("<Description />", f"<Description>{BOX_VERSION}</Description>")
    xml = xml.replace("<InventorySerial>0</InventorySerial>", "<InventorySerial>1</InventorySerial>")
    xml = xml.replace("</SceneObjectPart>", "<TaskInventory>" + "".join(items) + "</TaskInventory></SceneObjectPart>")
    path = OUT / "soundbox.oar"
    with tarfile.open(path, "w:gz") as tar:
        forest.add(tar, "archive.xml", forest.archive_xml())
        forest.add(tar, f"objects/{BOX_NAME.replace(' ', '_')}__{part_id}.xml", forest.group([xml]).encode())
        for n in SOUNDS:
            forest.add(tar, f"assets/{uid('asset', n)}_sound.ogg", (ASSETS / f"{n}.ogg").read_bytes())
        forest.add(tar, f"assets/{uid('asset', 'script')}_script.lsl", SCRIPT.encode())
    return path


def box_owners():
    c = db()
    try:
        return [(r[0], r[1]) for r in c.execute("select OwnerID, Description from prims where Name=?", (BOX_NAME,))]
    finally:
        c.close()


def box_loaded():
    return bool(box_owners())


def fixtures():
    import grid
    if not grid.start():
        sys.exit("the grid didn't start")
    print(grid.account("Audio", "Pilot", "sojourn-local"))
    owner = land_owner()
    have = box_owners()
    if have == [(owner, BOX_VERSION)]:
        print("the Sound Box is there")
        return
    grid.console('change region "Atelier"', wait=1)
    if have:   # an older one, or one owned by someone else: replaced
        grid.console(f'delete object name "{BOX_NAME}"', wait=2, answers=("y",))
    path = build_oar(owner).resolve()
    print("\n".join(grid.console(f'load oar --merge "{path}"', wait=5)[-3:]))
    end = time.time() + 120
    while time.time() < end and not box_loaded():
        time.sleep(2)
    print("the Sound Box is loaded" if box_loaded() else "the Sound Box didn't load (local/grid/console.log)")


# ---- the test radio --------------------------------------------------------------------------------------------------

class Radio(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


class RadioHandler(socketserver.StreamRequestHandler):
    def handle(self):
        line = self.rfile.readline().decode("latin1").split()
        while self.rfile.readline() not in (b"\r\n", b"\n", b""):
            pass
        path = line[1] if len(line) > 1 else "/"
        host = f"127.0.0.1:{RADIO}"
        if path == "/pls":
            body = f"[playlist]\nNumberOfEntries=1\nFile1=http://{host}/mp3\nTitle1=Sojourn Test\nLength1=-1\nVersion=2\n".encode()
            self.wfile.write(b"HTTP/1.0 200 OK\r\nContent-Type: audio/x-scpls\r\nContent-Length: %d\r\n\r\n" % len(body) + body)
            return
        if path == "/redirect":
            self.wfile.write(f"HTTP/1.0 302 Found\r\nLocation: http://{host}/aac\r\n\r\n".encode())
            return
        fmt = path.strip("/")
        if fmt not in ("mp3", "aac", "ogg"):
            self.wfile.write(b"HTTP/1.0 404 Not Found\r\n\r\n")
            return
        data = (ASSETS / f"music.{fmt}").read_bytes()
        rate = len(data) / 20.0   # bytes per second: the file is 20 s
        metaint = 8192 if fmt != "ogg" else 0
        ctype = {"mp3": "audio/mpeg", "aac": "audio/aacp", "ogg": "application/ogg"}[fmt]
        status = b"ICY 200 OK\r\n" if fmt == "mp3" else b"HTTP/1.0 200 OK\r\n"
        head = status + f"Content-Type: {ctype}\r\nicy-name: Sojourn Test Radio\r\n".encode()
        if metaint:
            head += f"icy-metaint: {metaint}\r\n".encode()
        self.wfile.write(head + b"\r\n")
        title = f"StreamTitle='Sojourn Test - {fmt}';".encode()
        meta = bytes([(len(title) + 15) // 16]) + title.ljust((len(title) + 15) // 16 * 16, b"\0")
        pos, sent, since_meta, t0 = 0, 0, 0, time.time()
        burst = int(rate * 2)   # Icecast's burst on connect: ~2 s
        try:
            while True:
                n = 2048
                chunk = data[pos:pos + n] if pos + n <= len(data) else data[pos:] + data[:pos + n - len(data)]
                pos = (pos + n) % len(data)
                out = b""
                if metaint:
                    while chunk:
                        take = min(len(chunk), metaint - since_meta)
                        out += chunk[:take]
                        chunk = chunk[take:]
                        since_meta += take
                        if since_meta == metaint:
                            out += meta
                            since_meta = 0
                else:
                    out = chunk
                self.wfile.write(out)
                sent += n
                ahead = (sent - burst) / rate - (time.time() - t0)
                if ahead > 0:
                    time.sleep(ahead)
        except (ConnectionError, OSError):
            pass


def serve(background=False):
    make_assets()
    srv = Radio(("127.0.0.1", RADIO), RadioHandler)
    if background:
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        return srv
    print(f"the test radio on http://127.0.0.1:{RADIO}/ (mp3 aac ogg pls redirect)")
    srv.serve_forever()


# ---- the test --------------------------------------------------------------------------------------------------------

def api(pump, op, timeout=20, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 20) as r:
        out = json.loads(r.read())
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def box(cmd):
    api("LLChatBar", "sendChat", message=cmd, channel=7)


def test(names):
    import grid
    import psutil
    names = set(names or "balance distance gain cutoff stop trigger queue cap music self steps teleport".split())
    if CLIENT_ROOT == ROOT and subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"]).returncode:
        return 1
    fixtures()
    radio = serve(background=True)
    OUT.mkdir(parents=True, exist_ok=True)
    heard, log = OUT / "heard.wav", OUT / "pilot.log"
    if heard.exists():
        heard.unlink()
    env = dict(os.environ, SOJOURN_VOICE_OUT_FILE=str(heard))
    x0, y0 = HOME
    lf = open(log, "w", encoding="utf-8")
    proc = subprocess.Popen([str(DOTNET), str(CLIENT), "--window", "320x180", "--fps", "20", "--no-assistant", "--no-voice",
                             "--first", "Audio", "--last", "Pilot", "--password", "sojourn-local", "--bridge-port", str(PORT),
                             "--base-dir", str(OUT / "pilot"), "--start", f"uri:Atelier&{x0:.0f}&{y0:.0f}&40", "--minutes", "8"],
                            cwd=CLIENT_ROOT, env=env, stdin=subprocess.DEVNULL, stdout=lf, stderr=subprocess.STDOUT, text=True,
                            creationflags=BELOW_NORMAL)
    ok = True
    marks = {}

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)

    def status():
        return api("SojournAudio", "status")

    try:
        end = time.time() + 90
        while time.time() < end:
            try:
                if json.loads(urllib.request.urlopen(f"http://127.0.0.1:{PORT}/status", timeout=3).read()).get("connected"):
                    break
            except OSError:
                pass
            time.sleep(1)
        else:
            check("logged in", False, "no login in 90 s")
            return 1
        time.sleep(4)
        t0 = status()["engine"]["render_file_t0"] / 1000   # the recording's first sample, Unix seconds
        me = api("LLAgent", "getPosition")["region"]
        z = me[2]
        bx, by, bz = x0 + 4, y0, z
        box(f"pos {bx} {by} {bz}")
        box("stop")
        box("radius 0")
        box("vol 1")
        box("queue 0")
        box("nomusic")
        time.sleep(2)
        proc_ps = psutil.Process(proc.pid)

        def face(dx, dy):
            api("LLAgent", "lookAt", position=[me[0] + dx, me[1] + dy, me[2]])
            time.sleep(1.0)

        def cam():
            c = api("LLAgent", "getCamera")
            return c["camera_pos"]

        def window(name, secs):
            a = time.time()
            time.sleep(secs)
            marks[name] = (a - t0, time.time() - t0)

        # 1. a loop to the side, the listener turning
        face(0, 10)          # north: the box (east) on the right
        box("loop noise 1.0")
        time.sleep(3)        # the first fetch and decode
        cpu0 = proc_ps.cpu_times()
        window("north", 3)
        cpu1 = proc_ps.cpu_times()
        face(0, -10)         # south: on the left
        window("south", 3)
        face(10, 0)          # east: ahead
        cam_near = cam()
        window("east", 3)
        face(-10, 0)         # west: behind (the box moved back, to stay behind the camera, which swings round)
        c = cam()
        box(f"pos {c[0] + 7} {by} {bz}")
        time.sleep(1.5)
        window("west", 3)
        box(f"pos {bx} {by} {bz}")
        face(10, 0)
        time.sleep(0.5)
        # 2. distance: 4 m -> 16 m
        box(f"pos {bx + 12} {by} {bz}")
        time.sleep(1.5)
        cam_far = cam()
        window("far", 3)
        box(f"pos {bx} {by} {bz}")
        time.sleep(1.5)
        # 3. gain
        box("vol 0.25")
        time.sleep(1)
        window("quiet", 2.5)
        box("vol 1")
        time.sleep(1)
        # 4. cutoff: a 2 m radius from a camera several metres away. OpenSim sends a new radius with the object's
        # next sound (llSetSoundRadius alone sends nothing), so the loop is asked for again each time
        box("radius 2")
        box("loop noise 1.0")
        time.sleep(1.5)
        window("cut", 2)
        box("radius 0")
        box("loop noise 1.0")
        time.sleep(1.5)
        window("uncut", 2)
        # 5. stop
        box("stop")
        time.sleep(1)
        window("stopped", 2)
        # 6. a trigger
        a = time.time()
        box("trigger beep 1.0")
        time.sleep(2)
        marks["beep"] = (a - t0, time.time() - t0)
        # 7. queueing: tone_a then tone_b straight after. Both heard once first: a queued sound takes the place of
        # one still waiting for its asset (the viewer's rule too; scripts preload for this)
        box("trigger tone_a 0.01")
        box("trigger tone_b 0.01")
        time.sleep(1.5)
        box("queue 1")
        a = time.time()
        box("play tone_a 1.0")
        box("play tone_b 1.0")
        time.sleep(3)
        marks["queue"] = (a - t0, time.time() - t0)
        box("queue 0")
        # 8. the cap: 40 at once
        box("burst noise 40")
        time.sleep(0.5)
        st = status()
        capped = st["playing"], st["mixer"]["channels_active"]
        cpu2 = proc_ps.cpu_times()
        window("burst", 1)
        cpu3 = proc_ps.cpu_times()
        mix_us = status()["mixer"]["mix_us_recent"], st["mixer"]["mix_us_avg"]   # about the last second: 30 playing
        time.sleep(2)
        # 9. music: three formats, the title, the volume, a switch, off
        music = {}
        if "music" in names:
            for fmt, path in (("mp3", "/pls"), ("aac", "/redirect"), ("ogg", "/ogg")):
                a = time.time()
                box(f"music http://127.0.0.1:{RADIO}{path}")
                got = None
                while time.time() - a < 15:
                    m = status()["music"]
                    if m.get("state") == "playing" and m.get("title", fmt).endswith(fmt) and m.get("url", "").endswith(path):
                        got = m
                        break
                    time.sleep(0.5)
                music[fmt] = (time.time() - a, got)
                time.sleep(4)     # fully faded in (3 s)
                window(f"music_{fmt}", 2.5)
                if fmt == "mp3":
                    api("SojournSettings", "set", music_volume=0.5)
                    time.sleep(1)
                    window("music_half", 2.5)
                    api("SojournSettings", "set", music_volume=1)
            box("nomusic")
            time.sleep(3)
            window("music_off", 2)
            st_end = status()["music"]
        # 10. a sound the agent plays where it stands (the inventory's Play Inworld; gestures' sounds come back alike)
        a = time.time()
        api("TesselAgent", "playSound", sound_id=uid("asset", "beep"), gain=1.0)
        time.sleep(2)
        marks["self"] = (a - t0, time.time() - t0)
        # 11. footsteps: a walk on the ground
        face(-10, 0)
        a = time.time()
        api("TesselAgent", "move", forward=1, secs=3, wait=True)
        marks["steps"] = (a - t0, time.time() - t0)
        time.sleep(1)
        window("still", 2)
        # 12. the teleport sound
        a = time.time()
        tp = api("LLAgent", "requestTeleport", regionname="Atelier", x=x0, y=y0 - 6, z=z, skip_confirmation=True)
        time.sleep(4)
        tp_sources = api("SojournAudio", "sources")["sources"]
        marks["teleport"] = (a - t0, time.time() - t0)
        final = status()
    finally:
        try:
            proc.terminate()
            proc.wait(timeout=30)
        except Exception:
            proc.kill()
        lf.close()
        radio.shutdown()

    (OUT / "marks.json").write_text(json.dumps(marks, indent=1))
    rate, x = read_wav(heard)

    def seg(name, pad=0.0):
        a, b = marks[name]
        return x[int((a + pad) * rate):int(b * rate)]

    def db(v):
        return 20 * math.log10(max(math.sqrt(np.mean(v * v)) if len(v) else 0, 1e-7))

    def lr(name):
        s = seg(name)
        return db(s[:, 0]) - db(s[:, 1])

    def level(name, pad=0.0):
        return db(seg(name, pad))

    def band(s, f, w=60):
        m = s.mean(1) if s.ndim == 2 else s
        spec = np.abs(np.fft.rfft(m * np.hanning(len(m))))
        fr = np.fft.rfftfreq(len(m), 1 / rate)
        return 20 * math.log10(max(spec[(fr > f - w) & (fr < f + w)].max(), 1e-9) / len(m))

    def onset(s, f, w=100):
        """when the band around f first rises to a quarter of its peak (10 ms steps), if it rises above -90 dBFS"""
        m = s.mean(1)
        spec = np.fft.rfft(m)
        fr = np.fft.rfftfreq(len(m), 1 / rate)
        spec[(fr < f - w) | (fr > f + w)] = 0
        y = np.fft.irfft(spec, len(m))
        blk = rate // 100
        env = np.array([math.sqrt(np.mean(y[i:i + blk] ** 2)) for i in range(0, len(y) - blk, blk)])
        if len(env) == 0 or env.max() < 10 ** (-90 / 20):
            return None
        return int(np.argmax(env > env.max() / 4)) / 100

    def tilt(s):
        return band(s, 6000, 1500) - band(s, 1000, 300)

    if "balance" in names:
        n, s_, e, w = lr("north"), lr("south"), lr("east"), lr("west")
        check("a loop on the right as the listener faces north", n < -3, f"L-R {n:+.1f} dB")
        check("on the left facing south", s_ > 3, f"L-R {s_:+.1f} dB")
        check("centred facing it", abs(e) < 1.5, f"L-R {e:+.1f} dB")
        check("centred with it behind", abs(w) < 1.5, f"L-R {w:+.1f} dB")
        # behind: the head's shadow takes the highs from both ears
        te, tw = tilt(seg("east")), tilt(seg("west"))
        check("duller from behind", tw < te - 1, f"6 kHz against 1 kHz {tw - te:+.1f} dB from behind than ahead")
    if "distance" in names:
        dn = math.dist(cam_near, (bx, by, bz))
        df = math.dist(cam_far, (bx + 12, by, bz))
        want = 20 * math.log10(df / dn)
        got = level("east") - level("far")
        check("inverse distance (OpenAL's clamped model, rolloff 1)", abs(got - want) < 1.5,
              f"{got:.1f} dB quieter from {dn:.1f} m to {df:.1f} m (1/d: {want:.1f})")
    if "gain" in names:
        got = level("east") - level("quiet")
        check("llAdjustSoundVolume(0.25)", abs(got - 12.04) < 1.0, f"{got:.1f} dB down (12.0)")
    if "cutoff" in names:
        check("llSetSoundRadius cuts it off past the radius", level("cut", 0.3) < -70, f"{level('cut', 0.3):.0f} dBFS")
        check("and back with no radius", abs(level("uncut", 0.3) - level("east")) < 1.5, f"{level('uncut', 0.3) - level('east'):+.1f} dB against before")
    if "stop" in names:
        check("llStopSound", level("stopped", 0.2) < -70, f"{level('stopped', 0.2):.0f} dBFS")
    if "trigger" in names:
        s = seg("beep")
        t = onset(s, 1000)
        check("llTriggerSound plays at once", t is not None and t < 0.5, f"heard {t if t is None else round(t * 1000)} ms after the command")
    if "queue" in names:
        s = seg("queue")
        ta, tb = onset(s, 600), onset(s, 1500)
        check("a queued sound follows the one playing", ta is not None and tb is not None and 0.5 < tb - ta < 0.75,
              f"tone_a at {ta and round(ta, 2)} s, tone_b at {tb and round(tb, 2)} s")
    if "cap" in names:
        peak = float(np.abs(seg("burst")).max())
        check("40 at once: 30 play (the viewer's channels)", capped[0] == 30 and capped[1] <= 30, f"{capped[0]} sources, {capped[1]} native channels")
        check("and the limiter holds the mix", peak < 0.99, f"peak {peak:.2f}")
        check("the mixer's time per 10 ms of sound, 30 playing", mix_us[0] < 500, f"{mix_us[0]:.0f} us ({mix_us[0] / 100:.2f}% of a core; {mix_us[1]:.0f} us averaged over the session)")
    if "music" in names:
        for fmt, (secs, got) in music.items():
            check(f"music: {fmt} plays, title from the station", got is not None,
                  f"{secs:.1f} s to play; {got and got.get('title')}; {got and got.get('format')}")
            chord = band(seg(f"music_{fmt}"), 415, 20)
            check(f"music: {fmt} heard", chord > -45, f"415 Hz at {chord:.0f} dB")
        half = level("music_mp3") - level("music_half")
        check("music volume 0.5", abs(half - 6.02) < 1.0, f"{half:.1f} dB down (6.0)")
        check("music off with the parcel's URL gone", level("music_off", 0.5) < -70 and st_end["state"] == "off",
              f"{level('music_off', 0.5):.0f} dBFS, {st_end['state']}")
    if "self" in names:
        s = seg("self")
        t = onset(s, 1000)
        d = db(s[:, 0]) - db(s[:, 1])
        check("a sound the agent plays where it stands", t is not None and t < 0.8, f"heard after {t if t is None else round(t * 1000)} ms, L-R {d:+.1f} dB")
    if "steps" in names:
        walk, still = level("steps"), level("still")
        # gain 0.1 at the ambient level (0.5), a few metres from the camera: OpenSim's land step averages -42 dBFS
        check("footsteps while walking", walk > still + 10 and walk > -100, f"{walk:.0f} dBFS walking, {still:.0f} standing")
    if "teleport" in names:
        check("the teleport sound", level("teleport") > -50, f"{level('teleport'):.0f} dBFS ({tp}; {len(tp_sources)} sources after)")
    loop_cpu = (cpu1.user + cpu1.system - cpu0.user - cpu0.system) / 3
    burst_cpu = (cpu3.user + cpu3.system - cpu2.user - cpu2.system) / 1
    print(f"client CPU: {loop_cpu * 100:.0f}% of a core with one sound, {burst_cpu * 100:.0f}% with 30 "
          f"(the whole client at --fps 20 in a 320x180 window); mixer {final['mixer']}")
    print("audio test:", "passed" if ok else "FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "test"
    if cmd == "fixtures":
        fixtures()
    elif cmd == "serve":
        serve()
    elif cmd == "test":
        sys.exit(test(sys.argv[2:]))
    else:
        sys.exit(__doc__)
