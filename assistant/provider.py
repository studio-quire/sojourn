"""Who the assistant's Claude Code sessions run on: the person's own provider, chosen in settings > assistant
(Sojourn Access: Alt+0). docs/assistant.md > Switching it on.

- claude: the Claude subscription Claude Code is signed in to on this PC (as it always was). Only from the repo: a
  packaged build offers no Claude-account sign-in (Anthropic's Agent SDK terms, the project's decision of 2026-10-30),
  so there "claude" means none chosen yet (state "none"), and the client puts a key or token kept in its place.
- anthropic: the person's Anthropic API key.
- openrouter: the person's OpenRouter key, and a model there: "claude" (Anthropic's models through OpenRouter, the
  same three roles and modes) or any OpenRouter model id, which then takes every request itself.
- relay: a personal token from Studio Quire, for people the studio covers: Anthropic's API through the studio's relay
  (tools/relay/, docs/assistant.md > The studio relay), which holds the studio's key and caps each token's spending by
  the month. The token is kept and added as a key is; its month's allowance used up, the person hears so plainly.

The keys are the person's secrets. They're kept only in the keys folder (local/secrets/assistant from the repo,
%LOCALAPPDATA%\\Sojourn\\secrets\\assistant otherwise; SOJOURN_KEYS for tests), each encrypted for this Windows account
(DPAPI, the client's Assistant/ProviderKeys.cs writes them), never in settings, a log or an environment. Claude Code
never gets a key either: the sessions talk to a proxy here on loopback, which adds the key on the way out. Claude Code
holds only the proxy's token, made afresh each run and worth nothing outside it, so even a shell that printed its own
environment would find no key. The folder is under harness.py's deny rules, shell guard and auto mode's hard rules.

The proxy is also where the providers' differences are met: Claude Code's model ids become OpenRouter's, auto mode's
safety checks stay on Claude (Sonnet 5 through OpenRouter) whatever model answers, and for other makers' models what's
Claude-only (effort, thinking, context management, Anthropic's beta headers) is left out, and pictures too for a model
that can't see them (a line saying so instead), rather than an error. With the person's "no data collection" (on by
default) every OpenRouter messages request carries OpenRouter's provider preference data_collection: "deny", which its
Anthropic-compatible /api/v1/messages takes in the body (its API reference, checked 2026-10-29): only providers that
don't store or train on what's sent answer; if none serves the model, OpenRouter refuses the request and the person
hears why.

Auto mode (prefs auto_mode, on by default) rides along here because changing it restarts the sessions as a provider
change does: off, the sessions run in dontAsk mode with harness.py's MANUAL_TOOLS and no classifier is ever called.

SOJOURN_PROVIDER_UPSTREAM sends both providers to a test server instead (tools/provider_mock.py).
Written by Claude Opus 5.5, 2026-10-29.
"""
import base64
import ctypes
import hmac
import http.client
import json
import os
import re
import secrets
import shutil
import subprocess
import threading
import time
import urllib.error
import urllib.request
from ctypes import wintypes
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

import paths

ROOT = Path(__file__).resolve().parents[1]
KINDS = ("claude", "anthropic", "openrouter", "relay")
KEY_KINDS = ("anthropic", "openrouter", "relay")
ANTHROPIC = "https://api.anthropic.com"
OPENROUTER = "https://openrouter.ai/api"
RELAY = "https://relay.studioquire.org"
NAMES = {"anthropic": "Anthropic", "openrouter": "OpenRouter", "relay": "Studio Quire"}
# auto mode's classifier (permission checks): pinned so the proxy knows its requests and keeps them on Claude
CLASSIFIER = "claude-sonnet-5"
ENTROPY = b"Sojourn assistant key"
# what a key looks like: a paste of anything else is refused rather than kept (the client checks the same)
SHAPES = {"anthropic": re.compile(r"sk-ant-[A-Za-z0-9_\-]{16,300}"), "openrouter": re.compile(r"sk-or-[A-Za-z0-9_\-]{16,300}"),
          "relay": re.compile(r"sqr-[A-Za-z0-9_\-]{40,64}")}


