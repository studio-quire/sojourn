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
                                   nothing on the GPU, for steps that don't need the view; ACCESS_ACCOUNTS=Walk runs as Walk Tester
                                   and Walk Visitor on ports 10 up, never throwing out a person's Access Tester)
  python tools/access.py package [--dirty]
                                   the build for a tester's own PC: self-contained (no .NET needed), win-x64, with its
                                   README and licences, zipped to local/dist/SojournAccess-0.1.0.N-win-x64.zip (N the
                                   commit count; uncommitted changes under src/ refused unless --dirty); it keeps its
                                   data in %LOCALAPPDATA%\\Sojourn Access and logs in to Second Life unless run with --local
  python tools/access.py package --check [ZIP] [--keep]
                                   the zip (default the newest in local/dist) unpacked in a fresh folder under %TEMP% and
                                   run as a tester would, on a hidden desktop of its own, as Package Tester on the local
                                   grid: ok/FAIL lines as the test's (docs/access-app.md > Packaging); then everything it
                                   made removed (--keep leaves the folder)
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

DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
APP_PROJECT = ROOT / "src" / "Sojourn.Access" / "Sojourn.Access.csproj"
APP_BIN = ROOT / "src" / "Sojourn.Access" / "bin" / "Release" / "net10.0-windows10.0.22621.0"
APP_EXE = APP_BIN / "SojournAccess.exe"
APP_DLL = APP_BIN / "SojournAccess.dll"
APP_AS_EXE = False                 # the apphost can't find .NET here; dotnet + the dll it is
QUIREUIA = ROOT / "local" / "bin" / "QuireUia" / "QuireUia.exe"
VISITOR_DLL = ROOT / "local" / "bin" / "Visitor" / "Visitor.dll"
OUT = ROOT / "local" / "access-test"
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

LOGIN_SECS = 120
NOTICE_SECS = 15
MAX_TABS = 25                      # Tab stops walked before calling the order endless

# UIA control types whose focusable instances must have a name
NAMED_TYPES = {"Edit", "Document", "Button", "CheckBox", "RadioButton", "ComboBox", "List", "Tree", "Tab", "TabItem",
               "Slider", "Spinner", "DataGrid", "Table", "Hyperlink", "MenuItem", "SplitButton"}
# the contract's controls (AutomationId = WinForms Name) and the UIA types that would be right for them
ROLES = {"chatInput": {"Edit"}, "chatHistory": {"Edit", "Document"}, "conversations": {"Tab"},
         "convInput": {"Edit"}, "convHistory": {"Edit", "Document"}, "people": {"List", "DataGrid", "Table"},
         "objects": {"List", "DataGrid", "Table"}, "notices": {"List", "DataGrid", "Table"},
         "noticeText": {"Edit", "Document"}, "noticeReply": {"Edit"}, "noticeIgnore": {"Button"},
         "assistantInput": {"Edit"}, "assistantHistory": {"Edit", "Document"}, "inventory": {"Tree"},
         "inventorySearch": {"Edit"}, "inventoryShow": {"ComboBox"}, "editorText": {"Edit", "Document"},
         "landmarks": {"Tree"}, "history": {"List", "DataGrid", "Table"}, "goRegion": {"Edit"},
         "regions": {"List", "DataGrid", "Table"}, "searchWords": {"Edit"}, "searchKind": {"ComboBox"},
         "results": {"List", "DataGrid", "Table"}, "friends": {"List", "DataGrid", "Table"}, "groups": {"List", "DataGrid", "Table"},
         "profileText": {"Edit", "Document"}}

VK = {"tab": 9, "enter": 13, "ctrl": 17, "esc": 27, "space": 32, "del": 46, "oem_open": 219, "oem_close": 221, "f1": 112, "f3": 114, "f4": 115, "f5": 116, "f6": 117, "apps": 93,
      "home": 36, "end": 35, "up": 38, "down": 40, "left": 37, "right": 39,
      **{str(d): 48 + d for d in range(10)}, **{c: ord(c.upper()) for c in "abcdefghijklmnopqrstuvwxyz"}}

