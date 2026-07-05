#!/usr/bin/env python3
"""Build a full-map walkability atlas from the live shard.

Sweeps the whole Felucca map (7168x4096) in 512x512 strips through the
EditorReloadWatcher walkmap bridge (Data/Live/walkmap_request.txt ->
walkmap.pgm, server-truth Walkable.TryFindSeedZ per tile) and stitches the
results into one big P5 PGM: tools/map/walk_atlas.pgm (255 = standable).

Run while the shard is up. ~3s/strip => ~6 min for the full map.
Re-runnable: pass --resume to keep strips already present in the strip cache.

Usage:
    python walkmap_atlas.py [--resume] [--live <Data/Live dir>]
"""
import argparse
import os
import sys
import time

MAP_W, MAP_H = 7168, 4096
SIDE = 512
DEFAULT_LIVE = r"C:/Users/logdc/uo-modernuo/ModernUO/Distribution/Data/Live"
HERE = os.path.dirname(os.path.abspath(__file__))
ATLAS = os.path.join(HERE, "map", "walk_atlas.pgm")
STRIP_DIR = os.path.join(HERE, "map", "walk_strips")
TIMEOUT = 240  # per strip


def read_pgm(path):
    with open(path, "rb") as f:
        data = f.read()
    if not data.startswith(b"P5"):
        raise ValueError("not a P5 pgm")
    # header: P5\n<w> <h>\n255\n
    parts = data.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    return w, h, parts[3][: w * h]


def request_strip(live, token, x0, y0, x1, y1):
    req = os.path.join(live, "walkmap_request.txt")
    pgm = os.path.join(live, "walkmap.pgm")
    m0 = os.path.getmtime(pgm) if os.path.exists(pgm) else 0
    with open(req, "w") as f:
        f.write(f"{token} {x0} {y0} {x1} {y1}")
    t0 = time.time()
    while time.time() - t0 < TIMEOUT:
        time.sleep(1)
        if os.path.exists(pgm) and os.path.getmtime(pgm) != m0:
            time.sleep(0.3)  # let the write finish
            return read_pgm(pgm)
    raise TimeoutError(f"strip ({x0},{y0}) timed out after {TIMEOUT}s — shard down?")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--resume", action="store_true")
    ap.add_argument("--live", default=DEFAULT_LIVE)
    args = ap.parse_args()

    os.makedirs(STRIP_DIR, exist_ok=True)
    atlas = bytearray(MAP_W * MAP_H)
    token = int(time.time())  # monotonic-enough unique token base
    total = (MAP_W // SIDE) * (MAP_H // SIDE)
    n = 0
    t_start = time.time()
    for sy in range(0, MAP_H, SIDE):
        for sx in range(0, MAP_W, SIDE):
            n += 1
            cache = os.path.join(STRIP_DIR, f"strip_{sx}_{sy}.pgm")
            if args.resume and os.path.exists(cache):
                w, h, body = read_pgm(cache)
            else:
                token += 1
                w, h, body = request_strip(
                    args.live, token, sx, sy, sx + SIDE - 1, sy + SIDE - 1
                )
                with open(cache, "wb") as f:
                    f.write(b"P5\n%d %d\n255\n" % (w, h) + body)
                print(f"[{n}/{total}] ({sx},{sy}) {w}x{h} ok", flush=True)
            for row in range(h):
                dst = (sy + row) * MAP_W + sx
                atlas[dst : dst + w] = body[row * w : (row + 1) * w]

    with open(ATLAS, "wb") as f:
        f.write(b"P5\n%d %d\n255\n" % (MAP_W, MAP_H) + bytes(atlas))
    print(f"atlas written: {ATLAS} ({MAP_W}x{MAP_H}) in {time.time()-t_start:.0f}s")

    try:
        from PIL import Image

        img = Image.frombytes("L", (MAP_W, MAP_H), bytes(atlas))
        img.resize((MAP_W // 4, MAP_H // 4)).save(
            os.path.join(HERE, "map", "walk_atlas_preview.png")
        )
        print("preview written: map/walk_atlas_preview.png")
    except ImportError:
        pass


if __name__ == "__main__":
    main()
