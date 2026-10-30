"""The Sojourn launcher (src/Sojourn.Launcher, docs/launcher.md): build it, run it, draw its states, check it.

  python tools/launcher.py build            dotnet build -c Release (seconds; the VR client builds by tools/client.py)
  python tools/launcher.py run [args...]    start it for the person (the foreground, no console), base dir local/main;
                                            args: --base-dir DIR, --desktop, and after -- the client's own
  python tools/launcher.py shots DIR        its states drawn offscreen to PNGs over made-up accounts (no window)
  python tools/launcher.py check            the grid's questions answered at the launcher, on a hidden desktop against
                                            the local grid (the grid's ask faked by SOJOURN_FAKE_LOGIN_ASK); below

Run as `dotnet Sojourn.dll` (the launcher is Sojourn.exe; the VR client it starts is SojournVR) because the apphost
can't find .NET here (the SDK isn't a global install), as tools/access.py does for Sojourn Access.

check: the launcher and the desktop client it starts run on a hidden Win32 desktop of their own ("sojourn-launcher"),
driven by QuireUia's UI Automation there (tools/quireuia, local/bin/QuireUia: `python tools/access.py tools` builds
it), so nothing reaches the person's screen, focus or keys. First RealPress itself in a WPF window of inputcheck's
there (tools/inputcheck -wpf: presses given to its judge as the hooks would, counting while WPF handles them, once;
posted keys and characters without them refused). Then four runs as Sojourn Launcher on the local grid, in a scratch
base dir and logins folder:
1. MFA, presses not from a person, the launcher in front on that desktop (its hooks on), every judgement traced
   (SOJOURN_INPUT_LOG): a key set into the key box by UI Automation, or typed there by posted characters and Enter,
   isn't kept; Enter posted in the code box, a click posted on Continue and Continue invoked by UI Automation are
   refused; nothing in the trace counted.
2. MFA with SOJOURN_TEST_INPUT=1 (as the client's tests: posted and automated presses count): the code goes through the
   pipe, the client logs in, says the code came from the launcher, never the code itself.
3. Terms of Service with SOJOURN_TEST_INPUT=1: I agree, Continue, in world.
4. MFA again with a lost GPU faked once in world: the restarted client (a copy of the pipe handed on) asks again and
   takes the launcher's answer too.
What it can't check: a real hand's press passing RealPress (that's the person's), SendInput (it only reaches the
desktop the person's input goes to; the hooks' injected flags are Windows' own), and the headset.
"""
import json
import os
import shutil
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
PROJECT = ROOT / "src" / "Sojourn.Launcher" / "Sojourn.Launcher.csproj"
DLL = ROOT / "src" / "Sojourn.Launcher" / "bin" / "Release" / "net10.0-windows" / "Sojourn.dll"
QUIREUIA = ROOT / "local" / "bin" / "QuireUia" / "QuireUia.exe"
INPUTCHECK = ROOT / "tools" / "inputcheck"
BELOW_NORMAL = 0x00004000
CREATE_NO_WINDOW = 0x08000000


def build():
    return subprocess.run([str(DOTNET), "build", str(PROJECT), "-c", "Release", "-v:q", "-nologo"],
                          creationflags=BELOW_NORMAL).returncode


def run(args):
    if not DLL.exists() and build() != 0:
        return 1
    env = {**os.environ, "SOJOURN_FOREGROUND": "1"}   # the person's: the launcher and the client it starts take focus
    p = subprocess.Popen([str(DOTNET), str(DLL), *args], cwd=ROOT, env=env, stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=CREATE_NO_WINDOW)
    print(f"the launcher started (pid {p.pid})")
    return 0


def shots(out):
    if build() != 0:
        return 1
    return subprocess.run([str(DOTNET), str(DLL), "--shots", str(Path(out).resolve())], cwd=ROOT).returncode


# ---- check ----------------------------------------------------------------------------------------------------------

