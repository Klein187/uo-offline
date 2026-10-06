// =========================================================================
// ShopFloorSurvey.cs — measure the shop floors nobody has drawn yet.
//
// A shop destination only works when its floor is drawn (a Polygon on the
// destination in destinations.json). This finds, for every shop
// destination without one, the NPC that actually runs it and the floor
// that NPC stands on: a flood fill from the NPC's tile over floor that is
// under a roof, stopped by tall walls and doors and let through low things
// like counters and tables. It changes nothing in the world; it writes
// what it measured to Data/Live/shop_floors.json, and
// tools/map/draw_shop_floors.py turns that into polygons in
// destinations.json, where the map editor shows them like any drawn floor.
//
//   [SurveyShopFloors        — run it
//   shopfloors_request.txt   — headless, any new token
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server.Commands;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class ShopFloorSurvey
    {
        private const int FindNpcRange = 25;
        private const int MaxTiles = 400;
        private const int FloorSlack = 6;    // floor height a shop can vary by
        private const int LowThing = 12;     // counters and tables are under this
        private const int RoofMin = 12, RoofMax = 80;

        private static string ReqPath => Path.Combine(Core.BaseDirectory, "Data", "Live", "shopfloors_request.txt");
        private static string OutPath => Path.Combine(Core.BaseDirectory, "Data", "Live", "shop_floors.json");
        private static long _lastToken;

        public static void Initialize()
        {
            CommandSystem.Register("SurveyShopFloors", AccessLevel.Administrator, e =>
                e.Mobile.SendMessage(Run()));
            _lastToken = ReadToken();
            Timer.DelayCall(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), () =>
            {
                long t = ReadToken();
                if (t != 0 && t != _lastToken)
                {
                    _lastToken = t;
                    Console.WriteLine($"[shopfloors] {Run()}");
                }
            });
        }

        private static long ReadToken()
        {
            try
            {
                return File.Exists(ReqPath) && long.TryParse(File.ReadAllText(ReqPath).Trim(), out var t) ? t : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static string Run()
        {
            var map = Map.Felucca;
            var results = new List<Dictionary<string, object>>();
            int measured = 0, noNpc = 0, open = 0;

            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type.ToString() is not { } type || !type.StartsWith("Vendor") ||
                    !string.IsNullOrEmpty(d.Dungeon) ||
                    ZoneRegistry.AreaForDestination(d.Name, d.Location) != null)
                {
                    continue;
                }

                var rec = new Dictionary<string, object> { ["dest"] = d.Name, ["type"] = type };
                results.Add(rec);

                var npc = FindNpc(map, d.Location, type);
                if (npc == null)
                {
                    rec["status"] = "no matching NPC within " + FindNpcRange;
                    rec["nearby"] = NearbyVendors(map, d.Location);
                    noNpc++;
                    continue;
                }
                rec["npc"] = new[] { npc.X, npc.Y, npc.Z };
                rec["npcType"] = npc.GetType().Name;

                var tiles = Flood(map, npc.Location, out string why);
                if (tiles == null)
                {
                    rec["status"] = why;
                    open++;
                    continue;
                }
                rec["status"] = "ok";
                rec["z"] = npc.Z;
                var list = new List<int[]>();
                foreach (var (x, y) in tiles)
                {
                    list.Add(new[] { x, y });
                }
                rec["tiles"] = list;
                measured++;
            }

            try
            {
                File.WriteAllText(OutPath, JsonSerializer.Serialize(results,
                    new JsonSerializerOptions { WriteIndented = false }));
            }
            catch (Exception ex)
            {
                return "could not write shop_floors.json: " + ex.Message;
            }
            return $"{results.Count} undrawn shop(s): {measured} measured, {noNpc} with no matching NPC, " +
                $"{open} not an enclosed floor";
        }

        private static bool Fits(string type, BaseVendor v) => type switch
        {
            "VendorWeaponer"    => v is Weaponsmith,
            "VendorSmith"       => v is Blacksmith or Armorer or Weaponsmith,
            "VendorMage"        => v is Mage or Scribe,
            "VendorTailor"      => v is Tailor or Weaver,
            "VendorCarpenter"   => v is Carpenter,
            "VendorBowyer"      => v is Bowyer,
            "VendorAlchemist"   => v is Alchemist or Herbalist,
            "VendorProvisioner" => v is Provisioner,
            _                   => false,
        };

        private static BaseVendor FindNpc(Map map, Point3D at, string type)
        {
            BaseVendor best = null;
            int bestD = int.MaxValue;
            foreach (var m in map.GetMobilesInRange(at, FindNpcRange))
            {
                if (m is BaseVendor v && !v.Deleted && Fits(type, v))
                {
                    int d = Math.Max(Math.Abs(v.X - at.X), Math.Abs(v.Y - at.Y));
                    if (d < bestD)
                    {
                        bestD = d;
                        best = v;
                    }
                }
            }
            return best;
        }

        private static List<string> NearbyVendors(Map map, Point3D at)
        {
            var seen = new SortedSet<string>();
            foreach (var m in map.GetMobilesInRange(at, FindNpcRange))
            {
                if (m is BaseVendor v)
                {
                    seen.Add(v.GetType().Name);
                }
            }
            return new List<string>(seen);
        }

        // ---- the floor ----

        private static List<(int, int)> Flood(Map map, Point3D seed, out string why)
        {
            why = null;
            if (!Covered(map, seed.X, seed.Y, seed.Z))
            {
                why = "NPC not under a roof (street stall)";
                return null;
            }
            var seen = new HashSet<(int, int)> { (seed.X, seed.Y) };
            var queue = new Queue<(int, int)>();
            queue.Enqueue((seed.X, seed.Y));
            var floor = new List<(int, int)>();
            while (queue.Count > 0)
            {
                var (x, y) = queue.Dequeue();
                floor.Add((x, y));
                if (floor.Count > MaxTiles)
                {
                    why = $"floor runs on past {MaxTiles} tiles (not enclosed)";
                    return null;
                }
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (!seen.Add((nx, ny)))
                    {
                        continue;
                    }
                    if (IsDoor(map, nx, ny, seed.Z) || !Covered(map, nx, ny, seed.Z) || !Inside(map, nx, ny, seed.Z))
                    {
                        continue;
                    }
                    queue.Enqueue((nx, ny));
                }
            }
            return floor;
        }

        // Floor you could stand on near the shop's height, or a tile holding
        // only low things (a counter, a table). A tall wall stops the fill.
        private static bool Inside(Map map, int x, int y, int z0)
        {
            bool tall = false;
            foreach (var t in map.Tiles.GetStaticAndMultiTiles(x, y))
            {
                var data = TileData.ItemTable[t.ID & TileData.MaxItemValue];
                if (!data.Impassable || t.Z + data.CalcHeight <= z0 || t.Z > z0 + LowThing + 4)
                {
                    continue;
                }
                if (t.Z + data.CalcHeight - z0 > LowThing)
                {
                    tall = true;
                    break;
                }
            }
            if (tall)
            {
                return false;
            }
            if (Walkable.TryFindSeedZ(map, x, y, z0, out var z) && Math.Abs(z - z0) <= FloorSlack)
            {
                return true;
            }
            // Blocked, but only by low things: still the shop floor.
            foreach (var t in map.Tiles.GetStaticAndMultiTiles(x, y))
            {
                var data = TileData.ItemTable[t.ID & TileData.MaxItemValue];
                if (data.Impassable && t.Z >= z0 - 2 && t.Z <= z0 + LowThing)
                {
                    return true;
                }
            }
            return false;
        }

        // Something overhead: a roof or the floor of the storey above.
        private static bool Covered(Map map, int x, int y, int z0)
        {
            foreach (var t in map.Tiles.GetStaticAndMultiTiles(x, y))
            {
                if (t.Z >= z0 + RoofMin && t.Z <= z0 + RoofMax)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsDoor(Map map, int x, int y, int z0)
        {
            foreach (var item in map.GetItemsAt(new Point3D(x, y, z0)))
            {
                if (item is BaseDoor)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
