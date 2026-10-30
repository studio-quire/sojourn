"""A tap on everything Claude Code sends to the model: a loopback HTTP proxy in front of the Anthropic API that keeps
each request's body (the system prompt, the messages, tool results, the auto mode classifier's transcripts) and
passes it on unchanged, streaming the answer back. For private mode's leak test (tools/private_leak.py): set
ANTHROPIC_BASE_URL=http://127.0.0.1:PORT in the environment the claude processes start from.

  python tools/model_tap.py PORT OUT_DIR        run until killed

OUT_DIR gets NNNNN.json (a request's body, decompressed, after a `// METHOD path` line), NNNNN.out.txt (what came
back: text, tool calls by name, the stop reason) and status.json ({started, finished, inflight, last}: how many
requests, and when the last one ended, so a test can tell when the model has gone quiet). Headers are passed on but
never written (they carry the account's token).
"""
import gzip
import http.client
import json
import os
import sys
import threading
import time
import zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

UPSTREAM = os.environ.get("SOJOURN_TAP_UPSTREAM", "api.anthropic.com")
HOP = {"connection", "keep-alive", "proxy-connection", "transfer-encoding", "te", "trailer", "upgrade", "host", "content-length"}


class Counter:
    def __init__(self, out: Path):
        self.out = out
        self.lock = threading.Lock()
        self.n = max([int(p.stem) for p in out.glob("*.json") if p.stem.isdigit()] or [0])
        self.started = self.finished = 0
        self.last = time.time()
        self.write()

    def begin(self):
        with self.lock:
            self.n += 1
            self.started += 1
            self.write()
            return self.n

    def end(self):
        with self.lock:
            self.finished += 1
            self.last = time.time()
            self.write()

    def write(self):
        tmp = self.out / "status.tmp"
        tmp.write_text(json.dumps({"started": self.started, "finished": self.finished,
                                   "inflight": self.started - self.finished, "last": self.last}), encoding="utf-8")
        os.replace(tmp, self.out / "status.json")


class Reply:
    """Reads the SSE stream as it passes: text, tool calls, the stop reason."""

    def __init__(self):
        self.buf = b""
        self.parts = []
        self.stop = None

    def feed(self, chunk):
        self.buf += chunk
        while b"\n" in self.buf:
            line, self.buf = self.buf.split(b"\n", 1)
            if not line.startswith(b"data:"):
                continue
            try:
                ev = json.loads(line[5:].strip())
            except ValueError:
                continue
            t = ev.get("type")
            if t == "content_block_start" and (ev.get("content_block") or {}).get("type") == "tool_use":
                self.parts.append(f"\n[tool {ev['content_block'].get('name')}] ")
            elif t == "content_block_delta":
                d = ev.get("delta") or {}
                self.parts.append(d.get("text") or d.get("partial_json") or "")
            elif t == "message_delta":
                self.stop = (ev.get("delta") or {}).get("stop_reason")

    def text(self):
        return "".join(self.parts) + f"\n[stop {self.stop}]\n"


def make_handler(out: Path, counter: Counter):
    class Tap(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):
            pass

        def _body(self):
            if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
                parts = []
                while True:
                    size = int(self.rfile.readline().strip().split(b";")[0], 16)
                    if size == 0:
                        self.rfile.readline()
                        break
                    parts.append(self.rfile.read(size))
                    self.rfile.readline()
                return b"".join(parts)
            n = int(self.headers.get("Content-Length") or 0)
            return self.rfile.read(n) if n else b""

        def _plain(self, body):
            enc = (self.headers.get("Content-Encoding") or "").lower()
            try:
                if enc == "gzip":
                    return gzip.decompress(body)
                if enc == "deflate":
                    return zlib.decompress(body)
                if enc == "br":
                    import brotli  # noqa: PLC0415
                    return brotli.decompress(body)
            except Exception:
                pass
            return body

        def _forward(self):
            body = self._body()
            n = counter.begin()
            try:
                (out / f"{n:05d}.json").write_bytes(f"// {self.command} {self.path}\n".encode() + self._plain(body))
                self._relay(n, body)
            finally:
                counter.end()

        def _relay(self, n, body):
            conn = http.client.HTTPSConnection(UPSTREAM, timeout=600)
            headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP}
            headers["Host"] = UPSTREAM
            headers["Accept-Encoding"] = "identity"   # so the answer can be read as it passes
            try:
                conn.request(self.command, self.path, body=body or None, headers=headers)
                r = conn.getresponse()
            except Exception as e:
                self.send_error(502, f"tap: {e!r}")
                return
            self.send_response(r.status, r.reason)
            for k, v in r.getheaders():
                if k.lower() not in HOP:
                    self.send_header(k, v)
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()
            reply = Reply() if not r.getheader("Content-Encoding") else None
            try:
                while True:
                    chunk = r.read1(65536)
                    if not chunk:
                        break
                    if reply:
                        reply.feed(chunk)
                    self.wfile.write(b"%x\r\n%s\r\n" % (len(chunk), chunk))
                    self.wfile.flush()
                self.wfile.write(b"0\r\n\r\n")
                self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass
            finally:
                conn.close()
                if reply:
                    (out / f"{n:05d}.out.txt").write_text(f"HTTP {r.status}\n" + reply.text(), encoding="utf-8")

        do_GET = do_POST = do_PUT = do_DELETE = do_PATCH = _forward

    return Tap


class Server(ThreadingHTTPServer):
    daemon_threads = True

    def handle_error(self, request, client_address):
        pass   # clients closing keep-alive connections


def serve(port, out):
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    return Server(("127.0.0.1", port), make_handler(out, Counter(out)))


if __name__ == "__main__":
    srv = serve(int(sys.argv[1]), sys.argv[2])
    print(f"tap on http://127.0.0.1:{sys.argv[1]} -> {UPSTREAM}, bodies in {sys.argv[2]}", flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
