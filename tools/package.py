"""Sojourn's package: the launcher (Sojourn.exe) and the VR client (SojournVR.exe) with the assistant, installed and
updated by Velopack (docs/launcher.md > The package). Sojourn Access has its own (tools/access.py package).

  python tools/package.py [--commit REV] [--tag NAME]
  python tools/package.py --dirty --tag NAME
        a release into local/dist/sojourn/releases: both apps published self-contained into one folder (stamped 0.1.0.N,
        N the commit count; Velopack's version 0.1.N), the natives, media's helpers (CEF, libVLC), the assistant
        with hearing and spoken replies (embedded Python, Parakeet, Kokoro; Whisper is fetched when chosen), the dev
        mode files and the licences; packed by Velopack (SojournViewer-win-Setup.exe, the full package, a delta from the
        last release when it's there) and its feed signed (releases.win.json.sig). Built from a commit, HEAD or
        --commit REV, exported by git archive into local/dist/sojourn/tree, however dirty the working tree is (other
        sessions' work stays out); what git doesn't hold (the natives, media, the venv, the models, the key) comes from
        this tree's local/ by path. The app and the release notes name the public commit tools/publish.py made from it
        (local/publish/map.txt); a release without one is refused (publish first), a try names the private. --tag NAME: a try in local/dist/sojourn/tries/NAME, apart from the release line;
        --dirty (with --tag): a try from the working tree itself, its commit marked -dirty when src/ or assistant/
        differ. Then the voice and hearing smoke test and the folder's size; the export and the build tree
        (local/dist/sojourn/build) are removed after. About 10 minutes.
  python tools/package.py check [--tag NAME] [--keep]
        the newest release installed and used as a person would, on a hidden desktop (no window reaches the screen, no
        focus taken), the data folder and everything else in a scratch folder: Setup, the Start menu shortcut and the
        Add/Remove Programs entry; the launcher (its version, a login on the local grid, the client in world on the
        desktop, its natives and the assistant's Python from the install); an update offered only from a signed feed
        and installed only at the press; uninstalling (the program, shortcut and entry gone, the caches and logs gone,
        the person's settings and accounts kept). The grid must be up (tools/grid.py start); the GPU is used for a
        minute. About 10 minutes.
  python tools/package.py key
        make the update-signing key, once: kept DPAPI-encrypted in local/secrets/studio/update-signing.key (back it up:
        without it no installed copy takes another update); prints the public half for src/Sojourn.Launcher/Updates.cs
  python tools/package.py backup
        the key encrypted under a new passphrase into Downloads/sojourn-key-backup (the .pem for the user's Google Drive,
        the passphrase in a file to keep elsewhere; also DPAPI'd in local/secrets/studio/update-signing-backup.passphrase)
  python tools/package.py restore FILE.pem PASSPHRASE_FILE [--force]
        a backup back into local/secrets/studio/update-signing.key

Publishing a release (the user's call, not done here): a GitHub release on studio-quire/sojourn tagged v0.1.0.N with
the files the build lists (Setup, the full and delta packages, releases.win.json and its .sig, assets.win.json).

Written by Claude Opus 5.5, 2026-11-01.
"""
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import access   # noqa: E402  (the assistant bundler, licences' helpers, QuireUia's helpers)
from buildenv import DOTNET   # noqa: E402

DIST = ROOT / "local" / "dist" / "sojourn"
RELEASES = DIST / "releases"
PUBLIC_REPO = ROOT.parent / "sojourn-public"   # publish.py's local snapshot repo
TREE = DIST / "tree"              # the built commit's files (git archive); never a junction to local/ in it
BUILD_ROOT = DIST / "build"       # SOJOURN_BUILD_ROOT: bin and obj, so the repo's own are untouched
VPK_VERSION = "1.2.161"           # the same as the launcher's Velopack package
VPK = ROOT / "local" / "dist" / "vpk" / "vpk.exe"
PACK_ID = "SojournViewer"         # Velopack's id: installs to %LOCALAPPDATA%\\SojournViewer (the data folder is ...\\Sojourn)
TITLE = "Sojourn"
AUTHORS = "Studio Quire"
ICON = ROOT / "art" / "icon" / "sojourn.ico"   # Setup's, and the uninstall entry's (tools/icon.py)
CHANNEL = "win"
RID = "win-x64"
LAUNCHER = ROOT / "src" / "Sojourn.Launcher" / "Sojourn.Launcher.csproj"
CLIENT = ROOT / "src" / "Sojourn.Client" / "Sojourn.Client.csproj"
VOICE_DLL = ROOT / "local" / "voice" / "build" / "Release" / "sojourn_voice.dll"
MEDIA = ROOT / "local" / "media"
SIGNING_KEY = ROOT / "local" / "secrets" / "studio" / "update-signing.key"
VENV_PY = access.VENV_PY
CREATE_NO_WINDOW, BELOW_NORMAL = 0x08000000, 0x00004000

# the assistant's Python side as Access ships it, plus hearing (sherpa-onnx for Parakeet, faster-whisper for Whisper),
# spoken replies (kokoro-onnx), the microphone outside voice chat (soundcard) and Soniox's stream (websockets)
DISTS = access.BUNDLED_DISTS + ["sherpa-onnx", "faster-whisper", "kokoro-onnx", "soundcard", "numpy", "websockets"]
IMPORTS = ["ears", "clientaudio", "cloud", "devrelay", "numpy", "sherpa_onnx", "faster_whisper", "kokoro_onnx", "soundcard"]

# the models in the package, from tools/assistant.py setup's downloads (and Whisper's vocabulary from its model, which
# setup's Whisper fetched), checked by hash so a package never ships a changed file unnoticed
PARAKEET = ROOT / "local" / "assistant" / "parakeet" / "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8"
KOKORO = ROOT / "local" / "assistant" / "kokoro"
WHISPER_SNAPSHOTS = Path.home() / ".cache" / "huggingface" / "hub" / "models--Systran--faster-whisper-small.en" / "snapshots"
MODELS = {
    "parakeet/encoder.int8.onnx": (PARAKEET / "encoder.int8.onnx", "a32b12d17bbbc309d0686fbbcc2987b5e9b8333a7da83fa6b089f0a2acd651ab"),   # gitleaks:allow
    "parakeet/decoder.int8.onnx": (PARAKEET / "decoder.int8.onnx", "b6bb64963457237b900e496ee9994b59294526439fbcc1fecf705b31a15c6b4e"),   # gitleaks:allow
    "parakeet/joiner.int8.onnx": (PARAKEET / "joiner.int8.onnx", "7946164367946e7f9f29a122407c3252b680dbae9a51343eb2488d057c3c43d2"),   # gitleaks:allow
    "parakeet/tokens.txt": (PARAKEET / "tokens.txt", "ec182b70dd42113aff6c5372c75cac58c952443eb22322f57bbd7f53977d497d"),   # gitleaks:allow
    "kokoro/kokoro-v1.0.int8.onnx": (KOKORO / "kokoro-v1.0.int8.onnx", "6e742170d309016e5891a994e1ce1559c702a2ccd0075e67ef7157974f6406cb"),   # gitleaks:allow
    "kokoro/voices-v1.0.bin": (KOKORO / "voices-v1.0.bin", "bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d"),   # gitleaks:allow
    "whisper-small.en/tokenizer.json": (None, "929c5252409436dce1b38a75d1abbcb5e132d170d8e324e4e04ed915fa2d22df"),   # gitleaks:allow
}

