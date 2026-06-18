#!/usr/bin/env python3
"""
Map server v2: live data + ZONE EDITING.
  GET  /map.html, /uomap.png      static viewer
  GET  /mapdata.json              live dests/wps/edges/zones from game files
  POST /zones                     save the full zones list -> zones.json
                                  (written into the game's Data folder so
                                   [ReloadZones picks it up)
Run:  python3 ~/uo-map/serve_map.py    open http://localhost:8777/map.html
"""
import json, os
from http.server import HTTPServer, SimpleHTTPRequestHandler

MAP_DIR = os.path.expanduser("~/uo-map")
DEST_JSON = os.path.expanduser(
    "~/uo-modernuo/ModernUO/Distribution/Data/Destinations/destinations.json")
GEN_JSON = os.path.expanduser(
    "~/uo-modernuo/ModernUO/Distribution/Data/Destinations/destinations_generated.json")
WP_JSON = os.path.expanduser(
    "~/uo-modernuo/ModernUO/Distribution/Data/Waypoints/waypoints.json")
ZONES_JSON = os.path.expanduser(
    "~/uo-modernuo/ModernUO/Distribution/Data/Zones/zones.json")
SPAWNS_JSON = os.path.expanduser(
    "~/uo-modernuo/ModernUO/Distribution/Data/CustomSpawns/spawns.json")
# Phase 2 live view: snapshot written by the game's [LiveMap command.
LIVE_JSON = os.path.expanduser(
    "~/uo-modernuo/ModernUO/Distribution/Data/Live/entities.json")
# "Reload in game" bridge: editor bumps a token, EditorReloadWatcher acts on it.
RELOAD_REQ = os.path.expanduser(
    "~/uo-modernuo/ModernUO/Distribution/Data/Live/reload_request.txt")
RELOAD_ACK = os.path.expanduser(
    "~/uo-modernuo/ModernUO/Distribution/Data/Live/reload_ack.json")
# "Regenerate bots" bridge (= [GenerateBots): re-lay the whole bot population.
GENBOTS_REQ = os.path.expanduser(
    "~/uo-modernuo/ModernUO/Distribution/Data/Live/genbots_request.txt")
GENBOTS_ACK = os.path.expanduser(
    "~/uo-modernuo/ModernUO/Distribution/Data/Live/genbots_ack.json")

# Valid Kind values for a spawn record (drives generator type + filter layer).
SPAWN_KINDS = {"Monster", "NPC", "Vendor", "PlayerBotFixed", "PlayerBotLifecycle"}

# UO client data dir (for map0.mul: true Z + road-tile snapping).
UO_DIR = os.path.expanduser("~/uo-modernuo/UOData/7.0.23.1")

def jload(p): return json.loads(open(p, encoding="utf-8-sig").read())

# ---- map0.mul land access ----------------------------------------------------
import struct
_map_f = None
_map_wb = None
def _map_open():
    global _map_f, _map_wb
    if _map_f is None:
        p = os.path.join(UO_DIR, "map0.mul")
        _map_f = open(p, "rb")
        _map_wb = os.path.getsize(p) // (196 * 512)
    return _map_f

