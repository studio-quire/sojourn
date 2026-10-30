"""What the headset really shows: captures Meta's own compositor mirror (OculusMirror.exe, started if it isn't running)
without focusing it. Unlike `dev.py shot`, which composites our layers itself, this is the runtime's final picture
(layer order, depth tests, our cuts, reprojection), one eye undistorted. Claude Opus 5.5, 2026-10-28.

  python tools/mirrorshot.py [--burst N] [--every S] [--out DIR]   saves N PNGs (default 1) and a contact sheet if N > 1
"""
import argparse, ctypes, subprocess, sys, time
from ctypes import wintypes
from pathlib import Path

import psutil
from PIL import Image

MIRROR = Path(r"C:\Program Files\Oculus\Support\oculus-diagnostics\OculusMirror.exe")
ROOT = Path(__file__).resolve().parents[1]
user32, gdi32 = ctypes.windll.user32, ctypes.windll.gdi32
user32.SetProcessDPIAware()


class BMI(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG), ("biHeight", wintypes.LONG),
                ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG), ("biYPelsPerMeter", wintypes.LONG),
                ("biClrUsed", wintypes.DWORD), ("biClrImportant", wintypes.DWORD)]


def mirror_window():
    pids = {p.pid for p in psutil.process_iter(["name"]) if (p.info["name"] or "").lower() == "oculusmirror.exe"}
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(h, _):
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
        if pid.value in pids and user32.IsWindowVisible(h):
            r = wintypes.RECT()
            user32.GetClientRect(h, ctypes.byref(r))
            if r.right * r.bottom > 0:
                found.append((r.right * r.bottom, h, r.right, r.bottom))
        return True

    user32.EnumWindows(cb, 0)
    return max(found)[1:] if found else None


def capture(h, w, hh):
    dc = user32.GetDC(h)
    mdc = gdi32.CreateCompatibleDC(dc)
    bmp = gdi32.CreateCompatibleBitmap(dc, w, hh)
    gdi32.SelectObject(mdc, bmp)
    user32.PrintWindow(h, mdc, 3)  # PW_CLIENTONLY | PW_RENDERFULLCONTENT: works behind other windows
    bmi = BMI(ctypes.sizeof(BMI), w, -hh, 1, 32, 0, 0, 0, 0, 0, 0)
    buf = ctypes.create_string_buffer(w * hh * 4)
    gdi32.GetDIBits(mdc, bmp, 0, hh, buf, ctypes.byref(bmi), 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mdc)
    user32.ReleaseDC(h, dc)
    return Image.frombuffer("RGBA", (w, hh), buf, "raw", "BGRA", 0, 1).convert("RGB")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--burst", type=int, default=1)
    ap.add_argument("--every", type=float, default=0.3)
    ap.add_argument("--out", default=str(ROOT / "local" / "main" / "dev-shots"))
    a = ap.parse_args()
    win = mirror_window()
    if not win:
        if not MIRROR.exists():
            sys.exit(f"no {MIRROR}")
        subprocess.Popen([str(MIRROR)], creationflags=0x08000000)  # no console
        for _ in range(50):
            time.sleep(0.2)
            if win := mirror_window():
                break
        else:
            sys.exit("OculusMirror started but no window appeared (is the headset's runtime up?)")
        time.sleep(1)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    shots = []
    for i in range(a.burst):
        im = capture(*win)
        p = out / (f"mirror-{stamp}.png" if a.burst == 1 else f"mirror-{stamp}-{i + 1}.png")
        im.save(p)
        shots.append(im)
        print(p)
        if i + 1 < a.burst:
            time.sleep(a.every)
    if len(shots) > 1:
        w, h = shots[0].size
        W, H = w // 2, h // 2
        cols = 2
        sheet = Image.new("RGB", (W * cols, H * ((len(shots) + 1) // cols)))
        for i, im in enumerate(shots):
            sheet.paste(im.resize((W, H)), ((i % cols) * W, (i // cols) * H))
        p = out / f"mirror-{stamp}-sheet.png"
        sheet.save(p)
        print(p)


if __name__ == "__main__":
    main()