# what a package must hold (beside the runtimes)
EXPECTED = ["Sojourn.exe", "SojournVR.exe", "Sojourn.dll", "SojournVR.dll", "openxr_loader.dll", "sojourn_voice.dll", "openjp2.dll",
            "shaderc_shared.dll", "libSkiaSharp.dll", "glfw3.dll", "Shaders", "Character", "Textures", "Fonts", "linden",
            "media/web/sojourn_web.exe", "media/web/libcef.dll", "media/video/sojourn_video.exe", "media/video/vlc/libvlc.dll",
            "python/python.exe", "assistant/main.py", *(f"models/{m}" for m in MODELS), "Licences/README.txt"]


def git(*args):
    return git_in(ROOT, *args)


def git_in(repo, *args):
    return subprocess.run(["git", *args], cwd=repo, capture_output=True, text=True, creationflags=CREATE_NO_WINDOW).stdout.strip()


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def run(cmd, env=None, quiet_ok=True):
    """A build step below normal priority, its output shown only where it matters."""
    p = subprocess.run([str(c) for c in cmd], env=env, capture_output=True, text=True, encoding="utf-8", errors="replace",
                       creationflags=CREATE_NO_WINDOW | BELOW_NORMAL)
    out = p.stdout + p.stderr
    for line in out.splitlines():
        if re.search(r"error|-> |FAILED|FTL|ERR\]", line) or not quiet_ok:
            print("  " + line.strip())
    return p.returncode, out


# ---- the signing key -------------------------------------------------------------------------------------------------

KEYGEN = r"""
import base64, sys
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec
k = ec.generate_private_key(ec.SECP256R1())
pem = k.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode()
pub = base64.b64encode(k.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)).decode()
sys.stdout.write(pub + "\n" + pem)
"""

SIGN = r"""
import base64, sys
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature
key = serialization.load_pem_private_key(sys.stdin.buffer.read(), None)
data = open(sys.argv[1], "rb").read()
r, s = decode_dss_signature(key.sign(data, ec.ECDSA(hashes.SHA256())))
sys.stdout.write(base64.b64encode(r.to_bytes(32, "big") + s.to_bytes(32, "big")).decode())
"""


def make_key():
    import relay
    if SIGNING_KEY.exists():
        print(f"{SIGNING_KEY.relative_to(ROOT)} exists already; a new key would leave every installed copy unable to take "
              "updates. Delete it by hand first if that's really meant.")
        return 1
    r = subprocess.run([str(VENV_PY), "-c", KEYGEN], capture_output=True, text=True, creationflags=CREATE_NO_WINDOW)
    if r.returncode:
        print(r.stderr[-800:])
        return 1
    pub, pem = r.stdout.split("\n", 1)
    relay.write_secret(SIGNING_KEY, pem)
    print(f"kept in {SIGNING_KEY.relative_to(ROOT)} (DPAPI, this Windows account only: back it up somewhere safe)")
    print(f"public half, for Updates.cs PublicKey:\n{pub}")
    return 0


def sign(feed):
    """feed's .sig beside it: base64 of ECDSA P-256 over its bytes (Updates.cs checks it)."""
    import relay
    pem = relay.read_secret(SIGNING_KEY)
    r = subprocess.run([str(VENV_PY), "-c", SIGN, str(feed)], input=pem.encode(), capture_output=True, creationflags=CREATE_NO_WINDOW)
    if r.returncode:
        raise RuntimeError(r.stderr.decode(errors="replace")[-800:])
    Path(str(feed) + ".sig").write_text(r.stdout.decode(), encoding="ascii")


# ---- building ---------------------------------------------------------------------------------------------------------

def vpk():
    """Velopack's packer, into local/dist/vpk once (the .NET SDK's tool install), run on the SDK's runtime."""
    if not VPK.exists():
        rc, _ = run([DOTNET, "tool", "install", "vpk", "--version", VPK_VERSION, "--tool-path", VPK.parent])
        if rc or not VPK.exists():
            raise RuntimeError("couldn't install vpk")
    env = dict(os.environ, DOTNET_ROOT=str(Path(DOTNET).parent), DOTNET_ROLL_FORWARD="Major", VPK_SKIP_UPDATES="1")
    return VPK, env


def publish(project, stage, version, commit, build_root):
    env = dict(os.environ, SOJOURN_BUILD_ROOT=str(build_root))
    rc, _ = run([DOTNET, "publish", project, "-c", "Release", "-r", RID, "--self-contained", "true", f"-p:Version={version}",
                 f"-p:SourceRevisionId={commit}", "-p:DebugType=embedded", "-p:SatelliteResourceLanguages=en", "-o", stage,
                 "-m:2", "-nr:false", "-p:UseSharedCompilation=false", "-v:m", "-clp:NoSummary"], env=env)
    return rc


def whisper_tokenizer():
    return next((p for p in sorted(WHISPER_SNAPSHOTS.glob("*/tokenizer.json")) if sha256(p) == MODELS["whisper-small.en/tokenizer.json"][1]), None)


def option(args, name):
    return args[args.index(name) + 1] if name in args[:-1] else None


