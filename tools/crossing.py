"""A seated region crossing on the local grid, without the app: a console session (src/Sojourn.SessionProbe, Crossing
Rider) sits in the visitor's physical kart at Atelier's east edge and drives it east into Atelier East through the
agent API; then checks the session is in Atelier East, still seated (the kart's own script says "kart empty" when its
driver goes, as a train's "Driver missing!"), and connected. No GPU, no window.

  python tools/crossing.py [--sound] [--noack MS] [--corner [--slow MS]] [--guard-off] [--keep]
      --sound      the kart loops a sound while driven (tools/audio.py's fixture noise; `python tools/audio.py fixtures`
                   once), the session hears the world (to a file, never the speakers), and the loop must be heard from
                   the kart's new region after the crossing, close by, not from where it crossed
      --noack MS   the crossing guard off (SOJOURN_CROSSING_GUARD=0) and each crossing held MS ms before it connects,
                   as LibreMetaverse's wait for an unacknowledged UseCircuitCode held it (CrossingGuard.cs): for seeing
                   what that does to a seated crossing
      --corner     two crossings seconds apart, as a train over a region corner: the kart starts in Atelier East
                   near the four regions' corner heading north-west, driving on its own once started as a train does
                   (the keys don't reach a region before its reply has come), and crosses into Atelier Northeast, then
                   about 3 s later into Atelier North, while every arrival reply is held --slow MS (default 6000;
                   SOJOURN_ARRIVAL_DELAY_MS, as a slow region's) so the first crossing is still waiting when the
                   second comes. Checks the second was staged (it came while the first was under way), and that the
                   session ends in Atelier North, seated and connected, after both replies have come in
      --guard-off  SOJOURN_CROSSING_GUARD=0 (CrossingGuard's 3 and 4 off), for seeing the failure the guard prevents
      --keep       leave the probe and the visitor running at the end (they log out on their own after 15 minutes)

Needs the grid (tools/grid.py start). Its own accounts (Crossing Rider, Crossing Visitor) and ports (agent API 9814,
visitor 9914), clear of tools/access.py's (9790 and 9885-9886, and 10 up for ACCESS_ACCOUNTS=Walk) and the other
tools'. Output in local/crossing/ (the probe's log: probe.log).
Written by Claude Opus 5.5, 2026-11-01.
"""
import json
import os
import socket
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import grid  # noqa: E402

DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
OUT = ROOT / "local" / "crossing"
PROBE = ROOT / "src" / "Sojourn.SessionProbe" / "bin" / "Release" / "net10.0" / "Sojourn.SessionProbe.dll"
VISITOR = OUT / "visitor-bin" / "Visitor.dll"
API, V_PORT = 9814, 9914
NOISE = "cd6dc921-1145-56fc-9138-54d363caa571"   # tools/audio.py's uid("asset", "noise")
RIDER, GUEST = ("Crossing", "Rider"), ("Crossing", "Visitor")
PASSWORD = "sojourn-local"
BELOW_NORMAL, NO_WINDOW = 0x00004000, 0x08000000
END_WORDS = "crossing test over"


def tcp(port, line, timeout=60):
    with socket.create_connection(("127.0.0.1", port), timeout=timeout) as s:
        s.sendall((line + "\n").encode())
        chunks = []
        while b := s.recv(65536):
            chunks.append(b)
    reply = b"".join(chunks).decode("utf-8", "replace").rstrip("\n")
    if reply.startswith("ERR"):
        raise RuntimeError(f"visitor {line[:40]}: {reply}")
    return reply


def v(line, timeout=60):
    return tcp(V_PORT, line, timeout)


