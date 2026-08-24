"""Audit the dungeon waypoint graph in the live Distribution data. Read-only."""
import json, io, os, math, collections, sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

DATA = os.path.expanduser("~/uo-modernuo/ModernUO/Distribution/Data")
WPP = os.path.join(DATA, "Waypoints", "waypoints.json")
DESTP = os.path.join(DATA, "Destinations", "destinations.json")
REGP = os.path.join(DATA, "regions.json")

W = json.load(io.open(WPP, encoding="utf-8-sig"))["Waypoints"]
D = json.load(io.open(DESTP, encoding="utf-8-sig"))
regions = json.load(io.open(REGP, encoding="utf-8-sig"))

dests = D["Destinations"] if isinstance(D, dict) and "Destinations" in D else D

# ---------------------------------------------------------------- regions
DUNG = {}
for r in regions:
    if r.get("$type") == "DungeonRegion" and r.get("Map") == "Felucca":
        DUNG[r["Name"]] = [(a["x1"], a["y1"], a["x2"], a["y2"]) for a in r.get("Area", [])]


def region_of(x, y):
    for nm, rects in DUNG.items():
        for (x1, y1, x2, y2) in rects:
            if min(x1, x2) <= x <= max(x1, x2) and min(y1, y2) <= y <= max(y1, y2):
                return nm
    return None


byname = {}
dupes = collections.Counter()
for w in W:
    dupes[w["Name"]] += 1
    byname.setdefault(w["Name"], w)

dung_wp = []
for w in W:
    rn = region_of(w["X"], w["Y"])
    if rn:
        w["_region"] = rn
        dung_wp.append(w)

print("total waypoints      :", len(W))
print("inside DungeonRegions:", len(dung_wp))
print()
cnt = collections.Counter(w["_region"] for w in dung_wp)
for k, v in cnt.most_common():
    print("  %-22s %4d" % (k, v))

# ---------------------------------------------------------------- graph
DUNGSET = {id(w) for w in dung_wp}
names = set(byname)

edges = collections.defaultdict(set)
dangling = []
selfedge = []
for w in W:
    for c in w.get("Connects") or []:
        if c == w["Name"]:
            selfedge.append(w["Name"])
            continue
        if c not in names:
            dangling.append((w["Name"], c))
            continue
        edges[w["Name"]].add(c)

# asymmetry
asym = []
for a, bs in edges.items():
    for b in bs:
        if a not in edges.get(b, ()):
            asym.append((a, b))

# long edges (WaypointGraph caps legs at Euclidean 38)
def dist(a, b):
    return math.hypot(a["X"] - b["X"], a["Y"] - b["Y"])

longe, zjump = [], []
for a, bs in edges.items():
    wa = byname[a]
    for b in bs:
        if a > b:
            continue
        wb = byname[b]
        d = dist(wa, wb)
        if d > 38:
            longe.append((round(d, 1), a, b))
        if abs(wa["Z"] - wb["Z"]) > 20:
            zjump.append((abs(wa["Z"] - wb["Z"]), a, b))

# orphans / components (dungeon-scoped)
dnames = {w["Name"] for w in dung_wp}
orph = [n for n in dnames if not edges.get(n)]

seen, comps = set(), []
for n in dnames:
    if n in seen:
        continue
    stack, comp = [n], []
    seen.add(n)
    while stack:
        cur = stack.pop()
        comp.append(cur)
        for nb in edges.get(cur, ()):
            if nb not in seen:
                seen.add(nb)
                stack.append(nb)
    comps.append(comp)
comps.sort(key=len)

# coincident / very close pairs inside dungeons
grid = collections.defaultdict(list)
for w in dung_wp:
    grid[(w["X"] // 8, w["Y"] // 8)].append(w)
close = []
for (gx, gy), lst in grid.items():
    cand = []
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            cand.extend(grid.get((gx + dx, gy + dy), []))
    for i, a in enumerate(lst):
        for b in cand:
            if a["Name"] >= b["Name"]:
                continue
            if abs(a["X"] - b["X"]) <= 3 and abs(a["Y"] - b["Y"]) <= 3 and abs(a["Z"] - b["Z"]) <= 5:
                close.append((round(dist(a, b), 1), a["Name"], b["Name"],
                              a["X"], a["Y"], a["Z"], b["Z"], a["_region"]))
close.sort()

print()
print("== integrity ==")
print("duplicate names        :", sum(1 for k, v in dupes.items() if v > 1),
      [k for k, v in dupes.items() if v > 1][:8])
print("dangling Connects      :", len(dangling), dangling[:6])
print("self-edges             :", len(selfedge), selfedge[:6])
print("one-way (asymmetric)   :", len(asym), asym[:6])
print("edges > 38 Euclid      :", len(longe))
for e in sorted(longe, reverse=True)[:15]:
    print("     ", e)
print("edges |dz| > 20        :", len(zjump), sorted(zjump, reverse=True)[:5])
print()
print("== dungeon graph ==")
print("orphan dungeon wps (no edges):", len(orph), sorted(orph)[:12])
print("components (dungeon-touching):", len(comps))
small = [c for c in comps if len(c) <= 3]
print("  components of size <=3     :", len(small))
for c in small[:20]:
    w = byname[c[0]]
    print("     size %d  %-40s %s (%d,%d,%d)" % (len(c), c[0], w.get("_region"), w["X"], w["Y"], w["Z"]))
print("  size histogram:", collections.Counter(len(c) for c in comps).most_common(12))
print()
print("== near-duplicate waypoints (<=3 tiles apart, same Z band) ==")
print("count:", len(close))
for c in close[:25]:
    print("   ", c)
