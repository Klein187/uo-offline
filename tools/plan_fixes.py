#!/usr/bin/env python3
"""Dungeon nav fix planner — produces dungeon_fix_plan.json + summary.

Order of operations (mirrors how the fixes will be applied):
  1. same-flood reconnect      : bridge graph comps that share an atlas flood
                                 (offline-provable walkable; direct edge or
                                 A*-corridor chain nodes)
  2. pad reachability          : BFS from Britannia SURFACE over teleporter
                                 pads across the post-reconnect meshes
  3. cross-flood bridges       : unreached mesh -> nearest reached mesh of the
                                 same region, <=36 tiles (Z-overlap stairways
                                 the 2D atlas can't see) — AUDIT-PENDING,
                                 validated in-game later
  4. retype / retag / rename   : Ascend = depth decreases, Descend = deeper /
                                 lateral / lost-lands / other-region; per-mesh
                                 canonical unique tags; Level = tag number
  5. rooms                     : beside-pad anchors pushed to 5..10 tiles,
                                 sparse floors topped up from spread nodes
  6. missing records           : real level-links with no record (Wrong L1->L2,
                                 Orc Cave L2->L1)
  7. approach repair           : Wrong Approach 28<->29 blocked edge reroute

Run: python plan_fixes.py            (full dry-run plan, prints summary)
     python plan_fixes.py --stage1   (waypoint ops only -> dungeon_fix_plan_wp.json)
     python plan_fixes.py --stage2   (NO graph additions; records planned against
                                      the CURRENT audited waypoints.json ->
                                      dungeon_fix_plan_dest.json)
"""
import heapq
import json
import math
import os
import sys
from collections import defaultdict, deque, Counter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

STAGE1 = "--stage1" in sys.argv
STAGE2 = "--stage2" in sys.argv

BASE = os.path.expanduser(r"~/uo-modernuo/ModernUO/Distribution/Data")
ATLAS = os.path.expanduser(r"~/uo-offline/tools/map/walk_atlas.pgm")
HERE = os.path.dirname(os.path.abspath(__file__))
PLAN = os.path.join(HERE, "dungeon_fix_plan_wp.json" if STAGE1 else
                    "dungeon_fix_plan_dest.json" if STAGE2 else
                    "dungeon_fix_plan.json")

TARGET = ["Covetous", "Deceit", "Despise", "Destard", "Hythloth", "Shame",
          "Wrong", "Fire", "Ice", "Orc Cave", "Terathan Keep", "Khaldun"]
MAX_ANCHOR = 38
MAX_EDGE = 26          # generator's edge cap
BRIDGE_CAP = 36        # cross-flood candidate cap (PathFollower box is 38)
BRIT_MAX_X = 5119
ROOM_MIN_PAD_DIST = 5
ROOM_MAX_PAD_DIST = 10

def cheb(a, b):
    return max(abs(a[0] - b[0]), abs(a[1] - b[1]))

# ---------------------------------------------------------------- load
tel = [t for t in json.load(open(BASE + "/teleporters.json", encoding="utf-8"))
       if t["src"]["map"] == "Felucca" and t["dst"]["map"] == "Felucca"]
wdoc = json.load(open(BASE + "/Waypoints/waypoints.json", encoding="utf-8"))
wp_list = wdoc["Waypoints"]
nodes = {w["Name"]: w for w in wp_list}
ddoc = json.load(open(BASE + "/Destinations/destinations.json", encoding="utf-8"))
dests = ddoc["Destinations"]
regions = json.load(open(BASE + "/regions.json", encoding="utf-8"))

rects = []          # ALL dungeon rects (any name)
target_rects = []
for r in regions:
    if "Dungeon" in r.get("$type", "") and r.get("Map") == "Felucca":
        for a in r.get("Area", []):
            rects.append((r["Name"], a["x1"], a["y1"], a["x2"], a["y2"]))
            if r["Name"] in TARGET:
                target_rects.append((r["Name"], a["x1"], a["y1"], a["x2"], a["y2"]))

def region_any(x, y):
    for n, x1, y1, x2, y2 in rects:
        if x1 <= x <= x2 and y1 <= y <= y2:
            return n
    return None

def region_target(x, y):
    r = region_any(x, y)
    return r if r in TARGET else None

