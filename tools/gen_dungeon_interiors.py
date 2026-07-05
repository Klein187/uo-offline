#!/usr/bin/env python3
"""Generate interior waypoint meshes + room/teleporter destinations for a dungeon.

Builds on gen_dungeon_skeleton.py (teleporter topology) and the walk atlas
(engine-truth walkability, dungeon space included). Each dungeon FLOOR is a
walk-connected component of the atlas inside the dungeon's region bounds —
that matches how the crawler scopes itself (graph reachability IS the floor
test), so components, not BFS guesses, define levels here.

Per floor component:
  * farthest-point-sample a node set (min spacing, capped), plus a node
    beside every teleporter pad on that floor;
  * connect nodes <= MAX_EDGE apart that have a clear line-of-walk;
  * bridge disconnected clusters with atlas A* splice nodes;
  * pick the most-spread nodes as DungeonRoom destinations.

Destination records come from the skeleton, re-leveled by component and
de-duplicated by name (A/B/C suffixes). Surface entrances outside Britannia
(Lost Lands / cross-dungeon walk sides) are skipped; ascends that LAND
outside the dungeon are kept (walk-on pads; the far side resolves itself).

Usage:
  python gen_dungeon_interiors.py Deceit [--merge] [--rooms-per-floor 6]
  --merge writes into the live waypoints.json + destinations.json (backups
  .bak-dungeonmesh) — otherwise emits mesh-<dungeon>.json for review.

After merge: bump Data/Live/reload_request.txt, then audit_request.txt.
"""

import argparse
import heapq
import json
import os
import re
import sys
from collections import deque

HERE = os.path.dirname(os.path.abspath(__file__))
ATLAS = os.path.join(HERE, "map", "walk_atlas.pgm")
DATA = os.path.expanduser(r"~\uo-modernuo\ModernUO\Distribution\Data")

MIN_NODE_SPACING = 13
MAX_EDGE = 26
MAX_NODES_PER_FLOOR = 80
COMP_CAP = 250_000
BRITANNIA_MAX_X = 5119  # surface entrances we keep have walk side here


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


def region_areas(dungeon):
    regs = load(os.path.join(DATA, "regions.json"))
    for r in regs:
        if (r.get("$type") == "DungeonRegion" and r.get("Map") == "Felucca"
                and r.get("Name", "").lower() == dungeon.lower()):
            return [(a["x1"], a["y1"], a["x2"], a["y2"]) for a in r["Area"]]
    raise SystemExit(f"no Felucca DungeonRegion named {dungeon!r}")


def in_bounds(areas, x, y):
    return any(x1 <= x <= x2 and y1 <= y <= y2 for x1, y1, x2, y2 in areas)


def flood(seed, areas, seen_global):
    """Flood-fill walkable tiles inside the region bounds from seed."""
    if not walk(*seed) or seed in seen_global:
        return None
    comp = set()
    q = deque([seed])
    comp.add(seed)
    while q and len(comp) < COMP_CAP:
        x, y = q.popleft()
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                if dx == 0 and dy == 0:
                    continue
                n = (x + dx, y + dy)
                if n in comp or n in seen_global:
                    continue
                if not walk(*n) or not in_bounds(areas, *n):
                    continue
                comp.add(n)
                q.append(n)
    seen_global.update(comp)
    return comp


def cheb(a, b):
    return max(abs(a[0] - b[0]), abs(a[1] - b[1]))


def line_of_walk(a, b):
    """Every tile on the straight segment a->b is walkable."""
    x0, y0 = a
    x1, y1 = b
    n = max(abs(x1 - x0), abs(y1 - y0))
    if n == 0:
        return True
    for i in range(n + 1):
        x = round(x0 + (x1 - x0) * i / n)
        y = round(y0 + (y1 - y0) * i / n)
        if not walk(x, y):
            return False
    return True


def astar(comp, start, goal):
    """A* constrained to the component. Returns tile path or None."""
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
    """Line-of-walk simplification down to <= MAX_EDGE spaced tiles."""
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