DESKTOP = "sojourn-launcher"
Q_PORT = 9907
TEST = ROOT / "local" / "launcher-check"
fails = 0


def check(ok, what, detail=""):
    global fails
    fails += not ok
    print(f"{'ok  ' if ok else 'FAIL'} {what}{f' ({detail})' if detail else ''}", flush=True)
    return ok


def u(line, timeout=30):
    with socket.create_connection(("127.0.0.1", Q_PORT), timeout=timeout) as s:
        s.sendall((line + "\n").encode("utf-8"))
        chunks = []
        while b := s.recv(65536):
            chunks.append(b)
    reply = b"".join(chunks).decode("utf-8", errors="replace").rstrip("\n")
    if reply.startswith("ERR"):
        raise RuntimeError(f"{line[:60]}: {reply}")
    return reply


def wait(f, secs, step=0.3):
    end = time.time() + secs
    while True:
        try:
            if got := f():
                return got
        except Exception:
            pass
        if time.time() >= end:
            return None
        time.sleep(step)


def launch_hidden(args, env=None):
    r = subprocess.run([str(QUIREUIA), "launch", "--desktop", DESKTOP, "--cwd", str(ROOT), "--"] + [str(a) for a in args],
                       env=env, capture_output=True, text=True, timeout=90, creationflags=CREATE_NO_WINDOW)
    if r.returncode != 0 or not r.stdout.strip().split()[0].isdigit():
        raise RuntimeError(f"launch: {r.stdout.strip()} {r.stderr.strip()}")
    return int(r.stdout.strip().split()[0])


def kill_tree(pid):
    subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True, creationflags=CREATE_NO_WINDOW)


def kill_ours():
    """Anything left with the check's folder in its command line (a restarted client isn't in the launcher's tree)."""
    ps = (f"Get-CimInstance Win32_Process | Where-Object {{ $_.CommandLine -like '*{TEST}*' -and $_.Name -notin 'powershell.exe','QuireUia.exe' }} "
          "| ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }")
    subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, creationflags=CREATE_NO_WINDOW)


