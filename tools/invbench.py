"""Sojourn Access's inventory with a big inventory: how long its window freezes (python tools/invbench.py ...).

  python tools/invbench.py make [--items N] [--as First Last]   a synthetic inventory (default about 20,000 items in ~400 folders: a
                                              fishing game's catch, a shop's boxes, old things nested deep) written as
                                              an IAR and loaded into the local grid's Inventory Big account (once)
  python tools/invbench.py run [--as First Last] [--app DIR]
                                              Sojourn Access logged in as Inventory Big (or another local account)
                                              on a hidden desktop, as the test runs it; each action timed: how long
                                              until it's done, and the window thread's longest freeze meanwhile
                                              (QuireUia's `stalls`: its window pinged every 20 ms); --app: another
                                              build's output folder (an older build copied aside, to compare)

Searches (one that matches nothing walks everything; one with hundreds of matches; one with a few), Show: worn, a
folder of 1,500 fish opened, //route with a notecard's name (the commands' own search) and Places' landmarks. Nothing
touches the person's desktop; the grid must be up. The account stays (its inventory is the fixture).

Written by Claude Opus 5.5, 2026-10-30.
"""
import io
import json
import os
import random
import re
import subprocess
import sys
import tarfile
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import access as A   # noqa: E402
import grid          # noqa: E402

HOME = ROOT / "local" / "invbench"
ACCOUNT = ("Inventory", "Big")   # (Inventory Bench has a broken first load in it)
IAR = HOME / "bench.iar"   # (remade for each make, deleted after the load)
# ports of its own, apart from the test's (9885, 9886, 9790) and ACCESS_ACCOUNTS' (+10)
A.Q_PORT, A.API_PORT = 9897, 9797
A.EVENTS = HOME / "events.jsonl"

FISH = ["Bluegill", "Salmon", "Rainbow Trout", "Carp", "Pike", "Perch", "Catfish", "Sturgeon", "Tuna", "Snapper",
        "Mackerel", "Herring", "Cod", "Halibut", "Marlin", "Swordfish", "Eel", "Grouper", "Bass", "Walleye"]
RARITY = ["Common", "Uncommon", "Rare", "Epic", "Legendary"]
BRANDS = ["Aurelia", "Blackthorn", "Cocoon", "Driftwood", "Ember", "Fable", "Gossamer", "Halcyon", "Ironleaf", "Juniper"]
THINGS = ["Dress", "Jacket", "Boots", "Hair", "Earrings", "Skirt", "Shirt", "Jeans", "Gloves", "Hat", "Necklace",
          "Sofa", "Lamp", "Rug", "Plant", "Table", "Chair", "Bed", "Shelf", "Curtains"]
COLOURS = ["Red", "Blue", "Green", "Black", "White", "Gold", "Silver", "Rose", "Teal", "Plum"]


# ---- the inventory --------------------------------------------------------------------------------------------------

def tree(items):
    """(folders, files): folders as (path parts) and items as (path parts, name), about `items` items in all."""
    rnd = random.Random(7)
    folders, files = [], []

    def folder(*parts):
        folders.append(parts)
        return parts

    scale = items / 20000
    catch = folder("7Seas Catch")
    for _ in range(int(1500 * scale)):   # the loose catch: one big folder
        files.append((catch, f"{rnd.choice(FISH)} ({rnd.choice(RARITY)}) {rnd.uniform(0.2, 40):.2f} kg"))
    for month in range(1, 13):
        f = folder("7Seas Catch", f"Catch 2026-{month:02d}")
        for _ in range(int(600 * scale)):
            files.append((f, f"{rnd.choice(FISH)} ({rnd.choice(RARITY)}) {rnd.uniform(0.2, 40):.2f} kg"))
    for i in range(int(250 * scale) or 1):
        b = rnd.choice(BRANDS)
        f = folder("Shopping", b, f"{b} {rnd.choice(THINGS)} {i}")
        for _ in range(30):
            files.append((f, f"{b} {rnd.choice(THINGS)} {rnd.choice(COLOURS)} {rnd.randint(1, 999)}"))
    for a in range(10):
        for b in range(10):
            f = folder("Old Things", f"Box {a}", f"Box {a}.{b}", "Inside")
            for _ in range(int(30 * scale)):
                files.append((f, f"Old {rnd.choice(THINGS)} {rnd.randint(1, 99999)}"))
    f = folder("Notes")
    for i in range(int(300 * scale)):
        files.append((f, f"Note {i}: {rnd.choice(BRANDS)} {rnd.choice(THINGS)}"))
    return folders, files


