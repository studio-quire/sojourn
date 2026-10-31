"""The assistant's sound through the client's voice engine (src/Sojourn.Session/Voice/AssistantMic.cs), when it has one.

The client owns the microphone for voice chat. While the talk button is held it hands the person's own mic, already
echo-cancelled and denoised, to the assistant instead of to residents (GET /mic: the utterance once the button is up,
with 300 ms from before the press and 250 ms after the release; kept a couple of minutes, so a press made while
the sidecar starts is heard once it's ready), and it plays the assistant's spoken replies in its
own mix (POST /play), so they're cancelled out of the mic and never reach a voice channel. Without voice (--no-voice,
or no engine) the sidecar records and plays on its own, as before.
"""
import base64
import time

import numpy as np

from api import ApiError


class ClientAudio:
    def __init__(self, api):
        self.api = api

    def info(self):
        try:
            return self.api._req("/mic/info", timeout=5) or {}
        except ApiError:
            return {}

    def utterance(self, wait=3.0, down=None, up=None):
        """An utterance as 16 kHz mono float32 (empty if none came): the press whose talk events were logged at down and
        up (however long ago, within a couple of minutes), or without them the last one."""
        press = f"&down={down}&up={up}" if down is not None and up is not None else ""
        r = self.api._req(f"/mic?wait={wait}{press}", timeout=wait + 10) or {}
        raw = base64.b64decode(r.get("pcm") or "")
        return np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768

    def play(self, pcm16, rate):
        r = self.api._req("/play", {"rate": rate, "pcm": base64.b64encode(pcm16).decode()}, timeout=10) or {}
        return r.get("queued_ms", 0)

    def queued(self):
        return (self.api._req("/play", timeout=5) or {}).get("queued_ms", 0)

    def clear(self):
        self.api._req("/play/clear", {}, timeout=5)

    def sink(self, samples, rate, stop):
        """voice.Speaker's sink: half-second pieces, about a second queued ahead, cut off at once by stop."""
        pcm = (np.clip(samples, -1, 1) * 32767).astype("<i2")
        piece = rate // 2
        try:
            for i in range(0, len(pcm), piece):
                if stop.is_set():
                    break
                queued = self.play(pcm[i:i + piece].tobytes(), rate)
                while queued > 1000 and not stop.is_set():
                    time.sleep(0.1)
                    queued = self.queued()
            while not stop.is_set() and self.queued() > 0:
                time.sleep(0.1)
            if stop.is_set():
                self.clear()
        except ApiError:
            pass