data = open(ATLAS, "rb").read()
parts = data.split(b"\n", 3)
AW, AH = map(int, parts[1].split())
A = parts[3]
def walk(x, y):
    return 0 <= x < AW and 0 <= y < AH and A[y * AW + x] > 127

def line_of_walk(a, b):
    x0, y0 = a
    x1, y1 = b
    n = max(abs(x1 - x0), abs(y1 - y0))
    for i in range(n + 1):
        x = round(x0 + (x1 - x0) * i / max(n, 1))
        y = round(y0 + (y1 - y0) * i / max(n, 1))
        if not walk(x, y):
            return False
    return True

# ---------------------------------------------------------------- atlas floods (target rects only)
flood_id = {}
fid = 0
flood_tiles = defaultdict(set)
for rname, x1, y1, x2, y2 in target_rects:
    for sy in range(y1, y2 + 1):
        for sx in range(x1, x2 + 1):
            if not walk(sx, sy) or (sx, sy) in flood_id:
                continue
            fid += 1
            q = deque([(sx, sy)])
            flood_id[(sx, sy)] = fid
            flood_tiles[fid].add((sx, sy))
            while q:
                px, py = q.popleft()
                for dx in (-1, 0, 1):
                    for dy in (-1, 0, 1):
                        if dx == 0 and dy == 0:
                            continue
                        t = (px + dx, py + dy)
                        if t in flood_id or not walk(*t):
                            continue
                        if region_target(*t) is None:
                            continue
                        flood_id[t] = fid
                        flood_tiles[fid].add(t)
                        q.append(t)

def flood_at(x, y, r=2):
    if (x, y) in flood_id:
        return flood_id[(x, y)]
    for rad in range(1, r + 1):
        for dx in range(-rad, rad + 1):
            for dy in range(-rad, rad + 1):
                if max(abs(dx), abs(dy)) == rad and (x + dx, y + dy) in flood_id:
                    return flood_id[(x + dx, y + dy)]
    return None

def astar_flood(f, start, goal):
    tiles = flood_tiles[f]
    if start not in tiles or goal not in tiles:
        return None
    openq = [(cheb(start, goal), 0, start, None)]
    came = {}
    gbest = {start: 0}
    while openq:
        _f, g, cur, par = heapq.heappop(openq)
        if cur in came:
            continue
        came[cur] = par
        if cur == goal:
            path = [cur]
            while came[path[-1]] is not None:
                path.append(came[path[-1]])
            return path[::-1]
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                if dx == 0 and dy == 0:
                    continue
                n2 = (cur[0] + dx, cur[1] + dy)
                if n2 not in tiles or n2 in came:
                    continue
                ng = g + 1
                if ng < gbest.get(n2, 1 << 30):
                    gbest[n2] = ng
                    heapq.heappush(openq, (ng + cheb(n2, goal), ng, n2, cur))
    return None

def simplify(path):
    if not path:
        return []
    out = [path[0]]
    i = 0
    while i < len(path) - 1:
        j = len(path) - 1
        while j > i + 1:
            if cheb(path[i], path[j]) <= MAX_EDGE and line_of_walk(path[i], path[j]):
                break
            j -= 1
        out.append(path[j])
        i = j
    return out

def corridor_pts(path):
    """simplify interior points, shifted off teleporter pads; None if impossible"""
    pts = simplify(path)[1:-1]
    idx = {p: i for i, p in enumerate(path)}
    out = []
    for p in pts:
        if pad_clear(*p):
            out.append(p)
            continue
        i = idx.get(p, 0)
        moved = None
        for off in (1, -1, 2, -2, 3, -3):
            j = i + off
            if 0 <= j < len(path) and pad_clear(*path[j]):
                moved = path[j]
                break
        if moved is None:
            return None
        out.append(moved)
    for a, b in zip([path[0]] + out, out + [path[-1]]):
        if cheb(a, b) > MAX_EDGE:
            return None
    return out

# ---------------------------------------------------------------- graph
adj_now = defaultdict(set)
for w in wp_list:
    for c in w.get("Connects", ()):
        if c in nodes:
            adj_now[w["Name"]].add(c)
            adj_now[c].add(w["Name"])