def write_iar(items, owner):
    """An IAR (OpenSim's inventory archive): archive.xml, the folders and items, then the one notecard asset."""
    folders, files = tree(items)
    ids = {}
    for parts in folders:
        for i in range(1, len(parts) + 1):
            ids.setdefault(parts[:i], str(uuid.uuid4()))

    def path(parts):
        return "inventory/" + "".join(f"{p}__{ids[parts[:i + 1]]}/" for i, p in enumerate(parts))

    asset = str(uuid.uuid4())
    text = "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 11\nbench note\n}\n"
    now = int(time.time())
    HOME.mkdir(parents=True, exist_ok=True)
    # (GNU long names: OpenSim's tar reader cuts PAX ones at 100 characters)
    with tarfile.open(IAR, "w:gz", format=tarfile.GNU_FORMAT) as tar:
        def add(name, data=None):
            info = tarfile.TarInfo(name)
            if data is None:
                info.type = tarfile.DIRTYPE
                tar.addfile(info)
            else:
                info.size = len(data)
                tar.addfile(info, io.BytesIO(data))
        add("archive.xml", b'<?xml version="1.0" encoding="utf-8"?><archive major_version="0" minor_version="3">'
                           b'<assets_included>True</assets_included></archive>')
        for parts in sorted(set(ids)):
            add(path(parts))
        for n, (parts, name) in enumerate(files):
            item = str(uuid.uuid4())
            made = now - (n % 400) * 86400   # spread over a year; every 400th today
            xml = (f"<InventoryItem><Name>{name}</Name><ID>{item}</ID><InvType>7</InvType>"
                   f"<CreatorUUID>{owner}</CreatorUUID><CreationDate>{made}</CreationDate><Owner>{owner}</Owner>"
                   f"<Description>bench</Description><AssetType>7</AssetType><AssetID>{asset}</AssetID>"
                   "<SaleType>0</SaleType><SalePrice>0</SalePrice><BasePermissions>647168</BasePermissions>"
                   "<CurrentPermissions>647168</CurrentPermissions><EveryOnePermissions>0</EveryOnePermissions>"
                   "<NextPermissions>647168</NextPermissions><Flags>0</Flags>"
                   "<GroupID>00000000-0000-0000-0000-000000000000</GroupID><GroupOwned>False</GroupOwned></InventoryItem>")
            add(f"{path(parts)}{name}__{item}.xml", xml.encode())
        add(f"assets/{asset}_notecard.txt", text.encode())   # (OpenSim wants every inventory/ entry before assets/)
    return len(folders), len(files)


def account_id(first, last):
    for line in grid.console(f"show account {first} {last}", wait=1):
        m = re.search(r"ID:\s*([0-9a-f-]{36})", line)
        if m:
            return m[1]
    return None


def make(items, who):
    if not grid.ready():
        print("the local grid is down (python tools/grid.py start)")
        return 1
    loaded = HOME / f"loaded-{who[0]}-{who[1]}.json".lower()
    if loaded.exists():
        print(f"already loaded: {loaded.read_text()}")
        return 0
    first, last = who
    print(f"account {first} {last}: {grid.account(first, last, A.PASSWORD)}")
    owner = account_id(first, last)
    nf, ni = write_iar(items, owner)
    print(f"{IAR.relative_to(ROOT)}: {ni} items in {nf} folders, {IAR.stat().st_size // 1024} KB")
    log = grid.GRID / "console.log"
    start = log.stat().st_size if log.exists() else 0
    grid.console(f'load iar {first} {last} / {A.PASSWORD} "{IAR}"', wait=3)
    t0 = time.time()
    while time.time() - t0 < 1800:
        tail = log.read_text(errors="replace")[start:] if log.exists() else ""
        if re.search(rf"Loaded \d+ items from archive / for {first} {last}", tail):
            break
        if re.search(r"(?i)archive load .{0,80}failed|failed.{0,80}(iar|archive)|(iar|archive).{0,80}error", tail):
            print("the grid's console reported a problem:\n" + "\n".join(l for l in tail.splitlines() if re.search(r"(?i)iar|archive", l))[-2000:])
            return 1
        time.sleep(5)
    else:
        print("no word of the load in 30 minutes (local/grid/console.log)")
        return 1
    loaded.write_text(json.dumps({"items": ni, "folders": nf, "secs": round(time.time() - t0)}))
    print(f"loaded in {time.time() - t0:.0f} s")
    IAR.unlink(missing_ok=True)
    return 0


