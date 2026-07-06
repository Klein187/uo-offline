// =========================================================================
// PKSpawnData.cs — editor-authored PK spawns and their hunt areas.
//
// PK spawners are no longer hardcoded. They come from
// Data/CustomSpawns/pk_spawns.json, drawn in the map editor:
//
//   { "Spawns": [
//       { "name": "Despise Reds", "x": 5407, "y": 857, "z": 0,
//         "amount": 3, "hunt": [[x,y],[x,y],...] } ] }
//
// The optional "hunt" polygon is the leash: a PK spawned inside (or
// nearest to) that spawn prowls ONLY within the polygon and never walks
// out toward a town. This class loads the file and answers
// HuntAreaFor(location) so PKBehavior can pick up its leash on attach.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;

namespace Server.CustomBots
{
    public sealed class PKSpawnDef
    {
        public string Name;
        public Point3D Location;
        public int Amount;
        public Point2D[] Hunt;   // null/empty = roaming road spawn

        // Point-in-polygon (ray cast) — is (x,y) inside the hunt area?
        public bool Contains(int x, int y)
        {
            if (Hunt == null || Hunt.Length < 3)
            {
                return false;
            }
            bool inside = false;
            for (int i = 0, j = Hunt.Length - 1; i < Hunt.Length; j = i++)
            {
                if ((Hunt[i].Y > y) != (Hunt[j].Y > y) &&
                    x < (double)(Hunt[j].X - Hunt[i].X) * (y - Hunt[i].Y) /
                        (Hunt[j].Y - Hunt[i].Y) + Hunt[i].X)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        public Point3D Centroid()
        {
            if (Hunt == null || Hunt.Length == 0)
            {
                return Location;
            }
            long sx = 0, sy = 0;
            foreach (var p in Hunt)
            {
                sx += p.X;
                sy += p.Y;
            }
            return new Point3D((int)(sx / Hunt.Length), (int)(sy / Hunt.Length),
                               Location.Z);
        }
    }

    public static class PKSpawnData
    {
        private static List<PKSpawnDef> _defs = new();

        private static string Path => System.IO.Path.Combine(
            Core.BaseDirectory, "Data", "CustomSpawns", "pk_spawns.json");

        public static IReadOnlyList<PKSpawnDef> Defs => _defs;

        public static List<PKSpawnDef> Load()
        {
            var list = new List<PKSpawnDef>();
            try
            {
                if (!File.Exists(Path))
                {
                    _defs = list;
                    return list;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(Path));
                if (!doc.RootElement.TryGetProperty("Spawns", out var arr) ||
                    arr.ValueKind != JsonValueKind.Array)
                {
                    _defs = list;
                    return list;
                }

                foreach (var el in arr.EnumerateArray())
                {
                    int x = el.GetProperty("x").GetInt32();
                    int y = el.GetProperty("y").GetInt32();
                    int z = el.TryGetProperty("z", out var zv) ? zv.GetInt32() : 0;
                    int amount = el.TryGetProperty("amount", out var av)
                        ? av.GetInt32() : 3;

                    Point2D[] hunt = null;
                    if (el.TryGetProperty("hunt", out var hv) &&
                        hv.ValueKind == JsonValueKind.Array && hv.GetArrayLength() >= 3)
                    {
                        var pts = new List<Point2D>();
                        foreach (var p in hv.EnumerateArray())
                        {
                            if (p.ValueKind == JsonValueKind.Array &&
                                p.GetArrayLength() >= 2)
                            {
                                pts.Add(new Point2D(p[0].GetInt32(), p[1].GetInt32()));
                            }
                        }
                        if (pts.Count >= 3)
                        {
                            hunt = pts.ToArray();
                        }
                    }

                    list.Add(new PKSpawnDef
                    {
                        Name = el.TryGetProperty("name", out var nv)
                            ? nv.GetString() : $"PK {x},{y}",
                        Location = new Point3D(x, y, z),
                        Amount = Math.Max(1, amount),
                        Hunt = hunt,
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PKSpawnData] load failed: {ex.Message}");
            }

            _defs = list;
            return list;
        }

        // The hunt area a bot at this location belongs to: the spawn whose
        // polygon contains it, else the nearest spawn within 120 tiles that
        // HAS a polygon (bots spawn scattered within the spawner bounds, a
        // little outside a tight polygon). Null = a roaming road spawn.
        public static PKSpawnDef HuntAreaFor(Point3D loc)
        {
            PKSpawnDef best = null;
            int bestD = 120;
            foreach (var d in _defs)
            {
                if (d.Hunt == null)
                {
                    continue;
                }
                if (d.Contains(loc.X, loc.Y))
                {
                    return d;
                }
                int dist = Math.Max(Math.Abs(d.Location.X - loc.X),
                                    Math.Abs(d.Location.Y - loc.Y));
                if (dist < bestD)
                {
                    bestD = dist;
                    best = d;
                }
            }
            return best;
        }
    }
}
