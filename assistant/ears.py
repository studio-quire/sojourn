"""Push-to-talk speech in: the microphone while the talk button is held, then a local recogniser on the CPU (never the
GPU: in the headset it costs frames, and the contention brought the view's jitter back). The person's `hearing` setting
picks it (docs/assistant.md > Hearing):
- whisper (the default): faster-whisper small.en, int8, prompted with the names around the person, then hearing.py's
  name pass;
- parakeet: NVIDIA's Parakeet TDT 0.6B v2, int8, in sherpa-onnx (local/assistant/parakeet, fetched by setup), then the
  name pass;
- soniox: Soniox's cloud service, the person's opt-in with their own key (cloud.py), streamed while the button is held,
  Whisper standing in when it's missing, down or slow.

The microphone: the client's, when voice chat's engine is up (clientaudio.py: one capture for both, echo-cancelled,
and the client keeps it from residents while the button is held). Otherwise SOJOURN_MIC (a part of a device name) if
set; in the headset, the Quest's own mic, which shows up over Link as "Headset Microphone (Oculus Virtual Audio Device)";
else the system's default. No microphone: requests are typed.
SOJOURN_MIC_FILE=path.wav makes a file the microphone (played at real time while the button is held), for tests.
"""
import os
import re
import threading
import time
import wave
from pathlib import Path

import numpy as np

RATE = 16000
BLOCK = 480  # 30 ms

# Whisper's favourite things to say about silence
HALLUCINATIONS = {"you", "thank you.", "thank you", "thanks for watching!", "thanks for watching.", ".", "bye.",
                  "thank you for watching.", "[music]", "(music)", "♪", "you."}


def read_wav(path):
    """16 kHz mono float32 from a WAV (any rate, mono or stereo, 16-bit)."""
    with wave.open(path, "rb") as w:
        n, ch, rate, width = w.getnframes(), w.getnchannels(), w.getframerate(), w.getsampwidth()
        raw = w.readframes(n)
    if width != 2:
        raise ValueError("16-bit WAV only")
    x = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768
    if ch > 1:
        x = x.reshape(-1, ch).mean(axis=1)
    if rate != RATE:
        x = np.interp(np.arange(0, len(x) * RATE / rate) * rate / RATE, np.arange(len(x)), x).astype(np.float32)
    return x


PARAKEET = Path(__file__).resolve().parents[1] / "local" / "assistant" / "parakeet" / "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8"


def _norm(s):
    return " ".join(re.sub(r"[^a-z0-9' ]", " ", s.lower()).split())


