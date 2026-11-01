"""Sojourn's voice: build the native shim and test it.

  python tools/voice.py fetch            LL's libwebrtc (pinned, checked against its SHA-256) into local/webrtc
  python tools/voice.py build            build src/Sojourn.Voice.Native into local/voice/build/Release (if stale; --force)
  python tools/voice.py devices          the audio endpoints the shim sees
  python tools/voice.py chirp [--desktop]  the headset's echo path (in the headset, Link on): a sweep played to
                                         the Oculus headphones, the Oculus mic recorded raw and echo-cancelled;
                                         prints the round trip, the echo's level and what cancelling leaves
  python tools/voice.py loopback         the shim alone: two in-process peer connections, a WAV through them,
                                         checked for delay, likeness, the mute and the assistant's gate
  python tools/voice.py devices-change   the shim following devices, simulated on real ones (nothing played;
                                         virtual devices where there are some): a stream lost, the device asked
                                         for gone and back, nothing there at all, the default moving; checked for
                                         recovery, the engine running on throughout, no spinning, no waiting
  python tools/voice.py roundtrip        two clients on the local grid (grid.py voice up): Sojourn Friend plays
                                         speech as its mic, Sojourn Pilot records what it hears; checked for the
                                         words (Whisper), mouth-to-ear latency, and the stereo balance flipping
                                         when Friend moves from Pilot's one side to the other
  python tools/voice.py scenarios [NAME..]  the rest of estate and parcel voice, each a short run of the two:
                                         neighbours (hearing across a border through a neighbour's connection),
                                         crossing (the mic follows you into the next region, sent through one
                                         connection only), parcel (a parcel's own channel), off (no voice on a
                                         parcel), retry (the mixer restarts mid-call and voice comes back),
                                         gate (holding the assistant's button: residents hear nothing, the
                                         assistant gets the mic through GET /mic), echo (a synthetic echo path
                                         in Pilot's audio, cancelled so Friend doesn't hear itself come back),
                                         call (Pilot calls Friend 100 m away, Friend answers with its panel's
                                         key, they hear each other in the call, Pilot hangs up, back to nearby)

The shim links LL's prebuilt libwebrtc m144 in local/webrtc (`fetch` puts it there; docs/voice.md > Building), built
with Visual Studio 2022's C++ tools and CMake as tools/buildenv.py finds them.
"""
import base64
import hashlib
import json
import math
import os
import shutil
import struct
import subprocess
import sys
import tarfile
import time
import urllib.request
from pathlib import Path

import buildenv

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "Sojourn.Voice.Native"
OUT = ROOT / "local" / "voice"
BUILD = OUT / "build"
DLL = BUILD / "Release" / "sojourn_voice.dll"   # the client's csproj copies it from here
LOOPBACK = BUILD / "Release" / "sv_loopback.exe"
WEBRTC = ROOT / "local" / "webrtc"
BELOW_NORMAL = 0x00004000
# LL's build of libwebrtc (secondlife/3p-webrtc-build): update the release, asset and hash together (the release's API
# answer lists each asset's sha256)
WEBRTC_RELEASE = "m144.7559.06.20"
WEBRTC_ASSET = "webrtc-m144.7559.06.20.36004322351-windows64-36004322351.tar.zst"
WEBRTC_SHA256 = "b7d9a8d51cf0000b759f96f3379b212424d879502159bbe361b8856619183f38"


STAMP = BUILD / "built.stamp"


def stale():
    if not DLL.exists() or not LOOPBACK.exists() or not STAMP.exists():
        return True
    built = STAMP.stat().st_mtime
    return any(p.stat().st_mtime > built for p in SRC.rglob("*") if p.is_file())


def build(force=False):
    if not force and not stale():
        return 0
    if not webrtc_present():
        print(f"no libwebrtc in {WEBRTC}: python tools/voice.py fetch", file=sys.stderr)
        return 1
    cmake = buildenv.cmake() or "cmake"
    BUILD.mkdir(parents=True, exist_ok=True)
    if not (BUILD / "CMakeCache.txt").exists():
        r = subprocess.run([cmake, "-S", str(SRC), "-B", str(BUILD), "-G", buildenv.CMAKE_GENERATOR, "-A", "x64"],
                           creationflags=BELOW_NORMAL)
        if r.returncode:
            return r.returncode
    r = subprocess.run([cmake, "--build", str(BUILD), "--config", "Release", "--", "/m:3", "/v:m", "/nologo"],
                       creationflags=BELOW_NORMAL)
    if r.returncode:
        return r.returncode
    STAMP.write_text(time.strftime("%Y-%m-%d %H:%M:%S"))
    print(f"built {DLL} ({DLL.stat().st_size // 1024} KB)")
    return 0


def webrtc_present():
    return (WEBRTC / "lib" / "release" / "webrtc.lib").exists() and (WEBRTC / "include").is_dir()


def fetch():
    """LL's libwebrtc into local/webrtc (~80 MB downloaded, ~800 MB unpacked). Run again after a failure, it starts
    the unpacking over; a whole download is kept until it's unpacked."""
    if webrtc_present():
        print(f"libwebrtc already in {WEBRTC}")
        return 0
    dl = ROOT / "local" / "webrtc-dl"
    dl.mkdir(parents=True, exist_ok=True)
    arc = dl / WEBRTC_ASSET
    if not arc.exists():
        url = f"https://github.com/secondlife/3p-webrtc-build/releases/download/{WEBRTC_RELEASE}/{WEBRTC_ASSET}"
        print(f"fetching {url}")
        part = arc.with_name(arc.name + ".part")
        req = urllib.request.Request(url, headers={"User-Agent": "Sojourn-voice-fetch"})
        with urllib.request.urlopen(req, timeout=60) as r, open(part, "wb") as f:
            shutil.copyfileobj(r, f, 1 << 20)
        part.replace(arc)
    h = hashlib.sha256()
    with open(arc, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    if h.hexdigest() != WEBRTC_SHA256:
        arc.unlink()
        print("the libwebrtc archive's hash doesn't match (deleted): run fetch again; if it keeps failing, the release "
              "changed upstream", file=sys.stderr)
        return 1
    tmp = ROOT / "local" / "webrtc-x"
    shutil.rmtree(tmp, ignore_errors=True)
    tmp.mkdir(parents=True)
    if rc := unpack_zst(arc, tmp):
        return rc
    # include/, lib/ and LICENSES/ at the archive's top (or under one folder)
    top = tmp if (tmp / "include").is_dir() else next((d for d in tmp.iterdir() if (d / "include").is_dir()), None)
    if top is None:
        print(f"no include/ in the unpacked archive ({tmp})", file=sys.stderr)
        return 1
    shutil.rmtree(WEBRTC, ignore_errors=True)   # an earlier, incomplete one
    shutil.move(str(top), str(WEBRTC))
    shutil.rmtree(tmp, ignore_errors=True)
    shutil.rmtree(dl, ignore_errors=True)
    print(f"libwebrtc {WEBRTC_RELEASE} in {WEBRTC}")
    return 0


def unpack_zst(arc, dest):
    """A .tar.zst unpacked into dest: by Python's own zstd (3.14+), else the zstandard package, else Windows' tar.exe
    (Windows 11's reads zstd; Windows 10's may not). Git's GNU tar is never used: it can't read Windows paths."""
    try:
        with tarfile.open(arc, "r:zst") as t:
            t.extractall(dest, filter="data")
        return 0
    except (tarfile.CompressionError, tarfile.ReadError):
        pass
    try:
        import zstandard
        with open(arc, "rb") as f, zstandard.ZstdDecompressor().stream_reader(f) as z, \
                tarfile.open(fileobj=z, mode="r|") as t:
            t.extractall(dest, filter="data")
        return 0
    except ImportError:
        pass
    tar = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "tar.exe"
    if tar.exists() and subprocess.run([str(tar), "-xf", str(arc), "-C", str(dest)],
                                       creationflags=BELOW_NORMAL).returncode == 0:
        return 0
    print("couldn't unpack the .tar.zst: run this with Python 3.14 or newer, or `pip install zstandard` for the Python "
          "running it", file=sys.stderr)
    return 1


# --- WAV helpers (16-bit PCM) ---

def write_wav(path, samples, rate=48000, channels=1):
    data = struct.pack(f"<{len(samples)}h", *[max(-32768, min(32767, int(round(s * 32767)))) for s in samples])
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt " +
                struct.pack("<IHHIIHH", 16, 1, channels, rate, rate * 2 * channels, 2 * channels, 16) +
                b"data" + struct.pack("<I", len(data)) + data)


