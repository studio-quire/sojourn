"""Studio Quire's relay for Sojourn Access's assistant (tools/relay/, docs/assistant.md > The studio relay): deploy it,
put the studio's key in, make and manage personal tokens, read their spending, and check it end to end.

  python tools/relay.py deploy             wrangler deploy from tools/relay (relay.studioquire.org)
  python tools/relay.py key                the studio's Anthropic key into the Worker as its ANTHROPIC_KEY secret:
                                           decrypted here and piped to wrangler, never printed, written or on a
                                           command line
  python tools/relay.py token new NAME [--cap 50]
                                           a new personal token: kept encrypted (DPAPI) in
                                           local/secrets/studio/relay-token-NAME.key, its hash and cap sent to the
                                           Worker (the TOKENS secret); a token is never printed
  python tools/relay.py token cap NAME DOLLARS   change a token's monthly cap
  python tools/relay.py token revoke NAME  the token refused from now on, its files deleted
  python tools/relay.py token copy NAME    the token onto the clipboard, for handing over (never printed)
  python tools/relay.py tokens             the tokens, their caps and this month's spending
  python tools/relay.py check              end to end against the live Worker: a bad token refused, a short request
                                           and a streamed one answered and counted, count_tokens, and a cap tripping
                                           (a token of its own with a cap of a millionth of a dollar, revoked after);
                                           then the logs: one sent, listed, fetched and deleted, and what's refused
  python tools/relay.py check --local      the parts that don't need Anthropic (tokens, usage, the logs and their
                                           limits) against this folder's Worker under `wrangler dev` on loopback, with
                                           made-up secrets and its own storage in a temporary folder: before a deploy
  python tools/relay.py admin [--new]      the studio's admin secret, for reading the logs: made once (kept encrypted
                                           in local/secrets/studio/relay-admin.key; --new replaces it), its SHA-256 put
                                           into the Worker as its ADMIN secret; never printed
  python tools/relay.py logs               the logs testers sent (Sojourn Access's Help > Send a log), newest first:
                                           id, token, when, size, the session, the start of the note
  python tools/relay.py logs fetch [ID ...|--all]   saved as local/relay-logs/<token>-<id>.txt (the note and the log;
                                           --all: every one not saved yet)
  python tools/relay.py logs delete ID     one deleted from the relay (they go by themselves after 30 days)

The registry (local/secrets/studio/relay-tokens.json) holds names, hashes and caps, never tokens. Tokens and the key
are DPAPI-encrypted for this Windows account in the format PowerShell's ConvertFrom-SecureString writes (hex, UTF-16),
so `ConvertTo-SecureString (Get-Content FILE)` reads them too.
Written by Claude Opus 5.5, 2026-10-30.
"""
import ctypes
import gzip
import hashlib
import json
import os
import re
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from ctypes import wintypes
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKER = ROOT / "tools" / "relay"
SECRETS = ROOT / "local" / "secrets" / "studio"
KEY_FILE = SECRETS / "relay-anthropic.key"
ADMIN_FILE = SECRETS / "relay-admin.key"
REGISTRY = SECRETS / "relay-tokens.json"
LOGS_DIR = ROOT / "local" / "relay-logs"
URL = "https://relay.studioquire.org"
DEFAULT_CAP = 50.0
CHECK_TOKEN = "relay-check"
TOKEN_SHAPE = re.compile(r"sqr-[A-Za-z0-9_\-]{40,64}")   # assistant/provider.py and ProviderKeys.cs know it too


# ---- DPAPI, as ConvertFrom-SecureString: the current user's, no entropy, UTF-16LE, hex ----------------------------------

class _Blob(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]


