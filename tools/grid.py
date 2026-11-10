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
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
# SOJOURN_LOCAL=dir: the main tree's local/ (grid, test tools, secrets) for a run from a git worktree, which has none
LOCAL = Path(os.environ["SOJOURN_LOCAL"]) if os.environ.get("SOJOURN_LOCAL") else ROOT / "local"
# SOJOURN_GRID=perf-grid works on another copy (local/perf-grid, a heavy test scene on :9740-9743), so a performance
# session can swap scenes and restart without disturbing whoever uses the usual one
GRID = LOCAL / os.environ.get("SOJOURN_GRID", "grid")
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


def console(command, wait=None, answers=()):
    """A command typed at the grid's console, then each of answers; every line it printed, the echoes and prompts among
    them. Each is done when the console asks for input again (its prompt, or a question, which the next answer is
    for), looked for after a tenth of a second and then at longer intervals: most are done within milliseconds.
    wait=SECONDS sleeps that long after each and reads once instead, for a command whose output comes after its
    prompt, or that never prompts again (quit)."""
    sid = ET.fromstring(post("/StartSession/", USER="admin", PASS="localdev")).findtext("SessionID")
    try:
        post(f"/ReadResponses/{sid}/", ID=sid)   # (what the console said before us)
        lines = []
        for c in (command, *answers):
            post("/SessionCommand/", ID=sid, COMMAND=c)
            if wait is not None:
                time.sleep(wait)
                lines += [(l.text or "") for l in ET.fromstring(post(f"/ReadResponses/{sid}/", ID=sid)).iter("Line")]
                continue
            # Never read at once: the grid holds a read that arrives before the output until there is some, and now and
            # then this OpenSim never answers one it held (9 of 1,500 with the processor busy, each a 30 s timeout here;
            # 2026-11-09). After the nap a quick command's output is all there and the read comes straight back (none
            # of 1,000 stalled, every one done in its first read).
            # A command slower than the naps does get a read held, so one that times out is asked again; and no prompt
            # after two minutes is an error, not an answer: a caller reading "no such line" out of half the output
            # would take a logged-in account for a free one.
            nap, end = 0.1, time.time() + 120
            while True:
                if time.time() >= end:
                    raise TimeoutError(f"the grid's console didn't ask for input again within 120 s of {c!r} "
                                       "(wait=SECONDS for a command that doesn't)")
                time.sleep(nap)
                try:
                    got = list(ET.fromstring(post(f"/ReadResponses/{sid}/", ID=sid)).iter("Line"))
                except TimeoutError:
                    continue
                lines += [(l.text or "") for l in got]
                if any(l.get("Prompt") == "true" for l in got):
                    break
                nap = min(nap * 2, 1.0)
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
            if why := warm():
                print(f"the grid is up, but its warm-up login went wrong: {why}", file=sys.stderr)
            return True
        time.sleep(2)
    return False


