"""Arrival on the GPU, from a client run with SOJOURN_GPU_LOG=FILE (Frame/PassProfiler.cs): per window, frames whose
GPU time is over the period, the GPU frame's median and p95, and loading's part of it (the upload pass, the bytes it
copied, reflection probe captures, impostor captures), mean and worst; then how long until loading's GPU work stopped.

  python tools/gpuarrival.py FILE [FILE ...] [--period 11.1] [--window 10]
"""
import csv
import sys


def q(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(p * len(xs)))] if xs else float("nan")


def report(path, period, window):
    rows = [r for r in csv.DictReader(open(path))]
    rows = [{k: float(v) for k, v in r.items()} for r in rows if r.get("t")]
    if not rows:
        return f"{path}: no frames"
    out = [f"{path}: {len(rows)} frames over {rows[-1]['t']:.0f} s; over {period} ms: "
           f"{sum(r['gpu_ms'] > period for r in rows)} ({100 * sum(r['gpu_ms'] > period for r in rows) / len(rows):.0f}%), "
           f"over 1.5x: {sum(r['gpu_ms'] > 1.5 * period for r in rows)}, over 2x: {sum(r['gpu_ms'] > 2 * period for r in rows)}"]
    out.append("  window   frames  over  gpu med/p95/max     uploads mean/max   MB/frame  probes mean/max  impostors")
    for k in range(0, int(rows[-1]["t"]) + 1, window):
        rs = [r for r in rows if k <= r["t"] < k + window]
        if not rs:
            continue
        g = [r["gpu_ms"] for r in rs]
        u = [r["uploads_ms"] for r in rs]
        p = [r["probes_ms"] for r in rs]
        out.append(f"  {k:4d}-{k + window:<4d} {len(rs):6d} {sum(x > period for x in g):5d}  {q(g, .5):5.1f} {q(g, .95):5.1f} {max(g):5.1f}"
                   f"     {sum(u) / len(u):5.2f} {max(u):5.2f}    {sum(r['upload_mb'] for r in rs) / len(rs):6.2f}"
                   f"    {sum(p) / len(p):5.2f} {max(p):5.2f}    {sum(r['impostors_ms'] for r in rs) / len(rs):5.2f}")
    busy = [r["t"] for r in rows if r["upload_mb"] > 0.05 or r["probes_ms"] > 0.05]
    if busy:
        out.append(f"  loading's GPU work last seen at {busy[-1]:.1f} s ({sum(r['upload_mb'] for r in rows):.0f} MB uploaded in all,"
                   f" {sum(r.get('probe_faces', 0) for r in rows):.0f} probe faces)")
    # perfrun's frame log beside it (LABEL-frames.csv): when what's in view had loaded (assets pending last over 20)
    frames = path.replace("-gpu.csv", "-frames.csv")
    try:
        fr = [[float(x) for x in r] for r in csv.reader(open(frames)) if len(r) >= 11]
        t0 = fr[0][0]
        over = [r[0] - t0 for r in fr if r[5] > 20]
        out.append(f"  assets pending last over 20 at {over[-1] if over else 0:.1f} s of the frame log")
    except (OSError, IndexError, ValueError):
        pass
    return "\n".join(out)


if __name__ == "__main__":
    args = sys.argv[1:]
    period = float(args[args.index("--period") + 1]) if "--period" in args else 11.1
    window = int(args[args.index("--window") + 1]) if "--window" in args else 10
    files = [a for i, a in enumerate(args) if not a.startswith("--") and (i == 0 or args[i - 1] not in ("--period", "--window"))]
    for f in files:
        print(report(f, period, window))