def _dpapi(data: bytes, protect: bool) -> bytes:
    crypt32, kernel32 = ctypes.windll.crypt32, ctypes.windll.kernel32
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    src = ctypes.create_string_buffer(data, len(data))
    b_in = _Blob(len(data), ctypes.cast(src, ctypes.POINTER(ctypes.c_char)))
    out = _Blob()
    fn = crypt32.CryptProtectData if protect else crypt32.CryptUnprotectData
    ok = fn(ctypes.byref(b_in), None, None, None, None, 1, ctypes.byref(out))
    ctypes.memset(src, 0, len(data))
    if not ok:
        raise OSError(f"data protection failed ({ctypes.GetLastError()})")
    try:
        return ctypes.string_at(out.pbData, out.cbData)
    finally:
        kernel32.LocalFree(ctypes.cast(out.pbData, ctypes.c_void_p))


def read_secret(path: Path) -> str:
    return _dpapi(bytes.fromhex(path.read_text(encoding="ascii").strip()), protect=False).decode("utf-16-le")


def write_secret(path: Path, text: str):
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(_dpapi(text.encode("utf-16-le"), protect=True).hex() + "\r\n", encoding="ascii")
    tmp.replace(path)


def token_file(name):
    return SECRETS / f"relay-token-{name}.key"


# ---- wrangler ---------------------------------------------------------------------------------------------------------