class Ears:
    def __init__(self, vr, model="small.en", threads=4, log=print, api=None, hearing="whisper", words=None):
        self.log = log
        self.words = words          # hearing.Words: the names around the person (None: no prompt, no name pass)
        self.threads = threads
        self.whisper_name = model
        self.whisper = None
        self.parakeet = None
        self.hearing = None
        self.client = None
        if api is not None and not os.environ.get("SOJOURN_MIC_FILE"):
            from clientaudio import ClientAudio
            info = ClientAudio(api).info()
            if info.get("available"):
                self.client = ClientAudio(api)
                self.client_device = info.get("device") or "the client's mic"
        self.vr = vr
        self.mic = None
        self.mic_name = None
        self.notice = None
        self.file = os.environ.get("SOJOURN_MIC_FILE") or None
        self._rec = None
        self._chunks = []
        self._stop = threading.Event()
        self._thread = None
        self.started_at = 0.0
        self.stopped_at = 0.0
        self.pick_mic()
        self.lock = threading.Lock()
        self.set_hearing(hearing)
        self.log(f"mic: {self.mic_name or 'none'}")

    # --- which recogniser --------------------------------------------------------------------------------------------

    def _load(self, which):
        t = time.time()
        if which == "parakeet":
            import sherpa_onnx
            d = PARAKEET
            r = sherpa_onnx.OfflineRecognizer.from_transducer(
                str(d / "encoder.int8.onnx"), str(d / "decoder.int8.onnx"), str(d / "joiner.int8.onnx"),
                str(d / "tokens.txt"), num_threads=self.threads, model_type="nemo_transducer", provider="cpu")
            self.parakeet = r
        else:
            from faster_whisper import WhisperModel
            self.whisper = WhisperModel(self.whisper_name, device="cpu", compute_type="int8", cpu_threads=self.threads)
        self._recognise(which, np.zeros(RATE // 2, dtype=np.float32))  # warm up
        self.log(f"{'parakeet tdt 0.6b v2' if which == 'parakeet' else 'whisper ' + self.whisper_name} ready in {time.time() - t:.1f}s")

    def set_hearing(self, which):
        """whisper, parakeet or soniox (the person's setting). The first time, a local model loads here (a few seconds;
        the other one keeps hearing meanwhile if it's up). Parakeet missing: Whisper, with a notice. Soniox streams
        while the button is held, with Whisper standing by (no key, down or slow: Whisper hears it)."""
        which = which if which in ("whisper", "parakeet", "soniox") else "whisper"
        if which == self.hearing:
            return
        if which == "soniox":
            if self.whisper is None:
                self._load("whisper")
            self.hearing = "soniox"
            import cloud
            self.notice = None if cloud.available() else "Soniox needs its key (python tools/assistant.py key --soniox); hearing with Whisper meanwhile."
            return
        if which == "parakeet" and not (PARAKEET / "encoder.int8.onnx").exists():
            self.notice = "Parakeet isn't set up (python tools/assistant.py setup); hearing with Whisper."
            which = "whisper"
            if which == self.hearing:
                return
        try:
            if (self.parakeet if which == "parakeet" else self.whisper) is None:
                self._load(which)
        except Exception as e:
            self.log(f"{which} didn't load: {e!r}")
            if which == "parakeet":
                self.notice = "Parakeet didn't load; hearing with Whisper."
                return self.set_hearing("whisper") if self.hearing is None else None
            raise
        self.hearing = which
        if self.notice and ("Parakeet" in self.notice or "Soniox" in self.notice):
            self.notice = None

    def pick_mic(self):
        if self.client:
            self.mic_name = f"{self.client_device} (through voice chat's engine)"
            return
        if self.file:
            self.mic_name = f"file {os.path.basename(self.file)}"
            return
        try:
            import soundcard as sc
            mics = sc.all_microphones()
        except Exception as e:  # no audio stack, no devices
            self.mic, self.mic_name = None, None
            self.notice = f"No microphone ({e}). Type requests: Enter on the PC keyboard."
            return
        want = os.environ.get("SOJOURN_MIC")
        pick = None
        if want:
            pick = next((m for m in mics if want.lower() in m.name.lower()), None)
            if pick is None:
                self.notice = f"No microphone matching \"{want}\"; using the default."
        if pick is None and self.vr:
            pick = next((m for m in mics if any(k in m.name.lower() for k in ("oculus", "quest", "headset microphone"))), None)
            if pick is None:
                self.notice = "The headset's microphone isn't there (is Link connected?); using the PC's default."
        if pick is None:
            try:
                pick = sc.default_microphone()
            except Exception:
                pick = mics[0] if mics else None
        self.mic = pick
        self.mic_name = pick.name if pick else None
        if pick is None:
            self.notice = "No microphone found. Type requests: Enter on the PC keyboard."

    @property
    def available(self):
        return self.mic is not None or self.file is not None or self.client is not None

    def start(self, blind=False):
        """The talk button went down: record until stop() (or take()). With Soniox chosen and its key there, the audio
        streams to it from now on (cloud.Stream), the names around the person as its context."""
        if not self.available or self._thread or getattr(self, "_client_held", False):
            return
        self.stream = None
        if self.client:
            self.started_at = time.time()
            self._client_held = True
        else:
            self._chunks = []
            self._stop.clear()
            self.started_at = time.time()
            self._thread = threading.Thread(target=self._file_loop if self.file else self._mic_loop, daemon=True)
            self._thread.start()
        if self.hearing == "soniox":
            import cloud
            if cloud.available():
                self.stream = cloud.Stream(self.live, self.words.ranked(blind) if self.words else [], self.log)

    def live(self, start):
        """The audio so far from sample `start`, and whether it's finished (the button up and the tail in)."""
        if self.client:
            try:
                r = self.client.api._req(f"/mic/live?from={start}", timeout=3) or {}
            except Exception:
                return np.zeros(0, dtype=np.float32), True
            import base64
            x = np.frombuffer(base64.b64decode(r.get("pcm") or ""), dtype="<i2").astype(np.float32) / 32768
            return x, not r.get("collecting") and not getattr(self, "_client_held", False)
        chunks = list(self._chunks)
        x = np.concatenate(chunks)[start:] if chunks else np.zeros(0, dtype=np.float32)
        return x, self._stop.is_set() and self._thread is None

    def _mic_loop(self):
        try:
            # COM is per thread on Windows; soundcard sets it up on first use in this thread
            with self.mic.recorder(samplerate=RATE, channels=1, blocksize=BLOCK) as rec:
                while not self._stop.is_set():
                    x = rec.record(numframes=BLOCK)
                    self._chunks.append(np.asarray(x[:, 0] if x.ndim > 1 else x, dtype=np.float32))
        except Exception as e:
            self.log(f"mic failed: {e}")
            self.notice = f"The microphone failed ({e}). Type requests: Enter on the PC keyboard."

    def _file_loop(self):
        x = read_wav(self.file)
        i = 0
        while not self._stop.is_set():
            if i < len(x):
                self._chunks.append(x[i:i + BLOCK])
                i += BLOCK
            time.sleep(BLOCK / RATE)

    def release(self):
        """The talk button came up: recording ends (the client's tail still comes in for the stream)."""
        self.stopped_at = time.time()
        if self.client and getattr(self, "_client_held", False):
            self._client_held = False
        elif self._thread:
            self._stop.set()
            self._thread.join(timeout=2)
            self._thread = None

    def take(self):
        """After release(): the whole utterance (16 kHz float32), from the client's engine or our own recording."""
        if self.client:
            return self.client.utterance(wait=3.0)
        return np.concatenate(self._chunks) if self._chunks else np.zeros(0, dtype=np.float32)

    def stop(self, blind=False):
        """The talk button came up: what was said ("" for nothing), the audio's length in seconds, and what heard it
        (soniox, or the local model: whisper, parakeet, or whisper standing in for soniox)."""
        held = getattr(self, "_client_held", False) or self._thread is not None
        if not held:
            return "", 0.0, self.hearing
        self.release()
        stream, self.stream = getattr(self, "stream", None), None
        if stream:
            stream.sent_all.wait(1.0)   # its last audio read before the client hands the utterance over
        audio = self.take()
        if len(audio) < RATE * 0.25:
            return "", len(audio) / RATE, self.hearing
        if stream:
            # Soniox's words if they come within 1.5 s of the release; the local model starts after 0.8 s, in case
            text = stream.result(self.stopped_at + 0.8)
            if text is None and stream.error:   # down or refused: no point waiting
                self.log(f"soniox: {stream.error}; whisper heard it")
                return self.transcribe(audio, blind), len(audio) / RATE, "soniox->whisper"
            if text is None:
                local = {}
                th = threading.Thread(target=lambda: local.setdefault("text", self.transcribe(audio, blind)), daemon=True)
                th.start()
                text = stream.result(self.stopped_at + 1.5)
                if text is None:
                    th.join()
                    self.log(f"soniox: {'no answer in 1.5 s' if not stream.error else stream.error}; whisper heard it")
                    return local.get("text", ""), len(audio) / RATE, "soniox->whisper"
            return text, len(audio) / RATE, "soniox"
        return self.transcribe(audio, blind), len(audio) / RATE, self.hearing

    def transcribe(self, audio, blind=False):
        """What was said. blind: an incognito spell the assistant isn't let see, so only what the person wears and the
        general vocabulary help (hearing.Words.ranked)."""
        with self.lock:
            return self._recognise(self.hearing, audio, blind)

    def _recognise(self, which, audio, blind=False):
        # (soniox streams from start(); anything else it would hear, a file say, Whisper hears)
        if which == "parakeet":
            s = self.parakeet.create_stream()
            s.accept_waveform(RATE, audio)
            self.parakeet.decode_stream(s)
            text = s.result.text.strip()
            if text.lower() in HALLUCINATIONS:
                return ""
            return self._names(text, blind)
        prompt = self.words.prompt(self.whisper.hf_tokenizer, blind=blind) if self.words else None
        # Timestamps on, so a long request comes back as sentence-sized segments rather than one per 30 s window, and a
        # segment is dropped as silence only when Whisper is both sure there's no speech and unsure of the words (its
        # own rule). Keeping only segments good on both counts lost the first 30 s of a 34 s request whole, leaving
        # just its tail (2026-10-27).
        parts, _ = self.whisper.transcribe(audio, language="en", vad_filter=True, beam_size=3,
                                           condition_on_previous_text=False, without_timestamps=False,
                                           initial_prompt=prompt)
        kept = [p.text.strip() for p in parts if not (p.no_speech_prob > 0.6 and p.avg_logprob < -1.0)
                and p.text.strip().lower() not in HALLUCINATIONS]
        text = " ".join(kept).strip()
        # a prompted Whisper can say its prompt back over noise or silence: a run of the prompt's own words is dropped
        if prompt and len(text.split()) >= 2 and _norm(text) in _norm(prompt):
            return ""
        return self._names(text, blind)

    def _names(self, text, blind):
        """hearing.py's name pass over what either local model heard: a name put in where the words are close to one
        (Whisper's vocabulary tells everyday words, which need a close fit)."""
        if not text or not self.words:
            return text
        import hearing
        if getattr(self, "_everyday", None) is None and self.whisper is not None:
            self._everyday = hearing.everyday(self.whisper.hf_tokenizer)
        return hearing.fix_names(text, self.words.ranked(blind), is_everyday=getattr(self, "_everyday", None))