def land_at(x, y):
    """(tileID, z) of the land tile at x,y."""
    f = _map_open()
    block = (x // 8) * 512 + (y // 8)
    cell = (y % 8) * 8 + (x % 8)
    f.seek(block * 196 + 4 + cell * 3)
    b = f.read(3)
    tid = b[0] | (b[1] << 8)
    z = b[2] - 256 if b[2] > 127 else b[2]
    return tid, z

def wp_file():
    w = jload(WP_JSON)
    key = next(k for k, v in w.items()
               if isinstance(v, list) and v and isinstance(v[0], dict) and "Connects" in v[0])
    return w, key

def save_wp(w):
    import shutil
    shutil.copy(WP_JSON, WP_JSON + ".bak-mapedit")
    open(WP_JSON, "w", encoding="utf-8").write(json.dumps(w, indent=2))

def _arrivals_view(d):
    """Read-only arrivals list for mapdata: real Arrivals[] if present, else
    a synthesized single from legacy flat fields. Does NOT modify d."""
    arr = d.get("Arrivals")
    if isinstance(arr, list):
        return [{"x": a.get("X"), "y": a.get("Y"), "z": a.get("Z"),
                 "wps": a.get("Waypoints", [])} for a in arr]
    if d.get("ArrivalX") is not None and d.get("ArrivalY") is not None:
        wps = []
        if d.get("NearestWaypoint"): wps = [d["NearestWaypoint"]]
        return [{"x": d["ArrivalX"], "y": d["ArrivalY"],
                 "z": d.get("ArrivalZ"), "wps": wps}]
    return []

def ensure_arrivals(hit, z_default=0):
    """Return hit['Arrivals'] as a list, migrating legacy flat ArrivalX/Y/Z
    (+ NearestWaypoint) into a single spot the first time. Option-2 shape:
    every arrival lives in Arrivals[]."""
    arr = hit.get("Arrivals")
    if isinstance(arr, list):
        return arr
    arr = []
    if hit.get("ArrivalX") is not None and hit.get("ArrivalY") is not None:
        spot = {"X": hit["ArrivalX"], "Y": hit["ArrivalY"],
                "Z": hit.get("ArrivalZ", z_default), "Waypoints": []}
        nw = hit.get("NearestWaypoint")
        if nw: spot["Waypoints"].append(nw)
        arr.append(spot)
    for k in ("ArrivalX", "ArrivalY", "ArrivalZ"):
        hit.pop(k, None)
    hit["Arrivals"] = arr
    return arr

def road_allowlist(nodes):
    """Tile IDs under existing waypoints = 'tiles waypoints live on'."""
    ids = set()
    for n in nodes:
        try: ids.add(land_at(int(n["X"]), int(n["Y"]))[0])
        except Exception: pass
    return ids

def snap_to_road(x, y, allow):
    """Nearest allowed tile within 4 (euclidean-best), else None."""
    if land_at(x, y)[0] in allow: return x, y
    best, bd = None, 1e9
    for dx in range(-4, 5):
        for dy in range(-4, 5):
            if dx == 0 and dy == 0: continue
            try:
                if land_at(x + dx, y + dy)[0] in allow:
                    d = dx * dx + dy * dy
                    if d < bd: bd, best = d, (x + dx, y + dy)
            except Exception:
                pass
    return best

def road_flood_connect(x, y, allow, nodes, max_steps=38, max_links=4):
    """BFS along allowed road tiles; returns names of nodes reached, nearest first."""
    from collections import deque
    pos = {(int(n["X"]), int(n["Y"])): n["Name"] for n in nodes}
    seen = {(x, y)}
    q = deque([(x, y, 0)])
    found = []
    while q:
        cx, cy, d = q.popleft()
        if (cx, cy) in pos and (cx, cy) != (x, y):
            found.append((pos[(cx, cy)], d))
        if d >= max_steps: continue
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                if dx == 0 and dy == 0: continue
                nx, ny = cx + dx, cy + dy
                if (nx, ny) in seen: continue
                seen.add((nx, ny))
                try:
                    if land_at(nx, ny)[0] in allow:
                        q.append((nx, ny, d + 1))
                except Exception:
                    pass
    found.sort(key=lambda t: t[1])
    return [n for n, _ in found[:max_links]]


def load_zones():
    if not os.path.exists(ZONES_JSON): return []
    try: return jload(ZONES_JSON).get("Zones", [])
    except Exception: return []

def save_zones(zones):
    os.makedirs(os.path.dirname(ZONES_JSON), exist_ok=True)
    if os.path.exists(ZONES_JSON):
        import shutil; shutil.copy(ZONES_JSON, ZONES_JSON + ".bak")
    open(ZONES_JSON, "w").write(json.dumps({"Zones": zones}, indent=2))

# ---- spawns (the spawn editor) ----------------------------------------------
def load_spawns():
    if not os.path.exists(SPAWNS_JSON): return []
    try: return jload(SPAWNS_JSON).get("Spawns", [])
    except Exception: return []

def save_spawns(spawns):
    os.makedirs(os.path.dirname(SPAWNS_JSON), exist_ok=True)
    if os.path.exists(SPAWNS_JSON):
        import shutil; shutil.copy(SPAWNS_JSON, SPAWNS_JSON + ".bak")
    open(SPAWNS_JSON, "w", encoding="utf-8").write(
        json.dumps({"Spawns": spawns}, indent=2))

def next_spawn_id(spawns):
    """Stable integer id, max existing + 1 (survives move/edit)."""
    return (max((int(s.get("Id", 0)) for s in spawns), default=0) + 1)

def build_data():
    dests, seen = [], set()
    for path, src in ((DEST_JSON, "active"), (GEN_JSON, "generated")):
        if not os.path.exists(path): continue
        for d in jload(path).get("Destinations", []):
            k = (d.get("Name") or "").lower()
            if k in seen: continue
            seen.add(k)
            dests.append({"n": d.get("Name"), "x": d.get("X"), "y": d.get("Y"),
                          "z": d.get("Z"), "t": d.get("Type"), "c": d.get("City"),
                          "w": d.get("NearestWaypoint"), "s": src,
                          "poly": d.get("Polygon"),
                          "arrivals": _arrivals_view(d)})
    wdata = jload(WP_JSON)
    key = next(k for k, v in wdata.items()
               if isinstance(v, list) and v and isinstance(v[0], dict) and "Connects" in v[0])
    wps, edges, done = [], [], set()
    for n in wdata[key]:
        wps.append({"n": n.get("Name"), "x": n.get("X"), "y": n.get("Y"),
                    "z": n.get("Z"), "ar": n.get("ArrivalRange", 0),
                    "co": n.get("Connects") or []})
    names = {w["n"] for w in wps}
    for w in wps:
        for c in w["co"]:
            if c in names and (c, w["n"]) not in done:
                edges.append([w["n"], c]); done.add((w["n"], c))
    return {"dests": dests, "wps": wps, "edges": edges,
            "zones": load_zones(), "spawns": load_spawns()}

class Handler(SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=MAP_DIR, **kw)
    def _json(self, code, obj):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def do_GET(self):
        if self.path.split("?")[0] == "/mapdata.json":
            try: self._json(200, build_data())
            except Exception as ex: self.send_error(500, str(ex))
            return
        if self.path.split("?")[0] == "/live.json":
            # The game's [LiveMap timer writes this; serve it fresh (no cache).
            # Empty/absent -> an empty snapshot so the editor polls happily.
            try:
                if os.path.exists(LIVE_JSON):
                    body = open(LIVE_JSON, "rb").read()
                else:
                    body = b'{"map":null,"count":0,"entities":[]}'
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Cache-Control", "no-store")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except Exception as ex:
                self.send_error(500, str(ex))
            return
        if self.path.split("?")[0] in ("/reload_status", "/genbots_status"):
            # The game's EditorReloadWatcher writes these acks.
            ack = RELOAD_ACK if self.path.split("?")[0] == "/reload_status" else GENBOTS_ACK
            try:
                body = open(ack, "rb").read() if os.path.exists(ack) else b"{}"
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Cache-Control", "no-store")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except Exception as ex:
                self.send_error(500, str(ex))
            return
        super().do_GET()
    def do_POST(self):
        if self.path.split("?")[0] == "/zone_dest":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                pts = [[int(a), int(b)] for a, b in p["points"]]
                if len(pts) < 3: raise ValueError("need >=3 points")
                d = jload(DEST_JSON)
                arr = d.get("Destinations", [])

                def inside(x, y):
                    ins = False
                    for i in range(len(pts)):
                        x1, y1 = pts[i]; x2, y2 = pts[i - 1]
                        if (y1 > y) != (y2 > y) and                            x < (x2 - x1) * (y - y1) / (y2 - y1) + x1:
                            ins = not ins
                    return ins

                hit = next((e for e in arr if inside(e.get("X", -1), e.get("Y", -1))), None)
                import shutil
                if hit is not None:
                    # ABSORB: the shape becomes this destination's outline.
                    hit["Polygon"] = pts
                    shutil.copy(DEST_JSON, DEST_JSON + ".bak-zonedest")
                    open(DEST_JSON, "w", encoding="utf-8").write(json.dumps(d, indent=2))
                    self._json(200, {"ok": True, "mode": "absorbed", "name": hit["Name"],
                                     "type": hit.get("Type")})
                    return
                # CREATE: new zone-destination.
                cx = round(sum(q[0] for q in pts) / len(pts))
                cy = round(sum(q[1] for q in pts) / len(pts))
                try: z = land_at(cx, cy)[1]
                except Exception: z = 0
                cities = [("Britain",1434,1690),("Vesper",2899,676),("Minoc",2466,437),
                          ("Trinsic",1900,2780),("Yew",632,858),("Skara Brae",596,2138),
                          ("Moonglow",4442,1172),("Jhelom",1383,3815),("Nujel'm",3732,1279),
                          ("Magincia",3714,2220),("Cove",2230,1200),("Buccaneer's Den",2706,2150)]
                city = min(cities, key=lambda c: (c[1]-cx)**2 + (c[2]-cy)**2)[0]
                w, key = wp_file()
                nodes = w[key]
                nw, nd = "", 10**9
                for nd_ in nodes:
                    dd = max(abs(int(nd_["X"]) - cx), abs(int(nd_["Y"]) - cy))
                    if dd < nd: nd, nw = dd, nd_["Name"]
                name = p.get("name") or f"{p.get('type','Spot')} at {cx},{cy}"
                if any((e.get("Name") or "").lower() == name.lower() for e in arr):
                    self._json(400, {"ok": False, "error": f"'{name}' already exists"}); return
                arr.append({"Name": name, "X": cx, "Y": cy, "Z": z,
                            "Type": p.get("type", "CityCenter"), "City": city,
                            "NearestWaypoint": nw, "Polygon": pts})
                shutil.copy(DEST_JSON, DEST_JSON + ".bak-zonedest")
                open(DEST_JSON, "w", encoding="utf-8").write(json.dumps(d, indent=2))
                self._json(200, {"ok": True, "mode": "created", "name": name,
                                 "x": cx, "y": cy, "z": z, "city": city,
                                 "nearest_wp": nw, "wp_dist": nd,
                                 "gap": nd > 38})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/dest_activate":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                name = p["name"]
                act = jload(DEST_JSON)
                if any((e.get("Name") or "").lower() == name.lower()
                       for e in act.get("Destinations", [])):
                    self._json(400, {"ok": False, "error": f"'{name}' already active"}); return
                gen = jload(GEN_JSON) if os.path.exists(GEN_JSON) else {"Destinations": []}
                rec = next((e for e in gen.get("Destinations", [])
                            if (e.get("Name") or "").lower() == name.lower()), None)
                if rec is None:
                    self._json(404, {"ok": False, "error": f"'{name}' not in generated catalog"}); return
                clean = {k: rec[k] for k in
                         ("Name", "X", "Y", "Z", "Type", "City", "NearestWaypoint")
                         if k in rec}
                act.setdefault("Destinations", []).append(clean)
                import shutil
                shutil.copy(DEST_JSON, DEST_JSON + ".bak-activate")
                open(DEST_JSON, "w", encoding="utf-8").write(json.dumps(act, indent=2))
                self._json(200, {"ok": True, "name": name,
                                 "active": len(act["Destinations"])})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/dest_deactivate":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                name = p["name"]
                act = jload(DEST_JSON)
                arr = act.get("Destinations", [])
                before = len(arr)
                arr[:] = [e for e in arr if (e.get("Name") or "").lower() != name.lower()]
                if len(arr) == before:
                    self._json(404, {"ok": False, "error": f"'{name}' not active"}); return
                import shutil
                shutil.copy(DEST_JSON, DEST_JSON + ".bak-deactivate")
                open(DEST_JSON, "w", encoding="utf-8").write(json.dumps(act, indent=2))
                self._json(200, {"ok": True, "name": name, "active": len(arr)})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/dest_arrival_add":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                name, x, y = p["name"], int(p["x"]), int(p["y"])
                d = jload(DEST_JSON)
                hit = next((e for e in d.get("Destinations", [])
                            if (e.get("Name") or "").lower() == name.lower()), None)
                if hit is None:
                    self._json(404, {"ok": False, "error": f"'{name}' not active"}); return
                # exact clicked tile (interior floors valid); land Z.
                try: az = land_at(x, y)[1]
                except Exception: az = hit.get("Z", 0)
                arr = ensure_arrivals(hit, hit.get("Z", 0))
                arr.append({"X": x, "Y": y, "Z": az, "Waypoints": []})
                import shutil
                shutil.copy(DEST_JSON, DEST_JSON + ".bak-arrival")
                open(DEST_JSON, "w", encoding="utf-8").write(json.dumps(d, indent=2))
                self._json(200, {"ok": True, "name": name, "index": len(arr) - 1,
                                 "x": x, "y": y, "z": az})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/dest_arrival_move":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                name, i, x, y = p["name"], int(p["index"]), int(p["x"]), int(p["y"])
                d = jload(DEST_JSON)
                hit = next((e for e in d.get("Destinations", [])
                            if (e.get("Name") or "").lower() == name.lower()), None)
                if hit is None: self._json(404, {"ok": False, "error": "dest not found"}); return
                arr = ensure_arrivals(hit, hit.get("Z", 0))
                if i < 0 or i >= len(arr): self._json(404, {"ok": False, "error": "bad index"}); return
                try: az = land_at(x, y)[1]
                except Exception: az = arr[i].get("Z", 0)
                arr[i]["X"], arr[i]["Y"], arr[i]["Z"] = x, y, az
                import shutil
                shutil.copy(DEST_JSON, DEST_JSON + ".bak-arrival")
                open(DEST_JSON, "w", encoding="utf-8").write(json.dumps(d, indent=2))
                self._json(200, {"ok": True, "name": name, "index": i, "x": x, "y": y, "z": az})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/dest_arrival_del":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                name, i = p["name"], int(p["index"])
                d = jload(DEST_JSON)
                hit = next((e for e in d.get("Destinations", [])
                            if (e.get("Name") or "").lower() == name.lower()), None)
                if hit is None: self._json(404, {"ok": False, "error": "dest not found"}); return
                arr = ensure_arrivals(hit, hit.get("Z", 0))
                if i < 0 or i >= len(arr): self._json(404, {"ok": False, "error": "bad index"}); return
                arr.pop(i)
                import shutil
                shutil.copy(DEST_JSON, DEST_JSON + ".bak-arrival")
                open(DEST_JSON, "w", encoding="utf-8").write(json.dumps(d, indent=2))
                self._json(200, {"ok": True, "name": name, "remaining": len(arr)})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/dest_arrival_wp":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                name, i, wp = p["name"], int(p["index"]), p["wp"]
                d = jload(DEST_JSON)
                hit = next((e for e in d.get("Destinations", [])
                            if (e.get("Name") or "").lower() == name.lower()), None)
                if hit is None: self._json(404, {"ok": False, "error": "dest not found"}); return
                w, key = wp_file()
                if not any((nd.get("Name") or "") == wp for nd in w[key]):
                    self._json(404, {"ok": False, "error": f"waypoint '{wp}' not found"}); return
                arr = ensure_arrivals(hit, hit.get("Z", 0))
                if i < 0 or i >= len(arr): self._json(404, {"ok": False, "error": "bad index"}); return
                wps = arr[i].setdefault("Waypoints", [])
                if wp in wps:
                    wps.remove(wp); action = "unlinked"
                else:
                    wps.append(wp); action = "linked"
                import shutil
                shutil.copy(DEST_JSON, DEST_JSON + ".bak-arrival")
                open(DEST_JSON, "w", encoding="utf-8").write(json.dumps(d, indent=2))
                self._json(200, {"ok": True, "name": name, "index": i, "wp": wp,
                                 "action": action, "waypoints": wps})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/dest_del":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                name = p["name"]
                d = jload(DEST_JSON)
                arr = d.get("Destinations", [])
                before = len(arr)
                arr[:] = [e for e in arr if (e.get("Name") or "").lower() != name.lower()]
                if len(arr) == before:
                    self._json(404, {"ok": False, "error": f"'{name}' not in active catalog"})
                    return
                import shutil
                shutil.copy(DEST_JSON, DEST_JSON + ".bak-del")
                open(DEST_JSON, "w", encoding="utf-8").write(json.dumps(d, indent=2))
                self._json(200, {"ok": True, "name": name, "remaining": len(arr)})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/dest_move":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                name, x, y = p["name"], int(p["x"]), int(p["y"])
                d = jload(DEST_JSON)
                hit = next((e for e in d.get("Destinations", [])
                            if (e.get("Name") or "").lower() == name.lower()), None)
                if hit is None:
                    self._json(404, {"ok": False, "error": f"'{name}' not in active catalog"})
                    return
                old_xy = (hit["X"], hit["Y"])
                hit["X"], hit["Y"] = x, y         # Z deliberately unchanged
                import shutil
                shutil.copy(DEST_JSON, DEST_JSON + ".bak-move")
                open(DEST_JSON, "w", encoding="utf-8").write(json.dumps(d, indent=2))
                self._json(200, {"ok": True, "name": name,
                                 "from": old_xy, "to": [x, y], "z": hit.get("Z")})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/wp_add":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                name, x, y = p["name"], int(p["x"]), int(p["y"])
                w, key = wp_file()
                nodes = w[key]
                if any((nd.get("Name") or "").lower() == name.lower() for nd in nodes):
                    self._json(400, {"ok": False, "error": f"'{name}' already exists"}); return
                allow = road_allowlist(nodes)
                snapped = snap_to_road(x, y, allow)
                if snapped is None:
                    self._json(400, {"ok": False,
                        "error": "not on a road tile (no waypoint-style tile within 4) — "
                                 "click ON the road, or [MarkWay in game for off-road"}); return
                sx, sy = snapped
                _, z = land_at(sx, sy)
                links = road_flood_connect(sx, sy, allow, nodes)
                nodes.append({"Name": name, "X": sx, "Y": sy, "Z": z, "Connects": links})
                save_wp(w)
                self._json(200, {"ok": True, "name": name, "x": sx, "y": sy, "z": z,
                                 "snapped": (sx, sy) != (x, y), "connects": links})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/wp_move":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                name, x, y = p["name"], int(p["x"]), int(p["y"])
                w, key = wp_file()
                nodes = w[key]
                hit = next((nd for nd in nodes
                            if (nd.get("Name") or "").lower() == name.lower()), None)
                if hit is None:
                    self._json(404, {"ok": False, "error": f"'{name}' not found"}); return
                allow = road_allowlist(nodes)
                snapped = snap_to_road(x, y, allow)
                if snapped is None:
                    self._json(400, {"ok": False, "error": "not on a road tile"}); return
                hit["X"], hit["Y"] = snapped
                hit["Z"] = land_at(*snapped)[1]
                save_wp(w)
                self._json(200, {"ok": True, "name": name,
                                 "x": snapped[0], "y": snapped[1], "z": hit["Z"]})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/wp_del":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                name = p["name"]
                w, key = wp_file()
                nodes = w[key]
                before = len(nodes)
                nodes[:] = [nd for nd in nodes
                            if (nd.get("Name") or "").lower() != name.lower()]
                if len(nodes) == before:
                    self._json(404, {"ok": False, "error": f"'{name}' not found"}); return
                for nd in nodes:
                    c = nd.get("Connects") or []
                    nd["Connects"] = [v for v in c if v.lower() != name.lower()]
                save_wp(w)
                self._json(200, {"ok": True, "name": name})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/wp_edge":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                a, b = p["a"], p["b"]
                w, key = wp_file()
                nodes = w[key]
                na = next((nd for nd in nodes if nd["Name"].lower() == a.lower()), None)
                nb = next((nd for nd in nodes if nd["Name"].lower() == b.lower()), None)
                if not na or not nb:
                    self._json(404, {"ok": False, "error": "node not found"}); return
                ca = na.setdefault("Connects", [])
                cb = nb.setdefault("Connects", [])
                linked = any(v.lower() == b.lower() for v in ca) or                          any(v.lower() == a.lower() for v in cb)
                if linked:   # sever both directions
                    na["Connects"] = [v for v in ca if v.lower() != b.lower()]
                    nb["Connects"] = [v for v in cb if v.lower() != a.lower()]
                    action = "severed"
                else:        # one-sided add; loader makes it bidirectional
                    ca.append(nb["Name"])
                    action = "linked"
                save_wp(w)
                self._json(200, {"ok": True, "action": action, "a": a, "b": b})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/zones":
            try:
                n = int(self.headers.get("Content-Length", 0))
                payload = json.loads(self.rfile.read(n))
                zones = payload.get("zones", [])
                # light validation: name + >=3 integer points
                for z in zones:
                    assert z.get("Name") and isinstance(z.get("Points"), list) \
                           and len(z["Points"]) >= 3, "bad zone"
                    z["Points"] = [[int(p[0]), int(p[1])] for p in z["Points"]]
                    z.setdefault("Kind", "Portal")
                save_zones(zones)
                self._json(200, {"ok": True, "count": len(zones)})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] in ("/reload", "/genbots"):
            # Bump a token; EditorReloadWatcher in-game acts on the change.
            #   /reload  -> reload waypoints + destinations(+arrivals) + zones
            #   /genbots -> re-lay the whole bot population ([GenerateBots)
            req = RELOAD_REQ if self.path.split("?")[0] == "/reload" else GENBOTS_REQ
            try:
                os.makedirs(os.path.dirname(req), exist_ok=True)
                tok = 0
                if os.path.exists(req):
                    try: tok = int((open(req).read().strip() or "0"))
                    except Exception: tok = 0
                tok += 1
                open(req, "w").write(str(tok))
                self._json(200, {"ok": True, "token": tok})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/spawn_add":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                kind = p.get("kind")
                if kind not in SPAWN_KINDS:
                    self._json(400, {"ok": False,
                        "error": f"bad kind '{kind}'"}); return
                x, y = int(p["x"]), int(p["y"])
                what = p.get("what", [])
                if isinstance(what, str): what = [what]
                what = [w for w in (s.strip() for s in what) if w]
                # bot-lifecycle seeds may legitimately have no 'what'
                if not what and kind != "PlayerBotLifecycle":
                    self._json(400, {"ok": False,
                        "error": "need at least one type/behavior"}); return
                try: z = land_at(x, y)[1]
                except Exception: z = int(p.get("z", 0))
                spawns = load_spawns()
                rec = {
                    "Id": next_spawn_id(spawns),
                    "Kind": kind,
                    "Map": p.get("map", "Felucca"),
                    "X": x, "Y": y, "Z": z,
                    "What": what,
                    "Count": max(1, int(p.get("count", 1))),
                    "Range": max(0, int(p.get("range", 5))),
                    "MinDelay": float(p.get("mindelay", 5)),
                    "MaxDelay": float(p.get("maxdelay", 15)),
                    "Source": "custom",
                }
                spawns.append(rec)
                save_spawns(spawns)
                self._json(200, {"ok": True, "spawn": rec})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/spawn_move":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                sid, x, y = int(p["id"]), int(p["x"]), int(p["y"])
                spawns = load_spawns()
                hit = next((s for s in spawns if int(s.get("Id", -1)) == sid), None)
                if hit is None:
                    self._json(404, {"ok": False, "error": "spawn not found"}); return
                try: z = land_at(x, y)[1]
                except Exception: z = hit.get("Z", 0)
                hit["X"], hit["Y"], hit["Z"] = x, y, z
                save_spawns(spawns)
                self._json(200, {"ok": True, "id": sid, "x": x, "y": y, "z": z})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/spawn_del":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                sid = int(p["id"])
                spawns = load_spawns()
                before = len(spawns)
                spawns[:] = [s for s in spawns if int(s.get("Id", -1)) != sid]
                if len(spawns) == before:
                    self._json(404, {"ok": False, "error": "spawn not found"}); return
                save_spawns(spawns)
                self._json(200, {"ok": True, "id": sid, "remaining": len(spawns)})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        if self.path.split("?")[0] == "/spawn_edit":
            try:
                n = int(self.headers.get("Content-Length", 0))
                p = json.loads(self.rfile.read(n))
                sid = int(p["id"])
                spawns = load_spawns()
                hit = next((s for s in spawns if int(s.get("Id", -1)) == sid), None)
                if hit is None:
                    self._json(404, {"ok": False, "error": "spawn not found"}); return
                if "kind" in p:
                    if p["kind"] not in SPAWN_KINDS:
                        self._json(400, {"ok": False, "error": "bad kind"}); return
                    hit["Kind"] = p["kind"]
                if "what" in p:
                    what = p["what"]
                    if isinstance(what, str): what = [what]
                    hit["What"] = [w for w in (s.strip() for s in what) if w]
                if "count" in p:    hit["Count"] = max(1, int(p["count"]))
                if "range" in p:    hit["Range"] = max(0, int(p["range"]))
                if "mindelay" in p: hit["MinDelay"] = float(p["mindelay"])
                if "maxdelay" in p: hit["MaxDelay"] = float(p["maxdelay"])
                save_spawns(spawns)
                self._json(200, {"ok": True, "spawn": hit})
            except Exception as ex:
                self._json(400, {"ok": False, "error": str(ex)})
            return
        self.send_error(404)

if __name__ == "__main__":
    print("Serving map at http://localhost:8777/map.html  (Ctrl+C to stop)")
    HTTPServer(("127.0.0.1", 8777), Handler).serve_forever()
