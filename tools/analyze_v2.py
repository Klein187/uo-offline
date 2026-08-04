#!/usr/bin/env python3
"""Dungeon nav analyzer v2 — atlas-aware, engine-parity anchoring.

Adds over v1:
  - walk-atlas flood components per dungeon region (2D standability truth)
  - landing-anchor validation: the EffDist rank-1 node at each pad dst must
    share the landing's atlas flood (else the crawler pools the wrong floor)
  - bridge candidates: graph comps that share an atlas flood but are
    disconnected (walkable splits the force-drop pass left behind)
  - Lost Lands vs Britannia surface distinction (LL = no nav = not an exit)
  - per-mesh canonical tag plan (minimal churn) + retype plan
Everything is PLANNED, not applied. Output: dungeon_plan.json + report.
"""
import json
import math
import os
import sys
from collections import defaultdict, deque, Counter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

BASE = os.path.expanduser(r"~/uo-modernuo/ModernUO/Distribution/Data")
ATLAS = os.path.expanduser(r"~/uo-offline/tools/map/walk_atlas.pgm")
HERE = os.path.dirname(os.path.abspath(__file__))

MAX_ANCHOR = 38
SHUFFLE_R = 4
BRIT_MAX_X = 5119   # x <= this and not in a dungeon rect -> Britannia surface

def cheby(a, b):
    return max(abs(a[0] - b[0]), abs(a[1] - b[1]))

# ---------------------------------------------------------------- loading
tel = [t for t in json.load(open(BASE + "/teleporters.json", encoding="utf-8"))
       if t["src"]["map"] == "Felucca" and t["dst"]["map"] == "Felucca"]
wdoc = json.load(open(BASE + "/Waypoints/waypoints.json", encoding="utf-8"))
nodes = {w["Name"]: (w["X"], w["Y"], w.get("Z", 0), tuple(w.get("Connects", ())))
         for w in wdoc["Waypoints"]}
ddoc = json.load(open(BASE + "/Destinations/destinations.json", encoding="utf-8"))
dests = ddoc["Destinations"]
regions = json.load(open(BASE + "/regions.json", encoding="utf-8"))
dungeon_rects = []
for r in regions:
    if "Dungeon" in r.get("$type", "") and r.get("Map") == "Felucca":
        for a in r.get("Area", []):
            dungeon_rects.append((r["Name"], a["x1"], a["y1"], a["x2"], a["y2"]))

def region_at(x, y):
    for n, x1, y1, x2, y2 in dungeon_rects:
        if x1 <= x <= x2 and y1 <= y <= y2:
            return n
    return None

data = open(ATLAS, "rb").read()
parts = data.split(b"\n", 3)
AW, AH = map(int, parts[1].split())
A = parts[3]
def walk(x, y):
    return 0 <= x < AW and 0 <= y < AH and A[y * AW + x] == 255

# ---------------------------------------------------------------- graph comps
parent = {n: n for n in nodes}
def find(a):
    while parent[a] != a:
        parent[a] = parent[parent[a]]
        a = parent[a]
    return a
def union(a, b):
    ra, rb = find(a), find(b)
    if ra != rb:
        parent[ra] = rb
for n, (x, y, z, cs) in nodes.items():
    for c in cs:
        if c in nodes:
            union(n, c)
comp = {n: find(n) for n in nodes}
comp_size = Counter(comp.values())