def keys_dir():
    if d := os.environ.get("SOJOURN_KEYS"):
        return Path(d)
    if (ROOT / "src" / "Sojourn.Client").is_dir():
        return ROOT / "local" / "secrets" / "assistant"
    return Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local") / "Sojourn" / "secrets" / "assistant"


def shape(kind, text):
    """The key in what was pasted (trimmed of spaces and quotes), or None if it isn't one of kind's."""
    t = (text or "").strip().strip("\"'").strip()
    return t if kind in SHAPES and SHAPES[kind].fullmatch(t) else None


def kind_of(text):
    """Which provider a pasted key is for, by its shape, or None."""
    return next((k for k in KEY_KINDS if shape(k, text)), None)


# --- DPAPI (CryptProtectData for the current Windows user), as the client's ProviderKeys.cs ---------------------------

class _Blob(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]


def _dpapi(data: bytes, protect: bool) -> bytes:
    crypt32, kernel32 = ctypes.windll.crypt32, ctypes.windll.kernel32
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    src = ctypes.create_string_buffer(data, len(data))
    ent = ctypes.create_string_buffer(ENTROPY, len(ENTROPY))
    b_in = _Blob(len(data), ctypes.cast(src, ctypes.POINTER(ctypes.c_char)))
    b_ent = _Blob(len(ENTROPY), ctypes.cast(ent, ctypes.POINTER(ctypes.c_char)))
    out = _Blob()
    if protect:
        ok = crypt32.CryptProtectData(ctypes.byref(b_in), ctypes.c_wchar_p("Sojourn"), ctypes.byref(b_ent), None, None, 1, ctypes.byref(out))
    else:
        ok = crypt32.CryptUnprotectData(ctypes.byref(b_in), None, ctypes.byref(b_ent), None, None, 1, ctypes.byref(out))
    ctypes.memset(src, 0, len(data))
    if not ok:
        raise OSError(f"data protection failed ({ctypes.GetLastError()})")
    try:
        return ctypes.string_at(out.pbData, out.cbData)
    finally:
        kernel32.LocalFree(ctypes.cast(out.pbData, ctypes.c_void_p))


def load_key(kind):
    """The person's key for kind, decrypted, or None (none kept, or kept by another Windows account)."""
    try:
        blob = base64.b64decode((keys_dir() / f"{kind}.key").read_text(encoding="ascii").strip())
        return _dpapi(blob, protect=False).decode("utf-8") or None
    except (OSError, ValueError):
        return None


def save_key(kind, key):
    """Keep a key (tools/assistant.py key; the client keeps the ones pasted into its menus)."""
    d = keys_dir()
    d.mkdir(parents=True, exist_ok=True)
    tmp = d / f"{kind}.key.tmp"
    tmp.write_text(base64.b64encode(_dpapi(key.encode("utf-8"), protect=True)).decode("ascii"), encoding="ascii")
    os.replace(tmp, d / f"{kind}.key")


def forget_key(kind):
    (keys_dir() / f"{kind}.key").unlink(missing_ok=True)


def has_key(kind):
    return (keys_dir() / f"{kind}.key").exists()


def cli():
    """The Claude Code to run: SOJOURN_CLAUDE, the one on PATH, or the Agent SDK's own copy (so a key works without
    Claude Code installed)."""
    if p := os.environ.get("SOJOURN_CLAUDE"):
        return p
    if p := shutil.which("claude"):
        return p
    try:
        import claude_agent_sdk
        b = Path(claude_agent_sdk.__file__).parent / "_bundled" / ("claude.exe" if os.name == "nt" else "claude")
        return str(b) if b.exists() else None
    except ImportError:
        return None


# --- OpenRouter's models --------------------------------------------------------------------------------------------