def build():
    args = sys.argv[1:]
    dirty = "--dirty" in args
    tag, rev = option(args, "--tag"), option(args, "--commit")
    if tag and not re.fullmatch(r"[a-z0-9][a-z0-9\-]{0,30}", tag):
        print("--tag: lowercase letters, digits and dashes")
        return 1
    if dirty and (rev or not tag):
        print("--dirty builds a try from the working tree: give it --tag NAME, and no --commit (the release line holds "
              "only commits)")
        return 1
    full = git("rev-parse", "--verify", "--quiet", f"{rev or 'HEAD'}^{{commit}}")
    if not full:
        print(f"no such commit: {rev}")
        return 1
    missing = [str(p.relative_to(ROOT)) for p in (VOICE_DLL, MEDIA / "runtime" / "web" / "sojourn_web.exe",
                                                    MEDIA / "runtime" / "video" / "sojourn_video.exe", VENV_PY, access.WEBRTC_LICENCES)
               if not p.exists()]
    missing += [m for m, (p, _) in MODELS.items() if p is not None and not p.exists()]
    if whisper_tokenizer() is None:
        missing.append("Whisper small.en's tokenizer.json (its model, fetched by the assistant's first Whisper run)")
    if not SIGNING_KEY.exists():
        missing.append(f"{SIGNING_KEY.relative_to(ROOT)} (python tools/package.py key)")
    if missing:
        print("missing: " + "; ".join(missing) + " (python tools/voice.py build; python tools/media.py fetch, build; "
              "python tools/assistant.py setup)")
        return 1
    n = int(git("rev-list", "--count", full))
    commit = git("rev-parse", "--short=7", full)
    # what the app and the release notes name: the studio-quire/sojourn commit publish.py made from this one, since the
    # private repo's hashes mean nothing outside the studio (docs/publishing.md); a try without one names the private
    public = None if dirty else access.public_commit(full)
    if dirty:
        src = ROOT
        commit += "-dirty" if git("status", "--porcelain", "--", "src", "assistant") else ""
    else:
        if not public and not tag:
            print(f"no public snapshot of {commit} yet (local/publish/map.txt): a release names the studio-quire/sojourn "
                  "commit its source is in. Make one with python tools/publish.py (it commits ../sojourn-public locally "
                  "and pushes nothing; pushing is by hand, docs/publishing.md), then build again; or --tag NAME for a try")
            return 1
        if public and not git_in(PUBLIC_REPO, "branch", "-r", "--contains", public):
            print(f"(note: public commit {public[:7]} isn't on ../sojourn-public's remote yet, as far as it knows: push it "
                  "before this release goes out)")
        src = TREE
        tooling = git("diff", "--name-only", full, "--", "tools/package.py", "tools/access.py", "tools/buildenv.py")
        if tooling:
            print("(note: the packaging code runs from the working tree, which differs from the commit here: "
                  + ", ".join(tooling.split()) + ")")
    out = DIST / "tries" / tag if tag else RELEASES
    stage = DIST / "stage"
    t0 = time.time()
    try:
        # a fresh build tree each time: the export's files carry the commit's time, which an older build's outputs
        # could look newer than
        for d in (stage, BUILD_ROOT):
            shutil.rmtree(d, ignore_errors=True)
        stage.mkdir(parents=True)
        if not dirty:
            print(f"exporting {commit} into {TREE.relative_to(ROOT)}")
            access.export(full, TREE)
        stamp, source = ((public[:7], f"commit {public[:7]} of github.com/studio-quire/sojourn") if public else
                         (commit, f"the studio's private commit {commit} (no public snapshot of it)"))
        print(f"Sojourn 0.1.0.{n}: private commit {commit}" + (f", public {public[:7]}" if public else ", no public snapshot"))
        return make(src, stage, out, n, stamp, source, t0)
    finally:
        for d in (stage, BUILD_ROOT, TREE):
            shutil.rmtree(d, ignore_errors=True)


def make(src, stage, out, n, commit, source, t0):
    """The package from src (the commit's export, or the working tree for a --dirty try) into out; commit: the one the
    app shows (its informational version), source: the release notes' words for where its source is."""
    version, pack_version = f"0.1.0.{n}", f"0.1.{n}"
    print(f"publishing Sojourn {version} ({commit}): the launcher and the VR client, self-contained {RID}, into one folder")
    for project in (LAUNCHER, CLIENT):
        if publish(src / project.relative_to(ROOT), stage, version, commit, BUILD_ROOT):
            return 1
    for f in [*stage.rglob("*.pdb"), stage / "aspnetcorev2_inprocess.dll", stage / "createdump.exe"]:
        f.unlink(missing_ok=True)
    if not (stage / "sojourn_voice.dll").exists():
        shutil.copy2(VOICE_DLL, stage)
    for part in ("web", "video"):   # media's helpers and their runtimes (MediaWiring: media/ beside the program)
        shutil.copytree(MEDIA / "runtime" / part, stage / "media" / part)
    dev = src / "tools" / "dev.py"   # dev mode from an installed copy (docs/dev-mode.md): its tool and the guide
    guide = src / "tools" / "publish" / "sojourn" / "DEV-MODE.md"
    for f in (dev, guide):
        if f.exists():
            shutil.copy2(f, stage / f.name)

    print("the assistant: embedded Python, the sidecar, hearing and spoken replies")
    licences(stage / "Licences", BUILD_ROOT, src)
    if access.bundle_assistant(stage, dists=DISTS, imports=IMPORTS, source=src) != 0:
        return 1
    print("the models: Parakeet, Kokoro, Whisper's vocabulary")
    for name, (model, digest) in MODELS.items():
        model = model or whisper_tokenizer()
        if sha256(model) != digest:
            print(f"{model} isn't the file this package expects (its SHA-256 differs)")
            return 1
        (stage / "models" / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(model, stage / "models" / name)
    absent = [e for e in EXPECTED if not (stage / e).exists()]
    if absent:
        print("not in the package: " + ", ".join(absent))
        return 1
    if smoke(stage) != 0:
        return 1
    size = sum(f.stat().st_size for f in stage.rglob("*") if f.is_file())
    print(f"the folder: {size / 1e6:.0f} MB, {sum(1 for f in stage.rglob('*') if f.is_file())} files")

    print(f"packing with Velopack {VPK_VERSION}: {PACK_ID} {pack_version}")
    out.mkdir(parents=True, exist_ok=True)
    exe, env = vpk()
    notes = DIST / "notes.md"
    notes.write_text(f"Sojourn {version}, from {source}.\n", encoding="utf-8")
    rc, log = run([exe, "pack", "--packId", PACK_ID, "--packVersion", pack_version, "--packDir", stage, "--mainExe", "Sojourn.exe",
                   "--packTitle", TITLE, "--packAuthors", AUTHORS, "--icon", src / ICON.relative_to(ROOT), "--channel", CHANNEL,
                   "--shortcuts", "StartMenuRoot", "--noPortable", "--releaseNotes", notes, "--outputDir", out, "--skip-updates"], env=env)
    if rc:
        print(log[-3000:])
        return 1
    # only the newest full package stays here (the next build's delta is made from it); older ones live in their releases
    fulls = sorted(out.glob(f"{PACK_ID}-*-full.nupkg"), key=lambda p: p.stat().st_mtime)
    for old in fulls[:-1]:
        old.unlink()
    sign(out / f"releases.{CHANNEL}.json")
    shutil.rmtree(stage, ignore_errors=True)
    files = [f"{PACK_ID}-win-Setup.exe", f"{PACK_ID}-{pack_version}-full.nupkg", f"{PACK_ID}-{pack_version}-delta.nupkg",
             f"releases.{CHANNEL}.json", f"releases.{CHANNEL}.json.sig", f"assets.{CHANNEL}.json"]
    print(f"\n{out.relative_to(ROOT)} ({(time.time() - t0) / 60:.0f} min); a release's files:")
    for f in files:
        if (out / f).exists():
            print(f"  {f}  {(out / f).stat().st_size / 1e6:.1f} MB")
    return 0


def smoke(stage):
    """Spoken replies and hearing in the package itself: its Python says a sentence with Kokoro (espeak-ng's phonemes
    and all) and Parakeet hears it back, from the package's models, nothing from the PC's Python."""
    script = r"""
import sys, numpy as np
sys.path.insert(0, sys.argv[1] + "/assistant")
import paths, ears, voice
from kokoro_onnx import Kokoro
import sherpa_onnx
k = Kokoro(str(voice.MODEL), str(voice.VOICES))
said, rate = k.create("Take me to the harbour, please.", voice="af_heart", speed=1.0, lang="en-us")
x = np.interp(np.arange(0, len(said) * 16000 / rate) * rate / 16000, np.arange(len(said)), said).astype(np.float32)
d = ears.PARAKEET
r = sherpa_onnx.OfflineRecognizer.from_transducer(str(d / "encoder.int8.onnx"), str(d / "decoder.int8.onnx"),
    str(d / "joiner.int8.onnx"), str(d / "tokens.txt"), num_threads=2, model_type="nemo_transducer", provider="cpu")
s = r.create_stream(); s.accept_waveform(16000, x); r.decode_stream(s)
print(voice.MODEL.parent); print(d); print(s.result.text.strip())
"""
    with tempfile.TemporaryDirectory(prefix="sojourn-smoke-") as data:
        env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("PYTHON", "SOJOURN_", "VIRTUAL_ENV", "HF_"))}
        env["SOJOURN_LOCAL"] = data
        r = subprocess.run([str(stage / "python" / "python.exe"), "-B", "-E", "-s", "-c", script, str(stage)], capture_output=True,
                           text=True, env=env, cwd=data, creationflags=CREATE_NO_WINDOW | BELOW_NORMAL)
    lines = r.stdout.strip().splitlines()
    ok = (r.returncode == 0 and len(lines) == 3 and Path(lines[0]) == stage / "models" / "kokoro"
          and Path(lines[1]) == stage / "models" / "parakeet" and "harbour" in lines[2].lower().replace("harbor", "harbour"))
    print(f"spoken and heard back from the package's own models: {lines[2] if len(lines) == 3 else '?'}" if ok else
          "the voice and hearing smoke test failed:\n" + (r.stdout + r.stderr)[-2000:])
    return 0 if ok else 1


