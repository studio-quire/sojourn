"""A stand-in for an Anthropic-compatible endpoint (Anthropic's API, OpenRouter's), for testing the assistant's
providers (assistant/provider.py) without a real key or spending anything. tools/provider_check.py runs it.

  python tools/provider_mock.py [--port 9741] [--log FILE] [--secret DECOY]... [--dump FILE]

The provider's proxy talks to it when SOJOURN_PROVIDER_UPSTREAM points here. It answers:
- POST .../v1/messages (streamed or not): a request whose last user text holds `MOCK_BASH: <command>` gets a Bash
  tool call with that command; one ending in a tool result gets "result: <the result's start>"; auto mode's
  classifier gets an allow (so a command's environment can be checked); anything else "mock reply".
- GET .../v1/models: a few models in both Anthropic's and OpenRouter's shapes (with and without tools and pictures),
  public as OpenRouter's is; GET .../v1/key: OpenRouter's key info.
A key with "bad" in it gets 401, one with "credits" in it 402.
--log appends a line per request: the path, the model, how it was authorised (x-api-key or Bearer, the key's length,
whether it's one of the --secret decoys; never the key), the system prompt's start, how many tools and pictures,
effort and thinking, and whether a decoy appears anywhere in the body: the check that a key never reaches a model.
--dump keeps the last classifier request's body (to see its format).
"""
import argparse
import json
import re
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ap = argparse.ArgumentParser()
ap.add_argument("--port", type=int, default=9741)
ap.add_argument("--log", default=None)
ap.add_argument("--secret", action="append", default=[])
ap.add_argument("--dump", default=None)
a = ap.parse_args()


def log(rec):
    line = json.dumps(rec)
    print(line, flush=True)
    if a.log:
        with open(a.log, "a", encoding="utf-8") as f:
            f.write(line + "\n")


