#!/usr/bin/env python3
"""Generate a destinations.json skeleton for a dungeon from teleporters.json.

Every dungeon's Entrance/Descend/Ascend topology already exists, verified, in
ModernUO's Data/teleporters.json — every pad tile with exact coordinates. This
tool reads it (plus regions.json for dungeon bounds) and emits proposed
destination records so authoring a new dungeon starts from a correct skeleton
instead of hand-copied coordinates. Only rooms and waypoints remain manual.

Baked-in lessons from the Despise debugging sessions:
  * back=true records create a REAL return pad at dst with no src record of
    its own — both directions are collected.
  * Adjacent teleporter tiles (a 3-tile-wide pad) are grouped into one
    logical pad; the middle tile becomes the walk target.
  * Entrance records put Location on a SAFE tile 2 tiles away from the pad
    (bots idling/rescuing at Location must not get revolving-doored) while
    ArrivalX/Y + Arrivals[0] sit exactly ON the pad.
  * Levels are BFS depth through the pad graph from the entrance — a first
    cut for human review, since "level" really means walk-connected area.

Usage:
  python gen_dungeon_skeleton.py <DungeonName> [--server-data DIR] [-o OUT]

Output: skeleton-<dungeon>.json next to this script (or -o path) for review;
merge the records you want into destinations.json, then run the audit
(Data/Live/audit_request.txt) to verify.
"""

import argparse
import json
import os
import sys
from collections import defaultdict, deque

DEFAULT_SERVER_DATA = os.path.expanduser(
    r"~\uo-modernuo\ModernUO\Distribution\Data"
)


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def in_area(areas, x, y):
    return any(a["x1"] <= x <= a["x2"] and a["y1"] <= y <= a["y2"] for a in areas)


def cheb(a, b):
    return max(abs(a[0] - b[0]), abs(a[1] - b[1]))