# the choices offered (the launcher's list, the menu's row, Access's Alt+0) are the client's: Sojourn.Session's
# Assistant/OpenRouterCatalog.cs, "claude" (Claude's three by the model setting, as or_slug maps them) and OpenRouter's
# families' current models by their real tags; any other id can be set by the person (the agent API is read-only for it)


def openrouter_models(log=print):
    """OpenRouter's public model list ({id: {name, context, tools, pictures}}), or {} if it can't be had."""
    try:
        req = urllib.request.Request(_base("openrouter") + "/v1/models", headers={"User-Agent": "Sojourn"})
        with urllib.request.urlopen(req, timeout=10) as r:
            data = json.loads(r.read()).get("data") or []
    except (OSError, ValueError) as e:
        log(f"provider: OpenRouter's model list unavailable ({type(e).__name__})")
        return {}
    return {m["id"]: {"name": m.get("name") or m["id"], "context": m.get("context_length") or 0,
                      "tools": "tools" in (m.get("supported_parameters") or []),
                      "pictures": "image" in ((m.get("architecture") or {}).get("input_modalities") or [])}
            for m in data if isinstance(m, dict) and m.get("id")}


def _base(kind):
    return os.environ.get("SOJOURN_PROVIDER_UPSTREAM") or {"anthropic": ANTHROPIC, "relay": RELAY}.get(kind, OPENROUTER)


def thing(kind):
    """What the person keeps for a provider, as said: "Anthropic key", "Studio Quire token"."""
    return f"{NAMES.get(kind, kind)} {'token' if kind == 'relay' else 'key'}"


def first_of_next_month():
    t = time.gmtime()
    y, m = (t.tm_year + 1, 1) if t.tm_mon == 12 else (t.tm_year, t.tm_mon + 1)
    return f"1 {time.strftime('%B', time.struct_time((y, m, 1, 0, 0, 0, 0, 1, 0)))}"


def claude_family(slug):
    return slug.startswith(("anthropic/", "~anthropic/"))


def or_slug(model_id):
    """Claude Code's model id as OpenRouter names it: claude-haiku-5-5[1m] -> anthropic/claude-haiku-5.5,
    claude-sonnet-5 -> anthropic/claude-sonnet-5 (a date suffix dropped); anything else unchanged."""
    base = re.sub(r"\[\w+\]$", "", model_id or "")
    m = re.fullmatch(r"claude-([a-z]+)-(\d+)(?:-(\d{1,2}))?(?:-\d{8})?", base)
    if not m:
        return base
    return f"anthropic/claude-{m.group(1)}-{m.group(2)}" + (f".{m.group(3)}" if m.group(3) else "")


# --- the proxy -------------------------------------------------------------------------------------------------------