CREATE_NO_WINDOW, BELOW_NORMAL, NEW_GROUP = 0x08000000, 0x00004000, 0x00000200
HOURS = ["twelve", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven"]


# ---- building ---------------------------------------------------------------------------------------------------------

def dotnet_build(project, configuration="Release"):
    """dotnet build at below-normal priority (inherited by MSBuild's nodes), two nodes, no build server."""
    p = subprocess.Popen([str(DOTNET), "build", str(project), "-c", configuration, "-m:2", "-nr:false",
                          "-p:UseSharedCompilation=false", "-v:m", "-clp:NoSummary"],
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
    return rc or dotnet_build(ROOT / "tools" / "visitor" / "Visitor.csproj")


# ---- the package for a tester -------------------------------------------------------------------------------------------

DIST = ROOT / "local" / "dist"
PACKAGE_FILES = ROOT / "src" / "Sojourn.Access" / "Package"   # README.txt, licences/ (texts not found elsewhere)
NVDA_DIR = ROOT / "local" / "nvda"                              # tools/screenreader.py fetch
VOICE_DLL = ROOT / "local" / "voice" / "build" / "Release" / "sojourn_voice.dll"   # tools/voice.py build
WEBRTC_LICENCES = ROOT / "local" / "webrtc" / "LICENSES" / "webrtc-license.txt"
NUGET = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))
RID = "win-x64"


def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, creationflags=CREATE_NO_WINDOW).stdout.strip()


