"""Spoken replies, when the person turns them on (the settings menu's "replies", or by asking): Kokoro (82M, Apache-2.0)
on the CPU through onnxruntime, about 0.7 s for a two-sentence reply, played on the default output device (the
headset over Link). Falls back to Windows' own voice when Kokoro's files aren't there.

Only the assistant's replies to the person are spoken, and only to them: this never plays into voice chat. When voice
chat owns the audio device, `Speaker.sink` is where its mix takes these samples over (so they also reach its echo
canceller and never the mic); until then it plays here.
"""
import os
import re
import subprocess
import threading
from pathlib import Path

try:
    import numpy as np
except ImportError:   # a packaged Sojourn Access: no local voice (main.py's NoSpeaker), only ENGLISH wanted
    np = None

HERE = Path(__file__).resolve().parent
MODEL_DIR = __import__("paths").model_dir("kokoro", __import__("paths").LOCAL / "assistant" / "kokoro")
MODEL = MODEL_DIR / "kokoro-v1.0.int8.onnx"
VOICES = MODEL_DIR / "voices-v1.0.bin"
DEFAULT_VOICE = "af_heart"

# English voices, for the tool's description: id -> how it sounds
ENGLISH = {
    "af_heart": "American, warm (the default)", "af_bella": "American, bright", "af_nicole": "American, soft and close",
    "af_sarah": "American, clear", "am_michael": "American, calm", "am_fenrir": "American, deep", "am_puck": "American, lively",
    "bf_emma": "British, measured", "bf_isabella": "British, light", "bm_george": "British, low", "bm_fable": "British, storyteller",
}


def speakable(text):
    """What's worth saying out loud: the reply without ids, long numbers or bracketed asides."""
    t = re.sub(r"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "", text or "")
    t = re.sub(r"\s*\([^)]*\)", "", t)
    return re.sub(r"\s+", " ", t).strip()


class Speaker:
    def __init__(self, log=print):
        self.log = log
        self.voice = DEFAULT_VOICE
        self.kokoro = None
        self.voices = set()
        self.sink = None            # f(samples float32 mono, rate, stop_event): voice chat's mix, when it has the device
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._loaded = False

    def _load(self):
        if self._loaded:
            return
        self._loaded = True
        if MODEL.exists() and VOICES.exists():
            try:
                from kokoro_onnx import Kokoro
                self.kokoro = Kokoro(str(MODEL), str(VOICES))
                self.voices = set(self.kokoro.get_voices())
                self.log("voice: kokoro ready")
            except Exception as e:  # noqa: BLE001
                self.log(f"voice: kokoro didn't load ({e!r}); Windows' voice instead")
        else:
            self.log("voice: kokoro's files aren't in local/assistant/kokoro (python tools/assistant.py setup); Windows' voice instead")

    def warm(self):
        """Load the model ahead of the first reply (in a thread: it takes about a second)."""
        threading.Thread(target=self._load, daemon=True).start()

    def set_voice(self, voice):
        self.voice = voice or DEFAULT_VOICE

    def stop(self):
        """Stop whatever is being said (the person started talking, or a new request came)."""
        self._stop.set()

    def say(self, text):
        """Speak in the background; a new reply cuts off the last."""
        text = speakable(text)
        if not text:
            return
        self._stop.set()
        threading.Thread(target=self._say, args=(text,), daemon=True).start()

    def _say(self, text):
        with self._lock:
            self._stop.clear()
            stop = self._stop
            self._load()
            try:
                if self.kokoro is None:
                    self._windows(text, stop)
                    return
                voice = self.voice if self.voice in self.voices else DEFAULT_VOICE
                samples, rate = self.kokoro.create(text, voice=voice, speed=1.05, lang="en-gb" if voice[0] == "b" else "en-us")
                if stop.is_set():
                    return
                samples = np.asarray(samples, dtype=np.float32)
                if os.environ.get("SOJOURN_TTS_OUT"):   # tests: each reply to a WAV instead of the speakers
                    self._wav(samples, rate)
                elif self.sink:
                    self.sink(samples, rate, stop)
                else:
                    self._play(samples, rate, stop)
            except Exception as e:  # noqa: BLE001
                self.log(f"voice: couldn't speak ({e!r})")

    def _wav(self, samples, rate):
        import time
        import wave
        out = Path(os.environ["SOJOURN_TTS_OUT"])
        out.mkdir(parents=True, exist_ok=True)
        path = out / f"reply-{time.strftime('%H%M%S')}-{self.voice}.wav"
        with wave.open(str(path), "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(rate)
            w.writeframes((np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes())
        self.log(f"voice: wrote {path.name}")

    @staticmethod
    def _play(samples, rate, stop):
        import soundcard as sc
        chunk = rate // 10
        with sc.default_speaker().player(samplerate=rate, channels=1, blocksize=chunk) as p:
            for i in range(0, len(samples), chunk):
                if stop.is_set():
                    return
                p.play(samples[i:i + chunk])

    @staticmethod
    def _windows(text, stop):
        ps = ("Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
              f"$s.Speak('{text.replace(chr(39), chr(39) * 2)}'); $s.Dispose()")
        p = subprocess.Popen(["powershell", "-NoProfile", "-Command", ps], creationflags=0x08000000 if os.name == "nt" else 0)
        while p.poll() is None:
            if stop.wait(0.1):
                p.kill()
                return
