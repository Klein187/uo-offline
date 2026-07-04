// =========================================================================
// ForgeStationCommand.cs — find the world's smithies and mint Forge stations.
//
// Smith bots (BotClass.Smith) station at DestinationType.Forge and "work"
// there (CrafterBehavior). But forges are static map fixtures, not NPCs, so
// the vendor-driven [exportdestinations can't find them — and with zero Forge
// destinations placed, a smith never routes to a station and never works.
//
// [exportforges
//     Sweeps each town region for forge + anvil tiles (the authoritative
//     graphic-id set ModernUO's DefBlacksmithy uses), clusters forge tiles
//     standing together into one smithy, keeps only clusters that also have a
//     nearby anvil (a real forge-and-anvil shop, not a lone decorative forge),
//     computes a few adjacent standable Arrival tiles the bot can route to,
//     fills NearestWaypoint from the waypoint graph, and writes
//     Data/Destinations/forges_generated.json for review. Merge the Forge
//     entries into destinations.json and run [ReloadDestinations.
//     Smithies with NO waypoint within 38 tiles are flagged: those are your
//     coverage gaps to [MarkWay.
//
// Mirrors DestinationToolsCommand's export shape; forges go to their own
// file so they don't clobber the vendor export.
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
    public static class ForgeStationCommand
    {
        private static readonly string OutPath = Path.Combine(
            Core.BaseDirectory, "Data", "Destinations", "forges_generated.json");

        // Town centers — smith shops sit well within CityScanRadius of these.
        // (Kept local so the command is self-contained.)
        private static readonly (string city, int x, int y)[] CityCenters =
        {
            ("Britain", 1434, 1690), ("Vesper", 2899, 676), ("Minoc", 2466, 437),
            ("Trinsic", 1900, 2780), ("Yew", 632, 858), ("Skara Brae", 596, 2138),
            ("Moonglow", 4442, 1172), ("Jhelom", 1383, 3815), ("Nujel'm", 3732, 1279),
            ("Magincia", 3714, 2220), ("Cove", 2230, 1200), ("Buccaneer's Den", 2706, 2150),
        };

        // How far around each town center to sweep for forge/anvil tiles.
        private const int CityScanRadius = 200;

        // Forge tiles within this Chebyshev distance fuse into one smithy.
        private const int ClusterRange = 6;

        // A forge cluster only becomes a smith station if an anvil sits within
        // this distance — that's what makes it a real forge-and-anvil shop and
        // not a standalone decorative forge (kitchen, glassblower, etc.).
        private const int AnvilNear = 6;

        // Most Arrival tiles to emit per smithy.
        private const int MaxArrivals = 4;

        // Authoritative forge/anvil graphic ids (see DefBlacksmithy).
        private static bool IsAnvilId(int id) => id is 4015 or 4016 or 11733 or 11734;
        private static bool IsForgeId(int id) => id is 4017 or (>= 6522 and <= 6569) or 11736;

        public static void Configure()
        {
            CommandSystem.Register("exportforges", AccessLevel.GameMaster, Export_OnCommand);
        }

        private static string NearestCity(int x, int y)
        {
            string best = "Britain";
            double bd = double.MaxValue;
            foreach (var (c, cx, cy) in CityCenters)
            {
                double d = (double)(cx - x) * (cx - x) + (double)(cy - y) * (cy - y);
                if (d < bd)
                {
                    bd = d;
                    best = c;
                }
            }
            return best;
        }

        // ---- [exportforges --------------------------------------------------
        private static void Export_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            var map = from.Map ?? Map.Felucca;

            var forges = new List<Point3D>();
            var anvils = new List<Point3D>();
            CollectFixtures(map, forges, anvils);

            from.SendMessage($"Found {forges.Count} forge tile(s), {anvils.Count} anvil tile(s).");
            if (forges.Count == 0)
            {
                from.SendMessage("No forges found — nothing written.");
                return;
            }

            var clusters = ClusterForges(forges);

            var counters = new Dictionary<string, int>();
            var entries = new List<Dictionary<string, object>>();
            int skippedNoAnvil = 0, gaps = 0;

            foreach (var cluster in clusters)
            {
                // Representative forge tile = the one nearest the cluster centroid.
                double cx = cluster.Average(p => (double)p.X);
                double cy = cluster.Average(p => (double)p.Y);
                var rep = cluster.OrderBy(p =>
                    (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)).First();

                // Require an anvil nearby — a real forge-and-anvil smithy.
                bool hasAnvil = anvils.Any(a =>
                    cluster.Any(f => Math.Max(Math.Abs(a.X - f.X), Math.Abs(a.Y - f.Y)) <= AnvilNear));
                if (!hasAnvil)
                {
                    skippedNoAnvil++;
                    continue;
                }

                string city = NearestCity(rep.X, rep.Y);
                counters.TryGetValue(city, out var n);
                counters[city] = n + 1;
                string name = $"{city} Forge" + (n > 0 ? $" {n + 1}" : "");

                string wp = "";
                var node = WaypointRegistry.Graph.FindNearestNode(rep);
                if (node != null)
                {
                    wp = node.Name;
                    int wd = Math.Max(Math.Abs(node.Location.X - rep.X),
                                      Math.Abs(node.Location.Y - rep.Y));
                    if (wd > 38)
                    {
                        gaps++;
                        from.SendMessage($"GAP: '{name}' ({rep.X},{rep.Y}) nearest waypoint '{node.Name}' is {wd} tiles — [MarkWay needed.");
                    }
                }

                var arrivals = BuildArrivals(map, cluster, wp);

                entries.Add(new Dictionary<string, object>
                {
                    ["Name"] = name,
                    ["X"] = rep.X,
                    ["Y"] = rep.Y,
                    ["Z"] = rep.Z,
                    ["Type"] = "Forge",
                    ["City"] = city,
                    ["NearestWaypoint"] = wp,
                    ["Arrivals"] = arrivals,
                });
            }

            var output = new Dictionary<string, object>
            {
                ["_comment"] = "GENERATED by [exportforges from world forge/anvil tiles. " +
                               "Review, merge the Forge entries into destinations.json, then run [ReloadDestinations.",
                ["Destinations"] = entries.OrderBy(d => (string)d["City"]).ThenBy(d => (string)d["Name"]).ToList(),
            };

            File.WriteAllText(OutPath, JsonSerializer.Serialize(output,
                new JsonSerializerOptions { WriteIndented = true }));

            from.SendMessage($"Wrote {entries.Count} Forge station(s) to forges_generated.json.");
            if (skippedNoAnvil > 0)
            {
                from.SendMessage($"Skipped {skippedNoAnvil} forge cluster(s) with no anvil nearby (decorative).");
            }
            from.SendMessage($"{gaps} waypoint coverage GAP(s) — see messages above for [MarkWay spots.");
        }

        // Sweep each town region's static tiles and world items for forge/anvil
        // fixtures. Static tiles cover stock-map town forges; the item sweep
        // also catches forge/anvil addons placed as items.
        private static void CollectFixtures(Map map, List<Point3D> forges, List<Point3D> anvils)
        {
            foreach (var (_, cxCenter, cyCenter) in CityCenters)
            {
                int minX = cxCenter - CityScanRadius, maxX = cxCenter + CityScanRadius;
                int minY = cyCenter - CityScanRadius, maxY = cyCenter + CityScanRadius;

                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        foreach (var tile in map.Tiles.GetStaticTiles(x, y))
                        {
                            if (IsForgeId(tile.ID))
                            {
                                forges.Add(new Point3D(x, y, tile.Z));
                            }
                            else if (IsAnvilId(tile.ID))
                            {
                                anvils.Add(new Point3D(x, y, tile.Z));
                            }
                        }
                    }
                }

                foreach (var item in map.GetItemsInRange(new Point3D(cxCenter, cyCenter, 0), CityScanRadius))
                {
                    if (item == null || item.Deleted)
                    {
                        continue;
                    }
                    if (IsForgeId(item.ItemID))
                    {
                        forges.Add(item.Location);
                    }
                    else if (IsAnvilId(item.ItemID))
                    {
                        anvils.Add(item.Location);
                    }
                }
            }
        }

        // Greedy single-link clustering: forge tiles within ClusterRange of any
        // tile already in a cluster fuse into that smithy.
        private static List<List<Point3D>> ClusterForges(List<Point3D> forges)
        {
            var clusters = new List<List<Point3D>>();
            foreach (var f in forges)
            {
                var c = clusters.FirstOrDefault(c => c.Any(p =>
                    Math.Max(Math.Abs(p.X - f.X), Math.Abs(p.Y - f.Y)) <= ClusterRange));
                if (c != null)
                {
                    c.Add(f);
                }
                else
                {
                    clusters.Add(new List<Point3D> { f });
                }
            }
            return clusters;
        }

        // Standable tiles adjacent to the smithy's forge tiles that a bot can
        // route to and stand on. Prefers tiles touching a forge (dist 1), then
        // a wider ring, and never a tile that holds a forge/anvil fixture.
        private static List<Dictionary<string, object>> BuildArrivals(
            Map map, List<Point3D> cluster, string wp)
        {
            var seen = new HashSet<(int, int)>();
            var spots = new List<Dictionary<string, object>>();
            var wps = string.IsNullOrEmpty(wp)
                ? Array.Empty<string>()
                : new[] { wp };

            for (int ring = 1; ring <= 2 && spots.Count < MaxArrivals; ring++)
            {
                foreach (var f in cluster)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        for (int dy = -ring; dy <= ring; dy++)
                        {
                            // Only the tiles on THIS ring's edge.
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                            {
                                continue;
                            }

                            int ax = f.X + dx, ay = f.Y + dy;
                            if (!seen.Add((ax, ay)))
                            {
                                continue;
                            }

                            // Don't stand on another fixture tile.
                            if (HasFixture(map, ax, ay))
                            {
                                continue;
                            }

                            if (!map.CanSpawnMobile(ax, ay, f.Z - 12, f.Z + 12, false, false, out int az))
                            {
                                continue;
                            }

                            spots.Add(new Dictionary<string, object>
                            {
                                ["X"] = ax,
                                ["Y"] = ay,
                                ["Z"] = az,
                                ["Waypoints"] = wps,
                            });

                            if (spots.Count >= MaxArrivals)
                            {
                                return spots;
                            }
                        }
                    }
                }
            }
            return spots;
        }

        private static bool HasFixture(Map map, int x, int y)
        {
            foreach (var tile in map.Tiles.GetStaticTiles(x, y))
            {
                if (IsForgeId(tile.ID) || IsAnvilId(tile.ID))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
