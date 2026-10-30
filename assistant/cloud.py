"""Hearing in the cloud, the person's opt-in (`hearing: soniox`): Soniox's real-time speech-to-text (stt-rt-v5), the
audio streamed while the talk button is held, so the final text comes moments after the release. docs/assistant.md >
Hearing > Soniox.

Why it may run in incognito and get the names around the person (the user's rule, 2026-10-27: only a service that
neither keeps nor trains on the audio may): Soniox's privacy policy (updated 2026-06-29) says real-time requests are
processed transiently and not stored, that customer content (audio, transcripts, context, vocabulary) is never used to
train, evaluate or improve its models, and that its logs hold no audio or transcripts. Checked 2026-10-27; if those terms
change, this stops qualifying.

The key is the person's, in local/secrets/soniox.key (one line): read here at each connection, never put in the
environment or a log, and out of the assistant's reach with the SL password (harness.py's deny rules on local/secrets,
the shell hook, auto mode's hard rules). Nothing of the audio or the text is logged here: only how long it took or an
error's type. SOJOURN_SONIOX_URL and SOJOURN_SONIOX_KEY_FILE point it at a test server and a decoy key (tools/soniox_mock.py).
"""
import json
import os
import threading
import time
from pathlib import Path

import numpy as np

KEY = Path(os.environ.get("SOJOURN_SONIOX_KEY_FILE") or Path(__file__).resolve().parents[1] / "local" / "secrets" / "soniox.key")
URL = "wss://stt-rt.soniox.com/transcribe-websocket"
MODEL = "stt-rt-v5"
RATE = 16000


def key():
    try:
        k = KEY.read_text(encoding="utf-8").strip()
    except OSError:
        return None
    return k or None


def available():
    return key() is not None


class Stream:
    """One utterance: connects at once, sends the audio as it arrives (live(from) -> (float32 samples, done)), ends the
    stream when the audio is done, and collects the final words. result(until) waits for them until a time.time()."""

    def __init__(self, live, terms, log):
        self.live, self.terms, self.log = live, terms, log
        self.text = None
        self.error = None
        self.done = threading.Event()
        self.sent_all = threading.Event()   # the last audio read and sent (the client may hand the utterance over)
        self.started = time.time()
        threading.Thread(target=self._run, daemon=True, name="soniox").start()

    def _run(self):
        try:
            self.text = self._session()
        except Exception as e:   # down, refused, too slow to connect: the local model hears it instead
            self.error = type(e).__name__
        finally:
            self.sent_all.set()
            self.done.set()

    def _session(self):
        from websockets.sync.client import connect
        k = key()
        if not k:
            raise PermissionError("no key")
        config = {"model": MODEL, "audio_format": "pcm_s16le", "sample_rate": RATE, "num_channels": 1,
                  "language_hints": ["en"], "language_hints_strict": True, "enable_endpoint_detection": False}
        if self.terms:
            config["context"] = {"terms": self.terms[:300]}
        with connect(os.environ.get("SOJOURN_SONIOX_URL") or URL, additional_headers={"Authorization": f"Bearer {k}"},
                     open_timeout=2.0, close_timeout=1.0, max_size=2 ** 20) as ws:
            ws.send(json.dumps(config))
            sent = 0
            while True:
                x, finished = self.live(sent)
                if len(x):
                    ws.send((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())
                    sent += len(x)
                if finished:
                    break
                time.sleep(0.08)
            self.sent_all.set()
            ws.send("")   # the end of the audio: the server finalises everything and says finished
            words = []
            while True:
                msg = json.loads(ws.recv(timeout=5.0))
                if msg.get("error_code"):
                    raise ConnectionError(f"{msg.get('error_code')} {msg.get('error_type') or ''}".strip())
                for t in msg.get("tokens") or []:
                    if t.get("is_final") and not (t.get("text", "").startswith("<") and t.get("text", "").endswith(">")):
                        words.append(t["text"])
                if msg.get("finished"):
                    return "".join(words).strip()

    def result(self, until):
        """The final text if it came by `until` (time.time()), else None."""
        self.done.wait(max(0.0, until - time.time()))
        return self.text if self.done.is_set() else None
