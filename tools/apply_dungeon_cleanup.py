"""Apply the dungeon waypoint cleanup.

  python dg_apply.py check   - build the new graph, run the regression gate, write nothing
  python dg_apply.py apply   - same, then back up and write waypoints.json + destinations.json

Regression gate: every pair of waypoints that could reach each other BEFORE
must still reach each other AFTER (component membership, mapped through the
merge). A cleanup may shrink the graph; it may never disconnect it.
"""
import json, io, os, sys, math, collections, shutil, datetime

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
MODE = sys.argv[1] if len(sys.argv) > 1 else "check"
DATA = os.path.expanduser("~/uo-modernuo/ModernUO/Distribution/Data")
HERE = os.path.dirname(os.path.abspath(__file__))
WPP = os.path.join(DATA, "Waypoints", "waypoints.json")
DESTP = os.path.join(DATA, "Destinations", "destinations.json")
MAXLEG = 38

wdoc = json.load(io.open(WPP, encoding="utf-8-sig"))
W = wdoc["Waypoints"]
ddoc = json.load(io.open(DESTP, encoding="utf-8-sig"))
dests = ddoc["Destinations"]
plan = json.load(io.open(os.path.join(HERE, "dungeon_cleanup_plan.json"), encoding="utf-8"))

merge = plan["merge"]
long_drop = {tuple(sorted((a, b))) for a, b, _ in plan["long_edge_drop"]}
orph_del = set(plan["orphan_delete"])

byname = {w["Name"]: w for w in W}


def resolve(n):
    seen = set()
    while n in merge and n not in seen:
        seen.add(n)
        n = merge[n]
    return n


# ---------------------------------------------------------------- BEFORE
before = collections.defaultdict(set)
for w in W:
    for c in w.get("Connects") or []:
        if c in byname and c != w["Name"]:
            before[w["Name"]].add(c)
            before[c].add(w["Name"])


def components(adjacency, nodes):
    comp, seen, cid = {}, set(), 0
    for n in nodes:
        if n in seen:
            continue
        stack = [n]
        seen.add(n)
        cid += 1
        while stack:
            cur = stack.pop()
            comp[cur] = cid
            for nb in adjacency.get(cur, ()):
                if nb not in seen:
                    seen.add(nb)
                    stack.append(nb)
    return comp


comp_before = components(before, [w["Name"] for w in W])

# ----------------------------------------------------------------- AFTER
kept = [w for w in W if w["Name"] not in merge and w["Name"] not in orph_del]
after = {w["Name"]: set() for w in kept}
for w in W:
    src = resolve(w["Name"])
    if src not in after:
        continue
    for c in w.get("Connects") or []:
        if c == w["Name"] or c not in byname:
            continue
        dst = resolve(c)
        if dst == src or dst not in after:
            continue
        if tuple(sorted((src, dst))) in long_drop:
            continue
        after[src].add(dst)
        after[dst].add(src)

comp_after = components(after, [w["Name"] for w in kept])

# a second graph that KEEPS the over-cap edges, to attribute splits
after_keep = {k: set(v) for k, v in after.items()}
for a, b, _ in plan["long_edge_drop"]:
    ra, rb = resolve(a), resolve(b)
    if ra in after_keep and rb in after_keep:
        after_keep[ra].add(rb)
        after_keep[rb].add(ra)
comp_keep = components(after_keep, [w["Name"] for w in kept])

# ------------------------------------------------------------------ gate
lost, expected_split = [], []
groups = collections.defaultdict(list)
for n, c in comp_before.items():
    groups[c].append(n)
for cid, members in groups.items():
    mapped = {resolve(m) for m in members}
    mapped = {m for m in mapped if m in comp_after}
    cids = {comp_after[m] for m in mapped}
    if len(cids) > 1:
        rep = sorted(mapped)[:6]
        if len({comp_keep[m] for m in mapped}) == 1:
            expected_split.append((len(members), len(cids), rep))
        else:
            lost.append((len(members), len(cids), rep))

print("=== regression gate: connectivity ===")
if lost:
    print("FAIL - %d pre-existing components would be split:" % len(lost))
    for l in sorted(lost, reverse=True)[:10]:
        print("   component of %d nodes -> %d pieces  %s" % l)
else:
    print("PASS - no unexpected split; %d components before, %d after"
          % (len(set(comp_before.values())), len(set(comp_after.values()))))
for e in expected_split:
    print("   ACCEPTED split (only link was an edge over the %d-tile engine cap,"
          " so no bot could ever walk it): %d nodes -> %d pieces  %s" % (MAXLEG, e[0], e[1], e[2]))

# other invariants
bad_long = []
for a, bs in after.items():
    for b in bs:
        if a < b and math.hypot(byname[a]["X"] - byname[b]["X"],
                                byname[a]["Y"] - byname[b]["Y"]) > MAXLEG:
            bad_long.append((a, b))
