"""Sojourn Access checked without sight, on the local grid (docs/access.md > Testing without sight; python tools/access.py ...).

  python tools/access.py build     build src/Sojourn.Access (Release) at below-normal priority, two MSBuild nodes
  python tools/access.py run       for a person: build if needed, start it on this (visible) desktop with the login
                                   form, the local grid filled in (no test switches)
  python tools/access.py visitor [COMMAND...]
                                   the Access Visitor for a person's session: started if it isn't up (no console
                                   window; Atelier 68,38, with its fixture box beside it), then sent COMMAND, e.g.
                                   `visitor say hi back`, `visitor im hello`, `visitor fmenu`, `visitor ftext`,
                                   `visitor fperm`, `visitor fgive`, `visitor offer`; Access Tester's UUID is filled in
                                   where a command needs one. `visitor stop` takes its objects back and logs it out.
                                   Its log: local/access-visitor/visitor.log. Commands: tools/visitor/Program.cs.
  python tools/access.py tools     build QuireUia (tools/quireuia) and the Access Visitor (tools/visitor) into local/bin
  python tools/access.py test      the scripted check below; prints `ok  name: detail` / `FAIL name: detail` per check
                                   (ACCESS_STEPS=inventory,walk runs only the steps whose names have those words, after
                                   the login's checks: for working on one part; ACCESS_NO_RENDER=1 starts the app with --no-render,
                                   nothing on the GPU, for steps that don't need the view; ACCESS_NO_GPU=1 hides Vulkan's drivers
                                   from it, as on a PC whose graphics can't run the view: it draws on the processor with
                                   local/lavapipe (fetch-lavapipe), or with ACCESS_NO_SOFTWARE=1 gives up (walk mode runs on
                                   the collision model alone); ACCESS_SOFTWARE=1 draws on the processor with the GPU there;
                                   ACCESS_ACCOUNTS=Walk runs as Walk Tester and Walk Visitor on ports 10 up, its files in
                                   local/access-test-walk, never throwing out a person's Access Tester; SOJOURN_BUILD_ROOT
                                   builds and runs from a tree of its own)
  python tools/access.py fetch-lavapipe
                                   Mesa's lavapipe (the view on the processor) into local/lavapipe, from mesa-dist-win's
                                   release, checked by SHA-256; package needs it
  python tools/access.py package [--dirty]
                                   the build for a tester's own PC: self-contained (no .NET needed), win-x64, with its
                                   README and licences, zipped to local/dist/SojournAccess-0.1.0.N-win-x64.zip (N the
                                   commit count; built from HEAD's export, however busy the working tree is, or from
                                   the working tree with --dirty, marked so when src/ differs); it keeps its
                                   data in %LOCALAPPDATA%\\Sojourn Access and logs in to Second Life unless run with --local
  python tools/access.py package --check [ZIP] [--keep] [--bare [--no-software]]
                                   the zip (default the newest in local/dist) unpacked in a fresh folder under %TEMP% and
                                   run as a tester would, on a hidden desktop of its own, as Package Tester on the local
                                   grid: ok/FAIL lines as the test's (docs/access-app.md > Packaging); then everything it
                                   made removed (--keep leaves the folder). --bare: as on a PC with no Vulkan driver and
                                   no Media Foundation (Windows N): Vulkan's drivers hidden, sojourn_voice.dll removed; the
                                   view draws on the processor (the package's lavapipe), or gives up with --no-software
  python tools/access.py stop      close whatever a broken run left (our app, visitors and QuireUia server, by path)
  python tools/access.py sweep     delete whatever the Access Visitor still owns in Atelier (the grid console)

How it sees without the screen: the app runs on a hidden Win32 desktop ("sojourn-access"), started there by QuireUia
(tools/quireuia), so it can't take the person's focus, keyboard or screen. QuireUia's server runs beside it on that
desktop (UI Automation and WinEvents don't cross desktops) and records what a screen reader is told (UIA notifications,
focus, alerts) to local/access-test/events.jsonl, and takes commands over TCP: the UIA tree, focus, set/get, and posted
keys (SOJOURN_TEST_INPUT=keys makes the app judge them as the person's hardware presses, every other rule of its
real-input guard applying; ACCESS_TEST_INPUT=1 lets them all through). A second avatar, the Access Visitor
(tools/visitor), talks to it, IMs it and owns a scripted fixture box that sends it a script menu, a text box and a
permission question, and hears what the app says. The app's renderer out of sight runs as it would for a person, its
GPU "lost" once 15 s in (SOJOURN_OFFSCREEN_LOSE_AFTER); the renderer step checks it came back, a picture by the
assistant's snapshot op, the collision model and touches on a box's faces (the visitor's `facebox`). The walk-mode
step (M3) builds a course of its own in Atelier East and drives walk mode by held keys (QuireUia's keydown/keyup): its
cues, the sonar, paths around walls, following the visitor, driving its cart, and a seated crossing in its kart; world
audio goes to a file (SOJOURN_VOICE_OUT_FILE), never the speakers. The sound-as-a-map step (M4) checks which earcon
starts for what arrives and where it's placed, the beacon (on the visitor, on a seat walked to by hand, on a //go's and
the assistant's destinations) and walk mode's O marks, by SojournWalk status' record of the cues sent.

The app is started as `dotnet SojournAccess.dll` with the studio SDK, not as SojournAccess.exe: the apphost can't
find .NET here (the SDK isn't a global install), and a new exe path would make Windows Firewall ask on the visible
desktop (the SDK's dotnet.exe already has an answer). The test also sets SOJOURN_TEST_NO_NVDA=1: on a hidden desktop
NVDA's controller client claims NVDA runs, and the read-out would go there instead of to UIA notifications.

Nothing touches the clipboard (it's shared with the person's desktop): the teleport is tested by //tp, not Ctrl+G.
The grid must be up (tools/grid.py start); it's never stopped or reset here.

Written by Claude Opus 5.5, 2026-10-29.
"""
import json
import math
import os
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

# ---- what to adjust -------------------------------------------------------------------------------------------------

from buildenv import DOTNET   # noqa: E402  (the .NET SDK wherever it's installed)
APP_PROJECT = ROOT / "src" / "Sojourn.Access" / "Sojourn.Access.csproj"
# SOJOURN_BUILD_ROOT=dir: build and run from a tree of its own (src/Directory.Build.props), for a second session's runs
APP_BIN = ((Path(os.environ["SOJOURN_BUILD_ROOT"]) / "bin" / "Sojourn.Access") if os.environ.get("SOJOURN_BUILD_ROOT")
           else ROOT / "src" / "Sojourn.Access" / "bin") / "Release" / "net10.0-windows10.0.22621.0"
APP_EXE = APP_BIN / "SojournAccess.exe"
APP_DLL = APP_BIN / "SojournAccess.dll"
APP_AS_EXE = False                 # the apphost can't find .NET here; dotnet + the dll it is
QUIREUIA = ROOT / "local" / "bin" / "QuireUia" / "QuireUia.exe"
VISITOR_DLL = ((Path(os.environ["SOJOURN_BUILD_ROOT"]) / "bin" / "Visitor") if os.environ.get("SOJOURN_BUILD_ROOT")
               else ROOT / "local" / "bin" / "Visitor") / "Visitor.dll"
OUT = ROOT / "local" / ("access-test-" + os.environ["ACCESS_ACCOUNTS"].strip().lower() if os.environ.get("ACCESS_ACCOUNTS")
                         else "access-test")   # ACCESS_ACCOUNTS (below): its own folder too
VISITOR_HOME = ROOT / "local" / "access-visitor"   # the `visitor` command's (a person's session), apart from the test's
EVENTS = OUT / "events.jsonl"
HEARD = OUT / "heard.wav"           # what the app played (walk mode's cues among it)

GRID_URI = "http://127.0.0.1:9700/"
PASSWORD = "sojourn-local"
TESTER = ("Access", "Tester")      # the app's account
VISITOR = ("Access", "Visitor")    # the bot's
REGION = "Atelier"
TESTER_START = (64, 36, 24)
VISITOR_START = (68, 38, 24)
FIXTURE_XY = (68, 34)              # within 20 m of the visitor (it obeys on channel 7) and in chat range of the tester
WALK_TO = (66, 36)
WALK_COMMAND = "//go xyz 66,36"
TP_TO = (80, 40, 24)
TP_COMMAND = "//tp Atelier/80/40/24"
PILOT_TO = (71, 40)                # the assistant's walk (LLAgent startAutoPilot, as its go tool), from TP_TO
PILOT_STOP = 1.5
PLACE_TO = (88, 48, 24)            # Places' Go to region (over 5 m from TP_TO: the history keeps places within 5 m once)
ROUTE = ((84, 46), (82, 44))       # a route notecard's two //go xyz points
AWAY = (68, 66)                    # where the visitor walks to leave the tester's chat range
FACEBOX_XY = (62, 34)              # the visitor's touch-reporting box for the view's checks, near the tester's start
FACEBOX = "Access Facebox"
LOSE_AFTER = 15                    # the view out of sight's GPU "lost" once this many seconds after its first frame
NO_RENDER = os.environ.get("ACCESS_NO_RENDER") == "1"   # the app with --no-render: no renderer out of sight at all
NO_GPU = os.environ.get("ACCESS_NO_GPU") == "1"         # Vulkan's drivers hidden from the app, as on a PC whose graphics can't run the view
# the software Vulkan driver the view falls back to without a GPU (Mesa's lavapipe: fetch-lavapipe), given to repo runs by
# SOJOURN_SOFTWARE_VULKAN (a package has it beside the program); ACCESS_NO_SOFTWARE=1 leaves it out, so without a GPU the
# view gives up; ACCESS_SOFTWARE=1 draws on it with the GPU there (SOJOURN_OFFSCREEN_SOFTWARE)
LAVAPIPE = ROOT / "local" / "lavapipe"
SOFTWARE_OK = (LAVAPIPE / "lvp_icd.x86_64.json").exists() and os.environ.get("ACCESS_NO_SOFTWARE") != "1"
SOFTWARE = SOFTWARE_OK and (NO_GPU or os.environ.get("ACCESS_SOFTWARE") == "1")   # the view expected on the processor
VIEWLESS = NO_RENDER or (NO_GPU and not SOFTWARE)       # no pictures or faces; the collision model needs no GPU
RELAY = os.environ.get("ACCESS_RELAY")   # a relay token's name (tools/relay.py): the app's assistant on Studio Quire's relay
                                         # (the HUD step's reading; the describing step's no-assistant checks are skipped)
LIST_DELAY_MS = 3000               # People's and Objects' first fills held back this long (SL's objects wait for names)

Q_PORT = 9885                      # QuireUia's server
V_PORT = 9886                      # the visitor
API_PORT = 9790                    # the app's agent API

# ACCESS_ACCOUNTS=Walk: the test's own pair of accounts (Walk Tester, Walk Visitor) and ports, so a run never throws out
# a person's guided session as Access Tester (it still uses Atelier's spots: run it while nobody's testing there)
if os.environ.get("ACCESS_ACCOUNTS"):
    _first = os.environ["ACCESS_ACCOUNTS"].strip()
    TESTER, VISITOR = (_first, "Tester"), (_first, "Visitor")
    Q_PORT, V_PORT, API_PORT = Q_PORT + 10, V_PORT + 10, API_PORT + 10
TNAME, VNAME = " ".join(TESTER), " ".join(VISITOR)
# two more bots, the tester's friends (made once, kept on the grid), online before the app logs in: the grid tells of
# every friend online at login, which the app says as one line (MainForm.FriendsOnline.cs); out of the way in Atelier
# Northeast, so no list or arrival check meets them
PALS = ((VISITOR[0], "Palone"), (VISITOR[0], "Paltwo"))
PAL_PORTS = (V_PORT + 2, V_PORT + 3)
PAL_START = ("Atelier Northeast", 200, 200, 30)

LOGIN_SECS = 120
NOTICE_SECS = 15
MAX_TABS = 25                      # Tab stops walked before calling the order endless

# UIA control types whose focusable instances must have a name
NAMED_TYPES = {"Edit", "Document", "Button", "CheckBox", "RadioButton", "ComboBox", "List", "Tree", "Tab", "TabItem",
               "Slider", "Spinner", "DataGrid", "Table", "Hyperlink", "MenuItem", "SplitButton"}
# the contract's controls (AutomationId = WinForms Name) and the UIA types that would be right for them
ROLES = {"chatInput": {"Edit"}, "chatHistory": {"Edit", "Document"}, "chatPeople": {"List", "DataGrid", "Table"}, "conversations": {"Tab"},
         "convInput": {"Edit"}, "convHistory": {"Edit", "Document"}, "people": {"List", "DataGrid", "Table"},
         "objects": {"List", "DataGrid", "Table"}, "notices": {"List", "DataGrid", "Table"},
         "noticeText": {"Edit", "Document"}, "noticeReply": {"Edit"}, "noticeIgnore": {"Button"},
         "assistantInput": {"Edit"}, "assistantHistory": {"Edit", "Document"}, "inventory": {"Tree"},
         "inventorySearch": {"Edit"}, "inventoryShow": {"ComboBox"}, "editorText": {"Edit", "Document"},
         "landmarks": {"Tree"}, "history": {"List", "DataGrid", "Table"}, "goRegion": {"Edit"},
         "regions": {"List", "DataGrid", "Table"}, "searchWords": {"Edit"}, "searchKind": {"ComboBox"},
         "results": {"List", "DataGrid", "Table"}, "friends": {"List", "DataGrid", "Table"}, "groups": {"List", "DataGrid", "Table"},
         "profileText": {"Edit", "Document"}, "keysText": {"Edit", "Document"}}

VK = {"back": 8, "minus": 189, "equals": 187, "tab": 9, "enter": 13, "ctrl": 17, "esc": 27, "space": 32, "del": 46, "oem_open": 219, "oem_close": 221, "f1": 112, "f3": 114, "f4": 115, "f5": 116, "f6": 117, "f10": 121, "apps": 93, "alt": 18, "pgup": 33, "pgdn": 34,
      "home": 36, "end": 35, "up": 38, "down": 40, "left": 37, "right": 39,
      **{str(d): 48 + d for d in range(10)}, **{c: ord(c.upper()) for c in "abcdefghijklmnopqrstuvwxyz"}}

CREATE_NO_WINDOW, BELOW_NORMAL, NEW_GROUP = 0x08000000, 0x00004000, 0x00000200
HOURS = ["twelve", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven"]


# ---- building ---------------------------------------------------------------------------------------------------------

def dotnet_build(project, configuration="Release", *extra):
    """dotnet build at below-normal priority (inherited by MSBuild's nodes), two nodes, no build server."""
    p = subprocess.Popen([str(DOTNET), "build", str(project), "-c", configuration, "-m:2", "-nr:false",
                          "-p:UseSharedCompilation=false", "-v:m", "-clp:NoSummary", *extra],
                         stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                         encoding="utf-8", errors="replace", creationflags=CREATE_NO_WINDOW | BELOW_NORMAL)
    out = p.communicate()[0]
    for line in out.splitlines():
        if re.search(r"error|-> |FAILED", line):
            print(line.strip())
    return p.returncode


def build():
    return dotnet_build(APP_PROJECT)


def tools():
    rc = dotnet_build(ROOT / "tools" / "quireuia" / "QuireUia.csproj")
    # with SOJOURN_BUILD_ROOT, the visitor into that tree too (another run's visitor holds the usual one open), built
    # even when QuireUia isn't (another run holds it open too; it rarely changes)
    out = [f"-p:OutputPath={VISITOR_DLL.parent}{os.sep}"] if os.environ.get("SOJOURN_BUILD_ROOT") else []
    return dotnet_build(ROOT / "tools" / "visitor" / "Visitor.csproj", "Release", *out) or rc


# ---- the package for a tester -------------------------------------------------------------------------------------------

DIST = ROOT / "local" / "dist"
PACKAGE_FILES = ROOT / "src" / "Sojourn.Access" / "Package"   # README.txt, licences/ (texts not found elsewhere)
NVDA_DIR = ROOT / "local" / "nvda"                              # tools/screenreader.py fetch
VOICE_DLL = ROOT / "local" / "voice" / "build" / "Release" / "sojourn_voice.dll"   # tools/voice.py build
WEBRTC_LICENCES = ROOT / "local" / "webrtc" / "LICENSES" / "webrtc-license.txt"
NUGET = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))
RID = "win-x64"
# the assistant's Python side in a package: python.org's embeddable CPython (3.12, as the venv; 3.12.10 is the last
# 3.12 with Windows builds, and the venv's compiled packages are ABI-compatible across 3.12.x), checked by its hash,
# with the venv's claude-agent-sdk and what it needs (the SDK carries Claude Code itself, _bundled/claude.exe)
PY_EMBED = "3.12.10"
PY_EMBED_URL = f"https://www.python.org/ftp/python/{PY_EMBED}/python-{PY_EMBED}-embed-amd64.zip"
PY_EMBED_SHA256 = "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3"   # python.org's MD5 matched too; gitleaks:allow
PY_EMBED_ZIP = DIST / "python-embed" / f"python-{PY_EMBED}-embed-amd64.zip"
VENV_PY = ROOT / "local" / "assistant-venv" / "Scripts" / "python.exe"
BUNDLED_DISTS = ["claude-agent-sdk", "anthropic"]   # with their dependencies (anthropic: describing's lean calls); no hearing or local voice
# the software Vulkan driver the view falls back to on a PC whose graphics can't run it (Render's Gpu/SoftwareVulkan.cs):
# Mesa's lavapipe as mesa-dist-win builds it, its release archive checked by the SHA-256 GitHub lists for it, the two
# files taken from it checked too (fetch-lavapipe); in every package's lavapipe folder
MESA = "26.2.5"
MESA_URL = f"https://github.com/pal1000/mesa-dist-win/releases/download/{MESA}/mesa3d-{MESA}-release-msvc.7z"
MESA_SHA256 = "997123ef4ce8896204ab912d5b54f19372c487ff9835bcefafa2fca23b8953c2"   # gitleaks:allow
LAVAPIPE_FILES = {"vulkan_lvp.dll": "cb50fd861b77743c7ab46a946af15a7af7a7e0bc697644c1e777d2784080570d",   # gitleaks:allow
                  "lvp_icd.x86_64.json": "5b867ac6fe25dca5f23a3479a18333df2577542a0ac565ba34a9666af03fdd92"}   # gitleaks:allow


def fetch_lavapipe():
    """Mesa's lavapipe for the view on the processor into local/lavapipe (repo runs find it by SOJOURN_SOFTWARE_VULKAN,
    which the test sets; package copies it): mesa-dist-win's release archive into a folder of its own under local/dist,
    checked by its SHA-256, the driver and its manifest taken out with 7-Zip and checked by theirs."""
    import hashlib
    sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
    if all((LAVAPIPE / n).exists() and sha(LAVAPIPE / n) == h for n, h in LAVAPIPE_FILES.items()):
        print(f"{LAVAPIPE.relative_to(ROOT)}: Mesa {MESA}'s lavapipe already there")
        return 0
    seven = shutil.which("7z") or next((str(p) for p in (Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "7-Zip" / "7z.exe",)
                                        if p.exists()), None)
    if not seven:
        print("7-Zip is needed to unpack mesa-dist-win's .7z (winget install 7zip.7zip)")
        return 1
    archive = DIST / f"mesa-{MESA}" / Path(MESA_URL).name
    if not archive.exists() or sha(archive) != MESA_SHA256:
        print(f"fetching {MESA_URL}")
        archive.parent.mkdir(parents=True, exist_ok=True)
        tmp = archive.with_suffix(".part")
        urllib.request.urlretrieve(MESA_URL, tmp)
        tmp.replace(archive)
    if sha(archive) != MESA_SHA256:
        print(f"{archive.name} isn't the file mesa-dist-win published (its SHA-256 differs); not used")
        return 1
    out = archive.parent / "x64"
    shutil.rmtree(out, ignore_errors=True)
    r = subprocess.run([seven, "e", str(archive), f"-o{out}", "-y"] + [f"x64/{n}" for n in LAVAPIPE_FILES],
                       capture_output=True, text=True, creationflags=CREATE_NO_WINDOW)
    wrong = [n for n, h in LAVAPIPE_FILES.items() if not (out / n).exists() or sha(out / n) != h]
    if r.returncode or wrong:
        print(f"unpacking failed or gave other files ({', '.join(wrong)}):\n{r.stdout[-800:]}{r.stderr[-800:]}")
        return 1
    LAVAPIPE.mkdir(parents=True, exist_ok=True)
    for n in LAVAPIPE_FILES:
        shutil.copy2(out / n, LAVAPIPE / n)
    print(f"{LAVAPIPE.relative_to(ROOT)}: Mesa {MESA}'s lavapipe ({(LAVAPIPE / 'vulkan_lvp.dll').stat().st_size / 1e6:.0f} MB)")
    return 0


def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, creationflags=CREATE_NO_WINDOW).stdout.strip()


def package():
    try:
        return package_build()
    finally:
        shutil.rmtree(access_tree(), ignore_errors=True)


def access_tree():
    tag = sys.argv[sys.argv.index("--tag") + 1] if "--tag" in sys.argv[2:-1] else None
    return ACCESS_TREE.with_name(f"{ACCESS_TREE.name}-{tag}") if tag else ACCESS_TREE


def package_build():
    """A self-contained build in a zip under local/dist for a tester's own PC (docs/access-app.md > Packaging):
    SojournAccess.exe with the .NET runtime inside, the renderer's files, NVDA's controller client, voice and world
    audio (sojourn_voice.dll), a README for a screen-reader user and the licences. Versioned 0.1.0.N, N the commit count;
    built from HEAD exported into local/dist/access-tree, whatever the working tree holds; --dirty builds the working
    tree instead (marked so in the version's commit and the zip's name when src/ or assistant/ differ)."""
    dirty_ok = "--dirty" in sys.argv[2:]
    lean = "--lean" in sys.argv[2:]       # without the assistant (no Python, no Claude Code: about 120 MB less)
    tag = sys.argv[sys.argv.index("--tag") + 1] if "--tag" in sys.argv[2:-1] else None
    if tag and not re.fullmatch(r"[a-z0-9][a-z0-9\-]{0,30}", tag):
        print("--tag: lowercase letters, digits and dashes")
        return 1
    changed = git("status", "--porcelain", "--", "src", "assistant") if dirty_ok else ""
    missing = [p for p in (NVDA_DIR / "x64" / "nvdaControllerClient.dll", NVDA_DIR / "license.txt", VOICE_DLL, WEBRTC_LICENCES,
                           *(LAVAPIPE / n for n in LAVAPIPE_FILES)) if not p.exists()]
    if missing:
        print("missing: " + ", ".join(str(p.relative_to(ROOT)) for p in missing)
              + " (python tools/screenreader.py fetch; python tools/voice.py build; python tools/access.py fetch-lavapipe)")
        return 1
    head = git("rev-parse", "HEAD")   # once: another session may commit meanwhile
    version = f"0.1.0.{git('rev-list', '--count', head)}"
    commit = head[:7] + ("-dirty" if changed else "")
    name = f"Sojourn Access {version}"
    # --tag: a try, apart from releases (its own folder under local/dist, which `package --check` doesn't pick by itself)
    out_dir = DIST / tag if tag else DIST
    marks = "".join(f"-{m}" for m in (tag, "lean" if lean else None, "dirty" if changed else None) if m)
    zip_path = out_dir / f"SojournAccess-{version}{marks}-{RID}.zip"
    stage = out_dir / "stage" / name
    # its own bin and obj (Directory.Build.props), so the repo's build folder isn't touched
    build_root = DIST / (f"build-{tag}" if tag else "build")
    shutil.rmtree(stage, ignore_errors=True)
    stage.mkdir(parents=True)
    src = ROOT
    if not dirty_ok:
        # a fresh build tree too: the export's files carry the commit's time, which older outputs could look newer than
        shutil.rmtree(build_root, ignore_errors=True)
        src = access_tree()
        export(head, src)

    print(f"publishing {name} ({commit}), self-contained {RID}")
    env = dict(os.environ, SOJOURN_BUILD_ROOT=str(build_root))
    p = subprocess.Popen([str(DOTNET), "publish", str(src / APP_PROJECT.relative_to(ROOT)), "-c", "Release", "-r", RID, "--self-contained", "true",
                          "-p:PublishSingleFile=true", f"-p:Version={version}", f"-p:SourceRevisionId={commit}",
                          "-p:DebugType=embedded", "-p:SatelliteResourceLanguages=en", "-o", str(stage),
                          "-m:2", "-nr:false", "-p:UseSharedCompilation=false", "-v:m", "-clp:NoSummary"],
                         env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                         encoding="utf-8", errors="replace", creationflags=CREATE_NO_WINDOW | BELOW_NORMAL)
    out = p.communicate()[0]
    for line in out.splitlines():
        if re.search(r"error|-> |FAILED", line):
            print(line.strip())
    if p.returncode:
        return p.returncode

    # native libraries' symbols (libSkiaSharp.pdb alone is 95 MB; our own are embedded in the exe), and ASP.NET's IIS
    # module (the agent API is Kestrel on loopback)
    for f in [*stage.rglob("*.pdb"), stage / "aspnetcorev2_inprocess.dll"]:
        f.unlink(missing_ok=True)
    shutil.copy2(NVDA_DIR / "x64" / "nvdaControllerClient.dll", stage)
    (stage / "lavapipe").mkdir()   # the view on the processor, where no GPU can run it (fetch_lavapipe)
    for n in LAVAPIPE_FILES:
        shutil.copy2(LAVAPIPE / n, stage / "lavapipe" / n)
    if not (stage / "sojourn_voice.dll").exists():
        shutil.copy2(VOICE_DLL, stage)
    readme = (src / PACKAGE_FILES.relative_to(ROOT) / "README.txt").read_text(encoding="utf-8").replace("{VERSION}", version)
    keep, drop = ("lean", "assistant") if lean else ("assistant", "lean")   # [assistant]...[/assistant] blocks
    readme = re.sub(rf"\[{drop}\].*?\[/{drop}\]\n?", "", readme, flags=re.S)
    write_text(stage / "README.txt", re.sub(rf"\[/?{keep}\]\n?", "", readme))
    licences(stage / "Licences", build_root, src)
    if not lean and bundle_assistant(stage, source=src) != 0:
        return 1

    expected = ["SojournAccess.exe", "nvdaControllerClient.dll", "sojourn_voice.dll", "openjp2.dll", "libSkiaSharp.dll",
                "shaderc_shared.dll", "lavapipe/vulkan_lvp.dll", "lavapipe/lvp_icd.x86_64.json", "Shaders", "Character", "Textures", "Fonts", "linden"]
    if not lean:
        expected += ["python/python.exe", "assistant/main.py"]
    absent = [e for e in expected if not (stage / e).exists()]
    if absent:
        print("not in the package: " + ", ".join(absent))
        return 1

    import zipfile
    zip_path.unlink(missing_ok=True)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for f in sorted(stage.rglob("*")):
            if f.is_file():
                z.write(f, f"{name}/{f.relative_to(stage).as_posix()}")
    unpacked = sum(f.stat().st_size for f in stage.rglob("*") if f.is_file())
    print(f"{zip_path.relative_to(ROOT)}: {zip_path.stat().st_size / 1e6:.0f} MB ({unpacked / 1e6:.0f} MB unpacked, "
          f"{sum(1 for f in stage.rglob('*') if f.is_file())} files); the folder: {stage.relative_to(ROOT)}")
    # the release notes name the public commit (tools/publish/access/RELEASES.md), the one made from this HEAD
    public = None if changed else public_commit(head)
    print(f"its source for the release notes: studio-quire/sojourn {public[:7]}" if public else
          f"no public snapshot of {commit} yet (local/publish/map.txt): python tools/publish.py makes one before a release")
    return 0


def bundle_assistant(stage, dists=None, imports=(), source=ROOT):
    """The assistant's Python side beside the program (docs/access-app.md > Packaging): python.org's embeddable CPython
    in python/, the venv's claude-agent-sdk and its dependencies in python/Lib/site-packages (Claude Code itself is the
    SDK's _bundled/claude.exe), assistant/'s code and prompts, all compiled ahead (unchecked-hash .pyc, so nothing is
    written beside the program when it runs; Sidecar.cs starts it with -B too). Then a smoke test: the embedded Python
    imports the sidecar's modules and finds its Claude Code inside the package. dists: the venv's distributions to take
    (BUNDLED_DISTS, Access's; Sojourn's package adds hearing and voice), imports: more modules the smoke test imports,
    source: the tree whose assistant/ is bundled (Sojourn's package: a commit's export)."""
    dists_wanted = dists or BUNDLED_DISTS
    import hashlib
    import zipfile
    if not VENV_PY.exists():
        print("missing the assistant's venv (python tools/assistant.py setup)")
        return 1
    if not PY_EMBED_ZIP.exists():
        print(f"fetching {PY_EMBED_URL}")
        PY_EMBED_ZIP.parent.mkdir(parents=True, exist_ok=True)
        tmp = PY_EMBED_ZIP.with_suffix(".part")
        urllib.request.urlretrieve(PY_EMBED_URL, tmp)
        tmp.replace(PY_EMBED_ZIP)
    if hashlib.sha256(PY_EMBED_ZIP.read_bytes()).hexdigest() != PY_EMBED_SHA256:
        print(f"{PY_EMBED_ZIP.name} isn't the file python.org published (its SHA-256 differs); delete it to fetch it again")
        return 1
    py = stage / "python"
    with zipfile.ZipFile(PY_EMBED_ZIP) as z:
        z.extractall(py)
    pth = next(py.glob("python3*._pth"))
    pth.write_text(f"{pth.stem}.zip\n.\nLib\\site-packages\nimport site\n", encoding="ascii")
    site = py / "Lib" / "site-packages"
    site.mkdir(parents=True)

    # what the venv has installed for the SDK: each distribution's files by its RECORD (scripts outside it left out)
    walk = r"""
import importlib.metadata as md, json, re, sys
from packaging.markers import Marker
# each distribution with the extras asked of it (mcp needs pyjwt[crypto], whose extra brings cryptography)
want, seen, done = [(n, "") for n in sys.argv[1:]], {}, set()
while want:
    n, extra = want.pop()
    k = re.sub(r"[-_.]+", "-", n).lower()
    if (k, extra) in done: continue
    done.add((k, extra))
    d = seen.get(k) or md.distribution(n)
    seen[k] = d
    for r in d.requires or []:
        req, _, marker = r.partition(";")
        env = {"extra": extra, "sys_platform": "win32", "platform_system": "Windows", "os_name": "nt",
               "python_version": "3.12", "python_full_version": "3.12.10", "implementation_name": "cpython",
               "platform_python_implementation": "CPython", "platform_machine": "AMD64"}
        if marker.strip() and not Marker(marker).evaluate(env): continue
        if not marker.strip() and extra: continue   # (taken with no extra already)
        m = re.match(r"\s*([A-Za-z0-9_.\-]+)\s*(?:\[([^\]]*)\])?", req)
        want.append((m.group(1), ""))
        want.extend((m.group(1), x.strip()) for x in (m.group(2) or "").split(",") if x.strip())
out = {}
for k, d in seen.items():
    files = [str(f) for f in d.files or [] if not str(f).startswith("..") and "__pycache__" not in str(f)]
    meta = d.metadata
    lic = meta.get("License-Expression") or (meta.get("License") or "").split("\n")[0][:80] or next(
        (c.split(" :: ")[-1] for c in meta.get_all("Classifier") or [] if c.startswith("License ::")), "see its files")
    out[d.metadata["Name"]] = {"version": d.version, "root": str(d.locate_file("")), "files": files, "licence": lic,
                               "home": meta.get("Home-page") or next((u.split(", ", 1)[-1] for u in meta.get_all("Project-URL") or []), "")}
print(json.dumps(out))
"""
    r = subprocess.run([str(VENV_PY), "-c", walk, *dists_wanted], capture_output=True, text=True, encoding="utf-8",
                       creationflags=CREATE_NO_WINDOW)
    if r.returncode:
        print("reading the venv's packages failed:\n" + r.stderr[-1500:])
        return 1
    dists = json.loads(r.stdout)
    for name, d in dists.items():
        root = Path(d["root"])
        for f in d["files"]:
            dest = site / f
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(root / f, dest)
    claude_exe = site / "claude_agent_sdk" / "_bundled" / "claude.exe"
    if not claude_exe.exists():
        print("the SDK's Claude Code (claude_agent_sdk/_bundled/claude.exe) isn't in the venv")
        return 1

    # the sidecar: its code and prompts
    own = stage / "assistant"
    own.mkdir()
    for f in sorted((source / "assistant").iterdir()):
        if f.suffix in (".py", ".md") and f.is_file():
            shutil.copy2(f, own / f.name)

    # compiled ahead with the embedded Python itself, below normal and on two workers
    r = subprocess.run([str(py / "python.exe"), "-E", "-s", "-m", "compileall", "-q", "-j", "2",
                        "--invalidation-mode", "unchecked-hash", str(site), str(own)],
                       capture_output=True, text=True, creationflags=CREATE_NO_WINDOW | BELOW_NORMAL)
    if r.returncode:
        print("compiling the assistant's Python failed:\n" + (r.stdout + r.stderr)[-1500:])
        return 1

    # the smoke test: as Sidecar.cs starts it (-B -E -s, the data folder in SOJOURN_LOCAL), every module imports and
    # its Claude Code is the package's own
    import tempfile
    probe = ("import sys, os; sys.path.insert(0, sys.argv[1]); "
             "import main, brain, tools, moretools, describe, describer, anthropic, jobs, build, private, privatetools, harness, home, provider, paths, voice, hearing; "
             + "".join(f"import {m}; " for m in imports) +
             "print(provider.cli()); print(paths.LOCAL)")
    with tempfile.TemporaryDirectory(prefix="sojourn-bundle-") as data:
        env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("PYTHON", "SOJOURN_", "VIRTUAL_ENV"))}
        env["SOJOURN_LOCAL"] = data
        env["PATH"] = without_claude(env.get("PATH", ""))
        r = subprocess.run([str(py / "python.exe"), "-B", "-E", "-s", "-c", probe, str(own)], capture_output=True, text=True,
                           env=env, cwd=data, creationflags=CREATE_NO_WINDOW)
    found = r.stdout.strip().splitlines()
    if r.returncode or not found or Path(found[0]).resolve() != claude_exe.resolve():
        print("the bundled sidecar's smoke test failed:\n" + (r.stdout + r.stderr)[-2000:])
        return 1
    print(f"assistant bundled: Python {PY_EMBED}, {len(dists)} packages, claude-agent-sdk {dists.get('claude-agent-sdk', {}).get('version')}")

    # their licences: Python's, Claude Code's terms, and each package's own files (in its dist-info) named here
    lic = stage / "Licences"
    shutil.copy2(py / "LICENSE.txt", lic / f"Python {PY_EMBED} (PSF).txt")
    lines = ["The assistant's parts (the python and assistant folders): the Python runtime and the packages the assistant "
             "runs on, each with its licence. Their licence files are also inside python\\Lib\\site-packages, in each "
             "package's .dist-info folder.", "",
             f"Python {PY_EMBED} (python.org's embeddable package): PSF License, in 'Python {PY_EMBED} (PSF).txt' here",
             "Claude Code (python\\Lib\\site-packages\\claude_agent_sdk\\_bundled\\claude.exe, as the Claude Agent SDK "
             "ships it): copyright Anthropic PBC, all rights reserved; its use is subject to Anthropic's Commercial Terms "
             "of Service (https://www.anthropic.com/legal/commercial-terms)"]
    for name, d in sorted(dists.items(), key=lambda kv: kv[0].lower()):
        lines.append(f"{name} {d['version']}: {d['licence']}" + (f" ({d['home']})" if d["home"] else ""))
    write_text(lic / "The assistant's parts.txt", "\n".join(lines) + "\n")
    return 0


def without_claude(path):
    """PATH without any folder holding a Claude Code (a tester's PC has none: the package's own is the SDK's)."""
    return os.pathsep.join(p for p in path.split(os.pathsep)
                           if p and not any((Path(p) / n).exists() for n in ("claude.exe", "claude.cmd", "claude")))


def write_text(path, text):
    """Plain text for Notepad and screen readers: CRLF, UTF-8 without a BOM."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(text.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))


# ---- building from a commit (Sojourn's package too) ----------------------------------------------------------------------

ACCESS_TREE = DIST / "access-tree"   # package's export of HEAD (a try's: access-tree-TAG); never a junction to local/ in it
PUBLISH_MAP = ROOT / "local" / "publish" / "map.txt"   # tools/publish.py: "<time> <repo> <public> from <private>"


def export(commit, dest):
    """The commit's tracked files, and nothing else, into dest (git archive), so a build of it ignores the working tree
    (several sessions work in it at once). Its local/ stays absent: the build reads the main tree's local/ by path."""
    import tarfile
    shutil.rmtree(dest, ignore_errors=True)
    dest.mkdir(parents=True)
    # into a file, then read: streamed through a pipe, tarfile stops at the archive's end marker and git, still writing
    # its padding, blocks on the pipe forever (2026-11-01)
    tar = dest.with_name(dest.name + ".tar")
    try:
        r = subprocess.run(["git", "archive", "--format=tar", "-o", str(tar), commit], cwd=ROOT, capture_output=True,
                           timeout=300, creationflags=CREATE_NO_WINDOW)
        if r.returncode:
            raise RuntimeError(f"git archive {commit} failed: {r.stderr.decode(errors='replace')[-800:]}")
        with tarfile.open(tar) as t:
            t.extractall(dest, filter="data")
    finally:
        tar.unlink(missing_ok=True)


def publish_map():
    """map.txt's sojourn-public lines as (public, private) full hashes, oldest first."""
    if not PUBLISH_MAP.exists():
        return []
    rows = [l.split() for l in PUBLISH_MAP.read_text(encoding="utf-8").splitlines()]
    return [(r[2], r[4]) for r in rows if len(r) == 5 and r[1] == "sojourn-public" and r[3] == "from"]


def public_commit(private):
    """The studio-quire/sojourn commit publish.py made from this private one (full hashes), or None."""
    return next((pub for pub, priv in reversed(publish_map()) if priv == private), None)


def private_commit(public):
    """The private commit a public one (or its prefix) was made from, or None."""
    return next((priv for pub, priv in reversed(publish_map()) if pub.startswith(public)), None)


def licences(dest, build_root, src=ROOT):
    """The licences of what ships: our notices for the parts that need one, the texts beside them, and every NuGet
    package with files in the package (from the publish's project.assets.json) with its licence. src: the tree built."""
    import html
    import xml.etree.ElementTree as ET
    files = src / PACKAGE_FILES.relative_to(ROOT)
    lgpl = (NVDA_DIR / "license.txt").read_text(encoding="utf-8", errors="replace")
    write_text(dest / "NVDA Controller Client (LGPL-2.1).txt",
               "nvdaControllerClient.dll is NV Access's NVDA Controller Client from NVDA 2026.3, unmodified "
               "(https://www.nvaccess.org, source at https://github.com/nvaccess/nvda). It is licensed under the GNU Lesser "
               "General Public License, version 2.1, below.\n\n" + lgpl)
    write_text(dest / "Second Life viewer parts (LGPL-2.1).txt",
               "Parts of Sojourn's renderer are ported from the Second Life viewer's source code, and the Character and "
               "Textures folders hold files from the viewer (the system avatar's meshes, skeleton and parameters; the "
               "default particle image). Copyright Linden Research, Inc. They are licensed under the GNU Lesser General "
               "Public License, version 2.1, below.\n\n" + lgpl)
    write_text(dest / "libwebrtc, in sojourn_voice.dll.txt",
               "sojourn_voice.dll (voice chat and world audio) is built over Linden Lab's build of Google's libwebrtc "
               "(github.com/secondlife/3p-webrtc-build). Its licence and those of the libraries in it follow.\n\n"
               + html.unescape(WEBRTC_LICENCES.read_text(encoding="utf-8", errors="replace")))
    write_text(dest / "OpenJPEG, openjp2.dll.txt", "openjp2.dll is OpenJPEG 2.5.3, built from its source.\n\n"
               + (files / "licences" / "OpenJPEG.txt").read_text(encoding="utf-8"))
    shutil.copy2(files / "licences" / "Mesa lavapipe.txt", dest / "Mesa lavapipe and LLVM, in the lavapipe folder.txt")
    write_text(dest / "Archivo font (OFL-1.1).txt",
               (src / "src" / "Sojourn.Session" / "Assets" / "Fonts" / "Archivo-OFL.txt").read_text(encoding="utf-8"))

    # the NuGet packages with files in the build, and the .NET runtime packs
    assets = json.loads((build_root / "obj" / "Sojourn.Access" / "project.assets.json").read_text(encoding="utf-8"))
    target = next(t for k, t in assets["targets"].items() if k.endswith("/" + RID))
    lines = ["The libraries in Sojourn Access, each with its licence (the texts that came with them follow in this "
             "folder where they had one).", ""]
    for key, lib in sorted(target.items(), key=lambda kv: kv[0].lower()):
        if lib.get("type") != "package" or not (lib.get("runtime") or lib.get("native") or lib.get("runtimeTargets")):
            continue
        if all(p.endswith("_._") for p in list(lib.get("runtime", {})) + list(lib.get("native", {}))) and not lib.get("runtimeTargets"):
            continue
        pkg_id, ver = key.split("/")
        folder = NUGET / assets["libraries"][key]["path"]
        licence, project = nuspec_licence(folder, ET)
        lines.append(f"{pkg_id} {ver}: {licence}" + (f" ({project})" if project else ""))
        for f in sorted(folder.glob("*")):
            if f.is_file() and re.match(r"(?i)(license|licence|third-party-notices|notice)", f.name):
                shutil.copy2(f, dest / f"{pkg_id} - {f.name}")
    for pack in ("microsoft.netcore.app.runtime", "microsoft.windowsdesktop.app.runtime", "microsoft.aspnetcore.app.runtime"):
        found = sorted(NUGET.glob(f"{pack}.{RID}/*"))
        if not found:
            continue
        folder = found[-1]
        lines.append(f"{pack}.{RID} {folder.name} (.NET, inside SojournAccess.exe): MIT")
        for f in sorted(folder.glob("*")):
            if f.is_file() and re.match(r"(?i)(license|third-party-notices)", f.name):
                shutil.copy2(f, dest / f"{pack}.{RID} - {f.name}")
    lines.append("Microsoft.Windows.SDK.NET and WinRT.Runtime (the Windows SDK's .NET projection, inside SojournAccess.exe): "
                 "https://aka.ms/WinSDKLicenseURL")
    lines += ["", "OpenJPEG 2.5.3 (openjp2.dll): BSD-2-Clause", "libwebrtc m144 (in sojourn_voice.dll): BSD-3-Clause and others",
              "NVDA Controller Client 2026.3 (nvdaControllerClient.dll): LGPL-2.1",
              f"Mesa {MESA}'s lavapipe (the lavapipe folder, mesa-dist-win's build): MIT and others, with LLVM inside "
              "(Apache-2.0 WITH LLVM-exception); in 'Mesa lavapipe and LLVM, in the lavapipe folder.txt'",
              "Second Life viewer parts (renderer code, Character, Textures): LGPL-2.1, Linden Research, Inc.",
              "The linden folder (LibreMetaverse's copy of the viewer's data files): see cc-by-sa-3.0.txt in it",
              "Archivo font (Fonts): SIL Open Font License 1.1",
              "SCOWL's common English words (inside SojournAccess.dll, for the log filter): Copyright 2000-2018 by Kevin "
              "Atkinson, http://wordlist.aspell.net; permission to use, copy, modify, distribute and sell them for any "
              "purpose is granted without fee, provided the copyright notice and this permission notice appear in all "
              "copies and supporting documentation; provided as is, without express or implied warranty", "",
              "Where a library's licence is MIT or Apache-2.0 and its package carried no text of it, the text is in "
              "MIT.txt or Apache-2.0.txt here; the copyright is its authors', as named at its project's address."]
    shutil.copy2(files / "licences" / "Apache-2.0.txt", dest / "Apache-2.0.txt")
    write_text(dest / "MIT.txt", MIT)
    write_text(dest / "README.txt", "\n".join(lines) + "\n")


def nuspec_licence(folder, ET):
    """(the licence: an SPDX expression, the package's file or a URL; the project's address) from a package's nuspec."""
    spec = next(folder.glob("*.nuspec"), None)
    meta = next((e for e in ET.parse(spec).getroot() if e.tag.endswith("metadata")), None) if spec else None
    if meta is None:
        return "unknown", None
    def text(tag):
        e = next((e for e in meta if e.tag.split("}")[-1] == tag), None)
        return (e.text or "").strip() if e is not None else None
    lic = next((e for e in meta if e.tag.split("}")[-1] == "license"), None)
    project = text("projectUrl") or text("repository")
    if lic is not None and lic.get("type") == "expression":
        return lic.text.strip(), project
    if lic is not None:
        return f"the licence file {lic.text.strip()} in the package (copied here)", project
    return text("licenseUrl") or "unknown", project


MIT = """MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
"""


# ---- talking to things --------------------------------------------------------------------------------------------------

class ToolError(Exception):
    pass


def tcp(port, line, timeout=60):
    """One command to QuireUia's server or the visitor (a line out, everything until the close back)."""
    with socket.create_connection(("127.0.0.1", port), timeout=timeout) as s:
        s.sendall((line.replace("\r", "").replace("\n", "\\n") + "\n").encode("utf-8"))
        chunks = []
        while True:
            b = s.recv(65536)
            if not b:
                break
            chunks.append(b)
    reply = b"".join(chunks).decode("utf-8", errors="replace").rstrip("\n")
    if reply.startswith("ERR"):
        raise ToolError(f"{line[:60]}: {reply}")
    return reply


def u(line, timeout=60):
    try:
        return tcp(Q_PORT, line, timeout)
    except ToolError as e:
        # UI Automation's element went stale between QuireUia finding it and using it (a window or tab just made or
        # laid out): found afresh once, a moment later; an element really gone fails again
        if "ElementNotAvailableException" not in str(e):
            raise
        time.sleep(0.5)
        return tcp(Q_PORT, line, timeout)


def v(line, timeout=60):
    return tcp(V_PORT, line, timeout)


def api(pump, op, timeout=15, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{API_PORT}/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 20) as r:
        out = json.loads(r.read())
    if isinstance(out, dict) and out.get("error"):
        raise ToolError(f"{pump}.{op}: {out['error']}")
    return out


def status():
    with urllib.request.urlopen(f"http://127.0.0.1:{API_PORT}/status", timeout=5) as r:
        return json.loads(r.read())


def wait(f, secs, step=0.3):
    """f() until it's truthy (exceptions count as not yet), or None after secs."""
    end = time.time() + secs
    while True:
        try:
            got = f()
            if got:
                return got
        except Exception:
            pass
        if time.time() >= end:
            return None
        time.sleep(step)


def port_free(port):
    with socket.socket() as s:
        return s.connect_ex(("127.0.0.1", port)) != 0


# ---- what QuireUia says about elements ----------------------------------------------------------------------------------

DESC = re.compile(r'^(?P<type>\w+) "(?P<name>(?:[^"\\]|\\.)*)"(?P<rest>.*)$')


def parse(desc):
    """QuireUia's one-line element description as a dict: type, name, id, focusable, offscreen, focused."""
    desc = desc.strip()
    m = DESC.match(desc)
    if not m:
        return {"type": "", "name": "", "id": "", "raw": desc}
    rest = m["rest"]
    ident = re.search(r" id=(\S+)", rest)
    hwnd = re.search(r" hwnd=([0-9a-f]+)", rest)
    return {"type": m["type"], "name": json.loads('"' + m["name"] + '"'), "id": ident[1] if ident else "",
            "hwnd": hwnd[1] if hwnd else "",
            "focusable": " focusable" in rest, "offscreen": " offscreen" in rest, "focused": " FOCUSED" in rest,
            "value": (re.search(r' value="((?:[^"\\]|\\.)*)"', rest) or [None, ""])[1], "raw": desc}


def label(d):
    if not d:
        return "nothing"
    return f'{d["type"]} "{d["name"]}"' + (f' ({d["id"]})' if d.get("id") else "")


def same(a, b):
    """The same control: its window if it has one, else what it's called."""
    return bool(a and b) and ((a["hwnd"], a["id"]) == (b["hwnd"], b["id"]) if a["hwnd"] or b["hwnd"]
                              else (a["type"], a["name"], a["id"]) == (b["type"], b["name"], b["id"]))


def focused():
    """What has the keyboard focus in the app's GUI thread (UIA's own focus means nothing off the input desktop)."""
    for line in u("focus").splitlines():
        if line.startswith("thread focus: "):
            return parse(line[len("thread focus: "):])
    return None


def focus_label():
    try:
        return "focus: " + label(focused())
    except Exception as e:
        return f"focus unknown ({e})"


def wait_focus(pred, secs=5):
    return wait(lambda: (lambda d: d if d and pred(d) else None)(focused()), secs, 0.2)


def key(*names, target="focus"):
    """Posted keys to whatever has the app's focus (or a selector): "ctrl+enter", "alt+3", "f6", "2"..."""
    specs = []
    for n in names:
        parts = n.lower().split("+")
        specs.append("+".join(parts[:-1] + [str(VK[parts[-1]])]))
    u(f"key {target} | {','.join(specs)}")


def type_text(text, target="focus"):
    u(f"chars {target} | {text}")


def value_of(sel):
    """The full value or text of an element (get's value: or text: line)."""
    out = u(f"get {sel}")
    m = re.search(r"^(?:value|text): (.*)", out, re.S | re.M)
    return m[1] if m else ""


def find(sel):
    try:
        return [parse(re.sub(r"^\d+: ", "", l)) for l in u(f"find {sel}").splitlines() if l.strip()]
    except ToolError:
        return []


def tab_items():
    """The tab items of the app's visible tab controls as COM UI Automation (a screen reader's) walks them. The managed
    client's find, which QuireUia's find uses, leaves out a WinForms tab control's first and last tab items (2026-10-30:
    the conversation's own tab, the only one, was never found), which COM UIA and MSAA both list."""
    try:
        out = u(f"tabs {APP_PID}")
    except ToolError:
        return []
    return [json.loads(m) for m in re.findall(r'TabItem ("(?:[^"\\]|\\.)*")', out)]


def selection(sel):
    m = re.search(r"^selection: (.*)$", u(f"get {sel}"), re.M)
    return m[1] if m else ""


# ---- the event log ------------------------------------------------------------------------------------------------------

def events():
    try:
        return [json.loads(x) for x in EVENTS.read_text(encoding="utf-8").splitlines() if x.strip()]
    except (OSError, ValueError):
        return []


def mark():
    return len(events())


def said_since(start):
    """What the app announced since start (UIA notifications from its process)."""
    return [e for e in events()[start:] if e.get("kind") == "notification"]


def focus_events_since(start):
    return [e for e in events()[start:] if e.get("kind") == "focus"]


# ---- processes ----------------------------------------------------------------------------------------------------------

def launch_hidden(args, env=None, cwd=None):
    """Start a program on the hidden desktop through QuireUia's launcher; its PID."""
    cmd = [str(QUIREUIA), "launch"] + (["--cwd", str(cwd)] if cwd else []) + ["--"] + [str(a) for a in args]
    r = subprocess.run(cmd, env=env, capture_output=True, text=True, timeout=90, creationflags=CREATE_NO_WINDOW)
    if r.returncode != 0 or not r.stdout.strip().split()[0].isdigit():
        raise ToolError(f"launch {Path(str(args[0])).name}: {r.stdout.strip()} {r.stderr.strip()}")
    return int(r.stdout.strip().split()[0])


def alive(pid):
    r = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH", "/FO", "CSV"], capture_output=True, text=True,
                       creationflags=CREATE_NO_WINDOW)
    return f'"{pid}"' in r.stdout


def kill(pid):
    subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True, creationflags=CREATE_NO_WINDOW)


def ours():
    """Our processes still running, by command line: (pid, what) for an app with our base dir, a QuireUia server
    logging into our folder, a visitor on our port. Others' runs of the same binaries (another session's app with its
    own base dir and ports) aren't ours and are left alone."""
    ps = ("Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.Name -ne 'powershell.exe' } | "
          "ForEach-Object { \"$($_.ProcessId)`t$($_.Name)`t$($_.CommandLine)\" }")
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True,
                       creationflags=CREATE_NO_WINDOW)
    out, base = str(OUT).lower(), str(OUT / "app").lower()
    found = []
    for line in r.stdout.splitlines():
        parts = line.split("\t")
        if len(parts) != 3 or not parts[0].isdigit():
            continue
        cmd = parts[2].lower()
        if "sojournaccess" in cmd and base in cmd:
            found.append((int(parts[0]), "app"))
        # (by its log file: the folder alone is a prefix of ACCESS_ACCOUNTS runs' own, access-test-walk)
        elif str(QUIREUIA).lower() in cmd and " serve" in cmd and str(EVENTS).lower() in cmd:
            found.append((int(parts[0]), "QuireUia"))
        elif str(VISITOR_DLL).lower() in cmd and f"--port {V_PORT}" in cmd:
            found.append((int(parts[0]), "visitor"))
        elif str(VISITOR_DLL).lower() in cmd and any(f"--port {p} " in cmd for p in PAL_PORTS):
            found.append((int(parts[0]), "pal"))
    return found


def online(first, last):
    """Whether the grid has this account logged in (a second login would throw the first out)."""
    import grid
    return any(re.match(rf"\s*{first}\s+{last}\s", l) for l in grid.console("show users", wait=1))


def stop():
    left = ours()
    for pid, what in left:
        kill(pid)
        print(f"killed {what} {pid}")
    if not left:
        print("nothing of ours running")
    return 0


def sweep():
    import grid
    vid = visitor_id()
    if not vid:
        print("no Access Visitor account")
        return 1
    # the console's selected region is shared with whoever else uses it, so it's changed back at once
    lines = grid.console(f"change region {REGION}", wait=1.5, answers=(f"show object owner {vid}", "change region root"))
    n = next((int(m[1]) for l in lines if (m := re.search(r"(\d+) object\(s\) found", l))), 0)
    if n:
        lines = grid.console(f"change region {REGION}", wait=1.5, answers=(f"delete object owner {vid}", "y", "change region root"))
    print(f"{n} object(s) of the Access Visitor's in {REGION}" + (", deleted" if n else ""))
    return 0


def visitor_id():
    import grid
    for line in grid.console(f"show account {VISITOR[0]} {VISITOR[1]}", wait=1):
        m = re.search(r"ID:\s*([0-9a-f-]{36})", line)
        if m:
            return m[1]
    return None


def tester_id_lookup():
    """Access Tester's UUID, from the grid console (cached in VISITOR_HOME)."""
    import grid
    cache = VISITOR_HOME / "tester.id"
    if cache.exists():
        return cache.read_text().strip()
    for line in grid.console(f"show account {TESTER[0]} {TESTER[1]}", wait=1):
        m = re.search(r"ID:\s*([0-9a-f-]{36})", line)
        if m:
            VISITOR_HOME.mkdir(parents=True, exist_ok=True)
            cache.write_text(m[1])
            return m[1]
    return None


def needs_build():
    if not APP_DLL.exists():
        return True
    built = APP_DLL.stat().st_mtime
    for d in ("Sojourn.Access", "Sojourn.Session", "Sojourn.Core", "Sojourn.Render"):
        for f in (ROOT / "src" / d).rglob("*"):
            if f.suffix in (".cs", ".csproj") and "obj" not in f.parts and "bin" not in f.parts and f.stat().st_mtime > built:
                return True
    return False


def run_for_person():
    if needs_build():
        print("building Sojourn Access")
        if build():
            return 1
    env = {k: val for k, val in os.environ.items() if not k.startswith("SOJOURN_TEST")}
    p = subprocess.Popen([str(DOTNET), str(APP_DLL), "--grid", GRID_URI], cwd=APP_BIN, env=env, stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=CREATE_NO_WINDOW | NEW_GROUP)
    print(f"Sojourn Access started (pid {p.pid}): log in as {TESTER[0]} {TESTER[1]}, password {PASSWORD}")
    return 0


UUID_RE = re.compile(r"^[0-9a-fA-F-]{36}$")


def visitor_command(args):
    import grid
    if args[:1] == ["stop"]:
        try:
            print(v("cleanup", timeout=10))
            time.sleep(2)
            print(v("quit", timeout=10))
        except OSError:
            print("the visitor isn't running")
            return 0
        # it logs out before it exits
        if not wait(lambda: not any(w == "visitor" for _, w in ours()), 20, 1):
            print("it didn't exit: python tools/access.py stop")
        return 0
    try:
        v("pos", timeout=5)
    except OSError:
        if not VISITOR_DLL.exists() and tools():
            return 1
        if not grid.ready():
            print("the local grid is down (python tools/grid.py start)")
            return 1
        grid.account(*VISITOR, PASSWORD)
        VISITOR_HOME.mkdir(parents=True, exist_ok=True)
        log = open(VISITOR_HOME / "visitor.log", "w", encoding="utf-8")
        subprocess.Popen([str(DOTNET), str(VISITOR_DLL), "--port", str(V_PORT), "--first", VISITOR[0], "--last", VISITOR[1],
                          "--password", PASSWORD, "--start", "%s/%d/%d/%d" % (REGION, *VISITOR_START), "--login", GRID_URI],
                         cwd=VISITOR_HOME, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                         creationflags=CREATE_NO_WINDOW | BELOW_NORMAL | NEW_GROUP)
        where = wait(lambda: v("pos", timeout=5), 60, 1)
        if not where:
            print("the visitor didn't log in (local/access-visitor/visitor.log)")
            return 1
        vz = visitor_settled()[2]
        print(f"visitor up: {where}; {v(f'fixture {FIXTURE_XY[0]} {FIXTURE_XY[1]} {vz + 0.3:.1f}', timeout=60)}")
    if not args:
        return 0
    verb, rest = args[0], args[1:]
    if verb in ("fmenu", "fmenu12", "fdance", "ftext", "fperm", "fgive", "offer", "friend") and not rest:
        rest = [tester_id_lookup()]
    elif verb in ("im", "tplure") and not (rest and UUID_RE.match(rest[0])):
        rest = [tester_id_lookup()] + rest
    try:
        print(v(" ".join([verb] + rest)))
    except ToolError as e:
        print(e)
        return 1
    return 0


# ---- where things are ---------------------------------------------------------------------------------------------------

def tester_where():
    """(x, y, z, heading): the heading a compass bearing, 0 north, clockwise (SL's yaw is 0 east, counter-clockwise)."""
    p = api("LLAgent", "getPosition")
    x, y, z = p["region"][:3]
    return x, y, z, (90 - math.degrees(p["euler"]["yaw"])) % 360


def visitor_where():
    m = re.search(r"<([-\d.]+), ([-\d.]+), ([-\d.]+)>", v("pos"))
    return tuple(float(m[i]) for i in (1, 2, 3))


def visitor_settled(secs=10):
    """Where the visitor stands once it has landed (it logs in at the start's height and falls to the ground)."""
    last = visitor_where()
    end = time.time() + secs
    while time.time() < end:
        time.sleep(1)
        now = visitor_where()
        if abs(now[2] - last[2]) < 0.05:
            return now
        last = now
    return last


def clock(dx, dy, heading):
    compass = (90 - math.degrees(math.atan2(dy, dx))) % 360
    rel = (compass - heading) % 360
    return HOURS[int(round(rel / 30)) % 12], rel


# ---- the test ---------------------------------------------------------------------------------------------------------

class Run:
    def __init__(self):
        self.ok = 0
        self.failed = []

    def check(self, name, cond, detail=""):
        if cond:
            self.ok += 1
        else:
            self.failed.append(name)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)
        return bool(cond)

    def expect(self, name, words, start, secs=8):
        """An announcement containing all these words since start."""
        def look():
            return next((e for e in said_since(start) if all(w.lower() in (e.get("text") or "").lower() for w in words)), None)
        got = wait(look, secs)
        heard = " | ".join(e.get("text") or "" for e in said_since(start))
        self.check(name, got, got["text"] if got else f"not heard; heard: {heard[-300:] or 'nothing'}")
        return got

    def heard_by_visitor(self, name, pred, since_n, secs=10, what=""):
        """A chat line the visitor heard after its line number since_n, matching pred."""
        def look():
            lines = v(f"heard 50 since {since_n}")
            for l in ([] if lines == "none" else lines.splitlines()):
                d = json.loads(l)
                if pred(d):
                    return d
        got = wait(look, secs, 0.5)
        self.check(name, got, f'{got["type"]} from {got["from"]}: {got["msg"]}' if got else f"not heard ({what})")
        return got


PAL_LOGS = {}


def pal_start(i):
    """One of the tester's friends (PALS) logged in, out of the way (PAL_START), taking any friendship offered."""
    first, last = PALS[i]
    log = PAL_LOGS[i] = open(OUT / "visitor" / f"pal{i + 1}.log", "w", encoding="utf-8")
    subprocess.Popen([str(DOTNET), str(VISITOR_DLL), "--port", str(PAL_PORTS[i]), "--first", first, "--last", last,
                      "--password", PASSWORD, "--start", "%s/%d/%d/%d" % PAL_START, "--login", GRID_URI, "--accept-friends"],
                     cwd=OUT / "visitor", stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                     creationflags=CREATE_NO_WINDOW | BELOW_NORMAL | NEW_GROUP)


def pal_up(i):
    try:
        return tcp(PAL_PORTS[i], "pos", 5)
    except (OSError, ToolError):
        return None


def pal_quit(i):
    """Logs a friend out (it exits once logged out); whether it was up."""
    try:
        tcp(PAL_PORTS[i], "quit", 10)
    except (OSError, ToolError):
        return False
    wait(lambda: not pal_up(i), 15, 0.5)
    return True


def visitor_n():
    """The visitor's running line number now (what it hears and receives after this is newer)."""
    nums = [json.loads(l)["n"] for kind in ("heard", "events") for l in v(f"{kind} 1").splitlines() if l.startswith("{")]
    return max(nums, default=0)


class Stop(Exception):
    """A check the rest can't go on without failed."""


def test():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    import grid
    run = Run()

    # ---- before anything starts
    if not APP_AS_EXE and needs_build():   # a test of stale binaries reads as a test of the sources
        print("building Sojourn Access", flush=True)
        if build():
            return 1
    app_file = APP_EXE if APP_AS_EXE else APP_DLL
    missing = [p for p in (QUIREUIA, VISITOR_DLL, app_file) if not p.exists()]
    if missing:
        print("missing: " + ", ".join(str(p.relative_to(ROOT)) for p in missing) + " (python tools/access.py tools / build)")
        return 1
    if not grid.ready():
        print("the local grid is down (python tools/grid.py start)")
        return 1
    busy = [p for p in (Q_PORT, V_PORT, API_PORT) + PAL_PORTS if not port_free(p)]
    running = ours()
    if busy or running:
        print(f"something is already running (ports in use: {busy}; ours: {running}): python tools/access.py stop")
        return 1
    for first, last in (TESTER, VISITOR) + PALS:
        r = grid.account(first, last, PASSWORD)
        print(f"account {first} {last}: {'exists' if r == 'exists' else 'made'}")
        if online(first, last):
            print(f"{first} {last} is logged in already (another run, or a person's session): logging in again would "
                  "throw it out; wait for it to end")
            return 1

    shutil.rmtree(OUT, ignore_errors=True)
    (OUT / "visitor").mkdir(parents=True)
    (OUT / "app").mkdir(parents=True)

    procs = {"server": None, "app": None, "visitor": None}
    try:
        session(run, procs)
    except Stop:
        pass
    except Exception as e:
        run.check("the run (stopped)", False, f"{type(e).__name__}: {e}")
    finally:
        for i in range(len(PALS)):
            pal_quit(i)
        shutdown(run, procs["server"], procs["app"], procs["visitor"])
    return finish(run)


def session(run, procs):
    check = run.check
    nonce = uuid.uuid4().hex[:6]

    # ---- QuireUia's server on the hidden desktop, the visitor and its fixture, the app
    procs["server"] = launch_hidden([QUIREUIA, "serve", "--port", Q_PORT, "--log", EVENTS])
    if not check("QuireUia's server answers", wait(lambda: u("help", timeout=5), 20), f"pid {procs['server']}"):
        raise Stop
    # UIA's notification registration only takes once a window is on the desktop (the server makes one of its own)
    listening = wait(lambda: any(e.get("src") == "server" and e.get("kind") == "listening" and e.get("text") == "com notification"
                                 for e in events()), 15, 0.5)
    if not check("it's listening for announcements", listening, "UIA notifications registered" if listening else
                 "; ".join(e.get("text") or "" for e in events() if e.get("src") == "server")):
        raise Stop

    # a run stopped before its cleanup leaves the visitor's things out, and a fixture box left in Atelier obeys its words
    # on channel 7 as the new one does: two menus for one fdance, the dance step's re-sent menu beside a stranger's (it
    # failed so, 2026-11-01). The visitor isn't on the grid elsewhere (checked before), so whatever it owns is a leftover
    vid = visitor_id()
    for region in (REGION, COURSE_REGION):
        if vid and (n := clear_owned(region, vid)):
            print(f"     ({n} of the visitor's things an earlier run left in {region} cleared)")

    vlog = open(OUT / "visitor" / "visitor.log", "w", encoding="utf-8")
    procs["visitor"] = subprocess.Popen(
        [str(DOTNET), str(VISITOR_DLL), "--port", str(V_PORT), "--first", VISITOR[0], "--last", VISITOR[1],
         "--password", PASSWORD, "--start", "%s/%d/%d/%d" % (REGION, *VISITOR_START), "--login", GRID_URI],
        cwd=OUT / "visitor", stdin=subprocess.DEVNULL, stdout=vlog, stderr=subprocess.STDOUT,
        creationflags=CREATE_NO_WINDOW | BELOW_NORMAL | NEW_GROUP)
    where = wait(lambda: v("pos", timeout=5), 60, 1)
    if not check("the visitor logged in", where, where or "no answer in 60 s (local/access-test/visitor/visitor.log)"):
        raise Stop
    vx, vy, vz = visitor_settled()
    fixture = v(f"fixture {FIXTURE_XY[0]} {FIXTURE_XY[1]} {vz + 0.3:.1f}", timeout=60)
    check("the fixture box is out and its script running", fixture.startswith("fixture"), fixture)
    # the tester's two other friends, online before it logs in (friends online at login)
    for i in range(len(PALS)):
        pal_start(i)
    pals = [wait(lambda i=i: pal_up(i), 60, 1) for i in range(len(PALS))]
    check("(the two friends logged in)", all(pals), "; ".join(str(p) for p in pals))

    # posted keys are judged as the hand's, every other rule of the person's own press applying (the focus and the
    # selected notice moved by our own code or anything else, other threads' sends, held keys; ACCESS_TEST_INPUT=1 lets
    # them all through instead); and no NVDA: on a hidden desktop its controller client claims NVDA runs
    env = dict(os.environ, SOJOURN_TEST_INPUT=os.environ.get("ACCESS_TEST_INPUT", "keys"), SOJOURN_TEST_NO_NVDA="1", SOJOURN_TEST_NO_CLIPBOARD="1",
               SOJOURN_OFFSCREEN_LOSE_AFTER=str(LOSE_AFTER),
               # People's and Objects' first fills held back, as Second Life's objects wait for their names
               SOJOURN_TEST_LIST_DELAY_MS=str(LIST_DELAY_MS),
               SOJOURN_VOICE_OUT_FILE=str(HEARD), SOJOURN_WALK_LOG=str(OUT / "walk.log"),   # world audio to a file (local grid only), never the speakers
               SOJOURN_TEST_UPLOAD_FEE=UPLOAD_FEES)   # uploads of some kinds confirmed as spending on the free local grid
    app_args = ["--grid", GRID_URI, "--user", f"{TESTER[0]} {TESTER[1]}", "--password", PASSWORD,
                "--start", "uri:%s&%d&%d&%d" % (REGION, *TESTER_START), "--port", API_PORT, "--base", OUT / "app",
                "--reader", "on", "--autologin"]
    if RELAY:
        # its sidecar on the relay with that token, kept as the app keeps keys (in the run's own folder)
        import relay as relaymod
        sys.path.insert(0, str(ROOT / "assistant"))
        os.environ["SOJOURN_KEYS"] = str(OUT / "keys")
        import provider as providermod
        providermod.save_key("relay", relaymod.read_secret(relaymod.token_file(RELAY)))
        del os.environ["SOJOURN_KEYS"]
        env["SOJOURN_KEYS"] = str(OUT / "keys")
        (OUT / "app" / "vr-settings.json").write_text(json.dumps({"assistantProvider": "relay"}), encoding="utf-8")
        env["SOJOURN_BUILDERS_POLL"] = "3"   # the builders' tab asks the relay every 3 s, not every minute
        if builders_wanted():
            builders_before_launch(nonce)
    else:
        app_args.append("--no-assistant")
    if os.environ.get("ACCESS_VOICE") != "1":
        app_args.append("--no-voice")   # voice chat is on by default; its keys' step runs with it only when asked
    if NO_RENDER:
        app_args.append("--no-render")   # no renderer out of sight: nothing on the GPU (the renderer step needs it)
    if NO_GPU:
        env["VK_LOADER_DRIVERS_DISABLE"] = "*"   # the Vulkan loader finds no driver: as the tester's old card, a VM
    if SOFTWARE_OK:
        env["SOJOURN_SOFTWARE_VULKAN"] = str(LAVAPIPE)
    if SOFTWARE and not NO_GPU:
        env["SOJOURN_OFFSCREEN_SOFTWARE"] = "1"
    host = [APP_EXE] if APP_AS_EXE else [DOTNET, APP_DLL]
    global APP_PID
    app = APP_PID = procs["app"] = launch_hidden(host + app_args, env=env, cwd=APP_BIN)
    u(f"watch {app}")
    u(f"mark app started, pid {app}")

    # ---- logged in, the window, the focus
    st = wait(lambda: (lambda s: s if s.get("connected") else None)(status()), LOGIN_SECS, 1)
    if not check("logged in", st, json.dumps(st)[:120] if st else f"not connected after {LOGIN_SECS} s"):
        raise Stop
    title = wait(lambda: next((l for l in u(f"windows {app}").splitlines() if '"Sojourn Access' in l), None), 60, 1)
    if not check("the main window, titled Sojourn Access", title, title or u(f"windows {app}")):
        raise Stop
    tester_id = api("LLAgent", "getID")["id"]
    try:
        u(f"activate {app}")   # a fresh process on the hidden desktop may not be its foreground: then no focus events
    except ToolError as e:
        print(f"     (activate: {e})")
    f = wait_focus(lambda d: d["id"] == "chatInput", 15)
    check("the focus lands in the Say box", f, label(f) if f else focus_label())
    run.expect("arriving is announced", ["Logged in"], 0, 10)
    global ARRIVED_AT
    ARRIVED_AT = time.time()

    steps = [
        ("the outfit while it arrives", lambda: outfit_arriving(run, nonce)),   # (first: within two minutes of the login)
        ("friends online at login", lambda: friends_online(run)),
        ("the window's controls", lambda: tree_check(run, "nearby chat")),
        ("Tab order", lambda: tab_order(run)),
        ("nearby chat in", lambda: chat_in(run, nonce)),
        ("nearby chat out", lambda: chat_out(run, nonce)),
        ("her own lines read back", lambda: own_lines(run, nonce)),
        ("lines cut by typing", lambda: typed_cut(run, nonce)),
        ("an IM", lambda: im(run, nonce, tester_id)),
        ("the builders' tab", lambda: builders(run, nonce)),
        ("people and objects", lambda: lists(run)),
        ("people beside chat, friends on Alt+2", lambda: people_beside_chat(run)),
        ("the renderer out of sight", lambda: offscreen(run)),
        ("keys", lambda: keys(run, nonce)),
        ("the assistant's last answer", lambda: assistant_answer(run, nonce)),
        ("the menu bar and the review's new keys", lambda: menu_bar(run, nonce, tester_id)),
        ("settings", lambda: settings(run)),
        ("the quit question", lambda: quit_question(run)),
        ("a script menu", lambda: notice_menu(run, tester_id)),
        ("a dance machine's menu, ignored", lambda: notice_dance(run, tester_id)),
        ("a text box", lambda: notice_text(run, tester_id, nonce)),
        ("a permission question", lambda: notice_perm(run, tester_id)),
        ("a confirmation left waiting", lambda: confirm_left_waiting(run, nonce)),
        ("inventory", lambda: inventory(run, nonce)),
        ("every part", lambda: parts(run)),
        ("your HUDs", lambda: huds(run, nonce)),
        ("a HUD that's one picture of buttons", lambda: hud_picture(run, nonce)),
        ("gestures", lambda: gestures(run, nonce)),
        ("placing by words", lambda: placing(run, nonce)),
        ("uploads", lambda: uploads(run, nonce)),
        ("two inventory panes", lambda: two_panes(run, nonce)),
        ("incognito", lambda: incognito(run, nonce)),
        ("voice keys", lambda: voice_keys(run)),
        ("the window's controls again", lambda: tree_check(run, "nearby chat, after use")),
        ("walking", lambda: walk(run)),
        ("a teleport", lambda: teleport(run)),
        ("the assistant's walk", lambda: pilot_walk(run)),
        ("places and a route", lambda: places(run, nonce)),
        ("search, profiles, pay", lambda: search_and_people(run)),
        ("more commands", lambda: more_commands(run)),
        ("describing", lambda: describing(run)),
        ("describing a look as it loads", lambda: describing_looks(run, nonce)),
        ("review keys and arrivals", lambda: review_and_arrivals(run, nonce)),
        ("sound as a map", lambda: sound_map(run, tester_id)),
        ("someone's own IM sound", lambda: own_im_sound(run, tester_id)),
        ("walk mode", lambda: walk_mode(run)),
        ("distance walked", lambda: distance_walked(run)),
        ("rail switches", lambda: rail_switches(run)),
        ("stopping a vehicle", lambda: vehicle_stop(run)),
        ("recording a route", lambda: route_recording(run)),
        ("recording a route across a border", lambda: route_crossing(run)),
    ]
    only = [w.strip() for w in os.environ.get("ACCESS_STEPS", "").split(",") if w.strip()]
    for name, step in steps:
        if only and not any(w in name for w in only):
            continue
        u(f"mark step: {name}")
        try:
            step()
        except Exception as e:
            check(f"{name} (the step stopped)", False, f"{type(e).__name__}: {e}")
        back_to_chat()


def back_to_chat():
    """Escape anything half-done and Alt+1 back to the Say box, so a failed step doesn't spoil the next."""
    try:
        if (focused() or {}).get("id") != "chatInput":
            key("alt+1")
            wait_focus(lambda d: d["id"] == "chatInput", 3)
    except Exception:
        pass


def say_in_app(text, how="enter"):
    """Type into the Say box as the person would, then Enter (or shift+enter, ctrl+enter)."""
    if (focused() or {}).get("id") != "chatInput":
        key("alt+1")
        wait_focus(lambda d: d["id"] == "chatInput", 3)
    u("set id=chatInput |")
    type_text(text, "id=chatInput")
    wait(lambda: value_of("id=chatInput") == text, 3, 0.2)
    key(how, target="id=chatInput")


# ---- the steps ------------------------------------------------------------------------------------------------------------

APP_PID = 0   # the app under test, for the tree checks


def tree_check(run, when, must=("chatInput", "chatHistory", "chatPeople")):
    """The UIA tree as it is now (only the shown area's controls are in it): names, roles, and the controls that must be there."""
    tree = [parse(l) for l in u(f"tree {APP_PID}").splitlines() if l.strip()]
    unnamed = [d for d in tree if d.get("focusable") and d["type"] in NAMED_TYPES and not d["name"].strip()]
    run.check(f"every focusable control named ({when})", not unnamed,
              f"{sum(1 for d in tree if d.get('focusable'))} focusable" if not unnamed else "unnamed: " + "; ".join(d["raw"][:80] for d in unnamed[:8]))
    wrong = []
    seen = []
    for d in tree:
        want = ROLES.get(d["id"]) or ({"Button"} if re.fullmatch(r"noticeButton\d+", d["id"]) else None)
        if want:
            seen.append(d["id"])
            if d["type"] not in want:
                wrong.append(f'{d["id"]} is {d["type"]}, not {"/".join(sorted(want))}')
    if seen or must:
        run.check(f"roles as expected ({when})", seen and not wrong,
                  ("; ".join(wrong) if wrong else f"checked {', '.join(dict.fromkeys(seen))}") if seen else "none of the contract's controls in the tree")
    for want in must:
        d = next((d for d in tree if d["id"] == want), None)
        run.check(f"{want} in the tree ({when})", d, label(d) if d else "missing")


def tab_order(run):
    start = focused()
    if not run.check("Tab walk starts in the Say box", start and start["id"] == "chatInput", label(start) if start else "nothing focused"):
        return
    stops, stuck = [start], None
    for _ in range(MAX_TABS):
        before = stops[-1]
        key("tab")
        d = wait_focus(lambda d: not same(d, before), 2) or focused()
        if d is None or same(d, before):
            stuck = d or before
            break
        stops.append(d)
        if d["id"] == "chatInput":
            break
    order = " -> ".join(label(d) for d in stops)
    # the blind tester's order (2026-10-31): Say, the history, People nearby, back to Say
    run.check("People nearby is the stop before Say", len(stops) > 2 and (stops[-2]["id"] == "chatPeople" or stops[-2]["type"] == "ListItem"),
              label(stops[-2]) if len(stops) > 2 else "focus didn't move")
    run.check("Tab goes round and back to Say, no dead ends", stuck is None and stops[-1]["id"] == "chatInput",
              order if stuck is None else f"stuck at {label(stuck)}: {order}")
    # the tab strips aren't stops (Alt+number and Control+Tab switch them): Tab and Shift+Tab go between history and Say
    strips = [label(d) for d in stops if d["id"] in ("areas", "conversations") or d["type"] in ("Tab", "TabItem")]
    run.check("no tab strip among the Tab stops", not strips, "; ".join(strips) or order)
    run.check("Tab from Say goes to the history", len(stops) > 1 and stops[1]["id"] == "chatHistory",
              label(stops[1]) if len(stops) > 1 else "focus didn't move")
    run.check("and Tab again to People nearby", len(stops) > 2 and (stops[2]["id"] == "chatPeople" or stops[2]["type"] == "ListItem"),
              label(stops[2]) if len(stops) > 2 else "focus didn't move")
    nameless = [label(d) for d in stops if not d["name"].strip()]
    run.check("every Tab stop has a name", not nameless, "; ".join(nameless) or f"{len(stops) - 1} stops")
    if stops[-1]["id"] == "chatInput" and len(stops) > 2:
        key("shift+tab")
        d = wait_focus(lambda d: d["id"] != "chatInput", 2)
        run.check("Shift+Tab from Say goes back the same way", same(d, stops[-2]), f"{label(d)}, expected {label(stops[-2])}")
        key("tab")
        wait_focus(lambda d: d["id"] == "chatInput", 2)


def chat_in(run, nonce):
    m = mark()
    line = f"Hello from the visitor {nonce}"
    v(f"say {line}")
    run.expect("nearby chat announced, with who said it", [VNAME, line], m, 10)
    got = wait(lambda: line in value_of("id=chatHistory"), 5)
    run.check("and in the Nearby chat history", got, line if got else value_of("id=chatHistory")[-200:])
    f = focused()
    run.check("the focus stays in the Say box", f and f["id"] == "chatInput", label(f) if f else "nothing")


def confirm_left_waiting(run, nonce):
    """A confirmation holds what arrives only while it's new (10 s, AccessWiring.HoldFor): one left waiting doesn't keep
    the reader silent; what waited is read, with a line saying the confirmation still waits. Then cancelled."""
    import threading
    vid = visitor_id()

    # a give waits for her confirmation (a payment from an account with no L$ is refused before asking)
    notes = api("LLInventory", "getBasicFolderID", ft_name="notecard")["id"]
    item = next((k for n, k in inv_items(notes).items() if n), None)
    if not run.check("(a notecard to offer)", item, item or "none in Notecards"):
        return

    def pay():
        try:
            api("TesselAgent", "give", 60, to=vid, item_id=item)
        except Exception:
            pass
    def notices():
        try:
            return selection("id=notices") or ""
        except ToolError:
            return ""   # (the Notices page not showing)
    m0 = mark()
    threading.Thread(target=pay, daemon=True).start()
    if not run.expect("(a give's confirmation said)", ["Give", "Enter confirms"], m0, 8):
        return
    m = mark()
    line = f"while you decide {nonce}"
    v(f"say {line}")
    time.sleep(4)
    early = [e.get("text") for e in said_since(m) if line in (e.get("text") or "")]
    run.check("while the confirmation is new, what arrives waits", not early, "; ".join(early) or "nothing read yet")
    run.expect("left waiting, it's read anyway", [line], m, 15)
    run.expect("with a line saying the confirmation still waits", ["still waiting for your answer"], m, 3)
    key("ctrl+n")
    wait_focus(lambda d: d["id"] == "notices" or d["type"] == "ListItem", 4)
    time.sleep(1.2)
    key("esc")
    gone = wait(lambda: "Give" not in notices(), 6, 0.3)
    run.check("(cancelled)", gone, notices())


def chat_out(run, nonce):
    for how, kind, name, text in (("enter", "Normal", "Enter in Say: the visitor hears it", f"said from Access {nonce}"),
                                  ("shift+enter", "Whisper", "Shift+Enter whispers", f"whispered from Access {nonce}"),
                                  ("ctrl+enter", "Shout", "Ctrl+Enter shouts", f"shouted from Access {nonce}")):
        n = visitor_n()
        say_in_app(text, how)
        run.heard_by_visitor(name, lambda d, t=text, k=kind: d["msg"] == t and d["type"] == k and d["from"] == TNAME, n, 10, text)
    run.check("the Say box is empty after sending", value_of("id=chatInput") == "", repr(value_of("id=chatInput")))

def own_lines(run, nonce):
    """Her own lines read back once the region echoes them, in the words others' get (Alt+0's "My own lines read back")."""
    for how, text, words in (("enter", f"mine said {nonce}", [f"{TNAME}: mine said {nonce}"]),
                             ("shift+enter", f"mine whispered {nonce}", [f"{TNAME} whispers: mine whispered {nonce}"])):
        m = mark()
        say_in_app(text, how)
        run.expect(f"her own line read back ({how})", words, m, 10)
    m = mark()
    api("SojournSettings", "set", access_read_mine=False)
    say_in_app(f"mine unread {nonce}")
    time.sleep(3)
    heard = [e["text"] for e in said_since(m) if f"mine unread {nonce}" in (e.get("text") or "")]
    run.check("with the row off, her own line isn't read back", not heard, "; ".join(heard) or "not read")
    api("SojournSettings", "set", access_read_mine=True)


def typed_cut(run, nonce):
    """A line arriving while she types is cut at her next key by the screen reader (NVDA's speech interrupt for typed
    characters): said again once the keys rest a second, those cut together as one, once; not one heard out before the
    typing, not her own, not after Control alone silenced it; in busy chat with typing all the while, no line more than
    twice and the newest three said again after how many there were."""
    def typing(word, gap=0.15):
        for c in word:
            key(c, target="id=chatInput")
            time.sleep(gap)

    def times(text, since):
        return [e for e in said_since(since) if text in (e.get("text") or "")]

    back_to_chat()
    u("set id=chatInput |")
    # not typing: said once
    m = mark()
    v(f"say calm line {nonce}")
    run.expect("(a line while not typing)", [f"calm line {nonce}"], m, 10)
    time.sleep(2)
    run.check("a line nobody typed over is said once", len(times(f"calm line {nonce}", m)) == 1,
              f"{len(times(f'calm line {nonce}', m))} times")

    # heard out, then typing begins: not said again
    m = mark()
    v(f"say heard first {nonce}")
    run.expect("(a line before typing)", [f"heard first {nonce}"], m, 10)
    time.sleep(2)
    typing("abcdefghij")
    time.sleep(2.5)
    run.check("a line heard out before typing began isn't said again", len(times(f"heard first {nonce}", m)) == 1,
              f"{len(times(f'heard first {nonce}', m))} times")
    u("set id=chatInput |")

    # two lines cut by typing: said again together after the pause (typing on while each arrives, as she would)
    import threading

    class Typist:
        """Keys every 0.15 s from a thread of their own, so reading the event log or the visitor's commands never pause
        them (a pause of a second is what says cut lines again); a line of her own typed and sent when asked."""
        def __init__(self, word="x"):
            self.word, self.keyed, self.mine = word, [], None
            self.stop_, self.thread = threading.Event(), threading.Thread(target=self.run)
            self.thread.start()

        def run(self):
            while not self.stop_.is_set():
                if self.mine:
                    for c in self.mine:
                        key(c, target="id=chatInput")
                        self.keyed.append(time.time())
                    key("enter", target="id=chatInput")
                    self.mine = None
                key(self.word, target="id=chatInput")
                self.keyed.append(time.time())
                time.sleep(0.15)

        def stop(self):
            """The last key's time, in ms."""
            self.stop_.set()
            self.thread.join()
            return self.keyed[-1] * 1000

        def longest(self):
            return max((b - a for a, b in zip(self.keyed, self.keyed[1:])), default=0)

    m = mark()
    typist = Typist()
    try:
        v(f"say cut one {nonce}")
        run.check("(the first line while typing)", wait(lambda: times(f"cut one {nonce}", m), 10), "")
        v(f"say cut two {nonce}")
        run.check("(the second line while typing)", wait(lambda: times(f"cut two {nonce}", m), 10), "")
        time.sleep(0.6)
    finally:
        last_key = typist.stop()
    again = wait(lambda: next((e for e in said_since(m) if f"cut one {nonce}" in (e.get("text") or "")
                               and f"cut two {nonce}" in (e.get("text") or "")), None), 6)
    run.check("lines cut by typing said again together once the keys rest", again, again["text"] if again else
              " | ".join(e.get("text") or "" for e in said_since(m))[-300:])
    if again:
        # (the test's clock for the last key runs a little behind the app's)
        run.check("after a second's pause of the keys, not during them", again["t"] - last_key >= 750, f'{again["t"] - last_key:.0f} ms after the last key')
    time.sleep(2)
    run.check("and only once", len(times(f"cut two {nonce}", m)) == 2, f"{len(times(f'cut two {nonce}', m))} times in all")
    u("set id=chatInput |")

    # Control alone silences on purpose: not said again
    m = mark()
    typing("ij")
    v(f"say hushed line {nonce}")
    run.expect("(a line while typing, then Control alone)", [f"hushed line {nonce}"], m, 10)
    typing("k")
    key("ctrl", target="id=chatInput")
    time.sleep(2.5)
    run.check("a line silenced by Control alone isn't said again", len(times(f"hushed line {nonce}", m)) == 1,
              f"{len(times(f'hushed line {nonce}', m))} times")
    u("set id=chatInput |")

    # her own line, its read-back cut by her typing on: she wrote it, so it isn't said again
    m = mark()
    type_text(f"own words {nonce}", "id=chatInput")
    wait(lambda: value_of("id=chatInput") == f"own words {nonce}", 3, 0.2)
    key("enter", target="id=chatInput")
    end = time.time() + 4
    while time.time() < end:
        typing("x")
    time.sleep(2.5)
    run.check("her own line cut by her typing is read back once, not again", len(times(f"own words {nonce}", m)) == 1,
              f"{len(times(f'own words {nonce}', m))} times")
    u("set id=chatInput |")

    # busy chat: the visitor talking every second or two while she types all the while, sending a line of her own
    # halfway; then a pause
    m = mark()
    lines = [f"busy {i:02d} {nonce}" for i in range(12)]
    mine = f"busymine{nonce}"
    typist = Typist("y")
    try:
        time.sleep(0.5)
        for i, line in enumerate(lines):
            v(f"say {line}")
            if i == len(lines) // 2:
                typist.mine = mine
            time.sleep(1.0 + (i % 3) * 0.4)
        time.sleep(1)
    finally:
        last_key = typist.stop()
    longest = typist.longest()
    time.sleep(4)
    said = said_since(m)
    counts = [sum((e.get("text") or "").count(t) for e in said) for t in lines]
    agains = [e for e in said if "busy" in (e.get("text") or "") and "while you typed" in (e.get("text") or "")]
    run.check("busy chat while typing: every line said, none more than twice", all(1 <= c <= 2 for c in counts),
              f"times each was said: {counts}; the longest pause in the keys {longest:.2f} s")
    run.check("those cut said again in one go after the pause, the newest three after how many",
              len(agains) == 1 and agains[0]["t"] - last_key >= 750 and all(t in agains[0]["text"] for t in lines[-3:])
              and f"{len(lines)} messages while you typed" in agains[0]["text"],
              " | ".join(e["text"] for e in agains) or "none: " + " | ".join(e.get("text") or "" for e in said)[-300:])
    run.check("her own line in busy chat read back once", len(times(mine, m)) == 1, f"{len(times(mine, m))} times")
    u("set id=chatInput |")


def im(run, nonce, tester_id):
    m = mark()
    text = f"an IM for you {nonce}"
    v(f"im {tester_id} {text}")
    run.expect("an IM announced, naming who sent it", [VNAME, text], m, 10)
    f = focused()
    run.check("the focus stays in the Say box", f and f["id"] == "chatInput", label(f) if f else "nothing")
    moved = [e for e in focus_events_since(m) if re.search(re.escape(VNAME) + r"|conv|TabItem|Message to", e.get("text") or "", re.I)]
    run.check("no focus event to the new conversation", not moved, "; ".join(f'{e["src"]}: {e.get("text")}' for e in moved[:4]) or "none")

    key("ctrl+tab")
    d = wait_focus(lambda d: d["id"] == "convInput", 4)
    run.check("Ctrl+Tab moves to the conversation's message box", d and VNAME in d["name"], label(d) if d else focus_label())
    if d:
        tab = wait(lambda: [t for t in tab_items() if VNAME in t], 4)
        run.check(f"a conversation tab for {VNAME}", tab, tab[0] if tab else "no tab item named so; tab items: " + ("; ".join(tab_items()) or "none"))
        tree_check(run, "a conversation", must=("conversations", "convInput", "convHistory"))
        hist = wait(lambda: (lambda h: h if text in h else None)(value_of("id=convHistory")), 4)
        run.check("the conversation shows the IM", hist, text if hist else "not in convHistory")
        n = visitor_n()
        reply = f"a reply {nonce}"
        type_text(reply)
        key("enter")
        got = wait(lambda: next((json.loads(l) for l in v(f"events 20 since {n}").splitlines()
                                 if l.startswith("{") and reply in l and '"im"' in l), None), 10, 0.5)
        run.check("Enter in the message box sends the IM", got, f'{got["dialog"]} from {got["from"]}' if got else "the visitor got nothing")
        key("ctrl+shift+tab")
        back = wait_focus(lambda d: d["id"] == "chatInput", 4)
        run.check("Ctrl+Shift+Tab back to the Say box", back, label(back) if back else focus_label())
        key("ctrl+tab")
        wait_focus(lambda d: d["id"] == "convInput", 4)
        key("ctrl+w")
        time.sleep(1)
        after = focus_label()
        go_conversations()   # shown, so its tabs are in the tree
        time.sleep(1)
        gone = not [t for t in tab_items() if VNAME in t]
        run.check("Ctrl+W closes the conversation", gone, f"then {after}" if gone else "its tab is still there")


BUILDERS_AWAY = None   # the reply posted before the app started (with ACCESS_RELAY), said once after logging in


def builders_wanted():
    only = [w.strip() for w in os.environ.get("ACCESS_STEPS", "").split(",") if w.strip()]
    return not only or any(w in "the builders' tab" for w in only)


def builders_before_launch(nonce):
    """With ACCESS_RELAY: a reply to the token's conversation before the app starts, as if a session answered while it
    was closed (the builders step checks it's said once after logging in)."""
    global BUILDERS_AWAY
    import relay as relaymod
    BUILDERS_AWAY = f"while you were away {nonce}"
    s, body, _ = relaymod.call("POST", f"/relay/admin/messages/{RELAY}", "", {"text": BUILDERS_AWAY, "by": "Claude (test)"},
                               headers={"content-type": "application/json", "authorization": f"Bearer {relaymod.admin_secret()}"})
    if s != 200:
        print(f"     (the builders' reply before the app started: the relay answered {s} {body})")
        BUILDERS_AWAY = None


def builders(run, nonce):
    """Help > Message the builders, Control+Shift+B (docs/access-app.md > Messages to the builders). Without
    ACCESS_RELAY (no token): the tab says how to reach us instead, and Enter says it again, sending nothing. With it, on
    the live relay with that token: a reply posted before the app started said once after logging in; the first
    opening introduces who she's writing to; Enter sends, "Sent."; a reply posted by the admin path while she's in the
    tab read as "Claude (test): ...", without a sound; one while she's in Say read naming the builders, with the IM's
    sound, the focus staying; the reply marked delivered; Control+W closes the tab, the menu opens it again with the
    whole conversation and no introduction; access_builders off refuses the key. Every message the step made is
    deleted after."""
    back_to_chat()
    if not RELAY:
        m = mark()
        key("ctrl+shift+b")
        d = wait_focus(lambda d: d["id"] == "convInput", 4)
        run.check("Control+Shift+B goes to the builders' Message box", d and "builders" in d["name"], label(d) if d else focus_label())
        run.expect("without a token, it says how to reach us instead", ["token", "Tessel", "issues"], m, 6)
        hist = value_of("id=convHistory")
        run.check("and its history says so too", "Tessel" in hist and "issues" in hist, hist[:160])
        m = mark()
        type_text(f"hello builders {nonce}")
        key("enter")
        run.expect("Enter there says so again, nothing sent", ["token", "Tessel"], m, 6)
        run.check("and what she typed is kept in the box", f"hello builders {nonce}" in value_of("id=convInput"), value_of("id=convInput"))
        u("set id=convInput |")
        key("ctrl+w")
        return

    import relay as relaymod
    secret = relaymod.admin_secret()
    js = {"content-type": "application/json", "authorization": f"Bearer {secret}"}

    def reply(text):
        s, body, _ = relaymod.call("POST", f"/relay/admin/messages/{RELAY}", "", {"text": text, "by": "Claude (test)"}, headers=js)
        return s == 200

    def conversation():
        s, body, _ = relaymod.admin_call("GET", f"/relay/admin/messages/{RELAY}", secret)
        return body if s == 200 and isinstance(body, dict) else {}
    try:
        if BUILDERS_AWAY:
            said = [e.get("text") or "" for e in said_since(0) if BUILDERS_AWAY in (e.get("text") or "")]
            run.check("a reply that came while it was closed is said once, after logging in",
                      len(said) == 1 and "While Sojourn Access was closed" in said[0], " | ".join(said) or "not said")
        m = mark()
        key("ctrl+shift+b")
        d = wait_focus(lambda d: d["id"] == "convInput", 4)
        run.check("Control+Shift+B goes to the builders' Message box", d and "builders" in d["name"], label(d) if d else focus_label())
        run.expect("the first time, it says who she's writing to", ["Claude sessions", "not Tessel", "not a person", "90 days"], m, 6)
        hist = value_of("id=convHistory")
        run.check("the tab's history starts with that, then the conversation", hist.startswith("Messages to the builders")
                  and (not BUILDERS_AWAY or BUILDERS_AWAY in hist), hist[:120] + " ... " + hist[-120:])
        tabs = tab_items()
        run.check("its tab is named for the builders", any("builders" in t for t in tabs), "; ".join(tabs))

        text = f"a message to the builders {nonce}"
        m = mark()
        type_text(text)
        key("enter")
        run.expect("Enter sends it: Sent.", ["Sent."], m, 15)
        got = wait(lambda: next((x for x in conversation().get("messages", []) if x["from"] == "tester" and x["text"] == text), None), 10)
        run.check("the relay has her words, as she typed them", got, json.dumps(got) if got else "not on the relay")
        run.check("and the history shows them", text in value_of("id=convHistory"), value_of("id=convHistory")[-160:])

        # a reply while she's in the tab: read as in a conversation in focus, no sound
        here = f"a reply in the tab {nonce}"
        t0, m = time.time(), mark()
        if run.check("(the admin path replies)", reply(here)):
            run.expect("a reply while she's in the tab is read, signed", ["Claude (test):", here], m, 25)
            time.sleep(1)
            run.check("and starts no sound", not cues_since(t0, "EarIm"), json.dumps(cues_since(t0)))
            run.check("and joins the history", here in value_of("id=convHistory"), value_of("id=convHistory")[-160:])

        # one while she's typing in Say: as an IM, naming the builders, the IM's sound, the focus staying
        back_to_chat()
        there = f"a reply elsewhere {nonce}"
        t0, m = time.time(), mark()
        if run.check("(the admin path replies again)", reply(there)):
            run.expect("a reply while she's elsewhere is read as an IM's, naming the builders", ["Claude (test), the builders", there], m, 25)
            c = cue_after(t0, ("EarIm",), 8)
            run.check("with the IM's sound", c, json.dumps(c) if c else f"not played; played: {[x['cue'] for x in cues_since(t0)]}")
            f = focused()
            run.check("the focus stays in the Say box", f and f["id"] == "chatInput", label(f) if f else "nothing")
        conv = wait(lambda: (lambda c: c if c.get("last_reply_delivered") else None)(conversation()), 10)
        run.check("the relay marks the reply delivered", conv, json.dumps({k: v for k, v in conversation().items() if k != "messages"}))

        # closed and opened again by the menu: the whole conversation, no introduction
        key("ctrl+shift+b")
        wait_focus(lambda d: d["id"] == "convInput", 4)
        key("ctrl+w")
        time.sleep(1)
        go_conversations()
        time.sleep(0.5)
        run.check("Control+W closes the builders' tab", not [t for t in tab_items() if "builders" in t], "; ".join(tab_items()))
        back_to_chat()
        m = mark()
        key("alt+h")
        if run.check("Help has Message the builders, with its key", menu_open("Message the builders (Claude, at Studio Quire)")
                     and menu_keys("type=MenuItem;name=Message the builders (Claude, at Studio Quire)").endswith("Control+Shift+B"),
                     menu_keys("type=MenuItem;name=Message the builders (Claude, at Studio Quire)") or focus_label()):
            key("m")
            d = wait_focus(lambda d: d["id"] == "convInput", 4)
            run.check("M there opens the tab again", d and "builders" in d["name"], label(d) if d else focus_label())
            hist = value_of("id=convHistory")
            run.check("with the whole conversation", all(w in hist for w in (text, here, there)), hist[-200:])
            time.sleep(1)
            run.check("and no introduction again", not [e for e in said_since(m) if "not Tessel" in (e.get("text") or "")],
                      " | ".join(e.get("text") or "" for e in said_since(m)))
            key("ctrl+w")
        back_to_chat()

        # turned off
        setting("access_builders", "off")
        m = mark()
        key("ctrl+shift+b")
        run.expect("with access_builders off, the key says it's off and where to turn it on", ["turned off", "Alt+0"], m, 5)
        setting("access_builders", "on")
    finally:
        try:
            setting("access_builders", "on")
        except Exception:
            pass
        made = [x["id"] for x in conversation().get("messages", []) if nonce in x["text"]]
        for i in made:
            relaymod.admin_call("DELETE", f"/relay/admin/messages/{RELAY}/{i}", secret)
        back_to_chat()


def arrived_in_list(run, area, since, is_item, settle=2.5):
    """What a screen reader is left on after arriving in a list (once it and the lists under it have filled): an item
    of that list, as Alt+3 reads its first person. The 2026-10-29 run on Second Life: Alt+7 said "Objects nearby,
    table" and no item until Down, the parts and faces lists under it raising focus events for their own items as they
    filled after it (WinForms does that for a list without the focus)."""
    time.sleep(settle)
    claims = find("type=ListItem;focused=1")
    run.check(f"{area}: only its own item has the keyboard focus", len(claims) == 1 and is_item(claims[0]["name"]),
              "; ".join(label(c) for c in claims) or "no item has it")
    uia = [e for e in focus_events_since(since) if e.get("src") == "uia"]
    last = uia[-1].get("text") or "" if uia else ""
    run.check(f"{area}: the last focus event (UI Automation) is its item", last.startswith("list item") and is_item(last),
              last or "no focus event")
    msaa = [e for e in focus_events_since(since) if e.get("src") == "msaa" and e.get("obj", -4) == -4]
    lastm = msaa[-1] if msaa else {}
    run.check(f"{area}: the last focus event (MSAA) is its item", lastm.get("role") == "list item" and is_item(lastm.get("text") or ""),
              f'{lastm.get("role")}: {lastm.get("text")}' if lastm else "no focus event")


SOFA = "Access Sofa"


def renamed_object(run, made):
    """A self-naming object (a blind tester's sofa renamed itself as people sat and stood, 2026-10-31, and the list
    kept its first name for minutes): the visitor's sofa renames itself on a touch. In the list she's in it stays as
    it was until F5, which shows the new name on the item she's on; while she's elsewhere (its parts) the list follows
    the next rename by itself."""
    ids = re.search(r"id ([0-9a-f-]{36})", made or "")
    if not run.check("(the visitor's self-naming sofa is out)", ids, made):
        return
    if not run.check("(the sofa selected in the list)", select_in("id=objects", SOFA + ","), selected_text("id=objects")):
        return
    n = visitor_n()
    key("apps")
    if menu_open("Touch"):
        key("t")
    run.heard_by_visitor("Touch from the list renames it (its script)", lambda d: d["msg"] == f"{SOFA} renamed to {SOFA} for two", n, 10)
    time.sleep(9)   # past the 5 s after which a scripted thing's name is asked again, and the 3 s refresh
    sel = lambda: selected_text("id=objects")
    run.check("while she's in the list, it doesn't change under her", sel().startswith(SOFA + ","), sel())
    m = mark()
    key("f5")
    run.expect("F5 there says how many", ["objects"], m, 6)
    now = wait(lambda: sel().startswith(SOFA + " for two,") and sel(), 6)
    run.check("F5 shows the new name on the item she's on", now, now or sel())
    left = wait(lambda: last_item_read(m).startswith(SOFA + " for two,") and last_item_read(m), 3)
    run.check("and the screen reader is left on it", left, left or last_item_read(m) or "no item read")
    key("tab")
    d = wait_focus(lambda d: d["id"] == "parts" or d["type"] == "ListItem" and SOFA in d["name"], 4)
    run.check("Tab into its parts, under its new name", d and SOFA + " for two" in d["name"], label(d) if d else focus_label())
    n = visitor_n()
    key("enter")
    run.heard_by_visitor("Enter on its part touches it: renamed back", lambda d: d["msg"] == f"{SOFA} renamed to {SOFA}", n, 10)
    back = wait(lambda: sel().startswith(SOFA + ",") and sel(), 15, 0.5)
    run.check("while she's in its parts, the list above follows the rename", back, back or sel())
    m = mark()
    key("shift+tab")
    got = wait(lambda: last_item_read(m).startswith(SOFA) and last_item_read(m), 4)
    run.check("Shift+Tab back on it, read by the name it has now", got and got.startswith(SOFA + ","), got or last_item_read(m) or "no item read")


def last_item_read(since):
    """The list item UI Automation's last focus event since then was about ("" if none)."""
    uia = [e.get("text") or "" for e in focus_events_since(since) if e.get("src") == "uia"]
    m = re.match(r'list item "((?:[^"\\]|\\.)*)"', uia[-1]) if uia else None
    return json.loads(f'"{m[1]}"') if m else ""


def lists(run):
    x, y, z, _ = tester_where()
    sofa = v(f"sofa {x - 3:.1f} {y - 2:.1f} {z - 0.4:.1f}", timeout=60)
    m = mark()
    key("alt+3")
    d = wait_focus(lambda d: d["id"] == "people" or d["type"] == "ListItem", 4)
    run.check("Alt+3 goes to People nearby", d, label(d) if d else focus_label())
    run.check("People nearby: arriving before it fills (held back 3 s), it says it's looking", d and "looking" in d["name"], label(d) if d else "nothing")
    item = wait(lambda: find(f"type=ListItem;name~={VNAME}"), 15)
    arrived_in_list(run, "People nearby", m, lambda n: VNAME in n)
    tree_check(run, "people nearby", must=("people",))
    if run.check("Access Visitor is in the list", item, label(item[0]) if item else "not listed"):
        name = item[0]["name"]
        x, y, z, heading = tester_where()
        vx, vy, vz = visitor_where()
        dist = math.dist((x, y, z), (vx, vy, vz))
        want, rel = clock(vx - x, vy - y, heading)
        m = re.search(re.escape(VNAME) + r", (?:(\d+) metres?|under a metre), (\w+) o'clock", name)
        run.check("as 'Access Visitor, N metres, X o'clock'", m, name)
        if m:
            said_d = int(m[1]) if m[1] else 0
            run.check("the distance right", abs(said_d - dist) <= 1.5, f"said {said_d} m, measured {dist:.1f} m")
            near = {HOURS[(HOURS.index(want) + k) % 12] for k in (-1, 0, 1)}
            run.check("the clock face right (within an hour)", m[2] in near,
                      f"said {m[2]}, measured {rel:.0f} deg = {want} (heading {heading:.0f}, from {x:.0f},{y:.0f} to {vx:.0f},{vy:.0f})")
    m = mark()
    key("alt+7")
    d = wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 4)
    run.check("Alt+7 goes to Objects nearby", d, label(d) if d else focus_label())
    run.check("Objects nearby: arriving before it fills (held back 3 s), it says it's looking", d and "looking" in d["name"], label(d) if d else "nothing")
    obj = wait(lambda: find("type=ListItem;name~=Access Fixture"), 15)
    run.check("the fixture box is in the objects list", obj, label(obj[0]) if obj else "not listed")
    arrived_in_list(run, "Objects nearby", m, lambda n: "metres" in n or "under a metre" in n)
    tree_check(run, "objects nearby", must=("objects",))
    try:
        renamed_object(run, sofa)
    finally:
        v(f"derez {SOFA}", timeout=15)
    key("alt+1")
    d = wait_focus(lambda d: d["id"] == "chatInput", 4)
    run.check("Alt+1 back to the Say box", d, label(d) if d else focus_label())


def go_conversations():
    """The conversations area by the menu bar (View, Conversations): Alt+2 is Friends, as in Radegast."""
    key("alt+v")
    if menu_open("Conversations"):
        key("o")
    time.sleep(0.5)


def listed(who):
    """The item naming someone in the list in view, or None."""
    got = find(f"type=ListItem;name~={who}")
    return got[0]["name"] if got else None


def people_beside_chat(run):
    """People nearby beside chat, as Radegast keeps its nearby list (a blind tester looked for it there, 2026-10-30;
    since 2026-10-31 after the history in her Tab order, so Shift+Tab from Say): the item read on arriving, with the distance; the actions on the Applications key; Control+D; the
    list unchanged while she's in it and current when she comes back; then Alt+2, Radegast's Friends key."""
    back_to_chat()
    m = mark()
    key("shift+tab")
    d = wait_focus(lambda d: d["id"] == "chatPeople" or d["type"] == "ListItem", 4)
    run.check("Shift+Tab from Say lands in People nearby", d, label(d) if d else focus_label())
    name = wait(lambda: listed(VNAME), 10)
    arrived_in_list(run, "People beside chat", m, lambda n: VNAME in n)

    def distance_right(what, name):
        x, y, z, _ = tester_where()
        dist = math.dist((x, y, z), visitor_where())
        mm = re.search(re.escape(VNAME) + r", (?:(\d+) metres?|under a metre), \w+ o'clock", name or "")
        return run.check(what, mm and abs((int(mm[1]) if mm[1] else 0) - dist) <= 1.5, f"{name}; measured {dist:.1f} m")

    distance_right("the visitor in it, with the distance in metres", name)
    key("apps")
    menu = menu_open("Send IM")
    run.check("the Applications key: the same actions as Alt+3's (Send IM, Walk to, Pay ...)",
              menu and menu_open("Walk to", 1) and menu_open("Pay...", 1), "Send IM shown" if menu else "no menu")
    key("esc")
    d = wait_focus(lambda d: d["id"] == "chatPeople" or (d["type"] == "ListItem" and VNAME in d["name"]), 3)
    run.check("Escape closes it, back on the person", d, label(d) if d else focus_label())
    m = mark()
    key("ctrl+d")
    run.expect("Control+D there is about them", ["Describ", VNAME], m, 10)
    time.sleep(1)

    # the list she's in stays as it is; beside it, it's kept current (every 3 s)
    before = listed(VNAME)
    to = (VISITOR_START[0] + 5, VISITOR_START[1] + 4)
    v(f"goto {to[0]} {to[1]}")
    wait(lambda: math.dist(visitor_where()[:2], to) < 1.5, 15, 0.5)
    time.sleep(4)
    still = listed(VNAME)
    run.check("while she's in it, the list doesn't change under her", still == before, f"{before} -> {still}")
    key("tab")
    d = wait_focus(lambda d: d["id"] == "chatInput", 3)
    run.check("Tab from it back to Say", d, label(d) if d else focus_label())
    time.sleep(4)
    key("shift+tab")
    wait_focus(lambda d: d["id"] == "chatPeople" or d["type"] == "ListItem", 4)
    now = listed(VNAME)
    if distance_right("coming back to it from Say, the distance is current", now):
        run.check("(and it had changed)", now != before, f"{before} -> {now}")
    back_to_chat()
    v(f"goto {VISITOR_START[0]} {VISITOR_START[1]}")

    # Alt+2: Friends, as in Radegast (the tester's habit; conversations are Control+Tab's)
    key("alt+2")
    d = wait_focus(lambda d: d["id"] == "friends" or (d["type"] == "ListItem" and re.search(r", (online|offline)$|^No friends yet", d["name"])), 4)
    run.check("Alt+2 goes to Friends, a friend (or none) read", d, label(d) if d else focus_label())
    tree_check(run, "Alt+2's friends", must=("people", "friends", "groups"))
    back_to_chat()
    key("alt+v")
    shown = menu_open("Friends")
    run.check("View shows Friends with Alt+2", shown and menu_keys("type=MenuItem;name=Friends").endswith("Alt+2"),
              menu_keys("type=MenuItem;name=Friends") if shown else "no menu")
    run.check("and Conversations (Control+Tab goes round them)", menu_open("Conversations", 1), "")
    key("esc")
    key("esc")
    back_to_chat()
    m = mark()
    key("ctrl+tab")
    if wait_focus(lambda d: d["id"] == "convInput", 2):
        print("     (a conversation is open: Control+Tab with none isn't checked)")
        back_to_chat()
    else:
        run.expect("Control+Tab with no conversations says so", ["No conversations open"], m, 4)
    wait(lambda: math.dist(visitor_where()[:2], VISITOR_START[:2]) < 1.5, 15, 0.5)   # back where the steps after expect it


def account_id(first, last):
    import grid
    for line in grid.console(f"show account {first} {last}", wait=1):
        m = re.search(r"ID:\s*([0-9a-f-]{36})", line)
        if m:
            return m[1]
    return None


ARRIVED_AT = 0.0   # when the app said it had logged in (time.time())


def outfit_arriving(run, nonce):
    """The outfit's attachments still coming in (the blind tester's skirt "looked missing, then re-attached itself"
    just after logging in, 2026-10-31): an object linked in Current Outfit but not on the avatar, as on Second Life in
    the first minute or so after logging in or teleporting, is said as still arriving by Control+Shift+W and in the
    inventory's words; once it's on, as attached. Runs first, within the two minutes after logging in."""
    name = f"Access Skirt {nonce}"
    objects = api("LLInventory", "getBasicFolderID", ft_name="object")["id"]
    cof = api("LLInventory", "getBasicFolderID", ft_name="current")["id"]
    x, y, z, _ = tester_where()
    box = api("TesselBuild", "create", 20, shape="box", pos=[x + 2, y - 2, z], scale=[0.3, 0.3, 0.3], name=name)["id"]
    api("TesselBuild", "take", 30, ids=[box], folder_id=objects)
    item = wait(lambda: inv_items(objects).get(name), 10)
    if not run.check("(fixture: an object in the inventory)", item, name):
        return
    try:
        api("TesselInventory", "link", item_id=item, folder_id=cof)
        if not run.check("(it's in the outfit, not attached: linked in Current Outfit)", wait(lambda: name in inv_items(cof), 10),
                         ", ".join(inv_items(cof))):
            return
        back_to_chat()
        m = mark()
        key("ctrl+shift+w")
        said = run.expect("Control+Shift+W says an outfit's attachment not on yet is still arriving", ["Still arriving", name], m, 5)
        run.check("and doesn't call it attached", said and f"Attached: {name}" not in said["text"] and f"{name} on" not in said["text"],
                  said["text"] if said else "")
        key("alt+4")
        wait_focus(lambda d: d["id"] == "inventory", 4)
        sel = inv_search(name, name)
        run.check("the inventory's words say it's still arriving", sel and "still arriving" in sel, sel or selection("id=inventory"))
        # one that never came can be worn again: Enter (Wear, the first action) attaches it
        m = mark()
        key("enter")
        run.expect("Enter on it wears it again", [f"Wearing {name}"], m, 15)
        on = wait(lambda: attached(item), 15)
        if run.check("(then it's attached)", on, ""):
            words = wait(lambda: (lambda s: s if "worn on" in s else None)(selection("id=inventory")), 6)
            run.check("and the item's words follow: worn on its point", words, selection("id=inventory"))
            back_to_chat()
            m = mark()
            key("ctrl+shift+w")
            said = run.expect("Control+Shift+W says it's attached", ["Attached:", name], m, 5)
            # (only this one: an interrupted earlier run can leave its own fixture linked in the outfit, unattached)
            arriving = said["text"].split("Still arriving", 1)[1].split(". ", 1)[0] if said and "Still arriving" in said["text"] else ""
            run.check("and no longer still arriving", said and name not in arriving, said["text"] if said else "")
    finally:
        try:
            api("TesselAgent", "detach", 30, item_id=item)
            time.sleep(1)
            delete_by_search(run, name)   # (deleting waits for her own press: by keys, behind its confirmation)
        except Exception as e:
            print(f"     (the skirt's clean-up: {e})")
        back_to_chat()


def friends_online(run):
    """Friends online at login said as one line (the blind tester heard 23 "is online" lines in the same second,
    2026-10-31): the two PALS (and the visitor, if a friend) online before the app logged in, so the grid told of them
    together; said once, naming them, and one line in the history; none of them on a line of its own. After the
    login's burst (30 s), a friend coming online is said alone as before. The friendships are made here the first time
    (the PALS take any offered), and the burst is checked from the next run."""
    pal_ids = [account_id(*p) for p in PALS]
    names = [" ".join(p) for p in PALS]
    friends = {f["id"]: f for f in api("TesselAgent", "friends").get("friends", [])}
    missing = [i for i, pid in enumerate(pal_ids) if pid and pid not in friends]
    if missing:
        for i in missing:
            api("TesselAgent", "offerFriendship", to=pal_ids[i], message="the friends-online check")
        made = wait(lambda: all(pal_ids[i] in {f["id"] for f in api("TesselAgent", "friends").get("friends", [])} for i in missing), 20)
        run.check("(the friends-online check's friendships made)", made, ", ".join(names[i] for i in missing))
        print("     (friendships made now: the login's one line is checked from the next run)")
        return
    said = said_since(0)
    burst = [e for e in said if "friends online" in (e.get("text") or "")]
    run.check("friends online at login said once, as one line", len(burst) == 1, " | ".join(e.get("text") or "" for e in burst) or "not said")
    if burst:
        text = burst[0]["text"]
        run.check("naming them", all(n in text for n in names) and re.match(r"^\d+ friends online: ", text), text)
    alone = [e["text"] for e in said if any(e.get("text", "").startswith(f"{n} is online") for n in names)]
    run.check("and none of them on a line of its own", not alone, " | ".join(alone) or "none")
    history = value_of("id=chatHistory")
    run.check("the history has the line, every name in it", any("friends online" in l and all(n in l for n in names) for l in history.splitlines()),
              next((l for l in history.splitlines() if "friends online" in l), history[-200:]))
    # after the login's burst: one coming online is its own line again
    wait(lambda: time.time() - ARRIVED_AT > 35, 40, 1)
    m = mark()
    pal_quit(1)
    run.expect("a friend going offline is said", [f"{names[1]} is offline"], m, 15)
    m = mark()
    pal_start(1)
    up = wait(lambda: pal_up(1), 60, 1)
    if run.check("(the friend logged in again)", up, ""):
        got = run.expect("after the login, a friend coming online is said alone, as before", [f"{names[1]} is online"], m, 20)
        run.check("(not as a count)", got and "friends online" not in got["text"], got["text"] if got else "")


def assistant_answer(run, nonce):
    """Control+Shift+L: the assistant's last answer, whole, for the clipboard (the blind tester's copy from Alt+9's
    history took only part of a long one, 2026-10-31). An answer of two paragraphs is put on the panel by the agent API
    (as the sidecar does); Alt+9's history shows each paragraph on its own line (a bare \\n ran them together), and
    Control+Shift+L says it copied every word (the test leaves the clipboard alone, so the count is the check); the
    Read-out menu has it with its key."""
    m = mark()
    key("ctrl+shift+l")
    run.expect("Control+Shift+L with no answer yet says so", ["hasn't answered"], m, 4)
    first = f"The pier is two regions east {nonce}, past the lighthouse."
    second = "Take the coast road and turn left at the market; the boats leave on the hour."
    answer = first + "\n\n" + second
    api("SojournAssistant", "panel", result=answer, working=False)
    key("alt+9")
    wait_focus(lambda d: d["id"] == "assistantInput", 4)
    shown = wait(lambda: (lambda h: h if second in h else None)(value_of("id=assistantHistory")), 6)
    lines = (shown or "").splitlines()
    run.check("Alt+9's history has the answer, its paragraphs on lines of their own",
              any(l.endswith(first) for l in lines) and second in lines, " / ".join(lines[-4:]) or "not shown")
    words = len(answer.split())
    m = mark()
    key("ctrl+shift+l")
    run.expect("Control+Shift+L copies it whole, saying how many words", [f"Copied the assistant's last answer, {words} words"], m, 4)
    back_to_chat()
    key("alt+r")
    if run.check("the Read-out menu has it", menu_open("Copy the assistant's last answer"), focus_label()):
        run.check("with Control+Shift+L beside it", menu_keys("type=MenuItem;name=Copy the assistant's last answer").endswith("Control+Shift+L"),
                  menu_keys("type=MenuItem;name=Copy the assistant's last answer"))
        key("esc")
        key("esc")
    back_to_chat()


def gestures(run, nonce):
    """Gestures (the blind tester couldn't play hers at all, 2026-10-31): two made and activated through the agent API
    (a clap and a line; a greeting whose trigger is replaced by its words), then by keys: a trigger typed in Say plays
    its gesture instead of being said (the region hears the gesture's line from her, the clap plays on her avatar, the
    trigger itself never said), a replacement said in the trigger's place with the rest of the line; Control+Shift+G's
    menu lists them with their triggers and plays one; World > Gestures; in the inventory a gesture's words say active
    and its trigger, its Applications key has Play and Deactivate, and deactivated, its trigger is said as typed."""
    root = api("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
    folder_name = f"Access Gestures {nonce}"
    folder = api("TesselInventory", "createFolder", name=folder_name, parent_id=root)["folder_id"]
    clap, hello = f"Access Clap {nonce}", f"Access Hello {nonce}"
    clap_trigger, hello_trigger = f"/clap{nonce}", f"/hi{nonce}"
    clap_line = f"claps for {nonce}"
    try:
        g1 = api("TesselInventory", "createGesture", 40, name=clap, trigger=clap_trigger, folder_id=folder,
                 steps=[{"animation": "clap"}, {"chat": clap_line}])
        g2 = api("TesselInventory", "createGesture", 40, name=hello, trigger=hello_trigger, replace="Hello there",
                 folder_id=folder, steps=[{"animation": "wave"}])
        if not run.check("(fixtures: two gestures, active)", g1.get("active") and g2.get("active"), f"{g1.get('item_id')} {g2.get('item_id')}"):
            return
        # a trigger typed in Say: the gesture plays, its line said as hers; the trigger itself never said
        n = visitor_n()
        m = mark()
        say_in_app(f"{clap_trigger}")
        run.expect("typing a trigger says the gesture is playing", [f"Playing {clap}"], m, 5)
        run.heard_by_visitor("the gesture's line goes out as her own chat", lambda d: d["from"] == TNAME and d["msg"] == clap_line, n, 10, clap_line)
        anims = wait(lambda: any(a.get("name") == "clap" for a in api("LLAgent", "animations").get("animations", [])), 4, 0.2)
        run.check("and its animation plays on her avatar", anims, json.dumps(api("LLAgent", "animations").get("animations", []))[:200])
        run.expect("her line is read back as her own lines are", [clap_line], m, 8)
        time.sleep(1.5)
        heard = v(f"heard 50 since {n}")
        run.check("the trigger itself isn't said", clap_trigger not in heard, "not heard" if clap_trigger not in heard else heard[-200:])
        # a replacement: the trigger's word replaced, the rest of the line kept
        n = visitor_n()
        say_in_app(f"{hello_trigger} everyone")
        run.heard_by_visitor("a trigger with replacement text is said as those words, the rest kept",
                             lambda d: d["from"] == TNAME and d["msg"] == "Hello there everyone", n, 10, "Hello there everyone")
        # Control+Shift+G: the active gestures as a menu, with their triggers
        back_to_chat()
        key("ctrl+shift+g")
        if run.check("Control+Shift+G opens the gestures menu", menu_open(clap), focus_label()):
            run.check("each gesture with its trigger beside it", menu_keys(f"type=MenuItem;name={clap}").endswith(clap_trigger),
                      menu_keys(f"type=MenuItem;name={clap}"))
            n = visitor_n()
            m = mark()
            # (menu items are pressed by UI Automation's invoke; a gesture is no confirmation, so that's enough)
            u(f"invoke type=MenuItem;name={clap}")
            run.expect("Enter on one plays it, saying so", [f"Playing {clap}"], m, 5)
            run.heard_by_visitor("its line goes out", lambda d: d["from"] == TNAME and d["msg"] == clap_line, n, 10, clap_line)
        back_to_chat()
        key("alt+w")
        if menu_open("Gestures"):
            run.check("World has Gestures, with Control+Shift+G", menu_keys("type=MenuItem;name=Gestures").endswith("Control+Shift+G"),
                      menu_keys("type=MenuItem;name=Gestures"))
            key("esc")
        back_to_chat()
        # the inventory: active and the trigger in its words; Play, Deactivate on the Applications key
        key("alt+4")
        wait_focus(lambda d: d["id"] == "inventory", 4)
        sel = inv_search(clap, clap)
        run.check("in the inventory a gesture's words say active and its trigger", sel and f"gesture, active, {clap_trigger}" in sel,
                  sel or selection("id=inventory"))
        key("apps")
        if run.check("its Applications key has Play and Deactivate", menu_open("Play") and menu_open("Deactivate"), focus_label()):
            m = mark()
            key("v")
            run.expect("Deactivate, said", [f"{clap} is no longer active"], m, 6)
            words = wait(lambda: (lambda s: s if "active" not in s else None)(selection("id=inventory")), 4)
            run.check("and its words follow", words, selection("id=inventory"))
            n = visitor_n()
            say_in_app(clap_trigger)
            run.heard_by_visitor("deactivated, its trigger is said as typed", lambda d: d["from"] == TNAME and d["msg"] == clap_trigger, n, 10, clap_trigger)
            key("alt+4")
            wait_focus(lambda d: d["id"] == "inventory", 4)
            inv_search(clap, clap)
            key("apps")
            if menu_open("Activate"):
                m = mark()
                key("a")
                run.expect("Activate, said with its trigger", [f"{clap} active", clap_trigger], m, 8)
            else:
                run.check("deactivated, its menu has Activate", False, focus_label())
    finally:
        try:
            ids = [g.get("item_id") for g in (locals().get("g1"), locals().get("g2")) if g and g.get("item_id")]
            if ids:
                api("TesselInventory", "deactivateGesture", item_ids=ids)
            delete_by_search(run, folder_name)
        except Exception as e:
            print(f"     (the gestures' clean-up: {e})")
        back_to_chat()


def keys(run, nonce):
    # F1: the keys in a window to read line by line (spoken, Control dropped the rest of some 2,000 characters)
    key("f1")
    d = wait_focus(lambda d: d["id"] == "keysText", 5)
    if run.check("F1 opens the keys in a window, the focus in its text", d, label(d) if d else focus_label()):
        lines = value_of("id=keysText").splitlines()
        run.check("one key a line under headings, all of them", "Alt+1: nearby chat." in lines and "Areas." in lines
                  and "Control+Shift+H: teleport home." in lines and lines[-1:] == ["F1: this list."], f"{len(lines)} lines, last {lines[-1:]}")
        key("esc")
        back = wait_focus(lambda d: d["id"] == "chatInput", 4)
        run.check("Escape closes it, the focus back in Say", back, label(back) if back else focus_label())
    # Control+Backspace deletes the word before the caret (a plain edit box types U+007F for it)
    u("set id=chatInput |")
    type_text("hello brave world", "id=chatInput")
    wait(lambda: value_of("id=chatInput") == "hello brave world", 3, 0.2)
    key("end")
    key("ctrl+back")
    got = wait(lambda: value_of("id=chatInput") == "hello brave ", 3, 0.2)
    run.check("Control+Backspace in Say deletes the word before the caret", got, repr(value_of("id=chatInput")))
    u("set id=chatInput |")
    m = mark()
    v(f"say a line to repeat {nonce}")
    line = run.expect("(a line arrives)", [f"a line to repeat {nonce}"], m, 8)
    if line:
        m = mark()
        key("ctrl+r")
        again = wait(lambda: said_since(m), 5)
        run.check("Ctrl+R repeats the last line that arrived", again and f"a line to repeat {nonce}" in (again[0].get("text") or ""),
                  (again[0].get("text") if again else "nothing said"))
    m = mark()
    key("f6")
    run.expect("F6 mutes the read-out, saying so", ["muted"], m, 5)
    m = mark()
    v(f"say muted line {nonce}")
    time.sleep(4)
    leaked = [e for e in said_since(m) if f"muted line {nonce}" in (e.get("text") or "")]
    run.check("muted: what arrives isn't read", not leaked, "nothing read" if not leaked else leaked[0]["text"])
    m = mark()
    key("f6")
    run.expect("F6 again turns it back on", ["on"], m, 5)
    f = focused()
    run.check("the keys left the focus in the Say box", f and f["id"] == "chatInput", label(f) if f else "nothing")


def settings(run):
    key("alt+0")
    d = wait_focus(lambda d: d["name"] == "Settings" or d["type"] == "ListItem", 4)
    run.check("Alt+0 goes to the Settings list", d, label(d) if d else focus_label())
    if not d:
        return
    tree_check(run, "settings", must=())
    item = wait(lambda: find("type=ListItem;name~=Nearby chat read out"), 4)
    if not run.check("it has 'Nearby chat read out'", item, label(item[0]) if item else "not listed"):
        return
    # each row leads with what differs: no two rows begin with the same three words (first-letter navigation, the review)
    rows = list_items("id=settings")
    starts = {}
    for r in rows:
        starts.setdefault(" ".join(r.split(":")[0].split()[:3]).lower(), []).append(r)
    shared = [v for v in starts.values() if len(v) > 1]
    run.check("no two settings rows start with the same three words", rows and not shared, "; ".join(" / ".join(v) for v in shared[:4]) or f"{len(rows)} rows")
    key("home")
    for _ in range(40):
        if "Nearby chat read out" in selection("id=settings"):
            break
        key("down")
        time.sleep(0.15)
    if not run.check("Down arrow reaches it", "Nearby chat read out" in selection("id=settings"), selection("id=settings")):
        return
    for state in ("off", "on"):
        m = mark()
        key("enter")
        run.expect(f"Enter on it turns it {state}, saying so", [f"Nearby chat read out: {state}."], m, 5)
        now = find("type=ListItem;name~=Nearby chat read out")
        run.check(f"and the item says {state}", now and now[0]["name"].endswith(f": {state}"), label(now[0]) if now else "gone")
    for k, state in (("right", "off"), ("left", "on")):
        m = mark()
        key(k)
        run.expect(f"{k.title()} on it changes it too: {state}", [f"Nearby chat read out: {state}."], m, 5)
    # a safeguard against programs on the PC: a caller may set it back to refuse, never lower it to hello (the person
    # does that in Alt+0, by their own key or Windows Hello)
    try:
        api("SojournSettings", "set", access_answers_without_key="hello")
        refused = None
    except ToolError as e:
        refused = str(e)
    run.check("SojournSettings won't lower access_answers_without_key to hello for a caller", refused and "Alt+0" in refused,
              refused or "it was set to hello")
    back = api("SojournSettings", "set", access_answers_without_key="refuse")
    run.check("and sets it back to refuse", json.dumps(back).count('"refuse"') >= 1, json.dumps(back)[:160])

    # the parcel's music and the world's sounds well under the screen reader by default (the 2026-10-29 run on Second
    # Life: the music drowned NVDA until turned down to 0.15), put in the session's file once
    got = {n: setting(n) for n in ("music_volume", "sounds_volume")}
    run.check("music and world sounds start under speech (0.15, 0.5)", got == {"music_volume": 0.15, "sounds_volume": 0.5}, json.dumps(got))
    try:
        kept = json.loads((OUT / "app" / "vr-settings.json").read_text(encoding="utf-8"))
        marked = json.loads((OUT / "app" / "access-settings.json").read_text(encoding="utf-8")).get("SessionDefaults")
        detail = f'musicVolume {kept.get("musicVolume")}, soundsVolume {kept.get("soundsVolume")}, SessionDefaults {marked}'
    except (OSError, ValueError) as e:
        kept, marked, detail = {}, None, str(e)
    run.check("kept in the session's settings, applied once (later choices are the person's)",
              abs((kept.get("musicVolume") or 0) - 0.15) < 0.001 and marked == 1, detail)
    # stepping a volume: Enter up, Shift+Enter down (down from 0.25 no longer goes by way of 2)
    item = wait(lambda: find("type=ListItem;name~=Music volume"), 4)
    if run.check("Alt+0 has the music's volume", item, label(item[0]) if item else "not listed"):
        key("home")
        for _ in range(60):
            if "Music volume" in selection("id=settings"):
                break
            key("down")
            time.sleep(0.12)
        for how, want in (("enter", "0.25"), ("shift+enter", "0.15"), ("shift+enter", "0"), ("right", "0.15")):
            m = mark()
            key(how)
            run.expect(f"{how.title()} on it: {want}", [f"Music volume: {want}."], m, 5)
        setting("music_volume", 0.15)

    # the answers-without-a-key row in plain words (the blind tester asked what "answers without a physical key:
    # refused" meant, 2026-10-31): who it's for, and to leave it with a keyboard
    row = wait(lambda: find("type=ListItem;name~=Answers from a braille display"), 4)
    run.check("the answers row says who it's for and to leave it with a keyboard",
              row and "dictation or on-screen keyboard: refused" in row[0]["name"] and "leave it if you use a keyboard" in row[0]["name"],
              label(row[0]) if row else "not listed")
    desc = (api("SojournSettings", "get", name="access_answers_without_key").get("settings", {})
            .get("access_answers_without_key", {}).get("desc", ""))
    run.check("and so does its tip (the setting's own words)", "braille display" in desc and "leave it if you use a keyboard" in desc, desc[:160])

    # the sound's device (her Radegast habit of switching headset and speakers, 2026-10-31; on 0.1.0.737 stepping blind
    # put her on her TV's HDMI output, 2026-11-01): Enter on the Alt+0 row opens the list of devices, each said with what
    # it is; Space steps, Shift+Space back; Control+Shift+O the next, with where it stands. (The test's world audio goes
    # to a file whatever is chosen, so the PC's own devices are never opened or played to.)
    row = wait(lambda: find("type=ListItem;name~=Sound output: speakers or headphones"), 4)
    if run.check("Alt+0 has the sound output, the default first", row and ": the default" in row[0]["name"], label(row[0]) if row else "not listed"):
        devices = api("SojournAudio", "devices").get("speakers") or []
        run.check("SojournAudio devices says what each is", devices and all("kind" in d for d in devices),
                  "; ".join(f"{d['name']} ({d.get('kind')})" for d in devices))
        key("home")
        for _ in range(80):
            if "Sound output" in selection("id=settings"):
                break
            key("down")
            time.sleep(0.1)
        first = devices[0]["name"] if devices else None
        if first:
            key("enter")
            item = wait(lambda: find(f"type=MenuItem;name~={first}"), 4)
            run.check("Enter on it: the list of devices, each with what it is",
                      item and (not devices[0].get("kind") or devices[0]["kind"] in item[0]["name"]), label(item[0]) if item else "no list")
            m = mark()
            key("down")
            key("enter")
            run.expect("a device chosen from it, said with where it stands", [f"Sound on {first}", f"2 of {len(devices) + 1}"], m, 6)
            run.check("(chosen)", setting("speakers") == first, str(setting("speakers")))
        m = mark()
        key("shift+space")
        run.expect("Shift+Space back to the default", ["the default", "1 of"], m, 6)
        run.check("(automatic again)", setting("speakers") == "auto", str(setting("speakers")))
        if first:
            back_to_chat()
            m = mark()
            key("ctrl+shift+o")
            run.expect("Control+Shift+O: the sound on the next device, said", [f"Sound on {first}"], m, 6)
            run.check("(chosen)", setting("speakers") == first, str(setting("speakers")))
            setting("speakers", "auto")


def open_newest_notice(run, words):
    key("ctrl+n")
    d = wait_focus(lambda d: d["id"] == "notices" or d["type"] == "ListItem", 4)
    run.check("Ctrl+N goes to the notices list", d, label(d) if d else focus_label())
    sel = wait(lambda: (lambda s: s if all(w in s for w in words) else None)(selection("id=notices")), 4)
    run.check("with the newest selected", sel, sel or f"selected: {selection('id=notices')!r}")
    return bool(d and sel)


def notice_menu(run, tester_id):
    m = mark()
    v(f"fmenu {tester_id}")
    run.expect("a script menu announced", ["Access Menu"], m, NOTICE_SECS)
    f = focused()
    run.check("it doesn't take the focus", f and f["id"] == "chatInput", label(f) if f else "nothing")
    # a screen reader stops speaking at any key: a notice cut off just after it began is said again once the keys rest
    m = mark()
    key("alt+1")
    run.expect("cut off by a key at once, the notice is said again when the keys rest", ["Access Menu"], m, 4)
    if open_newest_notice(run, ["Access Menu"]):
        tree_check(run, "a script menu's notice", must=("notices", "noticeText", "noticeButton1", "noticeIgnore"))
        n = visitor_n()
        key("2")
        run.heard_by_visitor("2 on the list answers Green", lambda d: d["msg"] == "picked Green", n, 10, "picked Green")
    # a menu of twelve (a script's most): after 1 to 9 and 0, minus and equals press the 11th and 12th
    for k, pick in (("minus", "Next"), ("equals", "Back")):
        back_to_chat()
        time.sleep(1)
        m = mark()
        v(f"fmenu12 {tester_id}")
        if run.expect(f"(a menu of twelve announced, for {k})", ["Access Long Menu"], m, NOTICE_SECS) and open_newest_notice(run, ["Access Long Menu"]):
            sel = selection("id=notices")
            run.check("its keys read to the last: 0 Ten, minus Next, equals Back", "0 Ten, minus Next, equals Back" in sel, sel)
            n = visitor_n()
            key(k)
            run.heard_by_visitor(f"{k} answers {pick}", lambda d, p=pick: d["msg"] == f"picked {p}", n, 10, f"picked {pick}")
    # Control alone is the screen reader's own key to stop speech: a notice silenced that way was meant to be, and
    # isn't said again (as one cut off by another key is)
    back_to_chat()
    time.sleep(1)
    m = mark()
    v(f"fmenu {tester_id}")
    if run.expect("(another menu announced)", ["Access Menu"], m, NOTICE_SECS):
        m = mark()
        key("ctrl")
        time.sleep(3)
        again = [e for e in said_since(m) if "Access Menu" in (e.get("text") or "")]
        run.check("silenced by Control alone, the notice isn't said again", not again, again[0]["text"] if again else "not said again")
        if open_newest_notice(run, ["Access Menu"]):
            key("del")
            time.sleep(0.5)
    # cut off by going to answer it (Control+N), answered there, back in chat: not said again (a vehicle's menu was,
    # each time, after its answer)
    back_to_chat()
    time.sleep(1)
    m = mark()
    v(f"fmenu {tester_id}")
    if run.expect("(a menu to answer at once)", ["Access Menu"], m, NOTICE_SECS):
        m = mark()
        # its arrival selected it in the list, a change not hers: a key within InputJudge.Settle (0.7 s) of that is
        # refused ("That changed just as you pressed"), so Control+N waits a little, still well inside the announcement
        time.sleep(0.8)
        if open_newest_notice(run, ["Access Menu"]):
            n = visitor_n()
            key("2")
            run.heard_by_visitor("(answered)", lambda d: d["msg"] == "picked Green", n, 10, "picked Green")
            back_to_chat()
            time.sleep(2.5)
            again = [e for e in said_since(m) if "Access Menu" in (e.get("text") or "")]
            run.check("a notice answered in Notices isn't said again afterwards", not again,
                      again[0]["text"] if again else "not said again")


def notice_dance(run, tester_id):
    """A dance machine's menu (the blind tester's, 2026-10-31: it stayed in Notices after she answered, and she found no
    way to close it): answered by 1, the object hears it and sends its menu again (as dance machines do), which is
    picked by itself; its keys end with Delete Ignore, and the Applications menu has Ignore (Delete); Delete ignores it
    (the official viewer's Ignore: the object hears nothing), said with what's left, the focus on the next notice in
    the list, then on none."""
    back_to_chat()
    time.sleep(1)
    # (another object's menu would do; the fixture's own next menu replaces its last, as in the viewer)
    m = mark()
    v(f"offer {tester_id}")
    run.expect("(an offer to leave waiting under the dance machine's menu)", ["Access Card"], m, NOTICE_SECS)
    time.sleep(1)
    m = mark()
    v(f"fdance {tester_id}")
    if not run.expect("a dance machine's menu announced", ["Access Dance"], m, NOTICE_SECS) or not open_newest_notice(run, ["Access Dance"]):
        return
    sel = selection("id=notices")
    run.check("its keys end with Delete Ignore", "1 Waltz, 2 Tango, 3 Block, Delete Ignore." in sel, sel)
    run.check("the Ignore button says its key", find("type=Button;name=Delete: Ignore"), json.dumps([d["name"] for d in find("type=Button")]))
    n = visitor_n()
    first = notice_ids("Access Dance")
    key("1")
    run.heard_by_visitor("1 answers Waltz", lambda d: d["msg"] == "danced Waltz", n, 10, "danced Waltz")
    again = wait(lambda: (lambda s, ids: s if "Access Dance" in s and len(ids) == 1 and ids[0] not in first else None)(
        selection("id=notices"), notice_ids("Access Dance")), NOTICE_SECS)
    run.check("the answered menu left the list, and the one sent again is picked by itself", again,
              f"{again or selection('id=notices')} (dance menus waiting: {notice_ids('Access Dance')}, first {first})")
    key("apps")
    if run.check("its Applications menu has Ignore", menu_open("Ignore"), focus_label()):
        run.check("with Delete beside it", menu_keys("type=MenuItem;name=Ignore").endswith("Delete"), menu_keys("type=MenuItem;name=Ignore"))
        key("esc")
        time.sleep(0.5)
    n = visitor_n()
    m = mark()
    key("del")
    got = run.expect("Delete ignores it, saying so and what's left", ["Ignored Access Fixture's menu.", "waiting."], m, 5)
    time.sleep(1)
    sel = selection("id=notices")
    f = focused()
    run.check("gone from the list, the focus on it still, on the next notice (the offer left under it)",
              not notice_ids("Access Dance") and "Access Card" in sel and f and (f["id"] == "notices" or f["type"] == "ListItem"),
              f"{sel}; focus {label(f) if f else 'none'}")
    time.sleep(2)
    heard = [l for l in v(f"heard 20 since {n}").splitlines() if "danced" in l]
    run.check("the object hears nothing of an ignored menu", not heard, heard[0] if heard else "nothing")
    # the rest ignored too, until none is left
    m = mark()
    for _ in range(8):
        if "No notices waiting" in selection("id=notices"):
            break
        key("del")
        time.sleep(0.7)
    last = [e.get("text") or "" for e in said_since(m) if (e.get("text") or "").startswith("Ignored")]
    run.check("ignoring the last says none is left", last and last[-1].endswith("No notices waiting."), last[-1] if last else "nothing said")
    run.check("and the list says so", "No notices waiting" in selection("id=notices"), selection("id=notices"))


def history_tail(chars=2000):
    """The end of nearby chat's history (its own lines and the app's: answers, arrivals, crossings)."""
    return value_of("id=chatHistory")[-chars:]


def notice_ids(words=""):
    """The ids of the script menus waiting (the agent API's LLNotifications), newest first; with words, only those
    whose message has them."""
    got = api("LLNotifications", "listChannelNotifications", channel="Visible").get("notifications") or []
    return [n["id"] for n in reversed(got) if n.get("name", "").startswith("ScriptDialog") and words in (n.get("message") or "")]


def quit_question(run):
    """Alt+F4 in world asks first (it's next to Alt+4): Escape stays, the focus back where it was."""
    key("alt+f4")
    d = wait_focus(lambda d: d["id"] == "quitYes", 4)
    if not run.check("Alt+F4 in world asks first, the focus on Quit", d, label(d) if d else focus_label()):
        return
    m = mark()
    key("esc")
    run.expect("Escape stays, saying so", ["Still here"], m, 4)
    back = wait_focus(lambda d: d["id"] == "chatInput", 3)
    run.check("and the focus is back in Say", back, label(back) if back else focus_label())


def notice_text(run, tester_id, nonce):
    m = mark()
    v(f"ftext {tester_id}")
    run.expect("a text box announced", ["Access Box"], m, NOTICE_SECS)
    if not open_newest_notice(run, ["Access Box"]):
        return
    tree_check(run, "a text box's notice", must=("notices", "noticeText", "noticeReply", "noticeIgnore"))
    key("enter")
    reply = wait_focus(lambda d: d["id"] == "noticeReply", 3)
    run.check("Enter on a text box's notice goes to the Reply box", reply, label(reply) if reply else focus_label())
    if not reply:
        for _ in range(8):
            key("tab")
            time.sleep(0.3)
            d = focused()
            if d and d["id"] == "noticeReply":
                reply = d
                break
        run.check("Tab from the list reaches the Reply box", reply, label(reply) if reply else focus_label())
    if reply:
        word = f"lantern{nonce}"
        n = visitor_n()
        type_text(word)
        key("enter")
        run.heard_by_visitor("typed and Enter answers the text box", lambda d: d["msg"] == f"typed {word}", n, 10, f"typed {word}")


def notice_perm(run, tester_id):
    m = mark()
    v(f"fperm {tester_id}")
    run.expect("a permission question announced", ["Access Fixture"], m, NOTICE_SECS)
    if not open_newest_notice(run, ["Access Fixture"]):
        return
    buttons = [d for d in find("type=Button") if d["id"].startswith("noticeButton")]
    yes = next((d for d in buttons if re.search(r"\b(yes|allow|grant|accept|ok)\b", d["name"], re.I)), None)
    if not run.check("the question has a button to allow it", yes, "; ".join(d["name"] for d in buttons) or "no notice buttons"):
        return
    number = re.match(r"\s*(\d)", yes["name"])
    number = number[1] if number else yes["id"].replace("noticeButton", "")
    n = visitor_n()
    key(number)
    run.heard_by_visitor(f"{number} on the list allows it", lambda d: d["msg"] == "permission 16", n, 10, "permission 16")


def inv_search(words, expect_first):
    """Control+F, the words typed, Enter: the focus back in the tree with the first match selected."""
    key("ctrl+f")
    wait_focus(lambda d: d["id"] == "inventorySearch", 3)
    for attempt in range(3):   # just after the area comes back (a confirmation answered), UIA may not have the box yet
        try:
            u("set id=inventorySearch |")
            break
        except ToolError:
            time.sleep(0.5)
    type_text(words, "id=inventorySearch")
    wait(lambda: value_of("id=inventorySearch") == words, 3, 0.2)
    # (UI Automation can give up on the box for a while: finding it by id walks the whole window, the tree's items
    # included, and WinForms then disconnects every item's provider one by one, a few ms each, when the tree is
    # emptied for the list. That was the seconds-long hold seen here, not the search's walk, which is 10 ms for 60,000
    # items: tools/invbench.py, 2026-10-30)
    for attempt in range(4):
        try:
            key("enter", target="id=inventorySearch")
            break
        except ToolError:
            time.sleep(2)
    wait_focus(lambda d: d["id"] == "inventory", 3)
    return wait(lambda: (lambda s: s if expect_first in s else None)(selection("id=inventory")), 5)


def inv_items(folder):
    """{name: id} of what's in an inventory folder (the agent API)."""
    r = api("LLInventory", "getDirectDescendants", folder_id=folder)
    out = {}
    for kind in ("items", "categories"):
        for k, val in (r.get(kind) or {}).items():
            out[val.get("name")] = k
    return out


def attached(item):
    return any(a.get("item_id") == item for a in api("LLAgent", "getAttachedObjectsList").get("result", []))


def confirm_in_notices(run, what, m, answer="enter"):
    """A confirmation the person's own action asked for: selected in Notices with the focus there, said, then Enter
    (or Escape)."""
    conf = wait_focus(lambda d: d["id"] == "notices" or d["type"] == "ListItem", 6)
    run.check(f"{what}: the confirmation comes up selected in Notices", conf and what in selection("id=notices"),
              selection("id=notices") if conf else focus_label())
    run.expect(f"{what}: and is said", [what], m, 4)
    time.sleep(1.2)   # it arms 0.8 s after it's shown
    key(answer)


def told_since(m):
    """The last item an MSAA screen reader was told has the focus since m (NVDA reads WinForms' tree views this way:
    the control's focus WinEvent, resolved by AccessibleObjectFromEvent)."""
    got = [e for e in events()[m:] if e.get("src") == "msaa" and e.get("kind") == "focus" and e.get("role") == "outline item"]
    return got[-1].get("text") if got else None


def reader_follows(run, folder_name):
    """Arrows through the tree as first shown, again after a reload (F5 fills it again), and inside a folder: at each
    move what a screen reader is told is the item the highlight is on. The tree view numbers its items in the order
    they went in, children and placeholders too, and WinForms' own MSAA object took a number for a top-level position
    when it was within the top level's count (as on a tree's first fill; refilled, the numbers run past it and fell
    through to Windows' object, right): NVDA spoke the folder at twice the highlight's place, "not selected" (the guided
    test of 2026-10-29)."""
    wrong, moves = [], []

    def move(k, where):
        m = mark()
        key(k)
        sel = wait(lambda: selected_text("id=inventory") or None, 2)
        told = wait(lambda: (lambda t: t if t and t == sel else None)(told_since(m)), 2) or told_since(m)
        moves.append(f"{where} {k}: {sel!r} told {told!r}")
        if told != sel:
            wrong.append(moves[-1])

    def walk(what, where, *keys):
        wrong.clear()
        moves.clear()
        for k in keys:
            move(k, where)
        run.check(what, not wrong, "; ".join(wrong or moves))

    key("home")
    time.sleep(0.5)
    walk("arrowing the top level as first shown, a screen reader is told the highlighted folder", "top", *["down"] * 4)
    key("f5")
    time.sleep(0.8)
    key("home")
    time.sleep(0.5)
    walk("and after a reload", "reloaded", *["down"] * 4)
    if not select_in("id=inventory", folder_name):
        run.check("(the run's folder at the top of the tree)", False, selected_text("id=inventory"))
        return
    key("right")
    time.sleep(0.8)
    walk("and inside a folder, the highlighted item", "inside", "down", "down")
    key("left")
    key("left")


def inventory(run, nonce):
    # fixtures through the agent API, as any program would make them: a folder for this run with two notecards and a
    # folder in it, and a crate (a box with a notecard inside) taken into it
    root = api("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
    test_name, moved_name, crate_name = f"Access Test {nonce}", f"Access Moved {nonce}", f"Access Crate {nonce}"
    note1, note2 = f"Access Note {nonce} one", f"Access Note {nonce} two"
    folder = api("TesselInventory", "createFolder", name=test_name, parent_id=root)["folder_id"]
    moved = api("TesselInventory", "createFolder", name=moved_name, parent_id=folder)["folder_id"]
    n1 = api("TesselInventory", "createNotecard", name=note1, text="first line", folder_id=folder)["item_id"]
    api("TesselInventory", "createNotecard", name=note2, text="second note", folder_id=folder)
    x, y, z, _ = tester_where()
    crate = api("TesselBuild", "create", 20, shape="box", pos=[x - 2, y + 2, z], scale=[0.4, 0.4, 0.4], name=crate_name)["id"]
    api("TesselBuild", "addToContents", 20, id=crate, item_id=n1)
    api("TesselBuild", "take", 30, ids=[crate], folder_id=folder)
    have = wait(lambda: (lambda i: i if crate_name in i else None)(inv_items(folder)), 10)
    if not run.check("(fixtures: a folder, notecards, a crate in it)", have, ", ".join(have or {}) or "the crate never arrived"):
        return

    key("alt+4")
    d = wait_focus(lambda d: d["id"] == "inventory", 4)
    run.check("Alt+4 goes to the inventory tree", d, label(d) if d else focus_label())
    tree_check(run, "inventory", must=("inventory", "inventorySearch", "inventoryShow"))
    reader_follows(run, test_name)

    # a search, and a notecard opened, edited and saved in a real edit control
    m = mark()
    sel = inv_search(note1, note1)
    run.check("a search lists it, with its type and folder", sel and "notecard" in sel and test_name in sel,
              sel or f"selected: {selection('id=inventory')!r}")
    run.expect("and says how many", ["1 found"], m, 4)
    key("enter")
    ed = wait_focus(lambda d: d["id"] == "editorText", 6)
    if run.check("Enter opens the notecard in an edit control", ed, label(ed) if ed else focus_label()):
        run.check("with its text", "first line" in value_of("id=editorText"), value_of("id=editorText")[:80])
        key("ctrl+end")
        type_text(" and more")
        m = mark()
        key("ctrl+s")
        run.expect("Control+S saves it, saying so", ["Saved."], m, 10)
        text = api("TesselInventory", "readNotecard", item_id=n1).get("text", "")
        run.check("the notecard has the new words", "first line and more" in text, repr(text[:80]))
        key("esc")
        back = wait_focus(lambda d: d["id"] == "inventory", 4)
        run.check("Escape closes it, back in the tree", back, label(back) if back else focus_label())

    # two marked with Space, cut, pasted into a folder
    inv_search(f"Access Note {nonce}", note1)
    m = mark()
    key("space")
    key("down")
    key("space")
    run.expect("Space marks them, counting", ["2 marked"], m, 4)
    m = mark()
    key("ctrl+x")
    run.expect("Control+X cuts them", ["Cut 2 items"], m, 4)
    inv_search(moved_name, moved_name)
    m = mark()
    key("ctrl+v")
    run.expect("Control+V on a folder moves them there", [f"Moved 2 items into {moved_name}"], m, 10)
    inside = wait(lambda: (lambda i: i if note1 in i and note2 in i else None)(inv_items(moved)), 6)
    run.check("both are in that folder now", inside, ", ".join(inside or inv_items(moved)) or "empty")

    # copied and pasted
    inv_search(note2, note2)
    key("ctrl+c")
    inv_search(test_name, test_name)
    m = mark()
    key("ctrl+v")
    run.expect("Control+C then Control+V copies it", [f"Copied it into {test_name}"], m, 10)
    run.check("the copy is there", wait(lambda: note2 in inv_items(folder), 6), ", ".join(inv_items(folder)))

    # wear and take off, the Worn filter
    inv_search(crate_name, crate_name)
    m = mark()
    key("enter")
    run.expect("Enter on an object wears it", [f"Wearing {crate_name}"], m, 10)
    crate_item = inv_items(folder).get(crate_name)
    run.check("it's attached", wait(lambda: attached(crate_item), 8), "")
    key("ctrl+f")
    wait_focus(lambda d: d["id"] == "inventorySearch", 3)
    key("tab")
    if run.check("Tab from the search reaches Show", wait_focus(lambda d: d["id"] == "inventoryShow", 3), focus_label()):
        key("down")
        listed = wait(lambda: (lambda f: f if f and "worn on" in f[0]["name"] else None)(find(f"type=TreeItem;name~={crate_name}")), 6)
        run.check("Show: Worn lists it, worn and where", listed, label(listed[0]) if listed else
                  "; ".join(label(t) for t in find("type=TreeItem")[:5]) or "nothing listed")
        # a worn body part isn't deleted (the official viewer and Firestorm: replace it first), and nobody is asked
        part = next((t["name"] for t in find("type=TreeItem") if re.search(r", (shape|skin|hair|eyes), worn", t["name"])), None)
        # (an account whose body parts the grid never linked into its Current Outfit has none the inventory knows worn)
        if not part:
            print("     (skipped: Show: Worn lists no body part for this account)")
        else:
            u("setfocus id=inventory")
            wait_focus(lambda d: d["id"] == "inventory", 3)
            if run.check("(the body part selected)", select_in("id=inventory", part.split(",")[0]), selected_text("id=inventory")):
                m = mark()
                key("del")
                run.expect("Delete on a worn body part says why", ["body part you're wearing", "nothing was deleted"], m, 6)
                asked = [e.get("text") for e in said_since(m) if "Enter confirms" in (e.get("text") or "")]
                run.check("without asking to confirm, the focus staying in the tree", not asked and (focused() or {}).get("id") == "inventory",
                          "; ".join(asked) or focus_label())
            key("ctrl+f")
            wait_focus(lambda d: d["id"] == "inventorySearch", 3)
            key("tab")
            wait_focus(lambda d: d["id"] == "inventoryShow", 3)
        key("up")
    inv_search(crate_name, crate_name)
    m = mark()
    key("enter")
    run.expect("Enter on it, worn, takes it off", [f"Took off {crate_name}"], m, 10)
    run.check("it's off", wait(lambda: not attached(crate_item), 8), "")

    # rez on the ground, from the Applications key's menu
    time.sleep(1)
    inv_search(crate_name, crate_name)
    m = mark()
    key("apps")
    time.sleep(0.6)
    key("r")
    if run.expect("Applications key, R: rezzed on the ground", [f"Rezzed {crate_name}", "on the ground"], m, 15):
        objs = wait(lambda: api("TesselObjects", "nearby", dist=10, query=crate_name).get("objects"), 6)
        if run.check("it's out in the world", objs, f"{len(objs or [])} found; near: " +
                     ", ".join(f'{o.get("name")!r} {o.get("dist")} {o.get("pos")}' for o in api("TesselObjects", "nearby", dist=96).get("objects", []))
                     + f"; me {tester_where()}"):
            o = objs[0]
            ground = api("TesselAgent", "ground", points=[o["pos"][:2]])["heights"][0]
            bottom = o["pos"][2] - (o.get("scale") or [0, 0, 0.4])[2] / 2
            run.check("resting on the ground", ground is not None and abs(bottom - ground) < 0.15, f"bottom {bottom:.2f}, ground {ground}")
            api("TesselBuild", "take", 30, ids=[o["id"]], folder_id=folder)

    # unboxing: the box rezzed, its contents copied into a new folder, the box put away
    time.sleep(1)
    inv_search(crate_name, crate_name)
    m = mark()
    key("apps")
    time.sleep(0.6)
    key("u")
    run.expect("Applications key, U: unboxed into a folder", [f"Unboxed {crate_name}", "1 of 1"], m, 30)
    box_folder = wait(lambda: inv_items(root).get(crate_name), 4)
    run.check("the folder has what was inside", box_folder and wait(lambda: note1 in inv_items(box_folder), 4),
              ", ".join(inv_items(box_folder)) if box_folder else "no folder named for the crate at the top")
    left = wait(lambda: not api("TesselObjects", "nearby", dist=10, query=crate_name).get("objects"), 5)
    run.check("and the box is put away", left, "gone" if left else "still out")

    # giving, behind the person's confirmation
    inv_search(note2, note2)
    n = visitor_n()
    m = mark()
    key("apps")
    time.sleep(0.6)
    key("g")
    pick = wait_focus(lambda d: d["id"] in ("pickList", "pickName"), 5)
    if run.check("Give to... opens a picker", pick, label(pick) if pick else focus_label()):
        listed = wait(lambda: VNAME in selection("id=pickList"), 4)
        run.check("Access Visitor first in it (nearby)", listed, selection("id=pickList"))
        key("enter", target="id=pickList")
        confirm_in_notices(run, "Give", m)
        got = wait(lambda: next((json.loads(l) for l in v(f"events 20 since {n}").splitlines()
                                 if l.startswith("{") and '"InventoryOffered"' in l and note2 in l), None), 12, 0.5)
        run.check("Enter confirms: the visitor is offered it", got, f'{got["dialog"]}: {got["msg"]}' if got else "no offer arrived")
        back = wait_focus(lambda d: d["id"] == "inventory", 4)
        run.check("and the focus is back in the tree", back, label(back) if back else focus_label())

        # the other way round, Radegast's (the blind tester's habit, 2026-10-31): copy it, then give it from the person
        key("ctrl+c")
        time.sleep(0.5)
        key("alt+3")
        # it says "looking" until it fills, then lands on the nearest
        on = wait(lambda: next((c for c in find("type=ListItem;focused=1") if VNAME in c["name"]), None), 15, 0.5)
        if run.check("Alt+3 lands on the visitor", on, label(on) if on else focus_label()):
            n = visitor_n()
            m = mark()
            key("apps")
            time.sleep(0.6)
            key("g")
            # (the people list is a ListItem too: wait for the confirmation's own item before answering it)
            wait_focus(lambda d: d["id"] == "notices" or (d["type"] == "ListItem" and "Give" in d["name"]), 6)
            confirm_in_notices(run, "Give", m)
            got = wait(lambda: next((json.loads(l) for l in v(f"events 20 since {n}").splitlines()
                                     if l.startswith("{") and '"InventoryOffered"' in l and note2 in l), None), 12, 0.5)
            run.check("copied, then Give what you copied on the person: the visitor is offered it", got,
                      f'{got["dialog"]}: {got["msg"]}' if got else "no offer arrived")
        key("alt+4")
        wait_focus(lambda d: d["id"] == "inventory", 4)

    # deleting, behind the person's confirmation: the run's folders go to the Trash (the first cancelled once)
    for name, fid in ((test_name, folder), (crate_name, box_folder)):
        if not fid:
            continue
        inv_search(name, name)
        if name == test_name:
            m = mark()
            key("del")
            confirm_in_notices(run, "Trash", m, answer="esc")
            run.expect("Escape on the confirmation cancels", ["Cancelled"], m, 6)
            back = wait_focus(lambda d: d["id"] == "inventory", 4)
            run.check("the focus back in the tree", back, label(back) if back else focus_label())
            time.sleep(1)
            extra = [e.get("text") for e in said_since(m) if "Not deleted" in (e.get("text") or "")]
            run.check("and only that is said (not the op's refusal)", not extra, "; ".join(extra) or "Cancelled.")
            run.check(f"{name} still there", name in inv_items(root), "")
            # with something in it worn: the confirmation says so, and it's taken off first (as the official viewer does)
            worn_crate = inv_items(folder).get(crate_name)
            if worn_crate:
                if not attached(worn_crate):
                    api("TesselAgent", "attach", 20, item_id=worn_crate, replace=False)
                run.check("(the crate worn again)", wait(lambda: attached(worn_crate), 8), "")
        m = mark()
        key("del")
        confirm_in_notices(run, "Trash", m)
        if name == test_name and worn_crate:
            run.expect("the confirmation says what's worn is taken off first", [f"Worn: {crate_name}", "Taken off first"], m, 2)
        run.expect(f"Enter confirms: {name} deleted", [f"Deleted {name}"], m, 10)
        if name == test_name and worn_crate:
            run.check("and what was worn in it is taken off", wait(lambda: not attached(worn_crate), 8), "still attached")
        run.check(f"{name} gone from the top of the inventory", wait(lambda: name not in inv_items(root), 6),
                  ", ".join(k for k in inv_items(root) if "Access" in (k or "")))
    try:
        key("alt+4")
        key("ctrl+f")
        key("esc", target="id=inventorySearch")
    except ToolError:
        pass


def selected_text(sel):
    """What's selected in a list or tree: its Selection pattern's word, else the tree item marked selected (a TreeView
    without check boxes answers its Selection pattern with nothing)."""
    got = selection(sel)
    if got:
        return got
    return next((t["name"] for t in find("type=TreeItem") if " selected" in t["raw"]), "")


def select_in(sel, words, tries=60):
    """Home, then Down until the control's selection has the words (a tree or list the focus is in)."""
    key("home")
    for _ in range(tries):
        if words in selected_text(sel):
            return True
        key("down")
        time.sleep(0.12)
    return words in selected_text(sel)


def delete_by_search(run, name):
    """The run's own fixture deleted through the inventory, by keys, behind the confirmation."""
    key("alt+4")
    wait_focus(lambda d: d["id"] == "inventory", 4)
    inv_search(name, name)
    m = mark()
    key("del")
    if wait_focus(lambda d: d["id"] == "notices" or d["type"] == "ListItem", 6):
        time.sleep(1.2)
        key("enter")
    run.expect(f"(cleanup: {name} deleted)", [f"Deleted {name}"], m, 10)
    clear_search()


def clear_search():
    """The inventory's first pane back to its tree: Control+F, Escape (clears the search). A step that searched leaves it
    so: a search left showing changes what later steps meet (an upload isn't selected over one, by design)."""
    try:
        key("alt+4")
        wait_focus(lambda d: d["id"] == "inventory", 4)
        key("ctrl+f")
        key("esc", target="id=inventorySearch")
    except ToolError:
        pass


def places(run, nonce):
    mark_name, route_name = f"Access Mark {nonce}", f"Access Route {nonce}"
    lm_folder = api("LLInventory", "getBasicFolderID", ft_name="landmark")["id"]
    x0, y0, z0, _ = tester_where()
    api("TesselInventory", "createLandmark", 20, name=mark_name, folder_id=lm_folder)
    api("TesselInventory", "createNotecard", name=route_name, folder_id=lm_folder,
        text=f"Walk to the post\n//go xyz {ROUTE[0][0]},{ROUTE[0][1]}\n//go xyz {ROUTE[1][0]},{ROUTE[1][1]}\n")

    # a region by name, typed
    key("alt+6")
    d = wait_focus(lambda d: d["id"] == "landmarks", 4)
    run.check("Alt+6 goes to the landmarks", d, label(d) if d else focus_label())
    tree_check(run, "places", must=("landmarks", "history", "goRegion", "regions"))
    key("tab")
    key("tab")
    if run.check("Tab twice reaches Go to region", wait_focus(lambda d: d["id"] == "goRegion", 3), focus_label()):
        m = mark()
        type_text(f"{REGION} {PLACE_TO[0]} {PLACE_TO[1]} {PLACE_TO[2]}", "id=goRegion")
        key("enter", target="id=goRegion")
        run.expect("a region's name and Enter: arrived, said", ["Arrived in", REGION], m, 40)
        x, y, z, _ = tester_where()
        run.check("and there", math.dist((x, y), PLACE_TO[:2]) < 3, f"at {x:.0f}, {y:.0f}")

    # a landmark by Enter
    key("alt+6")
    wait_focus(lambda d: d["id"] == "landmarks", 4)
    if run.check("the landmark is in the tree", select_in("id=landmarks", mark_name),
                 selected_text("id=landmarks") or focus_label()):
        m = mark()
        key("enter")
        run.expect("Enter on it teleports, saying where", ["Arrived in", REGION], m, 40)
        x, y, z, _ = tester_where()
        run.check("back where it was made", math.dist((x, y), (x0, y0)) < 3, f"at {x:.0f}, {y:.0f}; made at {x0:.0f}, {y0:.0f}")
        # the history records an arrival once the parcel's name has come (up to 6 s)
        wait(lambda: math.dist(api("SojournPeople", "teleportHistory", count=1)["places"][0]["pos"][:2], (x0, y0)) < 3, 8, 0.5)

    # the history: the place before, by Enter
    key("alt+6")
    wait_focus(lambda d: d["id"] == "landmarks", 4)
    key("f5")
    time.sleep(1.5)
    key("tab")
    if run.check("Tab reaches the teleport history", wait_focus(lambda d: d["id"] == "history" or d["type"] == "ListItem", 3), focus_label()):
        first = selection("id=history")
        run.check("newest first, here now", "here now" in first, first)
        # the second place in the session's history, whatever it kept (it misses some teleports within a region,
        # whose finish LibreMetaverse doesn't always report)
        before = api("SojournPeople", "teleportHistory", count=2)["places"][1]["pos"]
        key("down")
        m = mark()
        key("enter")
        run.expect("Enter on the one before goes back there", ["Arrived in"], m, 40)
        x, y, z, _ = tester_where()
        run.check("to it", math.dist((x, y), before[:2]) < 3, f"at {x:.0f}, {y:.0f}; it was {before[0]:.0f}, {before[1]:.0f}")
        key("tab")
        key("tab")
        rg = wait(lambda: find("type=ListItem;name~=, here") and [t for t in find("type=ListItem;name~=, here") if "people" in t["name"] or "person" in t["name"]], 4)
        run.check("the regions around listed, with people and where", rg and "here" in rg[0]["name"], label(rg[0]) if rg else "none")

    # a route from a notecard, step by step
    m = mark()
    say_in_app(f"//route {route_name}")
    run.expect("//route plays a notecard, saying how many steps", [f"Playing {route_name}", "2 steps"], m, 10)
    run.expect("its other lines read as notes", ["Note: Walk to the post"], m, 10)
    run.expect("each step said", ["Step 2 of 2"], m, 40)
    run.expect("and the end", [f"Route finished: {route_name}"], m, 40)
    x, y, z, _ = tester_where()
    run.check("at the route's last point", math.dist((x, y), ROUTE[1]) < 1.5, f"at {x:.1f}, {y:.1f}: {math.dist((x, y), ROUTE[1]):.1f} m from {ROUTE[1]}")

    for name in (mark_name, route_name):
        delete_by_search(run, name)


def search_and_people(run):
    # search: people, a result's profile and IM
    key("alt+5")
    d = wait_focus(lambda d: d["id"] == "searchWords", 4)
    run.check("Alt+5 goes to Search for", d, label(d) if d else focus_label())
    tree_check(run, "search", must=("searchWords", "searchKind", "results"))
    m = mark()
    type_text(VNAME, "id=searchWords")
    wait(lambda: value_of("id=searchWords") == VNAME, 3, 0.2)
    key("enter", target="id=searchWords")
    run.expect("Enter searches people, saying how many", ["found"], m, 15)
    got = wait_focus(lambda d: d["id"] == "results" or d["type"] == "ListItem", 4)
    run.check("the focus goes to the results, the first selected", got and VNAME in selection("id=results"),
              selection("id=results") or focus_label())
    key("apps")
    time.sleep(0.6)
    key("p")
    prof = wait_focus(lambda d: d["id"] == "profileText", 8)
    if run.check("Applications key, P: the profile in its own window", prof, label(prof) if prof else focus_label()):
        run.check("its text names them", VNAME in value_of("id=profileText"), " / ".join(value_of("id=profileText")[:100].splitlines()))
        key("esc")
        back = wait_focus(lambda d: d["id"] == "results" or d["type"] == "ListItem", 4)
        run.check("Escape closes it, back in the results", back, label(back) if back else focus_label())
    key("enter")
    conv = wait_focus(lambda d: d["id"] == "convInput", 4)
    run.check("Enter on a person opens an IM with them", conv and VNAME in conv["name"], label(conv) if conv else focus_label())
    if conv:
        key("ctrl+w")
        time.sleep(0.5)

    # places search: whatever the grid's search has, said either way
    key("alt+5")
    wait_focus(lambda d: d["id"] == "searchWords", 4)
    key("tab")
    if run.check("Tab reaches Look for", wait_focus(lambda d: d["id"] == "searchKind", 3), focus_label()):
        key("down")
        m = mark()
        key("enter", target="id=searchKind")
        heard = run.expect("Look for: Places, Enter searches them, saying how it went", ["Searching places"], m, 5)
        if heard:
            run.expect("then the result", ["found"], m, 20)
        key("up")

    # the people area's other lists, and pay through a confirmation
    key("alt+3")
    wait_focus(lambda d: d["id"] == "people" or d["type"] == "ListItem", 4)
    tree_check(run, "people, friends and groups", must=("people", "friends", "groups"))
    item = wait(lambda: find(f"type=ListItem;name~={VNAME}"), 10)
    if item and select_in("id=people", VNAME):
        key("apps")
        time.sleep(0.6)
        key("a")
        ask = wait_focus(lambda d: d["id"] == "answer", 5)
        if run.check("Applications key, A: Pay asks the amount", ask, label(ask) if ask else focus_label()):
            m = mark()
            type_text("1", "id=answer")
            key("enter", target="id=answer")
            conf = wait_focus(lambda d: d["id"] == "notices" or d["type"] == "ListItem", 6)
            if conf and "Pay" in selection("id=notices"):
                run.check("the payment waits for the person's confirmation", True, selection("id=notices"))
                time.sleep(0.2)
                key("esc")
                run.expect("Escape cancels it", ["Not paid"], m, 10)
            else:
                said = wait(lambda: next((e for e in said_since(m) if "paid" in (e.get("text") or "").lower()), None), 10)
                run.check("or the grid's refusal is said (no money here)", said, said["text"] if said else focus_label())


def more_commands(run):
    """The rest of Radegast's // set: siminfo, parcelinfo, scriptinfo."""
    for command, words in (("//siminfo", [f"Region {REGION}", "time dilation"]), ("//parcelinfo", ["parcel", "Here, "]),
                           ("//scriptinfo", ["wearing"])):
        m = mark()
        say_in_app(command)
        run.expect(f"{command} answers", words, m, 10)
    # the music's title on asking (titles read out on every change can be turned off in Alt+0)
    m = mark()
    say_in_app("//status music")
    said = wait(lambda: next((e.get("text") for e in said_since(m)
                              if re.search(r"No music on this parcel|Playing: |Music |This parcel has music|Sounds are off", e.get("text") or "")), None), 10)
    run.check("//status music answers", said, said or " / ".join(e.get("text") or "" for e in said_since(m)) or "nothing said")


def describing(run):
    """M5 without the assistant (the test runs with --no-assistant): the first Control+D says plainly that describing
    needs it, then what the lists know, and the keys after it only what the lists know; with the renderer out of sight, SojournDescribe's pictures aimed at
    the visitor, the fixture box and round the tester, each with what's visible in it by name."""
    back_to_chat()
    if RELAY:
        return   # (with the assistant: what's here is its absence)
    m = mark()
    key("ctrl+d")
    # said once a session: the parts step's describing may have had it first, so looked for since the run began
    run.expect("Control+D without the assistant says it needs one (once a session)", ["needs the assistant"], 0, 10)
    run.expect("then what the lists know of the place, the visitor by name", ["What the lists know", VNAME], m, 10)
    m = mark()
    key("ctrl+shift+d")
    run.expect("Control+Shift+D: what the tester wears, by name (the assistant's absence said only the first time)",
               ["What the lists know", "Clothing and body parts"], m, 10)
    key("alt+3")
    if wait(lambda: find(f"type=ListItem;name~={VNAME}"), 15):
        # (other agents' bots may be nearby too: Home, then down to the visitor by the list's own selection)
        wait_focus(lambda d: d["id"] == "people" or d["type"] == "ListItem", 4)
        key("home")
        for _ in range(12):
            if VNAME in selection("id=people"):
                break
            key("down")
            time.sleep(0.2)
        m = mark()
        key("ctrl+d")
        run.expect("Control+D on the visitor in People: who and where", ["What the lists know", VNAME, "metres"], m, 10)
    back_to_chat()
    m = mark()
    say_in_app("//describe more")
    run.expect("//describe more asks again", ["What the lists know"], m, 10)

    vid = visitor_id()
    r = timed_look(subject=vid, detail="brief")
    pics = r.get("pictures") or []
    if VIEWLESS:
        run.check("without the renderer: no pictures, said so", not pics and "no pictures" in json.dumps(r.get("notes")), json.dumps(r)[:300])
        return
    import base64
    import io
    from PIL import Image, ImageStat

    def spread(p):
        im = Image.open(io.BytesIO(base64.b64decode(p["png"]))).convert("RGB")
        return sum(ImageStat.Stat(im).stddev) / 3

    run.check("two pictures of the visitor, whole and from the waist up, not flat",
              len(pics) == 2 and all(p.get("png") and spread(p) > 6 for p in pics), "; ".join(p.get("label", "") for p in pics))
    who = next((s for s in (pics[0].get("seen") or []) if s.get("subject")), None) if pics else None
    run.check("the visitor is named in it and fills it", who and who.get("name") == VNAME and int(pics[0].get("subject_fills", "0%")[:-1]) >= 20,
              json.dumps(who) + f", fills {pics[0].get('subject_fills') if pics else '?'}")
    fixture = next((o for o in api("TesselObjects", "nearby", 10, dist=30, query="Access Fixture").get("objects", [])), None)
    if run.check("the fixture box is nearby", fixture, "found" if fixture else "not listed"):
        r = timed_look(subject=fixture["id"], detail="brief")
        p = (r.get("pictures") or [{}])[0]
        run.check("a picture of the fixture box, framed on it and named", p.get("png") and r["subject"].get("name") == "Access Fixture"
                  and int((p.get("subject_fills") or "0%")[:-1]) >= 10, f"{p.get('label')}, fills {p.get('subject_fills')}, {r['subject'].get('where')}")
    r = timed_look(subject="here", detail="brief")
    labels = [p.get("label", "") for p in r.get("pictures") or []]
    run.check("here: four views round the tester, the visitor among the people around",
              len(labels) == 4 and "ahead" in labels[0] and any(VNAME in s for s in r["subject"].get("people_within_30_metres", [])),
              "; ".join(labels))


def describing_looks(run, nonce):
    """A look waited for and said as it had loaded (docs/access-app.md > Describing): the tester wears a box and it's
    given a moss green picture made here once worn (so its texture is first asked for by the worn box, then the
    description), and the tester is described. Plain: the
    picture waits for it and its look is said to have loaded, the box green in the picture (on the processor too).
    With SOJOURN_TEST_HOLD_WORN=Tester[:SECONDS] (the app's own switch, World/LookHold.cs): for good, the box's colours
    said not to have loaded, by name, and with ACCESS_RELAY the description says its colours aren't known; for a few
    seconds, the picture waits them out and it's loaded. The visitor's look is said to have loaded either way."""
    if VIEWLESS:
        return
    hold = os.environ.get("SOJOURN_TEST_HOLD_WORN", "")
    # (the view's simulated GPU loss, 15 s in, over first: run alone this step comes before it)
    wait(lambda: (lambda st: st["state"] == "running" and st["losses"] >= 1)(api("SojournView", "status")), 60, 1)

    def loaded(look):
        return "look had" in look and "hadn't" not in look
    held_for_good = "tester" in hold.lower() and ":" not in hold
    name = f"Access Sweater {nonce}"
    root = api("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
    from PIL import Image, ImageDraw
    pic = OUT / "sweater.png"
    im = Image.new("RGB", (256, 256), (74, 103, 65))   # moss green, ribbed with darker stripes
    d = ImageDraw.Draw(im)
    for xx in range(0, 256, 16):
        d.rectangle((xx, 0, xx + 5, 255), fill=(58, 84, 50))
    im.save(pic)
    up = gated(run, "the sweater's picture uploaded", "TesselAgent", "upload", 120, path=str(pic), name=f"Access Sweater picture {nonce}")
    if not up:
        return
    tex = up.get("asset_id") or up.get("item_id")
    x, y, z, _ = tester_where()
    # (plain while it's in the world: textured there it would be drawn, and its texture loaded, before it's worn)
    box = api("TesselBuild", "create", 20, shape="box", pos=[x + 1.5, y, z], scale=[0.45, 0.45, 0.45], name=name)["id"]
    api("TesselBuild", "take", 30, ids=[box], folder_id=root)
    item = wait(lambda: inv_items(root).get(name), 10)
    if not run.check("(the sweater box in the inventory)", item, ""):
        return
    api("TesselAgent", "attach", 30, item_id=item, point="Chest")
    try:
        if not run.check("(and worn on the chest)", wait(lambda: attached(item), 10), ""):
            return
        worn = next(a["id"] for a in api("LLAgent", "getAttachedObjectsList").get("result", []) if a.get("item_id") == item)
        api("TesselBuild", "modify", 20, id=worn, texture=tex)
        time.sleep(1)
        t0 = time.time()
        r = timed_look(subject="me", detail="brief")
        took = time.time() - t0
        pics = r.get("pictures") or []
        look = (pics[0].get("subject_look") or "") if pics else ""
        print(f"     (subject_look: {look[:400]})")
        if pics and pics[0].get("png"):
            import base64
            (OUT / "describe-me.png").write_bytes(base64.b64decode(pics[0]["png"]))
            img = Image.open(OUT / "describe-me.png").convert("RGB")
            # moss green pixels: green over red, not under blue (a dusk sky lights it bluish; an untextured box is grey)
            green = sum(1 for p in img.getdata() if p[1] >= p[0] + 8 and p[1] >= p[2] - 2 and p[1] >= 25)
            share = green / (img.width * img.height)
        else:
            share = 0
        if held_for_good:
            run.check("held for good: the sweater's colours said not to have loaded, by name",
                      "hadn't fully loaded" in look and "not loaded on" in look and name in look, look[:300])
            run.check("(and it isn't green in the picture)", share < 0.01, f"{share:.1%} green (local/access-test-walk/describe-me.png or the run's folder)")
        else:
            run.check(f"the picture waited for the sweater and says the look had loaded{' (held ' + hold + ')' if hold else ''}",
                      loaded(look), f"{look[:200]}; look {took:.1f} s")
            run.check("(the sweater is green in the picture)", share > 0.01, f"{share:.1%} of it moss green")
        vid = visitor_id()
        rv = timed_look(subject=vid, detail="brief")
        vp = (rv.get("pictures") or [{}])[0]
        run.check("the visitor's look said to have loaded", loaded(vp.get("subject_look") or ""), (vp.get("subject_look") or "none")[:200])
        if RELAY:
            back_to_chat()
            m = mark()
            t0 = time.time()
            key("ctrl+shift+d")
            run.expect("Control+Shift+D: describing you", ["Describing you"], m, 10)
            said = wait(lambda: next((e for e in said_since(m) if len(e.get("text") or "") > 60
                                      and not (e.get("text") or "").startswith("Describing")), None), 150)
            text = (said or {}).get("text") or ""
            print(f"     (described in {time.time() - t0:.1f} s: {text[:600]})")
            if held_for_good:
                run.check("the description says the sweater's colours aren't known yet, naming none for it",
                          re.search(r"load|known|can't (tell|make out|see)|cannot|not yet", text, re.I) and "green" not in text.lower(), text[:300])
            else:
                run.check("the description gives the sweater's colour", re.search(r"green|olive|moss", text, re.I), text[:300])
    finally:
        api("TesselAgent", "detach", 30, item_id=item)
        back_to_chat()


def timed_look(**args):
    """SojournDescribe look, its time and each picture's settling printed (on the processor they're seconds each)."""
    t0 = time.time()
    r = api("SojournDescribe", "look", 90, **args)
    pics = r.get("pictures") or []
    print(f"     (look {args.get('subject')}: {time.time() - t0:.1f} s, {len(pics)} picture(s)"
          + (f", {pics[0].get('width')}x{pics[0].get('height')}" if pics else "") + ")")
    return r


def review_and_arrivals(run, nonce):
    back_to_chat()
    # review: back through what was read, a mark, a copy from it (the clipboard left alone: SOJOURN_TEST_NO_CLIPBOARD)
    m = mark()
    for word in ("alpha", "bravo", "charlie"):
        v(f"say review {word} {nonce}")
        time.sleep(0.4)
    run.expect("(three lines arrive)", [f"review charlie {nonce}"], m, 10)
    time.sleep(1)
    m = mark()
    key("ctrl+oem_open")
    key("ctrl+oem_open")
    run.expect("Control+[ twice: the line before the newest", [f"review bravo {nonce}"], m, 5)
    m = mark()
    key("ctrl+shift+m")
    run.expect("Control+Shift+M marks it", ["Marked."], m, 5)
    key("ctrl+oem_close")
    m = mark()
    key("ctrl+shift+c")
    run.expect("Control+Shift+C copies from the mark to the line reviewed", ["Copied 2 lines"], m, 5)
    m = mark()
    key("ctrl+shift+oem_open")
    run.check("Control+Shift+[ reads the oldest line kept", wait(lambda: said_since(m), 5), (said_since(m) or [{}])[0].get("text", "nothing")[:80])

    # timestamps by key
    m = mark()
    key("ctrl+shift+t")
    run.expect("Control+Shift+T: times in the histories", ["Times in the histories"], m, 5)
    v(f"say timed line {nonce}")
    stamped = wait(lambda: re.search(rf"\[\d\d:\d\d\] {VNAME}: timed line {nonce}", value_of("id=chatHistory")), 8)
    run.check("the next line has its time", stamped, stamped[0] if stamped else value_of("id=chatHistory")[-120:])
    m = mark()
    key("ctrl+shift+t")
    run.expect("Control+Shift+T again: no times", ["No times"], m, 5)

    # arrivals and departures (Firestorm's radar reports) by key
    m = mark()
    key("ctrl+shift+a")
    run.expect("Control+Shift+A: arrivals and departures read out", ["within chat range read out"], m, 5)
    time.sleep(9)   # the radar takes what's there as it is for 8 s after it's turned on (Radar.Settle)
    m = mark()
    v(f"goto {AWAY[0]} {AWAY[1]}")
    if not run.expect("the visitor walking away is said", [f"{VNAME} left chat range"], m, 30):
        print(f"     (visitor at {visitor_where()}, tester at {tester_where()[:3]})")
    m = mark()
    v(f"goto {VISITOR_START[0]} {VISITOR_START[1]}")
    run.expect("and coming back, how far in Access's words", [f"{VNAME} came into chat range, ", " metres away"], m, 30)
    m = mark()
    key("ctrl+shift+a")
    run.expect("Control+Shift+A again: not read out", ["not read out"], m, 5)
    f = focused()
    run.check("the keys left the focus in the Say box", f and f["id"] == "chatInput", label(f) if f else "nothing")


def touch_confirmed(run, what, box_id, **args):
    """LLAgent requestTouch on the visitor's box, as the assistant would: it waits for the person's confirmation
    (others' things), which comes up as a notice; Ctrl+N and Enter confirm it. The op's answer, or None."""
    import threading
    out = {}

    def call():
        try:
            out["r"] = api("LLAgent", "requestTouch", timeout=30, obj_uuid=box_id, **args)
        except Exception as e:
            out["e"] = e
    t = threading.Thread(target=call, daemon=True)
    t.start()
    m = mark()
    run.expect(f"{what}: the touch asks first", ["Touch", FACEBOX], m, 8)
    key("ctrl+n")
    wait_focus(lambda d: d["id"] == "notices" or d["type"] == "ListItem", 4)
    sel = wait(lambda: (lambda x: x if "Touch" in x else None)(selection("id=notices")), 4)
    time.sleep(1.2)   # it arms 0.8 s after it's shown
    key("enter")
    t.join(20)
    run.check(f"{what}: confirmed with Ctrl+N, Enter, it touches", "r" in out, json.dumps(out.get("r")) if "r" in out
              else f"{out.get('e') or 'no answer in 20 s'}; the notice: {sel!r}")
    return out.get("r")


def offscreen(run):
    """The renderer out of sight (Sojourn.Session's Offscreen/, docs/access.md > M0b): it runs (and came back after the
    GPU loss SOJOURN_OFFSCREEN_LOSE_AFTER fakes), unseen (the test has no assistant: no textures), the collision model
    has a box the visitor puts out, touches land on its faces as the VR client's do (the box's script says which), and
    the assistant's look then gets a real picture, drawing the view whole. The box stays out until the visitor's cleanup
    at the end."""
    if NO_GPU and not SOFTWARE:
        st = wait(lambda: (lambda s: s if s["state"] == "stopped" else None)(api("SojournView", "status")), 60, 1)
        run.check("without a Vulkan driver the view out of sight gives up, never having drawn", st and st["frames"] == 0,
                  json.dumps(st) if st else json.dumps(api("SojournView", "status")))
        box_checks(run, touches=False)
        return
    if SOFTWARE:
        offscreen_software(run)
        return

    def running():
        st = api("SojournView", "status")
        return st if st["state"] == "running" and st["losses"] >= 1 and st["frames"] > 2 else None
    st = wait(running, 60, 1)
    run.check("the view out of sight runs, and came back after a lost GPU", st,
              json.dumps(st) if st else json.dumps(api("SojournView", "status")))
    st = api("SojournView", "status")
    run.check("unseen with no assistant (only geometry drawn)", st.get("unseen") is True, json.dumps(st))
    box_checks(run)
    path = OUT / "look.png"
    r = api("LLViewerWindow", "saveSnapshot", timeout=20, filename=str(path), width=1024, height=1024, tool="assistant")
    detail = json.dumps(r)
    ok = False
    if path.exists():
        from PIL import Image, ImageStat
        im = Image.open(path).convert("RGB")
        spread = sum(ImageStat.Stat(im).stddev) / 3
        ok = im.size == (1024, 576) and spread > 6   # a flat frame (nothing drawn) is ~0; the local grid's night ~10
        detail += f"; {im.size[0]}x{im.size[1]}, spread {spread:.0f} ({path.relative_to(ROOT)})"
    run.check("the assistant's look (saveSnapshot) gets a picture of the view", r.get("ok") and ok, detail)

    st = api("SojournView", "status")
    run.check("and the view is drawn whole from then on", st.get("unseen") is False, json.dumps(st))


def offscreen_software(run):
    """The renderer out of sight on the processor (no GPU here can run it: Render's Gpu/SoftwareVulkan.cs, Mesa's
    lavapipe): it starts on it, draws nothing between pictures, so an idle app stays light, still lands touches on faces
    (each glances at what it touches), and the assistant's look gets a real picture, timed."""
    import psutil

    def running():
        st = api("SojournView", "status")
        return st if st["state"] == "running" else None
    st = wait(running, 60, 1)
    run.check("the view out of sight runs on the processor (no GPU it can use)", st and st.get("software") is True and "llvmpipe" in st["device"],
              json.dumps(st) if st else json.dumps(api("SojournView", "status")))
    time.sleep(3)
    # with the assistant up the warm-up draws one frame 2 s after it's up (OffscreenRenderer.WarmFirst), the rest at 20 s
    if RELAY:
        wait(lambda: any("a first frame drawn at once" in p.read_text(encoding="utf-8", errors="replace")
                         for p in (OUT / "app" / "logs").glob("*.log")), 30, 0.5)
    p = psutil.Process(APP_PID)
    p.cpu_percent(None)
    time.sleep(10)
    cpu = p.cpu_percent(None)
    mem = p.memory_info()
    st = api("SojournView", "status")
    run.check("idle, it draws nothing and the app stays light", st["frames"] <= (1 if RELAY else 0) and st["idle_hz"] == 0 and cpu < 15,
              f"{st['frames']} frames, idle_hz {st['idle_hz']}; {cpu:.0f}% of a core over 10 s, "
              f"{mem.private / 2**20:.0f} MB private, {mem.rss / 2**20:.0f} MB working set")
    box_checks(run, rays=False)
    path = OUT / "look.png"
    t0 = time.time()
    r = api("LLViewerWindow", "saveSnapshot", timeout=60, filename=str(path), width=1024, height=1024, tool="assistant")
    took = time.time() - t0
    detail = json.dumps(r)
    ok = False
    if path.exists():
        from PIL import Image, ImageStat
        im = Image.open(path).convert("RGB")
        spread = sum(ImageStat.Stat(im).stddev) / 3
        ok = im.size == (1024, 576) and spread > 6
        detail += f"; {im.size[0]}x{im.size[1]}, spread {spread:.0f} ({path.relative_to(ROOT)})"
    run.check("the assistant's look (saveSnapshot) gets a picture drawn on the processor", r.get("ok") and ok, f"{took:.1f} s; {detail}")
    mem = p.memory_info()
    print(f"     (after the picture: {mem.private / 2**20:.0f} MB private, {mem.rss / 2**20:.0f} MB working set; "
          f"{json.dumps(api('SojournView', 'status'))})")


def box_checks(run, touches=True, rays=True):
    """The collision models and touches on the visitor's facebox (unseen, in the step above): the one without a GPU
    (SojournCollision, walk mode's) and the renderer's (SojournView) meet it where it is. rays=False: not the
    renderer's, which counts only what it has drawn (on the processor nothing is drawn between pictures; it's for
    comparing, walk mode never uses it)."""
    x, y, z, _ = tester_where()
    made = v(f"facebox {FACEBOX_XY[0]} {FACEBOX_XY[1]} {z:.1f}", timeout=30)
    box = re.search(r"id ([0-9a-f-]{36})", made)
    if not run.check("the visitor's box is out", box, made):
        return
    box_id = box[1]
    dx, dy = FACEBOX_XY[0] - x, FACEBOX_XY[1] - y
    far = math.hypot(dx, dy)
    ray = {"from": [x, y, z], "dir": [dx, dy, 0], "max": 10}
    hit = wait(lambda: (lambda h: h if h.get("hit") and h.get("prim") == box_id else None)(api("SojournCollision", "collide", **ray)), 15, 1)
    run.check("the collision model (no GPU) has the box where it is", hit and abs(hit["distance"] - (far - 0.25)) < 0.3,
              f"{json.dumps(hit)}; the box's middle {far:.2f} m away" if hit else json.dumps(api("SojournCollision", "collide", **ray)))
    if not touches:
        return
    if rays:
        seen = wait(lambda: (lambda h: h if h.get("hit") and h.get("prim") == box_id else None)(api("SojournView", "collide", **ray)), 15, 1)
        run.check("the renderer's collision model has it at the same distance", seen and hit and abs(seen["distance"] - hit["distance"]) < 0.02,
                  f"{json.dumps(seen)} / {json.dumps(hit)}" if seen else json.dumps(api("SojournView", "collide", **ray)))

    n = visitor_n()
    # face 4, the box's side away from the tester (west): a touch the eyes' ray couldn't land
    r = touch_confirmed(run, "a touch on face 4", box_id, face=4)
    if r is not None:
        run.check("it lands on face 4", r.get("face") == 4, json.dumps(r))
        run.heard_by_visitor("the box's script detects face 4 with surface coordinates",
                             lambda d: re.search(r"touched face 4 st <(0\.\d+|1\.0+), (0\.\d+|1\.0+), ", d["msg"]), n, 10,
                             "touched face 4 st <s, t, 0>")
    back_to_chat()
    n = visitor_n()
    r = touch_confirmed(run, "a plain touch", box_id)
    if r is not None:
        face = r.get("face")
        run.check("it lands on the face toward the eyes, with a surface", isinstance(face, int) and face >= 0, json.dumps(r))
        run.heard_by_visitor(f"the box's script detects that face ({face})",
                             lambda d: f"touched face {face} st <" in d["msg"] and "-1" not in d["msg"], n, 10, f"touched face {face}")


CHEST = "Treasure Chest"


def list_items(sel):
    """The item names shown in one of the objects area's lists (QuireUia finds by the whole window, so each list's items
    are told by their words): objects, parts or faces."""
    names = [d["name"] for d in find("type=ListItem")]
    if sel == "id=faces":
        return [n for n in names if ", face " in n or n.startswith("Face ") or "faces" in n.lower() or "Faces" in n or n == "No part selected."]
    if sel == "id=parts":
        return [n for n in names if re.search(r"(part \d+ of \d+|^Part \d+ of |the whole object|parts\.|No object selected|isn't here|"
                                              r"button \d+ of \d+|^Read its buttons)", n)]
    return [n for n in names if not re.search(r"(part \d+ of \d+|^Part \d+ of |, face \d|button \d+ of \d+|^Read its buttons|^Face \d)", n)]


def parts(run):
    """Every part reachable (docs/access.md > M2), the treasure chest test (the Linden sunken city's chests): the visitor's chest whose
    root does nothing on a touch and whose unnamed lid answers. In Objects its parts are listed under it by name, or by
    number and what and where they are; Enter on the lid touches the lid (its script says so, with its link number);
    Tab passing the objects list doesn't change what the parts are of; with the renderer the lid's faces are listed by
    direction and the top one touched; //touch finds the lid by the chest's name, the latch by its own."""
    x, y, z, _ = tester_where()
    made = v(f"treasure {x - 2:.1f} {y + 3:.1f} {z:.1f}", timeout=60)
    ids = re.search(r"id ([0-9a-f-]{36}): lid ([0-9a-f-]{36}), latch ([0-9a-f-]{36})", made)
    if not run.check("(the visitor's treasure chest is out: only its unnamed lid takes touches)", ids, made):
        return
    try:
        key("alt+7")
        wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 4)
        listed = wait(lambda: find(f"type=ListItem;name~={CHEST}, "), 15)
        run.check("the chest is in Objects nearby", listed, label(listed[0]) if listed else "not listed")
        key("f5")
        time.sleep(1.5)
        if not run.check("(the chest selected)", select_in("id=objects", CHEST + ","), selected_text("id=objects")):
            return
        got = wait(lambda: (lambda ps: ps if len(ps) >= 4 else None)([n for n in (d["name"] for d in find("type=ListItem")) if " of 4" in n]), 10)
        names = got or [d["name"] for d in find("type=ListItem")]
        run.check("its four parts are listed below it", got, " | ".join(names))
        if not got:
            return
        root = next((n for n in got if n.startswith(f"{CHEST}, part 1 of 4, the root")), None)
        lid = next((n for n in got if n.startswith("Part 2 of 4") and "above the root" in n), None)
        latch = next((n for n in got if n.startswith("Latch, part ") and "nearer you" in n), None)
        foot = next((n for n in got if n.startswith("Part 4 of 4, a tiny box") and "below" in n), None)
        run.check("the root by name ('Treasure Chest, part 1 of 4, the root')", root, " | ".join(got))
        run.check("the unnamed lid by number, what and where ('Part 2 of 4, a box above the root')", lid, " | ".join(got))
        if not run.check("the latch by name, nearer you", latch, " | ".join(got)):
            kids = api("TesselObjects", "get", 15, ids=[ids[1]], children=True, full=True).get("objects", [{}])[0].get("children", [])
            print("     (the agent API's children: " + "; ".join(f'{k.get("name")!r} {k.get("id")}' for k in kids) + f"; latch {ids[3]})")
        run.check("the unnamed foot as a tiny box below", foot, " | ".join(got))
        run.check("only the lid marked as taking touches", [n for n in got if n.endswith(", touchable")] == [lid], " | ".join(got))
        key("tab")
        d = wait_focus(lambda d: d["id"] == "parts" or d["type"] == "ListItem", 4)
        run.check("Tab goes to the parts, named for the chest", d and wait(lambda: any(
            p["name"] == f"Parts of {CHEST}" for p in find("id=parts")), 3), label(d) if d else focus_label())
        if not run.check("(the lid selected)", select_in("id=parts", "Part 2 of 4"), selected_text("id=parts")):
            return
        n = visitor_n()
        m = mark()
        key("enter")
        run.expect("Enter on it touches the lid, saying so", [f"Touched part 2 of {CHEST}"], m, 10)
        run.heard_by_visitor("the lid's script takes the touch (not the root)", lambda d: f"chest lid touched by {TNAME}" in d["msg"], n, 10,
                             f"chest lid touched by {TNAME}")
        # Radegast's lesson: Tab passing the objects list made the root the target again
        key("shift+tab")
        wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 3)
        key("tab")
        wait_focus(lambda d: d["id"] == "parts" or d["type"] == "ListItem", 3)
        run.check("Shift+Tab and Tab back leave the lid chosen", "Part 2 of 4" in selected_text("id=parts"), selected_text("id=parts"))
        # Control+D describes the part the person is on (it described the place, where nothing else was selected)
        m = mark()
        key("ctrl+d")
        run.expect("Control+D on a part describes that part, not the place", ["What the lists know: " + CHEST], m, 10)
        key("tab")
        f = wait_focus(lambda d: d["id"] == "faces" or d["type"] == "ListItem", 4)
        run.check("Tab again goes to the lid's faces", f and any(p["name"] == "Faces of part 2 of " + CHEST for p in find("id=faces")),
                  label(f) if f else focus_label())
        if VIEWLESS:
            said = " | ".join(list_items("id=faces"))
            run.check("without the renderer, the faces list says they need it", "need the view" in said, said)
        else:
            faces = wait(lambda: (lambda fs: fs if any(", face " in x for x in fs) else None)(list_items("id=faces")), 10)
            run.check("its faces are listed by direction ('The top, face 0', the side facing you)", faces and any(
                x.startswith("The top, face 0") for x in faces) and any(x.startswith("The side facing you") for x in faces),
                " | ".join(faces or list_items("id=faces")))
            if faces and select_in("id=faces", "The top, face 0"):
                n = visitor_n()
                m = mark()
                key("enter")
                run.expect("Enter on the top touches it there", [f"Touched part 2 of {CHEST}, the top"], m, 10)
                run.heard_by_visitor("the lid's script detects face 0", lambda d: "chest lid touched" in d["msg"] and d["msg"].endswith("face 0"), n, 10, "face 0")
        back_to_chat()
        n = visitor_n()
        m = mark()
        say_in_app("//touch treasure chest")
        run.expect("//touch by the chest's name touches its one touchable part", [f"Touched part 2 of {CHEST}"], m, 15)
        run.heard_by_visitor("(the lid's script says so)", lambda d: "chest lid touched" in d["msg"], n, 10, "chest lid touched")
        m = mark()
        say_in_app("//touch treasure chest latch")
        run.expect("//touch OBJECT PART touches that part by name", ["Touched Latch"], m, 15)
        if not VIEWLESS:
            n = visitor_n()
            m = mark()
            say_in_app("//touch treasure chest face top")
            run.expect("//touch ... face top: the lid's top", [f"Touched part 2 of {CHEST}, the top"], m, 15)
            run.heard_by_visitor("(face 0, says its script)", lambda d: "chest lid touched" in d["msg"] and d["msg"].endswith("face 0"), n, 10, "face 0")
    finally:
        v(f"derez {CHEST}")


HUD_SCRIPT = 'default { touch_start(integer n) { llSay(0, "Access HUD pressed, link " + (string)llDetectedLinkNumber(0)); } }'


def gated(run, what, pump, op, timeout=60, **args):
    """An op the gate holds for the person's confirmation (a new script, a delete): called as a program would, then
    Ctrl+N and Enter by keys. Its answer, or None."""
    import threading
    out = {}

    def call():
        try:
            out["r"] = api(pump, op, timeout, **args)
        except Exception as e:
            out["e"] = e
    t = threading.Thread(target=call, daemon=True)
    t.start()
    time.sleep(1.5)
    key("ctrl+n")
    wait_focus(lambda d: d["id"] == "notices" or d["type"] == "ListItem", 4)
    time.sleep(1.2)   # it arms 0.8 s after it's shown
    key("enter")
    t.join(timeout)
    run.check(f"({what}: confirmed by keys)", "r" in out and not out["r"].get("error"), json.dumps(out.get("r")) if "r" in out else str(out.get("e")))
    back_to_chat()
    return out.get("r")


def huds(run, nonce):
    """HUD buttons as parts (M2): a HUD of two prims made and worn (a base, and a button "Play" whose script says when
    it's pressed); Objects' Show: Your HUDs lists it; its parts are the base and Play; Enter on Play presses it. And
    what's worn by name (Ctrl+Shift+W, //outfit) has it among the HUDs."""
    name = f"Access HUD {nonce}"
    root = api("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
    x, y, z, _ = tester_where()
    base = api("TesselBuild", "create", 20, shape="box", pos=[x + 1.5, y, z], scale=[0.3, 0.3, 0.05], name=name)["id"]
    button = api("TesselBuild", "create", 20, shape="box", pos=[x + 1.5, y, z + 0.2], scale=[0.1, 0.1, 0.05], name="Play")["id"]
    script = api("TesselInventory", "createScript", 60, name=f"Access HUD button {nonce}", text=HUD_SCRIPT)
    if not gated(run, "the button's script put in", "TesselBuild", "addToContents", id=button, item_id=script["item_id"]):
        return
    api("TesselBuild", "link", 20, ids=[base, button])
    api("TesselBuild", "take", 30, ids=[base], folder_id=root)
    item = wait(lambda: inv_items(root).get(name), 10)
    if not run.check("(the HUD in the inventory)", item, ""):
        return
    api("TesselAgent", "attach", 30, item_id=item, point="HUD Center")
    if not run.check("(and worn on HUD Center)", wait(lambda: attached(item), 10), ""):
        return
    try:
        m = mark()
        key("ctrl+shift+w")
        run.expect("Ctrl+Shift+W says what's worn, the HUD among the HUDs", ["Wearing", "HUDs:", name], m, 6)
        m = mark()
        say_in_app("//outfit")
        run.expect("//outfit says it too", ["Wearing", name], m, 6)
        m = mark()
        key("alt+shift+7")   # Objects showing what you wear, HUDs first (the blind tester found no way to her HUDs)
        wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 4)
        listed = wait(lambda: find(f"type=ListItem;name~={name}, on HUD Center, 2 parts"), 8)
        run.check("Alt+Shift+7: what you wear lists it first, where it's worn and its parts", listed, " | ".join(list_items("id=objects")))
        items = list_items("id=objects")
        run.check("HUDs come first", items and items[0].startswith(name), " | ".join(items))
        if not listed:
            return
        wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 3)
        select_in("id=objects", name)
        play = wait(lambda: (lambda ps: ps if ps else None)([n for n in list_items("id=parts") if n.startswith("Play, part 2 of 2")]), 10)
        run.check("its parts: the button Play by name, above the base, taking touches", play and "above the root" in play[0] and play[0].endswith(", touchable"),
                  " | ".join(list_items("id=parts")))
        key("tab")
        wait_focus(lambda d: d["id"] == "parts" or d["type"] == "ListItem", 3)
        if select_in("id=parts", "Play, part"):
            n = visitor_n()
            m = mark()
            key("enter")
            run.expect("Enter on Play presses it", ["Touched Play"], m, 10)
            run.heard_by_visitor("its script says it was pressed, on its own prim", lambda d: "Access HUD pressed, link 2" in d["msg"], n, 10, "pressed, link 2")
    finally:
        try:   # Show back to things nearby, whatever stopped the step (the placing step lists them)
            key("alt+7")
            time.sleep(0.5)
            u("setfocus id=objectsShow")
            key("up")
        except Exception:
            pass
        api("TesselAgent", "detach", 30, item_id=item)
        back_to_chat()


# A HUD that's one picture of buttons (docs/access-app.md > HUDs out of sight): its regions in the picture, as fractions
# from the left and the top (u0, v0, u1, v1), and the panel's look
PANEL_BUTTONS = {"Brake": (0.05, 0.26, 0.47, 0.58), "Horn": (0.53, 0.26, 0.95, 0.58),
                 "Forward": (0.05, 0.66, 0.47, 0.96), "Reverse": (0.53, 0.66, 0.95, 0.96)}
PANEL_COLOURS = {"Brake": (200, 30, 30), "Horn": (230, 200, 40), "Forward": (40, 160, 60), "Reverse": (40, 80, 200)}
PANEL_SCRIPT = ('default { touch_start(integer n) { vector st = llDetectedTouchST(0); llSay(0, "Access Panel touched, face " '
                '+ (string)llDetectedTouchFace(0) + ", st " + (string)st.x + " " + (string)st.y); } }')


def panel_picture(path):
    """The driver panel's picture: a title readout and four labelled buttons in two rows."""
    from PIL import Image, ImageDraw, ImageFont
    w, h = 512, 256
    im = Image.new("RGB", (w, h), (28, 30, 36))
    d = ImageDraw.Draw(im)
    try:
        font, small = ImageFont.truetype("arialbd.ttf", 34), ImageFont.truetype("arialbd.ttf", 26)
    except OSError:
        font = small = ImageFont.load_default()
    d.text((w / 2, h * 0.11), "DRIVER PANEL  SPEED 0", fill=(220, 220, 220), font=small, anchor="mm")
    for name, (u0, v0, u1, v1) in PANEL_BUTTONS.items():
        box = (u0 * w, v0 * h, u1 * w, v1 * h)
        d.rounded_rectangle(box, 12, fill=PANEL_COLOURS[name], outline=(240, 240, 240), width=3)
        d.text(((box[0] + box[2]) / 2, (box[1] + box[3]) / 2), name.upper(), fill=(255, 255, 255) if name != "Horn" else (20, 20, 20),
               font=font, anchor="mm")
    im.save(path)


def panel_touches(n, secs=10):
    """The panel's touch reports the visitor heard after its line n: [(face, s, t)]."""
    def look():
        lines = v(f"heard 50 since {n}")
        got = []
        for l in ([] if lines == "none" else lines.splitlines()):
            m_ = re.search(r"Access Panel touched, face (-?\d+), st ([\d.\-]+) ([\d.\-]+)", json.loads(l)["msg"])
            if m_:
                got.append((int(m_.group(1)), float(m_.group(2)), float(m_.group(3))))
        return got or None
    return wait(look, secs, 0.5) or []


def st_orientation(pairs):
    """Of the eight ways a face's ST can lie across the HUD (turned, mirrored), the one that fits every (meant, got)
    pair best: (worst error, name). meant: (u, v) across the HUD from its left and top; got: the script's (s, t)."""
    ways = {
        "s across, t up": lambda u, v: (u, 1 - v), "s across, t down": lambda u, v: (u, v),
        "s back, t up": lambda u, v: (1 - u, 1 - v), "s back, t down": lambda u, v: (1 - u, v),
        "s up, t across": lambda u, v: (1 - v, u), "s down, t across": lambda u, v: (v, u),
        "s up, t back": lambda u, v: (1 - v, 1 - u), "s down, t back": lambda u, v: (v, 1 - u),
    }
    best = min(((max(max(abs(f(*m)[0] - g[0]), abs(f(*m)[1] - g[1])) for m, g in pairs), k) for k, f in ways.items()), default=(9, "none"))
    return best


def hud_picture(run, nonce):
    """A HUD that's one textured prim of buttons, as most game, vehicle and fishing HUDs are, its script telling them
    apart by where it's touched: drawn out of sight, its face listed as it shows and pressed in its middle; buttons saved
    for it (as the assistant's reading saves them) listed after its parts and pressed where they are, by Enter and by
    //touch, each landing where it was meant (the script's llDetectedTouchST, checked against one way the face lies);
    SojournHud look's picture and describing's picture of it. With ACCESS_RELAY, the assistant reads its buttons off
    the picture and Brake lands inside the brake."""
    if VIEWLESS:
        return
    name = f"Access Panel {nonce}"
    # an earlier run's test HUDs still worn would cover this one's middle (worn on the same point)
    for h in api("SojournHud", "list")["huds"]:
        if re.match(r"Access (HUD|Panel) [0-9a-f]{6}$", h["name"]) and not h["name"].endswith(nonce):
            api("TesselAgent", "detach", 30, object_id=h["id"])
    root = api("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
    pic_path = OUT / "panel.png"
    panel_picture(pic_path)
    up = gated(run, "the panel's picture uploaded", "TesselAgent", "upload", 120, path=str(pic_path), name=f"Access Panel picture {nonce}")
    if not up:
        return
    tex = up.get("asset_id") or up.get("item_id")
    x, y, z, _ = tester_where()
    # thin into the screen, 0.48 of the screen's height wide and 0.24 high: the picture's 2:1 on its front
    panel = api("TesselBuild", "create", 20, shape="box", pos=[x + 1.5, y, z], scale=[0.02, 0.48, 0.24], name=name, texture=tex)["id"]
    script = api("TesselInventory", "createScript", 60, name=f"Access Panel script {nonce}", text=PANEL_SCRIPT)
    if not gated(run, "the panel's script put in", "TesselBuild", "addToContents", id=panel, item_id=script["item_id"]):
        return
    api("TesselBuild", "take", 30, ids=[panel], folder_id=root)
    item = wait(lambda: inv_items(root).get(name), 10)
    if not run.check("(the panel in the inventory)", item, ""):
        return
    api("TesselAgent", "attach", 30, item_id=item, point="HUD Center")
    if not run.check("(and worn on HUD Center)", wait(lambda: attached(item), 10), ""):
        return
    pressed = []   # ((u, v) meant across the HUD, (s, t) the script said)
    try:
        hud_id = wait(lambda: next((h["id"] for h in api("SojournHud", "list")["huds"] if h["name"] == name and h["drawn"]), None), 15)
        if not run.check("SojournHud list: the panel worn and placed out of sight", hud_id, json.dumps(api("SojournHud", "list"))[:300]):
            return
        t0 = time.time()
        look = api("SojournHud", "look", 60, hud=hud_id)
        took = time.time() - t0
        import base64
        import io
        from PIL import Image, ImageStat
        (OUT / "panel-look.png").write_bytes(base64.b64decode(look["png"]))
        im = Image.open(io.BytesIO(base64.b64decode(look["png"]))).convert("RGB")
        rect = next((h["rect"] for h in look["huds"] if h["id"] == hud_id), None)
        run.check("SojournHud look: a picture of it out of sight, its place on the picture, not flat", rect and sum(ImageStat.Stat(im).stddev) / 3 > 20,
                  f"{look['width']}x{look['height']}, rect {rect}, {took:.1f} s (local/access-test/panel-look.png)")
        if rect:
            # the brake's middle in the picture is red: the texture is on its front as drawn, upright
            bu = PANEL_BUTTONS["Brake"]
            px = im.getpixel((int(rect[0] + rect[2] * (bu[0] + bu[2]) / 2), int(rect[1] + rect[3] * (bu[1] + bu[3]) / 2 + rect[3] * 0.1)))
            run.check("(its picture shows the panel's texture on its front: red where the brake is)", px[0] > 120 and px[1] < 90 and px[2] < 90, str(px))

        m = mark()
        key("alt+shift+7")
        wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 4)
        if not run.check("Alt+Shift+7: the panel listed, one part, touchable", wait(lambda: find(f"type=ListItem;name~={name}, on HUD Center, 1 part, touchable"), 8),
                         " | ".join(list_items("id=objects"))):
            return
        select_in("id=objects", name)
        parts = wait(lambda: (lambda ps: ps if any("Read its buttons" in p for p in ps) else None)(list_items("id=parts")), 10) or list_items("id=parts")
        run.check("its parts: the whole object, then a line to read its buttons", any(p.startswith(name) and "the whole object" in p for p in parts)
                  and parts[-1].startswith("Read its buttons from its picture"), " | ".join(parts))
        key("tab")
        wait_focus(lambda d: d["id"] == "parts" or d["type"] == "ListItem", 3)
        select_in("id=parts", "the whole object")
        key("tab")
        wait_focus(lambda d: d["id"] == "faces" or d["type"] == "ListItem", 3)
        faces = wait(lambda: (lambda fs: fs if any(f.startswith("The front, face") for f in fs) else None)(list_items("id=faces")), 10) or list_items("id=faces")
        run.check("Faces of it: only its front, as it shows on the screen", len(faces) == 1 and faces[0].startswith("The front, face"), " | ".join(faces))
        if faces and faces[0].startswith("The front, face"):
            front = int(faces[0].rsplit(" ", 1)[1])
            n = visitor_n()
            m = mark()
            key("enter")
            run.expect("Enter on the front touches it", ["Touched", "the front"], m, 10)
            got = panel_touches(n)
            run.check("its script says that face, in its middle", got and got[0][0] == front and abs(got[0][1] - 0.5) < 0.05 and abs(got[0][2] - 0.5) < 0.05,
                      str(got))
            if got:
                pressed.append(((0.5, 0.5), got[0][1:]))

        # buttons saved as the assistant's reading saves them: each region's box on the look's picture
        if rect:
            given = [{"name": b, "rect": [rect[0] + rect[2] * u0, rect[1] + rect[3] * v0, rect[2] * (u1 - u0), rect[3] * (v1 - v0)]}
                     for b, (u0, v0, u1, v1) in PANEL_BUTTONS.items()]
            given.append({"name": "Off the edge", "rect": [rect[0] + rect[2] + 40, rect[1], 20, 20]})
            saved = api("SojournHud", "save_buttons", hud=hud_id, picture=look["picture"], buttons=given, by="the test")
            run.check("save_buttons keeps the four, in reading order, and drops one off the panel",
                      saved.get("buttons") == ["Brake", "Horn", "Forward", "Reverse"] and saved.get("dropped") == ["Off the edge"], json.dumps(saved))
            back_to_chat()
            key("alt+shift+7")
            wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 4)
            select_in("id=objects", name)
            key("tab")
            wait_focus(lambda d: d["id"] == "parts" or d["type"] == "ListItem", 3)
            key("f5")
            parts = wait(lambda: (lambda ps: ps if any("button 1 of 4" in p for p in ps) else None)(list_items("id=parts")), 10) or list_items("id=parts")
            run.check("its parts then its buttons by name, number and place, and a line to read them again",
                      "Brake, button 1 of 4, at the top left" in parts and "Reverse, button 4 of 4, at the bottom right" in parts
                      and parts[-1].startswith("Read its buttons again"), " | ".join(parts))
            for b in ("Horn", "Forward"):
                if not select_in("id=parts", f"{b}, button"):
                    run.check(f"({b} selected)", False, selected_text("id=parts"))
                    continue
                n = visitor_n()
                m = mark()
                key("enter")
                run.expect(f"Enter on {b} presses it", [f"Pressed {b}"], m, 10)
                got = panel_touches(n)
                u0, v0, u1, v1 = PANEL_BUTTONS[b]
                if run.check(f"(the panel's script heard {b})", got, str(got)):
                    pressed.append((((u0 + u1) / 2, (v0 + v1) / 2), got[0][1:]))
            back_to_chat()
            n = visitor_n()
            m = mark()
            say_in_app("//touch reverse")
            run.expect("//touch reverse presses the button read on the HUD (nothing nearby has the name)", ["Pressed Reverse"], m, 10)
            got = panel_touches(n)
            u0, v0, u1, v1 = PANEL_BUTTONS["Reverse"]
            if run.check("(the panel's script heard Reverse)", got, str(got)):
                pressed.append((((u0 + u1) / 2, (v0 + v1) / 2), got[0][1:]))
            err, way = st_orientation(pressed)
            run.check("every press landed where it was meant: one way the face lies fits them all within 0.03",
                      len(pressed) >= 3 and err < 0.03, f"{way}, worst {err:.3f} over {len(pressed)} presses: {pressed}")

        r = timed_look(subject=hud_id, detail="brief")
        pics = r.get("pictures") or []
        run.check("describing the HUD: its picture as it shows on the screen, and what it is",
                  len(pics) == 1 and pics[0].get("png") and "as it shows on their screen" in pics[0].get("label", "")
                  and "HUD" in (r.get("subject") or {}).get("what", ""), json.dumps({k: v for k, v in r.items() if k != "pictures"})[:300])

        if os.environ.get("ACCESS_RELAY"):
            hud_read_by_assistant(run, name, hud_id)
    finally:
        try:
            key("alt+7")
            time.sleep(0.5)
            u("setfocus id=objectsShow")
            key("up")
        except Exception:
            pass
        api("TesselAgent", "detach", 30, item_id=item)
        back_to_chat()


def hud_read_by_assistant(run, name, hud_id):
    """ACCESS_RELAY: the assistant reads the panel's buttons off its picture (a lean call through the relay), the parts
    list shows them, and Enter on Brake lands inside the brake as painted (the script's ST)."""
    api("SojournHud", "forget_buttons", hud=hud_id)
    back_to_chat()
    key("alt+shift+7")
    wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 4)
    select_in("id=objects", name)
    key("tab")
    wait_focus(lambda d: d["id"] == "parts" or d["type"] == "ListItem", 3)
    key("f5")
    if not run.check("(the reading line)", select_in("id=parts", "Read its buttons from its picture"), selected_text("id=parts")):
        return
    m = mark()
    t0 = time.time()
    key("enter")
    run.expect("Enter on it: reading the buttons", ["Reading the buttons on", name], m, 10)
    said = run.expect("the assistant says what it read", ["buttons on", name, "listed after its parts"], m, 120)
    print(f"     (read in {time.time() - t0:.1f} s: {said['text'][:300] if said else '-'})")
    parts = wait(lambda: (lambda ps: ps if any(", button 1 of" in p for p in ps) else None)(list_items("id=parts")), 10) or list_items("id=parts")
    names = [p.split(", button")[0].lower() for p in parts if ", button " in p]
    run.check("its buttons listed, the four labels among them", all(any(b.lower() in n for n in names) for b in PANEL_BUTTONS), " | ".join(parts))
    key("home")
    for _ in range(12):
        if selected_text("id=parts").lower().startswith("brake"):
            break
        key("down")
        time.sleep(0.12)
    if not run.check("(Brake selected)", selected_text("id=parts").lower().startswith("brake"), selected_text("id=parts")):
        return
    n = visitor_n()
    m = mark()
    key("enter")
    run.expect("Enter on Brake presses it", ["Pressed", "brake"], m, 10)
    got = panel_touches(n)
    u0, v0, u1, v1 = PANEL_BUTTONS["Brake"]
    # the texture's top is the face's top: s across, t up
    run.check("the assistant's Brake lands inside the brake as painted", got and u0 - 0.02 <= got[0][1] <= u1 + 0.02 and 1 - v1 - 0.02 <= got[0][2] <= 1 - v0 + 0.02,
              f"{got}; the brake spans s {u0}-{u1}, t {1 - v1:.2f}-{1 - v0:.2f}")
    # Control+D on it there: the HUD described from its picture
    m = mark()
    t0 = time.time()
    key("ctrl+d")
    run.expect("Control+D on its button: describing the HUD", ["Describing your", name], m, 10)
    said = run.expect("the description reads its controls (Brake among them)", ["brake", "reverse"], m, 120)
    print(f"     (described in {time.time() - t0:.1f} s)")


def placing(run, nonce):
    """Placing by words (M2): an object from the inventory rezzed "a metre to my left" (Applications key, S), then moved
    "by 50 cm up" from Objects (Applications key, M); each where the words say, resting on the ground first."""
    name = f"Access Block {nonce}"
    root = api("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
    x, y, z, _ = tester_where()
    blk = api("TesselBuild", "create", 20, shape="box", pos=[x + 2, y, z], scale=[0.3, 0.3, 0.3], name=name)["id"]
    api("TesselBuild", "take", 30, ids=[blk], folder_id=root)
    if not run.check("(a block in the inventory)", wait(lambda: inv_items(root).get(name), 10), ""):
        return
    try:
        key("alt+4")
        wait_focus(lambda d: d["id"] == "inventory", 4)
        inv_search(name, name)
        x, y, z, heading = tester_where()
        m = mark()
        key("apps")
        time.sleep(0.6)
        key("s")
        a = wait_focus(lambda d: d["id"] == "answer", 5)
        if not run.check("Applications key, S: Rez where you say asks where", a, label(a) if a else focus_label()):
            return
        u("set id=answer |")
        type_text("a metre to my left", "id=answer")
        wait(lambda: value_of("id=answer") == "a metre to my left", 3, 0.2)
        key("enter", target="id=answer")
        run.expect("it's rezzed, said where", [f"Rezzed {name}", "1 metre to your left"], m, 20)
        objs = wait(lambda: api("TesselObjects", "nearby", dist=10, query=name).get("objects"), 6)
        if not run.check("it's out", objs, ""):
            return
        o = objs[0]
        h = math.radians(heading)
        want = (x - math.cos(h), y + math.sin(h))
        off = math.dist(o["pos"][:2], want)
        run.check("a metre to the tester's left (within 0.3 m)", off < 0.3, f"at {o['pos'][0]:.2f}, {o['pos'][1]:.2f}; wanted {want[0]:.2f}, {want[1]:.2f} (heading {heading:.0f})")
        ground = api("TesselAgent", "ground", points=[o["pos"][:2]])["heights"][0]
        bottom = o["pos"][2] - 0.15
        run.check("resting on the ground", abs(bottom - ground) < 0.15, f"bottom {bottom:.2f}, ground {ground}")
        z0 = o["pos"][2]
        key("alt+7")
        wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 4)
        key("f5")
        time.sleep(1.5)
        if not run.check("(it's selected in Objects)", select_in("id=objects", name), selected_text("id=objects")):
            return
        m = mark()
        key("apps")
        time.sleep(0.6)
        key("m")
        if not run.check("Applications key, M: Move asks where", wait_focus(lambda d: d["id"] == "answer", 5), focus_label()):
            return
        u("set id=answer |")
        type_text("by 50 cm up", "id=answer")
        wait(lambda: value_of("id=answer") == "by 50 cm up", 3, 0.2)
        key("enter", target="id=answer")
        run.expect("moved, said how", [f"{name} moved 50 centimetres up"], m, 15)
        up = wait(lambda: (lambda os: os[0]["pos"][2] if os and os[0]["pos"][2] - z0 > 0.3 else None)(
            api("TesselObjects", "nearby", dist=10, query=name).get("objects")), 6)
        run.check("half a metre higher", up and abs(up - z0 - 0.5) < 0.1, f"from {z0:.2f} to {up}")
        # deleting what she rezzed (the blind tester found no way, 2026-11-01): Delete in Objects, confirmed in Notices
        key("alt+7")
        wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 4)
        key("f5")
        time.sleep(1.5)
        if run.check("(selected again in Objects)", select_in("id=objects", name), selected_text("id=objects")):
            m = mark()
            key("del")
            confirm_in_notices(run, "Trash", m)
            run.expect(f"Enter confirms: {name} deleted from the world", [f"Deleted {name}"], m, 10)
            gone = wait(lambda: not api("TesselObjects", "nearby", dist=20, query=name).get("objects"), 8)
            run.check("it's gone from the world", gone, "still there")
    finally:
        for o in api("TesselObjects", "nearby", dist=20, query=name).get("objects", []):
            api("TesselBuild", "take", 30, ids=[o["id"]], folder_id=root)
        clear_search()
        back_to_chat()


# SOJOURN_TEST_UPLOAD_FEE: pictures and models confirmed at L$0, a sound's clips at L$10 each, against a pretend balance
# of L$100 (the local grid's is 0), so animations at L$500 are unaffordable; a single sound stays free
UPLOAD_FEES = "texture=0,mesh=0,animation=500,clip=10,balance=100"
CLIP_TONES = (300, 500, 700)   # Hz: the 75-second fixture's tone in each of its 29.9-second clips


def upload_fixtures(nonce):
    """The files the upload step sends, made here (no image library): a 300 by 200 PNG with see-through parts, a two
    second stereo WAV at 48 kHz, a 75 second WAV (too long for one sound: clips) with a tone of its own in each clip,
    and 40 seconds of it as FLAC where ffmpeg is on this PC, a text file (wrong kind), a one-metre cube as glTF, and a
    three-frame BVH."""
    import array, base64, struct, wave, zlib
    d = OUT / "upload"
    d.mkdir(parents=True, exist_ok=True)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
    rows = b"".join(b"\0" + b"".join(bytes((x % 256, y % 256, 128, 255 if x < 150 else 96)) for x in range(300)) for y in range(200))
    (d / "Sunset.png").write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 300, 200, 8, 6, 0, 0, 0))
                                   + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))

    def wav(path, secs, rate, channels, width):
        with wave.open(str(path), "wb") as w:
            w.setnchannels(channels)
            w.setsampwidth(width)
            w.setframerate(rate)
            frames = bytearray()
            for i in range(int(secs * rate)):
                s = math.sin(i * 2 * math.pi * 440 / rate)
                one = struct.pack("<h", int(s * 8000)) if width == 2 else bytes((int(128 + s * 60),))
                frames += one * channels
            w.writeframes(bytes(frames))
    wav(d / "Door.wav", 2, 48000, 2, 2)
    rate = 22050   # (made 44.1 kHz on the way up)
    with wave.open(str(d / "Harp song.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(array.array("h", (int(8000 * math.sin(2 * math.pi * CLIP_TONES[min(2, int(i / rate / 29.9))] * i / rate))
                                        for i in range(75 * rate))).tobytes())
    if ffmpeg := shutil.which("ffmpeg"):
        subprocess.run([ffmpeg, "-y", "-v", "error", "-i", str(d / "Harp song.wav"), "-t", "40", str(d / "Harp short.flac")],
                       stdin=subprocess.DEVNULL, creationflags=CREATE_NO_WINDOW)
    (d / "Shopping list.txt").write_text("not a picture\n", encoding="utf-8")

    # a cube, 24 corners (a normal each), 12 triangles
    pos, nrm, idx = [], [], []
    for axis in range(3):
        for sign in (-0.5, 0.5):
            n = [0.0, 0.0, 0.0]
            n[axis] = 1.0 if sign > 0 else -1.0
            u_ax, v_ax = [a for a in range(3) if a != axis]
            base = len(pos)
            for du, dv in ((-0.5, -0.5), (0.5, -0.5), (0.5, 0.5), (-0.5, 0.5)):
                p = [0.0, 0.0, 0.0]
                p[axis], p[u_ax], p[v_ax] = sign, du, dv
                pos.append(p)
                nrm.append(n)
            idx += [base, base + 1, base + 2, base, base + 2, base + 3] if sign > 0 else [base, base + 2, base + 1, base, base + 3, base + 2]
    blob = b"".join(struct.pack("<3f", *p) for p in pos) + b"".join(struct.pack("<3f", *n) for n in nrm) + b"".join(struct.pack("<H", i) for i in idx)
    gltf = {
        "asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0, "name": "Cube"}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1}, "indices": 2}]}],
        "buffers": [{"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(blob).decode()}],
        "bufferViews": [{"buffer": 0, "byteOffset": 0, "byteLength": 288}, {"buffer": 0, "byteOffset": 288, "byteLength": 288},
                        {"buffer": 0, "byteOffset": 576, "byteLength": 72}],
        "accessors": [{"bufferView": 0, "componentType": 5126, "count": 24, "type": "VEC3", "min": [-0.5] * 3, "max": [0.5] * 3},
                      {"bufferView": 1, "componentType": 5126, "count": 24, "type": "VEC3"},
                      {"bufferView": 2, "componentType": 5123, "count": 36, "type": "SCALAR"}],
    }
    (d / "Crate.gltf").write_text(json.dumps(gltf), encoding="utf-8")
    (d / "Wave.bvh").write_text(
        "HIERARCHY\nROOT hip\n{\n  OFFSET 0 0 0\n  CHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation\n"
        "  JOINT abdomen\n  {\n    OFFSET 0 3 0\n    CHANNELS 3 Zrotation Xrotation Yrotation\n    End Site\n    {\n      OFFSET 0 5 0\n    }\n  }\n}\n"
        "MOTION\nFrames: 4\nFrame Time: 0.5\n0 40 0 0 0 0 0 0 0\n0 40 0 0 0 0 30 0 0\n0 40 0 0 0 0 -30 0 0\n0 40 0 0 0 0 0 0 0\n",
        encoding="utf-8")
    return d


def upload_window(run, what, secs=20):
    """The upload window up, the focus in its About the file; its text."""
    w = wait_focus(lambda d: d["id"] == "uploadAbout", secs)
    if not run.check(f"{what}: the upload window opens, the focus in About the file", w, label(w) if w else focus_label()):
        return None
    return value_of("id=uploadAbout")


def open_dialog_pick(run, what, path):
    """Windows' own Open window, as the person meets it: the path typed into its File name box, then Open."""
    box = wait(lambda: find("id=1148;type=Edit") or find("id=1148"), 20, 0.5)
    if not run.check(f"{what}: Windows' Open window comes up", box, focus_label()):
        return False
    u(f"set id=1148 | {path}")
    time.sleep(0.3)
    try:
        u("click id=1;type=Button")
    except ToolError:
        key("enter")
    return True


def uploads(run, nonce):
    """Uploading on her own (M6; docs/access-app.md > Uploading): a picture by Control+U and Windows' Open window, its
    size and resizing and see-through parts said before anything is sent, renamed, the fee confirmed in Notices by
    Enter (the run pretends pictures and models cost L$0: UPLOAD_FEES), said and selected in the inventory; a sound by
    a folder's Applications key, Upload a file here, its length, stereo and conversion said, landing in that folder,
    free (no confirmation); a model by //upload PATH, its triangles and size, a scale of 2 said as 2 metres, Weigh it,
    the confirmation cancelled once (nothing uploaded) then confirmed; an animation refused before asking (L$500 over the
    balance); a text file refused in words; Build > Upload a file's key shown; a sound over 30 seconds offered as clips
    (clips_upload), and a FLAC read by Windows. What it uploaded is moved into the run's folder and deleted by keys."""
    d = upload_fixtures(nonce)
    root = api("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
    folder_name = f"Access Uploads {nonce}"
    folder = api("TesselInventory", "createFolder", name=folder_name, parent_id=root)["folder_id"]
    pic, snd, model = f"Access Picture {nonce}", f"Access Sound {nonce}", f"Access Model {nonce}"
    made = []
    try:
        # ---- refused in words, nothing sent
        m = mark()
        say_in_app(f"//upload {d / 'Shopping list.txt'}")
        run.expect("a text file: said it isn't something Second Life takes", ["isn't something Second Life takes", "pictures"], m, 8)
        # ---- a FLAC, decoded by Windows: 40 seconds, offered as two clips; Escape uploads nothing
        flac = d / "Harp short.flac"
        if flac.exists():
            say_in_app(f"//upload {flac}")
            w = wait_focus(lambda e: e["id"] == "clipsAbout", 30)
            about = value_of("id=clipsAbout") if w else ""
            run.check("a 40-second FLAC: read by Windows, offered as 2 clips",
                      all(x in about for x in ("Harp short.flac is 40 seconds long", "as 2 clips", "FLAC", "read by Windows")), about[:300] or focus_label())
            if w:
                m = mark()
                key("esc")
                run.expect("Escape there: nothing uploaded", ["Nothing uploaded"], m, 8)
        else:
            print("     (no ffmpeg on this PC: the FLAC check skipped)")
        back_to_chat()

        # ---- the menu bar: Build > Upload a file, Control+U shown; Escape in the Open window uploads nothing
        key("alt+b")
        item = menu_open("Upload a file...")
        if run.check("Alt+B opens Build, with Upload a file", item, focus_label()):
            run.check("its key shown: Control+U", menu_keys("type=MenuItem;name=Upload a file...").endswith("Control+U"),
                      menu_keys("type=MenuItem;name=Upload a file..."))
            m = mark()
            key("u")
            if run.check("U there: Windows' Open window", wait(lambda: find("id=1148"), 20, 0.5), focus_label()):
                key("esc")
                run.expect("Escape there: nothing uploaded, said", ["Nothing uploaded"], m, 8)
        back_to_chat()

        # ---- a picture by Control+U from Say: the inventory shown once first, as its tree (not a search left showing,
        # where nothing new is selected), so the new item can be selected there
        clear_search()
        back_to_chat()
        m = mark()
        key("ctrl+u")
        if open_dialog_pick(run, "Control+U", d / "Sunset.png"):
            about = upload_window(run, "a picture") or ""
            run.check("it says the picture's size, what it's resized to, its see-through parts and fee",
                      all(w in about for w in ("a picture, PNG, 300 by 200 pixels", "resized to 256 by 128", "see-through", "L$0")), about[:300])
            key("tab")
            if run.check("Tab: Name, the file's name in it", wait_focus(lambda d: d["id"] == "uploadName", 3) and value_of("id=uploadName") == "Sunset",
                         f"{focus_label()}; {value_of('id=uploadName')!r}"):
                u("set id=uploadName |")
                type_text(pic, "id=uploadName")
                wait(lambda: value_of("id=uploadName") == pic, 3, 0.2)
                m = mark()
                key("enter", target="id=uploadName")
                confirm_in_notices(run, f"Upload {pic}, a picture, for L$0", m)
                run.expect("Enter confirms: uploaded, said with its size and folder, selected in the inventory",
                           [f"Uploaded {pic}, a picture, 256 by 128, into", "selected in your inventory"], m, 30)
                textures = api("LLInventory", "getBasicFolderID", ft_name="texture")["id"]
                got = wait(lambda: inv_items(textures).get(pic), 10)
                if run.check("it's in Textures", got, ""):
                    made.append(got)
                    back = wait_focus(lambda d: d["id"] == "chatInput", 4)
                    run.check("the focus back in Say", back, focus_label())
                    key("alt+4")
                    run.check("Alt+4: the new picture selected", wait(lambda: pic in selected_text("id=inventory"), 5), selected_text("id=inventory"))
        back_to_chat()

        # ---- a sound by a folder's Applications key: Upload a file here; free, into that folder, selected where she is
        key("alt+4")
        wait_focus(lambda d: d["id"] == "inventory", 4)
        # (in the tree, not a search's list: the new item lands in the folder, opened, and is selected there; selected by
        # UI Automation, as arrowing there passes whatever earlier runs left at the top and the folders the picture opened)
        try:
            u(f"select type=TreeItem;name~={folder_name}")
        except ToolError:
            pass
        if run.check("(the run's folder selected in the tree)", wait(lambda: folder_name in selected_text("id=inventory"), 3), selected_text("id=inventory")):
            key("apps")
            if run.check("a folder's Applications key: Upload a file here, with Control+U", menu_open("Upload a file here..."),
                         focus_label()) and menu_keys("type=MenuItem;name=Upload a file here...").endswith("Control+U"):
                key("u")
                if open_dialog_pick(run, "Upload a file here", d / "Door.wav"):
                    about = upload_window(run, "a sound") or ""
                    run.check("it says its length, stereo, made mono", all(w in about for w in ("a sound, 2 seconds, stereo", "48000 Hz", "made mono")), about[:300])
                    run.check("the folder offered first is the one chosen", value_of("id=uploadFolder").startswith(folder_name), value_of("id=uploadFolder"))
                    u("set id=uploadName |")
                    type_text(snd, "id=uploadName")
                    wait(lambda: value_of("id=uploadName") == snd, 3, 0.2)
                    m = mark()
                    key("enter", target="id=uploadName")
                    run.expect("uploaded, free, said (no confirmation)", [f"Uploaded {snd}, a sound, 2 seconds, into {folder_name}"], m, 30)
                    run.check("(no confirmation asked)", not any("Upload" in (e.get("text") or "") and "L$" in (e.get("text") or "") for e in said_since(m)), "")
                    got = wait(lambda: inv_items(folder).get(snd), 10)
                    if run.check("it's in the run's folder", got, ""):
                        made.append(got)
                    run.check("the tree's selection on it, the focus back in the tree",
                              wait(lambda: snd in selected_text("id=inventory") and (focused() or {}).get("id") == "inventory", 6),
                              f"{selected_text('id=inventory')}; {focus_label()}")
        back_to_chat()

        # ---- an animation refused before asking: L$500 over the balance
        m = mark()
        say_in_app(f"//upload {d / 'Wave.bvh'}")
        about = upload_window(run, "an animation") or ""
        run.check("it says its length and joints; priority and loop offered", "an animation, BVH" in about and find("id=uploadPriority") and find("id=uploadLoop"), about[:200])
        if about:
            m = mark()
            key("enter")
            run.expect("not enough L$: refused in words, nothing asked", ["Not enough L$", "L$500"], m, 15)

        # ---- a model by //upload: its size, a scale of 2, Weigh it, cancelled once, then confirmed
        say_in_app(f"//upload {d / 'Crate.gltf'}")
        about = upload_window(run, "a model") or ""
        run.check("it says its triangles and size", all(w in about for w in ("a model, glTF", "12 triangles", "About 1 metre along X")), about[:300])
        if about:
            u("setfocus id=uploadScale")
            u("set id=uploadScale |")
            m = mark()
            type_text("2", "id=uploadScale")
            run.expect("a scale of 2: the new size said", ["About 2 metres along X"], m, 8)
            m = mark()
            key("alt+w")
            got = run.expect("Weigh it: the land impact said (or that the grid gave none)", ["land impact"], m, 30)
            u("setfocus id=uploadName")
            u("set id=uploadName |")
            type_text(model, "id=uploadName")
            wait(lambda: value_of("id=uploadName") == model, 3, 0.2)
            m = mark()
            key("enter", target="id=uploadName")
            confirm_in_notices(run, f"Upload {model}, a model, for L$0", m, answer="esc")
            run.expect("Escape: cancelled, nothing uploaded", ["Cancelled"], m, 10)
            objects = api("LLInventory", "getBasicFolderID", ft_name="object")["id"]
            time.sleep(2)
            run.check("(nothing in Objects)", not inv_items(objects).get(model), "")
            back_to_chat()
            say_in_app(f"//upload {d / 'Crate.gltf'}")
            if upload_window(run, "the model again"):
                u("setfocus id=uploadName")
                u("set id=uploadName |")
                type_text(model, "id=uploadName")
                wait(lambda: value_of("id=uploadName") == model, 3, 0.2)
                m = mark()
                key("enter", target="id=uploadName")
                confirm_in_notices(run, f"Upload {model}, a model, for L$0", m)
                run.expect("Enter: the model uploaded, said", [f"Uploaded {model}, a model, into"], m, 60)
                got = wait(lambda: inv_items(objects).get(model), 10)
                if run.check("it's in Objects", got, ""):
                    made.append(got)
        back_to_chat()

        clips_upload(run, d, nonce, made)
    finally:
        back_to_chat()
        for item in made:
            api("TesselInventory", "move", ids=[item], folder_id=folder)
        delete_by_search(run, folder_name)
        back_to_chat()


def clips_upload(run, d, nonce, made):
    """A 75-second sound (a harp song: Second Life takes 30 seconds) by //upload, offered as clips: the window says the
    length, three clips of up to 29.9 seconds, their names and fees (L$10 each here, L$30 in all) and asks; a clip
    length of 20 says four clips; renamed, with the notecard; one confirmation in Notices for the whole set; each clip
    said as it lands, then the result with the new folder, the fee and the notecard. In Sounds a new folder named after
    them holds the three clips and the notecard (listing them in order); each clip's asset, read from the grid's store
    and decoded by ffmpeg where this PC has it, is the fixture's tone for that part, at most 30 seconds. The folder goes
    into made (cleanup)."""
    base = f"Access Clips {nonce}"
    sounds = api("LLInventory", "getBasicFolderID", ft_name="sound")["id"]
    say_in_app(f"//upload {d / 'Harp song.wav'}")
    w = wait_focus(lambda e: e["id"] == "clipsAbout", 30)
    if not run.check("a 75-second sound: the clips window, the focus in About the clips", w, label(w) if w else focus_label()):
        return
    about = value_of("id=clipsAbout")
    run.check("it says the length, the clips, their names and fees, and asks",
              all(x in about for x in ("Harp song.wav is 1 minute 15 seconds long", "3 clips of up to 29.9 seconds",
                                       "named Harp song 1 to Harp song 3", "L$10 each, L$30 in all", "Upload them as clips?",
                                       "made mono", "fade", "same length")), about[:500])
    send = (find("id=clipsSend") or [{}])[0].get("name", "")
    run.check("its Upload button says the count and total", send == "Upload as 3 clips for L$30", send)
    tree_check(run, "the clips window", must=("clipsAbout", "clipsName", "clipsLength", "clipsCuts", "clipsFolder", "clipsNotecard", "clipsSend"))

    u("setfocus id=clipsLength")
    u("set id=clipsLength |")
    m = mark()
    type_text("20", "id=clipsLength")
    run.expect("a clip length of 20: the new count said", ["4 clips of up to 20 seconds", "L$40 in all"], m, 8)
    u("set id=clipsLength |")
    type_text("29.9", "id=clipsLength")
    wait(lambda: "3 clips of up to 29.9" in value_of("id=clipsAbout"), 8, 0.3)
    run.check("the folder offered first: a new one named after them, in Sounds",
              value_of("id=clipsFolder").startswith("A new folder named after the clips, in Sounds"), value_of("id=clipsFolder"))
    u("setfocus id=clipsNotecard")
    key("space")
    run.check("(the notecard ticked)", wait(lambda: "toggle=On" in (find("id=clipsNotecard") or [{}])[0].get("raw", ""), 3, 0.2),
              (find("id=clipsNotecard") or [{}])[0].get("raw", ""))
    u("setfocus id=clipsName")
    u("set id=clipsName |")
    type_text(base, "id=clipsName")
    wait(lambda: value_of("id=clipsName") == base and f"{base} 1 to {base} 3" in value_of("id=clipsAbout"), 5, 0.2)

    m = mark()
    key("enter", target="id=clipsName")
    ask = f"Upload {base} as 3 clips, for L$30"
    confirm_in_notices(run, ask, m)
    for i in (1, 2, 3):
        run.expect(f"clip {i} of 3 said as it lands", [f"Clip {i} of 3 uploaded"], m, 60)
    run.expect("then the result: the clips, the new folder, the fee, the notecard",
               [f"Uploaded {base} as 3 clips, {base} 1 to {base} 3, 1 minute 15 seconds in all, into a new folder, {base}, in Sounds",
                "L$30 paid", f"A notecard, {base} clips, lists them in order"], m, 60)
    asks = {e.get("text") for e in said_since(m) if f"Upload {base}" in (e.get("text") or "") and "L$" in (e.get("text") or "")}
    run.check("one confirmation for the whole set", len(asks) == 1 and ask in next(iter(asks)), " | ".join(sorted(asks)))

    folder = wait(lambda: inv_items(sounds).get(base), 10)
    if not run.check("a new folder named after them, in Sounds", folder, ""):
        return
    made.append(folder)
    want = [f"{base} {i}" for i in (1, 2, 3)] + [f"{base} clips"]
    got = wait(lambda: (lambda r: r if all(n in r for n in want) else None)(inv_items(folder)), 15, 0.5) or inv_items(folder)
    run.check("in it the three clips and the notecard", sorted(got) == sorted(want), sorted(got))
    if f"{base} clips" in got:
        text = api("TesselInventory", "readNotecard", 20, item_id=got[f"{base} clips"]).get("text", "")
        run.check("the notecard lists the clips in order", text.split() == " ".join(want[:3]).split(), repr(text[:120]))
    items = (api("LLInventory", "getDirectDescendants", folder_id=folder).get("items") or {}).values()
    assets = {i.get("name"): i.get("asset_id") for i in items}
    for i, (tone, secs) in enumerate(zip(CLIP_TONES, (29.9, 29.9, 15.2)), 1):
        heard = asset_tone(assets.get(f"{base} {i}"))
        if heard is None:
            print("     (no ffmpeg on this PC: the clips' sound unchecked)")
            break
        length, freq = heard
        run.check(f"clip {i}'s sound: {secs} seconds of the {tone} Hz part", abs(length - secs) < 0.05 and length <= 30 and abs(freq - tone) < tone * 0.03,
                  f"{length:.3f} s, {freq:.0f} Hz")


def asset_tone(asset_id):
    """A sound asset's length in seconds and its tone (zero crossings over its middle, past the fades), read from the
    local grid's asset store and decoded by ffmpeg; None where ffmpeg isn't on this PC, (0, 0) for no such asset."""
    import array, sqlite3
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        return None
    db = sqlite3.connect(f"file:{ROOT / 'local' / os.environ.get('SOJOURN_GRID', 'grid') / 'bin' / 'Asset.db'}?mode=ro", uri=True)
    try:
        row = db.execute("select Data from assets where UUID = ?", (asset_id or "",)).fetchone()
    finally:
        db.close()
    if not row:
        return (0, 0)
    pcm = subprocess.run([ffmpeg, "-v", "error", "-i", "pipe:0", "-f", "s16le", "-ac", "1", "-ar", "44100", "pipe:1"],
                         input=row[0], capture_output=True, creationflags=CREATE_NO_WINDOW).stdout
    s = array.array("h")
    s.frombytes(pcm[:len(pcm) // 2 * 2])
    lo, hi = len(s) // 10, len(s) - len(s) // 10
    crossings = sum(1 for i in range(lo + 1, hi) if (s[i - 1] < 0) != (s[i] < 0))
    return len(s) / 44100, crossings / 2 / max(1e-9, (hi - lo) / 44100)


def two_panes(run, nonce):
    """The inventory's second pane (M2, a blind tester's second window): Alt+Shift+4 opens it (Alt+4 again in the first reads
    it again, as every Alt+number); there a folder is picked; back in the first (Alt+4), two notecards marked with Space
    go into it by Control+M; Control+W closes the second pane."""
    root = api("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
    a_name, b_name = f"Access Pane A {nonce}", f"Access Pane B {nonce}"
    a = api("TesselInventory", "createFolder", name=a_name, parent_id=root)["folder_id"]
    b = api("TesselInventory", "createFolder", name=b_name, parent_id=root)["folder_id"]
    for which in ("one", "two"):
        api("TesselInventory", "createNotecard", name=f"Access Fish {nonce} {which}", text="a fish", folder_id=a)
    if not run.check("(fixtures: two folders, two notecards in the first)", wait(lambda: len(inv_items(a)) == 2, 10), ""):
        return
    key("alt+4")
    wait_focus(lambda d: d["id"] == "inventory", 4)
    m = mark()
    key("alt+4")
    run.expect("Alt+4 again in the inventory reads it again", ["Inventory: "], m, 4)
    time.sleep(0.5)
    run.check("and opens no second pane", not find("id=inventory2Search"), focus_label())
    m = mark()
    key("alt+shift+4")
    d = wait_focus(lambda d: d["id"] == "inventory2", 4)
    run.check("Alt+Shift+4 opens the second pane", d, label(d) if d else focus_label())
    run.expect("saying how it works", ["Second pane", "Control+M"], m, 4)
    tree_check(run, "two inventory panes", must=("inventory", "inventorySearch", "inventory2", "inventory2Search", "inventory2Show"))
    key("ctrl+f")
    wait_focus(lambda d: d["id"] == "inventory2Search", 3)
    u("set id=inventory2Search |")
    type_text(b_name, "id=inventory2Search")
    wait(lambda: value_of("id=inventory2Search") == b_name, 3, 0.2)
    key("enter", target="id=inventory2Search")
    wait_focus(lambda d: d["id"] == "inventory2", 3)
    run.check("(the second pane on the folder to move into)", wait(lambda: b_name in selection("id=inventory2"), 5), selection("id=inventory2"))
    key("alt+4")
    d = wait_focus(lambda d: d["id"] == "inventory", 4)
    run.check("Alt+4 goes back to the first pane", d, label(d) if d else focus_label())
    inv_search(f"Access Fish {nonce}", "one")
    m = mark()
    key("space")
    key("down")
    key("space")
    run.expect("(two marked)", ["2 marked"], m, 4)
    m = mark()
    key("ctrl+m")
    run.expect("Control+M moves them into the folder in the other pane", [f"Moved 2 items into {b_name}"], m, 10)
    run.check("both are in it now", wait(lambda: len(inv_items(b)) == 2, 6), ", ".join(inv_items(b)) or "empty")
    key("alt+shift+4")
    wait_focus(lambda d: d["id"] == "inventory2", 4)
    m = mark()
    key("ctrl+w")
    d = wait_focus(lambda d: d["id"] == "inventory", 4)
    run.check("Control+W in the second pane closes it, back in the first", d, label(d) if d else focus_label())
    run.expect("(said)", ["Second pane closed"], m, 4)
    key("ctrl+f")
    key("esc", target="id=inventorySearch")
    for name in (a_name, b_name):
        delete_by_search(run, name)


def menu_keys(sel):
    """A menu item's access key and the key shown beside it (UI Automation's AccessKey | AcceleratorKey)."""
    m = re.search(r"^keys: (.*)$", u(f"get {sel}"), re.M)
    return m[1] if m else ""


def menu_open(name, secs=4):
    """The menu items in view now named so (a menu is open)."""
    return wait(lambda: find(f"type=MenuItem;name={name}"), secs)


def menu_bar(run, nonce, tester_id):
    """The review of 2026-10-30 (docs/access.md): the menu bar (Alt, Alt+letter, each item's key shown, items doing
    what their keys do, a focus-moving one landing), the context menus' keys, Control+1 to 9, Control+Shift+1, Escape
    from the notices back where Control+N came from, a notice's keys on the Applications key, an IM arriving while the
    focus is on the "no conversations" line, Control+Page Up and Down as the Control+Tab ring, the status bar."""
    back_to_chat()
    # ---- the menu bar
    # (Alt alone and F10 aren't checked here: WinForms opens the menu bar on them only for the foreground window
    # (ToolStripManager.ProcessMenuKey), and on a hidden desktop that's never ours, so Windows' own system menu opens
    # instead; Alt+letter goes by the menu's mnemonics, which don't ask)
    key("alt+w")
    item = menu_open("Region info")
    if run.check("Alt+W opens World", item, focus_label()):
        keys = {n: menu_keys(f"type=MenuItem;name={n}") for n in ("Region info", "Teleport home", "Away", "Who's in voice")}
        run.check("its items show their keys (Region info, Control+Shift+1; Teleport home, Control+Shift+H)",
                  keys["Region info"].endswith("Control+Shift+1") and keys["Teleport home"].endswith("Control+Shift+H")
                  and keys["Who's in voice"].endswith("Control+Shift+V"), json.dumps(keys))
        m = mark()
        key("r")
        run.expect("R there: region info, as Radegast's", [f"Region {REGION}"], m, 8)
    back = wait_focus(lambda d: d["id"] == "chatInput", 3)
    run.check("and the focus is back in Say", back, focus_label())
    m = mark()
    key("ctrl+shift+1")
    run.expect("Control+Shift+1: region info", [f"Region {REGION}"], m, 8)
    # Away by the menu, through SojournSettings, and back
    for want, words in (("on", "Away."), ("off", "Not away.")):
        m = mark()
        key("alt+w")
        if menu_open("Away"):
            key("a")
            run.expect(f"World > Away: {want}, said", [words], m, 6)
            run.check(f"(away {want})", wait(lambda: setting("away") == want, 4), str(setting("away")))
    # a focus-moving item (View > People) lands once the menu has closed
    key("alt+v")
    if run.check("Alt+V opens View", menu_open("People"), focus_label()):
        run.check("View shows the areas' keys (People, Alt+3)", menu_keys("type=MenuItem;name=People").endswith("Alt+3"),
                  menu_keys("type=MenuItem;name=People"))
        key("p")
        d = wait_focus(lambda d: d["id"] == "people" or d["type"] == "ListItem", 4)
        run.check("P there goes to People nearby", d and (d["id"] == "people" or VNAME in d["name"] or "Nobody" in d["name"]), label(d) if d else focus_label())
    back_to_chat()
    key("alt+h")
    if run.check("Alt+H opens Help", menu_open("Keys"), focus_label()):
        key("k")
        d = wait_focus(lambda d: d["id"] == "keysText", 4)
        run.check("K there opens the keys", d, label(d) if d else focus_label())
        key("esc")
    back_to_chat()

    # ---- the context menus show their keys: the inventory's Cut, Rename
    key("alt+4")
    if wait_focus(lambda d: d["id"] == "inventory", 4):
        time.sleep(1)
        key("down")
        time.sleep(0.3)
        key("apps")
        if menu_open("Rename"):
            run.check("an inventory item's menu shows Cut, Control+X and Rename, F2",
                      menu_keys("type=MenuItem;name=Cut").endswith("Control+X") and menu_keys("type=MenuItem;name=Rename").endswith("F2"),
                      f'Cut: {menu_keys("type=MenuItem;name=Cut")}; Rename: {menu_keys("type=MenuItem;name=Rename")}')
            key("esc")
        else:
            run.check("an inventory item's menu opens", False, focus_label())
    back_to_chat()

    # ---- Control+1 to 9: the last nine messages, 1 the newest
    m = mark()
    for word in ("first", "second", "third"):
        v(f"say recent {word} {nonce}")
        time.sleep(0.5)
    if run.expect("(three lines arrive)", [f"recent third {nonce}"], m, 10):
        time.sleep(1)
        for k, word in (("ctrl+1", "third"), ("ctrl+3", "first")):
            m = mark()
            key(k)
            run.expect(f"{k.title()}: the {'newest' if k == 'ctrl+1' else 'third newest'} message", [f"recent {word} {nonce}"], m, 4)
        m = mark()
        key("ctrl+oem_close")
        run.expect("Control+] then goes on from there", [f"recent second {nonce}"], m, 4)

    # ---- notices: Escape back where Control+N came from; a notice's keys on the Applications key
    for area, where, home in (("alt+1", "Say", "chatInput"), ("alt+3", "People nearby", None)):
        back_to_chat()
        key(area)
        start = wait_focus(lambda d: d["id"] == home if home else d["id"] == "people" or d["type"] == "ListItem", 4)
        m = mark()
        v(f"fmenu {tester_id}")
        if not run.expect(f"(a menu announced, in {where})", ["Access Menu"], m, NOTICE_SECS):
            continue
        time.sleep(1.5)
        if open_newest_notice(run, ["Access Menu"]):
            if where == "Say":
                key("apps")
                green = menu_open("Green")
                if run.check("the Applications key on a notice lists its keys", green, focus_label()):
                    run.check("each with the key that presses it (Green, 2; Ignore, Delete)",
                              menu_keys("type=MenuItem;name=Green").endswith("2") and menu_keys("type=MenuItem;name=Ignore").endswith("Delete"),
                              f'{menu_keys("type=MenuItem;name=Green")}; {menu_keys("type=MenuItem;name=Ignore")}')
                    key("esc")
                    time.sleep(0.5)
            key("esc")
            d = wait_focus(lambda d: d["id"] == home if home else d["id"] == "people" or (d["type"] == "ListItem" and "metre" in d["name"]), 4)
            run.check(f"Escape on the notices goes back to {where}, where Control+N came from", d, label(d) if d else focus_label())
            open_newest_notice(run, ["Access Menu"])
            key("del")
            time.sleep(0.5)
    back_to_chat()

    # ---- an IM arriving while the focus is on "No conversations yet"
    go_conversations()
    d = wait_focus(lambda d: d["id"] == "noConversations", 4)
    if run.check("(View > Conversations with none open: the no-conversations line)", d, label(d) if d else focus_label()):
        m = mark()
        v(f"im {tester_id} while you read {nonce}")
        run.expect("(the IM announced)", [f"while you read {nonce}"], m, 10)
        time.sleep(2)
        f = focused()
        run.check("the focus stays on that line", f and f["id"] == "noConversations", label(f) if f else "nothing")
        moved = [e for e in focus_events_since(m) if re.search(r"Message to|conv", e.get("text") or "", re.I)]
        run.check("no focus event to the new conversation", not moved, "; ".join(e.get("text") or "" for e in moved[:3]) or "none")
    # ---- Control+Page Down and Up: the same ring as Control+Tab
    back_to_chat()
    key("ctrl+pgdn")
    d = wait_focus(lambda d: d["id"] == "convInput", 4)
    run.check("Control+Page Down: to the next in the ring, the conversation", d, label(d) if d else focus_label())
    key("ctrl+pgup")
    d = wait_focus(lambda d: d["id"] == "chatInput", 4)
    run.check("Control+Page Up: back to nearby chat", d, label(d) if d else focus_label())
    key("ctrl+tab")
    if wait_focus(lambda d: d["id"] == "convInput", 4):
        key("ctrl+w")
    back_to_chat()

    # ---- the status bar: what NVDA+End reads (a status bar, its text the location)
    bar = find("type=StatusBar")
    where = find("type=Text;name~=Location: ")
    run.check("a status bar holding the location (NVDA+End)", bar and where and REGION in where[0]["name"],
              f'{label(bar[0]) if bar else "no status bar"}; {label(where[0]) if where else "no location text"}')


def window_title():
    line = next((l for l in u(f"windows {APP_PID}").splitlines() if '"Sojourn Access' in l), "")
    m = re.search(r'"(Sojourn Access[^"]*)"', line)
    return m[1] if m else line


def incognito(run, nonce):
    """Incognito's keys (M1): Ctrl+Shift+I on, said; the title leaves out the place; a program can't turn it off
    (SojournSettings refuses); nothing a visitor says then reaches the session log; Alt+0 shows it on; Ctrl+Shift+I
    off, said, and the place back in the title."""
    m = mark()
    key("ctrl+shift+i")
    run.expect("Ctrl+Shift+I turns incognito on, said", ["Incognito on"], m, 5)
    run.check("the title leaves out the place", wait(lambda: "incognito" in window_title() and REGION not in window_title(), 4), window_title())
    try:
        r = api("SojournSettings", "set", incognito=False)
    except Exception as e:
        r = {"error": str(e)}
    run.check("a program can't turn it off (SojournSettings refuses)", "error" in json.dumps(r) or "refus" in json.dumps(r).lower()
              or api("SojournPrivate", "status").get("on"), json.dumps(r)[:160])
    secret = f"incognito words {nonce}"
    m = mark()
    v(f"say {secret}")
    run.expect("(what's said nearby is still read out)", [secret], m, 8)
    key("alt+0")
    wait_focus(lambda d: d["name"] == "Settings" or d["type"] == "ListItem", 4)
    row = wait(lambda: find("type=ListItem;name~=Incognito (Control+Shift+I): on"), 5)
    run.check("Alt+0 shows it on", row, " | ".join(n for n in list_items("id=settings") if "ncognito" in n))
    back_to_chat()
    m = mark()
    key("ctrl+shift+i")
    run.expect("Ctrl+Shift+I again turns it off (the person's own key), said", ["Incognito off"], m, 5)
    run.check("the place back in the title", wait(lambda: REGION in window_title(), 6), window_title())
    logs = list((OUT / "app" / "logs").glob("*.log"))
    leaked = [p.name for p in logs if secret in p.read_text(encoding="utf-8", errors="replace")]
    run.check("nothing said during it reached the session log", logs and not leaked, ", ".join(leaked) or f"{len(logs)} logs clean")


def voice_keys(run):
    """Voice chat's keys (M1). Without voice running (the test's --no-voice) F4 and Ctrl+Shift+V say so; with it
    (ACCESS_VOICE=1, the local voice server up) F4 held opens the mic to residents and letting go closes it,
    Shift+F4 switches to an open mic and back, Ctrl+Shift+V says who's in voice."""
    on = os.environ.get("ACCESS_VOICE") == "1"
    if not on:
        m = mark()
        key("f4")
        run.expect("F4 without voice says it isn't running", ["Voice chat isn't running"], m, 4)
        m = mark()
        key("ctrl+shift+v")
        run.expect("so does Ctrl+Shift+V", ["Voice chat isn't running"], m, 4)
        return
    def status():
        try:
            return api("SojournVoice", "status")
        except Exception as e:
            return {"error": str(e)}
    st = wait(lambda: (lambda s: s if "connected" in str(s.get("status", "")).lower() else None)(status()), 40, 1)
    run.check("(voice connected)", st, json.dumps(status())[:200])
    t0 = time.time()
    u("keydown focus | 115")
    time.sleep(1)
    held = status()
    u("keyup focus | 115")
    time.sleep(1)
    let_go = status()
    run.check("F4 held opens the mic; let go, it closes", held.get("mic_live") and not let_go.get("mic_live"),
              f"held: {json.dumps(held)[:160]}; let go: {json.dumps(let_go)[:160]}")
    clicks = [c["cue"] for c in cues_since(t0, "MicOn", "MicOff")]
    run.check("a click as the mic opens and another as it closes", clicks == ["MicOn", "MicOff"], ", ".join(clicks) or "none")
    m = mark()
    key("shift+f4")
    run.expect("Shift+F4 switches to an open mic, muted", ["Open mic, muted"], m, 4)
    m = mark()
    key("shift+f4")
    run.expect("and back to push to talk", ["Push to talk"], m, 4)
    m = mark()
    key("ctrl+shift+v")
    run.expect("Ctrl+Shift+V says who's in voice", ["voice"], m, 4)


def walk(run):
    m = mark()
    say_in_app(WALK_COMMAND)
    run.expect(f"{WALK_COMMAND} starts walking, saying so", ["Walking", "66, 36"], m, 8)
    done = run.expect("and says when it's finished", ["Finished walking"], m, 40)
    if done:
        x, y, z, _ = tester_where()
        print(f"     (as it said so: at {x:.1f}, {y:.1f}, {math.dist((x, y), WALK_TO):.1f} m from {WALK_TO})")
    time.sleep(3)
    x, y, z, _ = tester_where()
    off = math.dist((x, y), WALK_TO)
    run.check("3 s later the tester is within 1 m of it", off < 1, f"at {x:.1f}, {y:.1f}: {off:.1f} m from {WALK_TO}")


def teleport(run):
    # home set where the tester stands if the parcel allows (the agent API's setHome), away by //tp, back by Control+Shift+H (Radegast's
    # Teleport Home: said as it goes, the arrival said), then //tp again for the steps after
    home = tester_where()
    set_home = api("TesselAgent", "setHome", timeout=15)
    m = mark()
    say_in_app(TP_COMMAND)
    run.expect(f"{TP_COMMAND} arrives, saying where", ["Arrived"], m, 40)
    tail = history_tail()
    run.check("the arrival is a line in nearby chat's history too", re.search(r"Arrived in Atelier, at \d+, \d+, \d+\.\s*$", tail), tail[-200:])
    x, y, z, _ = tester_where()
    off = math.dist((x, y), TP_TO[:2])
    run.check("the tester is there", off < 3, f"at {x:.1f}, {y:.1f}: {off:.1f} m from {TP_TO[:2]}")
    time.sleep(2)
    back_to_chat()
    m = mark()
    key("ctrl+shift+h")
    run.expect("Control+Shift+H: teleporting home, said", ["Teleporting home."], m, 4)
    got = wait(lambda: next((e["text"] for e in said_since(m) if re.search(r"Arrived|Couldn't go home", e.get("text") or "")), None), 45)
    time.sleep(1)
    x, y, z, _ = tester_where()
    off = math.dist((x, y), home[:2])
    if "not allowed" in str(set_home):
        # (Atelier's parcel doesn't let the tester set home there: then the refusal, in plain words, is what's checked)
        run.check("and where it ended said (home can't be set here, so the grid's refusal)", got, got or "nothing said after it")
    else:
        run.check("and the arrival said, home where it was set", got and got.startswith("Arrived") and off < 4,
                  f"{got or 'nothing said'}; {off:.1f} m from home (setHome: {set_home})")
    time.sleep(2)
    m = mark()
    say_in_app(TP_COMMAND)
    run.expect("(back by //tp for the steps after)", ["Arrived"], m, 40)


def pilot_walk(run):
    """The assistant's go tool walks by the session's own autopilot (LLAgent startAutoPilot, stop_distance 1.5): with no
    frame loop it steered by the position last heard and ran on past its stop distance (Motion.DeadReckon; with
    SOJOURN_DEAD_RECKON=0, 9 m ended 0.4 m from the target instead of ~1.5)."""
    x, y, z, _ = tester_where()
    start = (x, y)
    gx, gy = (lambda r: (r["global"][0] - r["region"][0], r["global"][1] - r["region"][1]))(api("LLAgent", "getPosition"))
    api("LLAgent", "startAutoPilot", target_global=[gx + PILOT_TO[0], gy + PILOT_TO[1], z], stop_distance=PILOT_STOP)
    time.sleep(1)
    ended = wait(lambda: not api("LLAgent", "getAutoPilot")["enabled"], 30, 0.3)
    time.sleep(2.5)   # coasting to a stop
    x, y, z, _ = tester_where()
    off = math.dist((x, y), PILOT_TO)
    # past the target along the walk: the overshoot, negative when it stopped short
    d = math.dist(start, PILOT_TO)
    along = ((x - start[0]) * (PILOT_TO[0] - start[0]) + (y - start[1]) * (PILOT_TO[1] - start[1])) / d
    # it lets go at the stop distance and coasts a little; steering by a stale position it ran on toward the target
    run.check("the assistant's walk (the session's autopilot) stops about its stop distance short",
              ended and PILOT_STOP - 0.6 <= off <= PILOT_STOP + 0.75 and along - d <= 0,
              f"{'ended' if ended else 'still going after 30 s'}; walked {d:.1f} m from {start[0]:.0f},{start[1]:.0f}: "
              f"{off:.1f} m from {PILOT_TO}, {along - d:+.1f} m past it")


# ---- sound as a map (M4) ------------------------------------------------------------------------------------------------

MARKS_AT = (60, 44)                # where the tester stands, facing north, for the marks and the beacon
MARKS = (("Marks Bench", "rez", (0, 4), "seat"), ("Marks Gate", "rez", (4, 0), "door"), ("Marks Bell", "facebox", (-4, 0), "touch"))


def cues_since(t0, *names):
    """The cues walk mode's sounds sent since time.time() t0 (SojournWalk status' recent), of these names if given."""
    ms = (time.time() - t0) * 1000 + 50   # (and a moment before: the status op's own latency)
    return [c for c in api("SojournWalk", "status").get("recent") or []
            if c["ms_ago"] <= ms and (not names or c["cue"] in names)]


def cue_after(t0, names, secs):
    return wait(lambda: (cues_since(t0, *names) or [None])[-1], secs, 0.2)


def off_by(a, b):
    return abs((a - b + 180) % 360 - 180)


def setting(name, value=None):
    if value is not None:
        api("SojournSettings", "set", **{name: value})
    return api("SojournSettings", "get", name=name)["settings"][name]["value"]


def own_im_sound(run, tester_id):
    """A sound of its own for someone's IMs (docs/access-app.md > Earcons): chosen by ear from the visitor's
    Applications key in People nearby, each sound played as the arrows reach it; heard in place of the usual IM sound
    when the visitor IMs; said and set by //imsound; taken back to the usual one from the conversation's own
    Applications key, after which the visitor's IMs ring the usual bells again."""
    visitor = visitor_id()
    back_to_chat()
    run.check("(nobody has a sound of their own yet)", setting("access_im_sounds") == {}, json.dumps(setting("access_im_sounds")))
    key("shift+tab")
    wait_focus(lambda d: d["id"] == "chatPeople" or d["type"] == "ListItem", 4)
    # (the nearest, and the only one there: the tester's other friends are a region away)
    on = wait(lambda: next((c for c in find("type=ListItem;focused=1") if VNAME in c["name"]), None), 15, 0.5)
    if not run.check("(People nearby beside chat, on the visitor)", on, label(on) if on else focus_label()):
        return
    key("apps")
    item = menu_open(f"IM sound for {VNAME}...")
    if not run.check("the Applications key on a person: IM sound for them", item, "; ".join(m["name"] for m in find("type=MenuItem")) or "no menu"):
        key("esc")
        return
    key("s")
    d = wait_focus(lambda d: d["id"] == "soundList" or (d["type"] == "ListItem" and "usual" in d["name"]), 5)
    run.check("S opens the list of sounds, on the usual one, said in use", d and "the usual IM sound, in use" in selection("id=soundList"),
              (label(d) + " | " + selection("id=soundList")) if d else focus_label())
    t0 = time.time()
    picked = select_in("id=soundList", "ocean wave")
    time.sleep(0.5)
    heard = [c["cue"] for c in cues_since(t0) if c["cue"].startswith("Im")]
    run.check("each sound plays as the arrows reach it", picked and heard[:7] == ["ImBird", "ImCricket", "ImHarp", "ImHeartbeat", "ImHorn",
              "ImMusicBox", "ImWave"], f"selected {selection('id=soundList')}; played {heard}")
    m = mark()
    key("enter")
    run.expect("Enter chooses it, said", [f"IMs from {VNAME}: ocean wave."], m, 5)
    run.check("kept by the visitor's key", setting("access_im_sounds") == {visitor: "ocean wave"}, json.dumps(setting("access_im_sounds")))
    back_to_chat()

    t0 = time.time()
    v(f"im {tester_id} a wave for you")
    c = cue_after(t0, ("ImWave",), 10)
    run.check("an IM from the visitor plays the ocean wave, at the IM's level", c and abs(c["level"] - 0.45) < 0.01 and c["bearing"] is None,
              json.dumps(c) if c else f"not played; played: {[x['cue'] for x in cues_since(t0)]}")
    run.check("and not the usual IM sound", not cues_since(t0, "EarIm"), json.dumps(cues_since(t0)))

    m = mark()
    say_in_app(f"//imsound {VNAME}")
    run.expect("//imsound NAME says theirs", [f"IMs from {VNAME}: ocean wave."], m, 5)
    m = mark()
    say_in_app(f"//imsound {VNAME} harp")
    run.expect("//imsound NAME SOUND gives them another", [f"IMs from {VNAME}: harp."], m, 5)
    run.check("(kept)", setting("access_im_sounds") == {visitor: "harp"}, json.dumps(setting("access_im_sounds")))

    # from the conversation's tab, back to the usual sound
    key("ctrl+tab")
    if not run.check("(the conversation's message box)", wait_focus(lambda d: d["id"] == "convInput", 4), focus_label()):
        return
    key("apps")
    if not run.check("the Applications key in a conversation: IM sound for them", menu_open(f"IM sound for {VNAME}..."),
                     "; ".join(m["name"] for m in find("type=MenuItem")) or "no menu"):
        key("esc")
        return
    key("s")
    wait_focus(lambda d: d["id"] == "soundList" or d["type"] == "ListItem", 5)
    run.check("the list opens on the one in use", "harp, in use" in selection("id=soundList"), selection("id=soundList"))
    t0 = time.time()
    key("home")
    c = cue_after(t0, ("EarIm",), 3)
    run.check("the usual one, first, plays the IM's bells", c, json.dumps(cues_since(t0)))
    m = mark()
    key("enter")
    run.expect("Enter takes it back to the usual sound", [f"IMs from {VNAME}: the usual sound."], m, 5)
    run.check("(no longer kept)", setting("access_im_sounds") == {}, json.dumps(setting("access_im_sounds")))
    d = wait_focus(lambda d: d["id"] == "convInput", 3)
    run.check("the focus back in the message box", d, label(d) if d else focus_label())
    key("ctrl+w")
    back_to_chat()
    t0 = time.time()
    v(f"im {tester_id} the usual again")
    c = cue_after(t0, ("EarIm",), 10)
    run.check("the visitor's IMs ring the usual bells again", c and not [x for x in cues_since(t0) if x["cue"].startswith("Im")],
              json.dumps(cues_since(t0)))
    key("ctrl+tab")
    if wait_focus(lambda d: d["id"] == "convInput", 4):
        key("ctrl+w")
    back_to_chat()


def sound_map(run, tester_id):
    """M4 (docs/access-app.md > Earcons, Beacon, Marks): which sound starts for what arrives (an IM, none for the
    conversation you're in, a script menu, an offer, nearby chat placed toward the speaker once it's on), a changed
    sound's row in Alt+0 playing it, a beacon placed toward someone, toward a thing you walk to by hand until "Reached",
    toward a //go's destination and the assistant's walk's while they last, and O's marks: a seat, a gate and a
    touchable bell around you by name, kind and clock face, each sounded from where it is. What the sounds sound like is
    for a person to judge; this checks only that the right one starts, where it should."""
    defaults = {n: setting(n) for n in ("earcon_im", "earcon_nearby", "earcon_menu", "walk_beacon", "walk_beacon_volume", "access_sounds_volume")}
    run.check("the earcons' and beacon's defaults (IM and menus on, nearby off; the beacon quiet)",
              defaults == {"earcon_im": "medium", "earcon_nearby": "off", "earcon_menu": "medium", "walk_beacon": "both",
                           "walk_beacon_volume": "quiet", "access_sounds_volume": "100%"}, json.dumps(defaults))

    # a known layout, whatever earlier steps left (a stalled route can leave the tester 25 m off): the tester at the
    # marks' spot, the visitor 8 m off it and within 20 m of its fixture box (it tells the box on channel 7)
    back_to_chat()
    m = mark()
    say_in_app("//tp %s/%d/%d/%d" % (REGION, MARKS_AT[0], MARKS_AT[1], TESTER_START[2]))
    if not run.expect("to the marks' spot", ["Arrived"], m, 30):
        return
    v(f"tp {REGION} {MARKS_AT[0] + 6} {MARKS_AT[1] - 6} {TESTER_START[2]}", timeout=30)
    time.sleep(3)

    # ---- earcons
    t0 = time.time()
    v(f"im {tester_id} a sound for this")
    c = cue_after(t0, ("EarIm",), 10)
    run.check("an IM starts the IM's sound, centred", c and c["bearing"] is None, json.dumps(c) if c else
              f"not played; played: {[x['cue'] for x in cues_since(t0)]}")
    key("ctrl+tab")
    if wait_focus(lambda d: d["id"] == "convInput", 4):
        time.sleep(0.5)
        t0 = time.time()
        m = mark()
        v(f"im {tester_id} read where you are")
        run.expect("an IM in the conversation you're in is read", ["read where you are"], m, 10)
        time.sleep(1)
        run.check("and starts no sound", not cues_since(t0, "EarIm"), json.dumps(cues_since(t0)))
        key("ctrl+w")
    back_to_chat()

    for what, cmd, cue in (("a script menu", "fmenu", "EarMenu"), ("an offer (a notecard given)", "offer", "EarOffer")):
        t0 = time.time()
        v(f"{cmd} {tester_id}")
        c = cue_after(t0, (cue,), NOTICE_SECS)
        run.check(f"{what} starts its sound", c, json.dumps(c) if c else f"not played; played: {[x['cue'] for x in cues_since(t0)]}")
        key("ctrl+n")
        if wait_focus(lambda d: d["id"] == "notices" or d["type"] == "ListItem", 4):
            key("del")
            time.sleep(0.5)
        back_to_chat()

    t0 = time.time()
    v("say nobody hears a sound for this")
    time.sleep(2.5)
    run.check("nearby chat starts no sound by default", not cues_since(t0, "EarNearby"), json.dumps(cues_since(t0)))
    setting("earcon_nearby", "medium")
    try:
        t0 = time.time()
        v("say now it sounds from here")
        c = cue_after(t0, ("EarNearby",), 8)
        x, y, _, heading = tester_where()
        vx, vy, _ = visitor_where()
        _, want = clock(vx - x, vy - y, heading)
        run.check("turned on, nearby chat sounds from where the speaker is",
                  c and c["bearing"] is not None and off_by(c["bearing"], want) < 25,
                  f"placed at {c['bearing']} degrees, the visitor at {want:.0f}" if c else "not played")
    finally:
        setting("earcon_nearby", "off")

    key("alt+0")
    if wait_focus(lambda d: d["name"] == "Settings" or d["type"] == "ListItem", 4) and select_in("id=settings", "IM sound"):
        t0 = time.time()
        m = mark()
        key("enter")
        run.expect("Alt+0: Enter on the IM's sound turns it up, saying so", ["IM sound: loud."], m, 5)
        c = cue_after(t0, ("EarIm",), 3)
        run.check("and plays it at the new level", c and abs(c["level"] - 0.7) < 0.01, json.dumps(c))
        for _ in range(4):   # full, off, quiet, medium: back where it was
            key("enter")
            time.sleep(0.6)
        run.check("and round again to medium", setting("earcon_im") == "medium", setting("earcon_im"))
    else:
        run.check("Alt+0 lists the IM's sound", False, selection("id=settings"))
    # the mic's click (push to talk, docs/access-app.md > Voice chat): on by default, its row plays it
    run.check("the mic's click is on by default (medium)", setting("earcon_mic") == "medium", setting("earcon_mic"))
    if select_in("id=settings", "Mic opening and closing"):
        t0 = time.time()
        key("enter")
        c = cue_after(t0, ("MicOn",), 3)
        run.check("Alt+0: the mic's row plays its click at the new level", c and abs(c["level"] - 0.7) < 0.01, json.dumps(c))
        for _ in range(4):
            key("enter")
            time.sleep(0.6)
        run.check("and round again to medium", setting("earcon_mic") == "medium", setting("earcon_mic"))
    else:
        run.check("Alt+0 lists the mic's click", False, selection("id=settings"))
    back_to_chat()

    # ---- the marks and a beacon walked to by hand
    x, y, z, _ = tester_where()
    made = []
    try:
        for name, how, (dx, dy), _ in MARKS:
            r = v(f"{how} {name} {x + dx:.1f} {y + dy:.1f} {z - 0.6:.1f}", timeout=40)
            if "id " in r:
                made.append(name)
        run.check("a seat, a gate and a touchable bell put around the tester", len(made) == 3, ", ".join(made))
        m = mark()
        say_in_app("//face 90")
        run.expect("facing north", ["Facing", "north"], m, 8)
        time.sleep(3)   # the objects' names and flags arrive

        key("f3")
        wait_focus(lambda d: d["id"] == "walkPad", 5)
        t0 = time.time()
        m = mark()
        key("o")
        got = run.expect("O names the doors, seats and touchable things around", ["Marks Bench, a seat", "Marks Gate, a door", "Marks Bell, touchable"], m, 8)
        text = (got or {}).get("text") or ""
        run.check("each by its clock face (bench ahead, gate right, bell left)",
                  re.search(r"Marks Bench, a seat, 4 metres, twelve o'clock", text) and re.search(r"Marks Gate, a door, 4 metres, three o'clock", text)
                  and re.search(r"Marks Bell, touchable, 4 metres, nine o'clock", text), text)
        time.sleep(3.5)
        # Each cue against the item said in its place in the line (they go in the same order), so a second seat in
        # range can't stand in for the bench: the clock words were checked above, the sounds must agree with them.
        sent = cues_since(t0, "MarkSeat", "Door", "MarkTouch")
        said = re.findall(r"([^.]+?), (a door|a seat|touchable), [^,]+, (\w+) o'clock\.", text)
        kinds = {"a door": "Door", "a seat": "MarkSeat", "touchable": "MarkTouch"}
        pairs = [(n.strip(), c["cue"], kinds[k], c["bearing"], HOURS.index(h) * 30 if h in HOURS else None)
                 for (n, k, h), c in zip(said, sent)]
        run.check("and each sounded from where it is said to be (a seat sinks, the gate knocks, the bell taps)",
                  len(said) >= 3 and len(sent) == len(said)
                  and all(cue == want_cue and b is not None and w is not None and off_by(b, w) < 20 for _, cue, want_cue, b, w in pairs),
                  f"{len(sent)} cues for {len(said)} said: "
                  + "; ".join(f"{n}: {cue} at {b} (said {w})" for n, cue, _, b, w in pairs))

        m = mark()
        key("b")
        run.expect("B with no beacon says so", ["No beacon"], m, 5)
        key("esc")
        wait_focus(lambda d: d["id"] == "chatInput", 5)
        m = mark()
        say_in_app(f"//beacon {VNAME}")
        run.expect("//beacon puts one on the visitor", ["Beacon on " + VNAME], m, 8)
        t0 = time.time()
        time.sleep(3.5)
        pings = cues_since(t0, "Beacon", "BeaconAhead")
        x, y, _, heading = tester_where()
        vx, vy, _ = visitor_where()
        _, want_b = clock(vx - x, vy - y, heading)
        run.check("its beacon pips from the visitor's direction, quietly",
                  len(pings) >= 2 and all(c["bearing"] is not None and off_by(c["bearing"], want_b) < 20 and c["level"] <= 0.21 for c in pings),
                  f"{len(pings)} pips at {[c['bearing'] for c in pings]} (the visitor at {want_b:.0f}), levels {[c['level'] for c in pings]}")
        m = mark()
        say_in_app("//beacon Marks Bench")
        run.expect("//beacon moves it to the bench", ["Beacon on Marks Bench"], m, 8)
        key("f3")
        wait_focus(lambda d: d["id"] == "walkPad", 5)
        m = mark()
        key("b")
        run.expect("B says where it is", ["Beacon on Marks Bench", "twelve o'clock"], m, 5)
        t0 = time.time()
        time.sleep(1.6)
        ahead = cues_since(t0, "BeaconAhead")
        run.check("straight ahead it pips brighter (a fifth above)", ahead, json.dumps(cues_since(t0)))
        m = mark()
        u("keydown focus | 38")
        reached = walk_said(m, ["Reached Marks Bench"], 10)
        u("keyup focus | 38")
        run.check("walking to it by hand: Reached, said with its sound", reached and cues_since(t0, "BeaconReached"),
                  reached["text"] if reached else f"not said; {walk_pos()}")
        m = mark()
        key("shift+b")
        run.expect("Shift+B with it gone: nothing to take off", ["No beacon to take off"], m, 5)
        key("esc")
        wait_focus(lambda d: d["id"] == "chatInput", 5)

        # a //go's destination, then the assistant's walk's
        x, y, z, _ = tester_where()
        gx, gy = x - 6, y - 3
        t0 = time.time()
        m = mark()
        say_in_app(f"//go xyz {gx:.0f},{gy:.0f}")
        mid = wait(lambda: (lambda b: b if b and b.get("name", "").startswith(f"{gx:.0f}, {gy:.0f}") else None)(api("SojournWalk", "status").get("beacon")), 5, 0.2)
        run.check("a //go puts the beacon on where it's going", mid, json.dumps(api("SojournWalk", "status").get("beacon")))
        done = run.expect("the walk ends", ["Finished walking"], m, 30)
        pings = cues_since(t0, "Beacon", "BeaconAhead")
        run.check("pips sounded while walking", len(pings) >= 2, f"{len(pings)} pips")
        time.sleep(2)
        t1 = time.time()
        time.sleep(2)
        run.check("and stop once it's over", done and not cues_since(t1, "Beacon", "BeaconAhead") and api("SojournWalk", "status").get("beacon") is None,
                  json.dumps(api("SojournWalk", "status").get("beacon")))
        x, y, z, _ = tester_where()
        g = api("LLAgent", "getPosition")["global"]
        t0 = time.time()
        api("LLAgent", "startAutoPilot", target_global=[g[0] + 6, g[1] + 3, z], stop_distance=1.0)
        b = wait(lambda: api("SojournWalk", "status").get("beacon"), 3, 0.1)
        run.check("the assistant's walk (the session's autopilot) has the beacon too", b and "assistant" in b["name"], json.dumps(b))
        wait(lambda: not api("LLAgent", "getAutoPilot")["enabled"], 20, 0.3)
        run.check("pipping as it went", cues_since(t0, "Beacon", "BeaconAhead"), f"{len(cues_since(t0, 'Beacon', 'BeaconAhead'))} pips")
    finally:
        back_to_chat()
        for name in made:
            try:
                v(f"derez {name}", timeout=15)
            except Exception:
                pass


# ---- walk mode (M3) ------------------------------------------------------------------------------------------------------

COURSE_REGION = "Atelier East"     # flat at 21 m along its east edge, nothing built there
COURSE = (236, 40)                 # the corridor's south mouth, its middle; the corridor runs 12 m north


def build_course(g):
    """The walk course on the ground (height g), built by the tester through the agent API: a 3 m wide corridor north
    with a stone floor, a 1.4 m opening in its west wall 6-7.4 m along, its end a wall named "Blue Door"; and west of it
    a 4 m platform 3 m up for the drop warning; east of it a closed 3 m pen. The prims' ids, for deleting after."""
    bx, by = COURSE
    parts = [
        dict(name="Course Floor", material="stone", pos=[bx, by + 6, g + 0.05], scale=[3.0, 12.2, 0.1]),
        dict(pos=[bx - 1.5, by + 3, g + 1.25], scale=[0.2, 6, 2.5]),
        dict(pos=[bx - 1.5, by + 9.7, g + 1.25], scale=[0.2, 4.6, 2.5]),
        dict(pos=[bx + 1.5, by + 6, g + 1.25], scale=[0.2, 12, 2.5]),
        dict(name="Blue Door", pos=[bx, by + 12.1, g + 1.25], scale=[3.2, 0.2, 2.5]),
        dict(name="Course Platform", pos=[bx - 8, by + 5, g + 2.8], scale=[4, 4, 0.4]),
        # a closed pen east of the corridor, 3 m inside: a walk into it has no way in
        dict(pos=[bx + 6.5, by + 6, g + 1.25], scale=[0.2, 3.2, 2.5]),
        dict(pos=[bx + 9.5, by + 6, g + 1.25], scale=[0.2, 3.2, 2.5]),
        dict(pos=[bx + 8, by + 4.5, g + 1.25], scale=[3.2, 0.2, 2.5]),
        dict(pos=[bx + 8, by + 7.5, g + 1.25], scale=[3.2, 0.2, 2.5]),
    ]
    ids = []
    for p in parts:
        r = api("TesselBuild", "create", timeout=20, shape="box", **p)
        ids.append(r["id"])
    return ids


def clear_course(tester_id):
    """Whatever the tester owns in the course's region goes (the grid console; deleting through the app would wait for
    the person's confirmation): this run's course, or one a broken run left."""
    import grid
    lines = grid.console(f"change region {COURSE_REGION}", wait=1.5, answers=(f"show object owner {tester_id}", "change region root"))
    n = next((int(m[1]) for l in lines if (m := re.search(r"(\d+) object\(s\) found", l))), 0)
    if n:
        grid.console(f"change region {COURSE_REGION}", wait=1.5, answers=(f"delete object owner {tester_id}", "y", "change region root"))
    return n


def walk_models_agree(run):
    """Both collision models, the one without a GPU (SojournCollision, what walk mode uses) and the renderer's
    (SojournView), cast the same rays from inside the corridor: all round at knee and chest height, and down. They should
    meet the same prims at the same distances (the course is boxes, the same surfaces at any level of detail)."""
    # the renderer's counts only what it has drawn: wait for the end wall and the west wall's far part (frames turn a
    # quarter at a time)
    wait(lambda: api("SojournView", "collide", dir=[0, 1, 0], max=15).get("hit") and
         api("SojournView", "collide", dir=[-0.2, 1, 0], max=15).get("distance", 99) < 8, 20, 1)
    x, y, z, _ = tester_where()
    rays = [([x, y, z + dz], [math.cos(a), math.sin(a), 0], 15) for dz in (-0.4, 0.4) for a in (i * math.pi / 8 for i in range(16))]
    rays.append(([x, y, z + 0.4], [0, 0, -1], 5))
    bad = []
    for frm, d, mx in rays:
        ray = {"from": frm, "dir": d, "max": mx}
        a, b = api("SojournCollision", "collide", **ray), api("SojournView", "collide", **ray)
        same = a.get("hit") == b.get("hit") and (not a.get("hit") or (abs(a["distance"] - b["distance"]) < 0.02 and a.get("prim") == b.get("prim")))
        if not same:
            bad.append(f"dir {d[0]:.2f},{d[1]:.2f},{d[2]:.0f} at {frm[2] - z:+.1f}: {a.get('distance', '-')} / {b.get('distance', '-')}")
    run.check("both collision models give the same answers in the corridor (33 rays)", not bad, "; ".join(bad) or "all alike")


def walk_pos():
    """Where walk mode has the avatar (dead-reckoned: the region's own word on it can be a second and a half old)."""
    return tuple(api("SojournWalk", "status")["pos"])


def walk_said(m, words, secs):
    def look():
        return next((e for e in said_since(m) if all(w.lower() in (e.get("text") or "").lower() for w in words)), None)
    return wait(look, secs, 0.1)


def go_tp(run, what, x, y, z):
    m = mark()
    say_in_app(f"//go tp xyz {x},{y},{z}")
    ok = run.expect(what, ["Teleported"], m, 20)
    time.sleep(1.5)
    return ok


def walk_paths(run, bx, by, g):
    """Paths around what's in the way (Walk/PathPlanner.cs) on the course: //go from inside the corridor to beyond its
    west wall goes round through the opening; the assistant's walk (the session's autopilot) back in does too; //go into
    the closed pen has no way and teleports there; then following the visitor as it walks off and stops, and losing it
    when it teleports away."""
    if not go_tp(run, "into the corridor", bx, by + 3, g + 1.5):
        return
    target = (bx - 4, by + 3)
    m = mark()
    say_in_app(f"//go xyz {target[0]},{target[1]}")
    run.expect("//go to beyond the west wall goes round it", ["around something in the way"], m, 10)
    done = run.expect("and finishes there", ["Finished walking"], m, 40)
    time.sleep(2)
    x, y, z = walk_pos()
    off = math.dist((x, y), target)
    run.check("round through the opening: within a metre and a half of the spot", done and off < 1.5,
              f"at {x:.1f}, {y:.1f}: {off:.1f} m from it ({(done or {}).get('text', 'not finished')})")

    # the assistant's walk (LLAgent startAutoPilot, as its go tool) back into the corridor
    inside = (bx, by + 3)
    gx, gy = (lambda r: (r["global"][0] - r["region"][0], r["global"][1] - r["region"][1]))(api("LLAgent", "getPosition"))
    api("LLAgent", "startAutoPilot", target_global=[gx + inside[0], gy + inside[1], z], stop_distance=1.5)
    time.sleep(1)
    ended = wait(lambda: not api("LLAgent", "getAutoPilot")["enabled"], 40, 0.3)
    time.sleep(2)
    x, y, z = walk_pos()
    off = math.dist((x, y), inside)
    run.check("the assistant's walk goes round too, ending in the corridor", ended and off < 2.3 and x > bx - 1.3,
              f"{'ended' if ended else 'still going after 40 s'}; at {x:.1f}, {y:.1f}: {off:.1f} m from {inside}")

    pen = (bx + 8, by + 6)
    m = mark()
    say_in_app(f"//go xyz {pen[0]},{pen[1]}")
    run.expect("//go into the closed pen: no way, so it teleports", ["No way to walk to", "teleporting there"], m, 15)
    run.expect("and arrives", ["Teleported to"], m, 25)
    time.sleep(1.5)
    x, y, z = walk_pos()
    run.check("inside the pen", abs(x - pen[0]) < 1.4 and abs(y - pen[1]) < 1.4, f"at {x:.1f}, {y:.1f}")

    walk_club(run, bx, by, g)

    # following the visitor: it comes to the course, walks off north past the corridor's west side and stops
    start = (bx - 3, by - 6)
    got = visitor_tp(start[0], start[1], g + 1.5)
    if not run.check("the visitor comes to the course", got.startswith("arrived"), got):
        return
    time.sleep(2)
    go_tp(run, "the tester beside it", bx - 6, by - 7, g + 1.5)
    m = mark()
    say_in_app(f"//follow {VISITOR[0]}")
    run.expect("//follow says so", ["Following", VNAME], m, 8)
    time.sleep(2.5)   # (with it at the start, said or not)
    end = (bx - 3, by + 9)
    m = mark()
    v(f"goto {end[0]} {end[1]} {g + 1}")
    arrived = wait(lambda: math.dist(visitor_where()[:2], end) < 1.5, 20, 0.5)
    print(f"     (the visitor {'walked to' if arrived else 'did not reach'} {end}: at {visitor_where()})")
    with_it = run.expect("the follow says when it's with the visitor, stopped", ["With", VNAME], m, 20)
    time.sleep(3)
    x, y, z = walk_pos()
    vx, vy, vz = visitor_where()
    run.check("following it there", with_it and math.dist((x, y), (vx, vy)) < 4.5 and y > by + 3,
              f"the tester at {x:.1f}, {y:.1f}; the visitor at {vx:.1f}, {vy:.1f}: {math.dist((x, y), (vx, vy)):.1f} m apart")
    m = mark()
    try:
        v("tp %s %d %d %d" % (REGION, *VISITOR_START), timeout=20)   # (its own answer can be slow: it's not what's checked)
    except Exception as e:
        print(f"     (the visitor's teleport back: {e})")
    run.expect("the visitor teleporting away is said, and the follow ends", ["gone", "stopped following"], m, 15)


def visitor_tp(x, y, z, region=None):
    """The visitor teleported within a region, tried again once (a same-region teleport on the local grid failed once
    with no reason given)."""
    for tries in range(2):
        try:
            return v(f"tp {region or COURSE_REGION} {x:.1f} {y:.1f} {z:.1f}", timeout=40)
        except ToolError as e:
            print(f"     (the visitor's teleport: {e})")
            time.sleep(3)
    return "ERR the teleport failed twice"


CLUB = (204, 28)   # the club's south-west corner in COURSE_REGION, west of the course


def walk_club(run, bx, by, g):
    """Going to a person indoors (the blind tester at a jazz club, 2026-10-31: going to her partner kept trying to
    teleport her and never worked). A club built on one floor slab over whatever ground is there, its top 0.3 m over
    the highest of it (a building on uneven ground: the old planner took the floor for walls where the ground fell away
    under it): walls with a door, tables, a bar, a stage 0.8 m up with three 0.2 m steps, and a plinth 1 m up with none.
    The tester sits on a sofa by the door; //go person to the visitor in the middle of the stage, 3.5 m from its edge
    (the old planner found no free spot within 3 m, so no way, so a teleport): stands up first, walks up the steps, ends
    beside the visitor on the stage. Then the visitor on the plinth: walked as near as the way goes, said so."""
    cx, cy = CLUB
    xs, ys = [cx + i for i in range(0, 15, 2)], [cy + j for j in range(0, 13, 2)]
    hs = api("TesselAgent", "ground", points=[[x, y] for y in ys for x in xs])["heights"]
    if not run.check("(ground under the club)", hs and all(h is not None for h in hs), json.dumps(hs)):
        return
    lo_g, hi_g = min(hs), max(hs)
    F = hi_g + 0.3
    print(f"     (the club's ground from {lo_g:.1f} to {hi_g:.1f} m; its floor at {F:.1f})")
    go_tp(run, "(the tester to where the club goes)", cx + 7, cy + 3, hi_g + 1.5)
    base = lo_g - 0.3
    parts = [
        dict(name="Club Floor", material="wood", pos=[cx + 7, cy + 6, (base + F) / 2], scale=[14, 12, F - base]),
        dict(pos=[cx + 0.1, cy + 6, F + 1.4], scale=[0.2, 12, 2.8]),
        dict(pos=[cx + 13.9, cy + 6, F + 1.4], scale=[0.2, 12, 2.8]),
        dict(pos=[cx + 7, cy + 11.9, F + 1.4], scale=[14, 0.2, 2.8]),
        dict(pos=[cx + 1, cy + 0.1, F + 1.4], scale=[2, 0.2, 2.8]),            # the south wall, a door 2-3.4 m along
        dict(pos=[cx + 8.7, cy + 0.1, F + 1.4], scale=[10.6, 0.2, 2.8]),
        dict(name="Club Stage", pos=[cx + 8.5, cy + 9.15, F + 0.4], scale=[7, 5.3, 0.8]),
        dict(name="Stage Step", pos=[cx + 8, cy + 6.3, F + 0.3], scale=[1.6, 0.4, 0.6]),
        dict(name="Stage Step", pos=[cx + 8, cy + 5.9, F + 0.2], scale=[1.6, 0.4, 0.4]),
        dict(name="Stage Step", pos=[cx + 8, cy + 5.5, F + 0.1], scale=[1.6, 0.4, 0.2]),
        dict(name="Club Bar", pos=[cx + 13, cy + 3.5, F + 0.55], scale=[0.8, 4, 1.1]),
        dict(name="Club Table", shape="cylinder", pos=[cx + 6, cy + 3.5, F + 0.375], scale=[0.8, 0.8, 0.75]),
        dict(name="Club Table", shape="cylinder", pos=[cx + 10, cy + 3.5, F + 0.375], scale=[0.8, 0.8, 0.75]),
        dict(name="Club Plinth", pos=[cx + 2.2, cy + 7.75, F + 0.5], scale=[2.4, 2.5, 1.0]),
    ]
    made = 0
    for p in parts:
        p.setdefault("shape", "box")
        made += bool(api("TesselBuild", "create", timeout=20, **p).get("id"))
    if not run.check("the club is built", made == len(parts), f"{made} of {len(parts)} prims"):
        return
    stage = (cx + 8.5, cy + 10)
    got = visitor_tp(stage[0], stage[1], F + 2)
    sofa = v(f"sofa Club Sofa {cx + 3} {cy + 2.5} {F + 0.25}", timeout=60)
    if not run.check("the visitor on the stage, its sofa by the door", got.startswith("arrived") and sofa.startswith("sofa"), f"{got}; {sofa}"):
        return
    go_tp(run, "(the tester by the door)", cx + 3, cy + 1.4, F + 1)
    wait(lambda: api("SojournCollision", "collide", **{"from": [cx + 8, cy + 4, F + 0.5], "dir": [0, 1, 0], "max": 4}).get("hit"), 20, 1)
    m = mark()
    say_in_app("//sit Club Sofa")
    sat = run.expect("(sitting on the sofa)", ["Sitting on Club Sofa"], m, 10)
    time.sleep(1.5)
    m = mark()
    say_in_app(f"//go person {VISITOR[0]}")
    if sat:
        run.expect("//go seated stands up first", ["Standing up to walk"], m, 8)
    run.expect("then walks to the visitor on the stage", ["Walking", VNAME], m, 15)
    done = run.expect("and finishes there", ["Finished walking"], m, 60)
    time.sleep(2)
    x, y, z = walk_pos()
    vx, vy, vz = visitor_where()
    tps = [e.get("text") for e in said_since(m) if "eleport" in (e.get("text") or "")]
    run.check("no teleport tried", not tps, "; ".join(tps) or "none")
    run.check("up the steps onto the stage, beside the visitor", done and z > F + 1.4 and math.dist((x, y), (vx, vy)) < 2.6,
              f"at {x:.1f}, {y:.1f}, its middle {z - F:+.1f} m over the floor; the visitor {math.dist((x, y), (vx, vy)):.1f} m away "
              f"({(done or {}).get('text', 'not finished')})")

    # the plinth has no steps: as near as the way goes
    got = visitor_tp(cx + 2.2, cy + 7.75, F + 2.2)
    time.sleep(2)
    go_tp(run, "(the tester back by the tables)", cx + 8, cy + 2, F + 1)
    m = mark()
    say_in_app(f"//go person {VISITOR[0]}")
    run.expect("//go to the visitor up on the plinth says it's out of reach, walking as near as the way goes",
               ["out of reach", "as near as the way goes"], m, 15)
    done = run.expect("and finishes, saying where the visitor is from there", ["Finished walking, as near to", VNAME], m, 60)
    time.sleep(2)
    x, y, z = walk_pos()
    vx, vy, vz = visitor_where()
    run.check("at the plinth's foot, on the floor", done and z < F + 1.3 and math.dist((x, y), (vx, vy)) < 3,
              f"at {x:.1f}, {y:.1f}, its middle {z - F:+.1f} m over the floor; the visitor {math.dist((x, y), (vx, vy)):.1f} m away")


def walk_vehicle(run, bx, by, g):
    """Driving seated (walk mode with a script holding the movement keys): the visitor's cart south of the course, sat
    on by //sit; its script takes the keys (a permission question answered by keys if the region asks); then in walk
    mode H says the cart's heading, a held Up drives it forward (its script says so, it moves), V says its speed while
    moving, a tap of Left turns it, A doesn't take the cart itself for what's ahead; //stand after."""
    at = (bx - 4, by - 12)
    got = v(f"tp {COURSE_REGION} {at[0] - 3} {at[1]} {g + 1.5}", timeout=40)
    if not run.check("the visitor comes back for the cart", got.startswith("arrived"), got):
        return
    time.sleep(2)
    made = v(f"cart {at[0]} {at[1]} {g + 0.25}", timeout=60)
    if not run.check("the visitor's cart is out, its script running", made.startswith("cart"), made):
        return
    go_tp(run, "the tester beside the cart", at[0] - 1.5, at[1] - 2, g + 1.5)
    n = visitor_n()
    m = mark()
    say_in_app("//sit Walk Cart")
    run.expect("//sit sits on the cart", ["Sitting on Walk Cart"], m, 10)
    # the region may grant the keys to a sitter by itself, or ask
    asked = wait(lambda: any("Walk Cart" in (e.get("text") or "") and "?" in (e.get("text") or "") for e in said_since(m)) or None, 4, 0.3)
    if asked and open_newest_notice(run, ["Walk Cart"]):
        buttons = [d for d in find("type=Button") if d["id"].startswith("noticeButton")]
        yes = next((d for d in buttons if re.search(r"\b(yes|allow|grant|accept|ok)\b", d["name"], re.I)), None)
        if yes:
            number = re.match(r"\s*(\d)", yes["name"])
            key(number[1] if number else yes["id"].replace("noticeButton", ""))
        back_to_chat()
    if not run.heard_by_visitor("the cart's script takes the movement keys", lambda d: d["msg"] == "cart controls taken", n, 15, "cart controls taken"):
        return
    m = mark()
    key("f3")
    wait_focus(lambda d: d["id"] == "walkPad", 5)
    m = mark()
    key("h")
    run.expect("H says the cart's heading", ["Facing east"], m, 5)
    x0, y0, _ = walk_pos()
    n = visitor_n()
    m = mark()
    u("keydown focus | 38")
    time.sleep(1.5)
    key("v")
    moving = run.expect("V while driving says its speed", ["Walk Cart:", "a second", "east"], m, 4)
    time.sleep(1)
    u("keyup focus | 38")
    run.heard_by_visitor("held Up reaches the cart's script as forward", lambda d: d["msg"] == "cart: forward", n, 8, "cart: forward")
    time.sleep(1)
    x1, y1, _ = walk_pos()
    run.check("and the cart drives east, the tester on it", x1 - x0 > 1.5 and abs(y1 - y0) < 0.6, f"from {x0:.1f}, {y0:.1f} to {x1:.1f}, {y1:.1f}")
    n = visitor_n()
    key("left")
    run.heard_by_visitor("a tap of Left turns it (the script's rotate left)", lambda d: d["msg"] == "cart: left", n, 8, "cart: left")
    m = mark()
    key("a")
    got = run.expect("A says what's ahead, not the cart itself", ["ahead"], m, 5)
    run.check("(the cart isn't what's ahead)", got and "Walk Cart" not in got["text"], (got or {}).get("text", ""))
    key("esc")
    wait_focus(lambda d: d["id"] == "chatInput", 5)
    m = mark()
    say_in_app("//stand")
    run.expect("//stand stands", ["Standing"], m, 8)
    time.sleep(1)


def clear_owned(region, owner_id):
    """Whatever this account owns in a region goes (the grid console)."""
    import grid
    lines = grid.console(f"change region {region}", wait=1.5, answers=(f"show object owner {owner_id}", "change region root"))
    n = next((int(m[1]) for l in lines if (m := re.search(r"(\d+) object\(s\) found", l))), 0)
    if n:
        grid.console(f"change region {region}", wait=1.5, answers=(f"delete object owner {owner_id}", "y", "change region root"))
    return n


def walk_crossing(run):
    """A seated border crossing (M3): the visitor's physical kart (an LSL car) near Atelier's east edge, sat on, driven
    east by a held Up into Atelier East. Checked: the crossing is said, with whether the seat held. (Atelier's ground
    there is found first: its south is sea.)"""
    ys = list(range(20, 240, 10))
    h = api("TesselAgent", "ground", points=[[250, y] for y in ys])["heights"]
    # land about as high as Atelier East's flat 21 m across the border (its north half is a hill: a cliff at the edge)
    land = [y for y, z in zip(ys, h) if z is not None and 21 < z < 23]
    if not run.check("a stretch of land at Atelier's east edge for the kart", land, f"heights at x 250: {h}"):
        return
    y = land[len(land) // 2]
    gz = h[ys.index(y)]
    got = v(f"tp {REGION} 240 {y} {gz + 1.5}", timeout=40)
    time.sleep(2)
    made = v(f"kart 244 {y} {gz + 0.4}", timeout=60)
    if not run.check("the visitor's kart is out", made.startswith("kart"), f"{got}; {made}"):
        return
    go_tp(run, "the tester beside the kart", 243, y - 2, gz + 1.5)
    n = visitor_n()
    m = mark()
    say_in_app("//sit Walk Kart")
    run.expect("//sit sits in the kart", ["Sitting on Walk Kart"], m, 10)
    # (heard through the app's own read-out: the visitor, standing on the slope beside it, missed it once)
    if not run.expect("the kart's script takes the keys", ["kart controls taken"], m, 15):
        say_in_app("//stand")
        return
    key("f3")
    wait_focus(lambda d: d["id"] == "walkPad", 5)
    m = mark()
    u("keydown focus | 38")
    crossed = walk_said(m, ["Now in Atelier East"], 8)
    u("keyup focus | 38")
    held = walk_said(m, ["Now in Atelier East, still on Walk Kart"], 6)
    left = walk_said(m, ["left you standing"], 1)
    x, yy, z = walk_pos()
    st = status()
    run.check("driving the kart east crosses into Atelier East, said", crossed, crossed["text"] if crossed else
              f"not said; at {x:.1f}, {yy:.1f} in {api('LLAgent', 'getPosition').get('region')}")
    run.check("and whether the seat held is said", held or left,
              (held or left or {}).get("text", "neither said") + f" (at {x:.1f}, {yy:.1f})")
    print(f"     (seated crossing: {'held' if held else 'the seat let go' if left else 'unknown'})")
    # (the blind tester's ask, 2026-10-31: crossings on a train were said but not in the history, as her region script's are)
    tail = history_tail()
    lines = [l for l in tail.splitlines() if l.startswith("Now in Atelier East")]
    run.check("the crossing is a line in nearby chat's history, once", len(lines) == 1, " | ".join(lines) or tail[-200:])
    key("esc")
    wait_focus(lambda d: d["id"] == "chatInput", 5)
    say_in_app("//stand")
    time.sleep(2)


DISTANCE_LINE = re.compile(r"^(\d+(?:\.\d)?) metres?( backward| to the left| to the right)?\.$")


def distance_walked(run):
    """How far the arrows walked (Walk/WalkMode.Distance.cs; a blind tester's ask, 2026-10-30): three taps of Up said
    once as one stretch when the keys rest, matching how far the avatar went; a tap of Down said as backward; M and
    Shift+M; nothing said with walk_distance off. Needs no renderer."""
    back_to_chat()
    m = mark()
    say_in_app("//face 90")
    run.expect("(facing north)", ["Facing", "north"], m, 8)
    time.sleep(1)
    m = mark()
    key("f3")
    if not run.check("(F3: walk mode)", wait_focus(lambda d: d["id"] == "walkPad", 5), focus_label()):
        return

    def stretch(keyname, taps, what, way=None):
        time.sleep(1.5)
        x0, y0, *_ = tester_where()
        m = mark()
        for _ in range(taps):
            key(keyname)
            time.sleep(0.3)
        got = walk_said(m, ["metre"], 8)
        time.sleep(2)
        x1, y1, *_ = tester_where()
        moved = math.dist((x0, y0), (x1, y1))
        lines = [e["text"] for e in said_since(m) if DISTANCE_LINE.match(e.get("text") or "")]
        mm = DISTANCE_LINE.match(lines[0]) if lines else None
        run.check(f"{what}: how far said once the keys rest, once", len(lines) == 1, "; ".join(lines) or "not said")
        if mm:
            said = float(mm[1])
            run.check(f"{what}: as far as the avatar went", abs(said - moved) <= 0.25 + 0.1 * moved,
                      f"said {lines[0]!r}, moved {moved:.2f} m ({moved / taps:.2f} m a tap)")
            run.check(f"{what}: the way said", (mm[2] or "").strip() == (way or ""), lines[0])
        return moved

    a = stretch("up", 3, "three taps of Up")
    b = stretch("down", 1, "a tap of Down", "backward")
    m = mark()
    key("m")
    got = run.expect("M: the last walk, all of them, where the count began", ["Last walk:", "backward", "In all:", "Where the count began"], m, 5)
    if got and a and b:
        mm = re.search(r"In all: (\d+(?:\.\d)?) metres?", got["text"])
        run.check("the count in all adds the stretches up", mm and abs(float(mm[1]) - (a + b)) <= 0.4, f"{got['text']}; walked {a:.2f} + {b:.2f}")
    m = mark()
    key("shift+m")
    run.expect("Shift+M counts from here", ["Counting from here."], m, 5)
    m = mark()
    key("m")
    run.expect("then M says nothing's walked yet", ["Nothing walked yet."], m, 5)
    api("SojournSettings", "set", walk_distance="off")
    try:
        time.sleep(1)
        m = mark()
        key("up")
        time.sleep(3.5)
        lines = [e["text"] for e in said_since(m) if DISTANCE_LINE.match(e.get("text") or "")]
        run.check("with walk_distance off, nothing said", not lines, "; ".join(lines) or "nothing")
    finally:
        api("SojournSettings", "set", walk_distance="text")
    m = mark()
    key("esc")
    run.expect("(Escape: walk mode off)", ["Walk mode off"], m, 5)


ROUTE_COURSE = (236, 120)          # in COURSE_REGION: the route recording's wall and doorway, well north of the walk course
ROUTE_TP = ("Atelier Northeast", 60, 60, 30)   # where the recorded walk ends, by a teleport into another region


SWITCH_LINE = re.compile(r"^Switch in (\d+) metres, set to the main line, North Station\.$")


def rail_switches(run):
    """Rail switches while riding (Walk/WalkMode.Switches.cs; the blind tester took two wrong turns on her partner's train,
    2026-11-01). On a flat stretch of Atelier East found first, the visitor's two switches as the SLRR standard has them
    (`switch`: a "Switch Stand" whose description says Main or Branch, a touch part, two guide rails that swap names and
    phantom) 30 m apart, and its kart heading east toward them. Seated, in walk mode, a held Up drives: the first switch
    said once with its setting and the switch cue; W says it ahead with both ways' names; Shift+W touches its stand and
    says the new setting (the visitor hears the stand set); //switch, //switch main and //switch main again (already);
    then driving on past both, the second said once and the first not again. Needs no renderer."""
    back_to_chat()
    m = mark()
    say_in_app(f"//tp {COURSE_REGION}/100/60/40")
    if not run.expect("(to Atelier East for the railway)", ["Arrived", COURSE_REGION], m, 40):
        return
    time.sleep(2)
    x0, a, b = 40, 115, 145
    xs = list(range(x0 - 5, b + 31, 10))
    y = None
    tried = []
    for yy in range(16, 200, 8):
        hs = api("TesselAgent", "ground", points=[[x, yy] for x in xs])["heights"]
        known = [h for h in hs if h is not None]
        if len(known) == len(hs) and max(hs) - min(hs) < 1.2:
            y, ground = yy, dict(zip(xs, hs))
            break
        tried.append(f"{yy}: {min(known):.1f}..{max(known):.1f}" if known else f"{yy}: -")
    if not run.check("(a flat stretch of Atelier East for the railway)", y is not None, "; ".join(tried)):
        return

    def g(x):
        return ground[min(ground, key=lambda k: abs(k - x))]

    print(f"     (the railway along y {y}, ground {min(ground.values()):.1f}-{max(ground.values()):.1f} m)")
    ray = api("SojournCollision", "collide", **{"from": [x0, y, g(x0) + 0.8], "dir": [1, 0, 0], "max": b + 25 - x0})
    if not run.check("(nothing in the kart's way along it)", not ray.get("hit"), json.dumps(ray)):
        return
    try:
        made = []
        for x, ch in ((a, 133), (b, 134)):
            v(f"tp {COURSE_REGION} {x} {y + 8} {g(x) + 1.5}", timeout=40)
            time.sleep(1.5)
            made.append(v(f"switch {x} {y} {g(x)} {ch}", timeout=60))
        if not run.check("the visitor's two switches are out, their scripts running", all(r.startswith("switch") for r in made), " | ".join(made)):
            return
        v(f"tp {COURSE_REGION} {x0} {y - 5} {g(x0) + 1.5}", timeout=40)
        time.sleep(1.5)
        kart = v(f"kart {x0} {y} {g(x0) + 0.4} facing 0", timeout=60)
        if not run.check("the visitor's kart is out", kart.startswith("kart"), kart):
            return
        # between the switches, in chat range of both stands
        v(f"tp {COURSE_REGION} {a + 15} {y + 9} {g(a) + 1.5}", timeout=40)
        go_tp(run, "the tester beside the kart", x0 - 1, y - 2, g(x0) + 1.5)
        m = mark()
        say_in_app("//sit Walk Kart")
        run.expect("//sit sits in the kart", ["Sitting on Walk Kart"], m, 10)
        if not run.expect("the kart's script takes the keys", ["kart controls taken"], m, 15):
            return
        key("f3")
        wait_focus(lambda d: d["id"] == "walkPad", 5)

        # driving toward them: the first said once, in time
        m = mark()
        t0 = time.time()
        u("keydown focus | 38")
        said = walk_said(m, ["Switch in"], 8)
        u("keyup focus | 38")
        at = walk_pos()
        got = SWITCH_LINE.match((said or {}).get("text") or "")
        run.check("driving toward a switch says it once, with its setting and where the main line goes",
                  got and 25 <= int(got[1]) <= 60, ((said or {}).get("text") or "not said") + f" (at {at[0]:.1f}, {at[1]:.1f})")
        run.check("with the switch cue", cues_since(t0, "Switch"), json.dumps(cues_since(t0)[-5:]))
        time.sleep(3)   # the kart coasts to a stop
        x1 = walk_pos()[0]
        print(f"     (said at {at[0]:.1f}; stopped at {x1:.1f}, the first switch at {a})")

        m = mark()
        key("w")
        got = walk_said(m, ["Switch", "ahead"], 8)
        run.check("W says it ahead, its setting and both ways' names",
                  got and "set to the main line" in got["text"] and "branch: East Yard" in got["text"], (got or {}).get("text", "not said"))

        n = visitor_n()
        m = mark()
        t0 = time.time()
        key("shift+w")
        got = walk_said(m, ["Switch set to"], 10)
        run.check("Shift+W throws it and says the new setting", got and got["text"] == "Switch set to the branch, East Yard.",
                  (got or {}).get("text") or " | ".join(e.get("text") or "" for e in said_since(m)))
        run.heard_by_visitor("the stand was set by the touch", lambda d: d["msg"] == "switch Branch", n, 8, "switch Branch")
        run.check("(the throw's cue)", cues_since(t0, "SwitchThrown"), json.dumps(cues_since(t0)[-5:]))

        key("esc")
        wait_focus(lambda d: d["id"] == "chatInput", 5)
        m = mark()
        say_in_app("//switch")
        run.expect("//switch says how it's set now", ["Switch", "set to the branch"], m, 8)
        n = visitor_n()
        m = mark()
        say_in_app("//switch main")
        run.expect("//switch main sets it back", ["Switch set to the main line, North Station."], m, 10)
        run.heard_by_visitor("(the stand back on the main line)", lambda d: d["msg"] == "switch Main", n, 8, "switch Main")
        m = mark()
        say_in_app("//switch main")
        run.expect("//switch main again: it already is", ["already set to the main line"], m, 8)

        # on past both: the second said once, the first not again
        key("f3")
        wait_focus(lambda d: d["id"] == "walkPad", 5)
        m = mark()
        u("keydown focus | 38")
        wait(lambda: walk_pos()[0] > b + 8 or None, 20, 0.3)
        u("keyup focus | 38")
        time.sleep(2)
        lines = [e.get("text") or "" for e in said_since(m) if (e.get("text") or "").startswith("Switch in")]
        print(f"     (passing both, at {walk_pos()[0]:.1f}: {lines})")
        run.check("driving on past both: the next one said once, the one passed not again",
                  len(lines) == 1 and SWITCH_LINE.match(lines[0]), " | ".join(lines) or "none said")
        key("esc")
        wait_focus(lambda d: d["id"] == "chatInput", 5)
    finally:
        say_in_app("//stand")
        time.sleep(1)
        try:
            v("derez Walk Kart", timeout=15)
        except Exception:
            pass
        clear_owned(COURSE_REGION, visitor_id())


def vehicle_stop(run):
    """Stopping a vehicle by its controls (Walk/WalkMode.Stop.cs; the blind tester's freight train ran off the end of its
    track, 2026-11-01). On a flat stretch of Atelier East: the visitor's kart as a car, driven by a held Up, stopped by
    Space with Up still held (the back control held, let go as it's nearly still: it never backs); then its kart as a
    throttle train (each Up press 5% of 10 m/s, each Down 5% less, on past 0 into reverse), opened 8 taps and stopped by
    Space, again by //stop from the Say box, then opened 12 taps and run past the end of its track, where it says "sensor :
    No further track guide detected": the urgent cue, "Track ends: stopping.", "Stopped.", its throttle back at 0 and never
    below, and it never moves back. Needs no renderer."""
    back_to_chat()
    m = mark()
    say_in_app(f"//tp {COURSE_REGION}/100/60/40")
    if not run.expect("(to Atelier East for the stops)", ["Arrived", COURSE_REGION], m, 40):
        return
    time.sleep(2)
    x0, endx = 40, 125
    xs = list(range(x0 - 5, endx + 31, 10))
    y = None
    tried = []
    for yy in range(16, 200, 8):
        hs = api("TesselAgent", "ground", points=[[x, yy] for x in xs])["heights"]
        known = [h for h in hs if h is not None]
        if len(known) == len(hs) and max(hs) - min(hs) < 1.2:
            y, ground = yy, dict(zip(xs, hs))
            break
        tried.append(f"{yy}: {min(known):.1f}..{max(known):.1f}" if known else f"{yy}: -")
    if not run.check("(a flat stretch of Atelier East for the stops)", y is not None, "; ".join(tried)):
        return

    def g(x):
        return ground[min(ground, key=lambda k: abs(k - x))]

    ray = api("SojournCollision", "collide", **{"from": [x0, y, g(x0) + 0.8], "dir": [1, 0, 0], "max": endx + 30 - x0})
    if not run.check("(nothing in the way along it)", not ray.get("hit"), json.dumps(ray)):
        return

    def watch(secs, until):
        """Walk mode's x every 0.2 s for up to secs, or until an announcement with these words: the samples."""
        got, t_end, m_ = [], time.time() + secs, mark()
        while time.time() < t_end:
            got.append(walk_pos()[0])
            if any(all(w.lower() in (e.get("text") or "").lower() for w in until) for e in said_since(m_)):
                break
            time.sleep(0.2)
        return got

    def still_and_forward(what, samples):
        time.sleep(1.5)
        a = walk_pos()[0]
        time.sleep(1)
        b = walk_pos()[0]
        top = max(samples + [a, b])
        run.check(f"{what}: still afterwards", abs(b - a) < 0.5, f"{abs(b - a):.2f} m in the last second")
        run.check(f"{what}: never moving back", b >= top - 0.4, f"furthest {top:.1f}, now {b:.1f}")

    def sit(name):
        m_ = mark()
        say_in_app(f"//sit {name}")
        if not run.expect(f"the {name}'s script takes the keys", ["kart controls taken"], m_, 15):
            return False
        key("f3")
        wait_focus(lambda d: d["id"] == "walkPad", 5)
        return True

    def lines_since(m_):
        return [e.get("text") or "" for e in said_since(m_)]

    try:
        # a car: held Up, then Space with Up still held
        v(f"tp {COURSE_REGION} {x0 + 10} {y + 8} {g(x0) + 1.5}", timeout=40)
        time.sleep(1.5)
        car = v(f"kart Walk Kart {x0} {y} {g(x0) + 0.4} facing 0", timeout=60)
        if not run.check("the visitor's kart is out", car.startswith("kart"), car):
            return
        go_tp(run, "the tester beside the kart", x0 - 1, y - 2, g(x0) + 1.5)
        if not sit("Walk Kart"):
            return
        u("keydown focus | 38")
        time.sleep(2.5)
        m = mark()
        t0 = time.time()
        key("space")
        samples = watch(8, ["Stopped"])
        u("keyup focus | 38")
        lines = lines_since(m)
        run.check("a car: Space says Stopping, then Stopped", "Stopping." in lines and "Stopped." in lines, " | ".join(lines))
        print(f"     (the car stopped in {time.time() - t0:.1f} s, at {walk_pos()[0]:.1f})")
        still_and_forward("a car stopped by Space", samples)
        key("esc")
        wait_focus(lambda d: d["id"] == "chatInput", 5)
        say_in_app("//stand")
        time.sleep(1.5)
        v("derez Walk Kart", timeout=15)

        # a throttle train
        train = v(f"kart Walk Train {x0} {y} {g(x0) + 0.4} facing 0 throttle 5 endx {endx}", timeout=60)
        if not run.check("the visitor's throttle train is out", train.startswith("kart"), train):
            return
        go_tp(run, "the tester beside the train", x0 - 1, y - 2, g(x0) + 1.5)
        if not sit("Walk Train"):
            return

        def open_throttle(n):
            m_ = mark()
            for _ in range(n):
                key("up")
                time.sleep(0.45)
            run.expect(f"(the throttle opened to {n * 5})", [f"throttle {n * 5}"], m_, 6)
            taps = api("SojournWalk", "status").get("throttle_taps")
            run.check(f"(walk mode counted {n} taps of Up)", taps == n, f"{taps}")

        def stopped_at_zero(what, m_):
            tl = [t for t in lines_since(m_) if "throttle" in t]
            run.check(f"{what}: its throttle back to 0 and never below",
                      tl and tl[-1].endswith("throttle 0") and not any("throttle -" in t for t in tl), " | ".join(tl) or "no throttle lines")

        # Space
        open_throttle(8)
        time.sleep(2.5)
        m = mark()
        key("space")
        samples = watch(13, ["Stopped"])
        lines = lines_since(m)
        run.check("a train: Space says Stopping, then Stopped", "Stopping." in lines and "Stopped." in lines, " | ".join(lines))
        stopped_at_zero("a train stopped by Space", m)
        still_and_forward("a train stopped by Space", samples)

        # //stop from the Say box (out of walk mode: the stop's controls don't depend on where the focus is)
        open_throttle(8)
        time.sleep(1)
        key("esc")
        wait_focus(lambda d: d["id"] == "chatInput", 5)
        m = mark()
        say_in_app("//stop")
        samples = watch(13, ["Stopped"])
        lines = lines_since(m)
        run.check("//stop from Say stops the train", "Stopping." in lines and "Stopped." in lines, " | ".join(lines))
        stopped_at_zero("a train stopped by //stop", m)
        still_and_forward("a train stopped by //stop", samples)

        # the track's end
        key("f3")
        wait_focus(lambda d: d["id"] == "walkPad", 5)
        print(f"     (the end of the track at x {endx}; the train at {walk_pos()[0]:.1f})")
        m = mark()
        open_throttle(12)
        t0 = time.time()
        said = walk_said(m, ["Track ends"], 30)
        run.check("past the end of its track: \"Track ends: stopping.\"", said and said["text"] == "Track ends: stopping.",
                  (said or {}).get("text") or " | ".join(lines_since(m))[-300:])
        if not said:
            return
        at = walk_pos()[0]
        run.check("with the urgent cue", cue_after(t0 - 5, ["TrackEnd"], 3), json.dumps(cues_since(t0 - 5)[-5:]))
        samples = watch(13, ["Stopped"])
        lines = lines_since(m)
        print(f"     (said at {at:.1f}; stopped at {walk_pos()[0]:.1f})")
        run.check("then \"Stopped.\"", "Stopped." in lines, " | ".join(lines))
        stopped_at_zero("a train stopped at the track's end", m)
        still_and_forward("a train stopped at the track's end", samples)
        time.sleep(4)   # the train's "No Guide found" again, now standing: not another stop
        run.check("said once (its later lines while standing don't stop it again)",
                  sum(1 for t in lines_since(m) if t.startswith("Track ends")) == 1, " | ".join(lines_since(m))[-300:])
        key("esc")
        wait_focus(lambda d: d["id"] == "chatInput", 5)
    finally:
        say_in_app("//stand")
        time.sleep(1)
        for name in ("Walk Kart", "Walk Train"):
            try:
                v(f"derez {name}", timeout=15)
            except Exception:
                pass
        clear_owned(COURSE_REGION, visitor_id())


def build_route_course(g):
    """A wall across the way north (12 m, east-west, 2.5 m high) 5 m from the start, with a 1.4 m doorway in its middle
    and an open door named Route Door standing beside the doorway's west side. The prims' ids."""
    bx, by = ROUTE_COURSE
    parts = [
        dict(pos=[bx - 3.6, by + 5, g + 1.25], scale=[5.8, 0.2, 2.5]),
        dict(pos=[bx + 3.6, by + 5, g + 1.25], scale=[5.8, 0.2, 2.5]),
        dict(name="Route Door", pos=[bx - 0.75, by + 5.8, g + 1.25], scale=[0.1, 1.4, 2.4]),
    ]
    return [api("TesselBuild", "create", timeout=20, shape="box", **p)["id"] for p in parts]


def route_recording(run):
    """Recording a route by walking it (Walk/RouteRecorder.cs, MainForm.Routes.cs; docs/access-app.md > Walk mode >
    Recording a route): in walk mode R starts recording; held Up walks north through the doorway, four taps of Right turn
    east, held Up walks on; a teleport into another region by //tp; R says how it's going; N adds a note (Backspace takes
    it back, N again); Shift+R asks a name and saves it in Routes. The notecard: a line saying where it starts, few
    //go xyz lines (one in the doorway, one at the turn), the teleport as an SLurl, the note. Then from the start again,
    //route plays it, arriving where the walk ended. The points' ticks are counted (and their lines heard, walk_route
    both for the step). Needs no renderer."""
    bx, by = ROUTE_COURSE
    tester_id = api("LLAgent", "getID")["id"]
    back_to_chat()
    m = mark()
    say_in_app(f"//tp {COURSE_REGION}/{bx}/{by}/25")
    if not run.expect("(to the route course)", ["Arrived", COURSE_REGION], m, 40):
        return
    time.sleep(1.5)
    g = api("TesselAgent", "ground", points=[[bx, by]])["heights"][0] or 21.0
    if n := clear_course(tester_id):
        print(f"     (cleared {n} object(s) a run before left in {COURSE_REGION})")
    ids, name = [], f"Route {uuid.uuid4().hex[:6]}"
    old_mode = setting("walk_route")
    try:
        ids = build_route_course(g)
        run.check("(the wall and its doorway built)", len(ids) == 3, f"{len(ids)} prims on the ground at {g:.1f} m")
        go_tp(run, "(at the start, south of the wall)", bx, by, g + 1.5)
        m = mark()
        say_in_app("//face 90")
        run.expect("(facing north)", ["Facing", "north"], m, 8)
        # the collision model has the wall before walking (the doorway rays need it)
        wait(lambda: api("SojournCollision", "collide", **{"from": [bx - 2, by, g + 1], "dir": [0, 1, 0], "max": 8}).get("hit"), 30, 1)
        setting("walk_route", "both")
        key("f3")
        if not run.check("(F3: walk mode)", wait_focus(lambda d: d["id"] == "walkPad", 5), focus_label()):
            return
        t0 = time.time()
        m = mark()
        key("r")
        run.expect("R starts recording, saying the keys", ["Recording a route", "Shift+R saves"], m, 5)
        start = api("LLAgent", "getPosition")["global"][:2]

        def hold(vk, done, secs):
            u(f"keydown focus | {vk}")
            try:
                return wait(done, secs, 0.1)
            finally:
                u(f"keyup focus | {vk}")
                time.sleep(1.5)

        through = hold(38, lambda: walk_pos()[1] > by + 9.5, 12)
        x, y, *_ = tester_where()
        north = y
        run.check("held Up walks north through the doorway", through and abs(x - bx) < 0.8, f"at {x:.1f}, {y:.1f}")
        for _ in range(4):
            key("right")
            time.sleep(0.35)
        time.sleep(1)
        on = hold(38, lambda: walk_pos()[0] > bx + 5, 12)
        x, y, *_ = tester_where()
        east = x
        run.check("turned east, held Up walks on", on and y > by + 8, f"at {x:.1f}, {y:.1f}")
        ticks = cues_since(t0, "RoutePoint")
        heard = [e["text"] for e in said_since(m) if (e.get("text") or "").startswith("Step ")]
        run.check("a tick for each point kept (and with text on, its line)", len(ticks) >= 2 and len(heard) == len(ticks),
                  f"{len(ticks)} ticks; lines: {'; '.join(heard) or 'none'}")
        run.check("a point kept in the doorway, said so", any("doorway" in h for h in heard), "; ".join(heard) or "none")
        run.check("and one at the turn", any("turn" in h for h in heard), "; ".join(heard) or "none")

        # a teleport into another region, typed in Say (recording goes on outside walk mode)
        key("esc")
        wait_focus(lambda d: d["id"] == "chatInput", 5)
        m = mark()
        say_in_app("//tp %s/%d/%d/%d" % ROUTE_TP)
        run.expect("(a teleport into another region while recording)", ["Arrived", ROUTE_TP[0]], m, 40)
        tick = walk_said(m, ["a teleport to", ROUTE_TP[0]], 6)
        run.check("the teleport kept as a step", tick, tick["text"] if tick else "not said")
        time.sleep(1)
        end = api("LLAgent", "getPosition")["global"][:2]
        key("f3")
        wait_focus(lambda d: d["id"] == "walkPad", 5)
        m = mark()
        key("r")
        run.expect("R while recording: how it's going", ["Recording:", "steps", "walked"], m, 5)

        def note(words):
            m = mark()
            key("n")
            if not run.check("N asks for a note", wait_focus(lambda d: d["id"] == "answer", 5), focus_label()):
                return None
            type_text(words, "id=answer")
            wait(lambda: value_of("id=answer") == words, 3, 0.2)
            key("enter", target="id=answer")
            return run.expect("the note kept, said", ["Note kept", words], m, 5)

        note("the end of the walk")
        wait_focus(lambda d: d["id"] == "walkPad", 5)
        m = mark()
        key("back")
        run.expect("Backspace takes the note back", ["Took back the note: the end of the walk"], m, 5)
        note("the end of the walk")
        wait_focus(lambda d: d["id"] == "walkPad", 5)
        m = mark()
        key("shift+r")
        if not run.check("Shift+R asks the route's name", wait_focus(lambda d: d["id"] == "answer", 5), focus_label()):
            return
        u("set id=answer |")
        type_text(name, "id=answer")
        wait(lambda: value_of("id=answer") == name, 3, 0.2)
        key("enter", target="id=answer")
        saved = run.expect("saved in Routes, said with how to play it", [f"Saved {name}", "Routes", f"//route {name} plays it"], m, 20)
        if not saved:
            return
        key("esc")
        wait_focus(lambda d: d["id"] == "chatInput", 5)

        root = api("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
        routes = inv_items(root).get("Routes")
        card = routes and inv_items(routes).get(name)
        text = api("TesselInventory", "readNotecard", item_id=card).get("text", "") if card else ""
        print("     (the notecard:\n       " + "\n       ".join(text.splitlines()) + ")")
        lines = [l.strip() for l in text.splitlines() if l.strip()]
        gos = [tuple(float(n) for n in mm[1].split(",")) for l in lines if (mm := re.match(r"//go xyz (-?[\d.]+,-?[\d.]+(?:,-?[\d.]+)?)$", l))]
        run.check("the notecard is in Routes", card, f"Routes {routes}; {name} {card}")
        run.check("it starts by saying where", lines and lines[0].startswith(f"Starts in {COURSE_REGION}"), lines[0] if lines else "empty")
        run.check("few //go xyz lines, not every step: the start, the doorway, the turn, the end", len(gos) == 4, f"{len(gos)}: {gos}")
        run.check("none where the walk never went (past where it stopped)", all(p[1] < north + 0.6 and p[0] < east + 0.6 for p in gos),
                  f"stopped at y {north:.1f} going north, x {east:.1f} going east: {gos}")
        run.check("one in the doorway", any(abs(p[0] - bx) < 0.8 and abs(p[1] - (by + 5)) < 1.0 for p in gos), str(gos))
        run.check("one at the turn", any(abs(p[0] - bx) < 1.2 and p[1] > by + 7.5 for p in gos), str(gos))
        tp = f"//tp http://maps.secondlife.com/secondlife/{ROUTE_TP[0].replace(' ', '%20')}/"
        run.check("the teleport as an SLurl", any(l.startswith(tp) for l in lines), tp)
        run.check("the note as a line of its own, after the teleport", lines[-1] == "the end of the walk", lines[-1] if lines else "")

        # played back from the start
        m = mark()
        say_in_app(f"//tp {COURSE_REGION}/{bx}/{by}/{g + 1.5:.0f}")
        run.expect("(back to the start)", ["Arrived", COURSE_REGION], m, 40)
        time.sleep(2)
        m = mark()
        say_in_app(f"//route {name}")
        run.expect("//route plays it at once", [f"Playing {name}"], m, 15)
        run.expect("its start read as a note", ["Note: Starts in"], m, 10)
        done = run.expect("and finishes", [f"Route finished: {name}"], m, 120)
        run.expect("the note read on arriving", ["Note: the end of the walk"], m, 5)
        time.sleep(1.5)
        here = api("LLAgent", "getPosition")["global"][:2]
        off = math.dist(here, end)
        run.check("where the walk ended", done and off < 3,
                  f"{off:.1f} m from where the recording ended (it began {math.dist(start, end):.0f} m away)")
        if not done:
            print("     (said: " + " | ".join(e.get("text") or "" for e in said_since(m))[-1500:] + ")")
        delete_by_search(run, name)
    finally:
        setting("walk_route", old_mode)
        back_to_chat()
        if ids:
            clear_course(tester_id)
        say_in_app("//tp Atelier/%d/%d/%d" % TP_TO)
        time.sleep(5)


def route_crossing(run):
    """A route recorded walking across a region's border (Walk/RouteRecorder.cs: the last point before the border and
    the first after, the one after in the coordinates of the region before, past 256) and played back by //route
    (Commands: a //go xyz past the edge walked straight and judged arrived in the frame of the region it set out from):
    held Up north from Atelier East into Atelier Northeast, //record save NAME, back by //tp, //route, arriving where the
    walk ended (the ground there is flat at 21 m across the border). Needs no renderer."""
    x0 = 236
    back_to_chat()
    m = mark()
    say_in_app(f"//tp {COURSE_REGION}/{x0}/245/25")
    if not run.expect("(to Atelier East's north edge)", ["Arrived", COURSE_REGION], m, 40):
        return
    time.sleep(2)
    name = f"Route {uuid.uuid4().hex[:6]}"
    try:
        m = mark()
        say_in_app("//face 90")
        run.expect("(facing north)", ["Facing", "north"], m, 8)
        time.sleep(1)
        start = api("LLAgent", "getPosition")["global"]
        m = mark()
        say_in_app("//record")
        run.expect("//record starts recording, saying the commands", ["Recording a route", "//record save NAME saves"], m, 5)
        key("f3")
        wait_focus(lambda d: d["id"] == "walkPad", 5)
        u("keydown focus | 38")
        try:
            crossed = wait(lambda: api("LLAgent", "getPosition")["global"][1] > start[1] + 22, 20, 0.2)
        finally:
            u("keyup focus | 38")
        time.sleep(2.5)
        end = api("LLAgent", "getPosition")["global"]
        # (judged once settled: just after a crossing the position can read a region's width off for a moment)
        if not run.check("held Up walks north across the border", crossed and end[1] - start[1] > 11.5, f"{end[1] - start[1]:.1f} m north, the border 11 m on"):
            return
        key("esc")
        wait_focus(lambda d: d["id"] == "chatInput", 5)
        m = mark()
        say_in_app(f"//record save {name}")
        run.expect("//record save NAME saves it", [f"Saved {name}", "Routes"], m, 20)
        root = api("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
        routes = inv_items(root).get("Routes")
        card = routes and inv_items(routes).get(name)
        text = api("TesselInventory", "readNotecard", item_id=card).get("text", "") if card else ""
        print("     (the notecard:\n       " + "\n       ".join(text.splitlines()) + ")")
        gos = [tuple(float(n) for n in mm[1].split(",")) for l in text.splitlines()
               if (mm := re.match(r"//go xyz (-?[\d.]+,-?[\d.]+(?:,-?[\d.]+)?)$", l.strip()))]
        run.check("the points past the border in Atelier East's frame (over 256), the one before it under",
                  any(p[1] > 256 for p in gos) and any(250 < p[1] <= 256 for p in gos), str(gos))
        m = mark()
        say_in_app(f"//tp {COURSE_REGION}/{x0}/245/25")
        run.expect("(back to the start)", ["Arrived", COURSE_REGION], m, 40)
        time.sleep(2)
        m = mark()
        say_in_app(f"//route {name}")
        done = run.expect("//route plays it across the border to its end", [f"Route finished: {name}"], m, 90)
        time.sleep(1.5)
        here = api("LLAgent", "getPosition")["global"]
        off = math.dist(here[:2], end[:2])
        run.check("where the walk ended", done and off < 3, f"{off:.1f} m from it")
        if not done:
            print("     (said: " + " | ".join(e.get("text") or "" for e in said_since(m))[-1500:] + ")")
        delete_by_search(run, name)
    finally:
        back_to_chat()
        say_in_app("//record discard")   # (a recording left going by a step that stopped early)
        say_in_app("//tp Atelier/%d/%d/%d" % TP_TO)
        time.sleep(5)


def walk_mode(run):
    """Walk mode (Walk/WalkMode.cs, docs/access-app.md > Walk mode) on a course of its own in Atelier East: F3 into it,
    F1, the heading, the sonar's words, walking north by a held Up arrow with footsteps on the stone floor, the opening
    passed on the left, the warning before the end wall (named Blue Door) and Blocked at it, a step turn each way,
    what's ahead, touching it, Space, the drop warning walking off a platform, Escape back to the Say box."""
    bx, by = COURSE
    if NO_GPU and not SOFTWARE:
        st = wait(lambda: (lambda s: s if s["state"] == "stopped" else None)(api("SojournView", "status")), 60, 1)
        run.check("(the view out of sight has given up: no Vulkan driver; walk mode on the collision model alone)", st,
                  json.dumps(api("SojournView", "status")))
    elif not NO_RENDER and not SOFTWARE:
        # after the faked GPU loss (SOJOURN_OFFSCREEN_LOSE_AFTER), so the comparison below doesn't meet a restarting view
        wait(lambda: (lambda st: st["state"] == "running" and st["losses"] >= 1)(api("SojournView", "status")), 60, 1)
    try:
        walk_crossing(run)
    finally:
        # the kart: in Atelier still, or across in Atelier East (where the visitor owns nothing else yet)
        try:
            v("derez Walk Kart", timeout=15)
        except Exception:
            pass
        clear_owned(COURSE_REGION, visitor_id())
    m = mark()
    say_in_app(f"//tp {COURSE_REGION}/{bx}/{by + 1}/25")
    if not run.expect("to the walk course", ["Arrived", COURSE_REGION], m, 40):
        return
    time.sleep(1.5)
    g = api("TesselAgent", "ground", points=[[bx, by]])["heights"][0] or 21.0
    tester_id = api("LLAgent", "getID")["id"]
    if n := clear_course(tester_id):
        print(f"     (cleared {n} object(s) a run before left in {COURSE_REGION})")
    ids = []
    try:
        ids = build_course(g)
        run.check("the course is built", len(ids) == 10, f"{len(ids)} prims on the ground at {g:.1f} m")
        m = mark()
        say_in_app("//face 90")
        run.expect("facing north", ["Facing", "north"], m, 8)
        # the collision model (no GPU) knows the course once the region has sent it
        seen = wait(lambda: (lambda h: h if h.get("hit") else None)(api("SojournCollision", "collide", dir=[1, 0, 0], max=5)), 30, 1)
        run.check("the collision model has the course (the east wall met)", seen and abs(seen["distance"] - 1.4) < 0.3, json.dumps(seen))
        st = api("SojournCollision", "status")
        run.check("(its status: prims indexed and shapes ready)", st.get("placed", 0) >= 10 and st.get("ready", 0) >= 1, json.dumps(st))
        if not VIEWLESS and not SOFTWARE:   # (on the processor the renderer draws nothing between pictures)
            walk_models_agree(run)

        m = mark()
        key("f3")
        f = wait_focus(lambda d: d["id"] == "walkPad", 5)
        run.check("F3 puts the focus on the walk pad", f, label(f) if f else focus_label())
        run.expect("and says walk mode is on", ["Walk mode", "F1"], m, 5)
        key("f1")
        d = wait_focus(lambda d: d["id"] == "keysText", 5)
        run.check("F1 there opens walk mode's keys in a window", d and "Up and Down arrows: walk forward and back" in [l.rstrip(".") for l in value_of("id=keysText").splitlines()],
                  label(d) if d else focus_label())
        key("esc")
        d = wait_focus(lambda d: d["id"] == "walkPad", 5)
        run.check("Escape closes it, back on the walk pad (still in walk mode)", d, label(d) if d else focus_label())
        m = mark()
        key("h")
        run.expect("H says the heading", ["Facing north, 90 degrees"], m, 5)

        m = mark()
        key("s")
        got = run.expect("S, the sonar: walls on both sides", ["Walls:", "left 1 metre", "right 1 metre"], m, 8)
        text = (got or {}).get("text") or ""
        run.check("the sonar finds the corridor's end ahead", re.search(r"ahead 1\d metres", text), text)
        run.check("the sonar finds the opening in the west wall, about 1.4 m wide",
                  re.search(r"Opening at (eleven|twelve) o'clock, \d metres? away, 1\.[2-6] metres wide", text),
                  f"{text} (gaps: {json.dumps(api('SojournWalk', 'status').get('gaps'))})")
        run.check("and the corridor's mouth behind",
                  re.search(r"Opening at (five|six|seven) o'clock, (under a metre|1 metre) away, [23](?:\.\d)? metres wide", text), text)

        print(f"     (the collision model after the sonar: {json.dumps(api('SojournCollision', 'status'))})")
        st0 = api("SojournWalk", "status")
        m = mark()
        u("keydown focus | 38")
        opening = walk_said(m, ["Opening on the left"], 6)
        x1, y1, *_ = walk_pos()
        warned = walk_said(m, ["Blue Door,"], 8)
        at_warning = walk_pos()
        st1 = api("SojournWalk", "status")
        blocked = walk_said(m, ["Blocked."], 8)
        u("keyup focus | 38")
        time.sleep(1.5)
        x, y, z, _ = tester_where()
        run.check("held Up walks north to the end wall", y > by + 10 and abs(x - bx) < 0.6,
                  f"at {x:.1f}, {y:.1f} (the wall's face at {by + 12:.1f})")
        run.check("passing the opening on the left is said", opening and by + 5.5 < y1 < by + 9.5,
                  f"{opening['text']} at {y1 - by:.1f} m along" if opening else "not said")
        run.check("a warning before the bump, naming the wall", warned and at_warning[1] < by + 11.2,
                  f"{warned['text']} at {at_warning[1] - by:.1f} m along (the wall at 12)" if warned else "not said")
        run.check("Blocked at the wall", blocked, blocked["text"] if blocked else "not said")
        run.check("footsteps by surface: stone on the course floor", st1.get("surface") == "stone", json.dumps(st1)[:200])
        played = st1.get("cues_played", 0) - st0.get("cues_played", 0)
        run.check("cue sounds sent to world audio while walking", st1.get("sound") and played >= 4,
                  f"{played} sounds, sound {st1.get('sound')}")

        m = mark()
        key("left")
        run.expect("a tap of Left turns a step: north-northwest", ["north-northwest"], m, 5)
        m = mark()
        key("right")
        run.expect("and Right back: north", ["north."], m, 5)
        time.sleep(0.5)
        m = mark()
        key("a")
        run.expect("A says what's ahead", ["Blue Door", "ahead"], m, 5)
        m = mark()
        key("t")
        run.expect("T touches it", ["Touched Blue Door"], m, 8)
        m = mark()
        key("space")
        run.expect("Space stops", ["Stopped."], m, 5)
        m = mark()
        key("l")
        run.expect("L says where", [COURSE_REGION, "on stone"], m, 5)

        # the drop: onto the platform, then walk south off it
        key("f3")
        wait_focus(lambda d: d["id"] == "chatInput", 5)
        m = mark()
        say_in_app(f"//go tp xyz {bx - 8},{by + 6.4},{g + 4}")
        run.expect("onto the platform", ["Teleported"], m, 20)
        time.sleep(2)
        m = mark()
        say_in_app("//face 270")
        run.expect("facing south", ["Facing", "south"], m, 8)
        time.sleep(1)
        top = tester_where()
        key("f3")
        wait_focus(lambda d: d["id"] == "walkPad", 5)
        m = mark()
        u("keydown focus | 38")
        drop = walk_said(m, ["Drop ahead"], 5)
        at_drop = walk_pos()
        u("keyup focus | 38")
        run.check("a drop ahead is said before walking off the platform", drop and at_drop[2] > g + 3.3,
                  f"{drop['text']} at height {at_drop[2] - g:.1f} m over the ground, {at_drop[1] - by - 3:.1f} m from the edge"
                  if drop else f"not said (on the platform at {top[2] - g:.1f} m)")
        time.sleep(1.5)
        m = mark()
        key("esc")
        f = wait_focus(lambda d: d["id"] == "chatInput", 5)
        run.check("Escape leaves walk mode, the focus back in Say", f, label(f) if f else focus_label())
        run.expect("saying so", ["Walk mode off"], m, 5)
        walk_vehicle(run, bx, by, g)
        walk_paths(run, bx, by, g)
    finally:
        if ids:
            run.check("the course is cleared away", clear_course(tester_id) >= len(ids), f"{len(ids)} prims")
        back_to_chat()
        say_in_app("//tp Atelier/%d/%d/%d" % TP_TO)
        time.sleep(5)
        # the visitor's things in both regions (its own cleanup at the end only reaches the region it's in)
        for region in (REGION, COURSE_REGION):
            clear_owned(region, visitor_id())


# ---- the end ------------------------------------------------------------------------------------------------------------

def shutdown(run, server_pid, app_pid, visitor):
    if app_pid:
        try:
            u(f"close {app_pid}")
        except Exception:
            pass
        if wait(lambda: not alive(app_pid), 10, 0.5) is None:
            kill(app_pid)
            print(f"     (the app didn't close in 10 s after WM_CLOSE: killed)")
    if visitor:
        for cmd in ("cleanup", "quit"):
            try:
                print(f"     (visitor {cmd}: {v(cmd, timeout=10)})")
            except Exception:
                pass
            if cmd == "cleanup":
                time.sleep(2)
        try:
            visitor.wait(15)
        except subprocess.TimeoutExpired:
            visitor.kill()
    if server_pid:
        try:
            u("quit", timeout=5)
        except Exception:
            pass
        if wait(lambda: not alive(server_pid), 5, 0.5) is None:
            kill(server_pid)
    wait(lambda: not ours(), 10, 1)
    strays = ours()
    run.check("nothing left running", not strays, "; ".join(f"{w} {p}" for p, w in strays) or "app, visitor and QuireUia all gone")


def finish(run):
    total = run.ok + len(run.failed)
    print(f"\n{run.ok} of {total} passed" + (f"; failed: {', '.join(run.failed)}" if run.failed else ""))
    print(f"logs: {EVENTS.relative_to(ROOT)}, {(OUT / 'visitor' / 'visitor.log').relative_to(ROOT)}, {(OUT / 'app').relative_to(ROOT)}")
    return 0 if not run.failed else 1


# ---- the package checked as a tester would run it ---------------------------------------------------------------------

CHECK_ACCOUNT = ("Package", "Tester")   # its own account, so a check never throws out a person's Access Tester
CHECK_DESKTOP = "sojourn-package"
CHECK_Q_PORT = 9905
CHECK_API_PORT = 9735                    # the agent API, which a package with the assistant opens for its sidecar
SHIPPED_DLLS = ("nvdaControllerClient.dll", "sojourn_voice.dll", "openjp2.dll", "shaderc_shared.dll")
OURS_NAMED = re.compile(r"(?i)sojourn|metaverse|quire")


def ps_json(script):
    """A PowerShell script's ConvertTo-Json output as a list."""
    r = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True,
                       creationflags=CREATE_NO_WINDOW)
    out = r.stdout.strip()
    if not out:
        return []
    got = json.loads(out)
    return got if isinstance(got, list) else [got]


def sockets_of(pid):
    """The process's TCP connections and listeners and its UDP endpoints: (kind, local, port, remote, state)."""
    rows = ps_json(f"@(Get-NetTCPConnection -OwningProcess {pid} -ErrorAction SilentlyContinue | ForEach-Object {{ "
                   "[pscustomobject]@{k='tcp'; l=$_.LocalAddress; p=$_.LocalPort; r=$_.RemoteAddress; s=[string]$_.State} }) + "
                   f"@(Get-NetUDPEndpoint -OwningProcess {pid} -ErrorAction SilentlyContinue | ForEach-Object {{ "
                   "[pscustomobject]@{k='udp'; l=$_.LocalAddress; p=$_.LocalPort; r=''; s=''} }) | ConvertTo-Json -Compress")
    return [(x["k"], x["l"], x["p"], x["r"], x["s"]) for x in rows]


def loopback(addr):
    return addr in ("127.0.0.1", "::1") or addr.startswith(("127.", "::ffff:127."))


def modules_of(pid):
    return [m["p"] for m in ps_json(f"(Get-Process -Id {pid}).Modules | ForEach-Object {{ [pscustomobject]@{{p=$_.FileName}} }} "
                                    "| ConvertTo-Json -Compress")]


def gpu_of(pid):
    """(the GPU engines' running time so far in 100 ns, dedicated video memory in bytes) for the process."""
    rows = ps_json(f"$t = (Get-Counter '\\GPU Engine(pid_{pid}_*)\\Running Time' -ErrorAction SilentlyContinue).CounterSamples | "
                   "Measure-Object RawValue -Sum; "
                   f"$m = (Get-Counter '\\GPU Process Memory(pid_{pid}_*)\\Dedicated Usage' -ErrorAction SilentlyContinue).CounterSamples | "
                   "Measure-Object RawValue -Sum; [pscustomobject]@{t=[double]$t.Sum; m=[double]$m.Sum} | ConvertTo-Json -Compress")
    return (rows[0]["t"], rows[0]["m"]) if rows else (0, 0)


def known_folders():
    """The profile's folders a program might write to on its own (the real ones: .NET's GetFolderPath ignores %LOCALAPPDATA%)."""
    names = ("LocalApplicationData", "ApplicationData", "UserProfile", "MyDocuments", "MyPictures", "MyVideos", "MyMusic",
             "Desktop", "CommonApplicationData")
    rows = ps_json("@(" + ",".join(f"[pscustomobject]@{{n='{n}'; p=[Environment]::GetFolderPath('{n}')}}" for n in names)
                   + ") | ConvertTo-Json -Compress")
    return [Path(r["p"]) for r in rows if r["p"]]


def top_level(folders):
    """Each folder's entries: path -> mtime."""
    seen = {}
    for d in folders:
        try:
            for e in os.scandir(d):
                try:
                    seen[e.path] = e.stat(follow_symlinks=False).st_mtime
                except OSError:
                    pass
        except OSError:
            pass
    return seen


def tree_hashes(root):
    import hashlib
    out = {}
    for f in sorted(root.rglob("*")):
        rel = f.relative_to(root).as_posix()
        if f.is_dir():
            out[rel + "/"] = "dir"
        else:
            h = hashlib.sha256()
            with open(f, "rb") as fh:
                for chunk in iter(lambda: fh.read(1 << 20), b""):
                    h.update(chunk)
            out[rel] = h.hexdigest()
    return out


def files_under(root):
    return sorted(f.relative_to(root).as_posix() for f in root.rglob("*") if f.is_file()) if root.exists() else []


def others_running():
    """The VR client or dev mode running (the user may be in the headset): their command lines."""
    ps = ("Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'dev\\.py|Sojourn\\.Client' -and "
          "$_.Name -ne 'powershell.exe' } | ForEach-Object { \"$($_.ProcessId) $($_.CommandLine)\" }")
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, creationflags=CREATE_NO_WINDOW)
    return [l for l in r.stdout.splitlines() if l.strip()]


def package_check():
    """`package --check [ZIP]`: the zip (default the newest in local/dist) unpacked in a fresh folder outside the repo
    and run as a tester would, on a hidden desktop (docs/access-app.md > Packaging): the local grid, the reader on,
    no voice, world audio to a file, data in a folder of the check's own; prints ok/FAIL per check, then removes
    everything it made. --keep leaves the folder (its path printed)."""
    import tempfile
    import zipfile
    import grid
    global Q_PORT, EVENTS, APP_PID
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    relay = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv[2:-1] else None
    rest = [a for a in sys.argv[2:] if a not in ("--check", "--keep", "--bare", "--no-software", "--relay", relay)]
    keep = "--keep" in sys.argv[2:]
    # a PC unlike ours: no Vulkan driver it can use (a VM, Microsoft's basic display adapter, an old card's driver) and
    # no Media Foundation (Windows' N editions), which sojourn_voice.dll imports, so it won't load: removing it fails the
    # same way. The view then draws on the processor (the package's lavapipe folder); --no-software removes that too, as
    # packages before it were, and the view gives up
    bare = "--bare" in sys.argv[2:]
    no_software = bare and "--no-software" in sys.argv[2:]
    zips = [Path(rest[0]).resolve()] if rest else sorted(DIST.glob("SojournAccess-*.zip"), key=lambda p: p.stat().st_mtime)
    if not zips or not zips[-1].exists():
        print("no zip (python tools/access.py package)")
        return 1
    zip_path = zips[-1]
    version = re.search(r"SojournAccess-(0\.1\.0\.\d+)", zip_path.name)
    version = version[1] if version else "?"
    print(f"checking {zip_path.name}" + (" as on a bare PC (no Vulkan driver, no sojourn_voice.dll" +
                                         (", no software driver)" if no_software else "; the view on the processor)") if bare else ""))

    others = others_running()
    if others:
        print("the VR client or dev mode is running (the user may be in the headset); not starting a renderer beside it:\n  "
              + "\n  ".join(others))
        return 1
    if not QUIREUIA.exists():
        print("missing local/bin/QuireUia (python tools/access.py tools)")
        return 1
    if not grid.ready():
        print("the local grid is down (python tools/grid.py start)")
        return 1
    Q_PORT = CHECK_Q_PORT
    if not port_free(Q_PORT):
        print(f"port {Q_PORT} is in use (another check?)")
        return 1
    grid.account(*CHECK_ACCOUNT, PASSWORD)
    if online(*CHECK_ACCOUNT):
        print(f"{' '.join(CHECK_ACCOUNT)} is logged in already (another check?)")
        return 1

    run = Run()
    check = run.check
    work = Path(tempfile.mkdtemp(prefix="sojourn-package-check-"))   # under %TEMP%, outside the repo
    pkg, data, out = work / "package", work / "LocalAppData" / "Sojourn Access", work / "out"
    env_dirs = {"TEMP": work / "temp", "LOCALAPPDATA": work / "LocalAppData", "APPDATA": work / "AppData"}
    for d in (pkg, out, *env_dirs.values()):
        d.mkdir(parents=True, exist_ok=True)
    EVENTS = out / "events.jsonl"
    server = app = None
    try:
        with zipfile.ZipFile(zip_path) as z:
            z.extractall(pkg)
        tops = [d for d in pkg.iterdir() if d.is_dir()]
        folder = tops[0] if len(tops) == 1 else pkg
        exe = folder / "SojournAccess.exe"
        if not check("the zip holds one folder with SojournAccess.exe", exe.exists(), str(exe.relative_to(work))):
            raise Stop
        if bare:
            (folder / "sojourn_voice.dll").unlink()
        if no_software:
            shutil.rmtree(folder / "lavapipe")
        software = bare and (folder / "lavapipe" / "vulkan_lvp.dll").exists()
        # a package with the assistant (python/ and assistant/ beside the exe): its sidecar runs, off (no provider) or,
        # with --relay NAME, on Studio Quire's relay with that token (tools/relay.py), asked something and capped
        assistant = (folder / "assistant" / "main.py").exists()
        if relay and not assistant:
            check("--relay needs a package with the assistant", False, zip_path.name)
            raise Stop
        keys = work / "keys"
        if relay:
            import relay as relaymod
            sys.path.insert(0, str(folder / "assistant"))
            os.environ["SOJOURN_KEYS"] = str(keys)
            sys.dont_write_bytecode = True   # (nothing written into the package's folder)
            import provider as providermod   # the package's own: kept as the app's provider keeps it
            providermod.save_key("relay", relaymod.read_secret(relaymod.token_file(relay)))
            del os.environ["SOJOURN_KEYS"]
            data.mkdir(parents=True, exist_ok=True)
            (data / "vr-settings.json").write_text(json.dumps({"assistantProvider": "relay"}), encoding="utf-8")
        elif assistant:
            # a Claude account saved, as every earlier build's settings have it (the old default): not offered packaged
            data.mkdir(parents=True, exist_ok=True)
            (data / "vr-settings.json").write_text(json.dumps({"assistantProvider": "claude"}), encoding="utf-8")
        before = tree_hashes(folder)
        profile = known_folders()
        profile_before = top_level(profile)
        started = time.time()

        # a tester's PC: no .NET of ours on the way (the SDK and any install off PATH, no DOTNET_ variables), %TEMP%,
        # %TMP%, %LOCALAPPDATA% and %APPDATA% pointed into the check's folder so whatever follows them shows up there
        path = without_claude(os.pathsep.join(p for p in os.environ.get("PATH", "").split(os.pathsep) if "dotnet" not in p.lower()))
        env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("DOTNET_", "SOJOURN_", "ACCESS_"))}
        env.update(PATH=path, TEMP=str(env_dirs["TEMP"]), TMP=str(env_dirs["TEMP"]), LOCALAPPDATA=str(env_dirs["LOCALAPPDATA"]),
                   APPDATA=str(env_dirs["APPDATA"]),
                   SOJOURN_TEST_INPUT="keys",            # posted keys judged as the hand's
                   SOJOURN_TEST_NO_CLIPBOARD="1",
                   SOJOURN_VOICE_OUT_FILE=str(out / "heard.wav"),   # world audio to a file, never the speakers
                   SOJOURN_VOICE_MIC_FILE="none")         # and no microphone
        if bare:
            env["VK_LOADER_DRIVERS_DISABLE"] = "*"      # the Vulkan loader (1.3.234 on) then finds no driver at all
        if relay:
            env["SOJOURN_KEYS"] = str(keys)             # the token kept above (the app and its sidecar read it there)

        server = launch_hidden_on(CHECK_DESKTOP, [QUIREUIA, "serve", "--port", Q_PORT, "--log", EVENTS])
        if not check("QuireUia's server answers", wait(lambda: u("help", timeout=5), 20), f"pid {server}"):
            raise Stop
        listening = wait(lambda: any(e.get("src") == "server" and e.get("kind") == "listening" and e.get("text") == "com notification"
                                     for e in events()), 15, 0.5)
        if not check("it's listening for announcements", listening, "UIA notifications registered" if listening else "no"):
            raise Stop

        args = [exe, "--local", "--user", " ".join(CHECK_ACCOUNT), "--password", PASSWORD, "--start",
                "uri:%s&%d&%d&%d" % (REGION, *TESTER_START), "--autologin", "--reader", "on", "--no-voice", "--base", data, "--port", CHECK_API_PORT]
        t0 = time.time()
        app = APP_PID = launch_hidden_on(CHECK_DESKTOP, args, env=env, cwd=folder)
        u(f"watch {app}")

        # ---- logged in, the window, the focus, arriving said
        # the main window shows on arrival; the login form ("Sojourn Access: log in") hides then
        title = wait(lambda: next((l for l in u(f"windows {app}").splitlines()
                                   if '"Sojourn Access' in l and "log in" not in l), None), LOGIN_SECS, 1)
        took = time.time() - t0
        if not check("the main window, titled Sojourn Access", title, f"{title} ({took:.0f} s from start)" if title else
                     f"not after {LOGIN_SECS} s; alive: {alive(app)}; windows: {u(f'windows {app}')}"):
            raise Stop
        check("logged in to the local grid", online(*CHECK_ACCOUNT), f"{' '.join(CHECK_ACCOUNT)} in the grid's users")
        mods = modules_of(app)
        dotnets = [m for m in mods if re.search(r"(?i)\\dotnet(-sdk)?\\|\\(coreclr|hostfxr|hostpolicy)\.dll$", m)]
        check("started with no .NET of its own (the runtime inside the exe)", not dotnets,
              "no dotnet folder, hostfxr or coreclr among its modules" if not dotnets else "; ".join(dotnets))
        try:
            u(f"activate {app}")
        except ToolError as e:
            print(f"     (activate: {e})")
        f = wait_focus(lambda d: d["id"] == "chatInput", 15)
        check("the focus lands in the Say box", f, label(f) if f else focus_label())
        run.expect("arriving is read out", ["Logged in"], 0, 10)
        if bare:
            run.expect("and that sounds can't play", ["Logged in", "Sounds can't play on this PC"], 0, 5)

        def network(when):
            socks = sockets_of(app)
            listen = [s for s in socks if s[0] == "tcp" and s[4] == "Listen"]
            # "Bound" rows are Windows' record of an outgoing socket's port (bound to any address before ConnectEx, as
            # every program's are): nothing can connect to them; the connections themselves are rows of their own
            bound = [s for s in socks if s[0] == "tcp" and s[4] == "Bound"]
            used = [s for s in socks if s not in bound]
            wide = [s for s in used if not loopback(s[1]) or (s[0] == "tcp" and not loopback(s[3]) and s[4] != "Listen")]
            if assistant:   # the agent API, for the sidecar: on loopback, at the port asked for, and nothing else
                api_only = all(s[2] == CHECK_API_PORT and loopback(s[1]) for s in listen) and listen
                check(f"listening only on the agent API, on loopback ({when})", api_only,
                      "; ".join(f"{s[1]}:{s[2]}" for s in listen) or "nothing listening")
            else:
                check(f"nothing listening on TCP ({when})", not listen,
                      f"{len(socks) - len(bound) - sum(1 for s in socks if s[0] == 'udp')} TCP connections, none listening" if not listen else
                      "; ".join(f"{s[1]}:{s[2]}" for s in listen))
            check(f"TCP and UDP on loopback only ({when})", used and not wide,
                  ", ".join(sorted({f"{s[0]} {s[1]}" + (f"->{s[3]}" if s[3] else f":{s[2]}") for s in used}))[:300]
                  + f" ({len(bound)} outgoing sockets' Bound rows aside)" if not wide
                  else "; ".join(f"{s[0]} {s[1]}:{s[2]}->{s[3]} {s[4]}" for s in wide))
        network("logged in")
        if not assistant:
            check(f"nothing at all on the agent API's port {CHECK_API_PORT}", port_free(CHECK_API_PORT), "connection refused"
                  if port_free(CHECK_API_PORT) else f"something answers on {CHECK_API_PORT} (ours if the app is the owner above)")
        logs = data / "logs"

        def log_text():
            return "\n".join(p.read_text(encoding="utf-8", errors="replace") for p in sorted(logs.glob("*.log")))
        sidecar = None
        if assistant:
            import psutil

            def bundled_python():
                try:
                    return next((c for c in psutil.Process(app).children(recursive=True)
                                 if Path(c.exe()).parent == folder / "python"), None)
                except psutil.Error:
                    return None
            sidecar = wait(bundled_python, 30, 1)
            check("the assistant's sidecar runs on the package's own Python", sidecar,
                  f"pid {sidecar.pid}, {sidecar.exe()}" if sidecar else "no python.exe from the package under the app")
            want = "relay: ready" if relay else "claude: none"
            said = wait(lambda: re.search(rf"provider: {want}.*", log_text()), 60, 1)
            check(f"its provider checked ({want})", said, said[0][:200] if said else
                  "; ".join(re.findall(r"assistant: .*", log_text())[-4:])[:300])
            key("alt+9")   # (a tab's controls are in the UIA tree only once it has been shown)
            d = wait_focus(lambda d: d["id"] == "assistantInput", 5)
            ask_name = (d or {}).get("name") or focus_label()
            check("Alt+9's box is the assistant's, not 'not in this build'", ask_name == "Ask the assistant", ask_name)
            back_to_chat()
            # Alt+0: no Claude account in a packaged build, the saved one shown as none chosen; a token or key otherwise
            key("alt+0")
            wait_focus(lambda d: d["name"] == "Settings" or d["type"] == "ListItem", 4)
            row = wait(lambda: find("type=ListItem;name~=Provider for the assistant:"), 5)
            names = [d["name"] for d in find("type=ListItem")] if row else []
            shown = next((n for n in names if n.startswith("Provider for the assistant:")), "")
            check("Alt+0's provider row offers no Claude account",
                  shown and "Claude account" not in " ".join(names) and ("Studio Quire" in shown if relay else "none chosen yet" in shown),
                  shown or "no provider row")
            back_to_chat()

        # ---- F1, //about, Control+D without the assistant, chat
        key("f1")
        d = wait_focus(lambda d: d["id"] == "keysText", 5)
        check("F1 opens the keys in a window", d and "Alt+1: nearby chat." in value_of("id=keysText").splitlines(), label(d) if d else focus_label())
        key("esc")
        back_to_chat()
        m = mark()
        say_in_app("//about")
        got = run.expect("//about is read out", ["Sojourn Access " + version], m, 8)
        if got:
            text = got["text"]
            check("//about names the public source", "github.com/studio-quire/sojourn" in text, text[:200])
            check("//about names the data folder", str(data) in text, text[-160:])
        if not relay:
            m = mark()
            key("ctrl+d")
            got = run.expect("Control+D answers", ["Describing needs the assistant"], m, 10)
            if got and assistant:
                check("and says why it's off and how to switch it on", "Alt+0" in got["text"] and "Studio Quire" in got["text"],
                      got["text"][:300])
            wait(lambda: False, 1.5)
            m2 = mark()
            key("ctrl+d")
            wait(lambda: said_since(m2), 10)
            wait(lambda: False, 1.5)
            mark_words = "Describing needs the assistant" if assistant else "this build doesn't include"
            absent = [e["text"] for e in said_since(m) if mark_words in (e.get("text") or "")]
            check("Control+D's absence said once, not again", len(absent) == 1 and said_since(m2),
                  f"said {len(absent)} time(s) in two presses; the second: {' | '.join(e.get('text') or '' for e in said_since(m2))[:160]}")
        back_to_chat()
        line = f"package check {uuid.uuid4().hex[:6]}"
        say_in_app(line)
        heard = wait(lambda: line in value_of("id=chatHistory"), 10)
        check("chat sent (the grid's echo in the Nearby chat history)", heard, line if heard else value_of("id=chatHistory")[-200:])

        # ---- the renderer out of sight, the natives
        if software:
            # no GPU it can use: it draws on the processor (Render's Gpu/SoftwareVulkan.cs), from the package's lavapipe
            moved = wait(lambda: re.search(r".*can't run on this PC's graphics.*drawing on the processor instead.*", log_text()), 60, 1)
            up = wait(lambda: re.search(r"the view out of sight is up on (llvmpipe[^,]*)", log_text()), 30, 1)
            check("without a GPU it can use, the view draws on the processor", moved and up and alive(app),
                  up[0] if up else moved[0][:200] if moved else "no such line in the session's log")
            lowered = re.search(r"\d+ of the software driver's threads put below normal priority", log_text())
            check("the software driver's threads below normal priority (the screen reader first)", lowered,
                  lowered[0] if lowered else "no such line")
            if assistant:
                # the assistant (even off) makes its pictures possible: what's around is drawn once ahead of them
                warm = wait(lambda: re.search(r"what's around drawn once ahead of any picture, .*", log_text()), 90, 1)
                check("and draws: what's around drawn once, ahead of any picture", warm and alive(app), warm[0] if warm else "no such line")
            failed = re.findall(r".*(?:couldn't start|the GPU was lost|stopped:).*", log_text())
            check("no renderer failures in the log", not failed, "; ".join(failed)[:200] or "none")
            dlls = ["nvdaControllerClient.dll", "openjp2.dll", "shaderc_shared.dll", "lavapipe/vulkan_lvp.dll"]
        elif bare:
            down = wait(lambda: re.search(r".*the view out of sight couldn't start.*", log_text()), 60, 1)
            check("the renderer's absence is logged, not fatal", down and alive(app), down[0][:200] if down else "no such line")
            up = re.search(r"the view out of sight is up on ([^:]+)", log_text())
            check("and no renderer came up", not up, up[0] if up else "none")
            # the keys a person might expect to lean on the view: walk mode and its sonar need none (the collision model
            # without a GPU, since 2026-10-30), so they work as usual; describing answers from the lists
            for k, what, plain in (("f3", "F3 (walk mode)", "Walk mode."), ("s", "S (the sonar)", ""),
                                   ("esc", "Escape (walk mode off)", "Walk mode off"), ("ctrl+d", "Control+D", "")):
                m = mark()
                key(k)
                said = wait(lambda: said_since(m), 8)
                wait(lambda: False, 1.5)
                text = " | ".join(e.get("text") or "" for e in said_since(m))
                check(f"{what} answers without the view", said and alive(app) and plain in text and "graphics" not in text,
                      text[:220] or "silence")
            back_to_chat()
            dlls = ["nvdaControllerClient.dll"]   # sojourn_voice is gone; openjp2 and shaderc load with a renderer only
        else:
            up = wait(lambda: re.search(r"the view out of sight is up on ([^:]+)", log_text()), 60, 1)
            check("the renderer out of sight starts", up, up[0] if up else "no such line in the session's log")
            g0 = gpu_of(app)
            wait(lambda: False, 5)
            g1 = gpu_of(app)
            check("and draws (the GPU working for it)", g1[0] > g0[0] and g1[1] > 50e6,
                  f"{(g1[0] - g0[0]) / 1e4:.0f} ms of GPU engine time in 5 s, {g1[1] / 1e6:.0f} MB of video memory")
            failed = re.findall(r".*(?:couldn't start|the GPU was lost|stopped:).*", log_text())
            check("no renderer failures in the log", not failed, "; ".join(failed)[:200] or "none")
            dlls = SHIPPED_DLLS
        if relay:
            relay_steps(run, relay)
        # what it costs a PC while nothing happens (a tester's may be a small laptop, and NVDA wants the CPU too)
        import psutil
        p = psutil.Process(app)
        c0, g0, t0i = sum(p.cpu_times()[:2]), gpu_of(app), time.time()
        wait(lambda: False, 20)
        c1, g1, dt = sum(p.cpu_times()[:2]), gpu_of(app), time.time() - t0i
        print(f"     idle for {dt:.0f} s: CPU {100 * (c1 - c0) / dt:.0f}% of one core, GPU {(g1[0] - g0[0]) / 1e4 / dt:.0f} ms a second, "
              f"{p.memory_info().rss / 1e6:.0f} MB in memory, {g1[1] / 1e6:.0f} MB of video memory")
        mods = modules_of(app)
        for dll in dlls:
            where = next((m for m in mods if Path(m).name.lower() == Path(dll).name.lower()), None)
            check(f"{dll} loaded from the package's folder", where and Path(where).parent == (folder / dll).parent, where or "not loaded")
        foreign = [m for m in mods if str(ROOT).lower() in m.lower()]
        check("nothing loaded from the repo", not foreign, "; ".join(foreign) or f"{len(mods)} modules")
        network("after the renderer and chat")
        crashes = re.findall(r".*(?:Unhandled exception|fail:|assistant: Traceback|assistant: \w*Error\b).*", log_text())
        check("no unhandled exceptions or failures in the log", not crashes, "; ".join(crashes)[:300] or "none")
    except Stop:
        pass
    except Exception as e:
        check("the check (stopped)", False, f"{type(e).__name__}: {e}")
    finally:
        if app:
            try:
                u(f"close {app}")
            except Exception:
                pass
            if wait(lambda: not alive(app), 20, 0.5) is None:
                kill(app)
                print("     (the app didn't close in 20 s after WM_CLOSE: killed)")
        if server:
            try:
                u("quit", timeout=5)
            except Exception:
                pass
            if wait(lambda: not alive(server), 5, 0.5) is None:
                kill(server)
    left = [p for p in (app, server) if p and alive(p)]
    check("the app and QuireUia gone", not left, "; ".join(map(str, left)) or "both exited")
    if app:
        logged_out = wait(lambda: not online(*CHECK_ACCOUNT), 20, 1)
        check("logged out of the grid on close", logged_out, "gone from the grid's users" if logged_out else "still in the grid's users")

    if "before" in locals():
        after = tree_hashes(folder)
        changed = sorted(k for k in set(before) | set(after) if before.get(k) != after.get(k))
        check("the package's folder unchanged", not changed, f"{len(before)} entries, the same bytes" if not changed
              else "; ".join(changed[:10]))
        strays = {k: files_under(d) for k, d in env_dirs.items()}
        strays["LOCALAPPDATA"] = [f for f in strays["LOCALAPPDATA"] if not f.startswith("Sojourn Access/")]
        driver = {k: [f for f in fs if f.split("/")[0] in ("NVIDIA", "D3DSCache", "AMD", "Intel")] for k, fs in strays.items()}
        strays = {k: [f for f in fs if f not in driver[k]] for k, fs in strays.items()}
        written = [f"%{k}%/{f}" for k, fs in strays.items() for f in fs]
        check("nothing written by way of %TEMP%, %LOCALAPPDATA% or %APPDATA% but the data folder", not written,
              "none" + (f" (the GPU driver's cache aside: {sum(map(len, driver.values()))} files)" if any(driver.values()) else "")
              if not written else "; ".join(written[:10]))
        profile_after = top_level(profile)
        new = [p for p in profile_after if p not in profile_before]
        touched = [p for p, t in profile_after.items() if p in profile_before and t != profile_before[p] and t >= started
                   and OURS_NAMED.search(Path(p).name)]
        ours_new = [p for p in new if OURS_NAMED.search(Path(p).name)]
        check("nothing of ours in the profile's own folders (AppData, Documents, Pictures...)", not ours_new and not touched,
              "; ".join(ours_new + touched) if ours_new or touched else
              "nothing new or changed named as ours" + (f"; new there, by other programs: {', '.join(Path(p).name for p in new)[:200]}" if new else ""))
        check("its data in the data folder", (data / "logs").exists(), ", ".join(sorted({f.split('/')[0] for f in files_under(data)})))
        heard = out / "heard.wav"
        if not bare:
            check("world audio went to the file, not the speakers", heard.exists() and heard.stat().st_size > 44,
                  f"{heard.stat().st_size / 1e6:.1f} MB" if heard.exists() else "no file")

    if keep:
        print(f"     (kept: {work})")
    else:
        shutil.rmtree(work, ignore_errors=True)
        check("the check's folder removed", not work.exists(), str(work))
    total = run.ok + len(run.failed)
    print(f"\n{zip_path.name}: {run.ok} of {total} passed" + (f"; failed: {', '.join(run.failed)}" if run.failed else ""))
    return 0 if not run.failed else 1


def relay_steps(run, name):
    """`package --check --relay NAME`: the packaged assistant on Studio Quire's relay with the token NAME (tools/relay.py
    token new NAME; a token of its own, as this sets its cap to nothing for a moment): a typed request in Alt+9 and a
    description by Control+D answered and counted by the relay, then the cap reached and said in a plain sentence."""
    import relay as relaymod
    check = run.check
    token = relaymod.read_secret(relaymod.token_file(name))
    errors = re.compile(r"API Error|\{\s*\"type\"|billing_error|authentication_error")

    def history():
        return value_of("id=assistantHistory") or ""

    def reply_after(n, secs):
        """The assistant's next reply in Alt+9's history after its first n characters."""
        def look():
            m_ = re.search(r"Assistant: (.+)", history()[n:])
            return m_.group(1).strip() if m_ else None
        return wait(look, secs, 1)

    def ask(text, secs=180):
        key("alt+9")
        wait_focus(lambda d: d["id"] == "assistantInput", 5)
        n = len(history())
        u("set id=assistantInput |")
        type_text(text, "id=assistantInput")
        wait(lambda: value_of("id=assistantInput") == text, 3, 0.2)
        key("enter", target="id=assistantInput")
        return reply_after(n, secs)

    u0 = relaymod.usage(token) or {}
    t0 = time.time()
    said = ask("In one short sentence: which region am I in?")
    check("a typed request (Alt+9) is answered through the relay", said and "Atelier" in said and not errors.search(said),
          f"{time.time() - t0:.0f} s: {said[:200]}" if said else "no reply in 180 s")
    relaymod.wait_until(lambda: (relaymod.usage(token) or {}).get("requests", 0) > u0.get("requests", 0), 20, 1)
    u1 = relaymod.usage(token) or {}
    check("and the relay counted it", u1.get("requests", 0) > u0.get("requests", 0) and u1.get("spent_usd", 0) > u0.get("spent_usd", 0),
          f"${u0.get('spent_usd', 0):.4f} -> ${u1.get('spent_usd', 0):.4f}, {u1.get('requests', 0) - u0.get('requests', 0)} requests")

    back_to_chat()
    key("alt+9")
    wait_focus(lambda d: d["id"] == "assistantInput", 5)
    n = len(history())
    back_to_chat()
    m = mark()
    t0 = time.time()
    key("ctrl+d")
    run.expect("Control+D starts describing here", ["Describing"], m, 10)
    key("alt+9")
    wait_focus(lambda d: d["id"] == "assistantInput", 5)
    described = reply_after(n, 240)
    check("a description (Control+D) comes back through the relay", described and len(described) > 40 and not errors.search(described),
          f"{time.time() - t0:.0f} s: {described[:240]}" if described else "no reply in 240 s")
    u2 = relaymod.usage(token) or {}
    print(f"     the relay's count for {name} this month: ${u2.get('spent_usd', 0):.4f} over {u2.get('requests', 0)} requests "
          f"(these two: ${u2.get('spent_usd', 0) - u0.get('spent_usd', 0):.4f})")

    # the cap reached: the token's cap set to a millionth of a dollar for a moment, then put back
    reg = relaymod.registry()
    cap = reg[name]["cap"]
    try:
        reg[name]["cap"] = 0.000001
        relaymod.push_tokens(reg)
        relaymod.wait_until(lambda: (relaymod.usage(token) or {}).get("left_usd", 1) == 0, 60)
        back_to_chat()
        said = ask("Say hello.", 90)
        check("over the cap, the assistant says so in a plain sentence", said and "allowance" in said and not errors.search(said),
              said[:240] if said else "no reply in 90 s")
    finally:
        reg[name]["cap"] = cap
        relaymod.push_tokens(reg)
    back_to_chat()
    send_log_steps(run, relaymod)


def send_log_steps(run, relaymod):
    """Help > Send a log (docs/access-app.md > Sending a log), on the live relay with the check's token: the window's
    controls in order, the focus in its note; Review shows the filtered text with her own line; Send gets it to the
    relay with the note and says so, the focus back in Say; then the check deletes it. Whose words are left out is
    tools/logcheck's (a synthetic log), since this run has no one else in it."""
    nonce = uuid.uuid4().hex[:6]
    secret = relaymod.admin_secret()
    say_in_app(f"my own line {nonce}")
    time.sleep(1.5)
    say_in_app("//sendlog")
    d = wait_focus(lambda d: d["id"] == "sendLogNote", 5)
    if not run.check("//sendlog opens the window, the focus in What happened", d, label(d) if d else focus_label()):
        back_to_chat()
        return
    order = []
    for _ in range(5):
        key("tab")
        time.sleep(0.3)
        order.append((focused() or {}).get("id"))
    run.check("Tab goes Session, What's sent, Send, Review, Cancel",
              order == ["sendLogSession", "sendLogWhat", "sendLogSend", "sendLogReview", "sendLogCancel"], str(order))
    run.check("the session list starts at this session", (value_of("id=sendLogSession") or "").startswith("This session"),
              value_of("id=sendLogSession"))
    key("alt+w")
    wait_focus(lambda d: d["id"] == "sendLogNote", 3)
    type_text(f"a check's note {nonce}", "id=sendLogNote")
    key("alt+r")
    d = wait_focus(lambda d: d["id"] == "sendLogTextBox", 5)
    if run.check("Review shows what's sent in a read-only text", d, label(d) if d else focus_label()):
        text = value_of("id=sendLogTextBox") or ""
        run.check("it has her own line", f"my own line {nonce}" in text, f"{len(text)} characters")
        run.check("and what was read out", "read out (" in text, "")
        key("esc")
        # Alt+R presses Review without moving the focus (a button's mnemonic), so back is the note it was pressed from
        back = wait_focus(lambda d: d["id"] in ("sendLogNote", "sendLogReview"), 3)
        run.check("Escape closes it, back where the focus was", back, label(back) if back else focus_label())
    m = mark()
    key("alt+n")
    run.expect("Send sends it and says so", ["Log sent"], m, 20)
    back = wait_focus(lambda d: d["id"] == "chatInput", 5)
    run.check("the window closes, the focus back in Say", back, label(back) if back else focus_label())
    s, listed, _ = relaymod.admin_call("GET", "/relay/admin/logs", secret)
    got = [l for l in (listed.get("logs", []) if isinstance(listed, dict) else []) if nonce in (l.get("note") or "")]
    if run.check("the relay has it, with the note", s == 200 and got, f"{s}, {len(got)} found"):
        for l in got:
            relaymod.admin_call("DELETE", f"/relay/admin/logs/{l['id']}", secret)
    back_to_chat()


def launch_hidden_on(desktop, args, env=None, cwd=None):
    """launch_hidden on a desktop of its own."""
    cmd = [str(QUIREUIA), "launch", "--desktop", desktop] + (["--cwd", str(cwd)] if cwd else []) + ["--"] + [str(a) for a in args]
    r = subprocess.run(cmd, env=env, capture_output=True, text=True, timeout=90, creationflags=CREATE_NO_WINDOW)
    if r.returncode != 0 or not r.stdout.strip().split()[0].isdigit():
        raise ToolError(f"launch {Path(str(args[0])).name}: {r.stdout.strip()} {r.stderr.strip()}")
    return int(r.stdout.strip().split()[0])


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "test"
    if cmd == "package" and "--check" in sys.argv[2:]:
        cmd = "package-check"
    commands = {"build": build, "run": run_for_person, "visitor": lambda: visitor_command(sys.argv[2:]), "tools": tools,
                "test": test, "stop": stop, "sweep": sweep, "package": package, "package-check": package_check,
                "fetch-lavapipe": fetch_lavapipe}
    if cmd not in commands:
        print(__doc__)
        sys.exit(2)
    sys.exit(commands[cmd]())
