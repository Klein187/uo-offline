#!/usr/bin/env python3
"""Bulk-fix EDGEWALK BLOCKED edges using Z-aware walkmap windows.

For every "EDGEWALK: BLOCKED: A <-> B" in Data/Live/audit_report.json:
  - fetch a Z walkmap window around the leg (live shard required)
  - A* with the server's step rules (|dz| <= 4, corner-cut guard)
  - path found  -> insert "A - B fixN" midpoint chain, drop the direct edge
  - path direct -> leave as-is (flagged ODD; something transient blocked it)
  - no path     -> drop the edge IF A and B stay graph-connected without it,
                   else leave it and flag MANUAL

Writes waypoints.json in place (backup .bak-bulkfix). Run reload+audit after.
"""
import collections
import heapq
import json
import math
import os
import shutil
import time

LIVE = r"C:/Users/logdc/uo-modernuo/ModernUO/Distribution/Data/Live"
WP = r"C:/Users/logdc/uo-modernuo/ModernUO/Distribution/Data/Waypoints/waypoints.json"
MARGIN = 55
MAX_CLIMB = 4
MAX_SPACING = 28


def read_pgm(path):
    data = open(path, "rb").read()
    parts = data.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    return w, h, parts[3][: w * h]


_token = int(time.time())


def fetch_window(x0, y0, x1, y1):
    global _token
    _token += 1
    pgm = os.path.join(LIVE, "walkmap.pgm")
    m0 = os.path.getmtime(pgm) if os.path.exists(pgm) else 0
    open(os.path.join(LIVE, "walkmap_request.txt"), "w").write(
        f"{_token} {x0} {y0} {x1} {y1}"
    )
    t0 = time.time()
    while time.time() - t0 < 120:
        time.sleep(0.5)
        if os.path.exists(pgm) and os.path.getmtime(pgm) != m0:
            time.sleep(0.3)
            break
    else:
        raise TimeoutError("walkmap timeout")
    w, h, mask = read_pgm(pgm)
    _, _, zs = read_pgm(os.path.join(LIVE, "walkmap_z.pgm"))
    return w, h, mask, zs


def solve_leg(ax, ay, bx, by):
    x0, y0 = min(ax, bx) - MARGIN, min(ay, by) - MARGIN
    x1, y1 = max(ax, bx) + MARGIN, max(ay, by) + MARGIN
    if x1 - x0 + 1 > 512 or y1 - y0 + 1 > 512:
        return None
    w, h, mask, zmap = fetch_window(x0, y0, x1, y1)

    def walk(x, y):
        return 0 <= x - x0 < w and 0 <= y - y0 < h and mask[(y - y0) * w + (x - x0)] > 127

    def z(x, y):
        return zmap[(y - y0) * w + (x - x0)] - 128

    def can_step(x, y, nx, ny):
        if not walk(nx, ny) or abs(z(nx, ny) - z(x, y)) > MAX_CLIMB:
            return False
        if nx != x and ny != y:
            ea = walk(nx, y) and abs(z(nx, y) - z(x, y)) <= MAX_CLIMB
            eb = walk(x, ny) and abs(z(x, ny) - z(x, y)) <= MAX_CLIMB
            if not ea and not eb:
                return False
        return True

    if not walk(ax, ay) or not walk(bx, by):
        return None

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
            ng = g[(x, y)] + c + abs(z(nx, ny) - z(x, y))
            if (nx, ny) not in g or ng < g[(nx, ny)]:
                g[(nx, ny)] = ng
                came[(nx, ny)] = (x, y)
                heapq.heappush(pq, (ng + 10 * max(abs(nx - bx), abs(ny - by)), nx, ny))
    if path is None:
        return None

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
    return [(x, y, z(x, y)) for x, y in nodes[1:-1]]


def main():
    report = json.load(open(os.path.join(LIVE, "audit_report.json")))
    pairs = []
    for s in report["findings"]:
        s = str(s)
        if s.startswith("EDGEWALK: BLOCKED: ") and "Trail" not in s:
            a, b = s[len("EDGEWALK: BLOCKED: "):].split(" <-> ")
            pairs.append((a.strip(), b.strip()))
    print(f"{len(pairs)} blocked pairs to process")

    root = json.load(open(WP, encoding="utf-8"))
    wps = root["Waypoints"]
    byname = {n["Name"]: n for n in wps}
    shutil.copy2(WP, WP + ".bak-bulkfix")

    # adjacency for connectivity checks (graph is auto-bidirectional in game)
    def build_adj(skip=None):
        adj = collections.defaultdict(set)
        for n in wps:
            for c in n.get("Connects", []):
                if c in byname:
                    e = frozenset((n["Name"], c))
                    if skip and e == skip:
                        continue
                    adj[n["Name"]].add(c)
                    adj[c].add(n["Name"])
        return adj

    def connected_without(a, b):
        adj = build_adj(skip=frozenset((a, b)))
        seen = {a}
        q = collections.deque([a])
        while q:
            x = q.popleft()
            if x == b:
                return True
            for y in adj[x]:
                if y not in seen:
                    seen.add(y)
                    q.append(y)
        return False

    fixed, dropped, manual, odd = [], [], [], []
    for a, b in pairs:
        na, nb = byname.get(a), byname.get(b)
        if not na or not nb:
            manual.append((a, b, "missing node"))
            continue
        try:
            mids = solve_leg(na["X"], na["Y"], nb["X"], nb["Y"])
        except TimeoutError:
            manual.append((a, b, "walkmap timeout"))
            continue
        if mids is None:
            if connected_without(a, b):
                na["Connects"] = [c for c in na.get("Connects", []) if c != b]
                nb["Connects"] = [c for c in nb.get("Connects", []) if c != a]
                dropped.append((a, b))
                print(f"DROP  {a} <-> {b} (no z-legal path; alt route exists)")
            else:
                manual.append((a, b, "no path AND edge is a bridge"))
                print(f"MANUAL {a} <-> {b}")
            continue
        if not mids:
            odd.append((a, b))
            print(f"ODD   {a} <-> {b} (z-legal direct line?)")
            continue
        prev = a
        for i, (x, y, zz) in enumerate(mids, 1):
            nm = f"{a} - {b} fix{i}"
            wps.append({"Name": nm, "X": x, "Y": y, "Z": zz, "Connects": [prev]})
            byname[nm] = wps[-1]
            prev = nm
        nb["Connects"] = [c for c in nb.get("Connects", []) if c != a] + [prev]
        na["Connects"] = [c for c in na.get("Connects", []) if c != b]
        fixed.append((a, b, len(mids)))
        print(f"FIX   {a} <-> {b} via {len(mids)} mid(s)")

    json.dump(root, open(WP, "w", encoding="utf-8"), indent=2)
    print(f"\nsaved. fixed={len(fixed)} dropped={len(dropped)} "
          f"odd={len(odd)} manual={len(manual)}")
    for m in manual:
        print("  MANUAL:", m)


if __name__ == "__main__":
    main()
