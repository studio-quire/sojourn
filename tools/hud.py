"""Sojourn's HUD tests on the local grid: floating text on a HUD, and media on a HUD face (python tools/hud.py ...).

  python tools/hud.py test [text media popup]   a client (Media Pilot) wearing the HUD Test Screen at HUD Center
  python tools/hud.py headset                   the same HUD on the headset's board in Meta's XR Simulator: the right
                                                hand taps the page's button, the left drags across the page
  python tools/hud.py fixture                   the HUD Test Screen (an OAR, then taken into Media Pilot's inventory)

The HUD Test Screen is a thin box sized for HUD space (0.4 x 0.2 of the screen's height) with media's test page on
its -x face (the one a HUD shows) and floating text above it, told over channel 9 ("hud text <words, | for a new
line>", "hud notext", "hud reset"). It uses media's test site and account (tools/media_fixtures.py). It stays in the
world beside media's stand; the test takes a copy into inventory once, wears it, checks, and takes it off again.

Written by Claude Opus 5.5, 2026-10-26.
"""
import base64
import io
import json
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import media_fixtures as mf  # noqa: E402

HUD = ROOT / "local" / "hud"
PILOT = HUD / "media-pilot"
API_PORT = 9735
NAME = "HUD Test Screen"
TAG = "tools/hud.py 1"
FACE = 4                                   # a box's -x face: what a HUD shows (its camera looks along +x)
TEXT_RGB = (0.2, 1.0, 0.6)                 # unlike anything on the page
SIZE = (0.02, 0.4, 0.2)
SPOT = (124.0, 229.0, 31.4)                # beside media's stand, south of the row

SCRIPT = """// HUD Test Screen (tools/hud.py): media's test page on face %(face)d and floating text above it; told over
// channel 9 ("hud text <words, | for a new line>", "hud notext", "hud reset")
setup()
{
    llSetText("HUD TEST|level 3", <%(rgb)s>, 1.0);
    text("HUD TEST|level 3");
    llSetPrimitiveParams([PRIM_TEXTURE, %(face)d, "%(blank)s", <1, 1, 0>, <0, 0, 0>, 0, PRIM_FULLBRIGHT, %(face)d, TRUE]);
    llSetPrimMediaParams(%(face)d, [PRIM_MEDIA_CURRENT_URL, "%(url)s", PRIM_MEDIA_HOME_URL, "%(url)s", PRIM_MEDIA_AUTO_PLAY, TRUE,
        PRIM_MEDIA_WIDTH_PIXELS, 1024, PRIM_MEDIA_HEIGHT_PIXELS, 512,
        PRIM_MEDIA_PERMS_INTERACT, PRIM_MEDIA_PERM_ANYONE, PRIM_MEDIA_PERMS_CONTROL, PRIM_MEDIA_PERM_ANYONE]);
}
text(string t) { llSetText(llDumpList2String(llParseStringKeepNulls(t, ["|"], []), "\\n"), <%(rgb)s>, 1.0); }
default
{
    state_entry() { setup(); llListen(9, "", NULL_KEY, ""); }
    on_rez(integer p) { llResetScript(); }
    listen(integer ch, string name, key id, string msg)
    {
        if (llGetSubString(msg, 0, 3) != "hud ") return;
        string rest = llGetSubString(msg, 4, -1);
        if (llGetSubString(rest, 0, 4) == "text ") text(llGetSubString(rest, 5, -1));
        else if (rest == "notext") llSetText("", <1, 1, 1>, 0);
        else if (rest == "reset") setup();
    }
    touch_start(integer n) { llOwnerSay("hud touched face " + (string)llDetectedTouchFace(0)); }
}
"""


def script():
    return SCRIPT % dict(face=FACE, rgb=", ".join(f"{c:g}" for c in TEXT_RGB), blank=mf.BLANK, url=mf.SITE_URL + "/page.html")


def build_oar(pilot):
    import tarfile
    import audio
    import forest
    HUD.mkdir(parents=True, exist_ok=True)
    out = HUD / "hud-screen.oar"
    pid = mf.uid("hud part", TAG)
    with tarfile.open(out, "w:gz") as tar:
        forest.add(tar, "archive.xml", forest.archive_xml())
        te = forest.te_bytes(mf.PLYWOOD, color=(0.9, 0.9, 0.9, 1))
        xml = forest.part(pid, 140, NAME, SPOT, (0, 0, 0, 1), SIZE, te)
        xml = xml.replace(forest.OWNER, pilot).replace("<Description />", f"<Description>{TAG}</Description>")
        item = audio.task_item(mf.uid("hud item", TAG), mf.uid("hud asset", TAG), "controller", 10, pid, pilot)
        xml = xml.replace("<InventorySerial>0</InventorySerial>", "<InventorySerial>1</InventorySerial>")
        xml = xml.replace("</SceneObjectPart>", "<TaskInventory>" + item + "</TaskInventory></SceneObjectPart>")
        forest.add(tar, f"objects/HUD_Test_Screen__{pid}.xml", forest.group([xml]).encode())
        forest.add(tar, f"assets/{mf.uid('hud asset', TAG)}_script.lsl", script().encode())
    return out


