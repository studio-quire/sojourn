"""The assistant's cost lab (docs/assistant-cost.md): Sojourn Access with its assistant on the studio relay, every model
call through a tap that keeps each request and its usage, so what a kind of use costs is measured, not guessed.

  python tools/costlab.py start [--at Region/x/y/z] [--model quick|sonnet|opus] [--build]
                                   build if there is none (or --build); start the tap (local/costlab/tap/N) and Sojourn Access on the
                                   hidden desktop as Cost Tester (local grid, agent API :9830, base local/costlab/app,
                                   the assistant's data in local/costlab/data as a packaged build keeps it), the
                                   relay's token `costlab` kept for it in local/costlab/keys
  python tools/costlab.py ask "TEXT" [--wait 300]
                                   a typed request as Alt+9 sends it; prints the reply, then each model call it made
                                   (model, input, cache read and write, output, dollars) and the total
  python tools/costlab.py describe here|me|ID [--rich]
                                   what Control+D sends (the request with its describe key), costed the same way
  python tools/costlab.py capture NAME SUBJECT [--rich] [--of view|profile|texture|item]
                                   SojournDescribe's input for a subject saved to local/costlab/captures/NAME
                                   (look.json, the pictures as PNGs): what describe_lab.py replays
  python tools/costlab.py calls [FIRST [LAST]]
                                   the tap's calls so far (or a range), costed
  python tools/costlab.py parts N  one call's input split by part (tools, system prompt, messages, pictures) by the
                                   API's own token counting through the relay (free)
  python tools/costlab.py sitting NAME   the same ten requests at the describe lab's platform (SITTING), costed
                                   one by one, to local/costlab/sitting-NAME.json
  python tools/costlab.py stop

The token is read where it's kept (tools/relay.py) and never printed or written in the clear. Prices mirror
tools/relay/worker.js PRICES (the relay's own count is the bill). Written by Claude Opus 5.5, 2026-10-31.
"""
import base64
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
sys.path.insert(0, str(ROOT / "tools"))

LAB = ROOT / "local" / "costlab"
BUILD = LAB / "build"
APP_BIN = BUILD / "bin" / "Sojourn.Access" / "Release" / "net10.0-windows10.0.22621.0"
APP_DLL = APP_BIN / "SojournAccess.dll"
BASE = LAB / "app"
DATA = LAB / "data"
KEYS = LAB / "keys"
TAPS = LAB / "tap"
CAPTURES = LAB / "captures"
STATE = LAB / "state.json"
API_PORT, TAP_PORT = 9830, 9897
ACCOUNT = ("Cost", "Tester")
PASSWORD = "sojourn-local"
GRID_URI = "http://127.0.0.1:9700/"
START = "Atelier/128/128/24"
RELAY_HOST = "relay.studioquire.org"

# $ per million tokens: input, output, cache read, 5-minute cache write, 1-hour cache write (tools/relay/worker.js)
PRICES = {
    "claude-haiku-5-5": [0.10, 0.50, 0.01, 0.125, 0.20],
    "claude-haiku-5-5/long": [0.50, 2.50, 0.05, 0.625, 1.00],   # prompts over 100K
    "claude-sonnet-5-5": [2, 10, 0.20, 2.50, 4],
    "claude-sonnet-5": [2, 10, 0.20, 2.50, 4],
    "claude-opus-5-5": [4, 20, 0.20, 5, 8],
}
DEAREST = [10, 50, 1.00, 12.50, 20]


def price_of(model, usage):
    """Dollars for one response's usage, as the relay counts them."""
    m = re.sub(r"\[\w+\]$", "", model or "")
    row = next((v for k, v in sorted(PRICES.items(), key=lambda kv: -len(kv[0])) if "/" not in k and m.startswith(k)), DEAREST)
    cw = usage.get("cache_creation") or {}
    w5 = int(cw.get("ephemeral_5m_input_tokens") or 0)
    w1 = int(cw.get("ephemeral_1h_input_tokens") or 0)
    write = int(usage.get("cache_creation_input_tokens") or 0)
    if not cw:
        w5 = write
    prompt = int(usage.get("input_tokens") or 0) + int(usage.get("cache_read_input_tokens") or 0) + write
    if m.startswith("claude-haiku-5-5") and prompt > 100_000:
        row = PRICES["claude-haiku-5-5/long"]
    return (int(usage.get("input_tokens") or 0) * row[0] + int(usage.get("output_tokens") or 0) * row[1]
            + int(usage.get("cache_read_input_tokens") or 0) * row[2] + w5 * row[3] + w1 * row[4]) / 1e6


