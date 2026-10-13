#!/usr/bin/env python3
"""Draw a Walk zone over every dungeon floor.

Reads the walk atlas (255 = standable) inside each dungeon's region box,
floods the standable ground out from the dungeon's authored points (rooms
and stairs in destinations.json), and turns each flooded floor into an
outline polygon: a Walk zone with Tag dungeon and the Dungeon/Level of the
points on it, the same thing the map editor draws with key 6.

Closed doors read as wall in the atlas. Doors from the Decoration files
count as floor here, because bots open them. The atlas was read at one
height, so a ramp up to a ledge can read as wall too: the tiles under every
short waypoint link inside the dungeon count as floor, because those links
were walked by the engine.

Zones already in zones.json are kept. A floor that overlaps one is skipped.
--replace NAME,... drops the old dungeon Walk zones of those dungeons first.

  python draw_dungeon_floors.py [--dry-run] [--replace Hythloth] [--png out.png]

A backup goes next to zones.json first (.bak-dungeonfloors). Then
[ReloadZones in game, or the editor's Reload button.
"""
import glob, json, os, re, shutil, sys
from collections import Counter, deque

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from draw_shop_floors import outline

SHARD_ROOT = os.environ.get("UO_SHARD_ROOT") or os.path.expanduser("~/uo-modernuo")
DATA = os.path.join(SHARD_ROOT, "ModernUO", "Distribution", "Data")
ZONES_JSON = os.path.join(DATA, "Zones", "zones.json")
DEST_JSON = os.path.join(DATA, "Destinations", "destinations.json")
WAYPOINTS_JSON = os.path.join(DATA, "Waypoints", "waypoints.json")
REGIONS_JSON = os.path.join(DATA, "regions.json")
DECOR = os.path.join(DATA, "Decoration", "Britannia")
ATLAS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "walk_atlas.pgm")

DUNGEONS = ["Covetous", "Deceit", "Despise", "Destard", "Hythloth", "Shame",
            "Wrong", "Fire", "Ice", "Orc Cave", "Terathan Keep", "Khaldun"]
INTERIOR = {"DungeonRoom", "DungeonDescend", "DungeonAscend"}
# An authored point can sit on a stair pad the atlas calls blocked.
SNAP = 3
# A waypoint link longer than this is a teleporter hop, not a walk.
MAX_LINK = 25
# Floors smaller than this are a stair landing or a pocket, not a floor.
MIN_TILES = 12


def load_atlas():
    data = open(ATLAS, "rb").read()
    parts = data.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    return w, h, parts[3]


def door_tiles():
    tiles = set()
    for path in glob.glob(os.path.join(DECOR, "*.cfg")):
        is_door = False
        for line in open(path, encoding="utf-8", errors="replace"):
            line = line.strip()
            if not line or line.startswith("#"):
                is_door = False
                continue
            if re.match(r"^-?\d+ -?\d+ -?\d+", line):
                if is_door:
                    x, y = map(int, line.split()[:2])
                    tiles.add((x, y))
            else:
                is_door = "Door" in line.split()[0]
    return tiles


def link_tiles():
    """Tiles under every short waypoint link in the dungeon strip."""
    wps = json.load(open(WAYPOINTS_JSON, encoding="utf-8"))["Waypoints"]
    by = {w["Name"]: w for w in wps}
    tiles = set()
    for a in wps:
        if a["X"] < 5120:
            continue
        for name in a.get("Connects", []):
            b = by.get(name)
            if b is None or b["X"] < 5120:
                continue
            n = max(abs(b["X"] - a["X"]), abs(b["Y"] - a["Y"]))
            if n > MAX_LINK:
                continue
            for i in range(n + 1):
                t = i / n if n else 0
                tiles.add((round(a["X"] + (b["X"] - a["X"]) * t), round(a["Y"] + (b["Y"] - a["Y"]) * t)))
    return tiles