def http(path, body=None, timeout=30):
    req = urllib.request.Request(f"http://127.0.0.1:{API}/{path}", data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def api(pump, op, timeout=20, **args):
    out = http("cmd", {"pump": pump, "data": {"op": op, **args}, "timeout": timeout}, timeout + 20)
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def wait(f, secs, step=0.5):
    end = time.time() + secs
    while time.time() < end:
        try:
            got = f()
        except (OSError, RuntimeError):
            got = None
        if got:
            return got
        time.sleep(step)
    return None


def status():
    try:
        return http("status", timeout=5)
    except OSError:
        return {}


def events(since):
    evs = http(f"events?since={since}&wait=0", timeout=10)
    return evs, max([e.get("seq", since) for e in evs] + [since])


def clear_kart():
    """The visitor's karts go, in every region (one driven over may be in any): the grid console, as
    tools/access.py's clear_owned."""
    owner = next((l.split("ID:")[1].strip() for l in grid.console(f"show account {GUEST[0]} {GUEST[1]}", wait=1) if "ID:" in l), None)
    if not owner:
        return
    for region in ("Atelier", "Atelier East", "Atelier Northeast", "Atelier North"):
        lines = grid.console(f"change region {region}", wait=1.5, answers=(f"show object owner {owner}", "change region root"))
        if any("object(s) found" in l and not l.strip().startswith("0") for l in lines):
            grid.console(f"change region {region}", wait=1.5, answers=(f"delete object owner {owner}", "y", "change region root"))


def build():
    for proj, out in ((ROOT / "src" / "Sojourn.SessionProbe" / "Sojourn.SessionProbe.csproj", None),
                      (ROOT / "tools" / "visitor" / "Visitor.csproj", VISITOR.parent)):
        cmd = [str(DOTNET), "build", str(proj), "-c", "Release", "-m:2", "-v:q", "-nologo", "-clp:ErrorsOnly", "-nodeReuse:false"]
        if out:
            cmd += ["-o", str(out)]
        if subprocess.run(cmd, creationflags=BELOW_NORMAL).returncode != 0:
            sys.exit(f"build failed: {proj.name}")


def main():
    args = sys.argv[1:]
    noack = int(args[args.index("--noack") + 1]) if "--noack" in args else 0
    corner = "--corner" in args
    slow = int(args[args.index("--slow") + 1]) if "--slow" in args else 6000 if corner else 0
    sound = "--sound" in args
    if not grid.ready():
        sys.exit("the local grid isn't up: python tools/grid.py start")
    build()
    for first, last in (RIDER, GUEST):
        grid.account(first, last, PASSWORD)
    (OUT / "visitor").mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    env["SOJOURN_LMV_LOG"] = "Information"   # LibreMetaverse's crossing machine in probe.log
    if sound:
        env["SOJOURN_VOICE_OUT_FILE"] = str(OUT / "heard.wav")
    if noack or "--guard-off" in args:
        env["SOJOURN_CROSSING_GUARD"] = "0"
    if noack:
        env["SOJOURN_CROSSING_NOACK"] = str(noack)
    if slow:
        env["SOJOURN_ARRIVAL_DELAY_MS"] = str(slow)
    vlog = open(OUT / "visitor.log", "w", encoding="utf-8", errors="replace")
    plog = open(OUT / "probe.log", "w", encoding="utf-8", errors="replace")
    visitor = subprocess.Popen([str(DOTNET), str(VISITOR), "--port", str(V_PORT), "--first", GUEST[0], "--last", GUEST[1],
                                "--password", PASSWORD, "--start", "Atelier/236/126/30", "--login", "http://127.0.0.1:9700/"],
                               cwd=OUT / "visitor", stdin=subprocess.DEVNULL, stdout=vlog, stderr=subprocess.STDOUT,
                               creationflags=NO_WINDOW | BELOW_NORMAL)
    probe = subprocess.Popen([str(DOTNET), str(PROBE), "--first", RIDER[0], "--last", RIDER[1], "--port", str(API),
                              "--start", "uri:Atelier&236&130&30", "--base", str(OUT / "probe"), "--expect", END_WORDS,
                              "--seconds", "900", *(["--sounds"] if sound else [])], cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=plog,
                             stderr=subprocess.STDOUT, creationflags=NO_WINDOW | BELOW_NORMAL)
    results = []
    mark = 0

    def probe_log_lines():
        plog.flush()
        return (OUT / "probe.log").read_text(encoding="utf-8", errors="replace").splitlines()

    def check(what, ok, detail=""):
        results.append(ok)
        print(f"{'PASS' if ok else 'FAIL'} {what}{': ' + str(detail) if detail else ''}", flush=True)
        return ok

    try:
        if not check("the rider logs in", wait(lambda: status().get("connected"), 90), status().get("status_message")):
            return 1
        if not check("the visitor logs in", wait(lambda: v("pos", 5), 60)):
            return 1
        if corner:
            # Atelier East near the corner it shares with Northeast and North (all three flat at 21 m there), heading
            # north-west: into Northeast after about 20 m, into North about 30 m later
            region, y, gz, facing, end = "Atelier East", 242, 21.0, 135, "Atelier North"
            kx, stand = 36, (34, 239)
            v(f"tp {region} 32 236 {gz + 1.5:.1f}", 40 + slow / 1000)
        else:
            # land at Atelier's east edge about as high as Atelier East's across the border (as tools/access.py walk_crossing)
            ys = list(range(20, 240, 10))
            h = wait(lambda: (r := api("TesselAgent", "ground", points=[[250, y] for y in ys])["heights"]) and all(z is not None for z in r) and r, 30)
            land = [y for y, z in zip(ys, h or []) if 21 < z < 23]
            if not check("land at Atelier's east edge for the kart", land, h):
                return 1
            y = land[len(land) // 2]
            region, gz, facing, end = "Atelier", h[ys.index(y)], 0, "Atelier East"
            kx, stand = 244, (243, y - 2)
            v(f"tp Atelier 240 {y} {gz + 1.5:.1f}", 40)
        clear_kart()   # a kart left from an earlier run
        time.sleep(2)
        made = v(f"kart {kx} {y} {gz + 0.4:.1f}" + (f" facing {facing} train 7" if corner else "") + (f" sound {NOISE}" if sound else ""), 60)
        kart = made.split(" id ")[-1].strip()
        check("the kart is out", made.startswith("kart"), made)
        api("LLAgent", "requestTeleport", regionname=region, x=stand[0], y=stand[1], z=round(gz + 1.5, 1), skip_confirmation=True, timeout=30)
        if not check(f"the rider is in {region}", wait(lambda: status().get("status_message") == f"in {region}", 30 + slow / 1000),
                     status().get("status_message")):
            return 1
        time.sleep(2 + slow / 1000)
        _, since = events(0)
        sat = api("LLAgent", "requestSit", obj_uuid=kart, timeout=15)
        check("sits in the kart", sat.get("sitting_on"), sat)

        def heard(words, since):
            evs, _ = events(since)
            return next((t for t in map(json.dumps, evs) if words in t), None)

        if not check("the kart's script takes the keys", wait(lambda: heard("kart controls taken", since), 15)):
            return 1
        _, since = events(0)
        mark = len(probe_log_lines())
        t0 = time.time()
        api("TesselAgent", "move", forward=1, secs=5)
        if corner:
            first = wait(lambda: status().get("status_message") == "in Atelier Northeast", 25, 0.1)
            check("drives into Atelier Northeast", first, f"{time.time() - t0:.1f} s; {status().get('status_message')}")
        crossed = wait(lambda: status().get("status_message") == f"in {end}", 25, 0.2)
        check(f"drives into {end}", crossed, f"{time.time() - t0:.1f} s; {status().get('status_message')}")
        if corner:
            # each reply is held `slow` ms; the crossing machine gives up after 30 s and the session's watchdog logs in
            # again after 30 s without a region: past those, the outcome stands
            time.sleep(35)
            log = probe_log_lines()[mark:]
            staged = [l for l in log if "while the last one was still" in l or "Cannot begin crossing" in l or "state machine is busy" in l]
            check("the second crossing came while the first was under way", staged, staged[0].strip() if staged else "no sign of it in the probe's log")
        else:
            time.sleep(10)
        st = status()
        empty = heard("kart empty", since)
        anims = api("LLAgent", "animations")
        check(f"still in {end} and connected {'then' if corner else '10 s on'}", st.get("connected") and st.get("status_message") == f"in {end}",
              st.get("status_message"))
        check("still seated (the kart never said it was empty)", not empty, empty or f"sitting: {anims.get('sitting')}")
        if sound:
            # (the probe has no listener of its own, so the distance is the loop's from the rider, both global)
            me = api("LLAgent", "getPosition")["global"]
            loops = [x for x in api("SojournAudio", "sources")["sources"] if x.get("loop") and x.get("sound_id") == NOISE]
            far = [max(abs(a - b) for a, b in zip(x["pos"], me)) if x.get("pos") else None for x in loops]
            check(f"the kart's loop is heard from {end}, from the kart", loops and all(x.get("region") == end for x in loops)
                  and all(f is not None and f < 5 for f in far), [{"region": x.get("region"), "from the rider": f} for x, f in zip(loops, far)] or "no loop tracked")
        evs, _ = events(since)
        notes = [e for e in evs if "CrossingFailed" in json.dumps(e) or "RegionLost" in json.dumps(e)]
        check("no crossing failed or region lost notice", not notes, " | ".join(json.dumps(n)[:200] for n in notes))
        guard = [l.strip().replace(f"[{RIDER[0]} {RIDER[1]}] ", "") for l in probe_log_lines()[mark:]]
        guard = [l for l in guard if any(w in l.lower() for w in ("crossing", "usecircuitcode", "own avatar in", "no region", "audio: sounds",
                                                                  "movementcomplete", "recover", "reconnecting"))]
        print("     probe log:", *guard[-24:], sep="\n       ")
        return 0 if all(results) else 1
    finally:
        if "--keep" not in args:
            try:
                api("LLAgent", "requestStand")
                time.sleep(1)
                api("LLChatBar", "sendChat", message=END_WORDS)
            except (OSError, RuntimeError):
                pass
            try:
                v("quit", 5)
            except (OSError, RuntimeError):
                pass
            clear_kart()
            for p in (probe, visitor):
                try:
                    p.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    p.kill()


if __name__ == "__main__":
    sys.exit(main())