def read_wav(path):
    """(rate, channels, list of per-channel float lists)"""
    raw = Path(path).read_bytes()
    pos, rate, ch, bits, data = 12, 0, 0, 0, b""
    while pos + 8 <= len(raw):
        tag, n = raw[pos:pos + 4], struct.unpack("<I", raw[pos + 4:pos + 8])[0]
        body = raw[pos + 8:pos + 8 + n]
        if tag == b"fmt ":
            _, ch, rate, _, _, bits = struct.unpack("<HHIIHH", body[:16])
        elif tag == b"data":
            data = body
        pos += 8 + n + (n & 1)
    assert bits == 16, "16-bit WAVs only"
    count = len(data) // 2
    vals = struct.unpack(f"<{count}h", data[:count * 2])
    chans = [[v / 32768 for v in vals[c::ch]] for c in range(ch)]
    return rate, ch, chans


def test_signal(rate=48000):
    """A speech-like test signal: syllable-length bursts of harmonics with a moving pitch, and gaps.
    Stationary tones get eaten by noise suppression; this doesn't."""
    out = []
    t = 0.0
    for k in range(14):
        dur = 0.18 + 0.07 * (k % 3)
        f0 = 140 + 40 * math.sin(k * 1.3)
        n = int(dur * rate)
        for i in range(n):
            env = math.sin(math.pi * i / n) ** 0.6
            f = f0 * (1 + 0.15 * i / n)
            s = sum(math.sin(2 * math.pi * f * h * t) / h for h in range(1, 9))
            out.append(0.18 * env * s)
            t += 1 / rate
        gap = int((0.09 + 0.05 * (k % 2)) * rate)
        out.extend([0.0] * gap)
        t += gap / rate
    return out


def envelope(x, rate, ms=10):
    w = int(rate * ms / 1000)
    return [math.sqrt(sum(v * v for v in x[i:i + w]) / w) for i in range(0, len(x) - w, w)]


def best_lag(ref, sig, max_lag):
    """Lag (in envelope steps) of sig behind ref with the highest normalised correlation."""
    def corr(a, b):
        n = min(len(a), len(b))
        if n < 10:
            return 0
        a, b = a[:n], b[:n]
        ma, mb = sum(a) / n, sum(b) / n
        num = sum((x - ma) * (y - mb) for x, y in zip(a, b))
        den = math.sqrt(sum((x - ma) ** 2 for x in a) * sum((y - mb) ** 2 for y in b))
        return num / den if den else 0
    best = max(range(max_lag), key=lambda L: corr(ref, sig[L:]))
    return best, corr(ref, sig[best:])


VENV_PY = ROOT / "local" / "assistant-venv" / "Scripts" / "python.exe"
HEAR = """
import sys, wave
import numpy as np
from faster_whisper import WhisperModel
m = WhisperModel("small.en", device="cpu", compute_type="int8", cpu_threads=3)
for f in sys.argv[1:]:
    with wave.open(f) as w:
        ch, rate, n = w.getnchannels(), w.getframerate(), w.getnframes()
        x = np.frombuffer(w.readframes(n), dtype=np.int16).astype(np.float32) / 32768
    x = x.reshape(-1, ch).mean(axis=1)
    t = np.arange(0, len(x) / rate, 1 / 16000)
    y = np.interp(t, np.arange(len(x)) / rate, x).astype(np.float32)
    print(" ".join(s.text.strip() for s in m.transcribe(y, beam_size=1)[0]))
"""


def hear(*wavs):
    """What the assistant's Whisper hears in each WAV (None without the assistant's venv)."""
    if not VENV_PY.exists():
        return None
    r = subprocess.run([str(VENV_PY), "-c", HEAR, *map(str, wavs)], capture_output=True, text=True,
                       creationflags=BELOW_NORMAL)
    return r.stdout.strip().splitlines() if r.returncode == 0 else None


def run_loopback(inp, out, seconds, *extra):
    r = subprocess.run([str(LOOPBACK), "run", str(inp), str(out), str(seconds), *extra], capture_output=True,
                       text=True, creationflags=BELOW_NORMAL, timeout=seconds + 30)
    events = [json.loads(line) for line in r.stdout.splitlines() if line.startswith("{")]
    if r.returncode:
        print(r.stdout, r.stderr, file=sys.stderr)
    return r.returncode, events, r.stderr


