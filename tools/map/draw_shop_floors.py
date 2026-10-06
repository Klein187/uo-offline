#!/usr/bin/env python3
"""Draw shop floors from the game's measurement.

The game's [SurveyShopFloors (or shopfloors_request.txt) writes
Data/Live/shop_floors.json: for every shop destination with no floor drawn,
the NPC that runs it and the floor tiles around that NPC. This turns each
measured floor into an outline polygon and writes it on the destination in
destinations.json, the same Polygon field the map editor draws. Floors that
were already drawn are never touched.

  python draw_shop_floors.py [--dry-run]

A backup goes next to destinations.json first (.bak-shopfloors). Then
[ReloadDestinations in game, or the editor's Reload button.
"""
import json, os, shutil, sys

SHARD_ROOT = os.environ.get("UO_SHARD_ROOT") or os.path.expanduser("~/uo-modernuo")
DATA = os.path.join(SHARD_ROOT, "ModernUO", "Distribution", "Data")
DEST_JSON = os.path.join(DATA, "Destinations", "destinations.json")
FLOORS_JSON = os.path.join(DATA, "Live", "shop_floors.json")

# A floor bigger than this leaked into the next room through a gap in a
# counter; keep the part near the NPC.
MAX_TILES = 250
CROP = 9
# A stall with no roof gets this much square around the NPC.
STALL = 2


def outline(tiles):
    """Outer boundary of a set of unit tiles, as tile-corner points.

    Tile (x, y) is the square [x, x+1] x [y, y+1]. Boundary edges run
    clockwise; chaining them gives loops, the biggest is the outside (holes
    such as a counter in the middle are dropped, so they stay inside).
    """
    s = set(tiles)
    # A corner can start two edges, so this is a multimap.
    starts = {}
    for x, y in s:
        if (x, y - 1) not in s:
            starts.setdefault((x, y), []).append((x + 1, y))
        if (x + 1, y) not in s:
            starts.setdefault((x + 1, y), []).append((x + 1, y + 1))
        if (x, y + 1) not in s:
            starts.setdefault((x + 1, y + 1), []).append((x, y + 1))
        if (x - 1, y) not in s:
            starts.setdefault((x, y + 1), []).append((x, y))
    loops = []
    while starts:
        a = next(iter(starts))
        loop = [a]
        cur = a
        while True:
            nxts = starts.get(cur)
            if not nxts:
                break
            nxt = nxts.pop()
            if not nxts:
                del starts[cur]
            if nxt == a:
                break
            loop.append(nxt)
            cur = nxt
        loops.append(loop)

    def area(lp):
        return abs(sum(lp[i][0] * lp[(i + 1) % len(lp)][1] - lp[(i + 1) % len(lp)][0] * lp[i][1]
                       for i in range(len(lp)))) / 2

    best = max(loops, key=area)
    # Drop points in the middle of a straight run.
    out = []
    n = len(best)
    for i in range(n):
        p, c, q = best[i - 1], best[i], best[(i + 1) % n]
        if (p[0] == c[0] == q[0]) or (p[1] == c[1] == q[1]):
            continue
        out.append([c[0], c[1]])
    return out


def main():
    dry = "--dry-run" in sys.argv
    floors = json.load(open(FLOORS_JSON, encoding="utf-8"))
    doc = json.load(open(DEST_JSON, encoding="utf-8"))
    by_name = {d["Name"]: d for d in doc["Destinations"]}

    drawn, skipped = [], []
    for f in floors:
        d = by_name.get(f["dest"])
        if d is None or d.get("Polygon"):
            continue
        status = f["status"]
        if status == "ok":
            tiles = [tuple(t) for t in f["tiles"]]
            nx, ny = f["npc"][0], f["npc"][1]
            if len(tiles) > MAX_TILES:
                tiles = [t for t in tiles if max(abs(t[0] - nx), abs(t[1] - ny)) <= CROP]
            poly = outline(tiles)
            note = f"{len(tiles)} tiles"
        elif status.startswith("NPC not under a roof"):
            # A street stall: the NPC has no shop around it. A small square
            # it stands in the middle of.
            npc = f.get("npc")
            if not npc:
                skipped.append((f["dest"], status))
                continue
            nx, ny = npc[0], npc[1]
            poly = [[nx - STALL, ny - STALL], [nx + STALL + 1, ny - STALL],
                    [nx + STALL + 1, ny + STALL + 1], [nx - STALL, ny + STALL + 1]]
            note = "stall"
        else:
            skipped.append((f["dest"], status + (f" (nearby: {', '.join(f.get('nearby', []))})" if f.get("nearby") else "")))
            continue
        if len(poly) < 3:
            skipped.append((f["dest"], "outline came out empty"))
            continue
        d["Polygon"] = poly
        drawn.append((f["dest"], note, len(poly)))

    print(f"drawing {len(drawn)} shop floor(s):")
    for name, note, n in drawn:
        print(f"  {name:30} {note:>10}  {n} corners")
    print(f"left undrawn ({len(skipped)}):")
    for name, why in skipped:
        print(f"  {name:30} {why}")
    if dry:
        print("(dry run, nothing written)")
        return
    shutil.copy(DEST_JSON, DEST_JSON + ".bak-shopfloors")
    with open(DEST_JSON, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2, ensure_ascii=False)
    print(f"wrote {DEST_JSON} (backup {DEST_JSON}.bak-shopfloors)")


if __name__ == "__main__":
    main()