def licences(dest, build_root, src=ROOT):
    """The licences of what ships (Access's list for the parts both have, then the VR client's own); src: the tree
    built (its own texts), build_root: its restore's package lists."""
    import html
    import xml.etree.ElementTree as ET
    w = access.write_text
    package_files = src / access.PACKAGE_FILES.relative_to(ROOT)
    lgpl = (access.NVDA_DIR / "license.txt").read_text(encoding="utf-8", errors="replace") if (access.NVDA_DIR / "license.txt").exists() \
        else (src / "tools" / "publish" / "sojourn" / "LICENSE").read_text(encoding="utf-8")
    w(dest / "Sojourn (LGPL-2.1).txt", "Sojourn is free software under the GNU Lesser General Public License, version 2.1, "
      "below. Its source: https://github.com/studio-quire/sojourn (each release's notes name the commit).\n\n" + lgpl)
    w(dest / "Second Life viewer parts (LGPL-2.1).txt",
      "Parts of Sojourn's renderer are ported from the Second Life viewer's source code, and the Character and Textures "
      "folders hold files from the viewer (the system avatar's meshes, skeleton and parameters; the default particle "
      "image). Copyright Linden Research, Inc. They are licensed under the GNU Lesser General Public License, version "
      "2.1, in 'Sojourn (LGPL-2.1).txt' here.\n")
    w(dest / "libwebrtc, in sojourn_voice.dll.txt",
      "sojourn_voice.dll (voice chat and world audio) is built over Linden Lab's build of Google's libwebrtc "
      "(github.com/secondlife/3p-webrtc-build). Its licence and those of the libraries in it follow.\n\n"
      + html.unescape(access.WEBRTC_LICENCES.read_text(encoding="utf-8", errors="replace")))
    w(dest / "OpenJPEG, openjp2.dll.txt", "openjp2.dll is OpenJPEG 2.5.3, built from its source.\n\n"
      + (package_files / "licences" / "OpenJPEG.txt").read_text(encoding="utf-8"))
    w(dest / "Archivo font (OFL-1.1).txt", (src / "src" / "Sojourn.Session" / "Assets" / "Fonts" / "Archivo-OFL.txt").read_text(encoding="utf-8"))
    shutil.copy2(package_files / "licences" / "Apache-2.0.txt", dest / "Apache-2.0.txt")
    w(dest / "MIT.txt", access.MIT)
    w(dest / "OpenXR loader (Apache-2.0).txt", "openxr_loader.dll is the Khronos Group's OpenXR loader 1.1.65 "
      "(github.com/KhronosGroup/OpenXR-SDK-Source), unmodified, under the Apache License 2.0 (Apache-2.0.txt here).\n")
    cef = MEDIA / "cef"
    w(dest / "CEF and Chromium (media, web).txt", "media\\web holds the Chromium Embedded Framework (CEF, "
      "bitbucket.org/chromiumembedded/cef), unmodified, for web pages on faces and parcel media. CEF's licence follows; "
      "Chromium's components are under their own licences, listed in 'CEF credits.html' here.\n\n"
      + (cef / "LICENSE.txt").read_text(encoding="utf-8", errors="replace"))
    shutil.copy2(cef / "CREDITS.html", dest / "CEF credits.html")
    vlc = MEDIA / "runtime" / "video" / "vlc"
    w(dest / "libVLC (media, video; LGPL-2.1).txt", "media\\video\\vlc holds VideoLAN's libVLC and its plugins, "
      "unmodified, for video on faces and parcel media (www.videolan.org; the source of each release there). Its "
      "licence follows.\n\n" + (vlc / "COPYING.txt").read_text(encoding="utf-8", errors="replace"))
    w(dest / "Models.txt", "The models in the models folder, used on this PC only:\n\n"
      "Parakeet TDT 0.6B v2 (models\\parakeet): NVIDIA, under CC BY 4.0 (creativecommons.org/licenses/by/4.0); the int8 "
      "ONNX export by the sherpa-onnx project (github.com/k2-fsa/sherpa-onnx). Hearing.\n"
      "Kokoro 82M v1.0 (models\\kokoro): hexgrad, under the Apache License 2.0 (Apache-2.0.txt); the ONNX export by "
      "kokoro-onnx (github.com/thewh1teagle/kokoro-onnx). Spoken replies.\n"
      "Whisper small.en's vocabulary (models\\whisper-small.en\\tokenizer.json): OpenAI, under the MIT licence (MIT.txt), "
      "as converted by Systran (huggingface.co/Systran/faster-whisper-small.en). Whisper itself, when chosen, is fetched "
      "from there.\n")
    w(dest / "espeak-ng and phonemizer (GPL-3.0).txt", "Spoken replies turn words into sounds with espeak-ng (in "
      "python\\Lib\\site-packages\\espeakng_loader, github.com/espeak-ng/espeak-ng, its source at the version named in "
      "that package's metadata) through phonemizer (github.com/bootphon/phonemizer, its source beside it as Python "
      "files). Both are under the GNU General Public License, version 3: https://www.gnu.org/licenses/gpl-3.0.txt\n")
    lines = ["The libraries in Sojourn (the launcher and the VR client), each with its licence (the texts that came with "
             "them follow in this folder where they had one).", ""]
    seen = set()
    for proj in ("Sojourn.Launcher", "Sojourn.Client"):
        assets = json.loads((build_root / "obj" / proj / "project.assets.json").read_text(encoding="utf-8"))
        target = next(t for k, t in assets["targets"].items() if k.endswith("/" + RID))
        for key, lib in sorted(target.items(), key=lambda kv: kv[0].lower()):
            if key in seen or lib.get("type") != "package" or not (lib.get("runtime") or lib.get("native") or lib.get("runtimeTargets")):
                continue
            if all(p.endswith("_._") for p in list(lib.get("runtime", {})) + list(lib.get("native", {}))) and not lib.get("runtimeTargets"):
                continue
            seen.add(key)
            pkg_id, ver = key.split("/")
            folder = access.NUGET / assets["libraries"][key]["path"]
            licence, project = access.nuspec_licence(folder, ET)
            lines.append(f"{pkg_id} {ver}: {licence}" + (f" ({project})" if project else ""))
            for f in sorted(folder.glob("*")):
                if f.is_file() and re.match(r"(?i)(license|licence|third-party-notices|notice)", f.name):
                    shutil.copy2(f, dest / f"{pkg_id} - {f.name}")
    for pack in ("microsoft.netcore.app.runtime", "microsoft.windowsdesktop.app.runtime", "microsoft.aspnetcore.app.runtime"):
        found = sorted(access.NUGET.glob(f"{pack}.{RID}/*"))
        if found:
            lines.append(f"{pack}.{RID} {found[-1].name} (.NET): MIT")
            for f in sorted(found[-1].glob("*")):
                if f.is_file() and re.match(r"(?i)(license|third-party-notices)", f.name):
                    shutil.copy2(f, dest / f"{pack}.{RID} - {f.name}")
    lines += ["", "Velopack (installing and updates; Update.exe beside the program): MIT, github.com/velopack/velopack",
              "OpenXR loader 1.1.65 (openxr_loader.dll): Apache-2.0", "OpenJPEG 2.5.3 (openjp2.dll): BSD-2-Clause",
              "libwebrtc m144 (in sojourn_voice.dll): BSD-3-Clause and others",
              "CEF (media\\web): BSD-3-Clause, Chromium's components under their own", "libVLC 3.0.21 (media\\video\\vlc): LGPL-2.1",
              "Second Life viewer parts (renderer code, Character, Textures): LGPL-2.1, Linden Research, Inc.",
              "The linden folder (LibreMetaverse's copy of the viewer's data files): see cc-by-sa-3.0.txt in it",
              "Archivo font (Fonts): SIL Open Font License 1.1", "The models: Models.txt",
              "The assistant's Python and packages: 'The assistant's parts.txt'", "",
              "Where a library's licence is MIT or Apache-2.0 and its package carried no text of it, the text is in "
              "MIT.txt or Apache-2.0.txt here; the copyright is its authors', as named at its project's address."]
    w(dest / "README.txt", "\n".join(lines) + "\n")


