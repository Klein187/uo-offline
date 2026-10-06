// =========================================================================
// BotHousing.cs — the T2A land rush, prototyped (IDEAS 4.5 research spike).
//
// Scatters small bot-"owned" era houses along the wilderness roads the
// bots already walk: candidate spots are sampled a short distance off
// rural waypoint nodes, validated with the REAL house placement rules
// (HousePlacement.Check, exercised through a Player-level probe so no
// staff bypass kicks in), and placed as ordinary BaseHouse multis.
//
// Spike findings baked into this design:
//   - HousePlacement.Check(from, multiId, center, out toMove) is the whole
//     placement API; AccessLevel >= GameMaster short-circuits to Valid, so
//     the probe must be a plain-Player mobile (same trick as the EDGEWALK
//     audit probe).
//   - Houses survive their owner: BaseHouse null-checks m_Owner everywhere
//     and RestrictDecay == true makes DecayType Ageless regardless of
//     owner. PlayerBots are ephemeral (session churn deletes them), so a
//     bot house is placed THROUGH a probe owner, then Owner is cleared and
//     RestrictDecay set — the house persists in the world save forever.
//   - The house SIGN carries a free-form Name string, so "Aldric's
//     cottage" reads right even though no such mobile exists anymore.
//   - Exterior doors get Locked + the key value the ctor rolled; the keys
//     themselves die with the probe, which is exactly what we want.
//
// Commands ([BotHouses ...], GameMaster):
//   scatter <n>   place up to n houses (default 50) in the wilderness
//   list          registry summary
//   clear         delete every registered bot house (full undo)
//
// Registry: Data/Live/bot_houses.json (serial + name + loc). The houses
// themselves live in the world save; the json is bookkeeping so clear/list
// work across restarts. Stale entries (house manually deleted) are skipped.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Server;
using Server.Commands;
using Server.Items;
using Server.Mobiles;
using Server.Multis;
using Server.Multis.Deeds;

namespace Server.CustomBots
{
    public static class BotHousing
    {
        // Era-correct small houses only: the classic 7x7 old houses plus
        // the log cabin. (Two-story/tower/keep would read as too rich for
        // roadside squatters, and their footprints rarely validate in
        // rough terrain anyway.)
        public static readonly int[] HouseMultiIds =
        {
            0x64, // stone and plaster
            0x66, // fieldstone
            0x68, // small brick
            0x6A, // wooden
            0x6C, // wood and plaster
            0x6E, // thatched-roof cottage
            0x9A, // log cabin
        };

        private static readonly string[] HouseNouns =
        {
            "cottage", "cabin", "house", "homestead", "hut",
        };

        // Keep houses OFF the waypoint trails: a candidate must be at
        // least this far from every graph edge segment nearby, or a
        // placed house could wall off a route the audit said was clean.
        private const int MinTrailDistance = 12;

        // And spread them out — no shantytowns.
        private const int MinHouseSpacing = 40;

        // Candidate offsets off a rural node: close enough to be "along
        // the road", far enough to never clip it.
        private const int OffsetMin = 14;
        private const int OffsetMax = 32;

        // A node is "rural" when it's at least this far from every City
        // destination (same threshold GatherSpots uses).
        private const int CityRadius = 45;

        private static string RegistryPath => Path.Combine(
            Core.BaseDirectory, "Data", "Live", "bot_houses.json");

        private sealed class HouseRecord
        {
            public uint Serial { get; set; }
            public string Name { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
            public int MultiId { get; set; }
        }

        private static List<HouseRecord> _registry;

        public static void Configure()
        {
            CommandSystem.Register("BotHouses", AccessLevel.GameMaster, OnCommand);
        }

        // ------------------------------------------------------------------
        // Registry I/O
        // ------------------------------------------------------------------
        private static List<HouseRecord> LoadRegistry()
        {
            if (_registry != null)
            {
                return _registry;
            }

            _registry = new List<HouseRecord>();
            try
            {
                if (File.Exists(RegistryPath))
                {
                    var loaded = JsonSerializer.Deserialize<List<HouseRecord>>(
                        File.ReadAllText(RegistryPath));
                    if (loaded != null)
                    {
                        _registry = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotHousing] registry load failed: {ex.Message}");
            }

            return _registry;
        }

        private static void SaveRegistry()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath));
                File.WriteAllText(RegistryPath,
                    JsonSerializer.Serialize(_registry,
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotHousing] registry save failed: {ex.Message}");
            }
        }