def group_pads(tiles):
    """Cluster adjacent (x,y,z) tiles into logical pads; returns list of
    (center_tile, tiles) sorted for stable output."""
    tiles = sorted(set(tiles))
    groups = []
    for t in tiles:
        placed = False
        for g in groups:
            if any(cheb(t, u) <= 2 for u in g):
                g.append(t)
                placed = True
                break
        if not placed:
            groups.append([t])
    out = []
    for g in groups:
        g.sort()
        out.append((g[len(g) // 2], g))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dungeon", help="DungeonRegion name, e.g. Deceit")
    ap.add_argument("--server-data", default=DEFAULT_SERVER_DATA)
    ap.add_argument("--map", default="Felucca")
    ap.add_argument("-o", "--out", default=None)
    args = ap.parse_args()

    regions = load(os.path.join(args.server_data, "regions.json"))
    teles = load(os.path.join(args.server_data, "teleporters.json"))

    region = next(
        (r for r in regions
         if r["$type"] == "DungeonRegion" and r.get("Map") == args.map
         and r["Name"].lower() == args.dungeon.lower()),
        None,
    )
    if region is None:
        names = sorted(r["Name"] for r in regions
                       if r["$type"] == "DungeonRegion" and r.get("Map") == args.map)
        sys.exit(f"No DungeonRegion '{args.dungeon}' on {args.map}. "
                 f"Known: {', '.join(names)}")
    areas = region["Area"]

    # Directed teleport edges touching this dungeon (src->dst, plus the
    # implicit dst->src pad for back=true records).
    edges = []
    for t in teles:
        if t["src"]["map"] != args.map or t["dst"]["map"] != args.map:
            continue
        s, d = tuple(t["src"]["loc"]), tuple(t["dst"]["loc"])
        s_in, d_in = in_area(areas, s[0], s[1]), in_area(areas, d[0], d[1])
        if s_in or d_in:
            edges.append((s, d))
            if t.get("back"):
                edges.append((d, s))

    if not edges:
        sys.exit(f"No teleporters touch '{region['Name']}' — nothing to generate.")

    # Group pad tiles; map every tile to its pad center.
    pad_groups = group_pads([e[0] for e in edges])
    tile2pad = {}
    for center, tiles in pad_groups:
        for t in tiles:
            tile2pad[t] = center

    # Pad-level directed graph: pad -> set of landing pads (the landing may
    # not itself be a pad; keep the raw landing too for arrival coords).
    pad_edges = defaultdict(set)   # pad center -> {(landing_tile)}
    for s, d in edges:
        pad_edges[tile2pad[s]].add(d)

    def pad_of_landing(tile):
        """Landing tiles are usually 1-3 tiles from the return pad."""
        best, bd = None, 99
        for center, tiles in pad_groups:
            for u in tiles:
                dd = cheb(tile, u)
                if dd < bd:
                    best, bd = center, dd
        return best if bd <= 4 else None

    # Entrances: pads OUTSIDE the dungeon leading IN. Level = BFS depth of
    # the pad's walk-area; approximated by teleport-hop depth from entrance.
    entrances = [p for p, _ in pad_groups
                 if not in_area(areas, p[0], p[1])
                 and any(in_area(areas, l[0], l[1]) for l in pad_edges[p])]

    depth = {}
    q = deque()
    for p in entrances:
        depth[p] = 0
        q.append(p)
    while q:
        p = q.popleft()
        for landing in pad_edges[p]:
            lp = pad_of_landing(landing)
            if lp is not None and lp not in depth:
                depth[lp] = depth[p] + 1
                q.append(lp)

    dname = region["Name"]
    records = []
    for p, tiles in pad_groups:
        landings = sorted(pad_edges[p])
        landing = landings[0] if landings else None
        d = depth.get(p)
        if p in entrances:
            records.append({
                "Name": f"{dname} L1 Entrance",
                # SAFE tile 2 east of the pad — never ON it (revolving door).
                "X": p[0] + 2, "Y": p[1] + 1, "Z": p[2],
                "Type": "DungeonEntrance",
                "City": "", "NearestWaypoint": "",
                "Dungeon": dname, "Level": 1,
                "ArrivalX": p[0], "ArrivalY": p[1], "ArrivalZ": p[2],
                "Arrivals": [{"X": p[0], "Y": p[1], "Z": p[2], "Waypoints": []}],
                "_padTiles": [list(t) for t in tiles],
                "_landsAt": list(landing) if landing else None,
            })
            continue
        if d is None:
            # A pad never reached from an entrance (exit-only side of a
            # one-way, or another dungeon's overlap) — flag for review.
            kind = "DungeonAscend" if not any(
                in_area(areas, l[0], l[1]) for l in pad_edges[p]) else "DungeonDescend"
            note = "UNREACHED from entrance — review"
        else:
            lands_outside = landing and not in_area(areas, landing[0], landing[1])
            deeper = (landing and pad_of_landing(landing) in depth
                      and depth.get(pad_of_landing(landing), 0) > d)
            kind = "DungeonAscend" if lands_outside else (
                "DungeonDescend" if deeper or not lands_outside else "DungeonAscend")
            note = None
        level = max(1, d if d is not None else 1)
        suffix = "Exit Ascend" if kind == "DungeonAscend" and landing and \
            not in_area(areas, landing[0], landing[1]) else \
            ("Descend" if kind == "DungeonDescend" else "Ascend")
        rec = {
            "Name": f"{dname} lvl{level} L{level} {suffix}",
            "X": p[0], "Y": p[1], "Z": p[2],
            "Type": kind,
            "City": "", "NearestWaypoint": "",
            "Dungeon": f"{dname} lvl{level}", "Level": level,
            "Arrivals": [{"X": p[0], "Y": p[1], "Z": p[2], "Waypoints": []}],
            "_padTiles": [list(t) for t in tiles],
            "_landsAt": list(landing) if landing else None,
        }
        if note:
            rec["_note"] = note
        records.append(rec)

    out = args.out or os.path.join(
        os.path.dirname(os.path.abspath(__file__)),
        f"skeleton-{dname.lower().replace(' ', '-')}.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"Dungeon": dname, "Records": records}, f, indent=2)

    print(f"{dname}: {len(pad_groups)} pad group(s), "
          f"{len(entrances)} entrance(s), depth levels: "
          f"{sorted(set(depth.values())) if depth else '[]'}")
    for r in records:
        print(f"  {r['Type']:16} {r['Name']:34} pad({r.get('ArrivalX', r['X'])},"
              f"{r.get('ArrivalY', r['Y'])}) -> {r['_landsAt']}"
              + (f"  [{r['_note']}]" if "_note" in r else ""))
    print(f"wrote {out}")
    print("Review, strip _-prefixed keys, merge into destinations.json, "
          "then bump Data/Live/audit_request.txt to verify.")


if __name__ == "__main__":
    main()