def state(base):
    try:
        return json.loads((base / "launcher-state.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def client_log(base):
    logs = sorted((base / "logs").glob("*.log"), key=lambda p: p.stat().st_mtime) if (base / "logs").exists() else []
    return "\n".join(p.read_text(encoding="utf-8", errors="replace") for p in logs)


def one_run(n, ask, test_input, restart=False):
    base = TEST / f"run{n}"
    env = {**os.environ, "SOJOURN_LOGINS": str(TEST / "logins"), "SOJOURN_KEYS": str(TEST / "keys"),
           "SOJOURN_FAKE_LOGIN_ASK": ask, "SOJOURN_BACKGROUND": "1"}
    if restart:   # a lost GPU faked 8 s into the first region: the client starts itself again and asks again
        env["SOJOURN_FAKE_DEVICE_LOST"] = "8"
    env.pop("SOJOURN_FOREGROUND", None)
    trace = TEST / f"run{n}-input.log"
    if test_input:
        env["SOJOURN_TEST_INPUT"] = "1"
    else:
        env.pop("SOJOURN_TEST_INPUT", None)
        env["SOJOURN_INPUT_LOG"] = str(trace)
    pid = launch_hidden([DOTNET, DLL, "--base-dir", base, "--desktop", "--", "--no-assistant", "--no-voice", "--no-media",
                         "--minutes", "3"], env=env)
    try:
        u(f"watch {pid}")
        check(bool(wait(lambda: u("find name=Launch;type=Button"), 30)), f"run {n}: the launcher is up on the hidden desktop")
        if not test_input:
            # in front on its desktop, as for the person: the hooks on, and WPF takes posted keys only in the focused window
            u(f"activate {pid}")
            # a key put into the key box by UI Automation (not a person's keys): not kept
            u("invoke name=Settings")
            u("select name=Anthropic API;type=RadioButton")   # (a Claude plan has no key box)
            try:
                u("set name=API key | sk-ant-api03-DECOYDECOYDECOY" + os.urandom(6).hex())
            except RuntimeError as e:
                print(f"     (the key box took no value: {e})")
            time.sleep(1)
            check(not (TEST / "keys").exists() or not any((TEST / "keys").iterdir()),
                  f"run {n}: a key set into the key box by UI Automation isn't kept")
            # typed there by posted characters, then a posted Enter: not kept either
            u("setfocus name=API key")
            u("chars name=API key | sk-ant-api03-POSTEDPOSTED" + os.urandom(6).hex())
            u("key name=API key | 13")
            time.sleep(1)
            check(not (TEST / "keys").exists() or not any((TEST / "keys").iterdir()),
                  f"run {n}: a key typed into the key box by posted characters and Enter isn't kept")
            u("select name=Claude plan;type=RadioButton")
            u("invoke name=Back")
        try:   # a new account (the form), unless an earlier run's login saved it
            u("set name=Username;type=Edit | Sojourn Launcher")
            u("select name=other;type=RadioButton")
            u("set name=Login URI;type=Edit | http://127.0.0.1:9700/")
        except RuntimeError:
            pass
        u("set name=Password | sojourn-local")
        u("invoke name=Launch;type=Button")
        want = "code" if ask == "mfa" else "terms"
        st = wait(lambda: (s := state(base)).get("state") == want and s.get("question") is not None and s, 90)
        check(bool(st) and st.get("answers") is True, f"run {n}: the client asks for the {want} and takes answers from the launcher",
              json.dumps({k: st.get(k) for k in ("state", "answers")}) if st else json.dumps(state(base))[:200])
        if not st:
            return
        check(bool(wait(lambda: u("find name=Continue;type=Button"), 10)), f"run {n}: the question is in the launcher's card")
        if ask == "mfa":
            u("set name=Authenticator code;type=Edit | 314159")
            if not test_input:
                u("setfocus name=Authenticator code;type=Edit")
                u("key name=Authenticator code;type=Edit | 13")
                time.sleep(3)
                check(state(base).get("state") == "code", "run 1: Enter posted in the code box is refused (the client still waits)")
                u("mouse name=Continue;type=Button")
                time.sleep(3)
                check(state(base).get("state") == "code", "run 1: a click posted on Continue is refused (the client still waits)")
                u("invoke name=Continue;type=Button")
                time.sleep(4)
                check(state(base).get("state") == "code", "run 1: Continue pressed by UI Automation is refused (the client still waits)")
                lines = trace.read_text(encoding="utf-8").splitlines() if trace.exists() else []
                judged = [l for l in lines if not l.split(" ", 1)[-1].startswith("hook:")]
                check(any("key down 0x0D" in l for l in judged) and any(" char " in l or " button " in l for l in judged)
                      and not any(l.endswith(" counts") for l in judged),
                      f"run 1: the trace judged the posted keys, characters and click, and nothing counted",
                      f"{len(judged)} judged, {sum(l.endswith(' counts') for l in judged)} counted")
                check(any("hook: keys on" in l for l in lines), "run 1: the hooks came on with the launcher in front")
                return
            u("invoke name=Continue;type=Button")
        else:
            u("toggle name=I agree")
            u("invoke name=Continue;type=Button")
        arrived = wait(lambda: state(base).get("state") == "world", 90)
        check(bool(arrived), f"run {n}: answered at the launcher, the client got in world", state(base).get("state", "?"))
        log = client_log(base)
        said = "gave a code at the launcher" if ask == "mfa" else "Terms of Service at the launcher"
        check(said in log, f"run {n}: the client's log says the answer came from the launcher")
        if ask == "mfa":
            check("314159" not in log and "314159" not in (base / "logs" / "launcher-console.log").read_text(errors="replace"),
                  f"run {n}: the code is in no log")
        if restart:
            first = state(base).get("pid")
            st = wait(lambda: (s := state(base)).get("pid") != first and s.get("state") == "code" and s.get("question") is not None and s, 120)
            check(bool(st) and st.get("answers") is True, f"run {n}: restarted after the lost GPU, the new run takes answers from the launcher too",
                  json.dumps({k: (st or state(base)).get(k) for k in ("pid", "state", "answers")}))
            if st:
                wait(lambda: u("find name=Authenticator code;type=Edit"), 10)
                u("set name=Authenticator code;type=Edit | 271828")
                u("invoke name=Continue;type=Button")
                check(bool(wait(lambda: state(base).get("state") == "world", 90)), f"run {n}: answered again, the restarted run got in world",
                      state(base).get("state", "?"))
    finally:
        try:
            u("invoke name=Stop")
            wait(lambda: state(base).get("state") in ("ending", None) or not state(base), 10)
        except Exception:
            pass
        time.sleep(2)
        kill_tree(pid)
        kill_ours()


def wpf_check():
    """tools/inputcheck -wpf on the hidden desktop: RealPress as WPF handles input (it refuses to run anywhere else)."""
    if subprocess.run([str(DOTNET), "build", str(INPUTCHECK), "-c", "Release", "-v:q", "-nologo"], creationflags=BELOW_NORMAL).returncode != 0:
        return check(False, "inputcheck builds")
    out = TEST / "inputcheck-wpf.txt"
    dll = INPUTCHECK / "bin" / "Release" / "net10.0-windows" / "inputcheck.dll"
    launch_hidden([DOTNET, dll, "-wpf", out])
    done = wait(lambda: out.exists() and any(w in out.read_text(encoding="utf-8") for w in ("passed", "failed")), 60)
    text = out.read_text(encoding="utf-8") if out.exists() else ""
    for line in text.splitlines():
        if line.startswith(("ok", "FAIL")):
            check(line.startswith("ok"), line.split(" ", 1)[1].strip())
    return check(bool(done) and "all passed" in text, "RealPress in a WPF window on the hidden desktop", text.splitlines()[-1] if text else "no result")


def check_all():
    if not QUIREUIA.exists():
        sys.exit("missing local/bin/QuireUia (python tools/access.py tools)")
    if subprocess.run([sys.executable, str(ROOT / "tools" / "grid.py"), "status"], capture_output=True, text=True).stdout.strip() != "up":
        sys.exit("the local grid is down (python tools/grid.py start)")
    subprocess.run([sys.executable, str(ROOT / "tools" / "grid.py"), "account", "Sojourn", "Launcher", "sojourn-local"], capture_output=True)
    if build() != 0 or subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"]).returncode != 0:
        return 1
    shutil.rmtree(TEST, ignore_errors=True)
    TEST.mkdir(parents=True)
    server = launch_hidden([QUIREUIA, "serve", "--port", Q_PORT, "--log", TEST / "events.jsonl"])
    try:
        if not check(bool(wait(lambda: u("help", timeout=5), 20)), "QuireUia's server answers on the hidden desktop"):
            return 1
        wpf_check()
        one_run(1, "mfa", test_input=False)
        one_run(2, "mfa", test_input=True)
        one_run(3, "tos", test_input=True)
        one_run(4, "mfa", test_input=True, restart=True)
    finally:
        try:
            u("quit", timeout=5)
        except Exception:
            pass
        time.sleep(1)
        kill_tree(server)
    print("all as they should be" if not fails else f"{fails} failed")
    return 1 if fails else 0


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "run"
    if cmd == "build":
        sys.exit(build())
    if cmd == "run":
        sys.exit(run(sys.argv[2:]))
    if cmd == "shots" and len(sys.argv) > 2:
        sys.exit(shots(sys.argv[2]))
    if cmd == "check":
        sys.exit(check_all())
    sys.exit(__doc__)