        private static BaseHouse ResolveHouse(HouseRecord rec) =>
            World.FindEntity<Item>((Serial)rec.Serial) as BaseHouse;

        // ------------------------------------------------------------------
        // Command
        // ------------------------------------------------------------------
        [Usage("BotHouses scatter <n> | list | clear")]
        [Description("Scatter, list, or remove bot-owned wilderness houses.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            var sub = e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "list";

            switch (sub)
            {
                case "scatter":
                    {
                        int want = e.Length > 1 ? e.GetInt32(1) : 50;
                        int placed = Scatter(from.Map, want, out var ms, out var tried);
                        from.SendMessage(
                            $"Placed {placed}/{want} bot houses ({tried} spots tried, {ms} ms).");
                        break;
                    }
                case "clear":
                    {
                        int removed = Clear();
                        from.SendMessage($"Removed {removed} bot houses.");
                        break;
                    }
                default:
                    {
                        var reg = LoadRegistry();
                        int live = 0;
                        foreach (var rec in reg)
                        {
                            if (ResolveHouse(rec) != null)
                            {
                                live++;
                            }
                        }
                        from.SendMessage(
                            $"{reg.Count} registered bot houses, {live} in-world. " +
                            $"([BotHouses scatter <n> / clear)");
                        break;
                    }
            }
        }

        // ------------------------------------------------------------------
        // Scatter
        // ------------------------------------------------------------------
        public static int Scatter(Map map, int count, out long elapsedMs, out int tried)
        {
            var sw = Stopwatch.StartNew();
            tried = 0;

            var reg = LoadRegistry();
            // Shed records whose house no longer exists so stale entries
            // don't eat spots via the spacing check.
            reg.RemoveAll(r => ResolveHouse(r) == null);
            var graph = WaypointRegistry.Graph;
            if (map == null || map == Map.Internal || graph == null || graph.NodeCount == 0)
            {
                elapsedMs = 0;
                return 0;
            }

            // Rural node pool: nodes far from every City-typed destination.
            var rural = new List<WaypointNode>();
            foreach (var node in graph.AllNodes)
            {
                bool nearCity = false;
                foreach (var d in DestinationCatalog.All)
                {
                    if (string.IsNullOrEmpty(d.City))
                    {
                        continue;
                    }
                    if (d.Type != DestinationType.CityCenter)
                    {
                        continue;
                    }
                    if (Math.Max(Math.Abs(d.Location.X - node.Location.X),
                            Math.Abs(d.Location.Y - node.Location.Y)) < CityRadius)
                    {
                        nearCity = true;
                        break;
                    }
                }
                if (!nearCity)
                {
                    rural.Add(node);
                }
            }

            if (rural.Count == 0)
            {
                elapsedMs = 0;
                return 0;
            }

            // One hidden Player-level probe for ALL the checks — placement
            // validation needs a non-staff mobile (staff short-circuits to
            // Valid) with a real map. Same pattern as the EDGEWALK audit.
            var probe = new Rat { Controlled = true, Blessed = true, Hidden = true };
            int placed = 0;
            int attemptCap = count * 60;

            // Rejection telemetry — placement is finicky and a silent
            // 0/N scatter is undebuggable without knowing WHY.
            int rejTrail = 0, rejCrowd = 0;
            var rejCheck = new Dictionary<HousePlacementResult, int>();

            try
            {
                for (int attempt = 0; attempt < attemptCap && placed < count; attempt++)
                {
                    var node = rural[Utility.Random(rural.Count)];
                    int dx = Utility.RandomMinMax(OffsetMin, OffsetMax) *
                             (Utility.RandomBool() ? 1 : -1);
                    int dy = Utility.RandomMinMax(OffsetMin, OffsetMax) *
                             (Utility.RandomBool() ? 1 : -1);
                    int x = node.Location.X + dx;
                    int y = node.Location.Y + dy;
                    int z = map.GetAverageZ(x, y);
                    var center = new Point3D(x, y, z);
                    tried++;

                    // Never wall off a route: distance to every nearby
                    // graph edge segment must clear the trail buffer.
                    if (TooCloseToTrail(graph, x, y))
                    {
                        rejTrail++;
                        continue;
                    }

                    // Spread: not near an existing bot house.
                    bool crowded = false;
                    foreach (var rec in reg)
                    {
                        if (Math.Max(Math.Abs(rec.X - x), Math.Abs(rec.Y - y)) < MinHouseSpacing)
                        {
                            crowded = true;
                            break;
                        }
                    }
                    if (crowded)
                    {
                        rejCrowd++;
                        continue;
                    }

                    int multiId = HouseMultiIds[Utility.Random(HouseMultiIds.Length)];

                    probe.MoveToWorld(center, map);
                    var result = HousePlacement.Check(probe, multiId, center, out var toMove);
                    if (result != HousePlacementResult.Valid)
                    {
                        rejCheck[result] = rejCheck.GetValueOrDefault(result) + 1;
                        continue;
                    }
                    // toMove (yard critters/items) is fine — vanilla placement
                    // relocates them under the sign; ours can too.

                    var house = BuildHouse(probe, multiId);
                    if (house == null)
                    {
                        continue;
                    }

                    house.MoveToWorld(center, map);

                    // The probe was only scaffolding: real "owner" is the
                    // sign's name. Clearing Owner + RestrictDecay makes the
                    // house ageless and NRE-proof against bot churn.
                    house.Owner = null;
                    house.RestrictDecay = true;

                    var owner = NamePool.PickRandom(Utility.RandomBool());
                    var noun = HouseNouns[Utility.Random(HouseNouns.Length)];
                    if (house.Sign != null)
                    {
                        house.Sign.Name = $"{owner}'s {noun}";
                    }

                    // Locked doors sell "someone lives here" (and keep
                    // real players from treating it as a dungeon).
                    foreach (var door in house.Doors)
                    {
                        if (door != null)
                        {
                            door.Locked = true;
                        }
                    }

                    reg.Add(new HouseRecord
                    {
                        Serial = house.Serial.Value,
                        Name = house.Sign?.Name ?? "bot house",
                        X = x, Y = y, Z = z,
                        MultiId = multiId,
                    });
                    placed++;
                }
            }
            finally
            {
                probe.Delete();
            }

            SaveRegistry();
            sw.Stop();
            elapsedMs = sw.ElapsedMilliseconds;
            Console.WriteLine(
                $"[BotHousing] scatter: {placed} placed, {tried} tried, {elapsedMs} ms " +
                $"({BaseHouse.AllHouses.Count} houses in world)");
            if (placed < count)
            {
                var parts = new List<string> { $"trail:{rejTrail}", $"crowd:{rejCrowd}" };
                foreach (var (res, n) in rejCheck)
                {
                    parts.Add($"{res}:{n}");
                }
                Console.WriteLine($"[BotHousing] rejections — {string.Join(" ", parts)}");
            }
            return placed;
        }

        // ------------------------------------------------------------------
        // Site finding for BotHomes. Same rules as Scatter: the real
        // HousePlacement.Check (through a Player-level mobile, so no staff
        // bypass), off the trails, and spaced from every house in the world.
        // ------------------------------------------------------------------

        // The real deed for each small house, so a bot places a house the
        // way a player does: HouseDeed.OnPlacement, keys and all.
        public static HouseDeed DeedFor(int multiId) => multiId switch
        {
            0x64 => new StonePlasterHouseDeed(),
            0x66 => new FieldStoneHouseDeed(),
            0x68 => new SmallBrickHouseDeed(),
            0x6A => new WoodHouseDeed(),
            0x6C => new WoodPlasterHouseDeed(),
            0x6E => new ThatchedRoofCottageDeed(),
            0x9A => new LogCabinDeed(),
            _    => new SmallBrickHouseDeed(),
        };

        private static bool TooCloseToHouse(Map map, int x, int y, int spacing)
        {
            foreach (var h in BaseHouse.AllHouses)
            {
                if (h != null && !h.Deleted && h.Map == map &&
                    Math.Max(Math.Abs(h.X - x), Math.Abs(h.Y - y)) < spacing)
                {
                    return true;
                }
            }
            return false;
        }

        private static List<WaypointNode> _ruralNodes;

        private static List<WaypointNode> RuralNodes()
        {
            if (_ruralNodes != null)
            {
                return _ruralNodes;
            }
            _ruralNodes = new List<WaypointNode>();
            var graph = WaypointRegistry.Graph;
            if (graph == null)
            {
                return _ruralNodes;
            }
            foreach (var node in graph.AllNodes)
            {
                bool nearCity = false;
                foreach (var d in DestinationCatalog.All)
                {
                    if (d.Type == DestinationType.CityCenter && !string.IsNullOrEmpty(d.City) &&
                        Math.Max(Math.Abs(d.Location.X - node.Location.X),
                            Math.Abs(d.Location.Y - node.Location.Y)) < CityRadius)
                    {
                        nearCity = true;
                        break;
                    }
                }
                // Dungeon floors live in the far east of the map; no house there.
                if (!nearCity && node.Location.X < 5120)
                {
                    _ruralNodes.Add(node);
                }
            }
            return _ruralNodes;
        }

        // A countryside spot off a rural road node, optionally within
        // nearRange of a point. checker must be a Player-level mobile; it is
        // moved to the spot for the check and put back after.
        public static bool TryFindRuralSite(Map map, Mobile checker, int multiId,
            Point3D? near, int nearRange, out Point3D center, out WaypointNode node,
            int attempts = 1500)
        {
            center = Point3D.Zero;
            node = null;
            var graph = WaypointRegistry.Graph;
            var pool = RuralNodes();
            if (map == null || graph == null || pool.Count == 0)
            {
                return false;
            }

            var candidates = pool;
            if (near is Point3D p)
            {
                candidates = pool.FindAll(n =>
                    Math.Max(Math.Abs(n.Location.X - p.X), Math.Abs(n.Location.Y - p.Y)) <= nearRange);
                if (candidates.Count == 0)
                {
                    candidates = pool;
                }
            }

            int rejTrail = 0, rejHouse = 0, rejCheck = 0, rejPath = 0;
            for (int i = 0; i < attempts; i++)
            {
                var n = candidates[Utility.Random(candidates.Count)];
                int x = n.Location.X + Utility.RandomMinMax(OffsetMin, OffsetMax) * (Utility.RandomBool() ? 1 : -1);
                int y = n.Location.Y + Utility.RandomMinMax(OffsetMin, OffsetMax) * (Utility.RandomBool() ? 1 : -1);
                if (TooCloseToTrail(graph, x, y))
                {
                    rejTrail++;
                    continue;
                }
                if (TooCloseToHouse(map, x, y, MinHouseSpacing))
                {
                    rejHouse++;
                    continue;
                }
                var c = new Point3D(x, y, map.GetAverageZ(x, y));
                if (!CheckSite(map, checker, multiId, c))
                {
                    rejCheck++;
                    continue;
                }
                if (!CanWalkToRoad(map, checker, c, n.Location))
                {
                    rejPath++;
                    continue;
                }
                center = c;
                node = n;
                return true;
            }
            Console.WriteLine($"[BotHousing] no countryside site for 0x{multiId:X} in {attempts} tries " +
                $"({candidates.Count} road nodes): trail {rejTrail}, near a house {rejHouse}, " +
                $"placement rules {rejCheck}, no path to the road {rejPath}");
            return false;
        }

        // A spot inside a drawn polygon (vendor hot spots). Houses may sit
        // closer to the road and to each other here: that is the point of a
        // hot spot. Still never on a trail.
        public static bool TryFindSiteInPolygon(Map map, Mobile checker, int multiId,
            Point2D[] poly, int minTrail, int minSpacing, out Point3D center, int attempts = 400)
        {
            center = Point3D.Zero;
            var graph = WaypointRegistry.Graph;
            if (map == null || graph == null || poly == null || poly.Length < 3)
            {
                return false;
            }
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            foreach (var q in poly)
            {
                x0 = Math.Min(x0, q.X);
                y0 = Math.Min(y0, q.Y);
                x1 = Math.Max(x1, q.X);
                y1 = Math.Max(y1, q.Y);
            }
            for (int i = 0; i < attempts; i++)
            {
                int x = Utility.RandomMinMax(x0, x1);
                int y = Utility.RandomMinMax(y0, y1);
                if (!InPolygon(poly, x, y) || TooCloseToHouse(map, x, y, minSpacing) ||
                    TooCloseToTrail(graph, x, y, minTrail))
                {
                    continue;
                }
                var c = new Point3D(x, y, map.GetAverageZ(x, y));
                var road = graph.FindNearestNode(c);
                if (road != null && CheckSite(map, checker, multiId, c) &&
                    CanWalkToRoad(map, checker, c, road.Location))
                {
                    center = c;
                    return true;
                }
            }
            return false;
        }

        private static bool CheckSite(Map map, Mobile checker, int multiId, Point3D c)
        {
            var back = checker.Location;
            var backMap = checker.Map;
            checker.MoveToWorld(c, map);
            var result = HousePlacement.Check(checker, multiId, c, out _);
            if (backMap != null && backMap != Map.Internal)
            {
                checker.MoveToWorld(back, backMap);
            }
            return result == HousePlacementResult.Valid;
        }

        // The owner has to get between its front door and the road. These
        // small houses open to the south, so walk from just below the
        // footprint to the road node with the engine's own pathfinder; open
        // country with no way through (cliffs, a river, a thicket) is
        // turned down.
        private static bool CanWalkToRoad(Map map, Mobile checker, Point3D center, Point3D road)
        {
            int fy = center.Y + 6;
            if (!Walkable.TryFindSeedZ(map, center.X, fy, center.Z, out var fz))
            {
                return false;
            }
            var back = checker.Location;
            var backMap = checker.Map;
            checker.MoveToWorld(new Point3D(center.X, fy, fz), map);
            bool ok = new MovementPath(checker, road).Success;
            if (backMap != null && backMap != Map.Internal)
            {
                checker.MoveToWorld(back, backMap);
            }
            return ok;
        }

        public static bool InPolygon(Point2D[] poly, int x, int y)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if ((poly[i].Y > y) != (poly[j].Y > y) &&
                    x < (double)(poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static BaseHouse BuildHouse(Mobile probeOwner, int multiId)
        {
            try
            {
                // The ctor rolls door key values against the owner's bank
                // box — hence a real probe owner instead of null.
                return multiId == 0x9A
                    ? new LogCabin(probeOwner)
                    : new SmallOldHouse(probeOwner, multiId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotHousing] build 0x{multiId:X} failed: {ex.Message}");
                return null;
            }
        }

        private static bool TooCloseToTrail(WaypointGraph graph, int x, int y) =>
            TooCloseToTrail(graph, x, y, MinTrailDistance);

        private static bool TooCloseToTrail(WaypointGraph graph, int x, int y, int minTrail)
        {
            foreach (var node in graph.AllNodes)
            {
                // Cheap reject: segments can only be close if an endpoint is.
                if (Math.Max(Math.Abs(node.Location.X - x), Math.Abs(node.Location.Y - y)) > 80)
                {
                    continue;
                }
                if (Math.Max(Math.Abs(node.Location.X - x), Math.Abs(node.Location.Y - y)) < minTrail)
                {
                    return true;
                }
                foreach (var link in node.Connects)
                {
                    var other = graph.Get(link);
                    if (other == null)
                    {
                        continue;
                    }
                    if (SegmentDistance(x, y, node.Location.X, node.Location.Y, other.Location.X, other.Location.Y) < minTrail)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static double SegmentDistance(int px, int py,
            int ax, int ay, int bx, int by)
        {
            double abx = bx - ax, aby = by - ay;
            double apx = px - ax, apy = py - ay;
            double lenSq = abx * abx + aby * aby;
            double t = lenSq <= 0 ? 0 : Math.Clamp((apx * abx + apy * aby) / lenSq, 0, 1);
            double cx = ax + t * abx - px, cy = ay + t * aby - py;
            return Math.Sqrt(cx * cx + cy * cy);
        }

        // ------------------------------------------------------------------
        // Clear
        // ------------------------------------------------------------------
        public static int Clear()
        {
            var reg = LoadRegistry();
            int removed = 0;
            foreach (var rec in reg)
            {
                var house = ResolveHouse(rec);
                if (house != null)
                {
                    house.Delete();
                    removed++;
                }
            }
            reg.Clear();
            SaveRegistry();
            Console.WriteLine($"[BotHousing] cleared {removed} bot houses");
            return removed;
        }
    }
}
