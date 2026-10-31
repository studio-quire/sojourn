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
                                           (a token of its own with a cap of a millionth of a dollar, revoked after)

The registry (local/secrets/studio/relay-tokens.json) holds names, hashes and caps, never tokens. Tokens and the key
are DPAPI-encrypted for this Windows account in the format PowerShell's ConvertFrom-SecureString writes (hex, UTF-16),
so `ConvertTo-SecureString (Get-Content FILE)` reads them too.
Written by Claude Opus 5.5, 2026-10-30.
"""
import ctypes
import hashlib
import json
import re
import secrets
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from ctypes import wintypes
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKER = ROOT / "tools" / "relay"
SECRETS = ROOT / "local" / "secrets" / "studio"
KEY_FILE = SECRETS / "relay-anthropic.key"
REGISTRY = SECRETS / "relay-tokens.json"
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

def call(method, path, token, body=None, headers=None, timeout=60):
    """(status, parsed JSON or text, raw bytes)."""
    data = json.dumps(body).encode() if body is not None else None
    h = {"x-api-key": token, "anthropic-version": "2023-06-01", "User-Agent": "sojourn-relay-check", **(headers or {})}
    if data is not None:
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
    finally:
        revoke(CHECK_TOKEN)
    refused = wait_until(lambda: call("GET", "/relay/usage", token)[0] == 401, 60)
    check("a revoked token is refused", refused, "401" if refused else "still answered")
    print(f"\n{passed} of {passed + len(failed)} passed" + (f"; failed: {', '.join(failed)}" if failed else ""))
    return 0 if not failed else 1


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
        return cmd_check()
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
