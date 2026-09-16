// =========================================================================
// ZoneRegistry.cs — painted zones, game-side.
//
// Loads Data/Zones/zones.json (written by the map editor). Three kinds:
//
//   Portal  a small painted threshold at a doorless doorway. Behaviors
//           route through the opening instead of grinding the wall.
//   Area    a polygon that IS a destination (bank floor, vendor shop,
//           mine). Destinations with a Polygon field are merged in here.
//   Walk    hand-drawn clear ground: a street, a plaza, a dock. Bots walk
//           straight inside one and cross a computed LINK between two.
//
// Areas and Walk zones together are the town walk mesh. Links are never
// drawn: two zones are linked where their polygons, each grown by one
// tile, share standable ground. A Portal that touches two zones links
// them too. ZoneNav does the routing over the links, ZoneFollower does
// the stepping.
//
// Commands:  [ReloadZones    re-read zones.json (after map edits)
//            [zones          list loaded zones
//            [zonelinks      list computed links
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public sealed class PaintedZone
    {
        public string Name;
        public string Kind;                 // "Portal" | "Area" | "Walk"
        public string Type;                 // DestinationType string (Areas)
        public string LinkedDest;           // destination this Area defines
        public string Tag;                  // road | plaza | interior | dock | no-bots
        public double Cost = 1.0;           // movement weight, from the tag
        public List<(int x, int y)> Points = new();
        public int CenterX, CenterY;
        public int MinX, MinY, MaxX, MaxY;  // bounding box, inclusive

        // Surface Z inside the shape. Read from the file when the editor
        // wrote them, else measured from the map on the first mesh build.
        public int ZMin = int.MinValue, ZMax = int.MaxValue;
        public bool ZKnown => ZMin != int.MinValue;

        // Filled by the mesh build.
        public int Id;
        public int TileCount;
        public int BadTiles;                // tiles with no standable Z at all
        public readonly List<ZoneLink> Links = new();

        public bool IsWalk   => string.Equals(Kind, "Walk", StringComparison.OrdinalIgnoreCase);
        public bool IsArea   => string.Equals(Kind, "Area", StringComparison.OrdinalIgnoreCase);
        public bool IsPortal => string.Equals(Kind, "Portal", StringComparison.OrdinalIgnoreCase);

        // Part of the walk mesh: bots may step through it.
        public bool IsMesh => (IsWalk || IsArea) && !IsNoBots;
        public bool IsNoBots => string.Equals(Tag, "no-bots", StringComparison.OrdinalIgnoreCase);

        public bool IsVendorArea =>
            IsArea && Type != null && Type.StartsWith("Vendor", StringComparison.OrdinalIgnoreCase);

        public bool Contains(int px, int py)
        {
            if (px < MinX || px > MaxX || py < MinY || py > MaxY)
            {
                return false;
            }
            bool inside = false;
            for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
            {
                if ((Points[i].y > py) != (Points[j].y > py) &&
                    px < (double)(Points[j].x - Points[i].x) * (py - Points[i].y) /
                         (Points[j].y - Points[i].y) + Points[i].x)
                    inside = !inside;
            }
            return inside;
        }

        public bool Contains(Point3D p) => Contains(p.X, p.Y);

        // Inside, or touching the edge from outside. This is the "grown by
        // one tile" test the link build and the in-zone walker use, so a
        // bot standing on the border tile of a street still counts as on it.
        public bool ContainsGrown(int px, int py)
        {
            if (px < MinX - 1 || px > MaxX + 1 || py < MinY - 1 || py > MaxY + 1)
            {
                return false;
            }
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (Contains(px + dx, py + dy))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // A Z is on this zone's surface if it sits inside the measured
        // range with one step of climb either side. Unknown ranges accept
        // everything.
        public bool ZCompatible(int z) => !ZKnown || (z >= ZMin - 4 && z <= ZMax + 4);

        public int Area => (MaxX - MinX + 1) * (MaxY - MinY + 1);

        // A painted work site: the shape IS the mine / the grove. Gatherers
        // may only work while standing inside one of these.
        public bool IsGatherSite =>
            IsArea &&
            (string.Equals(Type, "MiningSpot", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Type, "LumberSpot", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Type, "GatherSpot", StringComparison.OrdinalIgnoreCase));

        internal void Finish()
        {
            MinX = Points.Min(p => p.x); MaxX = Points.Max(p => p.x);
            MinY = Points.Min(p => p.y); MaxY = Points.Max(p => p.y);
            CenterX = (int)Points.Average(p => (double)p.x);
            CenterY = (int)Points.Average(p => (double)p.y);
            if (string.IsNullOrEmpty(Tag))
            {
                Tag = IsVendorArea ? "interior" : IsArea ? "plaza" : IsWalk ? "road" : null;
            }
            if (Cost <= 0)
            {
                Cost = ZoneRegistry.DefaultCost(Tag);
            }
        }

        // Every tile inside the polygon.
        public IEnumerable<(int x, int y)> Tiles()
        {
            for (int x = MinX; x <= MaxX; x++)
            {
                for (int y = MinY; y <= MaxY; y++)
                {
                    if (Contains(x, y))
                    {
                        yield return (x, y);
                    }
                }
            }
        }

        // Cached walk-in goal (the polygon never moves; a reload builds
        // fresh PaintedZone objects, so the cache dies with the old shape).
        private Point3D? _interior;

        // The tile a bot should walk toward to get INSIDE this area.
        // Preference order: the authored destination point (the mapper put
        // it where they want bots standing), the polygon center, then the
        // most-central standable tile found by scanning the interior. The
        // caller only needs to CROSS the boundary — this is a heading, not
        // a place it must reach — so a rough answer is fine and a bad shape
        // is caught by the caller's walk-in timeout.
        public Point3D InteriorGoal(Map map, int fallbackZ)
        {
            if (_interior.HasValue)
            {
                return _interior.Value;
            }

            if (!string.IsNullOrEmpty(LinkedDest))
            {
                var dest = DestinationCatalog.GetByName(LinkedDest);
                if (dest != null)
                {
                    var p = dest.ArrivalPoint ?? dest.Location;
                    if (Contains(p.X, p.Y))
                    {
                        _interior = p;
                        return p;
                    }
                }
            }

            // Vertex average — inside for any convex-ish painted blob, but
            // a crescent (a mountain face traced around its curve) can put
            // it outside the shape entirely, hence the scan below.
            if (map != null && Contains(CenterX, CenterY))
            {
                int cz = map.GetAverageZ(CenterX, CenterY);
                if (map.CanFit(CenterX, CenterY, cz, 16, false, false))
                {
                    var c = new Point3D(CenterX, CenterY, cz);
                    _interior = c;
                    return c;
                }
            }

            if (map != null)
            {
                Point3D best = default;
                int bestScore = int.MaxValue;
                for (int x = MinX; x <= MaxX; x++)
                {
                    for (int y = MinY; y <= MaxY; y++)
                    {
                        if (!Contains(x, y))
                        {
                            continue;
                        }
                        int score = Math.Max(Math.Abs(x - CenterX), Math.Abs(y - CenterY));
                        if (score >= bestScore)
                        {
                            continue;
                        }
                        int z = map.GetAverageZ(x, y);
                        if (!map.CanFit(x, y, z, 16, false, false))
                        {
                            continue;
                        }
                        best = new Point3D(x, y, z);
                        bestScore = score;
                    }
                }

                if (bestScore != int.MaxValue)
                {
                    _interior = best;
                    return best;
                }
            }

            // Nothing standable found (or no map yet) — head for the middle
            // and let the walk-in timeout decide. Not cached: a later call
            // with a real map can still do better.
            return new Point3D(CenterX, CenterY, fallbackZ);
        }

        // A random standable tile inside the shape near the given Z, for
        // wandering. Null when twenty tries find nothing.
        public Point3D? RandomStandable(Map map, int nearZ)
        {
            if (map == null || Points.Count < 3)
            {
                return null;
            }
            for (int i = 0; i < 20; i++)
            {
                int x = Utility.RandomMinMax(MinX, MaxX);
                int y = Utility.RandomMinMax(MinY, MaxY);
                if (!Contains(x, y))
                {
                    continue;
                }
                if (Walkable.TryFindStandZ(map, x, y, nearZ, out int z))
                {
                    return new Point3D(x, y, z);
                }
            }
            return null;
        }
    }

    // A computed crossing between two mesh zones.
    public sealed class ZoneLink
    {
        public int Id;
        public PaintedZone A, B;
        public PaintedZone Via;             // the Portal that made it, or null
        public readonly List<(int x, int y)> Tiles = new();
        public int MidX, MidY, MidZ;
        public bool IsDoor;                 // a door stands on the crossing

        public PaintedZone Other(PaintedZone z) => ReferenceEquals(z, A) ? B : A;
        public bool Joins(PaintedZone z) => ReferenceEquals(z, A) || ReferenceEquals(z, B);
        public Point3D Mid => new(MidX, MidY, MidZ);

        public override string ToString() =>
            $"{A.Name} <-> {B.Name} at ({MidX},{MidY}) x{Tiles.Count}{(IsDoor ? " door" : "")}{(Via != null ? " via " + Via.Name : "")}";
    }

    public static class ZoneRegistry
    {
        private static List<PaintedZone> _zones = new();
        private static List<ZoneLink> _links = new();
        private static bool _meshBuilt;
        private static DateTime _meshBuiltAt;
        private static TimeSpan _meshBuildTime;

        public static IReadOnlyList<PaintedZone> All => _zones;
        public static IReadOnlyList<ZoneLink> Links { get { EnsureMesh(); return _links; } }
        public static bool MeshBuilt => _meshBuilt;

        // The zones are Felucca-only today; the file has no map field.
        public static Map MeshMap => Map.Felucca;

        private static string JsonPath => Path.Combine(
            Core.BaseDirectory, "Data", "Zones", "zones.json");
        private static string DestJsonPath => Path.Combine(
            Core.BaseDirectory, "Data", "Destinations", "destinations.json");

        public static double DefaultCost(string tag) => (tag ?? "").ToLowerInvariant() switch
        {
            "road"     => 0.7,
            "plaza"    => 1.0,
            "interior" => 1.6,
            "dock"     => 1.0,
            "no-bots"  => 1000.0,
            _          => 1.0,
        };

        public static void Configure()
        {
            CommandSystem.Register("ReloadZones", AccessLevel.GameMaster, Reload_OnCommand);
            CommandSystem.Register("zones",       AccessLevel.GameMaster, List_OnCommand);
            CommandSystem.Register("zonelinks",   AccessLevel.GameMaster, Links_OnCommand);
        }

        public static void Initialize()
        {
            Load();
            // Build the mesh once the world is up so the status page and
            // the first traveler both find it ready.
            Timer.DelayCall(TimeSpan.FromSeconds(8), () => EnsureMesh());
        }

        // Public reload entry (used by the editor's "Reload in game" button
        // via EditorReloadWatcher). Returns the zone count after reloading.
        public static int Reload()
        {
            Load();
            EnsureMesh();
            return _zones.Count;
        }

        private static void Load()
        {
            var fresh = new List<PaintedZone>();
            try
            {
                if (File.Exists(JsonPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(JsonPath));
                    foreach (var z in doc.RootElement.GetProperty("Zones").EnumerateArray())
                    {
                        var pz = new PaintedZone
                        {
                            Name = z.GetProperty("Name").GetString(),
                            Kind = z.TryGetProperty("Kind", out var k) ? k.GetString() : "Portal",
                            Type = z.TryGetProperty("Type", out var t) ? t.GetString() : null,
                            LinkedDest = z.TryGetProperty("LinkedDest", out var l) ? l.GetString() : null,
                            Tag = z.TryGetProperty("Tag", out var tg) ? tg.GetString() : null,
                        };
                        if (z.TryGetProperty("Cost", out var c) && c.ValueKind == JsonValueKind.Number)
                        {
                            pz.Cost = c.GetDouble();
                        }
                        if (z.TryGetProperty("ZMin", out var zmin) && zmin.ValueKind == JsonValueKind.Number &&
                            z.TryGetProperty("ZMax", out var zmax) && zmax.ValueKind == JsonValueKind.Number)
                        {
                            pz.ZMin = zmin.GetInt32();
                            pz.ZMax = zmax.GetInt32();
                        }
                        foreach (var p in z.GetProperty("Points").EnumerateArray())
                            pz.Points.Add((p[0].GetInt32(), p[1].GetInt32()));
                        if (pz.Points.Count >= 3)
                        {
                            pz.Finish();
                            fresh.Add(pz);
                        }
                    }
                }
            }
            catch (Exception ex)
            { Console.WriteLine($"[zones] load failed: {ex.Message}"); }

            // Destinations with a painted Polygon ARE Area zones — the
            // shape is the destination. Merged here so arrival logic and
            // the planner each keep reading their accustomed source.
            int destAreas = 0;
            try
            {
                if (File.Exists(DestJsonPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(DestJsonPath));
                    foreach (var d in doc.RootElement.GetProperty("Destinations").EnumerateArray())
                    {
                        if (!d.TryGetProperty("Polygon", out var poly) ||
                            poly.ValueKind != JsonValueKind.Array) continue;
                        var pz = new PaintedZone
                        {
                            Name = d.GetProperty("Name").GetString() + " Area",
                            Kind = "Area",
                            Type = d.TryGetProperty("Type", out var t) ? t.GetString() : null,
                            LinkedDest = d.GetProperty("Name").GetString(),
                            Tag = d.TryGetProperty("Tag", out var tg) ? tg.GetString() : null,
                        };
                        if (d.TryGetProperty("Cost", out var c) && c.ValueKind == JsonValueKind.Number)
                        {
                            pz.Cost = c.GetDouble();
                        }
                        foreach (var p in poly.EnumerateArray())
                            pz.Points.Add((p[0].GetInt32(), p[1].GetInt32()));
                        if (pz.Points.Count >= 3)
                        {
                            pz.Finish();
                            fresh.Add(pz); destAreas++;
                        }
                    }
                }
            }
            catch (Exception ex)
            { Console.WriteLine($"[zones] destination polygons load failed: {ex.Message}"); }

            for (int i = 0; i < fresh.Count; i++)
            {
                fresh[i].Id = i;
            }

            _zones = fresh;
            _links = new List<ZoneLink>();
            _meshBuilt = false;
            int walk = fresh.Count(z => z.IsWalk);
            Console.WriteLine($"[zones] {_zones.Count} zone(s) loaded ({destAreas} from destination polygons, {walk} walk).");
        }

        // -----------------------------------------------------------------
        // Mesh build: Z ranges, bad-tile counts, links.
        // -----------------------------------------------------------------

        public static void EnsureMesh()
        {
            if (_meshBuilt)
            {
                return;
            }
            _meshBuilt = true;
            var map = MeshMap;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var links = new List<ZoneLink>();
            try
            {
                foreach (var z in _zones)
                {
                    MeasureZone(map, z);
                    z.Links.Clear();
                }

                var mesh = _zones.Where(z => z.IsMesh).ToList();

                // Geometry links: shared ground between two grown polygons.
                for (int i = 0; i < mesh.Count; i++)
                {
                    for (int j = i + 1; j < mesh.Count; j++)
                    {
                        var a = mesh[i];
                        var b = mesh[j];
                        if (a.MaxX + 1 < b.MinX - 1 || b.MaxX + 1 < a.MinX - 1 ||
                            a.MaxY + 1 < b.MinY - 1 || b.MaxY + 1 < a.MinY - 1)
                        {
                            continue;
                        }
                        var link = BuildLink(map, a, b, null);
                        if (link != null)
                        {
                            links.Add(link);
                        }
                    }
                }

                // Portal links: a painted threshold touching two mesh zones
                // joins them, even when the polygons themselves do not meet.
                foreach (var portal in _zones.Where(z => z.IsPortal))
                {
                    var touched = new List<PaintedZone>();
                    foreach (var z in mesh)
                    {
                        if (portal.MaxX + 1 < z.MinX - 1 || z.MaxX + 1 < portal.MinX - 1 ||
                            portal.MaxY + 1 < z.MinY - 1 || z.MaxY + 1 < portal.MinY - 1)
                        {
                            continue;
                        }
                        bool touches = false;
                        foreach (var (x, y) in portal.Tiles())
                        {
                            if (z.ContainsGrown(x, y)) { touches = true; break; }
                        }
                        if (!touches)
                        {
                            // A portal polygon can be thinner than a tile;
                            // fall back to its corners.
                            foreach (var (x, y) in portal.Points)
                            {
                                if (z.ContainsGrown(x, y)) { touches = true; break; }
                            }
                        }
                        if (touches)
                        {
                            touched.Add(z);
                        }
                    }
                    for (int i = 0; i < touched.Count; i++)
                    {
                        for (int j = i + 1; j < touched.Count; j++)
                        {
                            var a = touched[i];
                            var b = touched[j];
                            if (links.Any(l => l.Joins(a) && l.Joins(b)))
                            {
                                continue;
                            }
                            var link = new ZoneLink { A = a, B = b, Via = portal };
                            foreach (var (x, y) in portal.Tiles())
                            {
                                link.Tiles.Add((x, y));
                            }
                            if (link.Tiles.Count == 0)
                            {
                                link.Tiles.Add((portal.CenterX, portal.CenterY));
                            }
                            FinishLink(map, link);
                            links.Add(link);
                        }
                    }
                }

                for (int i = 0; i < links.Count; i++)
                {
                    links[i].Id = i;
                    links[i].A.Links.Add(links[i]);
                    links[i].B.Links.Add(links[i]);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[zones] mesh build failed: {ex.Message}");
            }

            _links = links;
            _meshBuiltAt = Core.Now;
            _meshBuildTime = sw.Elapsed;
            int meshZones = _zones.Count(z => z.IsMesh);
            int lonely = _zones.Count(z => z.IsMesh && z.Links.Count == 0);
            Console.WriteLine($"[zones] mesh: {meshZones} zone(s), {links.Count} link(s), " +
                              $"{lonely} with no link, built in {_meshBuildTime.TotalMilliseconds:0} ms.");
            foreach (var l in links)
            {
                Console.WriteLine($"[zones]   link {l}");
            }
            ZoneNavView.RefreshAll();
        }

        private static void MeasureZone(Map map, PaintedZone z)
        {
            z.TileCount = 0;
            z.BadTiles = 0;
            if (map == null || !(z.IsMesh || z.IsPortal))
            {
                return;
            }
            bool measureZ = !z.ZKnown;
            int zmin = int.MaxValue, zmax = int.MinValue;
            // Big walk zones get sampled; the count scales so a whole
            // street is not thousands of CanFit calls on every reload.
            int area = z.Area;
            int stride = area > 6000 ? 3 : area > 1500 ? 2 : 1;
            int seen = 0;
            for (int x = z.MinX; x <= z.MaxX; x += stride)
            {
                for (int y = z.MinY; y <= z.MaxY; y += stride)
                {
                    if (!z.Contains(x, y))
                    {
                        continue;
                    }
                    seen++;
                    int refZ = z.ZKnown ? (z.ZMin + z.ZMax) / 2 : map.GetAverageZ(x, y);
                    if (Walkable.TryFindSeedZ(map, x, y, refZ, out int sz))
                    {
                        if (sz < zmin) zmin = sz;
                        if (sz > zmax) zmax = sz;
                    }
                    else
                    {
                        z.BadTiles++;
                    }
                }
            }
            z.TileCount = seen * stride * stride;
            z.BadTiles *= stride * stride;
            if (measureZ && zmin != int.MaxValue)
            {
                z.ZMin = zmin;
                z.ZMax = zmax;
            }
        }

        private static ZoneLink BuildLink(Map map, PaintedZone a, PaintedZone b, PaintedZone via)
        {
            int x0 = Math.Max(a.MinX, b.MinX) - 1, x1 = Math.Min(a.MaxX, b.MaxX) + 1;
            int y0 = Math.Max(a.MinY, b.MinY) - 1, y1 = Math.Min(a.MaxY, b.MaxY) + 1;
            if (x1 < x0 || y1 < y0)
            {
                return null;
            }
            // The surfaces must be able to meet: a second floor and the
            // ground under it never link, however the outlines overlap.
            if (a.ZKnown && b.ZKnown && (a.ZMin > b.ZMax + 20 || b.ZMin > a.ZMax + 20))
            {
                return null;
            }
            var link = new ZoneLink { A = a, B = b, Via = via };
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    // Grown by one tile each: two shapes that stop one tile
                    // short of each other (a doorway column) still meet.
                    if (!a.ContainsGrown(x, y) || !b.ContainsGrown(x, y))
                    {
                        continue;
                    }
                    if (!TileStandable(map, x, y, a, b, out int z))
                    {
                        continue;
                    }
                    link.Tiles.Add((x, y));
                }
            }
            if (link.Tiles.Count == 0)
            {
                return null;
            }
            FinishLink(map, link);
            return link;
        }

        // Standable, and on a surface both zones accept.
        private static bool TileStandable(Map map, int x, int y, PaintedZone a, PaintedZone b, out int z)
        {
            z = 0;
            int refZ = a.ZKnown ? (a.ZMin + a.ZMax) / 2 : map.GetAverageZ(x, y);
            if (!Walkable.TryFindSeedZ(map, x, y, refZ, out z))
            {
                return Walkable.ClosedDoorAt(map, x, y, refZ);
            }
            return a.ZCompatible(z) && b.ZCompatible(z);
        }

        private static void FinishLink(Map map, ZoneLink link)
        {
            // Midpoint = the crossing tile nearest the centroid of the run,
            // so a long shared border is crossed near its middle.
            double cx = link.Tiles.Average(t => (double)t.x);
            double cy = link.Tiles.Average(t => (double)t.y);
            var mid = link.Tiles.OrderBy(t => (t.x - cx) * (t.x - cx) + (t.y - cy) * (t.y - cy)).First();
            link.MidX = mid.x;
            link.MidY = mid.y;
            int refZ = link.A.ZKnown ? (link.A.ZMin + link.A.ZMax) / 2 : map.GetAverageZ(mid.x, mid.y);
            link.MidZ = Walkable.TryFindSeedZ(map, mid.x, mid.y, refZ, out int z) ? z : refZ;
            foreach (var (x, y) in link.Tiles)
            {
                if (Walkable.ClosedDoorAt(map, x, y, link.MidZ) || DoorAt(map, x, y, link.MidZ))
                {
                    link.IsDoor = true;
                    break;
                }
            }
        }

        private static bool DoorAt(Map map, int x, int y, int z)
        {
            foreach (var item in map.GetItemsAt(x, y))
            {
                if (item is Server.Items.BaseDoor d && Math.Abs(d.Z - z) <= 15)
                {
                    return true;
                }
            }
            return false;
        }

        // -----------------------------------------------------------------
        // Queries
        // -----------------------------------------------------------------

        // The mesh zone this tile is inside. When zones overlap the
        // smallest wins, so a shop drawn inside a plaza is the shop.
        public static PaintedZone MeshZoneAt(int x, int y, int z)
        {
            EnsureMesh();
            PaintedZone best = null;
            foreach (var zone in _zones)
            {
                if (!zone.IsMesh || !zone.Contains(x, y) || !zone.ZCompatible(z))
                {
                    continue;
                }
                if (best == null || zone.Area < best.Area)
                {
                    best = zone;
                }
            }
            return best;
        }

        public static PaintedZone MeshZoneAt(Point3D p) => MeshZoneAt(p.X, p.Y, p.Z);

        // The painted Area (any type) this tile is inside, smallest first.
        public static PaintedZone AreaAt(int x, int y)
        {
            PaintedZone best = null;
            foreach (var zone in _zones)
            {
                if (!zone.IsArea || !zone.Contains(x, y))
                {
                    continue;
                }
                if (best == null || zone.Area < best.Area)
                {
                    best = zone;
                }
            }
            return best;
        }

        // Nearest portal whose center is within maxDist of the target —
        // "the painted threshold that serves this spot". Null if none.
        public static PaintedZone NearestPortalTo(Point3D target, int maxDist)
        {
            PaintedZone best = null; int bd = maxDist + 1;
            foreach (var z in _zones)
            {
                if (!z.IsPortal)
                    continue;
                int d = Math.Max(Math.Abs(z.CenterX - target.X),
                                 Math.Abs(z.CenterY - target.Y));
                if (d < bd) { bd = d; best = z; }
            }
            return best;
        }

        // The Area zone that DEFINES a destination: linked by name first,
        // else any Area containing the destination's coordinate. Null if
        // nothing painted — callers fall back to distance gating.
        public static PaintedZone AreaForDestination(string destName, Point3D coord)
        {
            // Match the destination's OWN area by name only. The old
            // coordinate-containment fallback returned a NEIGHBOR's polygon
            // when a shop had no area of its own — producing phantom Shopper
            // handoffs for unpainted shops whose point fell inside an
            // adjacent painted area (vendor rows). A destination is "painted"
            // only if it has its own LinkedDest area.
            foreach (var z in _zones)
            {
                if (!z.IsArea)
                    continue;
                if (!string.IsNullOrEmpty(z.LinkedDest) &&
                    string.Equals(z.LinkedDest, destName, StringComparison.OrdinalIgnoreCase))
                    return z;
            }
            return null;
        }

        // The painted work site this tile is INSIDE, if any. Used by the
        // gatherer to answer "am I in the mine?" without needing to know
        // which destination sent it here (behaviors only persist by name,
        // so a bot reloaded mid-shift has forgotten its site).
        public static PaintedZone GatherAreaAt(int x, int y)
        {
            foreach (var z in _zones)
            {
                if (z.IsGatherSite && z.Contains(x, y))
                {
                    return z;
                }
            }
            return null;
        }

        // The nearest painted work site by center distance — the site a bot
        // standing just outside one was almost certainly sent to.
        public static PaintedZone NearestGatherArea(Point3D p, int maxDist)
        {
            PaintedZone best = null; int bd = maxDist + 1;
            foreach (var z in _zones)
            {
                if (!z.IsGatherSite)
                {
                    continue;
                }
                int d = Math.Max(Math.Abs(z.CenterX - p.X), Math.Abs(z.CenterY - p.Y));
                if (d < bd) { bd = d; best = z; }
            }
            return best;
        }

        // -----------------------------------------------------------------
        // Status page
        // -----------------------------------------------------------------

        public static void AppendHtml(StringBuilder sb)
        {
            EnsureMesh();
            int meshZones = _zones.Count(z => z.IsMesh);
            int lonely = _zones.Count(z => z.IsMesh && z.Links.Count == 0);
            int bad = _zones.Count(z => z.IsMesh && z.BadTiles > 0);
            sb.Append("<h2>Nav zones</h2>");
            sb.Append($"<p>Zone walking: <b>{ZoneNav.ModeName}</b>. {meshZones} mesh zone(s), " +
                      $"{_links.Count} link(s), {lonely} with no link, {bad} with unwalkable tiles. " +
                      $"Mesh built {(_meshBuilt ? _meshBuiltAt.ToString("HH:mm:ss") : "never")} " +
                      $"in {_meshBuildTime.TotalMilliseconds:0} ms. " +
                      $"Zone stalls since boot: {StuckTelemetry.TotalOf("zone_stall")}. " +
                      $"Vendor purchases: {BotVendorPurchase.PurchaseCount}.</p>");
            sb.Append("<table><tr><th>Zone</th><th>Kind</th><th>Tag</th><th>Tiles</th>" +
                      "<th>Bad</th><th>Z</th><th>Links</th></tr>");
            foreach (var z in _zones.Where(z => z.IsMesh).OrderBy(z => z.BadTiles == 0)
                                    .ThenBy(z => z.Links.Count).ThenBy(z => z.Name))
            {
                string warn = z.Links.Count == 0 ? " style=\"color:#c33\"" : z.BadTiles > 0 ? " style=\"color:#c80\"" : "";
                sb.Append($"<tr{warn}><td>{System.Net.WebUtility.HtmlEncode(z.Name)}</td><td>{z.Kind}" +
                          $"{(z.Type != null ? " / " + z.Type : "")}</td><td>{z.Tag}</td>" +
                          $"<td>{z.TileCount}</td><td>{z.BadTiles}</td>" +
                          $"<td>{(z.ZKnown ? $"{z.ZMin}..{z.ZMax}" : "?")}</td><td>{z.Links.Count}</td></tr>");
            }
            sb.Append("</table>");
            var recent = BotVendorPurchase.Recent;
            if (recent.Count > 0)
            {
                sb.Append("<h3>Recent purchases</h3><ul>");
                foreach (var line in recent)
                {
                    sb.Append("<li>").Append(System.Net.WebUtility.HtmlEncode(line)).Append("</li>");
                }
                sb.Append("</ul>");
            }
        }

        // -----------------------------------------------------------------
        // Commands
        // -----------------------------------------------------------------

        private static void Reload_OnCommand(CommandEventArgs e)
        {
            Load();
            EnsureMesh();
            e.Mobile.SendMessage($"Zones reloaded: {_zones.Count}, links: {_links.Count}.");
        }

        private static void List_OnCommand(CommandEventArgs e)
        {
            if (_zones.Count == 0) { e.Mobile.SendMessage("No zones loaded."); return; }
            EnsureMesh();
            foreach (var z in _zones)
                e.Mobile.SendMessage($"{z.Kind}: '{z.Name}'" +
                    (z.Type != null ? $" [{z.Type}]" : "") +
                    (z.Tag != null ? $" tag {z.Tag} cost {z.Cost:0.##}" : "") +
                    (string.IsNullOrEmpty(z.LinkedDest) ? "" : $" -> {z.LinkedDest}") +
                    $" center ({z.CenterX},{z.CenterY}), {z.Points.Count} corners" +
                    (z.IsMesh ? $", {z.Links.Count} links, {z.BadTiles} bad tiles, Z {(z.ZKnown ? $"{z.ZMin}..{z.ZMax}" : "?")}" : ""));
        }

        private static void Links_OnCommand(CommandEventArgs e)
        {
            EnsureMesh();
            if (_links.Count == 0) { e.Mobile.SendMessage("No zone links."); return; }
            foreach (var l in _links)
            {
                e.Mobile.SendMessage(l.ToString());
            }
        }
    }
}