def loopback():
    if build():
        return 1
    tmp = OUT / "test"
    tmp.mkdir(parents=True, exist_ok=True)
    sig = test_signal()
    inp = tmp / "in.wav"
    write_wav(inp, sig)
    sig_s = len(sig) / 48000
    ok = True

    def check(name, cond, detail):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}")

    # Processing (NS, AGC) reshapes a synthetic signal, so the processed run uses real speech if there is some
    # (tools/assistant.py tts makes it), and the raw run the synthetic one.
    speech = sorted((ROOT / "local" / "assistant" / "speech").glob("*.wav"))[:3]
    runs = [("raw", inp, envelope(sig, 48000), sig_s, ["--raw"])]
    if speech:
        joined, srate = [], 0
        for f in speech:
            srate, _, chans = read_wav(f)
            joined += chans[0] + [0.0] * (srate // 3)
        write_wav(tmp / "speech.wav", joined, srate)
        runs.append(("processed", tmp / "speech.wav", envelope(joined, srate), len(joined) / srate, ["--no-aec"]))
    else:
        runs.append(("processed", inp, envelope(sig, 48000), sig_s, ["--no-aec"]))
    for mode, inp_wav, ref, dur, extra in runs:
        out = tmp / f"out-{mode}.wav"
        code, ev, err = run_loopback(inp_wav, out, dur + 1.5, *extra)
        done = next((e for e in ev if e.get("event") == "done"), None)
        conn = next((e for e in ev if e.get("event") == "connected"), {})
        check(f"{mode}: connects", code == 0 and done, f"{conn.get('ms')} ms, data channel both ways: "
              f"{done and done['data_a'] and done['data_b']}")
        if not done:
            continue
        _, ch, chans = read_wav(out)
        left = chans[0]
        env = envelope(left, 48000)
        lag, c = best_lag(ref, env, 60)
        level = max(env) if env else 0
        check(f"{mode}: the signal comes through", c > (0.95 if mode == "raw" else 0.6) and level > 0.02,   # processed: AGC and NS reshape it; Whisper is the real check
              f"delay {lag * 10} ms, envelope correlation {c:.3f}, peak level {20 * math.log10(level + 1e-9):.1f} dBFS, "
              f"{ch} channels")
        if mode == "processed" and speech:
            words = hear(inp_wav, out)
            if words and len(words) == 2:
                check("processed: Whisper hears the same words", words[0] == words[1], f"'{words[1]}'")
        st = done["stats"]
        check(f"{mode}: audio threads ran", st["capture_frames"] > 50 and st["render_frames"] > 50,
              f"{st['capture_frames']} capture / {st['render_frames']} render frames")

    # The mute: speech for 1.5 s, then muted; after the fade and the network's tail, silence.
    out = tmp / "out-mute.wav"
    long_sig = sig * 2
    write_wav(tmp / "in-long.wav", long_sig)
    code, ev, _ = run_loopback(tmp / "in-long.wav", out, 4.0, "--mute-at", "1.5")
    _, _, chans = read_wav(out)
    env = envelope(chans[0], 48000)
    before = max(env[50:140]) if len(env) > 140 else 0
    after = max(env[260:390]) if len(env) > 390 else 1
    check("mute gives silence", before > 0.02 and after < 0.001,
          f"peak before {20 * math.log10(before + 1e-9):.1f} dBFS, after {20 * math.log10(after + 1e-9):.1f} dBFS")

    # The assistant's gate: open 1.0-2.0 s; residents get nothing while it's open (and its tail), the tap gets
    # the mic (with 300 ms pre-roll).
    out = tmp / "out-gate.wav"
    code, ev, _ = run_loopback(tmp / "in-long.wav", out, 4.0, "--gate-at", "1.0", "--gate-for", "1.0")
    done = next((e for e in ev if e.get("event") == "done"), {})
    _, _, chans = read_wav(out)
    env = envelope(chans[0], 48000)
    # the path's own delay is ~40-120 ms; look well inside the gated window
    inside = max(env[140:200]) if len(env) > 200 else 1
    outside = max(env[30:90]) if len(env) > 90 else 0
    tap_ms = done.get("tap_frames", 0) / 16
    check("assistant gate keeps the mic from residents", inside < 0.001 and outside > 0.02,
          f"peak outside {20 * math.log10(outside + 1e-9):.1f} dBFS, inside {20 * math.log10(inside + 1e-9):.1f} dBFS")
    check("assistant gate taps the mic", 1400 <= tap_ms <= 1700,
          f"{tap_ms:.0f} ms tapped (1000 held + 300 pre-roll + 250 tail)")
    print("loopback:", "passed" if ok else "FAILED")
    return 0 if ok else 1


DOTNET = buildenv.DOTNET
# SOJOURN_CLIENT_ROOT=dir: a worktree whose build to test (another session's half-done edits can break the main
# tree's build mid-test; local/voice/wt-sync.sh keeps one at HEAD plus voice's working files)
CLIENT_ROOT = Path(os.environ.get("SOJOURN_CLIENT_ROOT") or ROOT)
# SOJOURN_CLIENT_DLL=path: a client built elsewhere (an agent's own output dir inside the project); not rebuilt here
CLIENT = Path(os.environ.get("SOJOURN_CLIENT_DLL") or CLIENT_ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll")


# the test's own two accounts (tools/grid.py account Voice Pilot|Friend sojourn-local), so they never kick another
# session's Sojourn Pilot or Friend off the grid
PILOT = ["--first", "Voice", "--last", "Pilot", "--password", "sojourn-local", "--bridge-port", "9730",
         "--base-dir", str(ROOT / "local" / "voice" / "pilot")]


def api(port, pump, op, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": 20}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=40) as r:
        return json.loads(r.read())


def client(name, log, env_extra, *args):
    env = dict(os.environ, **env_extra)
    f = open(log, "w", encoding="utf-8")
    return subprocess.Popen([str(DOTNET), str(CLIENT), "--window", "320x180", "--fps", "5", "--no-assistant", "--no-sounds", *args],
                            cwd=CLIENT_ROOT, env=env, stdin=subprocess.DEVNULL, stdout=f, stderr=subprocess.STDOUT, text=True,
                            creationflags=BELOW_NORMAL), f


def wait_log(path, needle, timeout):
    end = time.time() + timeout
    while time.time() < end:
        if needle in Path(path).read_text(encoding="utf-8", errors="replace"):
            return True
        time.sleep(0.5)
    return False


def stats_from(log):
    for line in Path(log).read_text(encoding="utf-8", errors="replace").splitlines():
        if "voice stats: " in line:
            return json.loads(line.split("voice stats: ", 1)[1])
    return {}


def roundtrip():
    """Pilot at Atelier 128,118 (its camera behind it, facing the way it faces at login); Friend 6 m to one side,
    then teleported 6 m to the other side half way through."""
    if build() or (CLIENT_ROOT == ROOT and not os.environ.get("SOJOURN_CLIENT_DLL") and subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"]).returncode):
        return 1
    grid = [sys.executable, str(ROOT / "tools" / "grid.py")]
    subprocess.run(grid + ["start"], check=True)
    print(subprocess.run(grid + ["voice", "up"], capture_output=True, text=True).stdout.strip())
    out = OUT / "rt"
    out.mkdir(parents=True, exist_ok=True)
    speech = sorted((ROOT / "local" / "assistant" / "speech").glob("*.wav"))[:3]
    if not speech:
        print("no test speech in local/assistant/speech (tools/assistant.py tts makes it)")
        return 1
    joined, srate = [], 0
    for f in speech:
        srate, _, chans = read_wav(f)
        joined += chans[0] + [0.0] * (srate // 3)
    joined += [0.0] * int(srate * 1.2)   # a gap, so the loop's period can't be confused with the latency
    source = out / "source.wav"
    write_wav(source, joined, srate)
    loop_s = len(joined) / srate
    heard = out / "heard.wav"
    if heard.exists():
        heard.unlink()

    friend, ff = client("friend", out / "friend.log", {"SOJOURN_VOICE_MIC_FILE": f"loop:{source}"},
                        "--first", "Voice", "--last", "Friend", "--password", "sojourn-local", "--bridge-port", "9731",
                        "--base-dir", str(ROOT / "local" / "voice" / "friend"), "--start", "uri:Atelier&128&124&24",
                        "--voice-mode", "open", "--voice-unmuted", "--minutes", "1.05")
    pilot, pf = client("pilot", out / "pilot.log", {"SOJOURN_VOICE_OUT_FILE": str(heard)}, *PILOT, "--start", "uri:Atelier&128&118&24", "--minutes", "1")
    ok = True

    def check(name, cond, detail):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}")

    try:
        up = wait_log(out / "pilot.log", "connected, 1 other", 60) and wait_log(out / "friend.log", "connected, 1 other", 30)
        check("both in nearby voice, each seeing the other", up, "")
        if not up:
            return 1
        t_conn = time.time()
        time.sleep(14)
        t_move = time.time()
        api(9731, "LLAgent", "requestTeleport", regionname="Atelier", x=128, y=112, z=24, skip_confirmation=True)
        time.sleep(14)
        t_end = time.time()
    finally:
        # they quit on their own (--minutes); the console's /quit isn't read from a pipe
        for proc in (friend, pilot):
            try:
                proc.wait(timeout=90)
            except subprocess.TimeoutExpired:
                proc.kill()
        ff.close()
        pf.close()

    ps, fs = stats_from(out / "pilot.log"), stats_from(out / "friend.log")
    rate, _, (left, right) = read_wav(heard)
    words = hear(heard)
    said = hear(source)
    if words and said:
        got = set(w.strip(".,?!").lower() for w in words[0].split())
        want = set(w.strip(".,?!").lower() for w in said[0].split())
        check("Pilot hears Friend's words", len(want & got) >= 0.9 * len(want), f"{len(want & got)}/{len(want)} words")

    # latency: where the heard envelope lines up with the looped source, given both files' start times
    env_h = envelope([(a + b) / 2 for a, b in zip(left, right)], rate)
    env_s = envelope(joined, srate)
    n_loop = len(env_s)
    offset = (ps["render_file_t0"] - fs["capture_file_t0"]) / 1000   # heard's t=0 in the source's time
    start = int((t_conn - ps["render_file_t0"] / 1000 + 2) * 100)
    seg = env_h[start:start + 1000]

    def corr_at(lag):
        ref = [env_s[int(((start + i) / 100 + offset - lag / 100) * 100) % n_loop] for i in range(len(seg))]
        ma, mb = sum(seg) / len(seg), sum(ref) / len(ref)
        num = sum((a - ma) * (b - mb) for a, b in zip(seg, ref))
        den = math.sqrt(sum((a - ma) ** 2 for a in seg) * sum((b - mb) ** 2 for b in ref))
        return num / den if den else 0
    best = max(range(0, 100), key=corr_at)   # 0-1 s
    check("mouth-to-ear latency", corr_at(best) > 0.6 and best * 10 < 600,
          f"{best * 10} ms (file mic to file speaker, through the local mixer; correlation {corr_at(best):.2f})")

    def balance(t0, t1):
        a, b = int((t0 - ps["render_file_t0"] / 1000) * rate), int((t1 - ps["render_file_t0"] / 1000) * rate)
        l = math.sqrt(sum(x * x for x in left[a:b]) / max(1, b - a))
        r = math.sqrt(sum(x * x for x in right[a:b]) / max(1, b - a))
        return 20 * math.log10((l + 1e-9) / (r + 1e-9))
    before, after = balance(t_conn + 2, t_move - 1), balance(t_move + 3, t_end - 1)
    check("the balance follows Friend from one side to the other", before * after < 0 and abs(before - after) > 3,
          f"L-R {before:+.1f} dB with Friend north, {after:+.1f} dB south")
    print("roundtrip:", "passed" if ok else "FAILED")
    return 0 if ok else 1


def pair(name, pilot_start, friend_start, seconds, during=None, speaker="friend", procs=None):
    """Pilot and Friend on the local grid for a while. One of them plays the test speech as an open, unmuted mic
    (speaker), the other records what it hears. during(t0) runs once both are in voice. Returns (dir, heard, ok)."""
    out = OUT / name
    out.mkdir(parents=True, exist_ok=True)
    source = OUT / "rt" / "source.wav"
    heard = out / "heard.wav"
    if heard.exists():
        heard.unlink()
    talk = {"SOJOURN_VOICE_MIC_FILE": f"loop:{source}", "SOJOURN_VOICE_LOG": "1"}
    listen = {"SOJOURN_VOICE_OUT_FILE": str(heard), "SOJOURN_VOICE_LOG": "1"}
    talk_args = ["--voice-mode", "open", "--voice-unmuted"]
    minutes = str(seconds / 60)
    # Pilot first, Friend once Pilot is in: both logging in at once has cost Pilot a neighbour's circuit
    pilot, pf = client("pilot", out / "pilot.log", listen if speaker == "friend" else talk, *PILOT, "--start", pilot_start,
                       "--minutes", minutes, *(talk_args if speaker == "pilot" else []))
    if procs is not None:
        procs["pilot"] = pilot
    wait_log(out / "pilot.log", "voice: joining", 40)
    time.sleep(3)
    friend, ff = client("friend", out / "friend.log", talk if speaker == "friend" else listen,
                        "--first", "Voice", "--last", "Friend", "--password", "sojourn-local", "--bridge-port", "9731",
                        "--base-dir", str(ROOT / "local" / "voice" / "friend"), "--start", friend_start, "--minutes", minutes,
                        *(talk_args if speaker == "friend" else []))
    if procs is not None:
        procs["friend"] = friend
    try:
        up = wait_log(out / "pilot.log", "voice: nearby voice: connected", 40) or wait_log(out / "pilot.log", "parcel voice: connected", 5)
        if up and during:
            during(time.time())
    finally:
        for proc in (friend, pilot):
            try:
                proc.wait(timeout=seconds + 60)
            except subprocess.TimeoutExpired:
                proc.kill()
        ff.close()
        pf.close()
    return out, heard, up


def log_lines(path, needle):
    return [l for l in Path(path).read_text(encoding="utf-8", errors="replace").splitlines() if needle in l]


def heard_words(heard):
    said = hear(OUT / "rt" / "source.wav")
    got = hear(heard) if Path(heard).exists() else None
    if not said or not got:
        return 0, 1
    want = set(w.strip(".,?!").lower() for w in said[0].split())
    have = set(w.strip(".,?!").lower() for w in got[0].split())
    return len(want & have), len(want)


def scenarios(names):
    if build() or (CLIENT_ROOT == ROOT and not os.environ.get("SOJOURN_CLIENT_DLL") and subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"]).returncode):
        return 1
    grid = [sys.executable, str(ROOT / "tools" / "grid.py")]
    subprocess.run(grid + ["start"], check=True)
    subprocess.run(grid + ["voice", "up"], capture_output=True)
    if not (OUT / "rt" / "source.wav").exists():
        print("run roundtrip once first (it makes the test speech)")
        return 1
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}{': ' + detail if detail else ''}")

    names = names or ["neighbours", "crossing", "parcel", "off", "retry", "gate", "echo", "call", "switches"]
    if "neighbours" in names:
        # Pilot near Atelier's north-east corner, Friend 40 m east in Atelier East: Pilot hears Friend only through
        # its connection to Atelier East
        out, heard, up = pair("neighbours", "uri:Atelier&236&200&24", "uri:Atelier East&20&200&24", 35)
        conns = {l.split("voice: Estate@")[1].split(" connected")[0] for l in log_lines(out / "pilot.log", " connected") if "voice: Estate@" in l}
        reach = log_lines(out / "pilot.log", "regions in reach")
        known = reach[-1].split("known: ", 1)[1] if reach else ""
        missing = [r for r in ("Atelier", "Atelier East", "Atelier North", "Atelier Northeast") if r not in conns]
        if missing and all(f"{r} 1" not in known for r in missing):
            # the client never got a circuit to it (a login race in LibreMetaverse with OpenSim's announcements,
            # docs/client.md > Gotchas): nothing voice can connect through
            print(f"warn neighbours: no circuit to {', '.join(missing)} this login, so no voice through it")
        else:
            check("neighbours: Pilot connects to all four regions", not missing, ", ".join(sorted(conns)))
        n, of = heard_words(heard)
        check("neighbours: Pilot hears Friend across the border", up and n >= 0.8 * of, f"{n}/{of} words")
    if "crossing" in names:
        # Pilot talks, walking-distance from the border; teleported 20 m over into Atelier East half way through.
        # Friend in Atelier East listens. Pilot's mic must move to its East connection and go out on one only.
        def cross(t0):
            time.sleep(12)
            api(9730, "LLAgent", "requestTeleport", regionname="Atelier East", x=12, y=118, z=24, skip_confirmation=True)
        out, heard, up = pair("crossing", "uri:Atelier&246&118&24", "uri:Atelier East&30&118&24", 40, during=cross, speaker="pilot")
        sends = [l.split("voice ")[1] for l in log_lines(out / "pilot.log", ": sending")]
        flips = log_lines(out / "pilot.log", "primary (the region you're in)")
        check("crossing: the mic goes out through Atelier, then Atelier East",
              any("@Atelier:" in x for x in sends) and any("@Atelier East:" in x for x in sends) and
              any("Atelier East" in l for l in flips), "; ".join(sends))
        ever_two = False
        sending = set()
        for l in Path(out / "pilot.log").read_text(encoding="utf-8", errors="replace").splitlines():
            if ": sending" in l:
                sending.add(l.split("voice ")[1].split(":")[0])
            elif ": not sending" in l:
                sending.discard(l.split("voice ")[1].split(":")[0])
            ever_two |= len(sending) > 1
        check("crossing: never through two connections at once", not ever_two)
        n, of = heard_words(heard)
        check("crossing: Friend hears Pilot through it", up and n >= 0.8 * of, f"{n}/{of} words")
    if "parcel" in names:
        print(subprocess.run(grid + ["voice", "parcels", "parcel"], capture_output=True, text=True).stdout.strip())
        out, heard, up = pair("parcel", "uri:Atelier&236&200&24", "uri:Atelier&226&200&24", 30)
        joined = log_lines(out / "pilot.log", "joining parcel")
        conns = [l for l in log_lines(out / "pilot.log", " connected") if "@" in l]
        check("parcel: a parcel's own channel, no neighbours", joined and conns and all("-1@Atelier connected" in l for l in conns),
              f"{len(conns)} connection(s)")
        n, of = heard_words(heard)
        check("parcel: Pilot hears Friend", n >= 0.8 * of, f"{n}/{of} words")
    if "off" in names:
        print(subprocess.run(grid + ["voice", "parcels", "off"], capture_output=True, text=True).stdout.strip())
        out, heard, up = pair("off", "uri:Atelier&128&118&24", "uri:Atelier&132&118&24", 25)
        check("off: no voice on a voice-off parcel", log_lines(out / "pilot.log", "voice is off on this parcel")
              and not [l for l in log_lines(out / "pilot.log", " connected") if "@" in l])
    if "parcel" in names or "off" in names:
        print(subprocess.run(grid + ["voice", "parcels", "estate"], capture_output=True, text=True).stdout.strip())
    if "retry" in names:
        def restart(t0):
            time.sleep(8)
            docker = subprocess.run(["docker", "restart", "sojourn-voice-janus-1"], capture_output=True, text=True)
            print("  mixer restarted" if docker.returncode == 0 else docker.stderr)
        out, heard, up = pair("retry", "uri:Atelier&128&118&24", "uri:Atelier&132&122&24", 75, during=restart)
        retries = log_lines(out / "pilot.log", "retrying in")
        ups = log_lines(out / "pilot.log", "Estate@Atelier connected")
        check("retry: the connection notices, backs off and comes back", retries and len(ups) >= 2,
              f"{len(retries)} retries, connected {len(ups)} times")
        rate, _, (left, right) = read_wav(heard)
        tail = left[-15 * rate:]
        level = math.sqrt(sum(x * x for x in tail) / max(1, len(tail)))
        check("retry: voice is back by the end", level > 0.005, f"{20 * math.log10(level + 1e-9):.1f} dBFS in the last 15 s")
    if "gate" in names:
        # Pilot talks (open mic, live); at 10 s it holds the assistant's button (T) for 3 s. Friend must hear
        # nothing of that, the assistant must get it: 300 ms before the press to 250 ms after the release.
        procs, marks, got = {}, {}, {}

        def hold(t0):
            time.sleep(10)
            r = subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId",
                                str(procs["pilot"].pid), "-Vk", "84", "-HoldMs", "3000"], capture_output=True, text=True)
            words = r.stdout.split()   # key 84 down T0 up T1 (Unix ms)
            marks["down"], marks["up"] = int(words[3]) / 1000, int(words[5]) / 1000
            got["mic"] = urllib.request.urlopen("http://127.0.0.1:9730/mic?wait=3", timeout=15).read()
        out, heard, up = pair("gate", "uri:Atelier&128&118&24", "uri:Atelier&132&122&24", 30, during=hold,
                              speaker="pilot", procs=procs)
        mic = json.loads(got.get("mic") or b"{}")
        pcm = base64.b64decode(mic.get("pcm") or "")
        check("gate: the assistant gets the mic", 3200 <= mic.get("ms", 0) <= 3900,
              f"{mic.get('ms', 0)} ms (3000 held + 300 before + 250 after, give or take the key's timing)")
        if pcm:
            write_wav(out / "assistant.wav", [v / 32768 for v in struct.unpack(f"<{len(pcm) // 2}h", pcm)], 16000)
            words = hear(out / "assistant.wav")
            check("gate: and hears words in it", words and len(words[0].split()) >= 4, f"'{words[0] if words else ''}'")
        fs = stats_from(out / "friend.log")
        rate, _, (left, right) = read_wav(heard)
        t0 = fs.get("render_file_t0", 0) / 1000

        def level(a, b):
            seg = left[int((a - t0) * rate):int((b - t0) * rate)]
            return 20 * math.log10(math.sqrt(sum(x * x for x in seg) / max(1, len(seg))) + 1e-9)
        # the path's own delay is 100-200 ms: look inside the held window, away from its edges
        inside = level(marks["down"] + 0.4, marks["up"] + 0.2)
        before = level(marks["down"] - 6, marks["down"] - 0.5)
        after = level(marks["up"] + 1.0, marks["up"] + 6)
        check("gate: Friend hears nothing while it's held", inside < -70 and before > -45 and after > -45,
              f"before {before:.0f} dBFS, held {inside:.0f} dBFS, after {after:.0f} dBFS")
    if "echo" in names:
        # Friend talks and records what it hears. Pilot's mic is silent but for an echo of what Pilot plays (150 ms,
        # -4 dB: louder than a headset's speakers into its own mic), sent as open mic: without echo cancellation
        # Friend hears itself come back through Pilot.
        levels = {}
        for apm in ("on", "off"):
            out = OUT / f"echo-{apm}"
            out.mkdir(parents=True, exist_ok=True)
            heard = out / "heard.wav"
            env_p = {"SOJOURN_VOICE_MIC_FILE": "none", "SOJOURN_VOICE_TEST_ECHO": "150,0.6"}
            if apm == "off":
                env_p["SOJOURN_VOICE_APM"] = "off"
            pilot, pf = client("pilot", out / "pilot.log", env_p, *PILOT, "--start", "uri:Atelier&128&118&24",
                               "--minutes", "0.6", "--voice-mode", "open", "--voice-unmuted")
            wait_log(out / "pilot.log", "voice: joining", 40)
            time.sleep(3)
            friend, ff = client("friend", out / "friend.log",
                                {"SOJOURN_VOICE_MIC_FILE": f"loop:{OUT / 'rt' / 'source.wav'}", "SOJOURN_VOICE_OUT_FILE": str(heard)},
                                "--first", "Voice", "--last", "Friend", "--password", "sojourn-local", "--bridge-port", "9731",
                                "--base-dir", str(ROOT / "local" / "voice" / "friend"), "--start", "uri:Atelier&132&122&24",
                                "--minutes", "0.5", "--voice-mode", "open", "--voice-unmuted")
            for proc in (friend, pilot):
                try:
                    proc.wait(timeout=120)
                except subprocess.TimeoutExpired:
                    proc.kill()
            ff.close()
            pf.close()
            rate, _, (left, right) = read_wav(heard)
            tail = left[-12 * rate:]
            levels[apm] = 20 * math.log10(math.sqrt(sum(x * x for x in tail) / max(1, len(tail))) + 1e-9)
        check("echo: Friend hears itself come back without cancellation", levels["off"] > -50, f"{levels['off']:.0f} dBFS")
        check("echo: and not with it", levels["on"] < levels["off"] - 25,
              f"{levels['on']:.0f} dBFS ({levels['off'] - levels['on']:.0f} dB less)")
    if "call" in names:
        # OpenSim 0.9.3.1 starts a P2P call (OK) but answers "call" with 400 and never rings the other side, so a
        # real call is tried on the main grid. Here: the ringing panel and its answer key (a test ring), then both in
        # one non-spatial channel (testCall, as "call"'s credentials would put them), 120 m apart, and hanging up.
        procs, seen = {}, {}
        pilot_id = "570ece80-558c-4038-b552-a7cc9d319e05"   # Voice Pilot on the local grid

        def call_flow(t0):
            time.sleep(3)
            api(9731, "SojournVoice", "testRing", id=pilot_id, name="Voice Pilot")
            time.sleep(1)
            seen["ringing"] = api(9731, "SojournVoice", "status").get("ringing")
            # Friend answers with its panel's first key ("answer"), pressed with the 1 key
            subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId",
                            str(procs["friend"].pid), "-Vk", "49"], capture_output=True)
            time.sleep(3)
            seen["after_answer"] = api(9731, "SojournVoice", "status")
            for port in (9730, 9731):
                api(port, "SojournVoice", "testCall", channel="sojourn-test-call", name="the test call")
            seen["t_call"] = time.time()
            time.sleep(16)
            seen["pilot_in"] = api(9730, "SojournVoice", "status")
            seen["friend_in"] = api(9731, "SojournVoice", "status")
            seen["t_hangup"] = time.time()
            api(9730, "SojournVoice", "hangup")
            time.sleep(9)
            seen["after"] = api(9730, "SojournVoice", "status")
            seen["friend_after"] = api(9731, "SojournVoice", "status")
        out, heard, up = pair("call", "uri:Atelier&40&40&24", "uri:Atelier&140&110&24", 55, during=call_flow, procs=procs)
        check("call: a ringing call shows on the panel", seen.get("ringing"), json.dumps(seen.get("ringing")))
        answered = log_lines(out / "friend.log", "no channel for the call with Voice Pilot")
        check("call: its answer key answers it (then the grid has no channel to give: 400)",
              answered and "ringing" not in seen.get("after_answer", {}), answered[0].split("voice: ")[-1] if answered else "")
        pi, fi = seen.get("pilot_in", {}), seen.get("friend_in", {})
        check("call: both in the call", "connected, 1 other" in pi.get("status", "") and "connected, 1 other" in fi.get("status", ""),
              f"{pi.get('status')} / {fi.get('status')}")
        n, of = heard_words(heard)
        check("call: Pilot hears Friend, 120 m away, through it", n >= 0.6 * of, f"{n}/{of} words")
        af, ff = seen.get("after", {}), seen.get("friend_after", {})
        check("call: hanging up goes back to nearby voice", "call" not in af and af.get("status", "").startswith("nearby voice"),
              af.get("status", ""))
        check("call: and Friend's side ends it once alone", "call" not in ff and ff.get("status", "").startswith("nearby voice"),
              ff.get("status", ""))
    if "switches" in names:
        # batch B10 (docs/voice.md > Calls turned away, Voice mutes): on Pilot, invitations from Friend as the region would
        # bring them (testInvite, through the person's switches), Friend's voice muted on the block list, where Pilot
        # hears from, and moderation refused before asking (OpenSim has no SpatialVoiceModerationRequest)
        import sqlite3
        sys.path.insert(0, str(ROOT / "tools"))
        import grid as gridmod
        db = sqlite3.connect(f"file:{gridmod.BIN / 'userprofiles.db'}?mode=ro", uri=True)
        friend_id = db.execute("select PrincipalID from UserAccounts where FirstName='Voice' and LastName='Friend'").fetchone()[0]
        db.close()
        seen = {}

        def call(port, pump, op, **args):
            try:
                return api(port, pump, op, **args)
            except urllib.error.HTTPError as e:
                try:
                    return json.loads(e.read())
                except Exception:
                    return {"error": f"HTTP {e.code}"}

        def invite(kind, **extra):
            call(9730, "SojournVoice", "testInvite", id=friend_id, name="Voice Friend", kind=kind, **extra)
            time.sleep(1.2)
            st = call(9730, "SojournVoice", "status")
            ringing = st.get("ringing")
            if ringing:
                call(9730, "SojournVoice", "decline")
                time.sleep(0.5)
            return ringing

        def setting(**kv):
            call(9730, "SojournSettings", "set", **kv)
            time.sleep(0.3)

        def switches(t0):
            time.sleep(2)
            seen["plain"] = invite("p2p")
            setting(reject_p2p_calls="on")
            seen["p2p_off"] = invite("p2p")
            seen["p2p_group"] = invite("group")
            setting(reject_p2p_calls="off")
            setting(friends_only_ims="on")
            seen["friends_p2p"] = invite("p2p")
            seen["friends_group"] = invite("group")
            seen["friends_conf"] = invite("conference")
            setting(friends_only_ims="off")
            setting(reject_group_calls="on", reject_conference_calls="on")
            seen["group_off"] = invite("group")
            seen["conf_off"] = invite("conference")
            seen["p2p_still"] = invite("p2p")
            setting(reject_group_calls="off", reject_conference_calls="off")
            setting(do_not_disturb="on")
            seen["dnd"] = invite("p2p")
            setting(do_not_disturb="off")
            # Friend's voice muted on the block list (a voice-only entry), then a call from them
            call(9730, "SojournVoice", "peerMute", id=friend_id, mute=True)
            time.sleep(1)
            seen["mutes"] = call(9730, "TesselAgent", "mutes").get("mutes", [])
            seen["muted_status"] = call(9730, "SojournVoice", "status")
            seen["muted_call"] = invite("p2p")
            seen["muted_group"] = invite("group")
            call(9730, "SojournVoice", "peerMute", id=friend_id, mute=False)
            time.sleep(1)
            seen["mutes_after"] = call(9730, "TesselAgent", "mutes").get("mutes", [])
            seen["calls"] = call(9730, "SojournVoice", "calls")
            # where Pilot hears from: the avatar's head (listener = speaker in the pose sent)
            setting(voice_hear_from="avatar")
            seen["t_avatar"] = time.time()
            call(9730, "LLAgent", "lookAt", position=[60, 60, 24])   # a turn: a fresh pose is sent
            time.sleep(2.5)
            seen["ear"] = call(9730, "SojournSettings", "get", name="voice_hear_from")
            setting(voice_hear_from="camera")
            seen["moderate"] = call(9730, "SojournVoice", "moderate", id=friend_id, mute=True)
            seen["moderate_all"] = call(9730, "SojournVoice", "moderateAll", mute=True)
            seen["conference"] = call(9730, "SojournVoice", "conference", ids=[friend_id])
            time.sleep(3)
        out, heard, up = pair("switches", "uri:Atelier&128&118&24", "uri:Atelier&132&122&24", 75, during=switches)
        check("switches: a call rings by default", seen.get("plain"), json.dumps(seen.get("plain")))
        check("switches: reject_p2p_calls turns away one-to-one calls, not group calls",
              not seen.get("p2p_off") and seen.get("p2p_group"), f"{seen.get('p2p_off')} / {seen.get('p2p_group')}")
        check("switches: friends only turns away a non-friend's one-to-one and conference calls, lets a group's ring",
              not seen.get("friends_p2p") and not seen.get("friends_conf") and seen.get("friends_group"),
              f"{seen.get('friends_p2p')} / {seen.get('friends_conf')} / {seen.get('friends_group')}")
        check("switches: reject_group_calls and reject_conference_calls, one-to-one still rings",
              not seen.get("group_off") and not seen.get("conf_off") and seen.get("p2p_still"))
        dnd_said = log_lines(out / "pilot.log", "voice: turned away Voice Friend's call (do_not_disturb)")
        check("switches: Do Not Disturb turns a call away", not seen.get("dnd") and dnd_said, dnd_said[0].split("voice: ")[-1] if dnd_said else "")
        entry = [m for m in seen.get("mutes", []) if m.get("id") == friend_id]
        check("switches: a voice mute is a voice-only entry on the block list", entry and entry[0].get("voice_only"), json.dumps(entry))
        people = [p for p in seen.get("muted_status", {}).get("people", []) if p.get("id") == friend_id]
        check("switches: status shows them muted by me", not people or people[0].get("muted_by_me"), json.dumps(people))
        check("switches: a voice-muted caller's call is turned away, their group's isn't",
              not seen.get("muted_call") and seen.get("muted_group"), f"{seen.get('muted_call')} / {seen.get('muted_group')}")
        check("switches: unmuting takes the entry off", not [m for m in seen.get("mutes_after", []) if m.get("id") == friend_id])
        turned = seen.get("calls", {}).get("turned_away", {})
        check("switches: counted", turned.get("p2p_calls") == 1 and turned.get("friends_only") == 2 and turned.get("voice_muted") == 1
              and turned.get("do_not_disturb") == 1, json.dumps(turned))
        poses = [json.loads(l.split("voice pose: ", 1)[1]) for l in log_lines(out / "pilot.log", "voice pose: ")]
        same = sum(1 for p in poses if p.get("lp") == p.get("sp"))
        check("switches: hearing from the avatar sends the avatar's place as the listener's (the camera's otherwise)",
              same and same < len(poses) and "avatar" in json.dumps(seen.get("ear")),
              f"{json.dumps(seen.get('ear'))}; {same} of {len(poses)} poses logged with the listener at the avatar")
        check("switches: moderation refused before asking where the region has none",
              "moderation" in json.dumps(seen.get("moderate")) and "moderation" in json.dumps(seen.get("moderate_all")),
              f"{seen.get('moderate')} / {seen.get('moderate_all')}")
        conf = log_lines(out / "pilot.log", "conference")
        print("info switches: conference on OpenSim: " + " | ".join(l.split("voice: ")[-1] for l in conf[-3:]))
    print("scenarios:", "passed" if ok else "FAILED")
    return 0 if ok else 1


