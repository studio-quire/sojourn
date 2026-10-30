"""The assistant's Python side: set it up, check it, and drive it in tests without a voice or a hand.

  python tools/assistant.py setup              the venv (local/assistant-venv): claude-agent-sdk, faster-whisper, soundcard,
                                               sherpa-onnx, websockets; Kokoro's voice and Parakeet's model into local/assistant
  python tools/assistant.py check              microphones, the Claude Code CLI, Whisper, Parakeet, whether a Soniox key is there,
                                               whether Claude Code is signed in and which provider keys are kept
  python tools/assistant.py key [FILE]         keep an Anthropic or OpenRouter key (its kind by its shape), encrypted for this
                                               Windows account, from FILE or typed unseen; `key forget KIND` deletes one.
                                               The menus do the same (docs/assistant.md > Switching it on)
  python tools/assistant.py ask "TEXT"         a typed request, as Enter in the window would send it
  python tools/assistant.py hear FILE.wav      a spoken request from a file (transcribed by the same Whisper)
  python tools/assistant.py talk FILE.wav      press talk, wait the file's length, release: the full push-to-talk path;
                                               the client must have been started with SOJOURN_MIC_FILE=FILE.wav
  python tools/assistant.py tts "TEXT" OUT.wav  a WAV of Windows' voice saying TEXT (test speech)
  python tools/assistant.py model [NAME]       show or set the model taking requests: quick, sonnet or opus (saved)
  python tools/assistant.py timings            latency of the last requests (local/assistant/timings.jsonl), from the
                                               end of speech: context line, the model's first token, first action, reply

ask/hear/talk send the pointer's current target along, as the client does.
"""
import json
import os
import subprocess
import sys
import time
import urllib.request
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VENV = ROOT / "local" / "assistant-venv"
PY = VENV / "Scripts" / "python.exe"
API = os.environ.get("SOJOURN_API", "http://127.0.0.1:9720")