def sample_nodes(comp, seeds):
    """Farthest-point sampling; seeds (pad-side nodes) always included."""
    nodes = [s for s in seeds if s in comp]
    # candidate pool: stride the component for speed
    pool = [t for t in comp if (t[0] + t[1]) % 2 == 0]
    if not nodes:
        pool_sorted = sorted(pool)
        if not pool_sorted:
            return nodes
        nodes.append(pool_sorted[len(pool_sorted) // 2])
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
    return nodes


def build_mesh(comp, seeds):
    """Nodes + edges for one floor. Returns (nodes, edges set of index pairs)."""
    nodes = sample_nodes(comp, seeds)
    edges = set()
    for i in range(len(nodes)):
        for j in range(i + 1, len(nodes)):
            if cheb(nodes[i], nodes[j]) <= MAX_EDGE and line_of_walk(nodes[i], nodes[j]):
                edges.add((i, j))

    # cluster repair: union-find, then A*-splice nearest cluster pairs
    parent = list(range(len(nodes)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    def union(i, j):
        parent[find(i)] = find(j)

    for i, j in edges:
        union(i, j)

    def clusters():
        c = {}
        for i in range(len(nodes)):
            c.setdefault(find(i), []).append(i)
        return list(c.values())

    guard = 0
    while len(clusters()) > 1 and guard < 40:
        guard += 1
        cl = clusters()
        cl.sort(key=len, reverse=True)
        main, rest = cl[0], cl[1:]
        # nearest (main, other) node pair
        best = None
        for other in rest:
            for i in main:
                for j in other:
                    d = cheb(nodes[i], nodes[j])
                    if best is None or d < best[0]:
                        best = (d, i, j)
        d, i, j = best
        path = astar(comp, nodes[i], nodes[j])
        if path is None:
            # genuinely unreachable inside the component?? drop the smaller
            # cluster's nodes entirely (keeps pads only if main-side)
            drop = set()
            for other in rest:
                if j in other:
                    drop = set(other)
                    break
            keep = [k for k in range(len(nodes)) if k not in drop]
            remap = {k: n for n, k in enumerate(keep)}
            nodes = [nodes[k] for k in keep]
            edges = {(remap[a], remap[b]) for a, b in edges
                     if a in remap and b in remap}
            parent = list(range(len(nodes)))
            for a, b in edges:
                union(a, b)
            continue
        mids = simplify(path)[1:-1]
        base = len(nodes)
        prev = i
        for k, m in enumerate(mids):
            nodes.append(m)
            edges.add((min(prev, base + k), max(prev, base + k)))
            prev = base + k
        edges.add((min(prev, j), max(prev, j)))
        parent = list(range(len(nodes)))
        for a, b in edges:
            union(a, b)
    return nodes, edges


def pick_rooms(nodes, seeds, count):
    """Most-spread non-pad nodes as room anchors."""
    cand = [i for i, n in enumerate(nodes) if n not in seeds]
    if not cand:
        cand = list(range(len(nodes)))
    rooms = [cand[0]]
    while len(rooms) < min(count, len(cand)):
        best, bestd = None, -1
        for i in cand:
            if i in rooms:
                continue
            d = min(cheb(nodes[i], nodes[r]) for r in rooms)
            if d > bestd:
                bestd = d
                best = i
        if best is None:
            break
        rooms.append(best)
    return rooms


def nearest_walkable_beside(comp, tile, exclude):
    """A walkable component tile within 4 of `tile`, not in exclude."""
    best, bestd = None, 99
    tx, ty = tile
    for dx in range(-4, 5):
        for dy in range(-4, 5):
            t = (tx + dx, ty + dy)
            if t in comp and t not in exclude:
                d = max(abs(dx), abs(dy))
                if 1 <= d < bestd:
                    bestd = d
                    best = t
    return best


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dungeon")
    ap.add_argument("--merge", action="store_true")
    ap.add_argument("--rooms-per-floor", type=int, default=6)
    args = ap.parse_args()

    d = args.dungeon
    slug = d.lower().replace(" ", "-")
    areas = region_areas(d)
    skel = load(os.path.join(HERE, f"skeleton-{slug}.json"))
    records = skel["Records"]

    wp_path = os.path.join(DATA, "Waypoints", "waypoints.json")
    dest_path = os.path.join(DATA, "Destinations", "destinations.json")
    wdoc = load(wp_path)
    ddoc = load(dest_path)
    existing_wp_names = {n["Name"] for n in wdoc["Waypoints"]}
    existing_wp_tiles = {(n["X"], n["Y"]) for n in wdoc["Waypoints"]}
    existing_dest_names = {r["Name"] for r in ddoc["Destinations"]}
    existing_arrivals = set()
    for r in ddoc["Destinations"]:
        for a in r.get("Arrivals", []):
            existing_arrivals.add((a["X"], a["Y"]))

    # ---- anchors: every pad walk-side / landing inside bounds ----
    anchors = []
    for r in records:
        for t in r.get("_padTiles", []):
            if in_bounds(areas, t[0], t[1]):
                anchors.append((t[0], t[1]))
        la = r.get("_landsAt")
        if la and in_bounds(areas, la[0], la[1]):
            anchors.append((la[0], la[1]))

    # snap anchors to walkable
    def snap(t, r=6):
        if walk(*t):
            return t
        for dd in range(1, r + 1):
            for dx in range(-dd, dd + 1):
                for dy in (-dd, dd):
                    for c in ((t[0] + dx, t[1] + dy), (t[0] + dy, t[1] + dx)):
                        if walk(*c):
                            return c
        return None

    anchors = [s for s in (snap(a) for a in anchors) if s]

    # ---- floor components ----
    seen = set()
    comps = []
    for a in anchors:
        c = flood(a, areas, seen)
        if c and len(c) > 40:  # ignore closet-sized slivers
            comps.append(c)

    def comp_of(t):
        st = snap(t)
        if st is None:
            return None
        for i, c in enumerate(comps):
            if st in c:
                return i
        return None

    # ---- door adjacency: closed doors split one real level into several
    # atlas components. Components whose tiles come within 4 tiles of each
    # other are door-neighbors: cluster them for level numbering and note
    # the closest tile pair as a bridge (bots open doors; the audit labels
    # these edges DOOR (bot opens)).
    bridges = {}  # (ci, cj) -> (tile_i, tile_j)
    for i in range(len(comps)):
        for j in range(i + 1, len(comps)):
            best = None
            # bounding-box prefilter
            for t in comps[i]:
                pass
            bi = comps[i]
            bj = comps[j]
            # cheap reject via bbox
            minxi = min(t[0] for t in bi); maxxi = max(t[0] for t in bi)
            minyi = min(t[1] for t in bi); maxyi = max(t[1] for t in bi)
            minxj = min(t[0] for t in bj); maxxj = max(t[0] for t in bj)
            minyj = min(t[1] for t in bj); maxyj = max(t[1] for t in bj)
            if (minxj - maxxi > 4 or minxi - maxxj > 4 or
                    minyj - maxyi > 4 or minyi - maxyj > 4):
                continue
            small, big = (bi, bj) if len(bi) <= len(bj) else (bj, bi)
            for t in small:
                for dx in range(-4, 5):
                    for dy in range(-4, 5):
                        u = (t[0] + dx, t[1] + dy)
                        if u in big:
                            d = max(abs(dx), abs(dy))
                            if best is None or d < best[0]:
                                best = (d, t, u) if small is bi else (d, u, t)
            if best:
                bridges[(i, j)] = (best[1], best[2])

    # union comps into level clusters over the bridges
    cparent = list(range(len(comps)))

    def cfind(i):
        while cparent[i] != i:
            cparent[i] = cparent[cparent[i]]
            i = cparent[i]
        return i

    for (i, j) in bridges:
        cparent[cfind(i)] = cfind(j)

    def cluster_of(ci):
        return cfind(ci)

    # ---- level assignment: BFS over door-merged CLUSTERS ----
    clevel = {}
    entry_q = deque()
    for r in records:
        la = r.get("_landsAt")
        if r["Type"] != "DungeonEntrance" or not la:
            continue
        if r["X"] > BRITANNIA_MAX_X:
            continue  # Lost Lands / cross-dungeon side entrance
        ci = comp_of((la[0], la[1]))
        if ci is not None and cluster_of(ci) not in clevel:
            clevel[cluster_of(ci)] = 1
            entry_q.append(cluster_of(ci))
    if not entry_q and comps:
        clevel[cluster_of(0)] = 1
        entry_q.append(cluster_of(0))
    while entry_q:
        cl = entry_q.popleft()
        for r in records:
            la = r.get("_landsAt")
            if not la:
                continue
            wci = comp_of((r["X"], r["Y"]))
            lci = comp_of((la[0], la[1]))
            if wci is not None and cluster_of(wci) == cl and                     lci is not None and cluster_of(lci) not in clevel:
                clevel[cluster_of(lci)] = clevel[cl] + 1
                entry_q.append(cluster_of(lci))
    nxt = max(clevel.values(), default=0) + 1
    for i in range(len(comps)):
        if cluster_of(i) not in clevel:
            clevel[cluster_of(i)] = nxt
            nxt += 1
    level = {i: clevel[cluster_of(i)] for i in range(len(comps))}

    # ---- meshes ----
    all_nodes = []   # (name, x, y, connects:list[str])
    node_name_at = {}
    dest_out = []

    def uniquify(name, taken):
        if name not in taken:
            return name
        for suf in "BCDEFGH":
            n2 = f"{name} {suf}"
            if n2 not in taken:
                return n2
        i = 2
        while f"{name} #{i}" in taken:
            i += 1
        return f"{name} #{i}"

    taken_wp = set(existing_wp_names)
    taken_dest = set(existing_dest_names)

    for ci, comp in enumerate(comps):
        lv = level[ci]
        # skip floors that already have authored waypoints (Despise L1 etc.)
        already = sum(1 for t in existing_wp_tiles if t in comp)
        if already >= 5:
            print(f"  floor L{lv} (comp {ci}, {len(comp)} tiles): "
                  f"{already} existing nodes — SKIPPING mesh")
            continue

        # pad-side seed nodes
        seeds = []
        pad_tiles = set()
        for r in records:
            for t in r.get("_padTiles", []):
                pad_tiles.add((t[0], t[1]))
        for r in records:
            wt = snap((r["X"], r["Y"]))
            la = r.get("_landsAt")
            for cand in ([wt] if wt else []) + \
                        ([snap((la[0], la[1]))] if la else []):
                if cand and cand in comp:
                    beside = nearest_walkable_beside(comp, cand, pad_tiles) or cand
                    seeds.append(beside)
        seeds = sorted(set(seeds))

        nodes, edges = build_mesh(comp, seeds)
        if not nodes:
            continue
        conns = {i: [] for i in range(len(nodes))}
        for i, j in edges:
            conns[i].append(j)
            conns[j].append(i)

        names = []
        for i, t in enumerate(nodes):
            nm = uniquify(f"{d} L{lv} WP {i + 1}", taken_wp)
            taken_wp.add(nm)
            names.append(nm)
            node_name_at[t] = nm
        for i, t in enumerate(nodes):
            all_nodes.append({
                "Name": names[i],
                "X": t[0], "Y": t[1], "Z": 0,
                "Connects": [names[j] for j in sorted(conns[i])],
            })

        # rooms
        rooms = pick_rooms(nodes, set(seeds), args.rooms_per_floor)
        for k, ri in enumerate(rooms):
            nm = uniquify(f"{d} lvl{lv} Room {k + 1}", taken_dest)
            taken_dest.add(nm)
            dest_out.append({
                "Name": nm,
                "X": nodes[ri][0], "Y": nodes[ri][1], "Z": 0,
                "Type": "DungeonRoom",
                "City": "",
                "NearestWaypoint": names[ri],
                "Dungeon": f"{d} lvl{lv}",
                "Level": lv,
            })
        print(f"  floor L{lv} (comp {ci}, {len(comp)} tiles): "
              f"{len(nodes)} nodes, {len(edges)} edges, {len(rooms)} rooms")

    # ---- door bridges: connect meshes across door-split components ----
    # place/find a node beside each end of the bridge and link them.
    name_by_tile = dict(node_name_at)

    def ensure_node_near(tile, comp_idx, lv):
        # reuse a mesh node within 6 tiles if one exists
        best, bestd = None, 7
        for t, nm in name_by_tile.items():
            if t in comps[comp_idx]:
                dd = cheb(t, tile)
                if dd < bestd:
                    bestd = dd
                    best = nm
        if best:
            return best
        nm = uniquify(f"{d} L{lv} Door WP", taken_wp)
        taken_wp.add(nm)
        all_nodes.append({
            "Name": nm, "X": tile[0], "Y": tile[1], "Z": 0,
            "Connects": [],
        })
        name_by_tile[tile] = nm
        node_name_at[tile] = nm
        return nm

    node_index = {n["Name"]: n for n in all_nodes}
    for (i, j), (ti, tj) in bridges.items():
        lv = level[i]
        skip_i = sum(1 for t in existing_wp_tiles if t in comps[i]) >= 5
        skip_j = sum(1 for t in existing_wp_tiles if t in comps[j]) >= 5
        if skip_i or skip_j:
            continue  # authored floors manage their own doors
        na = ensure_node_near(ti, i, lv)
        nb = ensure_node_near(tj, j, level[j])
        node_index = {n["Name"]: n for n in all_nodes}
        if nb not in node_index[na]["Connects"]:
            node_index[na]["Connects"].append(nb)
        if na not in node_index[nb]["Connects"]:
            node_index[nb]["Connects"].append(na)

    # ---- teleporter destination records ----
    def nearest_node_name(t):
        best, bestd = "", 1 << 30
        for tile, nm in node_name_at.items():
            dd = cheb(tile, t)
            if dd < bestd:
                bestd = dd
                best = nm
        if bestd > 30:
            # fall back to existing graph nodes (Despise handoff floors)
            for n in wdoc["Waypoints"]:
                dd = cheb((n["X"], n["Y"]), t)
                if dd < bestd:
                    bestd = dd
                    best = n["Name"]
        return best

    for r in records:
        walk_side = (r["X"], r["Y"])
        la = r.get("_landsAt")
        is_entrance = r["Type"] == "DungeonEntrance"
        if is_entrance and r["X"] > BRITANNIA_MAX_X:
            continue  # Lost Lands-side entrance: out of scope
        wci = comp_of(walk_side)
        lci = comp_of((la[0], la[1])) if la else None
        if not is_entrance and wci is None:
            continue  # walk side not in this dungeon (other region's pad)

        # dedupe against already-authored records (Despise)
        arr = r.get("Arrivals", [{}])[0]
        if (arr.get("X"), arr.get("Y")) in existing_arrivals:
            print(f"  SKIP (already authored): {r['Name']} at {arr.get('X')},{arr.get('Y')}")
            continue

        lv = level.get(wci, 0) if wci is not None else 0
        tlv = level.get(lci, 0) if lci is not None else 0

        if is_entrance:
            base = f"{d} L{tlv or 1} Entrance"
            dtag, rl = d, 0
        elif tlv == 0:
            base = f"{d} lvl{lv} Exit Ascend"
            dtag, rl = f"{d} lvl{lv}", lv
        elif tlv > lv:
            base = f"{d} lvl{lv} Descend to L{tlv}"
            dtag, rl = f"{d} lvl{lv}", lv
        else:
            base = f"{d} lvl{lv} Ascend to L{tlv}"
            dtag, rl = f"{d} lvl{lv}", lv
        name = uniquify(base, taken_dest)
        taken_dest.add(name)

        out = {
            "Name": name,
            "X": r["X"], "Y": r["Y"], "Z": r.get("Z", 0),
            "Type": r["Type"],
            "City": "",
            "NearestWaypoint": nearest_node_name(walk_side),
            "Dungeon": dtag,
            "Level": rl,
            "Arrivals": r.get("Arrivals", []),
        }
        if "ArrivalX" in r:
            out["ArrivalX"], out["ArrivalY"] = r["ArrivalX"], r["ArrivalY"]
            out["ArrivalZ"] = r.get("ArrivalZ", 0)
        if la:
            out["TargetX"], out["TargetY"], out["TargetZ"] = la
            out["TargetLevel"] = tlv
        # wire arrival waypoints to the nearest mesh node
        for a in out.get("Arrivals", []):
            a["Waypoints"] = [nearest_node_name((a["X"], a["Y"]))]
        dest_out.append(out)

    print(f"{d}: {len(all_nodes)} new waypoints, {len(dest_out)} new destinations")

    if not args.merge:
        out_path = os.path.join(HERE, f"mesh-{slug}.json")
        json.dump({"Waypoints": all_nodes, "Destinations": dest_out},
                  open(out_path, "w"), indent=1)
        print(f"wrote {out_path} (review, then rerun with --merge)")
        return

    import shutil
    shutil.copy(wp_path, wp_path + ".bak-dungeonmesh")
    shutil.copy(dest_path, dest_path + ".bak-dungeonmesh")
    wdoc["Waypoints"].extend(all_nodes)
    ddoc["Destinations"].extend(dest_out)
    json.dump(wdoc, open(wp_path, "w"), indent=2)
    json.dump(ddoc, open(dest_path, "w"), indent=2)
    print(f"merged into live waypoints/destinations (backups .bak-dungeonmesh)")


if __name__ == "__main__":
    main()