def in_world():
    import sqlite3
    import grid
    c = sqlite3.connect(f"file:{grid.BIN / 'OpenSim.db'}?mode=ro", uri=True)
    try:
        return c.execute("select Description from prims where Name = ?", (NAME,)).fetchall()
    finally:
        c.close()


def rez_fixture():
    """The object in the world (once; the test takes a copy into inventory the first time)."""
    import grid
    pilot = mf.accounts()["Media Pilot"]
    have = in_world()
    if have and all(h[0] == TAG for h in have):
        return 0
    grid.console('change region "Atelier"', wait=1)
    if have:
        grid.console(f'delete object name "{NAME}"', wait=2, answers=("y",))
    path = build_oar(pilot).resolve()
    print("\n".join(grid.console(f'load oar --merge "{path}"', wait=5)[-2:]))
    end = time.time() + 60
    while time.time() < end and not in_world():
        grid.console("backup", wait=3)
        time.sleep(2)
    return 0 if in_world() else 1


def api(op, pump, timeout=30, **args):
    import urllib.request
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{API_PORT}/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 20) as r:
        out = json.loads(r.read())
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def wait(f, secs, step=0.5):
    end = time.time() + secs
    while time.time() < end:
        try:
            v = f()
            if v:
                return v
        except Exception:
            pass
        time.sleep(step)
    return None


def find_item():
    got = api("collectDescendantsIf", "LLInventory", name=NAME, type="object")
    items = got.get("items") or {}
    items = list(items.values()) if isinstance(items, dict) else items
    return items[0] if items else None


def wear():
    """The HUD Test Screen worn at HUD Center, at its middle; its root's id."""
    item = find_item()
    if item is None:
        objs = api("nearby", "TesselObjects", dist=30, query=NAME)["objects"]
        obj = next((o for o in objs if o.get("name") == NAME), None)
        if obj is None:
            raise RuntimeError(f"no {NAME} in inventory or in the world (python tools/hud.py fixture)")
        api("take", "TesselBuild", ids=[obj["id"]], copy=True)   # (the one in the world stays: rez_fixture finds it)
        item = wait(find_item, 20)
    if item is None:
        raise RuntimeError(f"{NAME} didn't reach inventory")
    iid = item.get("item_id") or item.get("id")
    try:
        api("attach", "TesselAgent", item_id=iid, point="Center")
    except RuntimeError as e:
        if "already worn" not in str(e):
            raise
    hud = wait(lambda: next((h for h in api("list", "SojournHud")["huds"] if h["name"] == NAME and h["drawn"]), None), 30)
    if hud is None:
        raise RuntimeError(f"{NAME} isn't drawn on the HUD")
    api("modify", "TesselBuild", id=hud["id"], pos=[0, 0, 0], rot_euler=[0, 0, 0])
    time.sleep(1.5)
    return iid, hud["id"]


def colour_mask(img, rgb, tol=40):
    import numpy as np
    want = np.array([c * 255 for c in rgb])
    return (np.abs(img[..., :3].astype(int) - want).max(axis=2) < tol) & (img[..., 3] > 128 if img.shape[2] == 4 else True)


def png(data):
    import numpy as np
    from PIL import Image
    return np.array(Image.open(io.BytesIO(data)).convert("RGBA"))