# ---- the check --------------------------------------------------------------------------------------------------------

DESKTOP = "sojourn-package-vr"
Q_PORT = 9908
ACCOUNT = ("Package", "Viewer")     # its own on the local grid
PASSWORD = "sojourn-local"
UNINSTALL_KEY = rf"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{PACK_ID}"
START_MENU = Path(os.environ["APPDATA"]) / "Microsoft" / "Windows" / "Start Menu" / "Programs"
fails = 0


def check(ok, what, detail=""):
    global fails
    fails += not ok
    print(f"{'ok  ' if ok else 'FAIL'} {what}{f' ({detail})' if detail else ''}", flush=True)
    return ok


def u(line, timeout=30):
    access.Q_PORT = Q_PORT
    return access.u(line, timeout)


def wait(f, secs, step=0.5):
    return access.wait(f, secs, step)


def hidden(args, env=None, cwd=None):
    return access.launch_hidden_on(DESKTOP, args, env=env, cwd=cwd)


def reg(key):
    r = subprocess.run(["reg", "query", key], capture_output=True, text=True, creationflags=CREATE_NO_WINDOW)
    if r.returncode:
        return None
    return {m[1]: m[2].strip() for m in re.finditer(r"^\s+(\S+)\s+REG_\w+\s*(.*)$", r.stdout, re.M)}


def state(data):
    try:
        return json.loads((data / "launcher-state.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def text_of(name_part):
    """A text on the launcher's window whose name has name_part, or None."""
    try:
        got = u(f"find type=Text;name~={name_part}")
    except Exception:
        return None
    m = re.search(r'"([^"]*' + re.escape(name_part) + r'[^"]*)"', got)
    return m[1] if m else None


def clients_running():
    """A VR client running (the user may be in the headset; builds and other tools aside): its command lines."""
    ps = ("Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'SojournVR.exe' -or ($_.Name -eq 'dotnet.exe' -and "
          "$_.CommandLine -match 'SojournVR[.]dll' -and $_.CommandLine -notmatch ' build ') } | "
          "ForEach-Object { \"$($_.ProcessId) $($_.CommandLine)\" }")
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, creationflags=CREATE_NO_WINDOW)
    return [l for l in r.stdout.splitlines() if l.strip()]


def launcher_in(inst, not_pid=None, secs=40):
    """The installed launcher's own process (the stub starts current/Sojourn.exe and leaves), watched by QuireUia."""
    import psutil
    exe = (inst / "current" / "Sojourn.exe").resolve()

    def find():
        # not a Velopack hook's run of it (--veloapp-updated and the like: they exit at once), and still there a moment on
        for p in psutil.process_iter(["exe", "pid", "cmdline"]):
            try:
                if (p.info["exe"] and Path(p.info["exe"]).resolve() == exe and p.info["pid"] != not_pid
                        and not any(a.startswith("--veloapp") for a in p.info["cmdline"] or [])):
                    time.sleep(2)
                    if p.is_running():
                        return p.info["pid"]
            except (OSError, psutil.Error):
                pass
    pid = wait(find, secs)
    if pid:
        u(f"watch {pid}")
    return pid


def start_launcher(inst, args, env):
    hidden([inst / "Sojourn.exe", *args], env=env)   # the stub Velopack puts above current\, named as the main exe
    return launcher_in(inst)


def close_launcher(inst, pid):
    if pid and access.alive(pid):
        try:
            u(f"close {pid}")
        except Exception:
            pass
        wait(lambda: not access.alive(pid), 15)
    kill_under(inst)


def processes_under(folder):
    ps = (f"Get-CimInstance Win32_Process | Where-Object {{ $_.ExecutablePath -like '{folder}\\*' }} | "
          "ForEach-Object { \"$($_.ProcessId) $($_.Name)\" }")
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, creationflags=CREATE_NO_WINDOW)
    return [l for l in r.stdout.splitlines() if l.strip()]


def kill_under(folder):
    ps = (f"Get-CimInstance Win32_Process | Where-Object {{ $_.ExecutablePath -like '{folder}\\*' }} | "
          "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }")
    subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, creationflags=CREATE_NO_WINDOW)