def post(path, body):
    req = urllib.request.Request(API + path, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.loads(r.read() or b"null")


def target():
    try:
        return post("/cmd", {"pump": "SojournAssistant", "data": {"op": "pointer"}}).get("target")
    except OSError:
        return None


def event(data):
    return post("/event", {"kind": "sojourn", "data": data})


def setup():
    if not PY.exists():
        uv = subprocess.run(["uv", "venv", str(VENV), "--python", "3.12"]).returncode == 0
        if not uv:
            subprocess.check_call([sys.executable, "-m", "venv", str(VENV)])
    req = ROOT / "assistant" / "requirements.txt"
    if subprocess.run(["uv", "pip", "install", "--python", str(PY), "-r", str(req)]).returncode != 0:
        subprocess.check_call([str(PY), "-m", "pip", "install", "-r", str(req)])
    # Kokoro's voice model (Apache-2.0) for spoken replies: ~120 MB into local/assistant/kokoro
    kdir = ROOT / "local" / "assistant" / "kokoro"
    kdir.mkdir(parents=True, exist_ok=True)
    rel = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/"
    for name in ("kokoro-v1.0.int8.onnx", "voices-v1.0.bin"):
        if not (kdir / name).exists():
            print("downloading", name)
            urllib.request.urlretrieve(rel + name, kdir / (name + ".part"))
            (kdir / (name + ".part")).rename(kdir / name)
    # Parakeet TDT 0.6B v2 (NVIDIA, CC-BY-4.0), int8 for sherpa-onnx, for the `hearing: parakeet` choice: a ~480 MB
    # download, ~660 MB unpacked into local/assistant/parakeet
    pdir = ROOT / "local" / "assistant" / "parakeet"
    name = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8"
    if not (pdir / name / "encoder.int8.onnx").exists():
        import tarfile
        pdir.mkdir(parents=True, exist_ok=True)
        tar = pdir / (name + ".tar.bz2")
        print("downloading", name)
        urllib.request.urlretrieve(f"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{name}.tar.bz2", tar)
        with tarfile.open(tar, "r:bz2") as t:
            want = [m for m in t.getmembers() if m.isfile() and m.name.split("/")[-1] in
                    ("encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt")]
            for m in want:
                m.name = name + "/" + m.name.split("/")[-1]   # flat, nothing outside the folder
            t.extractall(pdir, members=want, filter="data")
        tar.unlink()


def check():
    code = ("import soundcard as sc\nprint('default mic:', sc.default_microphone().name)\n"
            "[print('  mic:', m.name) for m in sc.all_microphones()]\n"
            "import faster_whisper, claude_agent_sdk\nprint('faster-whisper', faster_whisper.__version__, '| agent sdk', claude_agent_sdk.__version__)\n"
            "import sys; sys.path.insert(0, 'assistant')\nimport ears, cloud, sherpa_onnx, websockets\n"
            "print('parakeet:', 'there' if (ears.PARAKEET / 'encoder.int8.onnx').exists() else 'missing (setup fetches it)', '| sherpa-onnx', sherpa_onnx.__version__)\n"
            "print('soniox key:', 'there' if cloud.available() else 'none (hearing: soniox needs local/secrets/soniox.key)')")
    subprocess.run([str(PY), "-c", code], cwd=ROOT)
    subprocess.run(["claude", "--version"])
    code = ("import sys, json; sys.path.insert(0, 'assistant'); import provider\n"
            "p = provider.Provider(log=lambda s: None); p.configure({'provider': 'claude'})\n"
            "print('claude account:', 'signed in' if p.ready else 'not signed in', '| claude code:', provider.cli())\n"
            "print('keys kept:', ', '.join(k for k in provider.KEY_KINDS if provider.has_key(k)) or 'none', 'in', provider.keys_dir())")
    subprocess.run([str(PY), "-c", code], cwd=ROOT)
    print("which provider is used is the client's setting (menu > assistant > provider); "
          "tools/provider_check.py tests them (real KIND with a kept key)")


def key(args):
    """Keep a key typed unseen or read from a file (never echoed, logged or put in the environment)."""
    sys.path.insert(0, str(ROOT / "assistant"))
    import provider
    if args[:1] == ["forget"] and len(args) == 2:
        provider.forget_key(args[1])
        print(f"{args[1]} key forgotten")
        return
    if args:
        text = Path(args[0]).read_text(encoding="utf-8")
    else:
        import getpass
        text = getpass.getpass("key (not shown): ")
    kind = provider.kind_of(text)
    if not kind:
        sys.exit("that isn't an Anthropic (sk-ant-...) or OpenRouter (sk-or-...) key; nothing kept")
    provider.save_key(kind, provider.shape(kind, text))
    print(f"{kind} key kept, encrypted, in {provider.keys_dir()}" + (f"; {args[0]} still holds it in the clear" if args else ""))
    print(f"to use it: menu > assistant > provider: {kind}")


def tts(text, out):
    ps = ("Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
          "$f = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, "
          "[System.Speech.AudioFormat.AudioChannel]::Mono); "
          f"$s.SetOutputToWaveFile('{Path(out).resolve()}', $f); $s.Speak('{text.replace(chr(39), chr(39) * 2)}'); $s.Dispose()")
    subprocess.check_call(["powershell", "-NoProfile", "-Command", ps])


def timings():
    f = ROOT / "local" / "assistant" / "timings.jsonl"
    for line in f.read_text(encoding="utf-8").splitlines()[-20:]:
        r = json.loads(line)
        print(f"{time.strftime('%H:%M:%S', time.localtime(r['t']))} {r['source']:<10} {r.get('mode', ''):<6} {r['model']:<18} "
              f"hear {r.get('transcribe_s')} {r.get('hearing') or ''}  context {r.get('context_s')}  first token {r.get('first_token_s')}  "
              f"first action {r.get('to_first_action_s')}  reply {r.get('to_reply_s')}  turns {r.get('turns')}  {r.get('tools')}")


if __name__ == "__main__":
    cmd, rest = (sys.argv[1] if len(sys.argv) > 1 else "check"), sys.argv[2:]
    if cmd == "setup":
        setup()
    elif cmd == "check":
        check()
    elif cmd == "key":
        key(rest)
    elif cmd == "ask":
        print(event({"what": "typed", "text": " ".join(rest), "target": target()}))
    elif cmd == "hear":
        print(event({"what": "audio", "path": str(Path(rest[0]).resolve()), "target": target()}))
    elif cmd == "talk":
        with wave.open(rest[0]) as w:
            secs = w.getnframes() / w.getframerate()
        event({"what": "talk_down", "target": target()})
        time.sleep(secs + 0.3)
        print(event({"what": "talk_up", "target": target()}))
    elif cmd == "tts":
        tts(rest[0], rest[1])
    elif cmd == "timings":
        timings()
    elif cmd == "model":
        print(post("/cmd", {"pump": "SojournAssistant", "data": {"op": "model", **({"model": rest[0]} if rest else {})}}))
    else:
        sys.exit(__doc__)
