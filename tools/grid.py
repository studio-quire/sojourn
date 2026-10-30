"""Sojourn's local OpenSim grid (local/grid, standalone; region Atelier on :9700, neighbours 9701-9703).

  python tools/grid.py start|stop|status
  python tools/grid.py console "show users"
  python tools/grid.py account First Last [password]
  python tools/grid.py scene light|dense       swap the region database (restarts the grid)
  python tools/grid.py voice up|down|status    the local voice server (legion-voice-mixer in Docker Desktop) and
                                               OpenSim's WebRTC voice config pointing at it (docs/voice.md)
  python tools/grid.py voice parcels estate|parcel|off    voice on the four regions' parcels: the estate channel
                                               (and neighbours), each parcel its own channel, or none (restarts)

The grid is a copy of Multitude's phase 2 render grid (its Atelier scene), moved to Sojourn's ports. OpenSim runs on
the .NET 8 runtime in %LOCALAPPDATA%\\Microsoft\\dotnet.
"""
import os
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
# SOJOURN_GRID=perf-grid works on another copy (local/perf-grid, a heavy test scene on :9740-9743), so a performance
# session can swap scenes and restart without disturbing whoever uses the usual one
GRID = ROOT / "local" / os.environ.get("SOJOURN_GRID", "grid")
BIN = GRID / "bin"


def _port():
    try:
        for line in (BIN / "OpenSim.ini").read_text(errors="replace").splitlines():
            if line.strip().startswith("http_listener_port"):
                return int(line.split("=")[1].strip())
    except (OSError, ValueError):
        pass
    return 9700


URL = f"http://127.0.0.1:{_port()}"
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "Microsoft" / "dotnet" / "dotnet.exe"
DETACHED = 0x08000000 | 0x00000200 | 0x01000000   # no window, new process group, breakaway from job
BELOW_NORMAL = 0x00004000


def post(path, **form):
    data = urllib.parse.urlencode(form).encode()
    with urllib.request.urlopen(urllib.request.Request(URL + path, data=data), timeout=30) as r:
        return r.read().decode("utf-8", errors="replace")


def ready():
    try:
        sid = ET.fromstring(post("/StartSession/", USER="admin", PASS="localdev")).findtext("SessionID")
        post("/CloseSession/", ID=sid)
        return True
    except (OSError, ET.ParseError):   # not listening yet, or up but its console not yet
        return False


def console(command, wait=2.0, answers=()):
    sid = ET.fromstring(post("/StartSession/", USER="admin", PASS="localdev")).findtext("SessionID")
    try:
        post(f"/ReadResponses/{sid}/", ID=sid)
        lines = []
        for c in (command, *answers):
            post("/SessionCommand/", ID=sid, COMMAND=c)
            time.sleep(wait)
            lines += [(l.text or "") for l in ET.fromstring(post(f"/ReadResponses/{sid}/", ID=sid)).iter("Line")]
        return lines
    finally:
        post("/CloseSession/", ID=sid)


def start(timeout=180):
    if ready():
        return True
    log = open(GRID / "console.log", "a")
    p = subprocess.Popen([str(DOTNET), "OpenSim.dll", "-console=rest"], cwd=BIN, stdout=log, stderr=log,
                         stdin=subprocess.DEVNULL, creationflags=DETACHED | BELOW_NORMAL)
    (GRID / "grid.pid").write_text(str(p.pid))
    end = time.time() + timeout
    while time.time() < end:
        if ready():
            return True
        time.sleep(2)
    return False


def stop():
    if ready():
        try:
            console("quit", wait=1)
        except Exception:
            pass


def stop_and_wait():
    stop()
    pid = int((GRID / "grid.pid").read_text()) if (GRID / "grid.pid").exists() else 0
    end = time.time() + 120
    while pid and time.time() < end and subprocess.run(["tasklist", "/FI", f"PID eq {pid}"], capture_output=True, text=True).stdout.count("dotnet"):
        time.sleep(2)


# --- voice: legion-voice-mixer (a Janus plugin speaking SL's WebRTC voice protocol, GPL-3) in Docker Desktop ---