# ---- the timings ------------------------------------------------------------------------------------------------------

def newest_log():
    logs = sorted((HOME / "app" / "logs").glob("session-*.log"), key=lambda p: p.stat().st_mtime)
    return logs[-1] if logs else None


def log_has(pattern):
    f = newest_log()
    return f and re.search(pattern, f.read_text(errors="replace"))


def timed(what, prep, go, done, secs=120):
    """prep(), then go() (the key that does it) and done(mark) until truthy, said: the seconds from go until done, the
    window thread's longest freeze meanwhile and the total of those over 100 ms."""
    prep()
    A.u("stalls start")
    m = A.mark()
    t0 = time.time()
    go()
    got = A.wait(lambda: done(m), secs, 0.05)
    took = time.time() - t0
    time.sleep(0.4)
    st = dict(zip(*[iter(A.u("stalls stop").split())] * 2))
    said = " | ".join(e.get("text") or "" for e in A.said_since(m))[:110]
    failed = f"   {st['failed']} pings failed" if st.get("failed", "0") != "0" else ""
    print(f"{what:44s} {took:6.2f} s   longest freeze {int(st['longest']):5d} ms   over 100 ms: {int(st['over100']):3d} "
          f"({int(st['overtotal'])} ms){failed}   {'' if got else 'NOT DONE '}said: {said}")
    return took, int(st["longest"])


# Nothing here looks the tree over by UI Automation (a selector like id=inventory, `get`, `find`): that makes WinForms
# give every tree item a UIA provider, which it then disconnects one by one, a few ms each, whenever the tree is
# cleared (12 s for a 4,500-item folder), where NVDA reads the tree by MSAA and never asks for them. What's selected
# is read from the MSAA focus events a screen reader gets, keys go to whatever has the focus.

def selected_since(m):
    """The tree item last announced by MSAA's focus event since mark m, as NVDA hears it."""
    items = [e.get("text") or "" for e in A.events()[m:] if e.get("src") == "msaa" and e.get("kind") == "focus" and e.get("role") == "outline item"]
    return items[-1] if items else ""


def in_tree():
    return (A.focused() or {}).get("id") == "inventory"


def search(words, expect):
    """Control+F (it selects what's in the box), the words typed (its timer searches on its own after 450 ms), Enter."""
    def prep():
        A.key("ctrl+f")
        A.wait_focus(lambda d: d["id"] == "inventorySearch", 10)
        A.type_text(words)
        time.sleep(1.5)
    return (prep, lambda: A.key("enter"), lambda m: in_tree() and expect in selected_since(m))


def nothing():
    pass