def comps_of(adj):
    comp = {}
    for n in nodes:
        if n in comp:
            continue
        rep = n
        stack = [n]
        comp[n] = rep
        while stack:
            cur = stack.pop()
            for nb in adj[cur]:
                if nb not in comp:
                    comp[nb] = rep
                    stack.append(nb)
    return comp

# working copy of edges we PLAN to have
adj_plan = {k: set(v) for k, v in adj_now.items()}
plan = {"wp_edge_add": [], "wp_edge_audit": [], "wp_add": [],
        "dest_retype": [], "dest_retag": [], "dest_rename": [],
        "dest_move": [], "dest_add": [], "wp_edge_drop": [], "notes": []}
new_nodes = {}
link_seq = defaultdict(int)

def node_pos(n):
    if n in nodes:
        w = nodes[n]
        return (w["X"], w["Y"], w.get("Z", 0))
    return new_nodes[n]

def add_edge(a, b, audit=False):
    adj_plan.setdefault(a, set()).add(b)
    adj_plan.setdefault(b, set()).add(a)
    (plan["wp_edge_audit"] if audit else plan["wp_edge_add"]).append([a, b])

def add_node(region, x, y, z, connects):
    link_seq[region] += 1
    name = f"{region} Link WP {link_seq[region]}"
    while name in nodes or name in new_nodes:
        link_seq[region] += 1
        name = f"{region} Link WP {link_seq[region]}"
    new_nodes[name] = (x, y, z)
    plan["wp_add"].append({"Name": name, "X": x, "Y": y, "Z": z})
    adj_plan.setdefault(name, set())
    for c in connects:
        add_edge(name, c)
    return name

# ---------------------------------------------------------------- 1. same-flood reconnect
def pad_clear(x, y, mind=2):
    for dx in range(-mind + 1, mind):
        for dy in range(-mind + 1, mind):
            if (x + dx, y + dy) in pads_xy:
                return False
    return True

pads_xy = set()
for t in tel:
    sx, sy, _sz = t["src"]["loc"]
    pads_xy.add((sx, sy))
    if t.get("back"):
        dx, dy, _dz = t["dst"]["loc"]
        pads_xy.add((dx, dy))

comp0 = comps_of(adj_now)
node_flood = {}
for n, w in nodes.items():
    if region_target(w["X"], w["Y"]):
        node_flood[n] = flood_at(w["X"], w["Y"], r=2)

flood_groups = defaultdict(lambda: defaultdict(list))
for n, f in node_flood.items():
    if f:
        flood_groups[f][comp0[n]].append(n)

reconnect_fail = []
for f, groups in sorted(flood_groups.items()):
    if STAGE2:
        break
    if len(groups) < 2:
        continue
    ordered = sorted(groups.items(), key=lambda kv: -len(kv[1]))
    connected = set(ordered[0][1])
    pending = [list(g) for _c, g in ordered[1:]]
    for grp in pending:
        # nearest pair grp <-> connected
        best = None
        for a in grp:
            ax, ay, _az = node_pos(a)
            for b in connected:
                bx, by, _bz = node_pos(b)
                d = cheb((ax, ay), (bx, by))
                if best is None or d < best[0]:
                    best = (d, a, b)
        d, a, b = best
        ax, ay, az = node_pos(a)
        bx, by, bz = node_pos(b)
        if d <= MAX_EDGE and line_of_walk((ax, ay), (bx, by)):
            add_edge(a, b)
        else:
            sa = (ax, ay) if (ax, ay) in flood_tiles[f] else None
            sb = (bx, by) if (bx, by) in flood_tiles[f] else None
            # snap endpoints onto the flood if the node tile itself isn't in it
            def snapf(x, y):
                for rad in range(0, 4):
                    for dx in range(-rad, rad + 1):
                        for dy in range(-rad, rad + 1):
                            t = (x + dx, y + dy)
                            if t in flood_tiles[f]:
                                return t
                return None
            sa = sa or snapf(ax, ay)
            sb = sb or snapf(bx, by)
            path = astar_flood(f, sa, sb) if sa and sb else None
            if not path:
                reconnect_fail.append((f, a, b, d))
                continue
            pts = corridor_pts(path)
            if pts is None or len(pts) > 4:
                reconnect_fail.append((f, a, b, f"corridor unusable"))
                continue
            reg = region_target(ax, ay) or "Dungeon"
            prev = a
            for (px, py) in pts:
                prev = add_node(reg, px, py, az, [prev])
            add_edge(prev, b)
        connected.update(grp)

