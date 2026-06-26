#!/usr/bin/env python3
"""
make_map_png.py — render uomap.png from UO's map0/statics0/staidx0 + radarcol.mul.

The map editor (serve_map.py / map.html) needs a world background image so
waypoints/zones can be placed against the actual geography. This generates
that PNG from the client's own data files — no copying between machines.

Output is one pixel per UO tile (6144 x 4096+ for Felucca/map0). Each pixel is
colored exactly like the in-client world map:
  * the LAND tile's radar color (radarcol.mul, 16-bit BGR555), UNLESS
  * a STATIC item covers that tile — then the topmost (highest-Z) static's
    radar color is drawn on top.
Statics matter: in UO, rivers and docks are static water/plank items sitting on
dirt/water LAND. A land-only render shows tan riverbeds and no docks; overlaying
statics (radarcol index 0x4000 + itemID) makes rivers blue and docks/towns
visible, matching the client's radar. Set MAP_INCLUDE_STATICS=0 to render land
only.

Usage:
    python make_map_png.py                # auto paths (UODATA + ~/uo-map)
    python make_map_png.py <uo_data_dir> <out_png>

Pure-Python PNG writer (no Pillow dependency) so it runs on a bare install.
"""
import os, sys, struct, zlib

# ---- paths (match serve_map.py defaults; override via argv) ----------------
UO_DIR = os.path.expanduser("~/uo-modernuo/UOData/7.0.23.1")
OUT_PNG = os.path.join(os.path.expanduser("~/uo-map"), "uomap.png")
if len(sys.argv) >= 2: UO_DIR = sys.argv[1]
if len(sys.argv) >= 3: OUT_PNG = sys.argv[2]

# ---- Felucca/Trammel map0 dimensions (in tiles) ---------------------------
# 6144 x 4096 tiles = 768 x 512 blocks of 8x8. Confirmed by file size:
# 768*512 blocks * 196 bytes = 77,070,336 ... but map0.mul is 89,915,392,
# which is the 7.0.x extended height: 6144 x 4096 IS correct for the classic
# Britannia map; the larger file includes the extra. We derive block count
# from width and validate against file size.
BLOCK_BYTES = 196                 # 4-byte header + 64 tiles * 3 bytes
# serve_map.py derives width as: filesize // (196 * 512), i.e. it assumes a
# fixed 512-block height and computes the block-width from the file size.
# We MUST match that exactly so the PNG aligns with where the editor plots
# waypoints. (Your map0.mul is an extended map, not the classic 768x512.)
MAP_H_BLOCKS = 512
def _derive_width_blocks(map_path):
    return os.path.getsize(map_path) // (BLOCK_BYTES * MAP_H_BLOCKS)

def load_radarcol(path):
    """radarcol.mul = array of 16-bit BGR555 colors indexed by tile id.
    Returns a list mapping tileID -> (r,g,b)."""
    data = open(path, "rb").read()
    n = len(data) // 2
    pal = [(0, 0, 0)] * n
    for i in range(n):
        c = struct.unpack_from("<H", data, i * 2)[0]
        # BGR555: bits 0-4 blue, 5-9 green, 10-14 red, 15 unused
        r = (c >> 10) & 0x1F
        g = (c >> 5) & 0x1F
        b = c & 0x1F
        # scale 5-bit -> 8-bit
        pal[i] = ((r << 3) | (r >> 2), (g << 3) | (g >> 2), (b << 3) | (b >> 2))
    return pal

STATIC_RADAR_OFFSET = 0x4000   # radarcol: 0..0x3FFF land, 0x4000.. statics
STATIC_BYTES = 7               # ushort id, byte x, byte y, sbyte z, ushort hue