def run(who, app_dir):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if not grid.ready():
        print("the local grid is down (python tools/grid.py start)")
        return 1
    if A.online(*who):
        print(f"{' '.join(who)} is logged in already")
        return 1
    HOME.mkdir(parents=True, exist_ok=True)
    A.EVENTS.unlink(missing_ok=True)
    server = app = None
    try:
        server = A.launch_hidden([A.QUIREUIA, "serve", "--port", A.Q_PORT, "--log", A.EVENTS])
        A.wait(lambda: A.u("help", timeout=5), 20)
        A.wait(lambda: any(e.get("kind") == "listening" and e.get("text") == "com notification" for e in A.events()), 15, 0.5)
        env = dict(os.environ,
                   SOJOURN_TEST_INPUT="keys",            # posted keys judged as the hand's
                   SOJOURN_TEST_NO_NVDA="1", SOJOURN_TEST_NO_CLIPBOARD="1")
        args = ["--grid", A.GRID_URI, "--user", " ".join(who), "--password", A.PASSWORD, "--start", "uri:Atelier&60&40&24",
                "--port", A.API_PORT, "--base", HOME / "app", "--no-assistant", "--no-voice", "--no-render", "--reader", "on",
                "--autologin"]
        t0 = time.time()
        app = A.launch_hidden([A.DOTNET, app_dir / "SojournAccess.dll"] + args, env=env, cwd=app_dir)
        A.u(f"watch {app}")
        if not A.wait(lambda: A.status().get("connected"), 120, 1):
            print("not logged in after 120 s")
            return 1
        A.wait(lambda: any('"Sojourn Access' in l for l in A.u(f"windows {app}").splitlines()), 60, 1)
        try:
            A.u(f"activate {app}")
        except A.ToolError:
            pass
        A.wait_focus(lambda d: d["id"] == "chatInput", 15)
        loaded = A.wait(lambda: log_has(r"inventory loaded: (\d+) folders"), 900, 2)
        print(f"logged in as {' '.join(who)}; {loaded[0] if loaded else 'inventory not loaded after 15 minutes'} "
              f"({time.time() - t0:.0f} s after starting)")
        time.sleep(3)

        timed("Alt+4: the inventory's tree", nothing, lambda: A.key("alt+4"), lambda m: in_tree())
        timed("search: nothing matches (walks everything)", *search("zzqx", "Nothing called zzqx"))
        timed("search: salmon (hundreds)", *search("salmon", "Salmon"))
        timed("search: shirt", *search("shirt", "hirt"))
        timed("search: Escape back to the tree", lambda: A.key("ctrl+f"), lambda: A.key("esc"),
              lambda m: in_tree())
        # Show: worn, then everything again (Tab from Search to Show, then Down / Up)
        def to_show():
            A.key("ctrl+f")
            A.wait_focus(lambda d: d["id"] == "inventorySearch", 10)
            A.key("tab")
            A.wait_focus(lambda d: d["id"] == "inventoryShow", 10)
        timed("Show: worn (walks everything)", to_show, lambda: A.key("down"),
              lambda m: any("worn" in (e.get("text") or "").lower() for e in A.said_since(m)))
        timed("Show: everything again", nothing, lambda: A.key("up"), lambda m: True)
        timed("back to the tree", nothing, lambda: A.key("alt+4"), lambda m: in_tree())
        # the catch's folder, 1,500 fish loose in it, opened by Right
        def to_catch():
            A.key("home")   # (the catch sorts first at the top)
            time.sleep(1)
        timed("Right: open 7Seas Catch (its loose catch)", to_catch, lambda: A.key("right"), lambda m: in_tree(), 60)
        # a fish in it selected (past its twelve months' folders), then F5: the tree made again, the fish still selected
        fish = {}
        def to_fish():
            time.sleep(1.5)
            for _ in range(14):
                A.key("down")
            m = A.mark()
            for _ in range(14):
                A.key("down")
            fish["name"] = A.wait(lambda: (lambda s: s if " kg" in s else None)(selected_since(m)), 10)
        timed("F5 with it open, a fish selected", to_fish, lambda: A.key("f5"),
              lambda m: any("shown again" in (e.get("text") or "") for e in A.said_since(m)), 60)
        time.sleep(1)
        m = A.mark()
        A.key("up")
        A.key("down")   # (the reader told what's selected now)
        after = A.wait(lambda: (lambda s: s if s and A.events()[-1:] else None)(selected_since(m)), 5)
        print(f"     (selected before F5: {fish.get('name')!r}; after it, Up then Down lands on {after!r})")
        A.key("home")
        A.key("left")
        timed("//route zzqx (the notecards searched)",
              lambda: (A.key("alt+1"), A.wait_focus(lambda d: d["id"] == "chatInput", 10), A.type_text("//route zzqx")),
              lambda: A.key("enter"),
              lambda m: any("No notecard called" in (e.get("text") or "") for e in A.said_since(m)))
        timed("Alt+6: Places' landmarks", nothing, lambda: A.key("alt+6"), lambda m: (A.focused() or {}).get("id") == "landmarks")
        return 0
    finally:
        if app:
            try:
                A.u(f"close {app}")
            except Exception:
                pass
            if A.wait(lambda: not A.alive(app), 15, 0.5) is None:
                A.kill(app)
        if server:
            try:
                A.u("quit", timeout=5)
            except Exception:
                pass
            if A.wait(lambda: not A.alive(server), 5, 0.5) is None:
                A.kill(server)


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "run"
    argv = sys.argv[2:]
    if cmd == "make":
        n = int(argv[argv.index("--items") + 1]) if "--items" in argv else 20000
        who = tuple(argv[argv.index("--as") + 1:argv.index("--as") + 3]) if "--as" in argv else ACCOUNT
        sys.exit(make(n, who))
    if cmd == "run":
        who = tuple(argv[argv.index("--as") + 1:argv.index("--as") + 3]) if "--as" in argv else ACCOUNT
        app_dir = Path(argv[argv.index("--app") + 1]).resolve() if "--app" in argv else A.APP_BIN
        sys.exit(run(who, app_dir))
    print(__doc__)
