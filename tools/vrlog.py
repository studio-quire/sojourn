"""Summarise a headset session's logs (python tools/client.py vr --logs writes them).

  python tools/vrlog.py [local/client]

frames.csv: frame pacing (a frame step over 1.5 display periods is a missed frame), CPU phases, captures, and (logs
from 2026-10-26 on) garbage collections and late frames.
vr.csv: the rig. Camera motion that neither the head in the room nor the stick explains (what comfort cares about),
and each follow of the avatar under the head with the gap it left.
"""
import csv
import math
import statistics as st
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
folder = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "local" / "client"


def frames(path):
    rows = [[float(x) for x in r] for r in csv.reader(open(path)) if r]
    if not rows:
        return
    period = st.median(r[1] for r in rows)
    t_end = rows[-1][0]
    print(f"frames.csv: {len(rows)} frames over {t_end - rows[0][0]:.0f} s, display period {period:.2f} ms")
    edges = [rows[0][0], rows[0][0] + 15, rows[0][0] + 60, t_end + 1]
    for lo, hi in zip(edges, edges[1:]):
        rs = [r for r in rows if lo <= r[0] < hi]
        if not rs:
            continue
        missed = sum(1 for r in rs if r[1] > period * 1.5)
        line = (f"  {lo - rows[0][0]:4.0f}-{min(hi, t_end) - rows[0][0]:4.0f} s: missed {missed}/{len(rs)} ({100 * missed / len(rs):.1f}%), "
                f"cpu mean {st.mean(r[2] for r in rs):.1f} ms, frames capturing probes {sum(1 for r in rs if r[4] > 0)}")
        if len(rs[0]) >= 11:
            line += (f"; scene {st.mean(r[6] for r in rs):.1f}, probes {st.mean(r[8] for r in rs):.1f}, "
                     f"record {st.mean(r[9] for r in rs):.1f}, acquire/present {st.mean(r[10] for r in rs):.1f}")
        if len(rs[0]) >= 15:
            # late frames (over two display periods: an older frame left up) and how many of them had a collection
            late = [r for r in rs if r[1] > period * 2.5]
            line += (f"; late {len(late)}, of them with a GC {sum(1 for r in late if r[11] > 0)}; cpu over the period "
                     f"{sum(1 for r in rs if r[2] > period)}; GC {sum(r[11] for r in rs):.0f}/{sum(r[12] for r in rs):.0f}/"
                     f"{sum(r[13] for r in rs):.0f} (gen0/1/2), {sum(r[14] for r in rs):.0f} ms paused")
        if len(rs[0]) >= 17:
            line += f"; the loop around the render {st.mean(r[15] for r in rs):.1f} + {st.mean(r[16] for r in rs):.1f} ms (before + after)"
        print(line)


def rig(path):
    rows = [r for r in csv.reader(open(path)) if r]
    if not rows:
        return
    t0 = float(rows[0][0])
    prev = None
    events, odd = [], []
    for r in rows:
        t = float(r[0]) - t0
        room = tuple(map(float, r[1:4]))
        head = tuple(map(float, r[4:7]))
        av = tuple(map(float, r[7:10]))
        follow, walking, yaw = r[10], r[11] == "1", float(r[12])
        if prev:
            proom, phead, pfollow, pwalking, pyaw = prev
            a = math.radians(yaw)
            dx, dy = room[0] - proom[0], room[1] - proom[1]
            ex, ey = dx * math.cos(a) - dy * math.sin(a), dx * math.sin(a) + dy * math.cos(a)
            stick = (walking or pwalking) and follow == "Idle"
            if not stick and abs(yaw - pyaw) < 0.01:
                u = math.hypot(head[0] - phead[0] - ex, head[1] - phead[1] - ey)
                if u > 0.002:
                    odd.append((t, u))
            if follow != pfollow:
                gap = math.hypot(head[0] - av[0], head[1] - av[1])
                events.append(f"  {t:7.2f} s  {pfollow} -> {follow}  head-avatar gap {gap:.2f} m")
        prev = (room, head, follow, walking, yaw)
    print(f"vr.csv: {len(rows)} frames; follows of the head:")
    print("\n".join(events[-40:]) or "  none")
    print(f"camera motion not from the room, the stick or turning: {sum(u for _, u in odd):.2f} m over {len(odd)} frames")
    for t, u in sorted(odd, key=lambda x: -x[1])[:5]:
        print(f"  {t:7.2f} s  {u * 1000:.0f} mm")


if (folder / "frames.csv").exists():
    frames(folder / "frames.csv")
if (folder / "vr.csv").exists():
    rig(folder / "vr.csv")
