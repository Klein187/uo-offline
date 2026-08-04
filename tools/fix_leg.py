#!/usr/bin/env python3
"""Reroute a blocked trail leg using Z-aware server-truth walkability.

For EDGEWALK BLOCKED legs the flat atlas can't explain (cliff seams: adjacent
tiles both standable but at incompatible heights). Requests a fresh walkmap
window from the live shard — which now emits walkmap_z.pgm alongside — then
runs A* applying the same per-step rules as Walkable.CanStep (climb <= 4,
drop <= 20; we require |dz| <= 4 so the leg works in BOTH directions), and
prints replacement nodes spaced <= 28 tiles.

Usage:
    python fix_leg.py --a 1193 727 --b 1195 715 --margin 60
"""
import argparse
import heapq
import json
import math
import os
import sys
import time

LIVE = os.path.expanduser(r"~/uo-modernuo/ModernUO/Distribution/Data/Live")
MAX_SPACING = 28
MAX_CLIMB = 4  # both directions => |dz| <= MAX_CLIMB per step


def read_pgm(path):
    data = open(path, "rb").read()
    parts = data.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    return w, h, parts[3][: w * h]


def fetch_window(x0, y0, x1, y1):
    side = max(x1 - x0, y1 - y0) + 1
    if side > 512:
        raise SystemExit(f"window {side} > 512")
    pgm = os.path.join(LIVE, "walkmap.pgm")
    zpgm = os.path.join(LIVE, "walkmap_z.pgm")
    m0 = os.path.getmtime(pgm) if os.path.exists(pgm) else 0
    token = int(time.time())
    open(os.path.join(LIVE, "walkmap_request.txt"), "w").write(
        f"{token} {x0} {y0} {x1} {y1}"
    )
    t0 = time.time()
    while time.time() - t0 < 240:
        time.sleep(1)
        if os.path.exists(pgm) and os.path.getmtime(pgm) != m0:
            time.sleep(0.3)
            break
    else:
        raise SystemExit("walkmap timeout — shard down?")
    w, h, mask = read_pgm(pgm)
    zw, zh, zs = read_pgm(zpgm)
    assert (w, h) == (zw, zh)
    return w, h, mask, zs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--a", nargs=2, type=int, required=True)
    ap.add_argument("--b", nargs=2, type=int, required=True)
    ap.add_argument("--margin", type=int, default=60)
    ap.add_argument("--prefix", default="FIX")
    args = ap.parse_args()

    ax, ay = args.a
    bx, by = args.b
    x0 = min(ax, bx) - args.margin
    y0 = min(ay, by) - args.margin
    x1 = max(ax, bx) + args.margin
    y1 = max(ay, by) + args.margin
    # clamp to 512 window centered on the leg
    if x1 - x0 + 1 > 512 or y1 - y0 + 1 > 512:
        raise SystemExit("leg + margin exceeds 512 window")

    w, h, mask, zmap = fetch_window(x0, y0, x1, y1)

    def walk(x, y):
        return 0 <= x - x0 < w and 0 <= y - y0 < h and mask[(y - y0) * w + (x - x0)] > 127

    def z(x, y):
        return zmap[(y - y0) * w + (x - x0)] - 128

    def can_step(x, y, nx, ny):
        if not walk(nx, ny):
            return False
        if abs(z(nx, ny) - z(x, y)) > MAX_CLIMB:
            return False
        if nx != x and ny != y:  # diagonal corner-cut guard (soft: one elbow)
            ea = walk(nx, y) and abs(z(nx, y) - z(x, y)) <= MAX_CLIMB
            eb = walk(x, ny) and abs(z(x, ny) - z(x, y)) <= MAX_CLIMB
            if not ea and not eb:
                return False
        return True

    if not walk(ax, ay) or not walk(bx, by):
        raise SystemExit("an endpoint is not walkable in the window")

    g = {(ax, ay): 0}
    came = {}
    pq = [(0, ax, ay)]
    dirs = [(-1, -1, 14), (0, -1, 10), (1, -1, 14), (-1, 0, 10),
            (1, 0, 10), (-1, 1, 14), (0, 1, 14), (1, 1, 14)]
    path = None
    while pq:
        f, x, y = heapq.heappop(pq)
        if (x, y) == (bx, by):
            path = [(x, y)]
            while (x, y) in came:
                x, y = came[(x, y)]
                path.append((x, y))
            path.reverse()
            break
        for dx, dy, c in dirs:
            nx, ny = x + dx, y + dy
            if not can_step(x, y, nx, ny):
                continue
            ng = g[(x, y)] + c + abs(z(nx, ny) - z(x, y))  # prefer flat ground
            if (nx, ny) not in g or ng < g[(nx, ny)]:
                g[(nx, ny)] = ng
                came[(nx, ny)] = (x, y)
                heapq.heappush(pq, (ng + 10 * max(abs(nx - bx), abs(ny - by)), nx, ny))
    if path is None:
        raise SystemExit("NO Z-LEGAL PATH in window — widen margin or reroute the trail")

    def line_ok(a, b):
        x, y = a
        x2, y2 = b
        dx, dy = abs(x2 - x), abs(y2 - y)
        sx = 1 if x2 > x else -1
        sy = 1 if y2 > y else -1
        err = dx - dy
        px, py = x, y
        while True:
            if (x, y) != (px, py):
                if not can_step(px, py, x, y):
                    return False
                px, py = x, y
            if (x, y) == (x2, y2):
                return True
            e2 = 2 * err
            if e2 > -dy:
                err -= dy
                x += sx
            if e2 < dx:
                err += dx
                y += sy

    nodes = [path[0]]
    i = 0
    while i < len(path) - 1:
        best = i + 1
        for j in range(len(path) - 1, i, -1):
            if math.hypot(path[j][0] - path[i][0], path[j][1] - path[i][1]) > MAX_SPACING:
                continue
            if line_ok(path[i], path[j]):
                best = j
                break
        nodes.append(path[best])
        i = best

    print(f"path {len(path)} tiles -> {len(nodes)} nodes (incl. endpoints)", file=sys.stderr)
    mids = nodes[1:-1]
    out = [{"X": x, "Y": y, "Z": z(x, y)} for x, y in mids]
    print(json.dumps(out, indent=2))


if __name__ == "__main__":
    main()
