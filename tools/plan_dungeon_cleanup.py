"""Full dungeon-waypoint cleanup plan (still read-only; writes a plan file).

  A1 merge exact-coordinate duplicates
  A5 collapse tight seed cliques (blob of nodes inside a <=3x3 box, same Z band)
  A2 drop self-edges
  A3 drop dungeon edges over the engine's 38-tile leg cap
  A4 delete zero-degree dungeon waypoints nothing references
  A6 symmetrise dungeon-internal edges on disk
"""
import json, io, os, sys, math, collections

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
DATA = os.path.expanduser("~/uo-modernuo/ModernUO/Distribution/Data")
HERE = os.path.dirname(os.path.abspath(__file__))
MAXLEG = 38
BOX = 3          # collapse only blobs whose bounding box is <= BOX tiles
ZBAND = 2

W = json.load(io.open(os.path.join(DATA, "Waypoints", "waypoints.json"), encoding="utf-8-sig"))["Waypoints"]
ddoc = json.load(io.open(os.path.join(DATA, "Destinations", "destinations.json"), encoding="utf-8-sig"))
regions = json.load(io.open(os.path.join(DATA, "regions.json"), encoding="utf-8-sig"))
dests = ddoc["Destinations"]

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

byname = {w["Name"]: w for w in W}
for w in W:
    w["_region"] = region_of(w["X"], w["Y"])

refs = collections.Counter()
for d in dests:
    if d.get("NearestWaypoint"):
        refs[d["NearestWaypoint"]] += 1
    for a in d.get("Arrivals") or []:
        for n in a.get("Waypoints") or []:
            refs[n] += 1

deg = collections.Counter()
adj = collections.defaultdict(set)
for w in W:
    for c in w.get("Connects") or []:
        if c in byname and c != w["Name"]:
            adj[w["Name"]].add(c)
            adj[c].add(w["Name"])
for k, v in adj.items():
    deg[k] = len(v)

dung = [w for w in W if w["_region"]]

# ---------------------------------------------------------------- clusters
# union-find over dungeon nodes that sit within BOX-1 tiles and same Z band
parent = {w["Name"]: w["Name"] for w in dung}
def find(a):
    while parent[a] != a:
        parent[a] = parent[parent[a]]
        a = parent[a]
    return a
def union(a, b):
    ra, rb = find(a), find(b)
    if ra != rb:
        parent[rb] = ra

# SAFETY RULE: only union nodes that ALREADY have an edge between them.
# An existing edge was line-of-walk generated and survived the 8/3-4 engine
# audit, so collapsing its endpoints cannot invent a path through a wall.
# (A pure proximity union can merge two rooms either side of a 1-tile wall,
#  or a stair landing stacked above a floor - the Z band does not catch that.)
# RULE 1: exact same tile AND exact same Z is provably the same place - no
# wall can separate a tile from itself - so always union those, edge or not.
# (Parallel duplicate corridors laid down by repeated splice passes never have
#  an edge between them, so the edge rule below would miss every one of them.)
exact = collections.defaultdict(list)
for w in dung:
    exact[(w["X"], w["Y"], w["Z"])].append(w["Name"])
n_exact = 0
for names in exact.values():
    for other in names[1:]:
        union(names[0], other)
        n_exact += 1
print("   exact-coordinate duplicates unioned: %d" % n_exact)

# RULE 2: near-co-located, but only when an edge ALREADY joins them.
dungset = {w["Name"] for w in dung}
for a in dungset:
    for b in adj.get(a, ()):
        if b not in dungset or a >= b:
            continue
        wa, wb = byname[a], byname[b]
        if abs(wa["X"] - wb["X"]) <= BOX - 1 and abs(wa["Y"] - wb["Y"]) <= BOX - 1            and abs(wa["Z"] - wb["Z"]) <= ZBAND:
            union(a, b)

clusters = collections.defaultdict(list)
for w in dung:
    clusters[find(w["Name"])].append(w["Name"])

merge = {}
collapsed_stats = collections.Counter()
skipped_chain = 0
for root, names in clusters.items():
    if len(names) < 2:
        continue
    xs = [byname[n]["X"] for n in names]
    ys = [byname[n]["Y"] for n in names]
    zs = [byname[n]["Z"] for n in names]
    if (max(xs) - min(xs)) > BOX or (max(ys) - min(ys)) > BOX or (max(zs) - min(zs)) > ZBAND:
        # a chain / long smear rather than a blob. Still collapse any exact
        # co-locations inside it, but leave the spread-out members alone.
        skipped_chain += 1
        sub = collections.defaultdict(list)
        for n in names:
            sub[(byname[n]["X"], byname[n]["Y"], byname[n]["Z"])].append(n)
        for grp in sub.values():
            if len(grp) < 2:
                continue
            c = sorted(grp, key=lambda n: (-refs[n], -deg[n], len(n), n))[0]
            for n in grp:
                if n != c:
                    merge[n] = c
            collapsed_stats[byname[c]["_region"]] += len(grp) - 1
        continue
    canon = sorted(names, key=lambda n: (-refs[n], -deg[n], len(n), n))[0]
    for n in names:
        if n != canon:
            merge[n] = canon
    collapsed_stats[byname[canon]["_region"]] += len(names) - 1