def package():
    """A self-contained build in a zip under local/dist for a tester's own PC (docs/access-app.md > Packaging):
    SojournAccess.exe with the .NET runtime inside, the renderer's files, NVDA's controller client, voice and world
    audio (sojourn_voice.dll), a README for a screen-reader user and the licences. Versioned 0.1.0.N, N the commit count;
    refuses uncommitted changes under src/ unless --dirty (then marked so in the version's commit and the zip's name)."""
    dirty_ok = "--dirty" in sys.argv[2:]
    changed = git("status", "--porcelain", "--", "src")
    if changed and not dirty_ok:
        print("uncommitted changes under src/ (a packaged build should be one commit, so its number names it):\n" + changed
              + "\ncommit first, or --dirty for a local try")
        return 1
    missing = [p for p in (NVDA_DIR / "x64" / "nvdaControllerClient.dll", NVDA_DIR / "license.txt", VOICE_DLL, WEBRTC_LICENCES)
               if not p.exists()]
    if missing:
        print("missing: " + ", ".join(str(p.relative_to(ROOT)) for p in missing)
              + " (python tools/screenreader.py fetch; python tools/voice.py build)")
        return 1
    version = f"0.1.0.{git('rev-list', '--count', 'HEAD')}"
    commit = git("rev-parse", "--short=7", "HEAD") + ("-dirty" if changed else "")
    name = f"Sojourn Access {version}"
    zip_path = DIST / f"SojournAccess-{version}{'-dirty' if changed else ''}-{RID}.zip"
    stage = DIST / "stage" / name
    build_root = DIST / "build"   # its own bin and obj (Directory.Build.props), so the repo's build folder isn't touched
    shutil.rmtree(stage, ignore_errors=True)
    stage.mkdir(parents=True)

    print(f"publishing {name} ({commit}), self-contained {RID}")
    env = dict(os.environ, SOJOURN_BUILD_ROOT=str(build_root))
    p = subprocess.Popen([str(DOTNET), "publish", str(APP_PROJECT), "-c", "Release", "-r", RID, "--self-contained", "true",
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
    if not (stage / "sojourn_voice.dll").exists():
        shutil.copy2(VOICE_DLL, stage)
    write_text(stage / "README.txt", (PACKAGE_FILES / "README.txt").read_text(encoding="utf-8").replace("{VERSION}", version))
    licences(stage / "Licences", build_root)

    expected = ["SojournAccess.exe", "nvdaControllerClient.dll", "sojourn_voice.dll", "openjp2.dll", "libSkiaSharp.dll",
                "shaderc_shared.dll", "Shaders", "Character", "Textures", "Fonts", "linden"]
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
    return 0


def write_text(path, text):
    """Plain text for Notepad and screen readers: CRLF, UTF-8 without a BOM."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(text.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))


def licences(dest, build_root):
    """The licences of what ships: our notices for the parts that need one, the texts beside them, and every NuGet
    package with files in the package (from the publish's project.assets.json) with its licence."""
    import html
    import xml.etree.ElementTree as ET
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
               + (PACKAGE_FILES / "licences" / "OpenJPEG.txt").read_text(encoding="utf-8"))
    write_text(dest / "Archivo font (OFL-1.1).txt",
               (ROOT / "src" / "Sojourn.Session" / "Assets" / "Fonts" / "Archivo-OFL.txt").read_text(encoding="utf-8"))

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
              "Second Life viewer parts (renderer code, Character, Textures): LGPL-2.1, Linden Research, Inc.",
              "The linden folder (LibreMetaverse's copy of the viewer's data files): see cc-by-sa-3.0.txt in it",
              "Archivo font (Fonts): SIL Open Font License 1.1", "",
              "Where a library's licence is MIT or Apache-2.0 and its package carried no text of it, the text is in "
              "MIT.txt or Apache-2.0.txt here; the copyright is its authors', as named at its project's address."]
    shutil.copy2(PACKAGE_FILES / "licences" / "Apache-2.0.txt", dest / "Apache-2.0.txt")
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
        elif str(QUIREUIA).lower() in cmd and " serve" in cmd and out in cmd:
            found.append((int(parts[0]), "QuireUia"))
        elif str(VISITOR_DLL).lower() in cmd and f"--port {V_PORT}" in cmd:
            found.append((int(parts[0]), "visitor"))
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
    for d in ("Sojourn.Access", "Sojourn.Session", "Sojourn.Core"):
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
    if verb in ("fmenu", "ftext", "fperm", "fgive", "offer", "friend") and not rest:
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
    app_file = APP_EXE if APP_AS_EXE else APP_DLL
    missing = [p for p in (QUIREUIA, VISITOR_DLL, app_file) if not p.exists()]
    if missing:
        print("missing: " + ", ".join(str(p.relative_to(ROOT)) for p in missing) + " (python tools/access.py tools / build)")
        return 1
    if not grid.ready():
        print("the local grid is down (python tools/grid.py start)")
        return 1
    busy = [p for p in (Q_PORT, V_PORT, API_PORT) if not port_free(p)]
    running = ours()
    if busy or running:
        print(f"something is already running (ports in use: {busy}; ours: {running}): python tools/access.py stop")
        return 1
    for first, last in (TESTER, VISITOR):
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

    # posted keys are judged as the hand's, every other rule of the person's own press applying (the focus and the
    # selected notice moved by our own code or anything else, other threads' sends, held keys; ACCESS_TEST_INPUT=1 lets
    # them all through instead); and no NVDA: on a hidden desktop its controller client claims NVDA runs
    env = dict(os.environ, SOJOURN_TEST_INPUT=os.environ.get("ACCESS_TEST_INPUT", "keys"), SOJOURN_TEST_NO_NVDA="1", SOJOURN_TEST_NO_CLIPBOARD="1",
               SOJOURN_OFFSCREEN_LOSE_AFTER=str(LOSE_AFTER),
               # People's and Objects' first fills held back, as Second Life's objects wait for their names
               SOJOURN_TEST_LIST_DELAY_MS=str(LIST_DELAY_MS),
               SOJOURN_VOICE_OUT_FILE=str(HEARD), SOJOURN_WALK_LOG=str(OUT / "walk.log"))   # world audio to a file (local grid only), never the speakers
    app_args = ["--grid", GRID_URI, "--user", f"{TESTER[0]} {TESTER[1]}", "--password", PASSWORD,
                "--start", "uri:%s&%d&%d&%d" % (REGION, *TESTER_START), "--port", API_PORT, "--base", OUT / "app",
                "--no-assistant", "--reader", "on", "--autologin"]
    if os.environ.get("ACCESS_VOICE") != "1":
        app_args.append("--no-voice")   # voice chat is on by default; its keys' step runs with it only when asked
    if os.environ.get("ACCESS_NO_RENDER") == "1":
        app_args.append("--no-render")   # no renderer out of sight: nothing on the GPU (the renderer and walk-mode steps need it)
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

    steps = [
        ("the window's controls", lambda: tree_check(run, "nearby chat")),
        ("Tab order", lambda: tab_order(run)),
        ("nearby chat in", lambda: chat_in(run, nonce)),
        ("nearby chat out", lambda: chat_out(run, nonce)),
        ("an IM", lambda: im(run, nonce, tester_id)),
        ("people and objects", lambda: lists(run)),
        ("the renderer out of sight", lambda: offscreen(run)),
        ("keys", lambda: keys(run, nonce)),
        ("settings", lambda: settings(run)),
        ("the quit question", lambda: quit_question(run)),
        ("a script menu", lambda: notice_menu(run, tester_id)),
        ("a text box", lambda: notice_text(run, tester_id, nonce)),
        ("a permission question", lambda: notice_perm(run, tester_id)),
        ("inventory", lambda: inventory(run, nonce)),
        ("every part", lambda: parts(run)),
        ("your HUDs", lambda: huds(run, nonce)),
        ("placing by words", lambda: placing(run, nonce)),
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
        ("review keys and arrivals", lambda: review_and_arrivals(run, nonce)),
        ("sound as a map", lambda: sound_map(run, tester_id)),
        ("walk mode", lambda: walk_mode(run)),
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


def tree_check(run, when, must=("chatInput", "chatHistory")):
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
    # the history is one Shift+Tab back from Say, as in Radegast (the guided test, 2026-10-29)
    run.check("the Nearby chat history is the stop before Say", len(stops) > 2 and stops[-2]["id"] == "chatHistory",
              label(stops[-2]) if len(stops) > 2 else "focus didn't move")
    run.check("Tab goes round and back to Say, no dead ends", stuck is None and stops[-1]["id"] == "chatInput",
              order if stuck is None else f"stuck at {label(stuck)}: {order}")
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


def chat_out(run, nonce):
    for how, kind, name, text in (("enter", "Normal", "Enter in Say: the visitor hears it", f"said from Access {nonce}"),
                                  ("shift+enter", "Whisper", "Shift+Enter whispers", f"whispered from Access {nonce}"),
                                  ("ctrl+enter", "Shout", "Ctrl+Enter shouts", f"shouted from Access {nonce}")):
        n = visitor_n()
        say_in_app(text, how)
        run.heard_by_visitor(name, lambda d, t=text, k=kind: d["msg"] == t and d["type"] == k and d["from"] == TNAME, n, 10, text)
    run.check("the Say box is empty after sending", value_of("id=chatInput") == "", repr(value_of("id=chatInput")))

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
        tab = wait(lambda: find(f"type=TabItem;name~={VNAME}"), 4)
        run.check(f"a conversation tab for {VNAME}", tab, label(tab[0]) if tab else
                  f"no TabItem named {VNAME}; tab items: " + ("; ".join(label(t) for t in find("type=TabItem")) or "none"))
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
        key("alt+2")   # shown, so its tabs are in the tree
        time.sleep(1)
        gone = not find(f"type=TabItem;name~={VNAME}")
        run.check("Ctrl+W closes the conversation", gone, f"then {after}" if gone else "its tab is still there")


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


def lists(run):
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
    key("alt+1")
    d = wait_focus(lambda d: d["id"] == "chatInput", 4)
    run.check("Alt+1 back to the Say box", d, label(d) if d else focus_label())


def keys(run, nonce):
    m = mark()
    key("f1")
    # whole, in pieces, to its last key (the announcer cut anything past 700 characters short, mid-list)
    got = wait(lambda: (lambda s: s if any("incognito" in (e.get("text") or "") for e in s) else None)(said_since(m)), 8)
    said = " ".join(e.get("text") or "" for e in said_since(m))
    run.check("F1 says the keys, all of them (none cut short)", got and "Alt+1" in said and "cut short" not in said,
              f"{said[:80]} ... {said[-80:]}" if said else "nothing said")
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
    item = wait(lambda: find("type=ListItem;name~=Read out nearby chat"), 4)
    if not run.check("it has 'Read out nearby chat'", item, label(item[0]) if item else "not listed"):
        return
    key("home")
    for _ in range(40):
        if "Read out nearby chat" in selection("id=settings"):
            break
        key("down")
        time.sleep(0.15)
    if not run.check("Down arrow reaches it", "Read out nearby chat" in selection("id=settings"), selection("id=settings")):
        return
    for state in ("off", "on"):
        m = mark()
        key("enter")
        run.expect(f"Enter on it turns it {state}, saying so", [f"Read out nearby chat: {state}."], m, 5)
        now = find("type=ListItem;name~=Read out nearby chat")
        run.check(f"and the item says {state}", now and now[0]["name"].endswith(f": {state}"), label(now[0]) if now else "gone")
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
    item = wait(lambda: find("type=ListItem;name~=Volume of the music"), 4)
    if run.check("Alt+0 has the music's volume", item, label(item[0]) if item else "not listed"):
        key("home")
        for _ in range(60):
            if "Volume of the music" in selection("id=settings"):
                break
            key("down")
            time.sleep(0.12)
        for how, want in (("enter", "0.25"), ("shift+enter", "0.15"), ("shift+enter", "0")):
            m = mark()
            key(how)
            run.expect(f"{how.title()} on it: {want}", [f"Volume of the music: {want}."], m, 5)
        setting("music_volume", 0.15)


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
    key("enter", target="id=inventorySearch")
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
    try:
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
    m = mark()
    key("ctrl+d")
    run.expect("Control+D without the assistant says it needs one", ["needs the assistant", "Claude account"], m, 10)
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
    r = api("SojournDescribe", "look", 60, subject=vid, detail="brief")
    pics = r.get("pictures") or []
    if os.environ.get("ACCESS_NO_RENDER") == "1":
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
        r = api("SojournDescribe", "look", 60, subject=fixture["id"], detail="brief")
        p = (r.get("pictures") or [{}])[0]
        run.check("a picture of the fixture box, framed on it and named", p.get("png") and r["subject"].get("name") == "Access Fixture"
                  and int((p.get("subject_fills") or "0%")[:-1]) >= 10, f"{p.get('label')}, fills {p.get('subject_fills')}, {r['subject'].get('where')}")
    r = api("SojournDescribe", "look", 60, subject="here", detail="brief")
    labels = [p.get("label", "") for p in r.get("pictures") or []]
    run.check("here: four views round the tester, the visitor among the people around",
              len(labels) == 4 and "ahead" in labels[0] and any(VNAME in s for s in r["subject"].get("people_within_30_metres", [])),
              "; ".join(labels))


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
    run.expect("and coming back", [f"{VNAME} came into chat range"], m, 30)
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
    GPU loss SOJOURN_OFFSCREEN_LOSE_AFTER fakes), the assistant's look gets a real picture, the collision model has a box
    the visitor puts out, and touches land on its faces as the VR client's do (the box's script says which). The box
    stays out until the visitor's cleanup at the end."""
    def running():
        st = api("SojournView", "status")
        return st if st["state"] == "running" and st["losses"] >= 1 and st["frames"] > 2 else None
    st = wait(running, 60, 1)
    run.check("the view out of sight runs, and came back after a lost GPU", st,
              json.dumps(st) if st else json.dumps(api("SojournView", "status")))
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

    x, y, z, _ = tester_where()
    made = v(f"facebox {FACEBOX_XY[0]} {FACEBOX_XY[1]} {z:.1f}", timeout=30)
    box = re.search(r"id ([0-9a-f-]{36})", made)
    if not run.check("the visitor's box is out", box, made):
        return
    box_id = box[1]
    dx, dy = FACEBOX_XY[0] - x, FACEBOX_XY[1] - y
    far = math.hypot(dx, dy)
    hit = wait(lambda: (lambda h: h if h.get("hit") and h.get("prim") == box_id else None)(
        api("SojournView", "collide", **{"from": [x, y, z], "dir": [dx, dy, 0], "max": 10})), 15, 1)
    run.check("the collision model has the box where it is", hit and abs(hit["distance"] - (far - 0.25)) < 0.3,
              f"{json.dumps(hit)}; the box's middle {far:.2f} m away" if hit else
              json.dumps(api("SojournView", "collide", **{"from": [x, y, z], "dir": [dx, dy, 0], "max": 10})))

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
NO_RENDER = os.environ.get("ACCESS_NO_RENDER") == "1"


