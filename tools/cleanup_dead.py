#!/usr/bin/env python3
"""Post-verdict cleanup: drop stable-blocked edges, delete unstandable nodes,
reroute severed surface trails via live-walkmap A*.

Produces dungeon_fix_plan_cleanup.json with:
  wp_edge_drop  — all stable-blocked pairs
  wp_del        — nodes whose tile is unstandable in the live walkmap
  wp_add / wp_edge_add — reroute chains for Moonglow Road 6<->8,
                  Shame Approach 31<->33, Wrong Approach 28<->29
Then apply with apply_cleanup (in this file, 'apply' argv).
"""
import heapq
import json
import os
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
HERE = os.path.dirname(os.path.abspath(__file__))
LIVE = os.path.expanduser(r"~/uo-modernuo/ModernUO/Distribution/Data/Live")
WPS = os.path.expanduser(r"~/uo-modernuo/ModernUO/Distribution/Data/Waypoints/waypoints.json")
MAX_CLIMB = 4
MAX_EDGE = 26

stable = [tuple(p) for p in json.load(open(os.path.join(HERE, "edges_to_check.json"), encoding="utf-8"))]
verdicts = json.load(open(os.path.join(HERE, "edge_verdicts.json"), encoding="utf-8"))
wdoc = json.load(open(WPS, encoding="utf-8"))
pos = {w["Name"]: (w["X"], w["Y"], w.get("Z", 0)) for w in wdoc["Waypoints"]}

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
        raise SystemExit("walkmap timeout")
    for _ in range(30):
        time.sleep(1)
        try:
            w, h, mask = read_pgm(pgm)
            zw, zh, zs = read_pgm(zpgm)
        except Exception:
            continue
        if (w, h) == (zw, zh) and len(mask) >= w * h and len(zs) >= w * h:
            return w, h, mask, zs
    raise SystemExit("walkmap never settled")

# ---- 1. dead nodes: every endpoint of a stable edge, walkability from verdicts
dead = set()
checked = {}
for a, b in stable:
    v = verdicts.get(f"{a}|{b}") or verdicts.get(f"{b}|{a}")
    if not v:
        continue
    if v["verdict"] == "ENDPOINT-DEAD":
        if v.get("a_walk") is False:
            dead.add(a)
        if v.get("b_walk") is False:
            dead.add(b)

# my Link nodes that end up useless (every edge of theirs is being dropped)
plan_wp = json.load(open(os.path.join(HERE, "dungeon_fix_plan_wp.json"), encoding="utf-8"))
my_nodes = {w["Name"] for w in plan_wp.get("wp_add", [])}
drop_set = {frozenset(p) for p in stable}
adj = {}
for w in wdoc["Waypoints"]:
    adj[w["Name"]] = set(w.get("Connects", ()))
for n in my_nodes:
    if n not in adj:
        continue
    left = {c for c in adj[n] if frozenset((n, c)) not in drop_set}
    if not left:
        dead.add(n)

# ---- 2. reroutes over live walkmap
reroutes = [("Moonglow Road 6", "Moonglow Road 8"),
            ("Shame Approach 31", "Shame Approach 33"),
            ("Wrong Approach 28", "Wrong Approach 29")]
out_nodes = []
out_edges = []
seq = 0
for a, b in reroutes:
    if a not in pos or b not in pos:
        print(f"reroute {a}<->{b}: missing node")
        continue
    ax, ay, az = pos[a]
    bx, by, bz = pos[b]
    x0, y0 = min(ax, bx) - 60, min(ay, by) - 60
    x1, y1 = max(ax, bx) + 60, max(ay, by) + 60
    W, H, mask, zmap = fetch_window(x0, y0, x1, y1)
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
    if not walk(ax, ay) or not walk(bx, by):
        print(f"reroute {a}<->{b}: an ANCHOR endpoint is dead in walkmap "
              f"(a={walk(ax,ay)} b={walk(bx,by)}) — skipping, needs wider fix")
        continue
    g = {(ax, ay): 0}
    came = {}
    pq = [(0, ax, ay)]
    path = None
    dirs = [(-1, -1, 14), (0, -1, 10), (1, -1, 14), (-1, 0, 10),
            (1, 0, 10), (-1, 1, 14), (0, 1, 14), (1, 1, 14)]
    while pq:
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
        print(f"reroute {a}<->{b}: NO PATH in window — leaving severed")
        continue
    # simplify with walk-line checks over the window
    def line_ok(p, q):
        px, py = p
        qx, qy = q
        n = max(abs(qx - px), abs(qy - py))
        lx, ly = px, py
        for i in range(1, n + 1):
            x = round(px + (qx - px) * i / n)
            y = round(py + (qy - py) * i / n)
            if not can_step(lx, ly, x, y):
                return False
            lx, ly = x, y
        return True
    pts = [path[0]]
    i = 0
    while i < len(path) - 1:
        j = len(path) - 1
        while j > i + 1:
            if max(abs(path[i][0] - path[j][0]), abs(path[i][1] - path[j][1])) <= MAX_EDGE \
               and line_ok(path[i], path[j]):
                break
            j -= 1
        pts.append(path[j])
        i = j
    chain = pts[1:-1]
    prev = a
    reg = a.rsplit(" ", 2)[0]
    print(f"reroute {a}<->{b}: path {len(path)} tiles -> {len(chain)} link node(s)")
    for (px, py) in chain:
        seq += 1
        nm = f"{reg} Fix WP {seq}"
        out_nodes.append({"Name": nm, "X": px, "Y": py, "Z": zat(px, py)})
        out_edges.append([prev, nm])
        prev = nm
    out_edges.append([prev, b])

plan = {"wp_edge_drop": [list(p) for p in stable],
        "wp_del": sorted(dead),
        "wp_add": out_nodes,
        "wp_edge_add": out_edges,
        "wp_edge_audit": []}
json.dump(plan, open(os.path.join(HERE, "dungeon_fix_plan_cleanup.json"), "w",
                     encoding="utf-8"), indent=1)
print(f"\ncleanup plan: -{len(stable)} edges, -{len(dead)} dead nodes, "
      f"+{len(out_nodes)} reroute nodes, +{len(out_edges)} reroute edges")
print("dead nodes:", ", ".join(sorted(dead)))
