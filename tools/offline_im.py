"""Offline messages on the local grids: a stand-in for OpenSim's offline message service, and a test that what was sent
while someone was away reaches them when they log in.

  python tools/offline_im.py serve [--port 9790]     the service (OpenSim's OfflineMessageModule posts SaveMessage/
                                                     and RetrieveMessages/ to it); messages kept in local/offline-im.json
  python tools/offline_im.py setup                   point the grid (SOJOURN_GRID: grid or perf-grid) at it (restarts)
  python tools/offline_im.py test [--dll DIR]        Sojourn Friend sends Sojourn Perf an IM while Perf is away; then
                                                     Perf logs in and the IM should be in its event log, marked offline

OpenSim's own offline storage (Offline Message Module V2) has no SQLite store, so the local grids keep none without
this. It speaks the older module's protocol: XML-serialised GridInstantMessages over HTTP POST. The client asks for
offline messages after login (AgentSession.Work.cs): OpenSim answers the RetrieveInstantMessages packet from here;
Second Life answers the ReadOfflineMsgs capability.
"""
import json
import os
import re
import subprocess
import sys
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STORE = ROOT / "local" / "offline-im.json"
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
LOCK = threading.Lock()


def load():
    try:
        return json.loads(STORE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def reply(self, body):
        data = body.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/xml")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        # OpenSim's bodies start with a byte order mark and their own declaration: neither may sit inside the array
        body = self.rfile.read(int(self.headers.get("Content-Length", 0))).decode("utf-8-sig", errors="replace")
        body = re.sub(r"^\s*<\?xml[^>]*\?>", "", body).strip()
        if self.path.rstrip("/").endswith("SaveMessage"):
            to = re.search(r"<toAgentID>([0-9a-fA-F-]{36})</toAgentID>", body)
            if not to:
                return self.reply("<?xml version=\"1.0\"?><boolean>false</boolean>")
            with LOCK:
                store = load()
                store.setdefault(to.group(1).lower(), []).append(body)
                STORE.write_text(json.dumps(store), encoding="utf-8")
            print("saved a message for", to.group(1), flush=True)
            return self.reply("<?xml version=\"1.0\"?><boolean>true</boolean>")
        if self.path.rstrip("/").endswith("RetrieveMessages"):
            who = re.search(r"([0-9a-fA-F]{8}-[0-9a-fA-F-]{27})", body)
            with LOCK:
                store = load()
                msgs = store.pop(who.group(1).lower(), []) if who else []
                STORE.write_text(json.dumps(store), encoding="utf-8")
            print(f"gave {len(msgs)} messages to", who.group(1) if who else "?", flush=True)
            return self.reply("<?xml version=\"1.0\"?><ArrayOfGridInstantMessage xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" "
                              "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" + "".join(msgs) + "</ArrayOfGridInstantMessage>")
        self.reply("<?xml version=\"1.0\"?><boolean>false</boolean>")


def serve(port):
    print(f"offline messages on :{port}", flush=True)
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()


def setup(port):
    sys.path.insert(0, str(ROOT / "tools"))
    import grid
    ini = grid.BIN / "config" / "offline-im.ini"
    text = ("; Written by tools/offline_im.py: offline messages kept by its stand-in service.\n[Messaging]\n"
            f"    OfflineMessageModule = OfflineMessageModule\n    OfflineMessageURL = http://127.0.0.1:{port}\n"
            "    ForwardOfflineGroupMessages = true\n")
    if ini.exists() and ini.read_text() == text:
        return "already set up"
    was_up = grid.ready()
    grid.stop_and_wait()
    ini.write_text(text)
    return "set up" + ("; " + ("up" if grid.start() else "failed to start") if was_up else "")


def client(first, last, port, base, minutes, dll):
    os.environ.setdefault("SOJOURN_GRID", "perf-grid")
    sys.path.insert(0, str(ROOT / "tools"))
    import grid
    log = open(ROOT / "local" / "perf" / f"offline-{first}.log", "w", encoding="utf-8")
    return subprocess.Popen([str(DOTNET), str(Path(dll) / "SojournVR.dll"), "--grid", grid.URL, "--first", first, "--last", last,
                             "--password", "sojourn-local", "--base-dir", str(ROOT / "local" / "perf" / base), "--bridge-port", str(port),
                             "--no-assistant", "--no-voice", "--no-sounds", "--window", "320x180", "--fps", "5", "--minutes", str(minutes)],
                            cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, creationflags=0x00004000)


def api(port, pump, op, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": 20}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/cmd", data=body, headers={"Content-Type": "application/json"})
    return json.loads(urllib.request.urlopen(req, timeout=30).read())


def connected(port, timeout=90):
    end = time.time() + timeout
    while time.time() < end:
        try:
            if json.loads(urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=3).read()).get("connected"):
                return True
        except OSError:
            pass
        time.sleep(1)
    return False


def test(dll):
    words = f"while you were away {int(time.time())}"
    perf_id = "5da1aaa8-3210-44a2-952a-7cd6a6bd3245"   # Sojourn Perf on the perf grid
    friend = client("Sojourn", "Friend", 9733, "offline-friend", 2, dll)
    try:
        if not connected(9733):
            return "Sojourn Friend didn't log in (local/perf/offline-Sojourn.log)"
        time.sleep(3)
        print("sent:", api(9733, "TesselIM", "send", to=perf_id, message=words))
        time.sleep(3)
    finally:
        friend.kill()
    events = ROOT / "local" / "perf" / "offline-perf" / "state" / "pilot" / "events.jsonl"
    seen_before = events.stat().st_size if events.exists() else 0
    perf = client("Sojourn", "Perf", 9734, "offline-perf", 2, dll)
    try:
        if not connected(9734):
            return "Sojourn Perf didn't log in"
        end = time.time() + 30
        while time.time() < end:
            if events.exists():
                with open(events, encoding="utf-8") as f:
                    f.seek(seen_before)
                    for line in f:
                        if words in line:
                            e = json.loads(line)["data"]
                            return f"arrived at login: from {e.get('from')}, offline={e.get('offline')}, timestamp {e.get('timestamp')}"
            time.sleep(1)
        return "didn't arrive (local/perf/offline-Sojourn.log)"
    finally:
        perf.kill()


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "serve"
    port = int(sys.argv[sys.argv.index("--port") + 1]) if "--port" in sys.argv else 9790
    if cmd == "serve":
        serve(port)
    elif cmd == "setup":
        print(setup(port))
    elif cmd == "test":
        print(test(sys.argv[sys.argv.index("--dll") + 1] if "--dll" in sys.argv
                   else str(ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0")))
