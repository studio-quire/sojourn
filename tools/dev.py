"""Sojourn's dev mode, from the Claude Code session driving it (docs/dev-mode.md): the client starts with --dev, has
no Claude of its own, and what the person says or types to its assistant comes to this session instead.

  python tools/dev.py start [--vr | --sim] [--main [--account "First Last"]] [--port P] [--logs] [--dry-run] [-- client args]
        start the client in dev mode, detached (it outlives this command), in its own minimised console; the local grid
        (started if it's down) as Sojourn Pilot unless --main (Second Life itself: base dir local/main, the stored
        password passed by path, never read here). Waits for the login, then says where everything is.
  python tools/dev.py watch            one line per thing the person says, types, picks or points at (run it under
                                       the Monitor tool; it carries on where the last watch stopped, across restarts)
  python tools/dev.py say "..."        your reply on the panel, read out if they have spoken replies on (--quiet: not)
  python tools/dev.py working "..."    a progress line on the panel while you work
  python tools/dev.py ask "question?" "choice" "choice" ...   choices on the panel; the pick comes back on the watch
  python tools/dev.py point "Point at the mirror"             asks them to point; what they point at comes back
  python tools/dev.py confirm "Who did you mean?" "Quillon"   asks them to check a word by typing, the guess already in
                                       (the headset's keyboard comes up); what they send comes back
  python tools/dev.py shot [--width 1280] [--close [panel|feed|reader|keyboard|card]] [--world]
        a picture of what they see, the panel, feed and cards over the view as the headset's compositor lays them
        (--close: that one also close up, at about its own pixels; --world: the view without them); prints the paths
  python tools/dev.py log [-n 60] [regex]   the end of this session's client log
  python tools/dev.py call PUMP OP ['{json}' | key=value ...]   any agent API op (docs/client.md > The agent API)
  python tools/dev.py info | list | stop | restart [--build]

--port picks the client (default: the one dev.py started last, else 9720).
"""
import argparse
import ctypes
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEV = ROOT / "local" / "dev"
sys.path.insert(0, str(ROOT / "tools"))
import client as clientpy  # noqa: E402

MAIN_GRID = "https://login.agni.lindenlab.com/cgi-bin/login.cgi"
MAIN_DIR = ROOT / "local" / "main"
MAIN_PASSWORD = ROOT / "local" / "secrets" / "sl-main.txt"
MAIN_LOGIN = MAIN_DIR / "login.json"   # the account's name (not a secret), remembered after the first --account

# a console of its own (the ToS and MFA are answered there by the person), minimised and behind; outlives us
NEW_CONSOLE, NEW_GROUP, BREAKAWAY = 0x10, 0x200, 0x01000000


def out(s):
    print(s, flush=True)


# --- the client --------------------------------------------------------------------------------------------------