VOICE = ROOT / "local" / "voice-server"
VOICE_INI = BIN / "config" / "os-webrtc-janus.ini"
DOCKER_DESKTOP = Path(r"C:\Program Files\Docker\Docker\Docker Desktop.exe")
JANUS = "http://127.0.0.1:14223/voice"
# OpenSim talks to the mixer through tools/janus_name_proxy.py (its Janus service names the stock AudioBridge plugin);
# the admin API and the media ports go straight to Janus.
COMPOSE = """services:
  janus:
    image: ghcr.io/johnlegionh/legion-voice-mixer:1.2.0
    ports:
      - "127.0.0.1:14225:14225/tcp"
      - "10000-10100:10000-10100/udp"
    env_file: [.env]
    restart: "no"
    logging:
      driver: json-file
      options: {max-size: "10m", max-file: "2"}
  proxy:
    image: ghcr.io/johnlegionh/legion-voice-mixer:1.2.0
    entrypoint: ["python3", "-u", "/proxy.py", "14223", "http://janus:14223"]
    volumes: ["{proxy}:/proxy.py:ro"]
    ports:
      - "127.0.0.1:14223:14223/tcp"
    depends_on: [janus]
    restart: "no"
"""
VOICE_INI_TEXT = """; Written by tools/grid.py voice up. OpenSim reads every .ini in bin/config at start.
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    WebRtcVoiceServerURI = ${{Const|PrivURL}}:${{Const|PrivatePort}}
    MessageDetails = false
[JanusWebRtcVoice]
    JanusGatewayURI = http://127.0.0.1:14223/voice
    APIToken = {api}
    JanusGatewayAdminURI = http://127.0.0.1:14225/voiceAdmin
    AdminAPIToken = {admin}
    MessageDetails = false
"""
ALLOW_VOICE, USE_ESTATE_VOICE = 1 << 29, 1 << 30   # ParcelFlags


def lan_ip():
    """The address the host's default route uses (no packet is sent). Janus advertises it as its media address:
    Docker Desktop publishes the UDP range on every host interface, and our client's host candidates reach it."""
    import socket
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("192.0.2.1", 9))
        return s.getsockname()[0]
    finally:
        s.close()


def docker(*args, **kw):
    return subprocess.run(["docker", *args], capture_output=True, text=True, creationflags=BELOW_NORMAL, **kw)


def ensure_docker(timeout=180):
    if docker("info", "--format", "{{.ServerVersion}}").returncode == 0:
        return True
    subprocess.Popen([str(DOCKER_DESKTOP)], creationflags=DETACHED)
    end = time.time() + timeout
    while time.time() < end:
        if docker("info", "--format", "{{.ServerVersion}}").returncode == 0:
            return True
        time.sleep(3)
    return False


def voice_secrets():
    env = VOICE / ".env"
    vals = {}
    if env.exists():
        for line in env.read_text().splitlines():
            if "=" in line and not line.startswith("#"):
                k, v = line.split("=", 1)
                vals[k] = v
    return vals.get("JS_API_SECRET") or os.urandom(32).hex(), vals.get("JS_ADMIN_SECRET") or os.urandom(32).hex()


def janus_up():
    try:
        with urllib.request.urlopen(JANUS + "/info", timeout=3) as r:
            return b"server_info" in r.read()
    except OSError:
        return False


def set_parcel_voice(mode):
    """estate: every parcel on the estate channel; parcel: voice, each parcel its own channel; off: no voice.
    Edits the region database, so the grid must be stopped."""
    import sqlite3
    c = sqlite3.connect(BIN / "OpenSim.db")
    for rid, lid, flags in c.execute("select RegionUUID, LocalLandID, LandFlags from land").fetchall():
        flags &= ~(ALLOW_VOICE | USE_ESTATE_VOICE)
        flags |= {"estate": ALLOW_VOICE | USE_ESTATE_VOICE, "parcel": ALLOW_VOICE, "off": 0}[mode]
        c.execute("update land set LandFlags=? where RegionUUID=? and LocalLandID=?", (flags, rid, lid))
    c.execute("update estate_settings set AllowVoice=1")
    c.commit()
    c.close()