def list_items(sel):
    """The item names shown in one of the objects area's lists (QuireUia finds by the whole window, so each list's items
    are told by their words): objects, parts or faces."""
    names = [d["name"] for d in find("type=ListItem")]
    if sel == "id=faces":
        return [n for n in names if ", face " in n or "faces" in n.lower() or "Faces" in n or n == "No part selected."]
    if sel == "id=parts":
        return [n for n in names if re.search(r"(part \d+ of \d+|^Part \d+ of |the whole object|parts\.|No object selected|isn't here)", n)]
    return [n for n in names if not re.search(r"(part \d+ of \d+|^Part \d+ of |, face \d)", n)]


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
        run.check("only the lid marked as taking touches", [n for n in got if n.endswith(", touch")] == [lid], " | ".join(got))
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
        if NO_RENDER:
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
        if not NO_RENDER:
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
        key("alt+7")
        wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 4)
        u("setfocus id=objectsShow")
        if not run.check("(Show reached)", wait_focus(lambda d: d["id"] == "objectsShow", 3), focus_label()):
            return
        key("down")
        u("setfocus id=objects")
        listed = wait(lambda: find(f"type=ListItem;name~={name}, on HUD Center, 2 parts"), 8)
        run.check("Show: Your HUDs lists it, where it's worn and its parts", listed, " | ".join(list_items("id=objects")))
        if not listed:
            return
        wait_focus(lambda d: d["id"] == "objects" or d["type"] == "ListItem", 3)
        select_in("id=objects", name)
        play = wait(lambda: (lambda ps: ps if ps else None)([n for n in list_items("id=parts") if n.startswith("Play, part 2 of 2")]), 10)
        run.check("its parts: the button Play by name, above the base, taking touches", play and "above the root" in play[0] and play[0].endswith("touch"),
                  " | ".join(list_items("id=parts")))
        key("tab")
        wait_focus(lambda d: d["id"] == "parts" or d["type"] == "ListItem", 3)
        if select_in("id=parts", "Play, part"):
            n = visitor_n()
            m = mark()
            key("enter")
            run.expect("Enter on Play presses it", ["Touched Play"], m, 10)
            run.heard_by_visitor("its script says it was pressed, on its own prim", lambda d: "Access HUD pressed, link 2" in d["msg"], n, 10, "pressed, link 2")
        u("setfocus id=objectsShow")
        key("up")
    finally:
        api("TesselAgent", "detach", 30, item_id=item)
        back_to_chat()


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
    finally:
        for o in api("TesselObjects", "nearby", dist=20, query=name).get("objects", []):
            api("TesselBuild", "take", 30, ids=[o["id"]], folder_id=root)
        back_to_chat()