def token():
    import relay
    return relay.read_secret(relay.token_file("costlab"))


def state():
    try:
        return json.loads(STATE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def save_state(st):
    LAB.mkdir(parents=True, exist_ok=True)
    STATE.write_text(json.dumps(st, indent=1), encoding="utf-8")


def tap_dir():
    return Path(state()["tap"])


# ---- the app's agent API --------------------------------------------------------------------------------------------

def post(path, body, timeout=30):
    req = urllib.request.Request(f"http://127.0.0.1:{API_PORT}{path}", data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read() or b"null")


def api(pump, op, timeout=15, **args):
    out = post("/cmd", {"pump": pump, "data": {"op": op, **args}, "timeout": timeout}, timeout + 20)
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def wait(f, secs, step=0.5):
    end = time.time() + secs
    while True:
        try:
            got = f()
            if got:
                return got
        except Exception:
            pass
        if time.time() >= end:
            return None
        time.sleep(step)


def log_file():
    logs = sorted((BASE / "logs").glob("*"), key=lambda p: p.stat().st_mtime)
    return logs[-1] if logs else None


def log_text():
    f = log_file()
    return f.read_text(encoding="utf-8", errors="replace") if f else ""


# ---- start and stop ---------------------------------------------------------------------------------------------------

def settings(model, describer):
    BASE.mkdir(parents=True, exist_ok=True)
    path = BASE / "vr-settings.json"
    try:
        st = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        st = {}
    st.update({"assistantProvider": "relay", "assistantAutoMode": True, "assistantModel": model,
               "assistantEffort": {"haiku": "medium", "sonnet": "medium", "opus": "medium"}, "assistantMemory": True})
    if describer:
        st["assistantDescriber"] = describer
    path.write_text(json.dumps(st, indent=2), encoding="utf-8")


def keep_token():
    os.environ["SOJOURN_KEYS"] = str(KEYS)
    sys.path.insert(0, str(ROOT / "assistant"))
    import provider
    provider.save_key("relay", token())


def build(force=False):
    """Only when there's no build yet or asked (--build): others' work in progress under src/ may not compile, and the
    lab measures the assistant (assistant/, read live), not the app."""
    if APP_DLL.exists() and not force:
        return 0
    print("building Sojourn Access into local/costlab/build", flush=True)
    import access
    env_before = os.environ.get("SOJOURN_BUILD_ROOT")
    os.environ["SOJOURN_BUILD_ROOT"] = str(BUILD)
    try:
        return access.dotnet_build(access.APP_PROJECT)
    finally:
        if env_before is None:
            os.environ.pop("SOJOURN_BUILD_ROOT", None)
        else:
            os.environ["SOJOURN_BUILD_ROOT"] = env_before


def start(args):
    import access
    import grid
    at = args[args.index("--at") + 1] if "--at" in args else START
    model = args[args.index("--model") + 1] if "--model" in args else "quick"
    describer = args[args.index("--describer") + 1] if "--describer" in args else None
    if not grid.ready():
        sys.exit("the local grid is down: python tools/grid.py start")
    if state().get("app") and access.alive(state()["app"]):
        sys.exit("already running: python tools/costlab.py stop")
    if build("--build" in args):
        sys.exit("build failed")
    grid.account(*ACCOUNT, PASSWORD)
    keep_token()
    settings(model, describer)
    n = max([int(p.name) for p in TAPS.glob("*") if p.name.isdigit()] or [0]) + 1
    tap = TAPS / str(n)
    tap.mkdir(parents=True)
    tap_log = open(tap / "tap.log", "w", encoding="utf-8")
    tap_proc = subprocess.Popen([sys.executable, str(ROOT / "tools" / "model_tap.py"), str(TAP_PORT), str(tap)],
                                env=dict(os.environ, SOJOURN_TAP_UPSTREAM=RELAY_HOST), stdout=tap_log, stderr=subprocess.STDOUT,
                                stdin=subprocess.DEVNULL, creationflags=access.CREATE_NO_WINDOW | access.BELOW_NORMAL)
    region, x, y, z = at.split("/")
    env = dict(os.environ, SOJOURN_KEYS=str(KEYS), SOJOURN_LOCAL=str(DATA),
               SOJOURN_PROVIDER_UPSTREAM=f"http://127.0.0.1:{TAP_PORT}", SOJOURN_TEST_NO_NVDA="1",
               SOJOURN_TEST_NO_CLIPBOARD="1", SOJOURN_LOG_REPLIES="1")
    env.pop("SOJOURN_BUILD_ROOT", None)
    app_args = ["--grid", GRID_URI, "--user", " ".join(ACCOUNT), "--password", PASSWORD,
                "--start", f"uri:{region}&{x}&{y}&{z}", "--port", API_PORT, "--base", BASE, "--reader", "on",
                "--autologin", "--no-voice"]
    app = access.launch_hidden([access.DOTNET, APP_DLL] + app_args, env=env, cwd=APP_BIN)
    save_state({"app": app, "tap_pid": tap_proc.pid, "tap": str(tap), "started": time.time()})
    def connected():
        with urllib.request.urlopen(f"http://127.0.0.1:{API_PORT}/status", timeout=5) as r:
            return json.loads(r.read()).get("connected")
    if not wait(connected, 150, 1):
        sys.exit("not logged in after 150 s: " + str(log_file()))
    up = wait(lambda: "session up" in log_text(), 120, 1)
    print(f"logged in as {' '.join(ACCOUNT)} at {at}; assistant {'up' if up else 'not up after 120 s'}; tap {tap}")


def stop():
    import access
    st = state()
    for k in ("app", "tap_pid"):
        if st.get(k):
            access.kill(st[k])
    save_state({**st, "app": None, "tap_pid": None})
    print("stopped")


# ---- requests and their cost ----------------------------------------------------------------------------------------

def calls(first=1, last=None):
    out = []
    d = tap_dir()
    for f in sorted(d.glob("*.usage.json")):
        n = int(f.name.split(".")[0])
        if n < first or (last and n > last):
            continue
        u = json.loads(f.read_text(encoding="utf-8"))
        try:
            body = json.loads((d / f"{n:05d}.json").read_text(encoding="utf-8").split("\n", 1)[1])
        except (OSError, ValueError, IndexError):
            body = {}
        out.append({"n": n, "model": u.get("model"), "usage": u.get("usage") or {}, "stop": u.get("stop"),
                    "usd": price_of(u.get("model"), u.get("usage") or {}), "what": what_call(body)})
    return out


def what_call(body):
    """A call's kind, roughly: a session's turn, auto mode's classifier, a background helper, a lean description."""
    system = body.get("system")
    stext = system if isinstance(system, str) else " ".join(b.get("text", "") for b in system or [] if isinstance(b, dict))
    tools = len(body.get("tools") or [])
    if "describer" in stext[:400].lower() and not tools:
        return "describe"
    if tools > 20:
        return f"session ({tools} tools, {len(body.get('messages') or [])} msgs)"
    if "classifier" in stext.lower() or "security monitor" in stext.lower() or "auto mode" in stext[:2000].lower():
        return f"classifier ({tools} tools)"
    return f"other ({tools} tools): {stext[:60]!r}"


def show(cs):
    total = 0.0
    for c in cs:
        u = c["usage"]
        cw = u.get("cache_creation") or {}
        print(f"  #{c['n']:<4} {re.sub(r'^claude-', '', c['model'] or '?'):<14} in {u.get('input_tokens', 0):>6}  "
              f"read {u.get('cache_read_input_tokens', 0):>7}  write {u.get('cache_creation_input_tokens', 0):>6}"
              f"{' (1h)' if cw.get('ephemeral_1h_input_tokens') else ''}  out {u.get('output_tokens', 0):>5}  "
              f"${c['usd']:.5f}  {c['what']}")
        total += c["usd"]
    print(f"  total ${total:.5f} over {len(cs)} calls")
    return total


def quiet(secs=4.0, limit=600):
    """Until the tap has had nothing in flight for secs."""
    d = tap_dir()
    end = time.time() + limit
    while time.time() < end:
        try:
            s = json.loads((d / "status.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            time.sleep(0.5)
            continue
        if s["inflight"] == 0 and time.time() - s["last"] > secs:
            return
        time.sleep(0.5)


def last_n():
    return max([int(p.name.split(".")[0]) for p in tap_dir().glob("*.json") if p.name[:5].isdigit()] or [0])


def replies():
    return re.findall(r"replied \((\w+)\) to (.+?): (['\"])(.*?)\3\s*$", log_text(), re.M)


def ask(text, secs=300):
    before = len(replies())
    n0 = last_n()
    t0 = time.time()
    post("/event", {"kind": "sojourn", "data": {"what": "typed", "text": text, "target": None}})
    got = wait(lambda: len(replies()) > before, secs, 0.5)
    took = time.time() - t0
    quiet()
    r = replies()[-1] if got else None
    print(f"{took:.1f} s; " + (f"{r[0]}: {r[3]}" if r else "no reply"))
    cs = calls(n0 + 1)
    total = show(cs)
    return {"text": text, "reply": r[3] if r else None, "by": r[0] if r else None, "s": round(took, 1),
            "usd": total, "calls": cs}


def describe(subject, rich=False):
    detail = "rich" if rich else "brief"
    words = {"here": "Describe where I am", "me": "Describe me: how I look and what I'm wearing"}.get(subject, f"Describe {subject}")
    asked = f"{words}, in detail: you've described it briefly already, so add to that." if rich else f"{words}, briefly."
    return ask(f"{asked} [describe key: subject={subject}, of=view, detail={detail}]")


def capture(name, subject, rich=False, of="view"):
    out = CAPTURES / name
    out.mkdir(parents=True, exist_ok=True)
    detail = "rich" if rich else "brief"
    if of == "view":
        r = api("SojournDescribe", "look", 60, subject=subject, detail=detail)
    elif of == "profile":
        r = api("SojournDescribe", "profile", 60, id=subject)
    elif of == "texture":
        r = api("SojournDescribe", "texture", 60, id=subject, max=1024 if rich else 768)
        r = {"pictures": [{"label": "the texture as uploaded", "png": r["png"]}]}
    for i, p in enumerate(r.get("pictures") or [], 1):
        if p.get("png"):
            (out / f"pic{i}.png").write_bytes(base64.b64decode(p.pop("png")))
            p["file"] = f"pic{i}.png"
    r.setdefault("detail", detail)
    r["captured"] = {"subject": subject, "of": of, "at": time.strftime("%Y-%m-%d %H:%M")}
    (out / "look.json").write_text(json.dumps(r, indent=1, ensure_ascii=False), encoding="utf-8")
    print(f"{out}: {len(r.get('pictures') or [])} pictures; " + json.dumps({k: v for k, v in r.items() if k != 'pictures'}, ensure_ascii=False)[:600])


def parts(n):
    """One call's input by part, counted by the API (count_tokens through the relay: free, not counted)."""
    body = json.loads((tap_dir() / f"{n:05d}.json").read_text(encoding="utf-8").split("\n", 1)[1])
    model = re.sub(r"\[\w+\]$", "", body["model"])
    keep = {k: body[k] for k in ("system", "tools", "messages") if k in body}
    # count_tokens takes a message's role and content only (an effort-only system message carries output_config)
    keep["messages"] = [{"role": m["role"], "content": m["content"]} for m in keep.get("messages") or []
                        if m.get("content") not in ([], None, "")]

    def count(b):
        req = urllib.request.Request(f"https://{RELAY_HOST}/v1/messages/count_tokens",
                                     data=json.dumps({"model": model, **b}).encode(),
                                     headers={"Content-Type": "application/json", "x-api-key": token(),
                                              "anthropic-version": "2023-06-01", "User-Agent": "Sojourn costlab"})
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.loads(r.read())["input_tokens"]
        except urllib.error.HTTPError as e:
            raise RuntimeError(f"count_tokens: {e.code} {e.read()[:400]!r}") from None

    hi = [{"role": "user", "content": "hi"}]
    bare = count({"messages": hi})
    tools = count({"tools": keep.get("tools") or [], "messages": hi}) - bare if keep.get("tools") else 0
    system = count({"system": keep.get("system") or "", "messages": hi}) - bare if keep.get("system") else 0
    full = count(keep)

    def no_pictures(x):
        if isinstance(x, list):
            return [no_pictures(i) for i in x if not (isinstance(i, dict) and i.get("type") == "image")]
        if isinstance(x, dict):
            return {k: no_pictures(v) for k, v in x.items()}
        return x
    pics = full - count(no_pictures(keep))
    msgs = full - tools - system - pics
    n_pics = json.dumps(keep.get("messages")).count('"type": "image"')
    print(f"#{n} {model}: {full} tokens in all: tools {tools} ({len(keep.get('tools') or [])}), system prompt {system}, "
          f"messages {msgs} ({len(keep.get('messages') or [])}), pictures {pics} ({n_pics})")
    return {"full": full, "tools": tools, "system": system, "messages": msgs, "pictures": pics}


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    a = sys.argv[1:]
    cmd = a[0] if a else ""
    if cmd == "start":
        start(a[1:])
    elif cmd == "stop":
        stop()
    elif cmd == "ask":
        secs = int(a[a.index("--wait") + 1]) if "--wait" in a else 300
        ask(" ".join(x for x in a[1:] if x not in ("--wait", str(secs))), secs)
    elif cmd == "describe":
        describe(a[1], "--rich" in a)
    elif cmd == "capture":
        capture(a[1], a[2], "--rich" in a, a[a.index("--of") + 1] if "--of" in a else "view")
    elif cmd == "calls":
        show(calls(int(a[1]) if len(a) > 1 else 1, int(a[2]) if len(a) > 2 else None))
    elif cmd == "parts":
        parts(int(a[1]))
    elif cmd == "sitting":
        sitting(a[1])
    else:
        print(__doc__)



# ---- a sitting, the same each time (python tools/costlab.py sitting NAME) -----------------------------------------------

SITTING = [
    ("ask", "In one short sentence: which region am I in?"),
    ("describe", "here", False),
    ("describe", "here", True),
    ("describe", "me", False),
    ("ask", "What does the jazz poster say?"),
    ("ask", "Is anyone near me?"),
    ("ask", "Turn my AO on."),
    ("describe", "Jazz Poster", False),
    ("ask", "What's on the table to my right?"),
    ("ask", "Thanks, that's all."),
]


def sitting(name):
    """The lab's standard sitting at the describe lab's platform (describe_lab.py scene), each step costed; saved to
    local/costlab/sitting-NAME.json."""
    api("LLAgent", "lookAt", position=[200, 210, 91])
    time.sleep(2)
    ids = {o["name"]: o["id"] for o in api("TesselObjects", "nearby", 10, dist=30.0, limit=100).get("objects", [])}
    out = []
    for step in SITTING:
        if step[0] == "ask":
            r = ask(step[1])
        else:
            r = describe(ids.get(step[1], step[1]), step[2])
        r["step"] = list(step)
        out.append(r)
    total = sum(r["usd"] for r in out)
    print(f"sitting {name}: ${total:.4f} over {len(out)} requests, {sum(len(r['calls']) for r in out)} model calls")
    (LAB / f"sitting-{name}.json").write_text(json.dumps(out, indent=1, default=str), encoding="utf-8")


if __name__ == "__main__":
    main()