def next_release(release_dir, work, version_n):
    """The update the check offers: this release's own files with the launcher published again as 0.1.0.(N+1), packed
    by Velopack as 0.1.(N+1) beside a copy of this release's full package (so a delta is made too) and signed."""
    feed = work / "feed"
    feed.mkdir()
    full = next(release_dir.glob(f"{PACK_ID}-0.1.{version_n}-full.nupkg"))
    shutil.copy2(full, feed / full.name)
    shutil.copy2(release_dir / f"releases.{CHANNEL}.json", feed / f"releases.{CHANNEL}.json")
    app = work / "next"
    with zipfile.ZipFile(full) as z:
        for m in z.namelist():
            if m.startswith("lib/app/") and not m.endswith("/"):
                z.extract(m, work / "next-raw")
    shutil.move(str(work / "next-raw" / "lib" / "app"), app)
    shutil.rmtree(work / "next-raw", ignore_errors=True)
    nv = version_n + 1
    # the launcher from the release's own commit, as the release was built: its notes name the public commit (found in
    # map.txt) or, for a try without one, the private; a -dirty try's from the working tree
    with zipfile.ZipFile(full) as z:
        nuspec = next((z.read(m).decode("utf-8", "replace") for m in z.namelist() if m.endswith(".nuspec")), "")
    m = re.search(r"commit ([0-9a-f]{7,})(?![-\w])", nuspec)
    private = m and (access.private_commit(m[1]) or git("rev-parse", "--verify", "--quiet", f"{m[1]}^{{commit}}"))
    src = ROOT
    if private:
        access.export(private, DIST / "tree-check")
        src = DIST / "tree-check"
    print(f"     (the update's launcher from {f'commit {private[:7]}' if private else 'the working tree'})")
    shutil.rmtree(DIST / "build-check", ignore_errors=True)
    if publish(src / LAUNCHER.relative_to(ROOT), app, f"0.1.0.{nv}", "check", DIST / "build-check"):
        raise RuntimeError("publishing the launcher for the update failed")
    for f in app.rglob("*.pdb"):
        f.unlink()
    (app / "createdump.exe").unlink(missing_ok=True)
    notes = work / "notes.md"
    notes.write_text("the package check's update\n", encoding="utf-8")
    exe, env = vpk()
    rc, log = run([exe, "pack", "--packId", PACK_ID, "--packVersion", f"0.1.{nv}", "--packDir", app, "--mainExe", "Sojourn.exe",
                   "--packTitle", TITLE, "--packAuthors", AUTHORS, "--icon", ICON, "--channel", CHANNEL, "--shortcuts", "StartMenuRoot",
                   "--noPortable", "--releaseNotes", notes, "--outputDir", feed, "--skip-updates"], env=env)
    if rc:
        raise RuntimeError("vpk pack for the update failed:\n" + log[-2000:])
    shutil.rmtree(app, ignore_errors=True)
    return feed, f"0.1.0.{nv}"