def render():
    map_path = os.path.join(UO_DIR, "map0.mul")
    rad_path = os.path.join(UO_DIR, "radarcol.mul")
    idx_path = os.path.join(UO_DIR, "staidx0.mul")
    sta_path = os.path.join(UO_DIR, "statics0.mul")
    if not os.path.exists(map_path): sys.exit(f"map0.mul not found at {map_path}")
    if not os.path.exists(rad_path): sys.exit(f"radarcol.mul not found at {rad_path}")

    include_statics = os.environ.get("MAP_INCLUDE_STATICS", "1") != "0"
    have_statics = include_statics and os.path.exists(idx_path) and os.path.exists(sta_path)
    if include_statics and not have_statics:
        print("  (statics overlay requested but staidx0/statics0 missing — land only)")

    print(f"Loading radar palette: {rad_path}")
    pal = load_radarcol(rad_path)
    pal_n = len(pal)

    MAP_W_BLOCKS = _derive_width_blocks(map_path)
    MAP_W_TILES = MAP_W_BLOCKS * 8
    MAP_H_TILES = MAP_H_BLOCKS * 8
    print(f"Reading {map_path} ({MAP_W_TILES}x{MAP_H_TILES} tiles, "
          f"{MAP_W_BLOCKS}x{MAP_H_BLOCKS} blocks)"
          f"{' + statics overlay' if have_statics else ''}...")
    mf = open(map_path, "rb")
    sif = open(idx_path, "rb") if have_statics else None
    sdf = open(sta_path, "rb") if have_statics else None

    # Build the image row by row. We produce a full-res RGB image; ~75MB+ raw,
    # PNG-compressed far smaller. We stream rows into zlib to keep memory sane.
    width, height = MAP_W_TILES, MAP_H_TILES  # derived above
    raw = bytearray()  # PNG scanlines (filter byte + RGB row)

    # For speed, read one horizontal STRIP of blocks (8 tile-rows) at a time.
    # Blocks are stored column-major: block index = bx * MAP_H_BLOCKS + by.
    # (UO map .mul layout: blocks go down each column, then next column.)
    for by in range(MAP_H_BLOCKS):  # MAP_H_BLOCKS is module-level (512)
        # 8 pixel rows for this block-row
        rows = [bytearray() for _ in range(8)]
        for bx in range(MAP_W_BLOCKS):
            block_index = bx * MAP_H_BLOCKS + by
            mf.seek(block_index * BLOCK_BYTES + 4)  # skip 4-byte header
            cells = mf.read(192)  # 64 tiles * 3 bytes

            # Per-cell static override: (cx,cy) -> (z, (r,g,b)) for the topmost
            # static whose radar color is non-transparent. Drawn over the land,
            # mirroring how the in-client radar layers statics above terrain.
            ov = None
            if have_statics:
                sif.seek(block_index * 12)
                lookup, length = struct.unpack("<ii", sif.read(8))  # skip 4-byte extra
                if lookup >= 0 and length > 0:
                    sdf.seek(lookup)
                    sdata = sdf.read(length)
                    ov = {}
                    for o in range(0, (length // STATIC_BYTES) * STATIC_BYTES, STATIC_BYTES):
                        iid = sdata[o] | (sdata[o + 1] << 8)
                        ridx = STATIC_RADAR_OFFSET + iid
                        if ridx >= pal_n:
                            continue
                        col = pal[ridx]
                        if col == (0, 0, 0):   # transparent on radar — don't draw
                            continue
                        cx = sdata[o + 2] & 7
                        cy = sdata[o + 3] & 7
                        z = sdata[o + 4]
                        if z >= 128:           # sbyte
                            z -= 256
                        key = (cx, cy)
                        prev = ov.get(key)
                        if prev is None or z >= prev[0]:
                            ov[key] = (z, col)

            for cy in range(8):
                row = rows[cy]
                base = cy * 8 * 3
                for cx in range(8):
                    cell = ov.get((cx, cy)) if ov else None
                    if cell is not None:
                        row += bytes(cell[1])
                    else:
                        off = base + cx * 3
                        tid = cells[off] | (cells[off + 1] << 8)
                        col = pal[tid] if tid < pal_n else (0, 0, 0)
                        row += bytes(col)
        # emit the 8 scanlines for this block-row, each prefixed with filter 0
        for cy in range(8):
            raw += b"\x00" + bytes(rows[cy])
        if by % 32 == 0:
            print(f"  row-block {by}/{MAP_H_BLOCKS}")

    mf.close()
    if sif: sif.close()
    if sdf: sdf.close()
    print("Compressing PNG...")
    write_png(OUT_PNG, width, height, bytes(raw))
    print(f"Wrote {OUT_PNG}  ({os.path.getsize(OUT_PNG)//1024} KB)")

def write_png(path, w, h, raw_scanlines):
    """Minimal PNG writer (RGB, 8-bit, no Pillow)."""
    def chunk(typ, data):
        c = struct.pack(">I", len(data)) + typ + data
        c += struct.pack(">I", zlib.crc32(typ + data) & 0xFFFFFFFF)
        return c
    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)  # 8-bit, color type 2 = RGB
    idat = zlib.compress(raw_scanlines, 6)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(sig)
        f.write(chunk(b"IHDR", ihdr))
        f.write(chunk(b"IDAT", idat))
        f.write(chunk(b"IEND", b""))

if __name__ == "__main__":
    render()
