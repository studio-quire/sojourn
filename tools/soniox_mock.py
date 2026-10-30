"""A stand-in for Soniox's real-time WebSocket API (assistant/cloud.py), for tests without a key or the network.

  python tools/soniox_mock.py [--port 9739] [--mode ok|slow|error|silent] [--say TEXT] [--log FILE]

It checks what cloud.py sends as Soniox documents it (a Bearer key on the connection, the start request's fields, raw
16 kHz s16le audio as binary frames, an empty text frame to end), answers with non-final then final tokens of --say
(or "heard N.N seconds" when not given), then a finished response. --mode slow answers 3 s late, error sends Soniox's
error shape and closes, silent never answers. --log appends a line per session: the config's fields, how many terms,
the terms themselves, the audio's length, the key's length (never the key). Point the sidecar at it with
SOJOURN_SONIOX_URL=ws://127.0.0.1:9739 and a decoy key file in SOJOURN_SONIOX_KEY_FILE.
Run it with the assistant's venv (it has websockets).
"""
import argparse
import json
import time

from websockets.sync.server import serve

ap = argparse.ArgumentParser()
ap.add_argument("--port", type=int, default=9739)
ap.add_argument("--mode", default="ok", choices=["ok", "slow", "error", "silent"])
ap.add_argument("--say", default=None)
ap.add_argument("--log", default=None)
a = ap.parse_args()


def handle(ws):
    auth = ws.request.headers.get("Authorization") or ""
    rec = {"t": time.time(), "bearer": auth.startswith("Bearer "), "key_len": len(auth) - 7 if auth.startswith("Bearer ") else 0}
    cfg = json.loads(ws.recv())
    rec["config"] = {k: v for k, v in cfg.items() if k != "context"}
    terms = ((cfg.get("context") or {}).get("terms")) or []
    rec["terms_n"], rec["terms"] = len(terms), terms
    problems = [f for f in ("model", "audio_format", "sample_rate", "num_channels") if f not in cfg]
    if cfg.get("audio_format") != "pcm_s16le" or cfg.get("sample_rate") != 16000 or not rec["bearer"]:
        problems.append("format or key")
    nbytes = 0
    while True:
        m = ws.recv()
        if isinstance(m, bytes):
            nbytes += len(m)
            if a.mode == "ok" and nbytes and nbytes % 32000 < len(m):   # a non-final guess now and then
                ws.send(json.dumps({"tokens": [{"text": "hmm", "is_final": False}], "final_audio_proc_ms": 0,
                                    "total_audio_proc_ms": nbytes // 32}))
            continue
        if m == "":
            break
    rec["audio_s"] = round(nbytes / 32000, 2)
    rec["problems"] = problems
    if a.log:
        with open(a.log, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec) + "\n")
    if a.mode == "silent":
        time.sleep(30)
        return
    if a.mode == "error" or problems:
        ws.send(json.dumps({"tokens": [], "error_code": 400, "error_type": "invalid_request",
                            "error_message": "; ".join(problems) or "test error", "request_id": "mock"}))
        return
    if a.mode == "slow":
        time.sleep(3)
    text = a.say or f"heard {nbytes / 32000:.1f} seconds"
    words = [(" " if i else "") + w for i, w in enumerate(text.split())]
    ws.send(json.dumps({"tokens": [{"text": w, "is_final": True} for w in words[:2]], "final_audio_proc_ms": nbytes // 32,
                        "total_audio_proc_ms": nbytes // 32}))
    ws.send(json.dumps({"tokens": [{"text": w, "is_final": True} for w in words[2:]] + [{"text": "<end>", "is_final": True}],
                        "final_audio_proc_ms": nbytes // 32, "total_audio_proc_ms": nbytes // 32, "finished": True}))


with serve(handle, "127.0.0.1", a.port) as server:
    print(f"soniox mock on ws://127.0.0.1:{a.port} ({a.mode})", flush=True)
    server.serve_forever()