class Proxy:
    """Claude Code's endpoint (ANTHROPIC_BASE_URL) on loopback: checks the run's token, puts the person's key on the
    request and sends it to the provider, the response streamed back as it comes. Its settings (route) change live."""

    def __init__(self, log):
        self.log = log
        self.token = "sojourn-" + secrets.token_urlsafe(24)
        self.route = None        # (kind, key, single model or None, can it see pictures, deny data collection)
        self.on_refused = None   # (status, words) -> None: the provider said no (a bad key, no credits)
        self.on_ok = None        # () -> None: a request went through (a month's allowance back, say)
        self.on_policy = None    # () -> None: OpenRouter had no provider for the model that keeps to "no data collection"
        self._idle = []          # idle upstream connections
        self._lock = threading.Lock()
        proxy = self

        class H(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *args):
                pass

            def do_GET(self):
                proxy.handle(self)

            def do_POST(self):
                proxy.handle(self)

        class Server(ThreadingHTTPServer):
            daemon_threads = True

            def handle_error(self, request, client_address):
                pass   # Claude Code closing a kept-alive connection as it exits: not worth a traceback in the log

        self.server = Server(("127.0.0.1", 0), H)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, name="provider-proxy", daemon=True).start()

    def _conn(self, base, fresh=False):
        u = urlsplit(base)
        if not fresh:
            with self._lock:
                for i, (b, c) in enumerate(self._idle):
                    if b == base:
                        del self._idle[i]
                        return c
        cls = http.client.HTTPSConnection if u.scheme == "https" else http.client.HTTPConnection
        return cls(u.hostname, u.port, timeout=600)

    def _done(self, base, c):
        with self._lock:
            if len(self._idle) < 8:
                self._idle.append((base, c))
                return
        c.close()

    def rewrite(self, body, single, sees, deny_data=False, messages=True):
        """An OpenRouter request made OpenRouter's: its model, the person's data preference, and for another maker's
        model what's Claude's alone."""
        if deny_data and messages:
            given = body.get("provider") if isinstance(body.get("provider"), dict) else {}
            body["provider"] = {**given, "data_collection": "deny"}
        m = re.sub(r"\[\w+\]$", "", body.get("model") or "")
        session_ids = {"claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5"}
        slug = single if single and m in session_ids else or_slug(m)
        body["model"] = slug
        if claude_family(slug):
            return True
        for k in ("output_config", "thinking", "context_management"):
            body.pop(k, None)
        if not sees:
            for msg in body.get("messages") or []:
                if isinstance(msg.get("content"), list):
                    msg["content"] = [_no_picture(b) for b in msg["content"]]
        return False

    def handle(self, h):
        route = self.route
        given = h.headers.get("x-api-key") or (h.headers.get("Authorization") or "").removeprefix("Bearer ")
        n = int(h.headers.get("Content-Length") or 0)
        raw = h.rfile.read(n) if n else b""
        if not hmac.compare_digest(given.encode(), self.token.encode()):
            return _reply(h, 401, {"type": "error", "error": {"type": "authentication_error", "message": "not this run's token"}})
        if not route:
            return _reply(h, 503, {"type": "error", "error": {"type": "api_error", "message": "no provider set up"}})
        kind, key, single, sees, deny_data = route
        path = h.path
        claude = True
        headers = {"Content-Type": h.headers.get("Content-Type") or "application/json", "Accept-Encoding": "identity",
                   "anthropic-version": h.headers.get("anthropic-version") or "2023-06-01",
                   "User-Agent": h.headers.get("User-Agent") or "Sojourn"}
        if kind == "relay" and not re.fullmatch(r"/v1/(messages(/count_tokens)?|models(/[\w.\-]+)?)", path.split("?")[0]):
            return _reply(h, 404, {"type": "error", "error": {"type": "not_found_error", "message": "not relayed"}})
        if kind == "openrouter":
            if not path.split("?")[0].endswith(("/v1/messages", "/v1/messages/count_tokens", "/v1/models")):
                return _reply(h, 404, {"type": "error", "error": {"type": "not_found_error", "message": "not proxied"}})
            if raw and h.command == "POST":
                try:
                    body = json.loads(raw)
                    claude = self.rewrite(body, single, sees, deny_data, path.split("?")[0].endswith("/v1/messages"))
                    raw = json.dumps(body).encode()
                except ValueError:
                    pass
            headers["Authorization"] = "Bearer " + key
            headers["HTTP-Referer"] = "https://github.com/studio-quire"
            headers["X-Title"] = "Sojourn"
        else:
            headers["x-api-key"] = key
        if claude and h.headers.get("anthropic-beta"):
            headers["anthropic-beta"] = h.headers["anthropic-beta"]
        if raw:
            headers["Content-Length"] = str(len(raw))
        base = _base(kind)
        upath = urlsplit(base).path.rstrip("/") + path
        for attempt in (0, 1):
            c = self._conn(base, fresh=attempt > 0)
            try:
                c.request(h.command, upath, body=raw or None, headers=headers)
                r = c.getresponse()
                break
            except (OSError, http.client.HTTPException) as e:
                c.close()
                if attempt:
                    self.log(f"provider: couldn't reach {urlsplit(base).hostname} ({type(e).__name__})")
                    return _reply(h, 502, {"type": "error", "error": {"type": "api_error", "message": f"couldn't reach the provider ({type(e).__name__})"}})
        if r.status in (401, 402, 403) and self.on_refused:
            self.on_refused(r.status)
        elif r.status == 200 and self.on_ok:
            self.on_ok()
        try:
            h.send_response(r.status)
            for k, v in r.getheaders():
                if k.lower() in ("content-type", "request-id", "retry-after", "x-should-retry") or k.lower().startswith("anthropic-ratelimit"):
                    h.send_header(k, v)
            length = r.getheader("Content-Length")
            if length is not None and r.getheader("Transfer-Encoding") is None:
                data = r.read()
                if kind == "openrouter" and deny_data and r.status in (400, 404) and self.on_policy and _policy_refusal(data):
                    self.on_policy()
                h.send_header("Content-Length", str(len(data)))
                h.end_headers()
                h.wfile.write(data)
            else:
                h.send_header("Transfer-Encoding", "chunked")
                h.end_headers()
                while chunk := r.read1(65536):
                    h.wfile.write(b"%x\r\n%s\r\n" % (len(chunk), chunk))
                    h.wfile.flush()
                h.wfile.write(b"0\r\n\r\n")
            h.wfile.flush()
        except (OSError, http.client.HTTPException):
            c.close()   # Claude Code went away mid-answer (an interrupt): this connection isn't reusable
            h.close_connection = True
            return
        if r.will_close:
            c.close()
        else:
            self._done(base, c)