def check_package():
    import grid
    global fails
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = sys.argv[2:]
    keep = "--keep" in args
    tag = args[args.index("--tag") + 1] if "--tag" in args[:-1] else None
    release_dir = DIST / "tries" / tag if tag else RELEASES
    setup = release_dir / f"{PACK_ID}-win-Setup.exe"
    fulls = sorted(release_dir.glob(f"{PACK_ID}-*-full.nupkg"), key=lambda p: p.stat().st_mtime)
    if not setup.exists() or not fulls:
        print(f"no release in {release_dir.relative_to(ROOT)} (python tools/package.py)")
        return 1
    n = int(re.search(rf"{PACK_ID}-0\.1\.(\d+)-full", fulls[-1].name)[1])
    version = f"0.1.0.{n}"
    print(f"checking Sojourn {version} ({setup.name}, {setup.stat().st_size / 1e6:.0f} MB)")
    if reg(UNINSTALL_KEY) is not None:
        print(f"Sojourn is installed on this PC ({UNINSTALL_KEY}): the check would replace its entry; not starting")
        return 1
    others = clients_running()
    if others:
        print("the VR client or dev mode is running (the user may be in the headset); not starting a client beside it:\n  " + "\n  ".join(others))
        return 1
    if not access.QUIREUIA.exists():
        print("missing local/bin/QuireUia (python tools/access.py tools)")
        return 1
    if not grid.ready():
        print("the local grid is down (python tools/grid.py start)")
        return 1
    grid.account(*ACCOUNT, PASSWORD)
    real_data = Path(os.environ["LOCALAPPDATA"]) / "Sojourn"
    real_before = access.files_under(real_data)

    work = Path(tempfile.mkdtemp(prefix="sojourn-package-check-"))
    inst, data = work / "install", work / "data"
    env_dirs = {"TEMP": work / "temp", "LOCALAPPDATA": work / "LocalAppData", "APPDATA": work / "AppData"}
    for d in env_dirs.values():
        d.mkdir(parents=True)
    path = access.without_claude(os.pathsep.join(p for p in os.environ.get("PATH", "").split(os.pathsep) if "dotnet" not in p.lower()))
    base_env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("DOTNET_", "SOJOURN_", "ACCESS_", "PYTHON", "HF_", "CLAUDE"))}
    base_env.update(PATH=path, TEMP=str(env_dirs["TEMP"]), TMP=str(env_dirs["TEMP"]), LOCALAPPDATA=str(env_dirs["LOCALAPPDATA"]),
                    APPDATA=str(env_dirs["APPDATA"]), SOJOURN_DATA=str(data), SOJOURN_BACKGROUND="1")
    server = None
    try:
        server = hidden([access.QUIREUIA, "serve", "--port", Q_PORT, "--log", work / "events.jsonl"])
        if not check(bool(wait(lambda: u("help", timeout=5), 20)), "QuireUia's server answers on the hidden desktop"):
            raise access.Stop

        # ---- installing
        t0 = time.time()
        pid = hidden([setup, "--silent", "--installto", inst, "--log", work / "setup.log"], env=base_env)
        done = wait(lambda: not access.alive(pid), 300, 1)
        log = (work / "setup.log").read_text(encoding="utf-8", errors="replace") if (work / "setup.log").exists() else ""
        check(done and "Installation completed successfully" in log, "Setup installs it", f"{time.time() - t0:.0f} s" if done else log[-300:])
        exe = inst / "current" / "Sojourn.exe"
        check(exe.exists() and (inst / "current" / "SojournVR.exe").exists() and (inst / "Update.exe").exists(),
              "the launcher, the client and Velopack's Update.exe in the install folder")
        entry = reg(UNINSTALL_KEY) or {}
        check(entry.get("DisplayName") == TITLE and entry.get("DisplayVersion") == f"0.1.{n}" and entry.get("Publisher") == AUTHORS,
              "Add/Remove Programs lists it", ", ".join(f"{k}={entry.get(k)}" for k in ("DisplayName", "DisplayVersion", "Publisher")))
        shortcut = START_MENU / f"{TITLE}.lnk"
        check(shortcut.exists(), "a Start menu shortcut", str(shortcut))
        check(not (Path.home() / "Desktop" / f"{TITLE}.lnk").exists(), "no desktop shortcut")
        check(not data.exists() or not any(data.iterdir()), "installing wrote nothing into the data folder")
        # dev mode from an installed copy (DEV-MODE.md): dev.py beside Sojourn.exe, run by the Python Sojourn ships
        r = subprocess.run([str(inst / "current" / "python" / "python.exe"), "-B", "-E", "-s", "dev.py", "list"], cwd=inst / "current",
                           env=base_env, capture_output=True, text=True, timeout=60, creationflags=CREATE_NO_WINDOW)
        check(r.returncode == 0 and (inst / "current" / "DEV-MODE.md").exists(), "dev.py runs on the install's Python, DEV-MODE.md beside it",
              (r.stdout + r.stderr).strip().splitlines()[-1][:160] if (r.stdout + r.stderr).strip() else f"exit {r.returncode}")

        # ---- the launcher as a person starts it (the shortcut's target, the stub before current\), a login, the client
        env = dict(base_env, SOJOURN_TEST_INPUT="1", SOJOURN_UPDATE_FEED=str(work / "no-feed"),
                   SOJOURN_VOICE_OUT_FILE=str(work / "heard.wav"), SOJOURN_VOICE_MIC_FILE="none")
        launcher = start_launcher(inst, ["--desktop", "--", "--minutes", "3"], env)
        up = launcher and wait(lambda: u("find name=Launch;type=Button"), 40)
        check(bool(up), "the launcher opens from the stub (the Start menu's target)")
        shown = wait(lambda: text_of(f"Sojourn {version}"), 10)
        check(bool(shown), "its footer shows the version", shown or "no such text")
        try:
            u("set name=Username;type=Edit | " + " ".join(ACCOUNT))
            u("select name=other;type=RadioButton")
            u("set name=Login URI;type=Edit | http://127.0.0.1:9700/")
        except Exception as e:
            print(f"     (the form: {e})")
        u(f"set name=Password | {PASSWORD}")
        u("invoke name=Launch;type=Button")
        arrived = wait(lambda: state(data).get("state") == "world", 150, 1)
        check(bool(arrived), "the client logs in to the local grid and gets in world", json.dumps(state(data))[:200])
        client = state(data).get("pid")
        logs = data / "logs"
        session_log = lambda: "\n".join(p.read_text(encoding="utf-8", errors="replace") for p in sorted(logs.glob("*.log")) if p.name != "launcher-console.log")
        if arrived and client:
            mods = access.modules_of(client)
            for dll in ("SojournVR.dll", "sojourn_voice.dll", "glfw3.dll", "shaderc_shared.dll", "coreclr.dll"):   # (openjp2 loads with the first texture)
                where = next((m for m in mods if Path(m).name.lower() == dll.lower()), None)
                check(where and Path(where).parent == inst / "current", f"{dll} loaded from the install", where or "not loaded")
            foreign = [m for m in mods if str(ROOT).lower() in m.lower() or "dotnet-sdk" in m.lower()]
            check(not foreign, "nothing loaded from the repo or the .NET SDK", "; ".join(foreign) or f"{len(mods)} modules")
            import psutil
            py = wait(lambda: next((c for c in psutil.Process(client).children(recursive=True)
                                    if Path(c.exe()).parent == inst / "current" / "python"), None), 30, 1)
            check(py, "the assistant's sidecar runs on the install's own Python", f"pid {py.pid}" if py else "none under the client")
            heard = wait(lambda: re.search(r"parakeet tdt 0\.6b v2 ready.*", session_log()), 90, 2)
            check(heard, "hearing loads Parakeet from the install's models", heard[0] if heard else
                  "; ".join(re.findall(r"assistant: .*(?:hearing|parakeet|whisper).*", session_log())[-3:])[:300])
            said = re.search(rf"\b{re.escape(version)}\b", session_log())
            check(said, f"the client's log names {version}", said[0] if said else "no")
            u("invoke name=Stop")
            check(bool(wait(lambda: not access.alive(client), 30, 1)), "Stop closes the client")
        crashes = re.findall(r".*(?:Unhandled exception|assistant: Traceback).*", session_log())
        check(not crashes, "no unhandled exceptions in the logs", "; ".join(crashes)[:300] or "none")
        close_launcher(inst, launcher)

        # ---- an update: refused unsigned, offered signed, installed only at the press
        feed, next_version = next_release(release_dir, work, n)
        bad = work / "bad-feed"
        shutil.copytree(feed, bad)
        f = bad / f"releases.{CHANNEL}.json"
        f.write_bytes(f.read_bytes().replace(b'"Assets"', b'"Assets" ', 1))   # one byte more: the signature no longer holds
        sign(feed / f"releases.{CHANNEL}.json")
        shutil.copy2(feed / f"releases.{CHANNEL}.json.sig", bad / f"releases.{CHANNEL}.json.sig")
        launcher = start_launcher(inst, ["--desktop"], dict(env, SOJOURN_UPDATE_FEED=str(bad)))
        wait(lambda: u("find name=Launch;type=Button"), 40)
        offered = wait(lambda: u("find type=Button;name~=update"), 12)
        check(launcher and not offered, "a feed whose signature doesn't hold: no update offered", offered or "none")
        close_launcher(inst, launcher)
        launcher = start_launcher(inst, ["--desktop"], dict(env, SOJOURN_UPDATE_FEED=str(feed), SOJOURN_TEST_INPUT="0"))
        wait(lambda: u("find name=Launch;type=Button"), 40)
        offered = wait(lambda: u(f"find type=Button;name~={next_version}"), 30)
        check(bool(offered), f"the signed feed: {next_version} offered in the footer", (offered or "none")[:160])
        time.sleep(3)
        check((reg(UNINSTALL_KEY) or {}).get("DisplayVersion") == f"0.1.{n}", "nothing installed before the press")
        if offered:
            u(f"invoke type=Button;name~={next_version}")   # not the person's press (SOJOURN_TEST_INPUT off): refused
            time.sleep(5)
            check((reg(UNINSTALL_KEY) or {}).get("DisplayVersion") == f"0.1.{n}" and access.alive(launcher),
                  "a press not from the person (UI Automation) installs nothing")
            close_launcher(inst, launcher)
            launcher = start_launcher(inst, ["--desktop"], dict(env, SOJOURN_UPDATE_FEED=str(feed)))
            wait(lambda: u(f"find type=Button;name~={next_version}"), 40)
            u(f"invoke type=Button;name~={next_version}")
            moved = wait(lambda: (reg(UNINSTALL_KEY) or {}).get("DisplayVersion") == f"0.1.{n + 1}", 300, 2)
            check(bool(moved), f"the press installs {next_version} (Add/Remove Programs says 0.1.{n + 1})")
            # Update.exe clears the old folder (thousands of files) before it starts the new launcher
            again = launcher_in(inst, not_pid=launcher, secs=300)
            back = again and wait(lambda: text_of(f"Sojourn {next_version}"), 60)
            check(bool(back), "the launcher starts again on its own, its footer the new version", back or "no such text")
            check(bool(access.files_under(data / "secrets" / "logins")) and (data / "vr-settings.json").exists(),
                  "the saved account and settings are there after the update", ", ".join(sorted(p.name for p in data.iterdir())))
        kill_under(inst)

        # ---- uninstalling, as Add/Remove Programs does (its quiet form)
        (data / "cache" / "probe").mkdir(parents=True, exist_ok=True)
        (data / "cache" / "probe" / "x.bin").write_bytes(b"x" * 1024)
        quiet = (reg(UNINSTALL_KEY) or {}).get("QuietUninstallString", "")
        m = re.match(r'"([^"]+)"\s*(.*)', quiet)
        check(bool(m), "the entry has a quiet uninstall", quiet)
        if m:
            pid = hidden([m[1], *m[2].split()], env=base_env)
            wait(lambda: not access.alive(pid), 120, 1)
            gone = wait(lambda: not inst.exists() or not any(inst.iterdir()), 60, 1)
            check(gone, "the program is removed", "" if gone else ", ".join(p.name for p in inst.iterdir()))
            check(reg(UNINSTALL_KEY) is None, "the Add/Remove Programs entry is removed")
            check(not shortcut.exists(), "the Start menu shortcut is removed")
            cleared = wait(lambda: not any((data / d).exists() for d in ("cache", "logs"))
                           and not list(data.glob("*.removing-*")), 60, 1)
            check(cleared, "the caches and logs are removed", ", ".join(sorted(p.name for p in data.iterdir())))
            kept = [p.name for p in data.iterdir()]
            check("secrets" in kept and "vr-settings.json" in kept, "the person's accounts and settings are kept", ", ".join(sorted(kept)))
    except access.Stop:
        pass
    except Exception as e:
        check(False, "the check (stopped)", f"{type(e).__name__}: {e}")
    finally:
        if inst.exists():
            kill_under(inst)
            if reg(UNINSTALL_KEY) is not None and (inst / "Update.exe").exists():
                subprocess.run([str(inst / "Update.exe"), "--uninstall", "--silent"], capture_output=True, creationflags=CREATE_NO_WINDOW)
        if reg(UNINSTALL_KEY) is not None:
            subprocess.run(["reg", "delete", UNINSTALL_KEY, "/f"], capture_output=True, creationflags=CREATE_NO_WINDOW)
        if (START_MENU / f"{TITLE}.lnk").exists():
            (START_MENU / f"{TITLE}.lnk").unlink()
        if server:
            try:
                u("quit", timeout=5)
            except Exception:
                pass
            if access.alive(server):
                access.kill(server)
    check(access.files_under(real_data) == real_before, "this PC's own %LOCALAPPDATA%\\Sojourn untouched")
    strays = {k: [f for f in access.files_under(d) if f.split("/")[0] not in ("NVIDIA", "D3DSCache", "AMD", "Intel")]
              for k, d in env_dirs.items()}
    written = [f"%{k}%/{f}" for k, fs in strays.items() for f in fs]
    check(not written, "nothing written by way of %TEMP%, %LOCALAPPDATA% or %APPDATA%", "; ".join(written[:8]) or "none")
    for d in ("build-check", "tree-check"):
        shutil.rmtree(DIST / d, ignore_errors=True)
    if keep:
        print(f"     (kept: {work})")
    else:
        shutil.rmtree(work, ignore_errors=True)
    print("all as they should be" if not fails else f"{fails} failed")
    return 1 if fails else 0