def voice_up():
    if not ensure_docker():
        return "Docker Desktop didn't start"
    VOICE.mkdir(parents=True, exist_ok=True)
    api, admin = voice_secrets()
    ip = lan_ip()
    env = [f"JS_PUBLIC_IP={ip}", "JS_KEEP_PRIVATE_HOST=false", f"JS_API_SECRET={api}", f"JS_ADMIN_SECRET={admin}",
           "JS_RTP_PORT_RANGE=10000-10100", "JS_HTTP_PORT=14223", "JS_ADMIN_PORT=14225", "JS_WS_ENABLED=false",
           "JS_SELFCHECK=off"]
    (VOICE / ".env").write_text("".join(line + chr(10) for line in env))
    (VOICE / "docker-compose.yml").write_text(COMPOSE.replace("{proxy}", (ROOT / "tools" / "janus_name_proxy.py").as_posix()))
    r = docker("compose", "-p", "sojourn-voice", "-f", str(VOICE / "docker-compose.yml"), "up", "-d", cwd=VOICE)
    if r.returncode:
        return "docker compose up failed: " + r.stderr
    end = time.time() + 60
    while time.time() < end and not janus_up():
        time.sleep(1)
    if not janus_up():
        return "the mixer didn't answer on " + JANUS
    ini = VOICE_INI_TEXT.format(api=api, admin=admin)
    if not VOICE_INI.exists() or VOICE_INI.read_text() != ini:
        was_up = ready()
        stop_and_wait()
        VOICE_INI.write_text(ini)
        set_parcel_voice("estate")
        if was_up:
            start()
        return f"voice up (media on {ip}); OpenSim configured" + (" and restarted" if was_up else "")
    return f"voice up (media on {ip})"


def voice_down():
    r = docker("compose", "-p", "sojourn-voice", "-f", str(VOICE / "docker-compose.yml"), "down", cwd=VOICE)
    return "voice down" if r.returncode == 0 else r.stderr


def voice_status():
    if not janus_up():
        return "mixer down"
    return "mixer up" + ("".join(chr(10) + l for l in console("janus list rooms", wait=1)) if ready() else "")


def scene(name):
    """Swap in a saved region database (local/grid/scenes/NAME/OpenSim.db): light is Multitude's Atelier scene as
    built, dense the same with 20 more copies of it spread over the four regions (load oar --merge --displacement)."""
    import shutil
    src = GRID / "scenes" / name / "OpenSim.db"
    if not src.exists():
        return f"no scene {name} (have: {', '.join(p.name for p in (GRID / 'scenes').iterdir())})"
    stop_and_wait()
    shutil.copyfile(src, BIN / "OpenSim.db")
    if VOICE_INI.exists():
        set_parcel_voice("estate")
    return f"scene {name}: " + ("up" if start() else "failed")


def account(first, last, pw="sojourn-local"):
    shown = console(f"show account {first} {last}", wait=1)
    if any(f"{first} {last}" in l for l in shown) and not any("No user named" in l for l in shown):
        return "exists"
    return "\n".join(console(f"create user {first} {last} {pw} none@localhost {uuid.uuid4()}", wait=2, answers=("",)))


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "status"
    if cmd == "start":
        print("up" if start() else "failed")
    elif cmd == "stop":
        stop()
    elif cmd == "status":
        print("up" if ready() else "down")
    elif cmd == "console":
        print("\n".join(console(sys.argv[2], wait=float(sys.argv[3]) if len(sys.argv) > 3 else 2.0)))
    elif cmd == "account":
        print(account(*sys.argv[2:]))
    elif cmd == "scene":
        print(scene(sys.argv[2]))
    elif cmd == "voice":
        sub = sys.argv[2] if len(sys.argv) > 2 else "status"
        if sub == "up":
            print(voice_up())
        elif sub == "down":
            print(voice_down())
        elif sub == "parcels":
            was_up = ready()
            stop_and_wait()
            set_parcel_voice(sys.argv[3])
            print(f"parcels: {sys.argv[3]}" + (("; " + ("up" if start() else "failed")) if was_up else ""))
        else:
            print(voice_status())
