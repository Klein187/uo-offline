// =========================================================================
// ZoneRegistry.cs — painted zones, game-side.
//
// Loads Data/Zones/zones.json (written by the map editor). v1 understands
// PORTALS: small painted thresholds at doorless doorways. Behaviors query
// NearestPortalTo(target) to route through the opening instead of grinding
// the wall toward it.
//
// Commands:  [ReloadZones    re-read zones.json (after map edits)
//            [zones          list loaded zones
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public sealed class PaintedZone
    {
        public string Name;
        public string Kind;                 // "Portal" | "Area"
        public string Type;                 // DestinationType string (Areas)
        public string LinkedDest;           // destination this Area defines
        public List<(int x, int y)> Points = new();
        public int CenterX, CenterY;

        public bool Contains(int px, int py)
        {
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

        // A painted work site: the shape IS the mine / the grove. Gatherers
        // may only work while standing inside one of these.
        public bool IsGatherSite =>
            string.Equals(Kind, "Area", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(Type, "MiningSpot", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Type, "LumberSpot", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Type, "GatherSpot", StringComparison.OrdinalIgnoreCase));

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
                int minX = int.MaxValue, maxX = int.MinValue;
                int minY = int.MaxValue, maxY = int.MinValue;
                foreach (var (px, py) in Points)
                {
                    if (px < minX) { minX = px; }
                    if (px > maxX) { maxX = px; }
                    if (py < minY) { minY = py; }
                    if (py > maxY) { maxY = py; }
                }

                Point3D best = default;
                int bestScore = int.MaxValue;
                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
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
    }

    public static class ZoneRegistry
    {
        private static List<PaintedZone> _zones = new();
        public static IReadOnlyList<PaintedZone> All => _zones;

        private static string JsonPath => Path.Combine(
            Core.BaseDirectory, "Data", "Zones", "zones.json");
        private static string DestJsonPath => Path.Combine(
            Core.BaseDirectory, "Data", "Destinations", "destinations.json");

        public static void Configure()
        {
            CommandSystem.Register("ReloadZones", AccessLevel.GameMaster, Reload_OnCommand);
            CommandSystem.Register("zones",       AccessLevel.GameMaster, List_OnCommand);
        }

        public static void Initialize() => Load();

        // Public reload entry (used by the editor's "Reload in game" button
        // via EditorReloadWatcher). Returns the zone count after reloading.
        public static int Reload()
        {
            Load();
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
                        };
                        foreach (var p in z.GetProperty("Points").EnumerateArray())
                            pz.Points.Add((p[0].GetInt32(), p[1].GetInt32()));
                        if (pz.Points.Count >= 3)
                        {
                            pz.CenterX = (int)pz.Points.Average(p => (double)p.x);
                            pz.CenterY = (int)pz.Points.Average(p => (double)p.y);
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
                        };
                        foreach (var p in poly.EnumerateArray())
                            pz.Points.Add((p[0].GetInt32(), p[1].GetInt32()));
                        if (pz.Points.Count >= 3)
                        {
                            pz.CenterX = (int)pz.Points.Average(p => (double)p.x);
                            pz.CenterY = (int)pz.Points.Average(p => (double)p.y);
                            fresh.Add(pz); destAreas++;
                        }
                    }
                }
            }
            catch (Exception ex)
            { Console.WriteLine($"[zones] destination polygons load failed: {ex.Message}"); }

            _zones = fresh;
            Console.WriteLine($"[zones] {_zones.Count} zone(s) loaded ({destAreas} from destination polygons).");
        }

        // Nearest portal whose center is within maxDist of the target —
        // "the painted threshold that serves this spot". Null if none.
        public static PaintedZone NearestPortalTo(Point3D target, int maxDist)
        {
            PaintedZone best = null; int bd = maxDist + 1;
            foreach (var z in _zones)
            {
                if (!string.Equals(z.Kind, "Portal", StringComparison.OrdinalIgnoreCase))
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
                if (!string.Equals(z.Kind, "Area", StringComparison.OrdinalIgnoreCase))
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

        private static void Reload_OnCommand(CommandEventArgs e)
        {
            Load();
            e.Mobile.SendMessage($"Zones reloaded: {_zones.Count}.");
        }

        private static void List_OnCommand(CommandEventArgs e)
        {
            if (_zones.Count == 0) { e.Mobile.SendMessage("No zones loaded."); return; }
            foreach (var z in _zones)
                e.Mobile.SendMessage($"{z.Kind}: '{z.Name}'" +
                    (z.Type != null ? $" [{z.Type}]" : "") +
                    (string.IsNullOrEmpty(z.LinkedDest) ? "" : $" -> {z.LinkedDest}") +
                    $" center ({z.CenterX},{z.CenterY}), {z.Points.Count} corners");
        }
    }
}
