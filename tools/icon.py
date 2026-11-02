"""Sojourn's app icon: art/icon/sojourn.svg rendered into art/icon/sojourn.ico, the one file the launcher (Sojourn.exe),
the VR client (SojournVR.exe's window) and the package (Velopack's Setup and shortcut) use.

  python tools/icon.py            render the .ico from the SVG (commit both)
  python tools/icon.py png SIZE   one size as art/icon/sojourn-SIZE.png beside it, to look at

To use another design, replace art/icon/sojourn.svg (the candidates are in art/icon/candidates/) and run this.

The SVG is drawn on a 256 grid. An element can carry data-min="N" and/or data-max="N" (pixels): it's drawn only at
sizes within them, so small sizes can drop detail or use bolder shapes from the same file. Rendered with resvg
(github.com/linebender/resvg, on PATH; `winget install linebender.resvg`); Pillow for the pixels.
"""
import io
import shutil
import struct
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SVG = ROOT / "art" / "icon" / "sojourn.svg"
ICO = ROOT / "art" / "icon" / "sojourn.ico"
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]   # Windows' sizes at 100-250% scaling, and Explorer's large views
SVG_NS = "http://www.w3.org/2000/svg"


def for_size(svg_text, size):
    """The SVG as drawn at this size: elements whose data-min/data-max exclude it removed."""
    ET.register_namespace("", SVG_NS)
    ET.register_namespace("xlink", "http://www.w3.org/1999/xlink")
    root = ET.fromstring(svg_text)

    def keep(el):
        lo, hi = el.get("data-min"), el.get("data-max")
        return (lo is None or size >= int(lo)) and (hi is None or size <= int(hi))

    def prune(el):
        for child in list(el):
            if keep(child):
                prune(child)
            else:
                el.remove(child)

    prune(root)
    return ET.tostring(root, encoding="unicode")


def resvg():
    exe = shutil.which("resvg")
    if not exe:
        sys.exit("resvg isn't on PATH (winget install linebender.resvg)")
    return exe


def render(svg_text, size):
    r = subprocess.run([resvg(), "-w", str(size), "-h", str(size), "-", "-c"],
                       input=for_size(svg_text, size).encode("utf-8"), capture_output=True)
    if r.returncode:
        raise RuntimeError(r.stderr.decode(errors="replace"))
    return Image.open(io.BytesIO(r.stdout)).convert("RGBA")


def dib(img):
    """A 32-bit DIB as an .ico entry holds it: the header (height doubled for the mask), BGRA rows bottom-up, then an
    empty AND mask (the alpha does the work)."""
    w, h = img.size
    pixels = img.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes("raw", "BGRA")
    mask = bytes(((w + 31) // 32) * 4 * h)
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(pixels) + len(mask), 0, 0, 0, 0)
    return header + pixels + mask


def ico(images):
    """An .ico of these images, PNG for 256 (as Windows expects) and DIBs below."""
    blobs = []
    for img in images:
        if img.size[0] >= 256:
            b = io.BytesIO()
            img.save(b, "PNG", optimize=True)
            blobs.append(b.getvalue())
        else:
            blobs.append(dib(img))
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for img, blob in zip(images, blobs):
        w = img.size[0]
        out += struct.pack("<BBBBHHII", w % 256, w % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    return out + b"".join(blobs)


def build(svg=SVG, out=ICO):
    text = svg.read_text(encoding="utf-8")
    out.write_bytes(ico([render(text, s) for s in SIZES]))
    print(f"{out.relative_to(ROOT)}: {', '.join(map(str, SIZES))} px from {svg.relative_to(ROOT)}")


def main(argv):
    if argv[:1] == ["png"] and len(argv) == 2:
        size = int(argv[1])
        dest = SVG.with_name(f"sojourn-{size}.png")
        render(SVG.read_text(encoding="utf-8"), size).save(dest)
        print(dest)
        return 0
    if argv:
        print(__doc__)
        return 2
    build()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