def login(first, last, pw="sojourn-local", start="last", timeout=30):
    """The login a viewer makes (login_to_simulator) with no viewer behind it: the grid's answer, 'login' in it 'true'
    when the account was let in. Nothing connects to the region, so the avatar never appears there, but the grid has
    the account as online (its presence, GridUser's Online, a circuit the region waits on) until logout(answer)."""
    import hashlib
    import xmlrpc.client
    ask = {"first": first, "last": last, "passwd": "$1$" + hashlib.md5(pw.encode()).hexdigest(), "start": start,
           "channel": "grid.py", "version": "1.0", "platform": "Win", "mac": "0" * 32, "id0": "0" * 32,
           "agree_to_tos": "true", "read_critical": "true", "options": []}
    req = urllib.request.Request(URL + "/", data=xmlrpc.client.dumps((ask,), "login_to_simulator").encode(),
                                 headers={"Content-Type": "text/xml"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return xmlrpc.client.loads(r.read())[0][0]


def logout(answer, timeout=20):
    """Logs a login() out as a viewer would; None, or what went unanswered. The grid only logs out an avatar that
    arrived (its presence and GridUser's Online are cleared when a root agent's connection closes), so this is a
    viewer's first and last packets to the region: UseCircuitCode, CompleteAgentMovement, and once the region says
    the arrival is complete, LogoutRequest, until the console's users no longer list the avatar. It is in the region
    for a moment between the last two."""
    import socket
    import struct
    agent, session = uuid.UUID(answer["agent_id"]).bytes, uuid.UUID(answer["session_id"]).bytes
    code = struct.pack("<I", int(answer["circuit_code"]))
    sim = (answer["sim_ip"], int(answer["sim_port"]))
    USE_CIRCUIT_CODE, COMPLETE_AGENT_MOVEMENT, AGENT_MOVEMENT_COMPLETE, LOGOUT_REQUEST = 3, 249, 250, 252
    sent = 0

    def send(number, body):
        nonlocal sent
        sent += 1   # (reliable, sequence number, no extra header; a low-frequency message's number)
        so.sendto(struct.pack(">BIB", 0x40, sent, 0) + struct.pack(">HH", 0xFFFF, number) + body, sim)

    def got(number, seconds, again):
        """Whether the region sent that message within seconds, our own sent again each second it hasn't (a number
        of None: anything at all). What it sends is never acknowledged: it resends until the logout, no harm."""
        end = time.time() + seconds
        while time.time() < end:
            try:
                data = so.recv(4096)
            except socket.timeout:
                again()
                continue
            except OSError:   # (Windows reports a closed port as a reset)
                return False
            head = data[6 + data[5]:][:8]
            if data[0] & 0x80:   # zero-coded: a zero byte is followed by how many zeros it stands for
                plain, i = b"", 0
                while i < len(head) - 1 and len(plain) < 4:
                    plain, i = (plain + bytes(head[i + 1]), i + 2) if head[i] == 0 else (plain + head[i:i + 1], i + 1)
                head = plain
            if number is None or head[:4] == struct.pack(">HH", 0xFFFF, number):
                return True
        return False

    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as so:
        so.settimeout(1)
        use = lambda: send(USE_CIRCUIT_CODE, code + session + agent)
        use()
        if not got(None, timeout, use):
            return "the region didn't answer the circuit"
        send(COMPLETE_AGENT_MOVEMENT, agent + session + code)
        if not got(AGENT_MOVEMENT_COMPLETE, timeout, lambda: None):   # (not sent twice: the region would arrive it twice)
            return "the region didn't complete the arrival"
        # (its LogoutReply isn't waited for: the region closes the connection as it queues it, and none came out)
        name = re.compile(r"\s*%s\s+%s\s" % tuple(re.escape(answer[k].strip('"')) for k in ("first_name", "last_name")))
        end = time.time() + timeout
        while True:
            send(LOGOUT_REQUEST, agent + session)
            time.sleep(0.05)
            if not any(name.match(l) for l in console("show users")):
                return None
            if time.time() >= end:
                return "the region still has the avatar after its logout request"


WARMUP = ("Grid", "Warmup")


def warm(timeout=60):
    """One whole login by an account of its own, logged out again, before start() says the grid is up; None, or why
    it failed. The login service's tables each read their column names at first use, without a lock: when a grid's
    first logins arrive together, the second reads rows through a list the first is still filling, and fails with "The
    given key 'Created' (user accounts) or 'Name' (avatars) was not present in the dictionary" (every time in three
    weeks of OpenSim.log that the first two logins came within 50 ms of each other, never otherwise). A console
    command doesn't do it: the login service has its own instances of the services. After one login has gone all the
    way through, any number can arrive at once. The logout leaves the grid as any account's visit does: nobody
    online, nothing waiting in the region (a login left standing wasn't said or counted by Sojourn Access's people,
    region, search and friends steps either, but the grid had the account online until it stopped)."""
    import xmlrpc.client
    from xml.parsers.expat import ExpatError
    end = time.time() + timeout
    while True:
        try:
            account(*WARMUP)
            answer = login(*WARMUP)
            if answer.get("login") == "true":
                # (the warm-up is done either way: a logout that failed is said, the login not made again)
                left = logout(answer)
                return left and f"it logged in, but not out ({left}): {' '.join(WARMUP)} may still count as online"
            why = answer.get("message") or str(answer)[:200]
        except (OSError, ET.ParseError, ExpatError, xmlrpc.client.Error) as e:
            why = str(e) or type(e).__name__
        if time.time() >= end:
            return f"{why}; the grid's first logins may fail if two arrive together"
        time.sleep(2)


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

VOICE = LOCAL / "voice-server"
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
    shown = console(f"show account {first} {last}")
    if any(f"{first} {last}" in l for l in shown) and not any("No user named" in l for l in shown):
        return "exists"
    return "\n".join(console(f"create user {first} {last} {pw} none@localhost {uuid.uuid4()}", wait=2, answers=("",)))


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "status"
    if cmd == "start":
        print("up" if start() else "failed")
    elif cmd == "stop":
        stop_and_wait()   # a plain stop returns while it's still answering, so a start straight after saw it "up"
    elif cmd == "status":
        print("up" if ready() else "down")
    elif cmd == "console":
        print("\n".join(console(sys.argv[2], wait=float(sys.argv[3]) if len(sys.argv) > 3 else None)))
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