def note(port):
    try:
        return json.loads((DEV / f"{port}.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def alive(pid):
    if not pid:
        return False
    k = ctypes.windll.kernel32
    h = k.OpenProcess(0x1000, False, int(pid))   # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return False
    code = ctypes.c_ulong()
    ok = k.GetExitCodeProcess(h, ctypes.byref(code))
    k.CloseHandle(h)
    return bool(ok) and code.value == 259   # STILL_ACTIVE


def default_port():
    try:
        return int((DEV / "last-port").read_text().strip())
    except (OSError, ValueError):
        return 9720


class Api:
    def __init__(self, port):
        self.base = f"http://127.0.0.1:{port}"

    def req(self, path, body=None, timeout=20):
        data = None if body is None else json.dumps(body).encode()
        r = urllib.request.Request(self.base + path, data=data, method="GET" if body is None else "POST",
                                   headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(r, timeout=timeout) as resp:
            raw = resp.read()
        return json.loads(raw) if raw else None

    def cmd(self, pump, op, wait_s=15, **args):   # wait_s: ops take a "wait" argument of their own
        r = self.req("/cmd", {"pump": pump, "data": {"op": op, **args}, "timeout": wait_s}, timeout=wait_s + 140)
        if isinstance(r, dict) and r.get("error"):
            raise RuntimeError(str(r["error"]))
        return r

    def status(self):
        try:
            return self.req("/status", timeout=3)
        except (OSError, ValueError):
            return None


# --- start, stop, restart ----------------------------------------------------------------------------------------

def start(a):
    port = a.port or 9720
    if Api(port).status() is not None:
        n = note(port)
        sys.exit(f"something already answers on :{port}" + (f" (a dev client, pid {n['pid']}; dev.py stop)" if n and alive(n.get("pid")) else
                 " (another Sojourn: pick another --port, or stop it)"))
    args, env = list(a.rest), dict(os.environ)
    env.pop("SOJOURN_DEV_TOKEN", None)
    env["SOJOURN_FOREGROUND"] = "1"   # the person uses it: its window takes focus (src/Sojourn.Client/Window/Background.cs)
    if a.main:
        if a.account:
            parts = a.account.split()
            first, last = parts[0], parts[1] if len(parts) > 1 else "Resident"
            MAIN_DIR.mkdir(parents=True, exist_ok=True)
            MAIN_LOGIN.write_text(json.dumps({"first": first, "last": last}), encoding="utf-8")
        else:
            try:
                acct = json.loads(MAIN_LOGIN.read_text(encoding="utf-8"))
                first, last = acct["first"], acct["last"]
            except (OSError, ValueError, KeyError):
                sys.exit("--main needs the account's name once: --account \"First Last\" (or just the username); ask the user")
        if not MAIN_PASSWORD.exists():
            sys.exit(f"no stored password at {MAIN_PASSWORD}: the user puts it there themselves")
        args = ["--grid", MAIN_GRID, "--first", first, "--last", last, "--password-file", str(MAIN_PASSWORD),
                "--base-dir", str(MAIN_DIR), "--start", "last"] + args
    elif not a.dry_run:
        sys.path.insert(0, str(ROOT / "tools"))
        import grid
        if not grid.start():
            sys.exit("the local grid didn't come up (python tools/grid.py status)")
    if a.vr or a.sim:
        args = clientpy.vr_args(args)
    if a.vr and not a.sim:
        args = ["--xr-wait", "300"] + args   # the headset may not be on Link yet: wait for it rather than fall back to the desktop
    if a.sim:
        args = ["--xr-wait", "90"] + args
        env.update({k: v for k, v in clientpy.simulator_env().items() if k == "XR_RUNTIME_JSON"})
    if a.logs:
        base = MAIN_DIR if a.main else ROOT / "local" / "client"
        env["SOJOURN_FRAME_LOG"] = str(base / "frames.csv")
        env["SOJOURN_VR_LOG"] = str(base / "vr.csv")
    args += ["--dev", "--bridge-port", str(port)]
    line = [str(clientpy.DOTNET), str(clientpy.DLL), *args]
    if a.dry_run:
        out(" ".join(line))
        return
    if not clientpy.DLL.exists() and clientpy.build() != 0:
        sys.exit("the build failed")
    DEV.mkdir(parents=True, exist_ok=True)
    (DEV / f"{port}.start.json").write_text(json.dumps({"vr": a.vr, "sim": a.sim, "main": a.main, "logs": a.logs, "rest": a.rest}),
                                             encoding="utf-8")
    (DEV / "last-port").write_text(str(port))
    si = subprocess.STARTUPINFO()
    si.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    si.wShowWindow = 7   # SW_SHOWMINNOACTIVE
    try:
        p = subprocess.Popen(line, cwd=ROOT, env=env, startupinfo=si, creationflags=NEW_CONSOLE | NEW_GROUP | BREAKAWAY)
    except OSError:
        p = subprocess.Popen(line, cwd=ROOT, env=env, startupinfo=si, creationflags=NEW_CONSOLE | NEW_GROUP)
    api, end = Api(port), time.time() + (240 if a.sim else 150)
    out(f"started (pid {p.pid}); waiting for the login on :{port}...")
    st = None
    while time.time() < end:
        if p.poll() is not None:
            sys.exit(f"the client exited ({p.returncode}) before logging in; its console log is in the base dir's logs/")
        st = api.status()
        if st and st.get("connected"):
            break
        time.sleep(2)
    else:
        out(f"not logged in yet ({(st or {}).get('status_message') or 'no answer'}); on the main grid the person may need to "
            "answer the ToS or an MFA code in the client's console window. It carries on; check with dev.py info.")
    info_print(port)


def stop(port, quiet=False):
    n = note(port)
    pid = n.get("pid") if n else None
    try:
        Api(port).cmd("LLAppViewer", "requestQuit", 5)
    except (OSError, RuntimeError, ValueError):
        pass
    end = time.time() + 30
    while time.time() < end and (alive(pid) if pid else Api(port).status() is not None):
        time.sleep(0.5)
    if pid and alive(pid):
        subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
    if not quiet:
        out("stopped")


def restart(a):
    port = a.port or default_port()
    try:
        s = json.loads((DEV / f"{port}.start.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        sys.exit(f"no record of how :{port} was started (dev.py start does that)")
    stop(port, quiet=True)
    if a.build and clientpy.build() != 0:
        sys.exit("the build failed; the client is stopped")
    start(argparse.Namespace(port=port, vr=s["vr"], sim=s["sim"], main=s["main"], logs=s["logs"], rest=s["rest"],
                             account=None, dry_run=False))


# --- what's there ------------------------------------------------------------------------------------------------

def info_print(port):
    n, st = note(port), Api(port).status()
    if not n:
        out(f":{port}: no dev client" + (" (a client answers there, not in dev mode)" if st else ""))
        return
    where = ""
    try:
        r = Api(port).cmd("LLAgent", "getPosition", 5) if st and st.get("connected") else None
        if isinstance(r, dict) and isinstance(r.get("region"), list):
            where = ", at " + ", ".join(f"{v:.0f}" for v in r["region"])
    except (OSError, RuntimeError, ValueError):
        pass
    out(f"dev client on :{port}: pid {n['pid']} ({'running' if alive(n['pid']) else 'gone'}), "
        f"{'headset' if n.get('vr') else 'desktop'}, {n.get('account')} on {'Second Life' if 'agni' in n.get('grid', '') else n.get('grid')}, "
        f"{(st or {}).get('status', 'no answer')}{where}")
    out(f"  log: {n.get('log')}")
    out(f"  base dir: {n.get('base_dir')}")
    if not n.get("assistant"):
        out("  started with --no-assistant: nothing hears the person (no watch lines)")


def list_clients():
    found = False
    for f in sorted(DEV.glob("*.json")):
        if f.stem.isdigit():
            n = note(int(f.stem))
            if n and alive(n.get("pid")):
                info_print(int(f.stem))
                found = True
    for port in range(9720, 9730):
        if not note(port) and Api(port).status():
            out(f":{port}: a Sojourn not in dev mode")
            found = True
    if not found:
        out("no Sojourn running")


# --- the person's words -------------------------------------------------------------------------------------------

def describe(t):
    if not isinstance(t, dict):
        return "nothing"
    bits = [t.get("name") or t.get("kind")]
    if t.get("distance") is not None:
        bits.append(f"{t['distance']} m")
    if t.get("hud"):
        bits.append("on a HUD")
    if t.get("id"):
        bits.append(f"{t.get('kind')} {t['id']}")
    if t.get("handle"):   # incognito, blind: a handle instead of a name and id
        bits.append(t["handle"])
    return ", ".join(str(b) for b in bits if b)


def target_text(t):
    return f" (that: {describe(t)})" if isinstance(t, dict) else ""


def line_for(d):
    what = d.get("what")
    if what == "request":
        verb = {"voice": "said", "typed": "typed", "pick": "picked"}.get(d.get("source"), "said")
        return f'{verb}: "{d.get("text", "")}"{target_text(d.get("target"))}'
    if what == "pick":
        return f'picked: "{d.get("label")}"'
    if what == "pointed":
        return "pointed: cancelled" if d.get("text") == "cancelled" else f"pointed at: {describe(d.get('target'))}"
    if what == "answer":
        return "typed answer: dismissed" if d.get("source") == "dismissed" else f'typed answer: "{d.get("text", "")}"'
    if what == "timer":
        return f"timer: {d.get('text')}"
    return None


def watch(port):
    """One line per dev event; carries on from the cursor (local/dev/PORT.cursor: pid and seq) so a re-armed watch
    misses nothing, follows a restart on the same port, and ends with a line when the client is gone for good."""
    cur_file, beat = DEV / f"{port}.cursor", DEV / f"{port}.watching"
    DEV.mkdir(parents=True, exist_ok=True)
    api = Api(port)
    gone_since = None
    while True:
        n = note(port)
        pid = n.get("pid") if n else None
        if not pid or not alive(pid):
            gone_since = gone_since or time.time()
            if time.time() - gone_since > 120:
                out(f"sojourn: the client on :{port} has closed")
                return
            time.sleep(2)
            continue
        if gone_since:
            out(f"sojourn: the client on :{port} is back (pid {pid})")
            gone_since = None
        try:
            cpid, seq = cur_file.read_text().split()
            seq = int(seq) if int(cpid) == pid else 0
        except (OSError, ValueError):
            seq = 0
        beat.touch()
        try:
            evs = api.req(f"/events?since={seq}&wait=25", timeout=40) or []
        except (OSError, ValueError):
            time.sleep(2)
            continue
        for e in evs:
            seq = max(seq, e.get("seq", seq))
            if e.get("kind") == "dev" and (text := line_for(e.get("data") or {})):
                out(text)
            elif e.get("kind") == "sojourn" and (e.get("data") or {}).get("what") == "private":
                d = e["data"]
                if not d.get("on"):
                    out(f"incognito off after {d.get('minutes')} min")
                elif d.get("sees"):
                    out(f"incognito on ({d.get('why') or 'let it see'}), letting the assistant see: help as usual; this session's "
                        "transcript keeps what it sees (docs/dev-mode.md); don't read the client's logs, caches or state files meanwhile")
                else:
                    out(f"incognito on ({d.get('why')}): only the general picture and handles until it's off; don't read the "
                        "client's logs, caches or state files meanwhile")
        cur_file.write_text(f"{pid} {seq}")


# --- the panel ----------------------------------------------------------------------------------------------------

def panel(port, **fields):
    Api(port).cmd("SojournAssistant", "panel", 5, **fields)


def parse_args_kv(items):
    if len(items) == 1 and items[0].lstrip().startswith("{"):
        return json.loads(items[0])
    data = {}
    for it in items:
        k, _, v = it.partition("=")
        try:
            data[k] = json.loads(v)
        except ValueError:
            data[k] = v
    return data


def main():
    if len(sys.argv) < 2 or sys.argv[1] in ("-h", "--help", "help"):
        out(__doc__)
        return
    cmd, argv = sys.argv[1], sys.argv[2:]
    rest = []
    if "--" in argv:
        i = argv.index("--")
        argv, rest = argv[:i], argv[i + 1:]
    ap = argparse.ArgumentParser(prog=f"dev.py {cmd}")
    ap.add_argument("--port", type=int)
    if cmd == "start":
        ap.add_argument("--vr", action="store_true")
        ap.add_argument("--sim", action="store_true")
        ap.add_argument("--main", action="store_true")
        ap.add_argument("--account")
        ap.add_argument("--logs", action="store_true")
        ap.add_argument("--dry-run", action="store_true")
        a = ap.parse_args(argv)
        a.rest = rest
        return start(a)
    if cmd == "restart":
        ap.add_argument("--build", action="store_true")
        return restart(ap.parse_args(argv))
    if cmd == "say":
        ap.add_argument("text")
        ap.add_argument("--quiet", action="store_true")
        a = ap.parse_args(argv)
        # Answering moves on from an unanswered request to point: left up, it kept the pointer forced on (2026-10-28).
        panel(a.port or default_port(), point="")
        return Api(a.port or default_port()).cmd("SojournAssistant", "say", 5, text=a.text, speak=not a.quiet)
    if cmd == "working":
        ap.add_argument("text")
        a = ap.parse_args(argv)
        return panel(a.port or default_port(), status=a.text, working=True)
    if cmd == "ask":
        ap.add_argument("prompt")
        ap.add_argument("choices", nargs="+")
        a = ap.parse_args(argv)
        return panel(a.port or default_port(), prompt=a.prompt, working=False, status=None, heard=None, result=None,
                     choices=[{"id": f"c{i + 1}", "label": c} for i, c in enumerate(a.choices[:6])])
    if cmd == "point":
        ap.add_argument("prompt")
        a = ap.parse_args(argv)
        return panel(a.port or default_port(), point=a.prompt)
    if cmd == "confirm":
        ap.add_argument("question")
        ap.add_argument("guess")
        a = ap.parse_args(argv)
        return Api(a.port or default_port()).cmd("SojournAssistant", "askTyped", 5, question=a.question, text=a.guess)
    if cmd == "shot":
        ap.add_argument("--width", type=int, default=1280)
        ap.add_argument("--close", nargs="?", const="panel")
        ap.add_argument("--world", action="store_true")
        a = ap.parse_args(argv)
        port = a.port or default_port()
        base = Path((note(port) or {}).get("base_dir") or ROOT / "local" / "client")
        path = base / "dev-shots" / time.strftime("%Y%m%d-%H%M%S.png")
        args = dict(filename=str(path), width=a.width, height=a.width)
        if not a.world:
            args["showui"] = True
            if a.close:
                args["close"] = a.close
        r = Api(port).cmd("LLViewerWindow", "saveSnapshot", 20, **args) or {}
        if r.get("ok") is False:
            sys.exit(f"no picture: {r.get('error')}")
        lines = [str(path)]
        if r.get("close"):
            lines.append(r["close"])
        notes = []
        if not a.world:
            notes.append("menus: " + (", ".join(r.get("layers") or []) or "none on layers"))
        if r.get("left_out"):
            notes.append("not drawn: " + ", ".join(r["left_out"]))
        if r.get("close_error"):
            notes.append("no close-up: " + r["close_error"])
        if notes:
            lines.append("(" + "; ".join(notes) + ")")
        return out("\n".join(lines))
    if cmd == "log":
        ap.add_argument("-n", type=int, default=60)
        ap.add_argument("pattern", nargs="?")
        a = ap.parse_args(argv)
        n = note(a.port or default_port())
        if not n or not n.get("log"):
            sys.exit("no log known for that client")
        lines = Path(n["log"]).read_text(encoding="utf-8", errors="replace").splitlines()
        if a.pattern:
            rx = re.compile(a.pattern, re.I)
            lines = [x for x in lines if rx.search(x)]
        return out("\n".join(lines[-a.n:]))
    if cmd == "call":
        ap.add_argument("pump")
        ap.add_argument("op")
        ap.add_argument("args", nargs="*")
        ap.add_argument("--timeout", type=float, default=15)
        a = ap.parse_args(argv)
        r = Api(a.port or default_port()).cmd(a.pump, a.op, a.timeout, **parse_args_kv(a.args))
        return out(json.dumps(r, indent=1, ensure_ascii=False))
    a = ap.parse_args(argv)
    port = a.port or default_port()
    if cmd == "watch":
        return watch(port)
    if cmd == "info":
        return info_print(port)
    if cmd == "list":
        return list_clients()
    if cmd == "stop":
        return stop(port)
    sys.exit(f"unknown command {cmd}; dev.py help")


if __name__ == "__main__":
    for f in (sys.stdout, sys.stderr):
        f.reconfigure(encoding="utf-8", errors="replace")
    try:
        main()
    except RuntimeError as e:
        sys.exit(f"refused: {e}")
    except urllib.error.URLError as e:
        sys.exit(f"no answer from the client: {e.reason}")