CHIRP = """
import sys, wave, json
import numpy as np
def read(p):
    with wave.open(p) as w:
        return np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32) / 32768
rate = 16000
n = int(rate * 1.5)
t = np.arange(n) / n
f = 300.0 * (8000.0 / 300.0) ** t
ph = np.cumsum(2 * np.pi * f / rate)
env = np.minimum(1, np.minimum(t * 20, (1 - t) * 20))
ref = np.sin(ph) * env * 0.25
raw, proc = read(sys.argv[1]), read(sys.argv[2])
def find(x):
    if len(x) < n:
        return None
    c = np.abs(np.correlate(x, ref, "valid"))
    i = int(np.argmax(c))
    seg = x[i:i + n]
    gain = float(np.dot(seg, ref) / np.dot(ref, ref))
    return i, gain
r = find(raw)
out = {"raw_s": len(raw) / rate}
if r:
    i, g = r
    out["round_trip_ms"] = round(i / rate * 1000) - 300   # the tap starts with 300 ms from before it opened
    out["echo_db"] = round(float(20 * np.log10(abs(g) + 1e-9)), 1)
    out["mic_rms_db"] = round(float(20 * np.log10(np.sqrt(np.mean(raw ** 2)) + 1e-9)), 1)
last = proc[-int(rate * 2.0):]
if len(last):
    out["after_aec_rms_db"] = round(float(20 * np.log10(np.sqrt(np.mean(last ** 2)) + 1e-9)), 1)
print(json.dumps(out))
"""


