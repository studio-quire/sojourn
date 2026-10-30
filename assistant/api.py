"""The client's agent API (loopback HTTP: Tessel's bridge API over the emulated LEAP ops), as the sidecar uses it."""
import json
import urllib.error
import urllib.request


class ApiError(Exception):
    pass


class Api:
    def __init__(self, base):
        self.base = base.rstrip("/")

    def _req(self, path, body=None, timeout=20):
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(self.base + path, data=data, method="GET" if body is None else "POST",
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                raw = r.read()
        except urllib.error.HTTPError as e:
            raise ApiError(f"{path}: HTTP {e.code} {e.read()[:200].decode(errors='replace')}") from e
        except OSError as e:
            raise ApiError(f"{path}: {e}") from e
        return json.loads(raw) if raw else None

    def cmd(self, pump, op, wait=15, **args):
        """One op; raises ApiError on {error}. `wait`: how long the API gives it (gated ops wait for the person on
        top of that). An op's own `timeout` argument goes in args."""
        r = self._req("/cmd", {"pump": pump, "data": {"op": op, **args}, "timeout": wait}, timeout=wait + 140)
        if isinstance(r, dict) and r.get("error"):
            raise ApiError(str(r["error"]))
        return r

    def status(self):
        return self._req("/status", timeout=5)

    def events(self, since, wait=0.0):
        return self._req(f"/events?since={since}&wait={wait}", timeout=wait + 10) or []

    def panel(self, **fields):
        try:
            self.cmd("SojournAssistant", "panel", **fields)
        except ApiError:
            pass
