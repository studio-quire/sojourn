"""Janus HTTP API proxy for the local voice grid: OpenSim 0.9.3.1's WebRtcJanusService attaches to the hardcoded
plugin "janus.plugin.audiobridge"; newer OpenSim has a PluginName setting for legion-voice-mixer's
"janus.plugin.slvoice". This sits between the two and renames the plugin both ways, nothing else.

Runs inside the voice compose project (the mixer image's Python 3.6), started by tools/grid.py voice up.
  python3 janus_name_proxy.py LISTEN_PORT UPSTREAM_URL
"""
import http.server
import socketserver
import sys
import urllib.error
import urllib.request

OLD, NEW = b"janus.plugin.audiobridge", b"janus.plugin.slvoice"
UPSTREAM = sys.argv[2].rstrip("/")


class Proxy(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def relay(self):
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n).replace(OLD, NEW) if n else None
        req = urllib.request.Request(UPSTREAM + self.path, data=body, method=self.command)
        for k in ("Content-Type", "Accept"):
            if self.headers.get(k):
                req.add_header(k, self.headers[k])
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                status, ctype, data = r.status, r.headers.get("Content-Type", "application/json"), r.read()
        except urllib.error.HTTPError as e:
            status, ctype, data = e.code, e.headers.get("Content-Type", "text/plain"), e.read()
        except OSError as e:
            status, ctype, data = 502, "text/plain", str(e).encode()
        data = data.replace(NEW, OLD)
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    do_GET = do_POST = relay

    def log_message(self, *args):
        pass


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True


Server(("0.0.0.0", int(sys.argv[1])), Proxy).serve_forever()