def last_user_text(body):
    """The last user message's text and whether it carries a tool result."""
    for m in reversed(body.get("messages") or []):
        if m.get("role") != "user":
            continue
        c = m.get("content")
        if isinstance(c, str):
            return c, None
        texts, result = [], None
        for b in c or []:
            if b.get("type") == "text":
                texts.append(b.get("text") or "")
            elif b.get("type") == "tool_result":
                rc = b.get("content")
                result = rc if isinstance(rc, str) else " ".join(x.get("text", "") for x in rc or [] if isinstance(x, dict))
        return "\n".join(texts), result
    return "", None


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def auth(self):
        if k := self.headers.get("x-api-key"):
            return "x-api-key", k
        h = self.headers.get("Authorization") or ""
        return ("bearer", h[7:]) if h.startswith("Bearer ") else (None, "")

    def send(self, code, obj):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        kind, key = self.auth()
        path = self.path.split("?")[0]
        log({"t": time.time(), "get": path, "auth": kind, "key_len": len(key), "auth_is_secret": key in a.secret})
        if (key or not path.endswith("/v1/models")) and (not key or "bad" in key):   # (OpenRouter's list is public)
            return self.send(401, {"type": "error", "error": {"type": "authentication_error", "message": "invalid x-api-key"}})
        if "credits" in key:
            return self.send(402, {"error": {"code": 402, "message": "Insufficient credits"}})
        if path.endswith("/v1/models"):
            # both shapes at once: Anthropic's (id, display_name) and OpenRouter's (name, context, tools, pictures)
            models = [("anthropic/claude-sonnet-5.5", 1_000_000, True, True), ("~google/gemini-pro-latest", 1_048_576, True, True),
                      ("test/no-pictures", 200_000, True, False), ("test/no-tools", 128_000, False, True)]
            return self.send(200, {"data": [{"type": "model", "id": m, "display_name": m, "name": m, "context_length": ctx,
                                             "supported_parameters": ["tools"] if tools else [],
                                             "architecture": {"input_modalities": ["text", "image"] if pics else ["text"]}}
                                            for m, ctx, tools, pics in models], "has_more": False})
        if path.endswith("/v1/key"):
            return self.send(200, {"data": {"label": "mock", "limit": None, "usage": 0, "is_free_tier": False}})
        self.send(404, {"error": "not found"})

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(n)
        kind, key = self.auth()
        path = self.path.split("?")[0]
        try:
            body = json.loads(raw)
        except ValueError:
            body = {}
        system = body.get("system")
        if isinstance(system, list):
            system = " ".join(b.get("text", "") for b in system if isinstance(b, dict))
        text, result = last_user_text(body)
        rec = {"t": time.time(), "post": path, "model": body.get("model"), "auth": kind, "key_len": len(key),
               "stream": bool(body.get("stream")), "tools": len(body.get("tools") or []),
               "system": re.sub(r"\s+", " ", system or "")[:140], "user": text[:100],
               "output_config": body.get("output_config"), "thinking": body.get("thinking")}
        if a.secret:
            rec["secret_in_body"] = any(x.encode() in raw for x in a.secret)
            rec["auth_is_secret"] = key in a.secret
        rec["images"] = raw.count(b'"type": "image"') + raw.count(b'"type":"image"')
        if result is not None:
            rec["tool_result"] = result[:300]
        log(rec)
        if a.dump and "security monitor" in (system or ""):
            with open(a.dump, "wb") as f:
                f.write(raw)
        if not key or "bad" in key:
            return self.send(401, {"type": "error", "error": {"type": "authentication_error", "message": "invalid x-api-key"}})
        if not path.endswith("/v1/messages"):
            return self.send(404, {"type": "error", "error": {"type": "not_found_error", "message": path}})
        model = body.get("model") or "mock"
        m = re.search(r"MOCK_BASH: (.+)", text or "")
        if "security monitor" in (system or ""):
            # auto mode's classifier: every action allowed, so what a command can see gets tested (this is a mock)
            blocks, stop = [{"type": "text", "text": "<severity>5</severity>"}], "end_turn"
        elif result is not None:
            blocks = [{"type": "text", "text": "result: " + result[:400]}]
            stop = "end_turn"
        elif m and body.get("tools"):
            blocks = [{"type": "tool_use", "id": f"toolu_mock{int(time.time() * 1000)}", "name": "Bash",
                       "input": {"command": m.group(1).strip(), "description": "mock command"}}]
            stop = "tool_use"
        else:
            blocks = [{"type": "text", "text": "mock reply"}]
            stop = "end_turn"
        if not body.get("stream"):
            return self.send(200, {"id": "msg_mock", "type": "message", "role": "assistant", "model": model, "content": blocks,
                                   "stop_reason": stop, "stop_sequence": None, "usage": {"input_tokens": 10, "output_tokens": 5}})
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()

        def ev(name, data):
            self.wfile.write(f"event: {name}\ndata: {json.dumps(data)}\n\n".encode())

        ev("message_start", {"type": "message_start", "message": {"id": "msg_mock", "type": "message", "role": "assistant",
                                                                  "model": model, "content": [], "stop_reason": None,
                                                                  "stop_sequence": None,
                                                                  "usage": {"input_tokens": 10, "output_tokens": 1}}})
        for i, b in enumerate(blocks):
            if b["type"] == "text":
                ev("content_block_start", {"type": "content_block_start", "index": i, "content_block": {"type": "text", "text": ""}})
                ev("content_block_delta", {"type": "content_block_delta", "index": i, "delta": {"type": "text_delta", "text": b["text"]}})
            else:
                ev("content_block_start", {"type": "content_block_start", "index": i,
                                           "content_block": {"type": "tool_use", "id": b["id"], "name": b["name"], "input": {}}})
                ev("content_block_delta", {"type": "content_block_delta", "index": i,
                                           "delta": {"type": "input_json_delta", "partial_json": json.dumps(b["input"])}})
            ev("content_block_stop", {"type": "content_block_stop", "index": i})
        ev("message_delta", {"type": "message_delta", "delta": {"stop_reason": stop, "stop_sequence": None}, "usage": {"output_tokens": 5}})
        ev("message_stop", {"type": "message_stop"})
        self.wfile.flush()
        self.close_connection = True


if __name__ == "__main__":
    print(f"provider mock on http://127.0.0.1:{a.port}", flush=True)
    ThreadingHTTPServer(("127.0.0.1", a.port), H).serve_forever()