def chirp(desktop=False, synthetic=False):
    """The echo test for the headset session: run it with the headset on and Link connected (the sweep is quiet).
    --synthetic: no devices, a made-up echo path (120 ms, -6 dB), to check the tool itself."""
    if build():
        return 1
    out = OUT / "chirp"
    out.mkdir(parents=True, exist_ok=True)
    dev = "none" if synthetic else "" if desktop else "name:Oculus Virtual Audio Device"
    r = subprocess.run([str(LOOPBACK), "chirp", dev, dev, str(out), *(["synthetic"] if synthetic else [])],
                       capture_output=True, text=True, timeout=120)
    done = next((json.loads(l) for l in r.stdout.splitlines() if l.startswith('{"event":"done"')), {})
    if not VENV_PY.exists():
        print("no assistant venv for the analysis (numpy); the WAVs are in", out)
        return 1
    a = subprocess.run([str(VENV_PY), "-c", CHIRP, str(out / "raw.wav"), str(out / "processed.wav")], capture_output=True, text=True)
    if a.returncode:
        print(a.stderr[-500:])
    res = json.loads(a.stdout or "{}")
    st = done.get("stats", {})
    print(f"devices: {dev or 'the defaults'}")
    if "round_trip_ms" in res:
        print(f"echo: {res['echo_db']} dB of the played sweep comes back into the mic, {res['round_trip_ms']} ms later "
              f"(play to capture through the device chain)")
    else:
        print("no echo found (is the headset on, with Link connected?)")
    print(f"mic level raw {res.get('mic_rms_db')} dBFS; with echo cancelling, the last of four noise bursts leaves "
          f"{res.get('after_aec_rms_db')} dBFS")
    print(f"recordings: {out}")
    return 0


