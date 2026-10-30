"""How steady the headset's view is: the room's placement in the region, frame by frame, from a vr.csv
(SOJOURN_VR_LOG, written by `client.py vr --logs` or `perfrun.py --vr-sim`).

  python tools/jitter.py [vr.csv ...]          (default: local/client/vr.csv)

In the headset the camera is the real head carried by the room (the play space placed in the region by an origin, a
yaw and a floor height). Head tracking itself is the runtime's; everything the client adds is the room's placement, so
that's what this measures:
- still (no stick, no turn, no climbing): any placement change moves the view without the head moving. Each frame it changes by
  over 0.5 mm is a correction; per second, and how much, horizontal and vertical apart.
- stick walking, steady (0.4 s after the press until release): the placement's velocity per frame. Jerk is how its
  velocity changes frame to frame (m/s^2, from the display-time dt when logged); a correction here is a frame whose
  velocity differs from the last frame's by over 0.15 m/s, or that moves backward.
- after letting go: how far the placement still moves over the next 2 s and how fast at most.
Frames that jump over 2 m (teleports, crossings) are left out.
"""
import csv
import math
import statistics as st
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def q(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(p * len(xs)))] if xs else float("nan")


def load(path):
    rows = []
    for r in csv.reader(open(path)):
        if len(r) < 19:
            continue
        t = float(r[0])
        room = tuple(map(float, r[1:4]))
        head = tuple(map(float, r[4:7]))
        yaw = math.radians(float(r[12]))
        follow, walk = r[10], r[11] == "1"
        if len(r) >= 25:
            # newer logs: the placement itself, the display dt, the stick and the region's z velocity
            origin = (float(r[19]), float(r[20]), float(r[21]))
            dt, stick, turning = float(r[22]) / 1000, float(r[23]), r[24] == "1" or (len(r) > 25 and r[25] != "0")
        else:
            # the placement from the head: what's left once the head in the room, turned by the room's yaw, is taken off
            rx = room[0] * math.cos(yaw) - room[1] * math.sin(yaw)
            ry = room[0] * math.sin(yaw) + room[1] * math.cos(yaw)
            origin = (head[0] - rx, head[1] - ry, head[2] - room[2])
            dt, stick, turning = None, 1.0 if walk and follow == "Idle" else 0.0, None
        rows.append(dict(t=t, origin=origin, yaw=float(r[12]), follow=follow, stick=stick > 0.25, dt=dt, turning=turning,
                         state=r[26] if len(r) > 26 else ""))
    if not rows:
        return rows
    # without a logged dt, the wall-clock step rounded to display periods
    steps = [b["t"] - a["t"] for a, b in zip(rows, rows[1:]) if b["t"] > a["t"]]
    period = q(steps, 0.1) if steps else 1 / 90
    for a, b in zip(rows, rows[1:]):
        if b["dt"] is None:
            b["dt"] = max(1, round((b["t"] - a["t"]) / period)) * period
        if b["turning"] is None:
            b["turning"] = abs(b["yaw"] - a["yaw"]) > 0.05
    rows[0]["dt"] = rows[0]["dt"] or period
    rows[0]["turning"] = bool(rows[0]["turning"])
    return rows


def analyse(path):
    rows = load(path)
    if len(rows) < 3:
        return f"{path}: too few frames"
    out = [f"{path}: {len(rows)} frames over {rows[-1]['t'] - rows[0]['t']:.0f} s"]
    still_t = 0.0
    still_h, still_v = [], []
    walk_t = 0.0
    jerk, speeds, walk_corr, backward = [], [], 0, 0
    after = []          # per release: (distance moved in 2 s, peak speed)
    press_at = None
    release = None      # (time, distance so far, peak)
    prev_v = None
    for a, b in zip(rows, rows[1:]):
        d = [b["origin"][i] - a["origin"][i] for i in range(3)]
        dh = math.hypot(d[0], d[1])
        dt = b["dt"] if b["dt"] > 0 else 1 / 90
        if dh > 2 or abs(d[2]) > 2:
            prev_v = None
            continue
        if b["stick"] and not a["stick"]:
            press_at = b["t"]
        if a["stick"] and not b["stick"]:
            release = [b["t"], 0.0, 0.0]
        if release is not None:
            if b["t"] - release[0] <= 2 and not b["stick"] and not b["turning"]:
                release[1] += math.sqrt(dh * dh + d[2] * d[2])
                release[2] = max(release[2], dh / dt)
            elif b["t"] - release[0] > 2 or b["stick"]:
                after.append((release[1], release[2]))
                release = None
        if not b["stick"] and not a["stick"] and not b["turning"]:
            still_t += dt
            still_h.append(dh)
            still_v.append(abs(d[2]))
            prev_v = None
        elif b["stick"] and a["stick"] and not b["turning"] and press_at is not None and b["t"] - press_at > 0.4:
            walk_t += dt
            v = (d[0] / dt, d[1] / dt)
            sp = math.hypot(*v)
            speeds.append(sp)
            if prev_v is not None:
                dv = math.hypot(v[0] - prev_v[0], v[1] - prev_v[1])
                jerk.append(dv / dt)
                if dv > 0.15:
                    walk_corr += 1
                if prev_v[0] * v[0] + prev_v[1] * v[1] < 0:
                    backward += 1
            prev_v = v
        else:
            prev_v = None
    if still_t > 0:
        ch = sum(1 for x in still_h if x > 0.0005)
        cv = sum(1 for x in still_v if x > 0.0005)
        out.append(f"  still {still_t:.0f} s: horizontal corrections {ch / still_t:.1f}/s ({sum(still_h) * 1000 / still_t:.1f} mm/s, "
                   f"p99 step {q(still_h, .99) * 1000:.1f} mm), vertical {cv / still_t:.1f}/s ({sum(still_v) * 1000 / still_t:.1f} mm/s, "
                   f"p99 step {q(still_v, .99) * 1000:.1f} mm)")
    if walk_t > 0:
        cvs = st.pstdev(speeds) / st.mean(speeds) if speeds and st.mean(speeds) > 0 else float("nan")
        out.append(f"  stick walking {walk_t:.0f} s: speed {st.median(speeds):.2f} m/s (cv {cvs:.3f}), "
                   f"jerk median {q(jerk, .5):.1f} p95 {q(jerk, .95):.1f} m/s^2, corrections {walk_corr / walk_t:.1f}/s, backward frames {backward}")
    blinks = sum(1 for a, b in zip(rows, rows[1:]) if b["state"] in ("cut", "blink") and a["state"] not in ("cut", "blink"))   # (blink: before 2026-10-28)
    slides = sum(1 for a, b in zip(rows, rows[1:]) if b["state"] == "slide" and a["state"] != "slide")
    if blinks or slides:
        out.append(f"  put back on the avatar: {blinks} cuts, {slides} slides")
    if after:
        out.append(f"  after letting go ({len(after)}): moved median {q([a[0] for a in after], .5) * 100:.0f} cm (p90 {q([a[0] for a in after], .9) * 100:.0f}), "
                   f"peak speed median {q([a[1] for a in after], .5):.2f} m/s (p90 {q([a[1] for a in after], .9):.2f})")
    return "\n".join(out)


if __name__ == "__main__":
    paths = sys.argv[1:] or [str(ROOT / "local" / "client" / "vr.csv")]
    for p in paths:
        print(analyse(p))
