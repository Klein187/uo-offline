#!/usr/bin/env python3
"""Author interior waypoint meshes + teleporter destinations for ALL dungeons.

Global single pass over every target DungeonRegion: reads teleporters.json
directly (src->dst plus implicit back-pads), flood-fills each region's floor
components from the walk atlas, meshes them, and classifies every pad record
against COMPUTED floor levels — so cross-dungeon passages (Deceit<->Destard,
Despise<->Terathan-side caves, dungeon<->Lost Lands) come out right instead
of being lost to per-dungeon filtering.

Record classification (walk side -> landing side):
  Britannia surface -> region        DungeonEntrance "<R> L<n> Entrance"
  region -> Britannia surface        "<R> lvl<n> Exit Ascend"
  region -> deeper same region       "<R> lvl<n> Descend to L<m>"
  region -> shallower same region    "<R> lvl<n> Ascend to L<m>"
  region -> same level same region   "<R> lvl<n> Shortcut"
  region -> other region             "<R> lvl<n> Passage to <R2>"
  region -> Lost Lands surface       "<R> lvl<n> Exit Ascend" (LL side unmeshed)
  Lost Lands surface -> region       skipped (no nav coverage out there yet)

Floors already carrying >= 5 authored waypoints (Despise L1, vestibule) are
left untouched; records whose arrival tile is already authored are skipped.

Usage:
  python gen_all_dungeon_interiors.py [--merge] [--rooms-per-floor 5]
"""

import argparse
import heapq
import json
import os
from collections import deque, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
ATLAS = os.path.join(HERE, "map", "walk_atlas.pgm")
DATA = os.path.expanduser(r"~\uo-modernuo\ModernUO\Distribution\Data")

TARGET_REGIONS = [
    "Covetous", "Deceit", "Despise", "Destard", "Hythloth",
    "Shame", "Wrong", "Fire", "Ice", "Orc Cave", "Terathan Keep", "Khaldun",
]

MIN_NODE_SPACING = 13
MAX_EDGE = 26
MAX_NODES_PER_FLOOR = 70
DOOR_GAP = 5
COMP_CAP = 250_000
MIN_COMP = 40
BRITANNIA_MAX_X = 5119


# --------------------------------------------------------------------------
def load_atlas():
    data = open(ATLAS, "rb").read()
    parts = data.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    return w, h, parts[3]


W, H, A = load_atlas()


def walk(x, y):
    return 0 <= x < W and 0 <= y < H and A[y * W + x] > 127


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def cheb(a, b):
    return max(abs(a[0] - b[0]), abs(a[1] - b[1]))


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


def snap(t, r=6):
    t = (t[0], t[1])
    if walk(*t):
        return t
    for dd in range(1, r + 1):
        for dx in range(-dd, dd + 1):
            for dy in (-dd, dd):
                for c in ((t[0] + dx, t[1] + dy), (t[0] + dy, t[1] + dx)):
                    if walk(*c):
                        return c
    return None


def astar(comp, start, goal):
    if start not in comp or goal not in comp:
        return None
    openq = [(cheb(start, goal), 0, start, None)]
    came = {}
    gbest = {start: 0}
    while openq:
        f, g, cur, parent = heapq.heappop(openq)
        if cur in came:
            continue
        came[cur] = parent
        if cur == goal:
            path = [cur]
            while came[path[-1]] is not None:
                path.append(came[path[-1]])
            return path[::-1]
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                if dx == 0 and dy == 0:
                    continue
                n = (cur[0] + dx, cur[1] + dy)
                if n not in comp or n in came:
                    continue
                ng = g + 1
                if ng < gbest.get(n, 1 << 30):
                    gbest[n] = ng
                    heapq.heappush(openq, (ng + cheb(n, goal), ng, n, cur))
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


