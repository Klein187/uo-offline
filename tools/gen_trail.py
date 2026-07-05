#!/usr/bin/env python3
"""Auto-generate a waypoint trail between two points using the walkability atlas.

Generalizes the Sacrifice-trail pipeline: A* over tools/map/walk_atlas.pgm
(server-truth walkability from walkmap_atlas.py), then line-of-walk
simplification down to waypoint nodes spaced <= 28 tiles (the audit EDGE
check is euclidean <= 38; 28 leaves margin), emitted in waypoints.json
format as a chain "<Prefix> 1..N".

Usage:
    python gen_trail.py --from 1440 900 --to 1300 634 --prefix "Justice Trail" \
        [--attach-start "WP 879"] [--attach-end "Some WP"] \
        [--out trail.json] [--merge <path-to-waypoints.json>]

--attach-start/--attach-end add the named existing waypoint to the first/last
node's Connects (verify the distance is <= 38 yourself or via audit).
--merge appends the nodes directly into a waypoints.json (backup written).
Without --merge, prints the JSON snippet to stdout / --out.
"""
import argparse
import heapq
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ATLAS = os.path.join(HERE, "map", "walk_atlas.pgm")
MAX_SPACING = 28


def load_atlas(path):
    with open(path, "rb") as f:
        data = f.read()
    parts = data.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    return w, h, parts[3]


def walkable(atlas, w, h, x, y):
    return 0 <= x < w and 0 <= y < h and atlas[y * w + x] > 127


def snap(atlas, w, h, x, y, r=12):
    if walkable(atlas, w, h, x, y):
        return x, y
    for d in range(1, r + 1):
        for dx in range(-d, d + 1):
            for dy in (-d, d):
                if walkable(atlas, w, h, x + dx, y + dy):
                    return x + dx, y + dy
                if walkable(atlas, w, h, x + dy, y + dx):
                    return x + dy, y + dx
    raise SystemExit(f"no walkable tile within {r} of ({x},{y})")


def astar(atlas, w, h, start, goal, margin=80):
    x0 = max(0, min(start[0], goal[0]) - margin)
    y0 = max(0, min(start[1], goal[1]) - margin)
    x1 = min(w - 1, max(start[0], goal[0]) + margin)
    y1 = min(h - 1, max(start[1], goal[1]) + margin)
    bw = x1 - x0 + 1

    def idx(x, y):
        return (y - y0) * bw + (x - x0)

    inb = lambda x, y: x0 <= x <= x1 and y0 <= y <= y1
    g = {}
    came = {}
    sx, sy = start
    gx, gy = goal
    g[idx(sx, sy)] = 0
    pq = [(0, sx, sy)]
    dirs = [(-1, -1, 14), (0, -1, 10), (1, -1, 14), (-1, 0, 10),
            (1, 0, 10), (-1, 1, 14), (0, 1, 10), (1, 1, 14)]
    while pq:
        f, x, y = heapq.heappop(pq)
        if (x, y) == (gx, gy):
            path = [(x, y)]
            k = idx(x, y)
            while k in came:
                k = came[k]
                px = x0 + k % bw
                py = y0 + k // bw
                path.append((px, py))
            path.reverse()
            return path
        ck = idx(x, y)
        for dx, dy, c in dirs:
            nx, ny = x + dx, y + dy
            if not inb(nx, ny) or not walkable(atlas, w, h, nx, ny):
                continue
            nk = idx(nx, ny)
            ng = g[ck] + c
            if nk not in g or ng < g[nk]:
                g[nk] = ng
                came[nk] = ck
                hcost = 10 * max(abs(nx - gx), abs(ny - gy))
                heapq.heappush(pq, (ng + hcost, nx, ny))
    return None


def line_walkable(atlas, w, h, a, b):
    # Bresenham; every tile on the segment must be walkable.
    x, y = a
    x2, y2 = b
    dx = abs(x2 - x)
    dy = abs(y2 - y)
    sx = 1 if x2 > x else -1
    sy = 1 if y2 > y else -1
    err = dx - dy
    while True:
        if not walkable(atlas, w, h, x, y):
            return False
        if (x, y) == (x2, y2):
            return True
        e2 = 2 * err
        if e2 > -dy:
            err -= dy
            x += sx
        if e2 < dx:
            err += dx
            y += sy


def simplify(atlas, w, h, path):
    # Greedy: from each anchor take the farthest path point that is within
    # MAX_SPACING (euclidean) and has a clear straight walk line.
    nodes = [path[0]]
    i = 0
    while i < len(path) - 1:
        best = i + 1
        for j in range(len(path) - 1, i, -1):
            ex = path[j][0] - path[i][0]
            ey = path[j][1] - path[i][1]
            if math.hypot(ex, ey) > MAX_SPACING:
                continue
            if line_walkable(atlas, w, h, path[i], path[j]):
                best = j
                break
        nodes.append(path[best])
        i = best
    return nodes


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--from", dest="src", nargs=2, type=int, required=True)
    ap.add_argument("--to", dest="dst", nargs=2, type=int, required=True)
    ap.add_argument("--prefix", required=True)
    ap.add_argument("--attach-start")
    ap.add_argument("--attach-end")
    ap.add_argument("--out")
    ap.add_argument("--merge")
    ap.add_argument("--atlas", default=ATLAS)
    ap.add_argument("--margin", type=int, default=80)
    args = ap.parse_args()

    w, h, atlas = load_atlas(args.atlas)
    start = snap(atlas, w, h, *args.src)
    goal = snap(atlas, w, h, *args.dst)
    if start != tuple(args.src):
        print(f"start snapped {tuple(args.src)} -> {start}", file=sys.stderr)
    if goal != tuple(args.dst):
        print(f"goal snapped {tuple(args.dst)} -> {goal}", file=sys.stderr)

    path = astar(atlas, w, h, start, goal, args.margin)
    if path is None:
        raise SystemExit(
            "NO PATH — points are in different walk components "
            "(try a bigger --margin, or the gap is real: water/cliff)"
        )
    nodes = simplify(atlas, w, h, path)
    print(f"path {len(path)} tiles -> {len(nodes)} waypoints", file=sys.stderr)

    recs = []
    for i, (x, y) in enumerate(nodes, 1):
        rec = {"Name": f"{args.prefix} {i}", "X": x, "Y": y, "Z": 0, "Connects": []}
        if i > 1:
            rec["Connects"].append(f"{args.prefix} {i - 1}")
        recs.append(rec)
    if args.attach_start:
        recs[0]["Connects"].append(args.attach_start)
    if args.attach_end:
        recs[-1]["Connects"].append(args.attach_end)

    if args.merge:
        root = json.load(open(args.merge, encoding="utf-8"))
        key = "Waypoints" if "Waypoints" in root else None
        if key is None:
            raise SystemExit("no Waypoints array in merge target")
        existing = {n["Name"] for n in root[key]}
        clash = [r["Name"] for r in recs if r["Name"] in existing]
        if clash:
            raise SystemExit(f"name clash, not merging: {clash[:3]}...")
        import shutil

        shutil.copy2(args.merge, args.merge + ".bak-gentrail")
        root[key].extend(recs)
        with open(args.merge, "w", encoding="utf-8") as f:
            json.dump(root, f, indent=2)
        print(f"merged {len(recs)} nodes into {args.merge} (backup .bak-gentrail)")
    else:
        out = json.dumps(recs, indent=2)
        if args.out:
            open(args.out, "w", encoding="utf-8").write(out)
            print(f"wrote {args.out}")
        else:
            print(out)


if __name__ == "__main__":
    main()
