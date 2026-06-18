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