def wrangler(*args, stdin=None):
    npx = shutil.which("npx")
    if not npx:
        raise SystemExit("npx isn't on PATH (Node.js)")
    r = subprocess.run([npx, "--no-install", "wrangler", *args], cwd=WORKER, input=stdin, capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    out = (r.stdout + r.stderr).strip()
    if r.returncode:
        raise SystemExit(f"wrangler {' '.join(args)} failed:\n{out[-2000:]}")
    return out


def put_secret(name, value):
    """A secret into the Worker through wrangler's stdin, so it's never on a command line or in a file."""
    out = wrangler("secret", "put", name, stdin=value)
    if "Success" not in out:
        raise SystemExit(f"wrangler didn't confirm the {name} secret:\n{out[-800:]}")


# ---- the registry ------------------------------------------------------------------------------------------------------

def registry():
    try:
        return json.loads(REGISTRY.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def save_registry(reg):
    REGISTRY.parent.mkdir(parents=True, exist_ok=True)
    REGISTRY.write_text(json.dumps(reg, indent=1), encoding="utf-8")


def push_tokens(reg):
    put_secret("TOKENS", json.dumps({t["hash"]: {"name": n, "cap": t["cap"]} for n, t in reg.items()}))


def new_token(name, cap):
    if not re.fullmatch(r"[a-z0-9][a-z0-9\-]{0,30}", name):
        raise SystemExit("a token's name: lowercase letters, digits and dashes (it names files and shows in the Worker)")
    reg = registry()
    if name in reg:
        raise SystemExit(f"{name} has a token already (revoke it first)")
    token = "sqr-" + secrets.token_urlsafe(32)
    assert TOKEN_SHAPE.fullmatch(token)
    write_secret(token_file(name), token)
    reg[name] = {"hash": hashlib.sha256(token.encode()).hexdigest(), "cap": cap, "made": time.strftime("%Y-%m-%d")}
    save_registry(reg)
    push_tokens(reg)
    return token


def revoke(name):
    reg = registry()
    if reg.pop(name, None) is None:
        raise SystemExit(f"no token called {name}")
    push_tokens(reg)
    save_registry(reg)
    token_file(name).unlink(missing_ok=True)


# ---- talking to the relay -----------------------------------------------------------------------------------------------

def call(method, path, token, body=None, headers=None, timeout=60, raw=None):
    """(status, parsed JSON or text, raw bytes). raw: bytes sent as they are (a gzipped log)."""
    data = raw if raw is not None else json.dumps(body).encode() if body is not None else None
    h = {"x-api-key": token, "anthropic-version": "2023-06-01", "User-Agent": "sojourn-relay-check", **(headers or {})}
    if data is not None and "content-type" not in h:
        h["content-type"] = "application/json"
    req = urllib.request.Request(URL + path, data=data, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw, status = r.read(), r.status
    except urllib.error.HTTPError as e:
        raw, status = e.read(), e.code
    try:
        return status, json.loads(raw), raw
    except ValueError:
        return status, raw.decode("utf-8", "replace"), raw


def usage(token):
    status, u, _ = call("GET", "/relay/usage", token)
    return u if status == 200 and isinstance(u, dict) else None


def wait_until(pred, secs=40, step=2):
    end = time.time() + secs
    while time.time() < end:
        if pred():
            return True
        time.sleep(step)
    return False


# ---- clipboard (Win32), for handing a token over without printing it -----------------------------------------------------

def to_clipboard(text):
    u32, k32 = ctypes.windll.user32, ctypes.windll.kernel32
    k32.GlobalAlloc.restype = ctypes.c_void_p
    k32.GlobalLock.restype = ctypes.c_void_p
    k32.GlobalLock.argtypes = [ctypes.c_void_p]
    k32.GlobalUnlock.argtypes = [ctypes.c_void_p]
    u32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
    data = text.encode("utf-16-le") + b"\0\0"
    for _ in range(10):
        if u32.OpenClipboard(None):
            break
        time.sleep(0.05)
    else:
        raise SystemExit("the clipboard is busy")
    try:
        u32.EmptyClipboard()
        h = k32.GlobalAlloc(0x0002, len(data))
        p = k32.GlobalLock(h)
        ctypes.memmove(p, data, len(data))
        k32.GlobalUnlock(h)
        u32.SetClipboardData(13, h)
    finally:
        u32.CloseClipboard()


# ---- commands ---------------------------------------------------------------------------------------------------------------

def cmd_key():
    key = read_secret(KEY_FILE).strip()
    if not re.fullmatch(r"sk-ant-[A-Za-z0-9_\-]{16,300}", key):
        raise SystemExit(f"{KEY_FILE.name} doesn't hold an Anthropic key")
    put_secret("ANTHROPIC_KEY", key)
    print("the studio's key is in the Worker (ANTHROPIC_KEY)")


def cmd_tokens():
    reg = registry()
    if not reg:
        print("no tokens")
    for name, t in reg.items():
        f = token_file(name)
        u = usage(read_secret(f)) if f.exists() else None
        spent = f"${u['spent_usd']:.4f} of ${u['cap_usd']:g} in {u['month']}, {u['requests']} requests" if u else "usage unavailable"
        print(f"{name}: cap ${t['cap']:g} a month, made {t['made']}; {spent}")


def cmd_check():
    passed, failed = 0, []

    def check(what, ok, detail=""):
        nonlocal passed
        print(f"{'ok  ' if ok else 'FAIL'} {what}: {detail}")
        if ok:
            passed += 1
        else:
            failed.append(what)
        return ok

    if CHECK_TOKEN in registry():
        revoke(CHECK_TOKEN)
    s, body, _ = call("GET", "/relay/usage", "sqr-" + "x" * 43)
    check("a token it doesn't know is refused", s == 401, f"{s} {body}")
    s, body, _ = call("POST", "/v1/messages", "", {"model": "claude-haiku-5-5", "max_tokens": 5, "messages": []})
    check("no token is refused", s == 401, f"{s}")
    s, _, _ = call("POST", "/v1/complete", "sqr-" + "x" * 43, {})
    check("what it doesn't relay is a 404", s == 404, f"{s}")

    token = new_token(CHECK_TOKEN, 0.000001)   # a millionth of a dollar: one short request goes over it
    try:
        if not check("the new token is taken (the TOKENS secret live)", wait_until(lambda: usage(token) is not None, 60),
                     "its usage answers"):
            raise SystemExit
        u0 = usage(token)
        check("a fresh token has spent nothing", u0["spent_usd"] == 0 and u0["cap_usd"] == 0.000001, json.dumps(u0))
        s, body, _ = call("POST", "/v1/messages/count_tokens", token,
                          {"model": "claude-haiku-5-5", "messages": [{"role": "user", "content": "Hello"}]})
        check("count_tokens answers", s == 200 and isinstance(body, dict) and body.get("input_tokens", 0) > 0, f"{s} {body}")
        s, body, _ = call("GET", "/v1/models?limit=1", token)
        check("the model list answers", s == 200 and isinstance(body, dict) and body.get("data"), f"{s}")
        s, body, _ = call("POST", "/v1/messages", token, {"model": "claude-haiku-5-5", "max_tokens": 16,
                          "messages": [{"role": "user", "content": "Say just: ok"}]})
        check("a short request is answered", s == 200 and isinstance(body, dict) and body.get("usage"),
              f"{s} usage {body.get('usage') if isinstance(body, dict) else body}")
        counted = wait_until(lambda: (usage(token) or {}).get("requests", 0) >= 1, 20, 1)
        u1 = usage(token)
        check("and counted", counted and u1["spent_usd"] > 0, json.dumps(u1))
        s, body, _ = call("POST", "/v1/messages", token, {"model": "claude-haiku-5-5", "max_tokens": 16,
                          "messages": [{"role": "user", "content": "Say just: ok"}]})
        check("over its cap, the next is refused with a plain sentence", s == 402 and isinstance(body, dict)
              and "allowance" in body.get("error", {}).get("message", ""), f"{s} {body}")
        # a streamed request, the way Claude Code asks: the cap raised for it, then the stream's usage counted
        reg = registry()
        reg[CHECK_TOKEN]["cap"] = 0.05
        save_registry(reg)
        push_tokens(reg)
        wait_until(lambda: (usage(token) or {}).get("cap_usd") == 0.05, 60)
        before = usage(token)["spent_usd"]
        status, _, raw = call("POST", "/v1/messages?beta=true", token, {"model": "claude-haiku-5-5", "max_tokens": 32,
                              "stream": True, "messages": [{"role": "user", "content": "Count to five."}]})
        text = raw.decode("utf-8", "replace")
        check("a streamed request is answered as a stream", status == 200 and "event: message_stop" in text,
              f"{status}, {text.count('event: ')} events")
        counted = wait_until(lambda: (usage(token) or {})["spent_usd"] > before, 20, 1)
        u2 = usage(token)
        check("and its usage counted", counted and u2["requests"] >= 2, json.dumps(u2))
        if check("the admin secret is made (relay.py admin)", ADMIN_FILE.exists(), str(ADMIN_FILE.relative_to(ROOT))):
            log_checks(check, token, CHECK_TOKEN, read_secret(ADMIN_FILE), day_limit=False)
    finally:
        revoke(CHECK_TOKEN)
    refused = wait_until(lambda: call("GET", "/relay/usage", token)[0] == 401, 60)
    check("a revoked token is refused", refused, "401" if refused else "still answered")
    print(f"\n{passed} of {passed + len(failed)} passed" + (f"; failed: {', '.join(failed)}" if failed else ""))
    return 0 if not failed else 1


# ---- the logs testers send ------------------------------------------------------------------------------------------------

def packed(doc):
    return gzip.compress(json.dumps(doc).encode())


def admin_call(method, path, secret, timeout=60):
    return call(method, path, "", headers={"authorization": f"Bearer {secret}"}, timeout=timeout)


def log_checks(check, token, name, secret, day_limit):
    """The log endpoints as Sojourn Access and relay.py use them; every log sent here is deleted again. day_limit: also
    send until the day's limit refuses one (ten logs: for the local check)."""
    gz = {"content-type": "application/gzip"}
    doc = {"v": 1, "app": "Sojourn Access check", "session": "2026-01-02 10:00:00", "note": "a check's note: nothing happened",
           "log": "Sojourn Access session log, as sent\n10:00:01 info: sojourn[0] a check's line\n"}
    sent = []
    try:
        s, body, _ = call("POST", "/relay/log", token, raw=packed(doc), headers=gz)
        ok = check("a log is taken", s == 200 and isinstance(body, dict) and body.get("id"), f"{s} {body}")
        lid = body.get("id") if ok else None
        if lid:
            sent.append(lid)
        s, listed, _ = admin_call("GET", "/relay/admin/logs", secret)
        mine = [l for l in (listed.get("logs", []) if isinstance(listed, dict) else []) if l.get("id") == lid]
        check("the admin lists it with its token, app, session and note", s == 200 and mine and mine[0].get("token") == name
              and mine[0].get("note") == doc["note"] and mine[0].get("session") == doc["session"], f"{s} {mine}")
        s, _, raw = admin_call("GET", f"/relay/admin/logs/{lid}", secret)
        try:
            back = json.loads(gzip.decompress(raw))
        except (OSError, ValueError):
            back = None
        check("and fetches it as sent", s == 200 and back == doc, f"{s}")
        s, _, _ = admin_call("GET", "/relay/admin/logs", "not-the-secret")
        check("a wrong admin secret is refused", s == 401, f"{s}")
        s, _, _ = admin_call("GET", "/relay/admin/logs", token)
        check("a token isn't the admin", s == 401, f"{s}")
        s, _, _ = call("GET", "/relay/admin/logs", token)
        check("nor is a token as x-api-key", s == 401, f"{s}")
        s, _, _ = call("POST", "/relay/log", "sqr-" + "x" * 43, raw=packed(doc), headers=gz)
        check("a log from a token it doesn't know is refused", s == 401, f"{s}")
        for what, raw_body, want in [
            ("a log not gzipped", json.dumps(doc).encode(), 400),
            ("gzipped text that isn't JSON", gzip.compress(b"hello"), 400),
            ("JSON with no log in it", packed({"note": "x"}), 400),
            ("a note over 4000 characters", packed({**doc, "note": "n" * 4001}), 400),
            ("a log over 1.5 MB as sent", gzip.compress(secrets.token_bytes(1_600_000)), 413),
            ("one over 24 MB unpacked", gzip.compress(b"0" * 25_000_000), 413),
        ]:
            s, body, _ = call("POST", "/relay/log", token, raw=raw_body, headers=gz, timeout=120)
            if s == 200 and isinstance(body, dict) and body.get("id"):
                sent.append(body["id"])
            check(f"{what} is refused", s == want, f"{s} {body if s != want else ''}")
        if day_limit:
            n = 1
            while n < 12:
                s, body, _ = call("POST", "/relay/log", token, raw=packed(doc), headers=gz)
                if s != 200:
                    break
                sent.append(body["id"])
                n += 1
            check("past ten a day, a log is refused with a plain sentence", s == 429 and n == 10
                  and "a day" in body.get("error", {}).get("message", ""), f"{n} taken, then {s} {body}")
        if lid:
            s, _, _ = admin_call("DELETE", f"/relay/admin/logs/{lid}", secret)
            gone = admin_call("GET", f"/relay/admin/logs/{lid}", secret)[0]
            check("the admin deletes it", s == 200 and gone == 404, f"{s}, then {gone}")
            sent.remove(lid)
    finally:
        for lid in sent:
            admin_call("DELETE", f"/relay/admin/logs/{lid}", secret)


def cmd_check_local():
    """The Worker under wrangler dev on loopback, made-up secrets, its storage in a temporary folder."""
    global URL
    passed, failed = 0, []

    def check(what, ok, detail=""):
        nonlocal passed
        print(f"{'ok  ' if ok else 'FAIL'} {what}: {detail}")
        if ok:
            passed += 1
        else:
            failed.append(what)
        return ok

    npx = shutil.which("npx")
    if not npx:
        raise SystemExit("npx isn't on PATH (Node.js)")
    with socket.socket() as so:
        so.bind(("127.0.0.1", 0))
        port = so.getsockname()[1]
    token, other, secret = "sqr-" + secrets.token_urlsafe(32), "sqr-" + secrets.token_urlsafe(32), secrets.token_urlsafe(32)
    sha = lambda t: hashlib.sha256(t.encode()).hexdigest()
    # workerd lets go of its storage a moment after it's stopped: the folder is removed below, with retries
    with tempfile.TemporaryDirectory(prefix="relay-check-", ignore_cleanup_errors=True) as tmp:
        env_file = Path(tmp) / "check.env"
        tokens = {sha(token): {"name": "local-check", "cap": 1}, sha(other): {"name": "local-other", "cap": 1}}
        env_file.write_text(f"ANTHROPIC_KEY=not-a-key\nTOKENS='{json.dumps(tokens)}'\nADMIN={sha(secret)}\n", encoding="utf-8")
        out = open(Path(tmp) / "wrangler.log", "w", encoding="utf-8", errors="replace")
        p = subprocess.Popen([npx, "--no-install", "wrangler", "dev", "--local", "--ip", "127.0.0.1", "--port", str(port),
                              "--env-file", str(env_file), "--persist-to", str(Path(tmp) / "state"),
                              "--show-interactive-dev-session=false"],
                             cwd=WORKER, stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                             env={**os.environ, "WRANGLER_SEND_METRICS": "false", "CI": "1"})
        URL = f"http://127.0.0.1:{port}"
        try:
            def up():
                try:
                    with urllib.request.urlopen(URL + "/", timeout=2) as r:
                        return r.status == 200
                except (urllib.error.URLError, OSError):
                    return False
            if not check("wrangler dev serves the Worker", wait_until(up, 90, 1), URL):
                out.flush()
                print((Path(tmp) / "wrangler.log").read_text(encoding="utf-8", errors="replace")[-2000:])
                return 1
            s, _, _ = call("GET", "/relay/usage", "sqr-" + "x" * 43)
            check("a token it doesn't know is refused", s == 401, f"{s}")
            u = usage(token)
            check("a token's usage answers", u is not None and u.get("spent_usd") == 0, json.dumps(u))
            log_checks(check, token, "local-check", secret, day_limit=True)
            # one token's logs aren't another's: the other's day is its own
            s, body, _ = call("POST", "/relay/log", other, raw=packed({"log": "x\n"}), headers={"content-type": "application/gzip"})
            check("another token's log is taken on its own count", s == 200, f"{s} {body}")
            if s == 200:
                admin_call("DELETE", f"/relay/admin/logs/{body['id']}", secret)
        finally:
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(p.pid)], capture_output=True)
            p.wait(timeout=20)
            out.close()
            wait_until(lambda: (shutil.rmtree(tmp, ignore_errors=True), not Path(tmp).exists())[1], 15, 0.5)
    print(f"\n{passed} of {passed + len(failed)} passed" + (f"; failed: {', '.join(failed)}" if failed else ""))
    return 0 if not failed else 1


def cmd_admin(new):
    if new or not ADMIN_FILE.exists():
        write_secret(ADMIN_FILE, secrets.token_urlsafe(32))
    put_secret("ADMIN", hashlib.sha256(read_secret(ADMIN_FILE).encode()).hexdigest())
    print(f"the admin secret's hash is in the Worker (ADMIN); the secret is in {ADMIN_FILE.relative_to(ROOT)}")


def admin_secret():
    if not ADMIN_FILE.exists():
        raise SystemExit("no admin secret yet: python tools/relay.py admin")
    return read_secret(ADMIN_FILE)


def logs_list(secret):
    s, body, _ = admin_call("GET", "/relay/admin/logs", secret)
    if s != 200 or not isinstance(body, dict):
        raise SystemExit(f"the relay answered {s}: {body}")
    return body.get("logs", [])


def cmd_logs(args):
    secret = admin_secret()
    if not args:
        logs = logs_list(secret)
        if not logs:
            print("no logs")
        for l in logs:
            when = time.strftime("%Y-%m-%d %H:%M", time.localtime(l["at"] / 1000))
            note = (l.get("note") or "").replace("\n", " ")
            print(f"{l['id']}  {l.get('token')}  sent {when}  {l.get('size', 0) // 1024} KB  session {l.get('session') or '?'}"
                  f"  {l.get('app') or ''}" + (f"\n    {note[:120]}" if note else ""))
        return 0
    what, rest = args[0], args[1:]
    if what == "fetch":
        logs = logs_list(secret)
        LOGS_DIR.mkdir(parents=True, exist_ok=True)
        if rest == ["--all"]:
            ids = [l["id"] for l in logs if not (LOGS_DIR / f"{l.get('token')}-{l['id']}.txt").exists()]
        else:
            ids = rest
        by = {l["id"]: l for l in logs}
        for lid in ids:
            if lid not in by:
                print(f"{lid}: no such log")
                continue
            s, _, raw = admin_call("GET", f"/relay/admin/logs/{lid}", secret)
            if s != 200:
                print(f"{lid}: the relay answered {s}")
                continue
            doc = json.loads(gzip.decompress(raw))
            path = LOGS_DIR / f"{by[lid].get('token')}-{lid}.txt"
            head = [f"From: {by[lid].get('token')}", f"App: {doc.get('app', '')} ({doc.get('commit') or 'no commit'})",
                    f"Session: {doc.get('session', '')}{' (still running when sent)' if doc.get('current') else ''}",
                    f"Lines: {doc.get('lines', '?')}, {doc.get('left_out', '?')} with words left out", "Note:",
                    *(doc.get("note") or "(none)").splitlines(), "", ""]
            path.write_text("\n".join(head) + doc.get("log", ""), encoding="utf-8")
            print(f"{lid}: {path.relative_to(ROOT)}")
        if not ids:
            print("nothing new to fetch")
        return 0
    if what == "delete" and rest:
        for lid in rest:
            s, body, _ = admin_call("DELETE", f"/relay/admin/logs/{lid}", secret)
            print(f"{lid}: {'deleted' if s == 200 else f'the relay answered {s}: {body}'}")
        return 0
    print(__doc__)
    return 2


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    a = sys.argv[1:]
    if not a:
        print(__doc__)
        return 2
    if a[0] == "deploy":
        print(wrangler("deploy")[-1500:])
    elif a[0] == "key":
        cmd_key()
    elif a[0] == "tokens":
        cmd_tokens()
    elif a[0] == "check":
        return cmd_check_local() if "--local" in a else cmd_check()
    elif a[0] == "admin":
        cmd_admin("--new" in a)
    elif a[0] == "logs":
        return cmd_logs(a[1:])
    elif a[0] == "token" and len(a) >= 3:
        what, name = a[1], a[2]
        if what == "new":
            cap = float(a[a.index("--cap") + 1]) if "--cap" in a else DEFAULT_CAP
            new_token(name, cap)
            print(f"token {name} made, cap ${cap:g} a month; kept in {token_file(name).relative_to(ROOT)} "
                  f"(python tools/relay.py token copy {name} puts it on the clipboard)")
        elif what == "cap" and len(a) >= 4:
            reg = registry()
            if name not in reg:
                raise SystemExit(f"no token called {name}")
            reg[name]["cap"] = float(a[3])
            save_registry(reg)
            push_tokens(reg)
            print(f"{name}: cap ${float(a[3]):g} a month")
        elif what == "revoke":
            revoke(name)
            print(f"{name}: revoked")
        elif what == "copy":
            to_clipboard(read_secret(token_file(name)))
            print(f"{name}'s token is on the clipboard")
        else:
            print(__doc__)
            return 2
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