def _policy_refusal(data):
    """OpenRouter's refusal when no provider for the model keeps to the data policy ("No endpoints found matching your
    data policy")."""
    t = data.decode("utf-8", "replace").lower()
    return "data policy" in t or "data_collection" in t or "data collection" in t


def _no_picture(b):
    if not isinstance(b, dict):
        return b
    if b.get("type") == "image":
        return {"type": "text", "text": "[a picture: the model answering can't see pictures, so it isn't shown]"}
    if b.get("type") == "tool_result" and isinstance(b.get("content"), list):
        return {**b, "content": [_no_picture(x) for x in b["content"]]}
    return b


def _reply(h, status, obj):
    data = json.dumps(obj).encode()
    h.send_response(status)
    h.send_header("Content-Type", "application/json")
    h.send_header("Content-Length", str(len(data)))
    h.end_headers()
    h.wfile.write(data)


# --- the provider as the person set it ------------------------------------------------------------------------------

class Provider:
    """The person's choice (prefs: provider, openrouter_model, key_stamp), whether it can work (ready), and what the
    sessions need for it (env, the context window, what to tell the model it is)."""

    def __init__(self, log=print):
        self.log = log
        self.kind, self.model, self.stamp = "claude", "claude", None
        self.deny_data = True       # OpenRouter: only providers that don't collect data (the person's setting)
        self.auto = True            # the sessions under auto mode (the person's setting; harness.py MANUAL_TOOLS when off)
        self.ready = False
        self.state = "checking"     # ready, checking, none, no_login, no_key, refused, credits, allowance, no_tools, offline
        self.words = ""             # for the person: what's set up and, if it isn't working, how to switch it on
        self.ok_words = ""          # the words while it works (back after a refusal clears)
        self.models = {}            # OpenRouter's list, once fetched
        self.single = None          # an OpenRouter model taking every request, or None (Claude's three roles)
        self.single_name = None
        self.window = 1_000_000
        self.caveat = ""            # what doesn't work with this setup, said plainly
        self.proxy = None
        self.app = None
        self._lock = threading.Lock()

    def wanted(self, prefs):
        p = prefs or {}
        kind = p.get("provider") if p.get("provider") in KINDS else "claude"
        return (kind, (p.get("openrouter_model") or "claude").strip(), p.get("key_stamp"),
                p.get("openrouter_deny_data") is not False, p.get("auto_mode") is not False)

    def differs(self, prefs):
        return self.wanted(prefs) != (self.kind, self.model, self.stamp, self.deny_data, self.auto)

    def configure(self, prefs):
        """Take the person's settings and check them (blocking: a request or two, a few seconds at most)."""
        with self._lock:
            self.kind, self.model, self.stamp, self.deny_data, self.auto = self.wanted(prefs)
            self.single = self.single_name = None
            self.window, self.caveat = 1_000_000, ""
            if self.kind == "claude":
                self._claude()
            else:
                self._keyed()
            data = "" if self.kind != "openrouter" else ", no data collection" if self.deny_data else ", data collection allowed"
            self.log(f"provider: {self.kind}{' ' + self.model if self.kind == 'openrouter' else ''}{data}"
                     f"{'' if self.auto else ', auto mode off'}: {self.state}")

    def _claude(self):
        if self.proxy:
            self.proxy.route = None
        if paths.PACKAGED:
            # no Claude-account sign-in in a packaged build: "claude" is the setting's old default, none chosen yet
            return self._not("none", "")
        exe = cli()
        if not exe:
            return self._not("no_login", "Claude Code isn't on this PC.")
        env = {k: v for k, v in os.environ.items() if not k.startswith(("ANTHROPIC_", "CLAUDE_CODE_OAUTH"))}
        try:
            r = subprocess.run([exe, "auth", "status"], capture_output=True, text=True, timeout=30, env=env,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            st = json.loads(r.stdout or "{}")
        except (OSError, ValueError, subprocess.SubprocessError) as e:
            self.log(f"provider: claude auth status failed ({type(e).__name__})")
            st = {}
        if not st.get("loggedIn"):
            return self._not("no_login", "No Claude account is signed in on this PC.")
        self.ready, self.state = True, "ready"
        self.words = "your Claude account"

    def _keyed(self):
        key = load_key(self.kind)
        name = NAMES[self.kind]
        if not key:
            return self._not("no_key", f"No {thing(self.kind)} is kept yet.")
        if self.kind == "openrouter":
            self.models = self.models or openrouter_models(self.log)
            if self.model != "claude":
                info = self.models.get(self.model)
                if self.models and not info:
                    return self._not("no_tools", f"OpenRouter has no model called {self.model}.")
                if info and not info["tools"]:
                    return self._not("no_tools", f"{info['name']} can't use tools on OpenRouter, so it can't act for you.")
                self.single = self.model
                self.single_name = (info or {}).get("name") or self.model
                if info and info["context"]:
                    self.window = min(1_000_000, info["context"])
                notes = []
                if info and not info["pictures"]:
                    notes.append("it can't see pictures, so looking around and describing things won't work")
                notes.append("effort settings and web search are Claude's, so they may not apply")
                self.caveat = f"With {self.single_name}: " + "; ".join(notes) + "."
        verdict = self.check(key)
        if verdict in ("refused", "credits"):
            return self._not(verdict, self.refusal(401 if verdict == "refused" else 402))
        if not self.proxy:
            self.proxy = Proxy(self.log)
            self.proxy.on_refused = self.refused
            self.proxy.on_policy = self.policy
            self.proxy.on_ok = self.went_through
        self.proxy.route = (self.kind, key, self.single, not self.single or (self.models.get(self.single) or {}).get("pictures", True),
                            self.kind == "openrouter" and self.deny_data)
        self.ready = True
        # a month's allowance used up: ready all the same, so the first request after it comes back goes through
        self.state = {"ok": "ready", "allowance": "allowance"}.get(verdict, "offline")
        self.words = (("Studio Quire's relay" if self.kind == "relay" else f"your {name} key")
                      + (f", {self.single_name}" if self.single else "")
                      + ("" if verdict in ("ok", "allowance") else f" (couldn't reach {name} to check it)"))
        self.ok_words = self.words
        if verdict == "allowance":
            self.words = self.refusal(402)

    def check(self, key):
        """ok, refused, credits, allowance or offline: a request that costs nothing (Anthropic's model list,
        OpenRouter's key, the relay's count of the token's month)."""
        if self.kind == "anthropic":
            url, headers = _base("anthropic") + "/v1/models?limit=1", {"x-api-key": key, "anthropic-version": "2023-06-01"}
        elif self.kind == "relay":
            url, headers = _base("relay") + "/relay/usage", {"x-api-key": key}
        else:
            url, headers = _base("openrouter") + "/v1/key", {"Authorization": "Bearer " + key}
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={**headers, "User-Agent": "Sojourn"}), timeout=10) as r:
                if self.kind == "relay":
                    try:
                        if float(json.loads(r.read()).get("left_usd", 1)) <= 0:
                            return "allowance"
                    except (ValueError, TypeError, AttributeError):
                        pass
                return "ok"
        except urllib.error.HTTPError as e:
            if e.code == 402:
                return "allowance" if self.kind == "relay" else "credits"
            return "refused" if e.code in (401, 403) else "offline"
        except OSError:
            return "offline"

    def refusal(self, status):
        """What the person is told when the provider says no: 402 is out of credits (OpenRouter) or the month's
        allowance used up (the relay); 401 and 403 a key or token refused."""
        if status == 402:
            return ("This month's allowance for the assistant from Studio Quire is used up." if self.kind == "relay"
                    else "The OpenRouter account is out of credits.")
        return "Studio Quire's relay refused the token." if self.kind == "relay" else f"{NAMES.get(self.kind, self.kind)} refused the key."

    def refused(self, status):
        """The provider turned a request down mid-sitting (the key revoked, credits or the month's allowance run out):
        the person hears why."""
        state = ("allowance" if self.kind == "relay" else "credits") if status == 402 else "refused"
        if self.state == state:
            return
        self.state, self.words = state, self.refusal(status)
        self.log(f"provider: the {self.kind} {'token' if self.kind == 'relay' else 'key'} was refused ({status})")

    def went_through(self):
        """A request went through after a refusal (a new month's allowance, credits topped up): working again."""
        if self.state in ("allowance", "credits", "refused"):
            self.state, self.words = "ready", self.ok_words
            self.log(f"provider: {self.kind} answers again")

    def policy(self):
        """OpenRouter refused a request: no provider for this model keeps to "no data collection"."""
        if self.state == "no_private":
            return
        self.state = "no_private"
        self.words = "OpenRouter has no provider for this model that keeps to no data collection."
        self.log("provider: openrouter has no provider for the model under data_collection deny")

    def _not(self, state, why):
        self.ready, self.state = False, state
        if self.proxy:
            self.proxy.route = None
        self.words = why

    def how(self):
        """What the person is told when it isn't working: why, and how to switch it on."""
        access = self.app == "access"
        where = "Alt+0, the assistant's provider" if access else "the launcher's settings"
        noun = "token" if self.kind == "relay" else "key"
        if self.state == "allowance":
            return (f"The assistant can't answer: {self.words} It starts again on {first_of_next_month()}. "
                    "Everything else works without it.")
        if self.state in ("no_key", "refused", "credits"):
            how = (f"Copy your {noun}, then {'choose its row in Alt+0 and paste it' if access else 'paste it into the key box in the launcher'}"
                   if self.state == "no_key" else f"Put in another {noun} or choose another provider")
            return f"The assistant is off: {self.words} {how} ({where})."
        if self.state == "none":
            return ("The assistant is off until you choose how it runs: with a token from Studio Quire, or your own "
                    "Anthropic or OpenRouter key. Copy it, then "
                    f"{'choose the assistant key row in Alt+0 and paste it' if access else 'paste it into the key box in the launcher (its settings)'}. "
                    "Everything else works without it.")
        if self.state == "no_tools":
            return f"The assistant is off: {self.words} Choose another model ({'Alt+0' if access else 'settings, assistant, openrouter model'})."
        if self.state == "no_private":
            return (f"The assistant can't answer: {self.words} Choose another model, or allow data collection "
                    f"({'Alt+0' if access else "the launcher's settings"}).")
        return (f"The assistant is off: {self.words} To switch it on, sign in to Claude Code on this PC with a Claude "
                f"subscription, or use your own Anthropic or OpenRouter key, or a token from Studio Quire: copy it, then "
                f"{'choose the assistant key row in Alt+0' if access else 'paste it into the key box in the launcher (its settings)'}. "
                "Everything else works without it.")

    def status(self):
        """For the client's menu (a trouble note on the first model row) and Access's provider row."""
        noun = "token" if self.kind == "relay" else "key"
        short = {"ready": "signed in" if self.kind == "claude" else f"{noun} works", "offline": f"{noun} kept, not checked",
                 "none": "none chosen yet", "no_login": "not signed in", "no_key": f"no {noun} yet", "refused": f"{noun} refused", "credits": "no credits",
                 "allowance": "this month's allowance used up", "no_tools": "can't use tools", "checking": "checking",
                 "no_private": "no provider without data collection"}.get(self.state, self.state)
        if self.ready and self.single and not (self.models.get(self.single) or {}).get("pictures", True):
            short += ", can't see pictures"
        ok = self.ready and self.state not in ("no_private", "allowance")
        return {"kind": self.kind, "state": self.state, "short": short, "words": self.words if ok else self.how(),
                "caveat": self.caveat or None, "model": self.single_name, "auto": self.auto}

    def env(self):
        """What a Claude Code session needs for this provider (never the key: the proxy's token)."""
        if self.kind == "claude" or not self.proxy:
            return {}   # the subscription (main.py takes ANTHROPIC_* out of the sidecar's environment at start)
        # (an API key outranks a signed-in subscription in Claude Code, so nothing else needs clearing)
        e = {"ANTHROPIC_BASE_URL": self.proxy.url, "ANTHROPIC_API_KEY": self.proxy.token}
        if paths.PACKAGED:   # Claude Code's own files in the app's data folder, not the person's profile
            e["CLAUDE_CONFIG_DIR"] = str(paths.packaged_claude())
            # and none of its own traffic (usage telemetry, error reports, update checks): the README promises no
            # analytics, and the requests go only through the provider chosen
            e.update({"DISABLE_TELEMETRY": "1", "DISABLE_ERROR_REPORTING": "1", "DISABLE_AUTOUPDATER": "1"})
            # its cache (MCP logs holding the tools' calls, error logs) follows LOCALAPPDATA, not CLAUDE_CONFIG_DIR: kept
            # in the data folder too, so removing that folder removes everything (checked by `package --check --relay`)
            e["LOCALAPPDATA"] = str(paths.packaged_claude() / "local")
            os.makedirs(e["LOCALAPPDATA"], exist_ok=True)
        if self.kind == "openrouter" and self.auto:
            # the safety classifier's own requests, on Claude through the proxy (OpenRouter wouldn't review in-band)
            e.update({"CLAUDE_CODE_AUTO_MODE_MODEL": CLASSIFIER, "CLAUDE_CODE_AUTO_MODE_SERVER": "0"})
        return e

    def label(self, model_id):
        """The panel's name for who's answering."""
        if self.single:
            return self.single_name
        return None

    def prompt_note(self):
        """For another maker's model: what it is, plainly (Claude Code's own text calls every model Claude); and with
        auto mode off, what that leaves the session."""
        manual = "" if self.auto else (
            "\nAuto mode is off (the person's choice, in Sojourn's launcher or Sojourn Access's Alt+0): nothing reviews "
            "your actions, so you have only the tools that are safe without a reviewer: your sojourn tools, reading files, "
            "and writing files in your own folder and scratch folder. No shell or scripts, no web search or fetching, no "
            "helpers. When a request needs one of those, say so plainly and that turning auto mode on gives it.\n")
        if not self.single:
            return manual
        return manual + (f"\nYou are {self.single_name} ({self.single}), reached through OpenRouter with the person's own key: their "
                "choice. Claude Code's own instructions around these call the model Claude; that's the harness, not you. "
                + (self.caveat + " " if self.caveat else "") + "Say so plainly if something can't be done because of it.\n")


current = Provider()