def test(names):
    import numpy as np
    import grid
    names = set(names or ["text", "media", "popup"])
    if subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"], capture_output=True).returncode:
        print("the client didn't build: python tools/client.py build")
        return 1
    if mf.fixtures() or rez_fixture():
        return 1
    srv = mf.start_server()
    import shutil
    shutil.rmtree(PILOT, ignore_errors=True)
    PILOT.mkdir(parents=True)
    (PILOT / "vr-settings.json").write_text(json.dumps({"confirmTouches": False, "panelHidden": True}))
    env = dict(os.environ, SOJOURN_TEST_INPUT="1")
    dotnet = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
    client = ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll"
    log = open(PILOT / "console.txt", "w", encoding="utf-8")
    W, H = 960, 540
    proc = subprocess.Popen([str(dotnet), str(client), "--window", f"{W}x{H}", "--fps", "30", "--no-assistant", "--no-voice",
                             "--first", "Media", "--last", "Pilot", "--password", "sojourn-local", "--bridge-port", str(API_PORT),
                             "--base-dir", str(PILOT), "--start", f"uri:Atelier&{mf.STAND[0]:.0f}&{mf.STAND[1]:.0f}&31", "--minutes", "8",
                             "--sky", "noon"],
                            cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, text=True,
                            creationflags=0x00004000)
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)

    def key(vk):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(proc.pid), "-Vk", str(vk)],
                       capture_output=True)

    def click(x, y):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winmouse.ps1"), "-X", str(int(x)), "-Y", str(int(y)),
                        "-Click", "-ProcessId", str(proc.pid)], capture_output=True)

    def shot():
        from PIL import Image
        shots = PILOT / "shots"
        before = set(shots.glob("*.png")) if shots.exists() else set()
        key(123)   # F12: the window as drawn
        new = wait(lambda: set(shots.glob("*.png")) - before, 10)
        if not new:
            return None
        time.sleep(0.5)
        return np.array(Image.open(sorted(new)[-1]).convert("RGBA"))

    def media_item():
        return next((m for m in api("list", "SojournMedia")["media"] if m.get("name") == NAME), None)

    def page_state():
        t = api("read", "SojournMedia", media=media_item()["key"])["text"]
        return t[t.find("clicks"):].split("\n")[0]

    iid = None
    try:
        if not wait(lambda: mf.logged_in(API_PORT, 2), 90):
            check("login", False, "no login (local/hud/media-pilot/console.txt)")
            return 1
        iid, root = wear()
        key(27)   # the mouse free of looking about, so the pointer follows it
        if "text" in names:
            api("sendChat", "LLChatBar", message="hud reset", channel=9)
            time.sleep(1.5)
            look = api("look", "SojournHud")
            img = png(base64.b64decode(look["png"]))
            rect = next(h["rect"] for h in look["huds"] if h["name"] == NAME)
            m = colour_mask(img, TEXT_RGB)
            ys, xs = np.nonzero(m)
            above = len(ys) and ys.max() < rect[1] + 2 and abs(xs.mean() - (rect[0] + rect[2] / 2)) < rect[2] * 0.1
            check("text on the picture", len(ys) > 40 and above,
                  f"{len(ys)} text pixels, rows {ys.min() if len(ys) else '-'}-{ys.max() if len(ys) else '-'} over the HUD's top at {rect[1]}")
            s = shot()
            # (thin anti-aliased strokes at the screen's own size: green well above red and blue rather than the exact colour)
            g = s[..., :3].astype(int) if s is not None else np.zeros((1, 1, 3), int)
            sm = (g[..., 1] > 150) & (g[..., 1] > g[..., 0] + 80) & (g[..., 1] > g[..., 2] + 40)
            box = np.zeros_like(sm)
            box[int(H * 0.2):int(H * 0.4), W // 2 - 90:W // 2 + 90] = True   # above the HUD (its top at 0.4 of the height)
            sm &= box
            sy, sx = np.nonzero(sm)
            check("text on the desktop", len(sy) > 40 and abs(sx.mean() - W / 2) < 30,
                  f"{len(sy)} text pixels around {sx.mean() if len(sx) else 0:.0f},{sy.mean() if len(sy) else 0:.0f}")
            api("sendChat", "LLChatBar", message="hud text a longer|three|line status", channel=9)
            time.sleep(1.5)
            img2 = png(base64.b64decode(api("look", "SojournHud")["png"]))
            ys2, _ = np.nonzero(colour_mask(img2, TEXT_RGB))
            check("text changes", len(ys2) and ys2.max() < rect[1] + 2 and ys2.min() < (ys.min() if len(ys) else 0) - 5,
                  f"top row {ys2.min() if len(ys2) else '-'} (was {ys.min() if len(ys) else '-'}), still above the HUD")
            api("sendChat", "LLChatBar", message="hud notext", channel=9)
            time.sleep(1.5)
            gone = colour_mask(png(base64.b64decode(api("look", "SojournHud")["png"])), TEXT_RGB).sum()
            check("text goes", gone < 10, f"{gone} text pixels")
            api("sendChat", "LLChatBar", message="hud reset", channel=9)

        if "media" in names or "popup" in names:
            item = wait(lambda: (m := media_item()) and m["state"] == "loaded" and m, 30)
            check("media plays on the HUD", item, f"{item and item['state']}")
            time.sleep(2)
            s = shot()
            # the page's buttons are its orange (#e2a03f); "Press me" is the leftmost
            bm = colour_mask(s, (0xe2 / 255, 0xa0 / 255, 0x3f / 255), tol=24) if s is not None else np.zeros((1, 1), bool)
            by, bx = np.nonzero(bm)
            if "media" in names:
                if len(bx) > 20:
                    left = bx < bx.min() + 40
                    x, y = bx[left].mean(), by[left].mean()
                    before = page_state()
                    n0 = int(before.split()[1])
                    click(x, y)
                    st = wait(lambda: f"clicks {n0 + 1}" in page_state() and page_state(), 5)
                    check("a click on the HUD's page reaches it", st, f"at {x:.0f},{y:.0f}: {before} -> {st or page_state()}")
                else:
                    check("a click on the HUD's page reaches it", False, f"the page's button isn't on screen ({len(bx)} pixels)")
            if "popup" in names:
                popup(api, check, wait, media_item)
    finally:
        try:
            if iid:
                api("detach", "TesselAgent", item_id=iid)
                time.sleep(2)
        except Exception as e:
            print("couldn't take the HUD off:", e)
        proc.terminate()
        try:
            proc.wait(15)
        except subprocess.TimeoutExpired:
            proc.kill()
        srv.shutdown()
        grid.console("backup", wait=1)
    print("all ok" if ok else "some FAILED")
    return 0 if ok else 1


def headset():
    """SOJOURN_VR_SCRIPT=hud in the simulator with the HUD Test Screen worn: a right tap on the page's "Press me" (its
    place on the 16:9 screen as the desktop test found it) must reach the page, and the left hand's held sweep must be
    the page's gesture (a press and drag: it holds still first), not the HUD's touch."""
    global API_PORT
    if subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"], capture_output=True).returncode:
        print("the client didn't build")
        return 1
    if mf.fixtures() or rez_fixture():
        return 1
    srv = mf.start_server()
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)

    def media_item():
        return next((m for m in api("list", "SojournMedia")["media"] if m.get("name") == NAME), None)

    def page_state():
        t = api("read", "SojournMedia", media=media_item()["key"])["text"]
        return t[t.find("clicks"):].split("\n")[0]

    # worn first from the desktop (the region keeps it worn into the next login)
    base = HUD / "headset"
    proc = mf.start_client(base, API_PORT, vr=False, minutes=3)
    iid = None
    try:
        if not mf.logged_in(API_PORT, 90):
            check("login", False, "the desktop client didn't log in")
            return 1
        iid, _ = wear()
    finally:
        proc.terminate()
        proc.wait(20)
    time.sleep(5)
    proc = mf.start_client(base, API_PORT, vr=True, minutes=3, env_extra={
        "SOJOURN_VR_SCRIPT": "hud", "SOJOURN_HUD_AIMS": "0.4125,0.467;0.45,0.56", "SOJOURN_MEDIA_DEBUG": "1"})
    log = base / "console.txt"
    try:
        if not mf.logged_in(API_PORT, 150):
            check("login", False, "the headset client didn't log in")
            return 1
        before = wait(lambda: (m := media_item()) and m["state"] == "loaded" and page_state(), 60)
        done = wait(lambda: any("hud script" in l and l.endswith("done") for l in log.read_text(encoding="utf-8", errors="replace").splitlines()), 90)
        time.sleep(1)
        after = page_state()
        text = log.read_text(encoding="utf-8", errors="replace")
        n0 = int(before.split()[1]) if before else -1
        check("the board's tap reaches the HUD's page", done and after.startswith(f"clicks {n0 + 1} "), f"{before} -> {after}")
        # (the routine holds still before sweeping, so it's the page's press-and-drag rather than a scroll)
        downs = text.count("media gesture: down on")
        check("the left hand's hold and sweep is the page's gesture, not the HUD's touch", downs >= 2 and "hud touched" not in text,
              f"{downs} page gestures begun, the HUD touched: {'hud touched' in text}")
    finally:
        try:
            if iid:
                api("detach", "TesselAgent", item_id=iid)
                time.sleep(2)
        except Exception as e:
            print("couldn't take the HUD off:", e)
        proc.terminate()
        try:
            proc.wait(20)
        except subprocess.TimeoutExpired:
            proc.kill()
        srv.shutdown()
    print("all ok" if ok else "some FAILED")
    return 0 if ok else 1


def popup(api, check, wait, media_item):
    """The page's "New window" link (target=_blank), pressed: the window opens up close as the standalone page."""
    key = media_item()["key"]
    api("press", "SojournMedia", media=key, words="New window")
    page = wait(lambda: next((m for m in api("list", "SojournMedia")["media"] if m.get("kind") == "page" and m.get("state") == "loaded"), None), 10)
    up = api("list", "SojournMedia").get("up_close")
    check("a new window opens up close", page and page.get("opened_from") == key and "page2.html" in page.get("url", "") and up == "page",
          f"window {page and (page.get('url'), page.get('opened_from'))}, up close {up}")
    api("zoom", "SojournMedia", on=False)


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "test"
    if cmd == "fixture":
        sys.exit(mf.fixtures() or rez_fixture())
    elif cmd == "headset":
        sys.exit(headset())
    elif cmd == "test":
        sys.exit(test(sys.argv[2:]))
    else:
        sys.exit(__doc__)
