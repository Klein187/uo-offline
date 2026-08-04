#!/usr/bin/env python3
"""Z-aware live-walkmap verdicts for a set of graph edges (mob-immune).

Groups edges into <=480px windows, fetches walkmap.pgm + walkmap_z.pgm via
the live bridge, A*s each edge with fix_leg's rules (|dz|<=4 per step,
corner-cut guard). Verdicts: PATH n / NOPATH / ENDPOINT-DEAD.

Usage: python batch_edge_check.py edges_to_check.json
  input: [["Node A", "Node B"], ...]
  output: edge_verdicts.json {"A|B": {"verdict":..., "path": [[x,y],...]}}
"""
import heapq
import json
import os
import sys
import time
from collections import defaultdict

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

LIVE = os.path.expanduser(r"~/uo-modernuo/ModernUO/Distribution/Data/Live")
WPS = os.path.expanduser(r"~/uo-modernuo/ModernUO/Distribution/Data/Waypoints/waypoints.json")
HERE = os.path.dirname(os.path.abspath(__file__))
MAX_CLIMB = 4

pairs = json.load(open(sys.argv[1], encoding="utf-8"))
wdoc = json.load(open(WPS, encoding="utf-8"))
pos = {w["Name"]: (w["X"], w["Y"]) for w in wdoc["Waypoints"]}

def read_pgm(path):
    data = open(path, "rb").read()
    parts = data.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    return w, h, parts[3][: w * h]

def fetch_window(x0, y0, x1, y1):
    pgm = os.path.join(LIVE, "walkmap.pgm")
    zpgm = os.path.join(LIVE, "walkmap_z.pgm")
    m0 = os.path.getmtime(pgm) if os.path.exists(pgm) else 0
    token = int(time.time() * 1000) % 2000000000
    open(os.path.join(LIVE, "walkmap_request.txt"), "w").write(
        f"{token} {x0} {y0} {x1} {y1}")
    t0 = time.time()
    while time.time() - t0 < 240:
        time.sleep(1)
        if os.path.exists(pgm) and os.path.getmtime(pgm) != m0:
            break
    else:
        raise SystemExit("walkmap timeout — shard down?")
    # wait until BOTH files are complete for the requested dims (the bridge
    # writes mask then z; a short settle reads a truncated payload)
    want_w = x1 - x0 + 1
    for _i in range(30):
        time.sleep(1)
        try:
            w, h, mask = read_pgm(pgm)
            zw, zh, zs = read_pgm(zpgm)
        except Exception:
            continue
        if (w, h) == (zw, zh) and w >= min(want_w, 1) and \
           len(mask) >= w * h and len(zs) >= w * h:
            return w, h, mask, zs
    raise SystemExit("walkmap files never settled")

# group edges into windows greedily
edges = []
for a, b in pairs:
    if a in pos and b in pos:
        edges.append((a, b))
    else:
        print(f"skip {a}<->{b}: missing node")

clusters = []
for a, b in edges:
    ax, ay = pos[a]
    bx, by = pos[b]
    placed = False
    for c in clusters:
        nx0 = min(c["x0"], ax - 40, bx - 40)
        ny0 = min(c["y0"], ay - 40, by - 40)
        nx1 = max(c["x1"], ax + 40, bx + 40)
        ny1 = max(c["y1"], ay + 40, by + 40)
        if nx1 - nx0 <= 480 and ny1 - ny0 <= 480:
            c.update(x0=nx0, y0=ny0, x1=nx1, y1=ny1)
            c["edges"].append((a, b))
            placed = True
            break
    if not placed:
        clusters.append({"x0": min(ax, bx) - 40, "y0": min(ay, by) - 40,
                         "x1": max(ax, bx) + 40, "y1": max(ay, by) + 40,
                         "edges": [(a, b)]})

out = {}
for ci, c in enumerate(clusters):
    print(f"window {ci+1}/{len(clusters)} ({c['x0']},{c['y0']})-({c['x1']},{c['y1']}) "
          f"{len(c['edges'])} edges")
    W, H, mask, zmap = fetch_window(c["x0"], c["y0"], c["x1"], c["y1"])
    x0, y0 = c["x0"], c["y0"]
    def walk(x, y):
        return 0 <= x - x0 < W and 0 <= y - y0 < H and mask[(y - y0) * W + (x - x0)] > 127
    def zat(x, y):
        return zmap[(y - y0) * W + (x - x0)] - 128
    def can_step(x, y, nx, ny):
        if not walk(nx, ny):
            return False
        if abs(zat(nx, ny) - zat(x, y)) > MAX_CLIMB:
            return False
        if nx != x and ny != y:
            ea = walk(nx, y) and abs(zat(nx, y) - zat(x, y)) <= MAX_CLIMB
            eb = walk(x, ny) and abs(zat(x, ny) - zat(x, y)) <= MAX_CLIMB
            if not ea and not eb:
                return False
        return True
    for a, b in c["edges"]:
        ax, ay = pos[a]
        bx, by = pos[b]
        key = f"{a}|{b}"
        if not walk(ax, ay) or not walk(bx, by):
            out[key] = {"verdict": "ENDPOINT-DEAD",
                        "a_walk": walk(ax, ay), "b_walk": walk(bx, by)}
            continue
        g = {(ax, ay): 0}
        came = {}
        pq = [(0, ax, ay)]
        path = None
        budget = 250000
        dirs = [(-1, -1, 14), (0, -1, 10), (1, -1, 14), (-1, 0, 10),
                (1, 0, 10), (-1, 1, 14), (0, 1, 14), (1, 1, 14)]
        while pq and budget > 0:
            budget -= 1
            _f, x, y = heapq.heappop(pq)
            if (x, y) == (bx, by):
                path = [(x, y)]
                while (x, y) in came:
                    x, y = came[(x, y)]
                    path.append((x, y))
                path.reverse()
                break
            for dx, dy, cost in dirs:
                nx, ny = x + dx, y + dy
                if not can_step(x, y, nx, ny):
                    continue
                ng = g[(x, y)] + cost + abs(zat(nx, ny) - zat(x, y))
                if (nx, ny) not in g or ng < g[(nx, ny)]:
                    g[(nx, ny)] = ng
                    came[(nx, ny)] = (x, y)
                    heapq.heappush(pq, (ng + 10 * max(abs(nx - bx), abs(ny - by)), nx, ny))
        if path is None:
            out[key] = {"verdict": "NOPATH"}
        else:
            out[key] = {"verdict": f"PATH {len(path)}", "path": path}

json.dump(out, open(os.path.join(HERE, "edge_verdicts.json"), "w", encoding="utf-8"))
print("wrote edge_verdicts.json")
for k, v in out.items():
    print(f"  {v['verdict']:14s} {k}")
