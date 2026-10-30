"""Who the assistant's Claude Code sessions run on: the person's own provider, chosen in settings > assistant
(Sojourn Access: Alt+0). docs/assistant.md > Switching it on.

- claude: the Claude subscription Claude Code is signed in to on this PC (as it always was).
- anthropic: the person's Anthropic API key.
- openrouter: the person's OpenRouter key, and a model there: "claude" (Anthropic's models through OpenRouter, the
  same three roles and modes) or any OpenRouter model id, which then takes every request itself.

The keys are the person's secrets. They're kept only in the keys folder (local/secrets/assistant from the repo,
%LOCALAPPDATA%\\Sojourn\\secrets\\assistant otherwise; SOJOURN_KEYS for tests), each encrypted for this Windows account
(DPAPI, the client's Assistant/ProviderKeys.cs writes them), never in settings, a log or an environment. Claude Code
never gets a key either: the sessions talk to a proxy here on loopback, which adds the key on the way out. Claude Code
holds only the proxy's token, made afresh each run and worth nothing outside it, so even a shell that printed its own
environment would find no key. The folder is under harness.py's deny rules, shell guard and auto mode's hard rules.

The proxy is also where the providers' differences are met: Claude Code's model ids become OpenRouter's, auto mode's
safety checks stay on Claude (Sonnet 5 through OpenRouter) whatever model answers, and for other makers' models what's
Claude-only (effort, thinking, context management, Anthropic's beta headers) is left out, and pictures too for a model
that can't see them (a line saying so instead), rather than an error.

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

ROOT = Path(__file__).resolve().parents[1]
KINDS = ("claude", "anthropic", "openrouter")
KEY_KINDS = ("anthropic", "openrouter")
ANTHROPIC = "https://api.anthropic.com"
OPENROUTER = "https://openrouter.ai/api"
# auto mode's classifier (permission checks): pinned so the proxy knows its requests and keeps them on Claude
CLASSIFIER = "claude-sonnet-5"
ENTROPY = b"Sojourn assistant key"
# what a key looks like: a paste of anything else is refused rather than kept (the client checks the same)
SHAPES = {"anthropic": re.compile(r"sk-ant-[A-Za-z0-9_\-]{16,300}"), "openrouter": re.compile(r"sk-or-[A-Za-z0-9_\-]{16,300}")}


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

# the menu's short list (the client has it too, Control/VrSettings.cs): OpenRouter's rolling "latest" ids, each with tool
# use and pictures; any other id can be set by the person (Access's Alt+0, the agent API is read-only for it)
SUGGESTED = {"claude": "Claude (Haiku, Sonnet and Opus)", "~google/gemini-pro-latest": "Gemini Pro",
             "~google/gemini-flash-latest": "Gemini Flash", "~openai/gpt-sol-latest": "GPT Sol",
             "~x-ai/grok-latest": "Grok", "~moonshotai/kimi-latest": "Kimi"}


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
    return os.environ.get("SOJOURN_PROVIDER_UPSTREAM") or (ANTHROPIC if kind == "anthropic" else OPENROUTER)


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
        self.route = None        # (kind, key, single model or None, can it see pictures)
        self.on_refused = None   # (status, words) -> None: the provider said no (a bad key, no credits)
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

    def rewrite(self, body, single, sees):
        """An OpenRouter request made OpenRouter's: its model, and for another maker's model what's Claude's alone."""
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
        kind, key, single, sees = route
        path = h.path
        claude = True
        headers = {"Content-Type": h.headers.get("Content-Type") or "application/json", "Accept-Encoding": "identity",
                   "anthropic-version": h.headers.get("anthropic-version") or "2023-06-01",
                   "User-Agent": h.headers.get("User-Agent") or "Sojourn"}
        if kind == "openrouter":
            if not path.split("?")[0].endswith(("/v1/messages", "/v1/messages/count_tokens", "/v1/models")):
                return _reply(h, 404, {"type": "error", "error": {"type": "not_found_error", "message": "not proxied"}})
            if raw and h.command == "POST":
                try:
                    body = json.loads(raw)
                    claude = self.rewrite(body, single, sees)
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
        try:
            h.send_response(r.status)
            for k, v in r.getheaders():
                if k.lower() in ("content-type", "request-id", "retry-after", "x-should-retry") or k.lower().startswith("anthropic-ratelimit"):
                    h.send_header(k, v)
            length = r.getheader("Content-Length")
            if length is not None and r.getheader("Transfer-Encoding") is None:
                data = r.read()
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
        self.ready = False
        self.state = "checking"     # ready, checking, no_login, no_key, refused, credits, no_tools, offline
        self.words = ""             # for the person: what's set up and, if it isn't working, how to switch it on
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
        return kind, (p.get("openrouter_model") or "claude").strip(), p.get("key_stamp")

    def differs(self, prefs):
        return self.wanted(prefs) != (self.kind, self.model, self.stamp)

    def configure(self, prefs):
        """Take the person's settings and check them (blocking: a request or two, a few seconds at most)."""
        with self._lock:
            self.kind, self.model, self.stamp = self.wanted(prefs)
            self.single = self.single_name = None
            self.window, self.caveat = 1_000_000, ""
            if self.kind == "claude":
                self._claude()
            else:
                self._keyed()
            self.log(f"provider: {self.kind}{' ' + self.model if self.kind == 'openrouter' else ''}: {self.state}")

    def _claude(self):
        if self.proxy:
            self.proxy.route = None
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
        name = "Anthropic" if self.kind == "anthropic" else "OpenRouter"
        if not key:
            return self._not("no_key", f"No {name} key is kept yet.")
        if self.kind == "openrouter":
            self.models = self.models or openrouter_models(self.log)
            if self.model != "claude":
                info = self.models.get(self.model)
                if self.models and not info:
                    return self._not("no_tools", f"OpenRouter has no model called {self.model}.")
                if info and not info["tools"]:
                    return self._not("no_tools", f"{info['name']} can't use tools on OpenRouter, so it can't act for you.")
                self.single = self.model
                self.single_name = (info or {}).get("name") or SUGGESTED.get(self.model) or self.model
                if info and info["context"]:
                    self.window = min(1_000_000, info["context"])
                notes = []
                if info and not info["pictures"]:
                    notes.append("it can't see pictures, so looking around and describing things won't work")
                notes.append("effort settings and web search are Claude's, so they may not apply")
                self.caveat = f"With {self.single_name}: " + "; ".join(notes) + "."
        verdict = self.check(key)
        if verdict in ("refused", "credits"):
            return self._not(verdict, f"{name} refused the key." if verdict == "refused" else "The OpenRouter account is out of credits.")
        if not self.proxy:
            self.proxy = Proxy(self.log)
            self.proxy.on_refused = self.refused
        self.proxy.route = (self.kind, key, self.single, not self.single or (self.models.get(self.single) or {}).get("pictures", True))
        self.ready = True
        self.state = "ready" if verdict == "ok" else "offline"
        self.words = (f"your {name} key" + (f", {self.single_name}" if self.single else "")
                      + ("" if verdict == "ok" else f" (couldn't reach {name} to check it)"))

    def check(self, key):
        """ok, refused, credits or offline: a request that costs nothing (Anthropic's model list, OpenRouter's key)."""
        if self.kind == "anthropic":
            url, headers = _base("anthropic") + "/v1/models?limit=1", {"x-api-key": key, "anthropic-version": "2023-06-01"}
        else:
            url, headers = _base("openrouter") + "/v1/key", {"Authorization": "Bearer " + key}
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={**headers, "User-Agent": "Sojourn"}), timeout=10):
                return "ok"
        except urllib.error.HTTPError as e:
            return "credits" if e.code == 402 else "refused" if e.code in (401, 403) else "offline"
        except OSError:
            return "offline"

    def refused(self, status):
        """The provider turned a request down mid-sitting (the key revoked, credits run out): the person hears why."""
        if self.state in ("refused", "credits"):
            return
        self.state = "credits" if status == 402 else "refused"
        self.words = ("The OpenRouter account is out of credits." if status == 402 else
                      f"{'Anthropic' if self.kind == 'anthropic' else 'OpenRouter'} refused the key.")
        self.log(f"provider: the {self.kind} key was refused ({status})")

    def _not(self, state, why):
        self.ready, self.state = False, state
        if self.proxy:
            self.proxy.route = None
        self.words = why

    def how(self):
        """What the person is told when it isn't working: why, and how to switch it on."""
        where = "Alt+0, the assistant's provider" if self.app == "access" else "settings, assistant, provider"
        if self.state in ("no_key", "refused", "credits"):
            how = (f"Copy your key, then {'choose its row in Alt+0 and paste it' if self.app == 'access' else 'press paste key there'}"
                   if self.state == "no_key" else "Put in another key or choose another provider")
            return f"The assistant is off: {self.words} {how} ({where})."
        if self.state == "no_tools":
            return f"The assistant is off: {self.words} Choose another model ({where})."
        return (f"The assistant is off: {self.words} To switch it on, sign in to Claude Code on this PC with a Claude "
                f"subscription, or use your own Anthropic or OpenRouter key: copy it, then "
                f"{'choose the assistant key row in Alt+0' if self.app == 'access' else 'press paste key in settings, assistant'}. "
                "Everything else works without it.")

    def status(self):
        """For the client's menu (the provider row's note)."""
        short = {"ready": "signed in" if self.kind == "claude" else "key works", "offline": "key kept, not checked",
                 "no_login": "not signed in", "no_key": "no key yet", "refused": "key refused", "credits": "no credits",
                 "no_tools": "can't use tools", "checking": "checking"}.get(self.state, self.state)
        if self.ready and self.single and not (self.models.get(self.single) or {}).get("pictures", True):
            short += ", can't see pictures"
        return {"kind": self.kind, "state": self.state, "short": short, "words": self.words if self.ready else self.how(),
                "caveat": self.caveat or None, "model": self.single_name}

    def env(self):
        """What a Claude Code session needs for this provider (never the key: the proxy's token)."""
        if self.kind == "claude" or not self.proxy:
            return {}   # the subscription (main.py takes ANTHROPIC_* out of the sidecar's environment at start)
        # (an API key outranks a signed-in subscription in Claude Code, so nothing else needs clearing)
        e = {"ANTHROPIC_BASE_URL": self.proxy.url, "ANTHROPIC_API_KEY": self.proxy.token}
        if self.kind == "openrouter":
            # the safety classifier's own requests, on Claude through the proxy (OpenRouter wouldn't review in-band)
            e.update({"CLAUDE_CODE_AUTO_MODE_MODEL": CLASSIFIER, "CLAUDE_CODE_AUTO_MODE_SERVER": "0"})
        return e

    def label(self, model_id):
        """The panel's name for who's answering."""
        if self.single:
            return self.single_name
        return None

    def prompt_note(self):
        """For another maker's model: what it is, plainly (Claude Code's own text calls every model Claude)."""
        if not self.single:
            return ""
        return (f"\nYou are {self.single_name} ({self.single}), reached through OpenRouter with the person's own key: their "
                "choice. Claude Code's own instructions around these call the model Claude; that's the harness, not you. "
                + (self.caveat + " " if self.caveat else "") + "Say so plainly if something can't be done because of it.\n")


current = Provider()