# --------------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--merge", action="store_true")
    ap.add_argument("--rooms-per-floor", type=int, default=5)
    ap.add_argument("--only", nargs="*", default=None,
                    help="restrict to these region names (mesh+records)")
    args = ap.parse_args()

    regions = load(os.path.join(DATA, "regions.json"))
    teles = load(os.path.join(DATA, "teleporters.json"))

    region_area = {}
    for r in regions:
        if (r.get("$type") == "DungeonRegion" and r.get("Map") == "Felucca"
                and r["Name"] in TARGET_REGIONS):
            region_area[r["Name"]] = [
                (a["x1"], a["y1"], a["x2"], a["y2"]) for a in r["Area"]]

    def region_of(x, y):
        for name, areas in region_area.items():
            if any(x1 <= x <= x2 and y1 <= y <= y2 for x1, y1, x2, y2 in areas):
                return name
        return None

    # ---- directed pad edges touching any target region ----
    edges = []
    for t in teles:
        if t["src"]["map"] != "Felucca" or t["dst"]["map"] != "Felucca":
            continue
        s, d = tuple(t["src"]["loc"]), tuple(t["dst"]["loc"])
        if region_of(s[0], s[1]) or region_of(d[0], d[1]):
            edges.append((s, d))
            if t.get("back"):
                edges.append((d, s))

    # group adjacent pad tiles into logical pads
    tiles = sorted({e[0] for e in edges})
    pad_groups = []
    for t in tiles:
        placed = False
        for g in pad_groups:
            if any(cheb(t, u) <= 2 for u in g):
                g.append(t)
                placed = True
                break
        if not placed:
            pad_groups.append([t])
    tile2pad = {}
    pads = []
    for g in pad_groups:
        g.sort()
        center = g[len(g) // 2]
        pads.append((center, g))
        for t in g:
            tile2pad[t] = center
    pad_lands = defaultdict(set)
    for s, d in edges:
        pad_lands[tile2pad[s]].add(d)

    # ---- floor components per region ----
    seen = set()
    comps = []           # list of (region, set(tiles))
    def flood(seed, areas):
        comp = set([seed])
        q = deque([seed])
        while q and len(comp) < COMP_CAP:
            x, y = q.popleft()
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    if dx == 0 and dy == 0:
                        continue
                    n = (x + dx, y + dy)
                    if n in comp or n in seen:
                        continue
                    if not walk(*n):
                        continue
                    if not any(x1 <= n[0] <= x2 and y1 <= n[1] <= y2
                               for x1, y1, x2, y2 in areas):
                        continue
                    comp.add(n)
                    q.append(n)
        seen.update(comp)
        return comp

    seeds = []
    for center, g in pads:
        seeds.extend([(t[0], t[1]) for t in g])
    for lands in pad_lands.values():
        seeds.extend([(l[0], l[1]) for l in lands])
    for sd in seeds:
        reg = region_of(sd[0], sd[1])
        if reg is None:
            continue
        st = snap(sd)
        if st is None or st in seen or region_of(*st) != reg:
            continue
        comp = flood(st, region_area[reg])
        if len(comp) >= MIN_COMP:
            comps.append((reg, comp))

    comp_cache = {}
    def comp_of(t):
        st = snap(t)
        if st is None:
            return None
        if st in comp_cache:
            return comp_cache[st]
        for i, (_, c) in enumerate(comps):
            if st in c:
                comp_cache[st] = i
                return i
        comp_cache[st] = None
        return None

    # ---- door adjacency within each region -> clusters + bridges ----
    bridges = {}
    for i in range(len(comps)):
        for j in range(i + 1, len(comps)):
            if comps[i][0] != comps[j][0]:
                continue
            bi, bj = comps[i][1], comps[j][1]
            minxi = min(t[0] for t in bi); maxxi = max(t[0] for t in bi)
            minyi = min(t[1] for t in bi); maxyi = max(t[1] for t in bi)
            minxj = min(t[0] for t in bj); maxxj = max(t[0] for t in bj)
            minyj = min(t[1] for t in bj); maxyj = max(t[1] for t in bj)
            if (minxj - maxxi > DOOR_GAP or minxi - maxxj > DOOR_GAP or
                    minyj - maxyi > DOOR_GAP or minyi - maxyj > DOOR_GAP):
                continue
            small, big = (bi, bj) if len(bi) <= len(bj) else (bj, bi)
            best = None
            for t in small:
                for dx in range(-DOOR_GAP, DOOR_GAP + 1):
                    for dy in range(-DOOR_GAP, DOOR_GAP + 1):
                        u = (t[0] + dx, t[1] + dy)
                        if u in big:
                            dd = max(abs(dx), abs(dy))
                            if best is None or dd < best[0]:
                                best = (dd, t, u)
            if best:
                _, ta, tb = best
                if small is bi:
                    bridges[(i, j)] = (ta, tb)
                else:
                    bridges[(i, j)] = (tb, ta)

    cparent = list(range(len(comps)))
    def cfind(i):
        while cparent[i] != i:
            cparent[i] = cparent[cparent[i]]
            i = cparent[i]
        return i
    for (i, j) in bridges:
        cparent[cfind(i)] = cfind(j)

    # ---- level numbering per region over clusters ----
    # entries: pads whose walk side is OUTSIDE the landing's region
    clevel = {}
    q = deque()
    for center, g in pads:
        wreg = region_of(center[0], center[1])
        for land in pad_lands[center]:
            lreg = region_of(land[0], land[1])
            if lreg is None:
                continue
            ci = comp_of((land[0], land[1]))
            if ci is None:
                continue
            if wreg != lreg:  # entered from surface or another dungeon
                key = (lreg, cfind(ci))
                if key not in clevel:
                    # Britannia-side entrances define L1; other cross links
                    # get numbered too but Britannia wins ties by ordering
                    clevel[key] = 1
                    q.append(key)
    while q:
        reg, cl = q.popleft()
        for center, g in pads:
            wci = comp_of(center)
            if wci is None or comps[wci][0] != reg or cfind(wci) != cl:
                continue
            for land in pad_lands[center]:
                lci = comp_of((land[0], land[1]))
                if lci is None or comps[lci][0] != reg:
                    continue
                key = (reg, cfind(lci))
                if key not in clevel:
                    clevel[key] = clevel[(reg, cl)] + 1
                    q.append(key)
    # leftovers per region
    per_region_next = defaultdict(lambda: 1)
    for key, lv in clevel.items():
        per_region_next[key[0]] = max(per_region_next[key[0]], lv + 1)
    for i in range(len(comps)):
        key = (comps[i][0], cfind(i))
        if key not in clevel:
            clevel[key] = per_region_next[comps[i][0]]
            per_region_next[comps[i][0]] += 1

    def level_of(ci):
        return clevel[(comps[ci][0], cfind(ci))]

    # ---- load live data for dedupe ----
    wp_path = os.path.join(DATA, "Waypoints", "waypoints.json")
    dest_path = os.path.join(DATA, "Destinations", "destinations.json")
    wdoc = load(wp_path)
    ddoc = load(dest_path)
    existing_wp_tiles = {(n["X"], n["Y"]) for n in wdoc["Waypoints"]}
    taken_wp = {n["Name"] for n in wdoc["Waypoints"]}
    taken_dest = {r["Name"] for r in ddoc["Destinations"]}
    existing_arrivals = set()
    for r in ddoc["Destinations"]:
        for a in r.get("Arrivals", []):
            existing_arrivals.add((a["X"], a["Y"]))
        if "ArrivalX" in r and r.get("ArrivalX") is not None:
            existing_arrivals.add((r["ArrivalX"], r["ArrivalY"]))

    def uniquify(name, taken):
        if name not in taken:
            return name
        for suf in "BCDEFGH":
            if f"{name} {suf}" not in taken:
                return f"{name} {suf}"
        i = 2
        while f"{name} #{i}" in taken:
            i += 1
        return f"{name} #{i}"

    only = set(args.only) if args.only else None

    # ---- mesh each component ----
    pad_tiles_all = set(tile2pad.keys())
    all_nodes = []
    node_name_at = {}

    def nearest_beside(comp, tile):
        best, bestd = None, 99
        for dx in range(-4, 5):
            for dy in range(-4, 5):
                t = (tile[0] + dx, tile[1] + dy)
                if t in comp and t not in pad_tiles_all:
                    d = max(abs(dx), abs(dy))
                    if 1 <= d < bestd:
                        bestd = d
                        best = t
        return best or tile

    skipped_floor = set()
    for ci, (reg, comp) in enumerate(comps):
        if only and reg not in only:
            skipped_floor.add(ci)
            continue
        lv = level_of(ci)
        already = sum(1 for t in existing_wp_tiles if t in comp)
        if already >= 5:
            print(f"{reg} L{lv} (comp {ci}, {len(comp)}t): {already} existing nodes — skip mesh")
            skipped_floor.add(ci)
            continue

        seeds_here = []
        for center, g in pads:
            wct = comp_of(center)
            if wct == ci:
                seeds_here.append(nearest_beside(comp, snap(center) or center))
            for land in pad_lands[center]:
                lct = comp_of((land[0], land[1]))
                if lct == ci:
                    seeds_here.append(nearest_beside(comp, snap((land[0], land[1]))))
        seeds_here = sorted(set(s for s in seeds_here if s in comp))

        # farthest-point sampling
        nodes = list(seeds_here)
        pool = [t for t in comp if (t[0] + t[1]) % 2 == 0]
        if not nodes and pool:
            nodes.append(sorted(pool)[len(pool) // 2])
        while len(nodes) < MAX_NODES_PER_FLOOR:
            best, bestd = None, MIN_NODE_SPACING
            for t in pool:
                d = min(cheb(t, n) for n in nodes)
                if d > bestd:
                    bestd = d
                    best = t
            if best is None:
                break
            nodes.append(best)

        edges_set = set()
        for i in range(len(nodes)):
            for j in range(i + 1, len(nodes)):
                if cheb(nodes[i], nodes[j]) <= MAX_EDGE and \
                        line_of_walk(nodes[i], nodes[j]):
                    edges_set.add((i, j))

        # connectivity repair
        parent = list(range(len(nodes)))
        def find(i):
            while parent[i] != i:
                parent[i] = parent[parent[i]]
                i = parent[i]
            return i
        def union(i, j):
            parent[find(i)] = find(j)
        for i, j in edges_set:
            union(i, j)
        def clusters():
            c = {}
            for i in range(len(nodes)):
                c.setdefault(find(i), []).append(i)
            return sorted(c.values(), key=len, reverse=True)
        guard = 0
        while len(clusters()) > 1 and guard < 40:
            guard += 1
            cl = clusters()
            main, other = cl[0], cl[1]
            best = None
            for i in main:
                for j in other:
                    d = cheb(nodes[i], nodes[j])
                    if best is None or d < best[0]:
                        best = (d, i, j)
            _, i, j = best
            path = astar(comp, nodes[i], nodes[j])
            if path is None:
                keep = [k for k in range(len(nodes)) if k not in set(other)]
                remap = {k: n for n, k in enumerate(keep)}
                nodes = [nodes[k] for k in keep]
                edges_set = {(remap[a], remap[b]) for a, b in edges_set
                             if a in remap and b in remap}
                parent = list(range(len(nodes)))
                for a, b in edges_set:
                    union(a, b)
                continue
            mids = simplify(path)[1:-1]
            base = len(nodes)
            prev = i
            for k, m in enumerate(mids):
                nodes.append(m)
                parent.append(len(parent))
                edges_set.add((min(prev, base + k), max(prev, base + k)))
                prev = base + k
            edges_set.add((min(prev, j), max(prev, j)))
            parent = list(range(len(nodes)))
            for a, b in edges_set:
                union(a, b)

        conns = {i: [] for i in range(len(nodes))}
        for i, j in edges_set:
            conns[i].append(j)
            conns[j].append(i)
        names = []
        for i, t in enumerate(nodes):
            nm = uniquify(f"{reg} L{lv} WP {i + 1}", taken_wp)
            taken_wp.add(nm)
            names.append(nm)
            node_name_at[t] = nm
        for i, t in enumerate(nodes):
            all_nodes.append({
                "Name": names[i], "X": t[0], "Y": t[1], "Z": 0,
                "Connects": [names[j] for j in sorted(conns[i])],
            })
        print(f"{reg} L{lv} (comp {ci}, {len(comp)}t): {len(nodes)} nodes, {len(edges_set)} edges")

    # ---- door bridges across meshes ----
    node_index = {n["Name"]: n for n in all_nodes}
    for (i, j), (ti, tj) in bridges.items():
        if i in skipped_floor or j in skipped_floor:
            continue
        def near_node(tile, ci):
            best, bestd = None, 8
            for t, nm in node_name_at.items():
                if t in comps[ci][1]:
                    d = cheb(t, tile)
                    if d < bestd:
                        bestd = d
                        best = nm
            if best:
                return best
            nm = uniquify(f"{comps[ci][0]} L{level_of(ci)} Door WP", taken_wp)
            taken_wp.add(nm)
            all_nodes.append({"Name": nm, "X": tile[0], "Y": tile[1], "Z": 0,
                              "Connects": []})
            node_name_at[tile] = nm
            node_index[nm] = all_nodes[-1]
            return nm
        na = near_node(ti, i)
        nb = near_node(tj, j)
        if nb not in node_index[na]["Connects"]:
            node_index[na]["Connects"].append(nb)
        if na not in node_index[nb]["Connects"]:
            node_index[nb]["Connects"].append(na)

    # ---- room destinations ----
    dest_out = []
    from collections import Counter
    for ci, (reg, comp) in enumerate(comps):
        if ci in skipped_floor:
            continue
        lv = level_of(ci)
        floor_nodes = [(t, nm) for t, nm in node_name_at.items() if t in comp]
        if not floor_nodes:
            continue
        want = min(args.rooms_per_floor, max(1, len(floor_nodes) // 6))
        rooms = [floor_nodes[0]]
        while len(rooms) < want:
            best, bestd = None, -1
            for t, nm in floor_nodes:
                if any(nm == r[1] for r in rooms):
                    continue
                d = min(cheb(t, r[0]) for r in rooms)
                if d > bestd:
                    bestd = d
                    best = (t, nm)
            if best is None:
                break
            rooms.append(best)
        for k, (t, nm) in enumerate(rooms):
            name = uniquify(f"{reg} lvl{lv} Room {k + 1}", taken_dest)
            taken_dest.add(name)
            dest_out.append({
                "Name": name, "X": t[0], "Y": t[1], "Z": 0,
                "Type": "DungeonRoom", "City": "",
                "NearestWaypoint": nm,
                "Dungeon": f"{reg} lvl{lv}", "Level": lv,
            })

    # ---- teleporter destination records ----
    def nearest_node_name(t, prefer_new=True):
        best, bestd = "", 1 << 30
        for tile, nm in node_name_at.items():
            d = cheb(tile, t)
            if d < bestd:
                bestd = d
                best = nm
        if bestd > 30:
            for n in wdoc["Waypoints"]:
                d = cheb((n["X"], n["Y"]), t)
                if d < bestd:
                    bestd = d
                    best = n["Name"]
        return best

    counts = Counter()
    for center, g in pads:
        wreg = region_of(center[0], center[1])
        landings = sorted(pad_lands[center])
        if not landings:
            continue
        land = landings[0]
        lreg = region_of(land[0], land[1])
        if only and (wreg or lreg) and not ({wreg, lreg} & only):
            continue

        # arrival = ON the pad center; dedupe vs authored data
        if any((t[0], t[1]) in existing_arrivals for t in g):
            counts["skip_authored"] += 1
            continue

        wci = comp_of(center)
        lci = comp_of((land[0], land[1]))
        lv = level_of(wci) if wci is not None and wreg else 0
        tlv = level_of(lci) if lci is not None and lreg else 0

        if wreg is None:
            # surface-side pad: Britannia entrance only
            if center[0] > BRITANNIA_MAX_X or lreg is None:
                counts["skip_lostlands_side"] += 1
                continue
            base = f"{lreg} L{tlv or 1} Entrance"
            rtype, dtag, rl = "DungeonEntrance", lreg, 0
        elif lreg is None:
            base = f"{wreg} lvl{lv} Exit Ascend"
            rtype, dtag, rl = "DungeonAscend", f"{wreg} lvl{lv}", lv
        elif lreg != wreg:
            base = f"{wreg} lvl{lv} Passage to {lreg}"
            rtype, dtag, rl = "DungeonAscend", f"{wreg} lvl{lv}", lv
        elif tlv > lv:
            base = f"{wreg} lvl{lv} Descend to L{tlv}"
            rtype, dtag, rl = "DungeonDescend", f"{wreg} lvl{lv}", lv
        elif tlv < lv:
            base = f"{wreg} lvl{lv} Ascend to L{tlv}"
            rtype, dtag, rl = "DungeonAscend", f"{wreg} lvl{lv}", lv
        else:
            base = f"{wreg} lvl{lv} Shortcut"
            rtype, dtag, rl = "DungeonDescend", f"{wreg} lvl{lv}", lv

        name = uniquify(base, taken_dest)
        taken_dest.add(name)

        if rtype == "DungeonEntrance":
            # Location = safe tile 2 off the pad; arrival ON the pad
            safe = None
            for dd in range(2, 5):
                for dx in range(-dd, dd + 1):
                    for dy in (-dd, dd):
                        c = (center[0] + dx, center[1] + dy)
                        if walk(*c) and c not in tile2pad:
                            safe = c
                            break
                    if safe:
                        break
                if safe:
                    break
            loc = safe or center
        else:
            loc = center

        rec = {
            "Name": name,
            "X": loc[0], "Y": loc[1], "Z": center[2] if len(center) > 2 else 0,
            "Type": rtype, "City": "",
            "NearestWaypoint": nearest_node_name(
                (center[0], center[1]) if rtype != "DungeonEntrance" else loc),
            "Dungeon": dtag, "Level": rl,
            "ArrivalX": center[0], "ArrivalY": center[1],
            "ArrivalZ": center[2] if len(center) > 2 else 0,
            "Arrivals": [{
                "X": center[0], "Y": center[1],
                "Z": center[2] if len(center) > 2 else 0,
                "Waypoints": [nearest_node_name((center[0], center[1]))],
            }],
            "TargetX": land[0], "TargetY": land[1],
            "TargetZ": land[2] if len(land) > 2 else 0,
            "TargetLevel": tlv,
        }
        dest_out.append(rec)
        counts[rtype] += 1

    print(f"\nTOTAL: {len(all_nodes)} waypoints, {len(dest_out)} destinations")
    print(dict(counts))

    if not args.merge:
        out = os.path.join(HERE, "mesh-all-dungeons.json")
        json.dump({"Waypoints": all_nodes, "Destinations": dest_out},
                  open(out, "w"), indent=1)
        print(f"wrote {out} (rerun with --merge to apply)")
        return

    import shutil
    shutil.copy(wp_path, wp_path + ".bak-dungeonmesh")
    shutil.copy(dest_path, dest_path + ".bak-dungeonmesh")
    wdoc["Waypoints"].extend(all_nodes)
    ddoc["Destinations"].extend(dest_out)
    json.dump(wdoc, open(wp_path, "w"), indent=2)
    json.dump(ddoc, open(dest_path, "w"), indent=2)
    print("merged into live data (backups .bak-dungeonmesh)")


if __name__ == "__main__":
    main()