def two_panes(run, nonce):
    """The inventory's second pane (M2, a blind tester's second window): Alt+4 again opens it; there a folder is picked; back in
    the first (Alt+4), two notecards marked with Space go into it by Control+M; Control+W closes the second pane."""
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
    d = wait_focus(lambda d: d["id"] == "inventory2", 4)
    run.check("Alt+4 again in the inventory opens the second pane", d, label(d) if d else focus_label())
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
    run.check("Alt+4 moves back to the first pane", d, label(d) if d else focus_label())
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
    key("alt+4")
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
    u("keydown focus | 115")
    time.sleep(1)
    held = status()
    u("keyup focus | 115")
    time.sleep(1)
    let_go = status()
    run.check("F4 held opens the mic; let go, it closes", held.get("mic_live") and not let_go.get("mic_live"),
              f"held: {json.dumps(held)[:160]}; let go: {json.dumps(let_go)[:160]}")
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
    m = mark()
    say_in_app(TP_COMMAND)
    run.expect(f"{TP_COMMAND} arrives, saying where", ["Arrived"], m, 40)
    x, y, z, _ = tester_where()
    off = math.dist((x, y), TP_TO[:2])
    run.check("the tester is there", off < 3, f"at {x:.1f}, {y:.1f}: {off:.1f} m from {TP_TO[:2]}")


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
    if wait_focus(lambda d: d["name"] == "Settings" or d["type"] == "ListItem", 4) and select_in("id=settings", "Sound for an IM"):
        t0 = time.time()
        m = mark()
        key("enter")
        run.expect("Alt+0: Enter on the IM's sound turns it up, saying so", ["Sound for an IM: loud."], m, 5)
        c = cue_after(t0, ("EarIm",), 3)
        run.check("and plays it at the new level", c and abs(c["level"] - 0.7) < 0.01, json.dumps(c))
        for _ in range(4):   # full, off, quiet, medium: back where it was
            key("enter")
            time.sleep(0.6)
        run.check("and round again to medium", setting("earcon_im") == "medium", setting("earcon_im"))
    else:
        run.check("Alt+0 lists the IM's sound", False, selection("id=settings"))
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
        got = run.expect("O names the doors, seats and touchable things around", ["Marks Bench, a seat", "Marks Gate, a door", "Marks Bell, touch"], m, 8)
        text = (got or {}).get("text") or ""
        run.check("each by its clock face (bench ahead, gate right, bell left)",
                  re.search(r"Marks Bench, a seat, 4 metres, twelve o'clock", text) and re.search(r"Marks Gate, a door, 4 metres, three o'clock", text)
                  and re.search(r"Marks Bell, touch, 4 metres, nine o'clock", text), text)
        time.sleep(3.5)
        sent = cues_since(t0, "MarkSeat", "Door", "MarkTouch")
        want = {"MarkSeat": 0, "Door": 90, "MarkTouch": 270}
        placed = {c["cue"]: c["bearing"] for c in sent}
        run.check("and each sounded from where it is (a seat sinks, the gate knocks, the bell taps)",
                  all(k in placed and placed[k] is not None and off_by(placed[k], b) < 20 for k, b in want.items()),
                  json.dumps(placed))

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
    run.expect("//go into the closed pen: no way, so it teleports", ["No way around found", "teleporting"], m, 15)
    run.expect("and arrives", ["Teleported to"], m, 25)
    time.sleep(1.5)
    x, y, z = walk_pos()
    run.check("inside the pen", abs(x - pen[0]) < 1.4 and abs(y - pen[1]) < 1.4, f"at {x:.1f}, {y:.1f}")

    # following the visitor: it comes to the course, walks off north past the corridor's west side and stops
    start = (bx - 3, by - 6)
    got = v(f"tp {COURSE_REGION} {start[0]} {start[1]} {g + 1.5}", timeout=40)
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
    key("esc")
    wait_focus(lambda d: d["id"] == "chatInput", 5)
    say_in_app("//stand")
    time.sleep(2)