def devices():
    if build():
        return 1
    r = subprocess.run([str(LOOPBACK), "devices"], capture_output=True, text=True, encoding="utf-8")
    for line in r.stdout.splitlines():
        d = json.loads(line)
        for kind, items in d.items():
            print(f"{kind}:")
            for it in items:
                print(f"  {'*' if it['default'] else ' '} {it['name']}    {it['id']}")
    return r.returncode


def devices_change():
    """sv_loopback devices-change on a mic and a speaker that aren't the defaults (Steam's virtual pair if it's
    there), so the only real device opened is the default speaker, playing silence."""
    if build():
        return 1
    r = subprocess.run([str(LOOPBACK), "devices"], capture_output=True, text=True, encoding="utf-8")
    lists = {}
    for line in r.stdout.splitlines():
        lists.update(json.loads(line))

    def pick(kind, prefer):
        others = [d for d in lists.get(kind, []) if not d["default"]]
        hit = next((d for d in others if prefer in d["name"]), others[0] if others else None)
        return hit and hit["name"]

    cap, ren = pick("capture", "Steam Streaming Microphone"), pick("render", "Steam Streaming Speakers")
    if not cap or not ren:
        print("devices-change needs a mic and a speaker besides the defaults", file=sys.stderr)
        return 1
    r = subprocess.run([str(LOOPBACK), "devices-change", "name:" + cap, "name:" + ren], capture_output=True,
                       text=True, encoding="utf-8", creationflags=BELOW_NORMAL, timeout=90)
    events = [json.loads(line) for line in r.stdout.splitlines() if line.startswith("{")]
    steps, cur, prev_stats = {}, None, None
    for e in events:
        if e["event"] == "begin":
            cur = steps[e["step"]] = {"t": e["t"], "dev": []}
        elif e["event"] == "device" and cur is not None:
            cur["dev"].append(e)
        elif e["event"] == "end":
            st = e["stats"]
            cur.update(cpu=e["cpu_ms"], ms=e["t"] - cur["t"],
                       cap=st["capture_frames"] - (prev_stats or {}).get("capture_frames", 0),
                       ren=st["render_frames"] - (prev_stats or {}).get("render_frames", 0))
            prev_stats = st
    done = next((e for e in events if e["event"] == "done"), None)
    ok = True

    def check(name, cond, detail):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}")

    def seq(step, d):
        return [(e["what"], e["name"], e["t"] - steps[step]["t"]) for e in steps.get(step, {}).get("dev", []) if e["dir"] == d]

    def says(step, d):
        return ", ".join(f"{w} {n} at {t} ms" for w, n, t in seq(step, d)) or "nothing"

    def opened(step, d, part=None, other_than=None):
        return [x for x in seq(step, d) if x[0] == "open" and (part is None or part in x[1])
                and (other_than is None or other_than not in x[1])]

    def lost(step, d):
        return any(w == "lost" for w, _, _ in seq(step, d))

    print(f"mic asked for: {cap}; speakers asked for: {ren}")
    check("both open", opened("open", 0, cap) and opened("open", 1, ren), f"{says('open', 0)}; {says('open', 1)}")
    for step, d in (("render invalidated", 1), ("capture invalidated", 0)):
        back = opened(step, d, cap if d == 0 else ren)
        check(f"{step}: lost and reopened", lost(step, d) and back and back[0][2] < 1000, says(step, d))
    check("the speakers asked for gone: onto the default", lost("render device gone", 1) and opened("render device gone", 1, other_than=ren),
          says("render device gone", 1))
    check("and back to them when they return", opened("render device back", 1, ren), says("render device back", 1))
    check("nothing there: lost, quiet", lost("all gone", 0) and lost("all gone", 1) and not opened("all gone", 0)
          and not opened("all gone", 1) and not any(w == "failed" for w, _, _ in seq("all gone", 0) + seq("all gone", 1)),
          f"{says('all gone', 0)}; {says('all gone', 1)}")
    g = steps.get("all gone", {})
    want = g.get("ms", 0) / 10
    check("nothing there: the engine runs on (paced)", 0.85 * want <= g.get("ren", 0) <= 1.1 * want and 0.85 * want <= g.get("cap", 0) <= 1.1 * want,
          f"{g.get('ren')} render / {g.get('cap')} capture frames in {g.get('ms')} ms")
    check("nothing there: no spinning", g.get("cpu", 1e9) < 0.1 * g.get("ms", 0),
          f"{g.get('cpu')} ms of CPU in {g.get('ms')} ms (the whole process, the clock's 15.6 ms resolution)")
    check("devices back: reopened", opened("all back", 0, cap) and opened("all back", 1, ren), f"{says('all back', 0)}; {says('all back', 1)}")
    check("the default followed", opened("default", 1, other_than=ren) and opened("default moved", 1, ren)
          and opened("default back", 1, other_than=ren),
          f"{says('default', 1)}; moved: {says('default moved', 1)}; back: {says('default back', 1)}")
    stalls = [s for s, v in steps.items() if s not in ("all gone", "gone at the end") and v.get("ren", 0) < 0.8 * v.get("ms", 0) / 10]
    check("the playout never stalls", not stalls, f"short steps: {stalls}" if stalls else
          "about 100 render frames a second in every step")
    check("no waiting on the caller's thread", done and done["start_ms"] < 50 and done["restart_ms"] < 50 and done["stop_ms"] < 1000,
          done and f"sv_audio_start {done['start_ms']:.2f} ms, again with a new spec {done['restart_ms']:.2f} ms, "
                   f"sv_audio_stop with nothing there {done['stop_ms']:.1f} ms")
    if r.returncode or not done:
        print(r.stdout[-2000:], r.stderr[-2000:], file=sys.stderr)
        ok = False
    print("devices-change:", "passed" if ok else "FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    cmd = sys.argv[1] if len(sys.argv) > 1 else "loopback"
    if cmd == "fetch":
        sys.exit(fetch())
    if cmd == "build":
        sys.exit(build(force="--force" in sys.argv))
    if cmd == "devices":
        sys.exit(devices())
    if cmd == "chirp":
        sys.exit(chirp("--desktop" in sys.argv, "--synthetic" in sys.argv))
    if cmd == "loopback":
        sys.exit(loopback())
    if cmd == "devices-change":
        sys.exit(devices_change())
    if cmd == "roundtrip":
        sys.exit(roundtrip())
    if cmd == "scenarios":
        sys.exit(scenarios(sys.argv[2:]))
    sys.exit(__doc__)