# ---- backing up the key -----------------------------------------------------------------------------------------------
# The key never reaches stdout or a log: only paths and "ok" are printed. The backup is the key as PKCS#8 under the
# passphrase (cryptography's best available: scrypt and AES-256), for the user to put on their Google Drive by hand; the
# passphrase goes in a file beside it (to be kept somewhere other than the Drive, then deleted there) and, DPAPI'd, in
# local/secrets so this PC can restore too.

BACKUP_PASSPHRASE = SIGNING_KEY.with_name("update-signing-backup.passphrase")

ENCRYPT = r"""
import json, sys
from cryptography.hazmat.primitives import serialization
a = json.loads(sys.stdin.read())
key = serialization.load_pem_private_key(a["pem"].encode(), None)
out = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                        serialization.BestAvailableEncryption(a["pass"].encode()))
back = serialization.load_pem_private_key(out, a["pass"].encode())
pub = serialization.PublicFormat.SubjectPublicKeyInfo
assert back.public_key().public_bytes(serialization.Encoding.DER, pub) == key.public_key().public_bytes(serialization.Encoding.DER, pub)
open(a["path"], "wb").write(out)
print("ok")
"""

DECRYPT = r"""
import json, sys
from cryptography.hazmat.primitives import serialization
a = json.loads(sys.stdin.read())
key = serialization.load_pem_private_key(open(a["path"], "rb").read(), a["pass"].encode())
sys.stdout.write(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                   serialization.NoEncryption()).decode())
"""


def backup_key():
    """python tools/package.py backup: the encrypted key and its passphrase into Downloads/sojourn-key-backup."""
    import relay
    import secrets
    if not SIGNING_KEY.exists():
        print(f"no key at {SIGNING_KEY.relative_to(ROOT)}")
        return 1
    alphabet = "abcdefghjkmnpqrstuvwxyz23456789"
    passphrase = "-".join("".join(secrets.choice(alphabet) for _ in range(5)) for _ in range(7))
    out = Path.home() / "Downloads" / "sojourn-key-backup"
    out.mkdir(parents=True, exist_ok=True)
    pem_path = out / "sojourn-update-signing-key.pem"
    r = subprocess.run([str(VENV_PY), "-c", ENCRYPT], capture_output=True, creationflags=CREATE_NO_WINDOW,
                       input=json.dumps({"pem": relay.read_secret(SIGNING_KEY), "pass": passphrase,
                                         "path": str(pem_path)}).encode())
    if r.returncode or r.stdout.decode().strip() != "ok":
        print("failed:", r.stderr.decode(errors="replace")[-400:])
        return 1
    relay.write_secret(BACKUP_PASSPHRASE, passphrase)
    (out / "passphrase.txt").write_text(
        "The passphrase for sojourn-update-signing-key.pem, Sojourn's update-signing key (Studio Quire).\n"
        "Put the .pem on Google Drive; keep this passphrase somewhere else (a password manager), then delete this file.\n"
        "Restore: python tools/package.py restore <the .pem> <a file holding the passphrase>\n\n"
        + passphrase + "\n", encoding="utf-8")
    print(f"ok: {pem_path}\n    {out / 'passphrase.txt'}\n    (the passphrase also DPAPI'd in "
          f"{BACKUP_PASSPHRASE.relative_to(ROOT)})")
    return 0


def restore_key():
    """python tools/package.py restore FILE.pem PASSPHRASE_FILE [--force]: the backup back into local/secrets."""
    import relay
    args = [a for a in sys.argv[2:] if not a.startswith("--")]
    if len(args) != 2:
        print("restore FILE.pem PASSPHRASE_FILE [--force]")
        return 2
    if SIGNING_KEY.exists() and "--force" not in sys.argv:
        print(f"{SIGNING_KEY.relative_to(ROOT)} exists; --force to replace it")
        return 1
    lines = [l.strip() for l in Path(args[1]).read_text(encoding="utf-8").splitlines() if l.strip()]
    r = subprocess.run([str(VENV_PY), "-c", DECRYPT], capture_output=True, creationflags=CREATE_NO_WINDOW,
                       input=json.dumps({"path": args[0], "pass": lines[-1]}).encode())
    if r.returncode:
        print("couldn't open it (wrong passphrase?)")
        return 1
    relay.write_secret(SIGNING_KEY, r.stdout.decode())
    print(f"ok: {SIGNING_KEY.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") else "build"
    commands = {"build": build, "check": check_package, "key": make_key, "backup": backup_key, "restore": restore_key}
    if cmd not in commands:
        print(__doc__)
        sys.exit(2)
    sys.exit(commands[cmd]())