def walk_mode(run):
    """Walk mode (Walk/WalkMode.cs, docs/access-app.md > Walk mode) on a course of its own in Atelier East: F3 into it,
    F1, the heading, the sonar's words, walking north by a held Up arrow with footsteps on the stone floor, the opening
    passed on the left, the warning before the end wall (named Blue Door) and Blocked at it, a step turn each way,
    what's ahead, touching it, Space, the drop warning walking off a platform, Escape back to the Say box."""
    bx, by = COURSE
    # after the faked GPU loss (SOJOURN_OFFSCREEN_LOSE_AFTER), so it doesn't fall in the middle of a walk
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
        # the collision model counts what the view has drawn: wait for the east wall
        seen = wait(lambda: (lambda h: h if h.get("hit") else None)(api("SojournView", "collide", dir=[1, 0, 0], max=5)), 30, 1)
        run.check("the view out of sight has drawn the course (the east wall met)", seen and abs(seen["distance"] - 1.4) < 0.3,
                  json.dumps(seen))
        # and the end wall and the west wall's far part (frames turn a quarter at a time)
        wait(lambda: api("SojournView", "collide", dir=[0, 1, 0], max=15).get("hit") and
             api("SojournView", "collide", dir=[-0.2, 1, 0], max=15).get("distance", 99) < 8, 20, 1)

        m = mark()
        key("f3")
        f = wait_focus(lambda d: d["id"] == "walkPad", 5)
        run.check("F3 puts the focus on the walk pad", f, label(f) if f else focus_label())
        run.expect("and says walk mode is on", ["Walk mode", "F1"], m, 5)
        m = mark()
        key("f1")
        run.expect("F1 there says walk mode's keys", ["Up and Down arrows walk", "sonar"], m, 5)
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
    rest = [a for a in sys.argv[2:] if a not in ("--check", "--keep")]
    keep = "--keep" in sys.argv[2:]
    zips = [Path(rest[0]).resolve()] if rest else sorted(DIST.glob("SojournAccess-*.zip"), key=lambda p: p.stat().st_mtime)
    if not zips or not zips[-1].exists():
        print("no zip (python tools/access.py package)")
        return 1
    zip_path = zips[-1]
    version = re.search(r"SojournAccess-(0\.1\.0\.\d+)", zip_path.name)
    version = version[1] if version else "?"
    print(f"checking {zip_path.name}")

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
        before = tree_hashes(folder)
        profile = known_folders()
        profile_before = top_level(profile)
        started = time.time()

        # a tester's PC: no .NET of ours on the way (the SDK and any install off PATH, no DOTNET_ variables), %TEMP%,
        # %TMP%, %LOCALAPPDATA% and %APPDATA% pointed into the check's folder so whatever follows them shows up there
        path = os.pathsep.join(p for p in os.environ.get("PATH", "").split(os.pathsep) if "dotnet" not in p.lower())
        env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("DOTNET_", "SOJOURN_", "ACCESS_"))}
        env.update(PATH=path, TEMP=str(env_dirs["TEMP"]), TMP=str(env_dirs["TEMP"]), LOCALAPPDATA=str(env_dirs["LOCALAPPDATA"]),
                   APPDATA=str(env_dirs["APPDATA"]),
                   SOJOURN_TEST_INPUT="keys",            # posted keys judged as the hand's
                   SOJOURN_TEST_NO_CLIPBOARD="1",
                   SOJOURN_VOICE_OUT_FILE=str(out / "heard.wav"),   # world audio to a file, never the speakers
                   SOJOURN_VOICE_MIC_FILE="none")         # and no microphone

        server = launch_hidden_on(CHECK_DESKTOP, [QUIREUIA, "serve", "--port", Q_PORT, "--log", EVENTS])
        if not check("QuireUia's server answers", wait(lambda: u("help", timeout=5), 20), f"pid {server}"):
            raise Stop
        listening = wait(lambda: any(e.get("src") == "server" and e.get("kind") == "listening" and e.get("text") == "com notification"
                                     for e in events()), 15, 0.5)
        if not check("it's listening for announcements", listening, "UIA notifications registered" if listening else "no"):
            raise Stop

        args = [exe, "--local", "--user", " ".join(CHECK_ACCOUNT), "--password", PASSWORD, "--start",
                "uri:%s&%d&%d&%d" % (REGION, *TESTER_START), "--autologin", "--reader", "on", "--no-voice", "--base", data]
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

        def network(when):
            socks = sockets_of(app)
            listen = [s for s in socks if s[0] == "tcp" and s[4] == "Listen"]
            # "Bound" rows are Windows' record of an outgoing socket's port (bound to any address before ConnectEx, as
            # every program's are): nothing can connect to them; the connections themselves are rows of their own
            bound = [s for s in socks if s[0] == "tcp" and s[4] == "Bound"]
            used = [s for s in socks if s not in bound]
            wide = [s for s in used if not loopback(s[1]) or (s[0] == "tcp" and not loopback(s[3]) and s[4] != "Listen")]
            check(f"nothing listening on TCP ({when})", not listen,
                  f"{len(socks) - len(bound) - sum(1 for s in socks if s[0] == 'udp')} TCP connections, none listening" if not listen else
                  "; ".join(f"{s[1]}:{s[2]}" for s in listen))
            check(f"TCP and UDP on loopback only ({when})", used and not wide,
                  ", ".join(sorted({f"{s[0]} {s[1]}" + (f"->{s[3]}" if s[3] else f":{s[2]}") for s in used}))[:300]
                  + f" ({len(bound)} outgoing sockets' Bound rows aside)" if not wide
                  else "; ".join(f"{s[0]} {s[1]}:{s[2]}->{s[3]} {s[4]}" for s in wide))
        network("logged in")
        check("nothing at all on the agent API's port 9725", port_free(9725), "connection refused" if port_free(9725)
              else "something answers on 9725 (ours if the app is the owner above)")

        # ---- F1, //about, Control+D without the assistant, chat
        m = mark()
        key("f1")
        run.expect("F1 is read out", ["Alt+1 nearby chat"], m, 8)   # in several announcements: it's long
        run.expect("and ends pointing to //about", ["//about says which version"], m, 8)
        back_to_chat()
        m = mark()
        say_in_app("//about")
        got = run.expect("//about is read out", ["Sojourn Access " + version], m, 8)
        if got:
            text = got["text"]
            check("//about names the public source", "github.com/studio-quire/sojourn" in text, text[:200])
            check("//about names the data folder", str(data) in text, text[-160:])
        m = mark()
        key("ctrl+d")
        run.expect("Control+D answers", ["Describing needs the assistant"], m, 10)
        wait(lambda: False, 1.5)
        m2 = mark()
        key("ctrl+d")
        wait(lambda: said_since(m2), 10)
        wait(lambda: False, 1.5)
        absent = [e["text"] for e in said_since(m) if "this build doesn't include" in (e.get("text") or "")]
        check("Control+D's absence said once, not again", len(absent) == 1 and said_since(m2),
              f"said {len(absent)} time(s) in two presses; the second: {' | '.join(e.get('text') or '' for e in said_since(m2))[:160]}")
        back_to_chat()
        line = f"package check {uuid.uuid4().hex[:6]}"
        say_in_app(line)
        heard = wait(lambda: line in value_of("id=chatHistory"), 10)
        check("chat sent (the grid's echo in the Nearby chat history)", heard, line if heard else value_of("id=chatHistory")[-200:])

        # ---- the renderer out of sight, the natives
        logs = data / "logs"
        def log_text():
            return "\n".join(p.read_text(encoding="utf-8", errors="replace") for p in sorted(logs.glob("*.log")))
        up = wait(lambda: re.search(r"the view out of sight is up on ([^:]+)", log_text()), 60, 1)
        check("the renderer out of sight starts", up, up[0] if up else "no such line in the session's log")
        g0 = gpu_of(app)
        wait(lambda: False, 5)
        g1 = gpu_of(app)
        check("and draws (the GPU working for it)", g1[0] > g0[0] and g1[1] > 50e6,
              f"{(g1[0] - g0[0]) / 1e4:.0f} ms of GPU engine time in 5 s, {g1[1] / 1e6:.0f} MB of video memory")
        failed = re.findall(r".*(?:couldn't start|the GPU was lost|stopped:).*", log_text())
        check("no renderer failures in the log", not failed, "; ".join(failed)[:200] or "none")
        mods = modules_of(app)
        for dll in SHIPPED_DLLS:
            where = next((m for m in mods if Path(m).name.lower() == dll.lower()), None)
            check(f"{dll} loaded from the package's folder", where and Path(where).parent == folder, where or "not loaded")
        foreign = [m for m in mods if str(ROOT).lower() in m.lower()]
        check("nothing loaded from the repo", not foreign, "; ".join(foreign) or f"{len(mods)} modules")
        network("after the renderer and chat")
        crashes = re.findall(r".*(?:Unhandled exception|fail:).*", log_text())
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
                "test": test, "stop": stop, "sweep": sweep, "package": package, "package-check": package_check}
    if cmd not in commands:
        print(__doc__)
        sys.exit(2)
    sys.exit(commands[cmd]())
