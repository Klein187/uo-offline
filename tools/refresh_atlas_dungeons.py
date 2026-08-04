#!/usr/bin/env python3
"""Refresh walk_atlas.pgm in the 12 target dungeon rects from the LIVE shard.

The July atlas has stale strips (world content changed); every offline
authoring decision inside dungeons should ride current engine truth. Fetches
each region rect via the walkmap bridge and patches the atlas in place
(backup first).
"""
import json
import os
import shutil
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
LIVE = r"C:/Users/logdc/uo-modernuo/ModernUO/Distribution/Data/Live"
ATLAS = r"C:/Users/logdc/uo-offline/tools/map/walk_atlas.pgm"
REG = r"C:/Users/logdc/uo-modernuo/ModernUO/Distribution/Data/regions.json"
TARGET = ["Covetous", "Deceit", "Despise", "Destard", "Hythloth", "Shame",
          "Wrong", "Fire", "Ice", "Orc Cave", "Terathan Keep", "Khaldun"]

def read_pgm_bytes(path):
    data = open(path, "rb").read()
    parts = data.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    return w, h, bytearray(parts[3])

def fetch(x0, y0, x1, y1):
    pgm = os.path.join(LIVE, "walkmap.pgm")
    zp = os.path.join(LIVE, "walkmap_z.pgm")
    m0 = os.path.getmtime(pgm) if os.path.exists(pgm) else 0
    tok = int(time.time() * 1000) % 2000000000
    open(os.path.join(LIVE, "walkmap_request.txt"), "w").write(
        f"{tok} {x0} {y0} {x1} {y1}")
    t0 = time.time()
    while time.time() - t0 < 300:
        time.sleep(1)
        if os.path.getmtime(pgm) != m0:
            break
    else:
        raise SystemExit("walkmap timeout")
    for _ in range(30):
        time.sleep(1)
        try:
            data = open(pgm, "rb").read()
            parts = data.split(b"\n", 3)
            w, h = map(int, parts[1].split())
            if len(parts[3]) >= w * h:
                return w, h, parts[3][: w * h]
        except Exception:
            pass
    raise SystemExit("walkmap never settled")

regions = json.load(open(REG, encoding="utf-8"))
rects = []
for r in regions:
    if "Dungeon" in r.get("$type", "") and r.get("Map") == "Felucca" \
       and r["Name"] in TARGET:
        for a in r.get("Area", []):
            rects.append((r["Name"], a["x1"], a["y1"], a["x2"], a["y2"]))

AW, AH, atlas = read_pgm_bytes(ATLAS)
shutil.copy2(ATLAS, ATLAS + ".bak-pre-dungeon-refresh")
changed = 0
for name, x1, y1, x2, y2 in rects:
    # clamp to 512 windows (all target rects fit; Despise is 507 tall)
    w, h, mask = fetch(x1, y1, min(x2, x1 + 511), min(y2, y1 + 511))
    for yy in range(h):
        row = (y1 + yy) * AW
        for xx in range(w):
            old = atlas[row + x1 + xx]
            new = mask[yy * w + xx]
            if old != new:
                changed += 1
            atlas[row + x1 + xx] = new
    print(f"{name} ({x1},{y1})-({x1+w-1},{y1+h-1}) patched")

with open(ATLAS, "wb") as f:
    f.write(f"P5\n{AW} {AH}\n255\n".encode())
    f.write(bytes(atlas))
print(f"atlas updated, {changed} tiles changed")