# ---------------------------------------------------------------- 2. pads + reachability
pads = defaultdict(list)
for t in tel:
    sx, sy, sz = t["src"]["loc"]
    dx, dy, dz = t["dst"]["loc"]
    pads[(sx, sy)].append({"z": sz, "dst": (dx, dy, dz)})
    if t.get("back"):
        pads[(dx, dy)].append({"z": dz, "dst": (sx, sy, sz)})

grid = defaultdict(list)
CELL = 32
def rebuild_grid():
    grid.clear()
    for n in list(nodes) + list(new_nodes):
        x, y, z = node_pos(n)
        grid[(x // CELL, y // CELL)].append((n, x, y, z))
rebuild_grid()

def eff(nx, ny, nz, x, y, z):
    dz = abs(nz - z)
    d = math.hypot(nx - x, ny - y)
    return d + (dz - 10) * 3 if dz > 10 else d

def rank1(x, y, z):
    best, bd = None, 1e18
    cx, cy = x // CELL, y // CELL
    for gx in range(cx - 3, cx + 4):
        for gy in range(cy - 3, cy + 4):
            for n, nx, ny, nz in grid.get((gx, gy), ()):
                d = eff(nx, ny, nz, x, y, z)
                if d < bd:
                    bd, best = d, n
    return best

comp_cur = None
def recompute():
    global comp_cur
    full = {k: set(v) for k, v in adj_plan.items()}
    comp = {}
    allnodes = list(nodes) + list(new_nodes)
    for n in allnodes:
        if n in comp:
            continue
        rep = n
        comp[n] = rep
        stack = [n]
        while stack:
            cur = stack.pop()
            for nb in full.get(cur, ()):
                if nb not in comp:
                    comp[nb] = rep
                    stack.append(nb)
    comp_cur = comp
recompute()

def label(x, y, z):
    ra = region_any(x, y)
    if ra is None:
        return "SURFACE" if x <= BRIT_MAX_X else "LOSTLAND"
    if ra not in TARGET:
        return "OTHERDUN"
    n = rank1(x, y, z)
    if n is None:
        return "~" + ra
    nx, ny, _nz = node_pos(n)
    if cheb((x, y), (nx, ny)) > MAX_ANCHOR:
        return "~" + ra
    return comp_cur[n]

def pad_edges_now():
    out = []
    for (px, py), lst in pads.items():
        sreg = region_any(px, py)
        if sreg is not None and sreg not in TARGET:
            continue  # bots never nav other-region interiors
        for p in lst:
            sl = label(px, py, p["z"])
            dl = label(*p["dst"])
            out.append({"src": (px, py, p["z"]), "dst": p["dst"], "sl": sl, "dl": dl})
    return out

def reach_depth(edges):
    ad = defaultdict(set)
    for e in edges:
        ad[e["sl"]].add(e["dl"])
    dep = {"SURFACE": 0}
    q = deque(["SURFACE"])
    while q:
        cur = q.popleft()
        for nx in ad[cur]:
            if nx not in dep and nx not in ("LOSTLAND", "OTHERDUN") \
               and not (isinstance(nx, str) and nx.startswith("~")):
                dep[nx] = dep[cur] + 1
                q.append(nx)
    return dep

# mesh inventory (comps holding interior records), post-reconnect
interior_types = {"DungeonRoom", "DungeonDescend", "DungeonAscend"}
def mesh_of_record(d):
    n = rank1(d["X"], d["Y"], d.get("Z", 0))
    if n is None:
        return None
    nx, ny, _nz = node_pos(n)
    if cheb((d["X"], d["Y"]), (nx, ny)) > MAX_ANCHOR:
        return None
    return comp_cur[n]

# 3. cross-flood bridges for unreached meshes
edges = pad_edges_now()
dep = reach_depth(edges)
mesh_region = {}
mesh_members = defaultdict(list)
for n in list(nodes) + list(new_nodes):
    x, y, z = node_pos(n)
    r = region_target(x, y)
    if r:
        mesh_members[comp_cur[n]].append(n)
        mesh_region.setdefault(comp_cur[n], Counter())[r] += 1

rec_mesh = {}
for d in dests:
    if d.get("Type") in interior_types:
        rec_mesh[d["Name"]] = mesh_of_record(d)

KHALDUN_OK_UNREACHED = True
for _pass in range(4 if not STAGE2 else 0):
    edges = pad_edges_now()
    dep = reach_depth(edges)
    unreached = set()
    for d in dests:
        if d.get("Type") not in interior_types:
            continue
        m = rec_mesh.get(d["Name"])
        if m is None or m in dep:
            continue
        reg = mesh_region.get(m)
        reg = reg.most_common(1)[0][0] if reg else None
        if reg == "Khaldun" and KHALDUN_OK_UNREACHED:
            continue
        unreached.add((m, reg))
    if not unreached:
        break
    progressed = False
    for m, reg in sorted(unreached, key=lambda t: str(t[0])):
        # nearest reached mesh node of the same region
        best = None
        for a in mesh_members[m]:
            ax, ay, _az = node_pos(a)
            for m2, mem in mesh_members.items():
                if m2 == m or m2 not in dep:
                    continue
                r2 = mesh_region.get(m2)
                if not r2 or r2.most_common(1)[0][0] != reg:
                    continue
                for b in mem:
                    bx, by, _bz = node_pos(b)
                    d2 = cheb((ax, ay), (bx, by))
                    if best is None or d2 < best[0]:
                        best = (d2, a, b)
        if best and best[0] <= BRIDGE_CAP:
            _d, a, b = best
            add_edge(a, b, audit=True)
            progressed = True
        else:
            plan["notes"].append(
                f"mesh {m} ({reg}) unreachable; nearest same-region reached pair "
                f"{best[0] if best else 'none'} tiles — left to rescue ladder")
    if not progressed:
        break
    recompute()
    rebuild_grid()
    # comp merge changes rec meshes
    rec_mesh = {d["Name"]: mesh_of_record(d)
                for d in dests if d.get("Type") in interior_types}
    mesh_members = defaultdict(list)
    mesh_region = {}
    for n in list(nodes) + list(new_nodes):
        x, y, z = node_pos(n)
        r = region_target(x, y)
        if r:
            mesh_members[comp_cur[n]].append(n)
            mesh_region.setdefault(comp_cur[n], Counter())[r] += 1

edges = pad_edges_now()
dep = reach_depth(edges)

# ---------------------------------------------------------------- 4. retype/retag
# canonical tag per mesh
import re
mesh_tag = {}
mesh_recs = defaultdict(list)
for d in dests:
    if d.get("Type") in interior_types:
        m = rec_mesh.get(d["Name"])
        if m:
            mesh_recs[m].append(d)

def wellformed(tag, reg):
    return re.fullmatch(re.escape(reg) + r" lvl\d+[a-z]?", tag or "") is not None

def lvlnum_of(t):
    mm = re.search(r"lvl(\d+)", t or "")
    return int(mm.group(1)) if mm else 99

taken_tags = {}
for m, rl in sorted(mesh_recs.items(),
                    key=lambda kv: (-len(kv[1]), -len(mesh_members.get(kv[0], [])))):
    regc = mesh_region.get(m)
    reg = regc.most_common(1)[0][0] if regc else "?"
    cnt = Counter(d.get("Dungeon", "") for d in rl)
    def lvlnum(t):
        mm = re.search(r"lvl(\d+)", t)
        return int(mm.group(1)) if mm else 99
    wf = [t for t in cnt if wellformed(t, reg) and lvlnum_of(t) >= 1]
    tag = None
    if wf:
        # a merged floor carries several strong tags; the LOWEST level number
        # among near-majority candidates wins (the entrance-side identity)
        top = max(cnt[t] for t in wf)
        strong = [t for t in wf if cnt[t] * 2 >= top]
        tag = sorted(strong, key=lambda t: (lvlnum(t), -cnt[t]))[0]
    if tag is None:
        # derive from depth (dense-ish): use pad depth if known else 1
        n = dep.get(m)
        tag = f"{reg} lvl{max(1, (n or 1))}"
    base = tag
    k = 0
    while taken_tags.get(tag) not in (None, m):
        k += 1
        letters = "bcdefghijklmnopqrstuvwxyz"
        tag = base + (letters[k - 1] if k <= len(letters) else f"x{k}")
    taken_tags[tag] = m
    mesh_tag[m] = tag

def tag_level(tag):
    mm = re.search(r"lvl(\d+)", tag)
    return int(mm.group(1)) if mm else 1

# per-record ops
name_taken = {d["Name"] for d in dests}
def uniq_name(base):
    if base not in name_taken:
        name_taken.add(base)
        return base
    for suf in [" B", " C", " D", " E", " F", " G", " H", " I", " J", " K"]:
        if base + suf not in name_taken:
            name_taken.add(base + suf)
            return base + suf
    i = 2
    while f"{base} {i}" in name_taken:
        i += 1
    name_taken.add(f"{base} {i}")
    return f"{base} {i}"

retype_count = Counter()
for d in dests:
    if d.get("Type") not in interior_types:
        continue
    m = rec_mesh.get(d["Name"])
    if m is None:
        plan["notes"].append(f"record '{d['Name']}' unanchored — left as-is")
        continue
    tag = mesh_tag[m]
    lvl = tag_level(tag)
    ops = {}
    if d.get("Dungeon", "") != tag or d.get("Level", 0) != lvl:
        ops["retag"] = {"name": d["Name"], "tag": tag, "level": lvl}
    if d["Type"] != "DungeonRoom":
        plist = pads.get((d["X"], d["Y"]))
        if plist:
            p = min(plist, key=lambda pp: abs(pp["z"] - d.get("Z", 0)))
            dl = label(*p["dst"])
            sd = dep.get(m)
            dd = dep.get(dl) if not isinstance(dl, str) or dl in dep else None
            if dl == "SURFACE":
                want = "DungeonAscend"
            elif dl in ("LOSTLAND", "OTHERDUN") or (isinstance(dl, str) and dl.startswith("~")):
                want = "DungeonDescend"
            elif sd is not None and dd is not None:
                want = ("DungeonAscend" if dd < sd else "DungeonDescend")
            elif sd is None and dd is not None:
                want = "DungeonAscend"    # from sealed pocket toward reached = up
            else:
                want = "DungeonDescend"
            if want != d["Type"]:
                ops["retype"] = {"name": d["Name"], "from": d["Type"], "to": want}
                retype_count[(d["Type"], want)] += 1
            # rename to match final type/tag
            verb = "Ascend" if want == "DungeonAscend" else "Descend"
            dst_part = ""
            if dl == "SURFACE":
                verb = "Exit Ascend"
            elif dl in mesh_tag:
                suffix = mesh_tag[dl].rsplit(" lvl", 1)[-1]
                dst_part = f" to L{suffix}"
            if "Passage" in d["Name"]:
                keep = d["Name"].split("Passage", 1)[1]
                newname = f"{tag} Passage{keep}".strip()
            else:
                newname = f"{tag} {verb}{dst_part}"
            if newname != d["Name"]:
                name_taken.discard(d["Name"])
                newname = uniq_name(newname)
                ops["rename"] = {"from": d["Name"], "to": newname}
    else:
        # room: keep name unless tag changed materially; normalize prefix
        if not d["Name"].lower().startswith(tag.lower()):
            base = f"{tag} Room"
            name_taken.discard(d["Name"])
            newname = uniq_name(base)
            ops["rename"] = {"from": d["Name"], "to": newname}
    if "retag" in ops:
        plan["dest_retag"].append(ops["retag"])
    if "retype" in ops:
        plan["dest_retype"].append(ops["retype"])
    if "rename" in ops:
        plan["dest_rename"].append(ops["rename"])
    # refresh a NearestWaypoint that no longer exists in the graph
    nw = d.get("NearestWaypoint", "")
    if nw and nw not in nodes and nw not in new_nodes:
        a = rank1(d["X"], d["Y"], d.get("Z", 0))
        if a:
            plan.setdefault("dest_setwp", []).append({"name": d["Name"], "wp": a})

# ---------------------------------------------------------------- 5. rooms
def nearest_pad_d(x, y, r=10):
    bd = r + 1
    for (px, py) in pads:
        d2 = cheb((x, y), (px, py))
        if d2 < bd:
            bd = d2
    return bd

for d in dests:
    if d.get("Type") != "DungeonRoom":
        continue
    x, y, z = d["X"], d["Y"], d.get("Z", 0)
    pd = nearest_pad_d(x, y, r=ROOM_MIN_PAD_DIST)
    if pd > ROOM_MIN_PAD_DIST - 1:
        continue
    f = flood_at(x, y, r=2)
    if not f:
        continue
    # spiral out for a tile in-flood with pad-dist in [5,10]
    found = None
    for rad in range(1, 9):
        for dx in range(-rad, rad + 1):
            for dy in range(-rad, rad + 1):
                if max(abs(dx), abs(dy)) != rad:
                    continue
                t = (x + dx, y + dy)
                if t not in flood_tiles.get(f, ()):
                    continue
                if ROOM_MIN_PAD_DIST <= nearest_pad_d(*t) and line_of_walk((x, y), t):
                    found = t
                    break
            if found:
                break
        if found:
            break
    if found:
        plan["dest_move"].append({"name": d["Name"], "from": [x, y, z],
                                  "to": [found[0], found[1], z]})
    else:
        plan["notes"].append(f"room '{d['Name']}' near pad but no safe tile found")

# top-ups: meshes with nodes>=25 and rooms < nodes//25 (cap +3)
mesh_rooms = Counter()
for d in dests:
    if d.get("Type") == "DungeonRoom":
        m = rec_mesh.get(d["Name"])
        if m:
            mesh_rooms[m] += 1
room_add_seq = defaultdict(int)
for m, mem in mesh_members.items():
    if m not in mesh_recs:
        continue
    want = max(2, min(len(mem) // 25 + 1, 6))
    have = mesh_rooms.get(m, 0)
    if have >= want:
        continue
    tag = mesh_tag.get(m)
    if not tag:
        continue
    picks = []
    cand = []
    for n in mem:
        x, y, z = node_pos(n)
        if nearest_pad_d(x, y, r=7) >= 6:
            cand.append((n, x, y, z))
    # farthest-point picks from existing rooms + picks
    anchors = [(d["X"], d["Y"]) for d in mesh_recs[m] if d.get("Type") == "DungeonRoom"]
    while len(picks) < want - have and cand:
        best = None
        for c in cand:
            dmin = min((cheb((c[1], c[2]), a) for a in anchors), default=999)
            if best is None or dmin > best[0]:
                best = (dmin, c)
        if best is None or best[0] < 15:
            break
        _dm, c = best
        picks.append(c)
        anchors.append((c[1], c[2]))
        cand.remove(c)
    for (_n, x, y, z) in picks:
        room_add_seq[tag] += 1
        nm = uniq_name(f"{tag} Room {room_add_seq[tag] + 20}")
        plan["dest_add"].append({
            "Name": nm, "X": x, "Y": y, "Z": z, "Type": "DungeonRoom",
            "City": "", "NearestWaypoint": _n, "Dungeon": tag, "level_num": tag_level(tag)})

# ---------------------------------------------------------------- 6. missing records
def add_trans(x, y, z, reg):
    plist = pads.get((x, y))
    p = min(plist, key=lambda pp: abs(pp["z"] - z))
    src_l = label(x, y, p["z"])
    dl = label(*p["dst"])
    if src_l not in mesh_tag:
        plan["notes"].append(f"missing-record pad ({x},{y}) src mesh untagged — skip")
        return
    tag = mesh_tag[src_l]
    sd, dd = dep.get(src_l), (dep.get(dl) if dl in dep else None)
    if dl == "SURFACE":
        typ, verb = "DungeonAscend", "Exit Ascend"
    elif dd is not None and sd is not None and dd < sd:
        typ, verb = "DungeonAscend", "Ascend"
    elif dd is not None and sd is not None and dd > sd:
        typ, verb = "DungeonDescend", "Descend"
    else:
        typ, verb = "DungeonDescend", "Descend"
    dst_tag = mesh_tag.get(dl)
    nm = uniq_name(f"{tag} {verb}" + (f" to L{tag_level(dst_tag)}" if dst_tag else ""))
    anchor_node = rank1(x, y, z)
    plan["dest_add"].append({
        "Name": nm, "X": x, "Y": y, "Z": p["z"], "Type": typ, "City": "",
        "NearestWaypoint": anchor_node or "", "Dungeon": tag,
        "level_num": tag_level(tag)})

add_trans(5867, 528, 15, "Wrong")      # Wrong L1 -> L2 second stair
add_trans(5329, 1381, 10, "Orc Cave")  # Orc Cave L2 -> L1 up-teleporter

# ---------------------------------------------------------------- 7. Wrong Approach 28<->29
wa28, wa29 = nodes.get("Wrong Approach 28"), nodes.get("Wrong Approach 29")
if wa28 and wa29 and not STAGE2:
    a = (wa28["X"], wa28["Y"])
    b = (wa29["X"], wa29["Y"])
    if line_of_walk(a, b):
        plan["notes"].append("Wrong Approach 28<->29 line is atlas-walkable now (audit was live-engine) — leaving, re-audit in game")
    else:
        # local flood outside dungeon rects
        box = (min(a[0], b[0]) - 40, min(a[1], b[1]) - 40,
               max(a[0], b[0]) + 40, max(a[1], b[1]) + 40)
        tiles = set()
        for yy in range(box[1], box[3] + 1):
            for xx in range(box[0], box[2] + 1):
                if walk(xx, yy):
                    tiles.add((xx, yy))
        def astar_local(s, g):
            if s not in tiles or g not in tiles:
                return None
            openq = [(cheb(s, g), 0, s, None)]
            came = {}
            gb = {s: 0}
            while openq:
                _f, g2, cur, par = heapq.heappop(openq)
                if cur in came:
                    continue
                came[cur] = par
                if cur == g:
                    path = [cur]
                    while came[path[-1]] is not None:
                        path.append(came[path[-1]])
                    return path[::-1]
                for dx in (-1, 0, 1):
                    for dy in (-1, 0, 1):
                        if dx == 0 and dy == 0:
                            continue
                        n2 = (cur[0] + dx, cur[1] + dy)
                        if n2 not in tiles or n2 in came:
                            continue
                        ng = g2 + 1
                        if ng < gb.get(n2, 1 << 30):
                            gb[n2] = ng
                            heapq.heappush(openq, (ng + cheb(n2, g), ng, n2, cur))
            return None
        path = astar_local(a, b)
        if path:
            pts = simplify(path)[1:-1]
            if len(pts) <= 3:
                prev = "Wrong Approach 28"
                for (px, py) in pts:
                    prev = add_node("Wrong", px, py, wa28.get("Z", 0), [prev])
                add_edge(prev, "Wrong Approach 29")
                plan["wp_edge_drop"].append(["Wrong Approach 28", "Wrong Approach 29"])
            else:
                plan["notes"].append(f"Wrong Approach 28<->29: corridor needs {len(pts)} nodes — flag for manual")
        else:
            plan["notes"].append("Wrong Approach 28<->29: no atlas path in local box — flag for manual")

# ---------------------------------------------------------------- summary
print(f"plan: +{len(plan['wp_add'])} waypoints, +{len(plan['wp_edge_add'])} edges, "
      f"+{len(plan['wp_edge_audit'])} audit-pending bridges, "
      f"-{len(plan['wp_edge_drop'])} dropped edges")
print(f"      {len(plan['dest_retype'])} retypes, {len(plan['dest_retag'])} retags, "
      f"{len(plan['dest_rename'])} renames, {len(plan['dest_move'])} room moves, "
      f"+{len(plan['dest_add'])} new records")
print()
print("retypes by direction:", dict(retype_count))
print()
print("mesh tags (post-plan):")
for m, tag in sorted(mesh_tag.items(), key=lambda kv: kv[1]):
    print(f"  {tag:22s} mesh={str(m):24s} depth={dep.get(m)} nodes={len(mesh_members.get(m, []))} recs={len(mesh_recs.get(m, []))}")
print()
print("audit-pending bridges:")
for a, b in plan["wp_edge_audit"]:
    pa_, pb_ = node_pos(a), node_pos(b)
    print(f"  {a} ({pa_[0]},{pa_[1]}) <-> {b} ({pb_[0]},{pb_[1]})  d={cheb(pa_[:2], pb_[:2])}")
print()
print("notes:")
for n in plan["notes"]:
    print("  -", n)

json.dump(plan, open(PLAN, "w", encoding="utf-8"), indent=1, default=str)
print("\nwrote", PLAN)