print("A1+A5 collapse co-located / tight-blob nodes")
print("   blobs collapsed      : %d" % sum(1 for r, n in clusters.items()
                                           if len(n) > 1 and any(x in merge for x in n)))
print("   nodes removed        : %d" % len(merge))
print("   smears left alone    : %d (bounding box > %d tiles - a corridor, not a blob)"
      % (skipped_chain, BOX))
for k, v in collapsed_stats.most_common():
    print("      %-16s %3d" % (k, v))

# guard: a collapse must not push any surviving edge past the engine leg cap
def _resolve(n, m):
    seen = set()
    while n in m and n not in seen:
        seen.add(n)
        n = m[n]
    return n

while True:
    bad = set()
    for a in adj:
        for b in adj[a]:
            ra, rb = _resolve(a, merge), _resolve(b, merge)
            if ra == rb:
                continue
            if math.hypot(byname[ra]["X"] - byname[rb]["X"],
                          byname[ra]["Y"] - byname[rb]["Y"]) > MAXLEG:
                for n in (a, b):
                    r = _resolve(n, merge)
                    if n != r:
                        bad.add(n)
    if not bad:
        break
    for n in bad:
        merge.pop(n, None)
    print("   backed off %d collapses that would break the %d-tile leg cap" % (len(bad), MAXLEG))

def resolve(n):
    seen = set()
    while n in merge and n not in seen:
        seen.add(n)
        n = merge[n]
    return n

# ------------------------------------------------------- rebuild the graph
kept = [w for w in W if w["Name"] not in merge]
conn = {w["Name"]: set() for w in kept}
selfedges = []
for w in W:
    src = resolve(w["Name"])
    for c in w.get("Connects") or []:
        if c == w["Name"]:
            selfedges.append(w["Name"])
            continue
        if c not in byname:
            continue
        dst = resolve(c)
        if dst == src:
            continue
        conn[src].add(dst)
        conn[dst].add(src)

print()
print("A2 self-edges dropped   : %d %s" % (len(set(selfedges)), sorted(set(selfedges))))

def dist(a, b):
    return math.hypot(a["X"] - b["X"], a["Y"] - b["Y"])

long_d, long_w = [], []
for a in list(conn):
    for b in list(conn[a]):
        if a >= b:
            continue
        d = dist(byname[a], byname[b])
        if d > MAXLEG:
            if byname[a]["_region"] and byname[b]["_region"]:
                long_d.append([a, b, round(d, 1)])
            else:
                long_w.append([a, b, round(d, 1)])
for a, b, _ in long_d:
    conn[a].discard(b)
    conn[b].discard(a)
print("A3 dungeon edges > %d dropped : %d %s" % (MAXLEG, len(long_d), long_d))
print("   world edges > %d (left alone, reported) : %d %s" % (MAXLEG, len(long_w), long_w))

# refs must be counted against the SURVIVING node: a canonical node whose only
# references came through a merged-away alias is still referenced.
rrefs = collections.Counter()
for n, c in refs.items():
    rrefs[resolve(n)] += c
orph = [w["Name"] for w in kept if w["_region"] and not conn[w["Name"]]]
orph_del = [n for n in orph if not rrefs[n]]
orph_keep = [n for n in orph if rrefs[n]]
for n in orph_del:
    conn.pop(n, None)
print()
print("A4 zero-degree dungeon wps : %d  -> delete %d, keep %d (referenced by a destination)"
      % (len(orph), len(orph_del), len(orph_keep)))
print("   kept:", sorted(orph_keep))

# --------------------------------------------------------------- results
final = [w for w in kept if w["Name"] not in set(orph_del)]
dn = [w["Name"] for w in final if w["_region"]]
seen, comps = set(), []
for n in dn:
    if n in seen:
        continue
    st, comp = [n], []
    seen.add(n)
    while st:
        cur = st.pop()
        comp.append(cur)
        for nb in conn.get(cur, ()):
            if nb not in seen:
                seen.add(nb)
                st.append(nb)
    comps.append(comp)

before_edges = sum(len(v) for v in adj.values()) // 2
after_edges = sum(len(v) for v in conn.values()) // 2
print()
print("=== totals ===")
print("waypoints  : %d -> %d   (dungeon %d -> %d)"
      % (len(W), len(final), len(dung), len(dn)))
print("edges      : %d -> %d" % (before_edges, after_edges))
print("dungeon components : 176 -> %d" % len(comps))
print("   histogram:", collections.Counter(len(c) for c in comps).most_common(8))

json.dump({"merge": merge,
           "self_edges": sorted(set(selfedges)),
           "long_edge_drop": long_d,
           "long_edge_world_reported": long_w,
           "orphan_delete": sorted(orph_del),
           "orphan_kept_referenced": sorted(orph_keep)},
          io.open(os.path.join(HERE, "dungeon_cleanup_plan.json"), "w", encoding="utf-8"), indent=1)
print()
print("wrote dungeon_cleanup_plan.json")