def inside(points, px, py):
    """The game's ZoneRegistry.Contains, ray cast on tile coordinates."""
    ins = False
    j = len(points) - 1
    for i in range(len(points)):
        xi, yi = points[i]
        xj, yj = points[j]
        if (yi > py) != (yj > py) and px < (xj - xi) * (py - yi) / (yj - yi) + xi:
            ins = not ins
        j = i
    return ins


def simplify(loop, tol):
    """Douglas-Peucker on a closed loop of integer points."""
    def dp(pts):
        if len(pts) < 3:
            return pts
        (ax, ay), (bx, by) = pts[0], pts[-1]
        dx, dy = bx - ax, by - ay
        n = (dx * dx + dy * dy) ** 0.5
        best, bi = -1, 0
        for i in range(1, len(pts) - 1):
            px, py = pts[i]
            d = abs(dy * (px - ax) - dx * (py - ay)) / n if n else ((px - ax) ** 2 + (py - ay) ** 2) ** 0.5
            if d > best:
                best, bi = d, i
        if best <= tol:
            return [pts[0], pts[-1]]
        return dp(pts[:bi + 1])[:-1] + dp(pts[bi:])
    # Split at the point farthest from the first so both halves are open runs.
    a = loop[0]
    far = max(range(len(loop)), key=lambda i: (loop[i][0] - a[0]) ** 2 + (loop[i][1] - a[1]) ** 2)
    left = dp(loop[:far + 1])
    right = dp(loop[far:] + [loop[0]])
    return left[:-1] + right[:-1]