grid = defaultdict(list)
CELL = 32
for n, (x, y, z, _c) in nodes.items():
    grid[(x // CELL, y // CELL)].append((n, x, y, z))

def eff(nx, ny, nz, x, y, z):
    dz = abs(nz - z)
    d = math.hypot(nx - x, ny - y)
    return d + (dz - 10) * 3 if dz > 10 else d

def nearest_nodes(x, y, z, k=3):
    out = []
    cx, cy = x // CELL, y // CELL
    cands = []
    for gx in range(cx - 3, cx + 4):
        for gy in range(cy - 3, cy + 4):
            cands.extend(grid.get((gx, gy), ()))
    if not cands:
        for n2, (nx, ny, nz2, _c) in nodes.items():
            cands.append((n2, nx, ny, nz2))
    scored = sorted((eff(nx, ny, nz2, x, y, z), n2, nx, ny)
                    for n2, nx, ny, nz2 in cands)
    return scored[:k]

def anchor(x, y, z):
    """(comp, node) per engine rule, else (None, rank1node)"""
    nn = nearest_nodes(x, y, z, 1)
    if not nn:
        return None, None
    _d, n, nx, ny = nn[0]
    if cheby((x, y), (nx, ny)) > MAX_ANCHOR:
        return None, n
    return comp[n], n

# ---------------------------------------------------------------- atlas floods
# flood the walkable tiles inside each region's rects (8-way), label tiles
flood_id = {}
flood_sizes = Counter()
fid = 0
for rname, x1, y1, x2, y2 in dungeon_rects:
    for sy in range(y1, y2 + 1):
        for sx in range(x1, x2 + 1):
            if not walk(sx, sy) or (sx, sy) in flood_id:
                continue
            fid += 1
            q = deque([(sx, sy)])
            flood_id[(sx, sy)] = fid
            flood_sizes[fid] += 1
            while q:
                px, py = q.popleft()
                for dx in (-1, 0, 1):
                    for dy in (-1, 0, 1):
                        if dx == 0 and dy == 0:
                            continue
                        t = (px + dx, py + dy)
                        if t in flood_id or not walk(*t):
                            continue
                        # stay inside the union of dungeon rects
                        if region_at(*t) is None:
                            continue
                        flood_id[t] = fid
                        flood_sizes[fid] += 1
                        q.append(t)

def flood_at(x, y, r=2):
    """flood id at tile, or nearest within r (landing tiles can be on
    non-standable teleporter statics)"""
    if (x, y) in flood_id:
        return flood_id[(x, y)]
    for rad in range(1, r + 1):
        for dx in range(-rad, rad + 1):
            for dy in range(-rad, rad + 1):
                if max(abs(dx), abs(dy)) == rad and (x + dx, y + dy) in flood_id:
                    return flood_id[(x + dx, y + dy)]
    return None

# node -> flood
node_flood = {}
for n, (x, y, z, _c) in nodes.items():
    if region_at(x, y):
        node_flood[n] = flood_at(x, y, r=2)

# ---------------------------------------------------------------- pads
pads = defaultdict(list)
for t in tel:
    sx, sy, sz = t["src"]["loc"]
    dx, dy, dz = t["dst"]["loc"]
    pads[(sx, sy)].append({"z": sz, "dst": (dx, dy, dz), "back": False})
    if t.get("back"):
        pads[(dx, dy)].append({"z": dz, "dst": (sx, sy, sz), "back": True})

def nearest_pad(x, y, r=6):
    best, bestd = None, r + 1
    for (px, py) in pads:
        d = cheby((x, y), (px, py))
        if d < bestd:
            bestd, best = d, (px, py)
    return best, bestd

def loc_label(x, y, z):
    """SURFACE (Britannia, has nav) / LOSTLAND / comp / ~Region"""
    reg = region_at(x, y)
    if reg is None:
        return "SURFACE" if x <= BRIT_MAX_X else "LOSTLAND"
    c, _n = anchor(x, y, z)
    return c if c is not None else "~" + reg

# every physical pad edge
pad_edges = []
for (px, py), lst in pads.items():
    for p in lst:
        sl = loc_label(px, py, p["z"])
        dl = loc_label(*p["dst"])
        pad_edges.append({"src": (px, py, p["z"]), "dst": p["dst"],
                          "sl": sl, "dl": dl, "back": p["back"]})

# depth BFS from SURFACE
adj = defaultdict(set)
for e in pad_edges:
    adj[e["sl"]].add(e["dl"])
depth = {"SURFACE": 0}
q = deque(["SURFACE"])
while q:
    cur = q.popleft()
    for nxt in adj[cur]:
        if nxt not in depth and nxt != "LOSTLAND":
            depth[nxt] = depth[cur] + 1
            q.append(nxt)

# ---------------------------------------------------------------- records
interior_types = {"DungeonRoom", "DungeonDescend", "DungeonAscend"}
interior = [d for d in dests if d.get("Type") in interior_types]
entrances = [d for d in dests if d.get("Type") == "DungeonEntrance"]

rec = []
for d in interior:
    x, y, z = d["X"], d["Y"], d.get("Z", 0)
    c, anch = anchor(x, y, z)
    rec.append({"d": d, "name": d["Name"], "type": d["Type"],
                "tag": d.get("Dungeon", ""), "level": d.get("Level", 0),
                "x": x, "y": y, "z": z, "comp": c, "anchor": anch,
                "region": region_at(x, y), "flood": flood_at(x, y, r=2)})

meshes = defaultdict(lambda: {"recs": [], "region": Counter(), "floods": Counter()})
for ri in rec:
    if ri["comp"] is None:
        continue
    m = meshes[ri["comp"]]
    m["recs"].append(ri)
    if ri["region"]:
        m["region"][ri["region"]] += 1
# mesh node flood makeup + region even for meshes without records
mesh_nodes = defaultdict(list)
for n, c in comp.items():
    mesh_nodes[c].append(n)
for c, m in meshes.items():
    for n in mesh_nodes[c]:
        f = node_flood.get(n)
        if f:
            m["floods"][f] += 1

# ---------------------------------------------------------------- findings
report = defaultdict(list)

# 1. pad-edge anchor validation at landings (engine parity failure list)
land_fix = []
for e in pad_edges:
    dx, dy, dz = e["dst"]
    reg = region_at(dx, dy)
    if reg is None:
        continue
    lf = flood_at(dx, dy, r=2)
    if lf is None:
        report["landing_not_on_atlas"].append(f"pad {e['src']} -> ({dx},{dy},{dz}) [{reg}] "
                                              f"lands on non-standable atlas tile")
        continue
    nn = nearest_nodes(dx, dy, dz, 1)
    if not nn:
        continue
    _d, n1, nx, ny = nn[0]
    ok = cheby((dx, dy), (nx, ny)) <= MAX_ANCHOR and node_flood.get(n1) == lf
    if not ok:
        land_fix.append({"dst": (dx, dy, dz), "region": reg, "flood": lf,
                         "rank1": n1, "rank1_flood": node_flood.get(n1),
                         "src": e["src"]})
        report["landing_misanchor"].append(
            f"pad {e['src']} -> ({dx},{dy},{dz}) [{reg}]: rank1 node '{n1}' "
            f"flood={node_flood.get(n1)} vs landing flood={lf}")

# 2. bridge candidates: comps sharing a flood but disconnected
flood_comps = defaultdict(set)
for n, f in node_flood.items():
    if f:
        flood_comps[f].add(comp[n])
for f, cs in sorted(flood_comps.items()):
    if len(cs) > 1:
        sizes = {c: sum(1 for n in mesh_nodes[c] if node_flood.get(n) == f) for c in cs}
        report["split_mesh"].append(
            f"atlas flood {f} (sz {flood_sizes[f]}) spans {len(cs)} graph comps: "
            + ", ".join(f"{c}({sizes[c]}n)" for c in sorted(cs, key=lambda c: -sizes[c])))

# 3. record direction truth
for ri in rec:
    if ri["type"] == "DungeonRoom":
        continue
    plist = pads.get((ri["x"], ri["y"]))
    if not plist:
        np_, npd = nearest_pad(ri["x"], ri["y"])
        report["record_off_pad"].append(
            f"{ri['name']} [{ri['type']}] ({ri['x']},{ri['y']},{ri['z']}) not on a pad "
            f"(nearest {np_} d={npd})")
        ri["ok"] = False
        continue
    ri["ok"] = True
    p = min(plist, key=lambda pp: abs(pp["z"] - ri["z"]))
    dl = loc_label(*p["dst"])
    sd, dd = depth.get(ri["comp"]), depth.get(dl)
    ri["dst"] = p["dst"]
    ri["dl"] = dl
    if dl == "SURFACE":
        ri["dir"] = "up"
    elif dl == "LOSTLAND":
        ri["dir"] = "lostland"
    elif sd is None or dd is None:
        ri["dir"] = "unknown"
    elif dd < sd:
        ri["dir"] = "up"
    elif dd > sd:
        ri["dir"] = "down"
    else:
        ri["dir"] = "lateral"

# 4. per-mesh summary with target typing
mesh_rows = []
for c, m in sorted(meshes.items(), key=lambda kv: (kv[1]["region"].most_common(1)[0][0]
                                                   if kv[1]["region"] else "?", str(kv[0]))):
    reg = m["region"].most_common(1)[0][0] if m["region"] else "?"
    d = depth.get(c)
    tags = Counter((r["tag"], r["level"]) for r in m["recs"])
    rooms = [r for r in m["recs"] if r["type"] == "DungeonRoom"]
    trans = [r for r in m["recs"] if r["type"] != "DungeonRoom"]
    ups = [r for r in trans if r.get("dir") == "up"]
    downs = [r for r in trans if r.get("dir") == "down"]
    lats = [r for r in trans if r.get("dir") in ("lateral", "lostland")]
    unk = [r for r in trans if r.get("dir") == "unknown"]
    mesh_rows.append((reg, str(c), d, len(mesh_nodes[c]), len(rooms), len(ups),
                      len(downs), len(lats), len(unk),
                      ", ".join(f"{t}|{l}x{n}" for (t, l), n in tags.most_common())))

# 5. physical pads with no record, on record-bearing meshes (potential missing
#    stairs), and pads on meshes at finite depth with no up-pad record
mesh_has = defaultdict(lambda: defaultdict(list))
for ri in rec:
    if ri["type"] != "DungeonRoom" and ri.get("ok"):
        mesh_has[ri["comp"]][(ri["x"], ri["y"])].append(ri)
padless = []
for e in pad_edges:
    sl = e["sl"]
    if sl in ("SURFACE", "LOSTLAND") or isinstance(sl, str) and sl.startswith("~"):
        continue
    if sl not in meshes:
        continue
    sx, sy, sz = e["src"]
    # covered if any transition record within 2 tiles on this mesh
    cov = any(cheby((sx, sy), pos) <= 2 for pos in mesh_has[sl])
    if not cov:
        padless.append(e)
        report["pad_without_record"].append(
            f"[{region_at(sx,sy)}] mesh {sl} pad ({sx},{sy},{sz}) -> {e['dl']} "
            f"({e['dst']}) has no transition record")

# 6. entrances
for d in entrances:
    x, y, z = d["X"], d["Y"], d.get("Z", 0)
    arrs = [(a["X"], a["Y"], a.get("Z", z)) for a in d.get("Arrivals", [])]
    if not arrs and "ArrivalX" in d:
        arrs = [(d["ArrivalX"], d["ArrivalY"], d.get("ArrivalZ", z))]
    for (ax, ay, az) in arrs:
        plist = pads.get((ax, ay))
        if not plist:
            np_, npd = nearest_pad(ax, ay)
            report["entrance_arrival_off_pad"].append(
                f"'{d['Name']}' arrival ({ax},{ay},{az}) not on a pad (nearest {np_} d={npd})")
        else:
            p = min(plist, key=lambda pp: abs(pp["z"] - az))
            if abs(p["z"] - az) > 5:
                report["entrance_arrival_z_off"].append(
                    f"'{d['Name']}' arrival ({ax},{ay},{az}) vs pad z {p['z']}")
    if (x, y) in pads:
        report["entrance_location_on_pad"].append(f"'{d['Name']}' Location on pad")

# 7. waypoints on pads / rooms near pads
for n, (x, y, z, _c) in nodes.items():
    if (x, y) in pads:
        report["waypoint_on_pad"].append(f"'{n}' ({x},{y},{z}) region={region_at(x,y)}")
for ri in rec:
    if ri["type"] == "DungeonRoom":
        np_, npd = nearest_pad(ri["x"], ri["y"], r=SHUFFLE_R)
        if np_ is not None:
            report["room_near_pad"].append(
                f"'{ri['name']}' ({ri['x']},{ri['y']}) d={npd} from pad {np_}")

# ---------------------------------------------------------------- print
print(f"nodes={len(nodes)} pads={len(pads)} pad_edges={len(pad_edges)} "
      f"floods={len(flood_sizes)} meshes_with_recs={len(meshes)}")
print()
print(f"{'region':14s} {'mesh':24s} {'dep':>3s} {'nod':>4s} {'rm':>3s} "
      f"{'up':>3s} {'dn':>3s} {'lat':>3s} {'unk':>3s}  tags")
for row in mesh_rows:
    reg, c, d, nn, rm, up, dn, lat, unk, tags = row
    print(f"{reg:14s} {c:24s} {str(d):>3s} {nn:4d} {rm:3d} {up:3d} {dn:3d} "
          f"{lat:3d} {unk:3d}  {tags}")
print()
for k in sorted(report):
    lst = report[k]
    print(f"== {k} ({len(lst)}) ==")
    for line in lst[:30]:
        print("   " + line)
    if len(lst) > 30:
        print(f"   ... +{len(lst)-30} more")
    print()

json.dump({"report": dict(report),
           "mesh_rows": mesh_rows,
           "depth": {str(k): v for k, v in depth.items()},
           "land_fix": land_fix},
          open(os.path.join(HERE, "dungeon_analysis2.json"), "w", encoding="utf-8"),
          indent=1, default=str)
print("wrote dungeon_analysis2.json")