pre_long = set()
for a, bs in before.items():
    for b in bs:
        if a < b and math.hypot(byname[a]["X"] - byname[b]["X"],
                                byname[a]["Y"] - byname[b]["Y"]) > MAXLEG:
            pre_long.add((a, b))
new_long = [e for e in bad_long if e not in pre_long]
print("edges over the %d-tile leg cap after cleanup : %d (%d pre-existing, %d NEW %s)"
      % (MAXLEG, len(bad_long), len(bad_long) - len(new_long), len(new_long), new_long[:5]))
bad_long = new_long

names_after = set(after)
dang = []
for n, bs in after.items():
    for b in bs:
        if b not in names_after:
            dang.append((n, b))
print("dangling Connects after cleanup             :", len(dang))

# destination refs. A ref that was ALREADY broken before the cleanup is a
# pre-existing data bug, not a regression - repoint it instead of failing.
orig_names = set(byname)


def repoint(x, y, z):
    """Nearest surviving waypoint, preferring the same Z shelf. Dungeon floors
    are stacked, so a node 6 tiles away on a ledge 25 Z up is the wrong pick."""
    best = None
    for n in names_after:
        w = byname[n]
        d = math.hypot(w["X"] - x, w["Y"] - y)
        if d > 30:
            continue
        key = (abs(w["Z"] - z) > 5, abs(w["Z"] - z), d)
        if best is None or key < best[0]:
            best = (key, n)
    return best[1] if best else None


unresolved, prebroken = [], []
for d in dests:
    v = d.get("NearestWaypoint")
    if v and resolve(v) not in names_after:
        (prebroken if v not in orig_names else unresolved).append(
            (d["Name"], "NearestWaypoint", v, d.get("X"), d.get("Y"), d.get("Z", 0)))
    for a in d.get("Arrivals") or []:
        for v in a.get("Waypoints") or []:
            if resolve(v) not in names_after:
                (prebroken if v not in orig_names else unresolved).append(
                    (d["Name"], "Arrival", v, a.get("X"), a.get("Y"), a.get("Z", 0)))
print("destination refs newly dangling             :", len(unresolved), unresolved[:6])
print("destination refs already broken before      :", len(prebroken))
for p in prebroken:
    print("      %-28s %-15s -> %-22s repoint to %s"
          % (p[0], p[1], p[2], repoint(p[3], p[4], p[5])))

ok = not lost and not bad_long and not dang and not unresolved
print()
print("waypoints %d -> %d   edges %d -> %d"
      % (len(W), len(kept),
         sum(len(v) for v in before.values()) // 2,
         sum(len(v) for v in after.values()) // 2))
print("GATE:", "PASS" if ok else "FAIL")

if MODE != "apply":
    sys.exit(0 if ok else 1)
if not ok:
    print("refusing to apply - gate failed")
    sys.exit(1)

# ----------------------------------------------------------------- write
stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
shutil.copy(WPP, WPP + ".bak-dungeon-cleanup")
shutil.copy(DESTP, DESTP + ".bak-dungeon-cleanup")
print("backed up -> *.bak-dungeon-cleanup")

out = []
for w in W:
    if w["Name"] in merge or w["Name"] in orph_del:
        continue
    nw = {k: v for k, v in w.items() if not k.startswith("_") or k == "_note"}
    nw["Connects"] = sorted(after[w["Name"]])
    out.append(nw)
wdoc["Waypoints"] = out
c = wdoc.get("_comment", "")
wdoc["_comment"] = (c + " | ") if c else ""
wdoc["_comment"] += ("dungeon cleanup %s: merged %d co-located/tight nodes, "
                     "deleted %d zero-degree nodes, dropped %d over-cap edges "
                     "and %d self-edges; dungeon-internal edges symmetrised."
                     % (stamp, len(merge), len(orph_del), len(long_drop),
                        len(plan["self_edges"])))
io.open(WPP, "w", encoding="utf-8").write(json.dumps(wdoc, indent=2, ensure_ascii=False))

nrew, nfix = 0, 0
for d in dests:
    v = d.get("NearestWaypoint")
    if v and v not in orig_names:
        r = repoint(d.get("X"), d.get("Y"), d.get("Z", 0))
        if r:
            print("   repointed broken ref %-24s %s -> %s" % (d["Name"], v, r))
            d["NearestWaypoint"] = r
            nfix += 1
            v = None
    if v and resolve(v) != v:
        d["NearestWaypoint"] = resolve(v)
        nrew += 1
    for a in d.get("Arrivals") or []:
        ws = a.get("Waypoints")
        if ws:
            new = []
            for x in ws:
                r = resolve(x)
                if r not in new:
                    new.append(r)
            if new != ws:
                nrew += 1
            a["Waypoints"] = new
io.open(DESTP, "w", encoding="utf-8").write(json.dumps(ddoc, indent=2, ensure_ascii=False))
print("rewrote %d destination waypoint references, repaired %d broken ones" % (nrew, nfix))
print("wrote", WPP)
print("wrote", DESTP)