def main():
    dry = "--dry-run" in sys.argv
    replace = set()
    png = None
    for i, a in enumerate(sys.argv):
        if a == "--replace":
            replace = {s.strip().lower() for s in sys.argv[i + 1].split(",")}
        if a == "--png":
            png = sys.argv[i + 1]

    w, h, px = load_atlas()
    doors = door_tiles() | link_tiles()
    regions = {r["Name"]: r["Area"] for r in json.load(open(REGIONS_JSON, encoding="utf-8"))
               if r.get("Map") == "Felucca" and r.get("Name") in DUNGEONS}
    dests = json.load(open(DEST_JSON, encoding="utf-8"))["Destinations"]
    zdoc = json.load(open(ZONES_JSON, encoding="utf-8"))
    dropped = [z for z in zdoc["Zones"] if z.get("Tag") == "dungeon"
               and (z.get("Dungeon") or "").split(" lvl")[0].lower() in replace]
    existing = [z for z in zdoc["Zones"] if z not in dropped]
    taken = {z["Name"] for z in existing}

    def in_box(name, x, y):
        return any(b["x1"] <= x <= b["x2"] and b["y1"] <= y <= b["y2"] for b in regions[name])

    def standable(x, y):
        return (0 <= x < w and 0 <= y < h and px[y * w + x] == 255) or (x, y) in doors

    def owned(x, y):
        return any(z["Kind"] in ("Walk", "Area") and inside(z["Points"], x, y) for z in existing)

    report = [f"dropping {len(dropped)} old zone(s): " + ", ".join(z["Name"] for z in dropped)] if dropped else []
    new_zones = []
    for dg in DUNGEONS:
        if dg not in regions:
            report.append(f"{dg}: no region box")
            continue
        pts = [d for d in dests if d.get("Type") in INTERIOR and in_box(dg, d["X"], d["Y"])]
        seen = {}
        floors = []
        for d in pts:
            # Snap to the nearest standable tile.
            seed = None
            for r in range(SNAP + 1):
                for dx in range(-r, r + 1):
                    for dy in range(-r, r + 1):
                        if seed is None and max(abs(dx), abs(dy)) == r and standable(d["X"] + dx, d["Y"] + dy) \
                                and in_box(dg, d["X"] + dx, d["Y"] + dy):
                            seed = (d["X"] + dx, d["Y"] + dy)
            if seed is None:
                report.append(f"  {dg}: '{d['Name']}' has no standable ground within {SNAP}")
                continue
            if seed in seen:
                floors[seen[seed]][1].append(d)
                continue
            fi = len(floors)
            tiles = []
            q = deque([seed])
            seen[seed] = fi
            while q:
                x, y = q.popleft()
                tiles.append((x, y))
                for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                    if (nx, ny) not in seen and in_box(dg, nx, ny) and standable(nx, ny):
                        seen[(nx, ny)] = fi
                        q.append((nx, ny))
            floors.append((tiles, [d]))

        n_dg = 0
        for tiles, ds in floors:
            tags = Counter((d.get("Dungeon") or dg, d.get("Level") or 1) for d in ds)
            (tag, level), _ = tags.most_common(1)[0]
            if len(tiles) < MIN_TILES:
                report.append(f"  {dg}: {len(tiles)}-tile pocket at {tiles[0]} ({tag}) left out")
                continue
            if any(owned(x, y) for x, y in tiles[::7]):
                report.append(f"  {dg}: floor under '{tag}' overlaps a drawn zone, left alone")
                continue
            # Grow one tile so every floor tile sits strictly inside the
            # outline, then thin the stair-step edge.
            grown = set(tiles)
            for x, y in tiles:
                grown.update(((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1),
                              (x + 1, y + 1), (x - 1, y - 1), (x + 1, y - 1), (x - 1, y + 1)))
            loop = [tuple(p) for p in outline(grown)]
            poly = loop
            for tol in (1.4, 1.0, 0.7):
                cand = simplify(loop, tol)
                if all(inside(cand, x, y) for x, y in tiles):
                    poly = cand
                    break
            base = f"{tag} floor"
            name, k = base, 2
            while name in taken:
                name, k = f"{base} {k}", k + 1
            taken.add(name)
            new_zones.append({"Name": name, "Kind": "Walk", "Tag": "dungeon", "Cost": 1.0,
                              "Points": [list(p) for p in poly], "Dungeon": tag, "Level": level})
            n_dg += 1
            mixed = "" if len(tags) == 1 else "  (points tagged " + ", ".join(f"{t} L{l}" for (t, l) in tags) + ")"
            report.append(f"  {name}: {len(tiles)} tiles, {len(poly)} corners, {len(ds)} points{mixed}")
        report.append(f"{dg}: {n_dg} floor(s)")

    print("\n".join(report))
    print(f"\n{len(new_zones)} new dungeon Walk zones")

    if png:
        from PIL import Image, ImageDraw
        boxes = [b for a in regions.values() for b in a if b["y2"] < 2100]
        x0, y0 = min(b["x1"] for b in boxes), min(b["y1"] for b in boxes)
        x1, y1 = max(b["x2"] for b in boxes), max(b["y2"] for b in boxes)
        sc = 2
        img = Image.new("RGB", ((x1 - x0) * sc, (y1 - y0) * sc), (20, 20, 24))
        pix = img.load()
        for y in range(y0, y1):
            for x in range(x0, x1):
                if standable(x, y):
                    for a in range(sc):
                        for b in range(sc):
                            pix[(x - x0) * sc + a, (y - y0) * sc + b] = (70, 70, 78)
        dr = ImageDraw.Draw(img)
        for z in new_zones:
            if z["Points"][0][1] > y1:
                continue
            dr.polygon([((p[0] - x0) * sc, (p[1] - y0) * sc) for p in z["Points"]], outline=(80, 200, 255))
            fx, fy = z["Points"][0]
            dr.text(((fx - x0) * sc + 3, (fy - y0) * sc + 3), z["Name"], fill=(255, 255, 255))
        img.save(png)
        print("preview:", png)

    if dry:
        return
    shutil.copy2(ZONES_JSON, ZONES_JSON + ".bak-dungeonfloors")
    zdoc["Zones"] = existing + new_zones
    with open(ZONES_JSON, "w", encoding="utf-8") as f:
        json.dump(zdoc, f, indent=2)
    print("wrote", ZONES_JSON)


if __name__ == "__main__":
    main()
