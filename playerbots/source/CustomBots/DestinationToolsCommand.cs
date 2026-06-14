// =========================================================================
// DestinationToolsCommand.cs — ground-truth destination coordinates.
//
// [destaudit
//     For every entry in destinations.json: how far is the nearest real
//     vendor NPC? Flags entries with no vendor nearby (junk/missing coords).
//
// [exportdestinations
//     Walks the live world's vendor NPCs, maps each to a DestinationType,
//     clusters same-type vendors standing together into one shop, names it
//     (nearest named sign item if any, else "City Type N"), fills
//     NearestWaypoint from the waypoint graph, and writes
//     Data/Destinations/destinations_generated.json for review.
//     Vendors with NO waypoint within 38 tiles are flagged: those are your
//     coverage gaps to [MarkWay.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Server;
using Server.Commands;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class DestinationToolsCommand
    {
        private static readonly string JsonPath = Path.Combine(
            Core.BaseDirectory, "Data", "Destinations", "destinations.json");
        private static readonly string OutPath = Path.Combine(
            Core.BaseDirectory, "Data", "Destinations", "destinations_generated.json");

        public static void Configure()
        {
            CommandSystem.Register("destaudit",          AccessLevel.GameMaster, Audit_OnCommand);
            CommandSystem.Register("exportdestinations", AccessLevel.GameMaster, Export_OnCommand);
        }

        // ---- vendor class name -> DestinationType --------------------------
        private static string TypeFor(Mobile m)
        {
            var n = m.GetType().Name;
            return n switch
            {
                "Banker" or "GypsyBanker" or "Minter"            => "Bank",
                "Barkeeper" or "TavernKeeper" or "Waiter"        => "Tavern",
                "InnKeeper"                                      => "Inn",
                "Blacksmith" or "IronWorker" or "Armorer"        => "VendorSmith",
                "Weaponsmith"                                    => "VendorWeaponer",
                "Mage" or "HolyMage"                             => "VendorMage",
                "Tailor" or "Weaver" or "Furtrader" or "Cobbler" => "VendorTailor",
                "Carpenter" or "Architect"                       => "VendorCarpenter",
                "Bowyer" or "Fletcher"                           => "VendorBowyer",
                "Alchemist" or "Herbalist"                       => "VendorAlchemist",
                "Provisioner" or "Cook" or "Baker" or "Butcher"
                    or "Farmer" or "Jeweler" or "Tinker"
                    or "Mapmaker" or "Scribe"                    => "VendorProvisioner",
                "Healer" or "EvilHealer" or "PricedHealer"       => "Healer",
                "AnimalTrainer" or "GypsyAnimalTrainer"
                    or "Veterinarian"                            => "Stables",
                _ => null,
            };
        }

        private static readonly (string city, int x, int y)[] CityCenters =
        {
            ("Britain", 1434, 1690), ("Vesper", 2899, 676), ("Minoc", 2466, 437),
            ("Trinsic", 1900, 2780), ("Yew", 632, 858), ("Skara Brae", 596, 2138),
            ("Moonglow", 4442, 1172), ("Jhelom", 1383, 3815), ("Nujel'm", 3732, 1279),
            ("Magincia", 3714, 2220), ("Cove", 2230, 1200), ("Buccaneer's Den", 2706, 2150),
        };

        private static string NearestCity(int x, int y)
        {
            string best = "Britain"; double bd = double.MaxValue;
            foreach (var (c, cx, cy) in CityCenters)
            {
                double d = (double)(cx - x) * (cx - x) + (double)(cy - y) * (cy - y);
                if (d < bd) { bd = d; best = c; }
            }
            return best;
        }

        private static List<Mobile> WorldVendors() =>
            World.Mobiles.Values
                .Where(m => !m.Deleted && m.Map == Map.Felucca && TypeFor(m) != null)
                .Cast<Mobile>().ToList();

        // ---- [destaudit ------------------------------------------------------
        private static void Audit_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (!File.Exists(JsonPath)) { from.SendMessage("destinations.json not found."); return; }

            var vendors = WorldVendors();
            using var doc = JsonDocument.Parse(File.ReadAllText(JsonPath));
            int ok = 0, suspect = 0;

            foreach (var d in doc.RootElement.GetProperty("Destinations").EnumerateArray())
            {
                string name = d.GetProperty("Name").GetString();
                int x = d.GetProperty("X").GetInt32(), y = d.GetProperty("Y").GetInt32();

                double best = double.MaxValue;
                foreach (var v in vendors)
                {
                    double dist = Math.Max(Math.Abs(v.X - x), Math.Abs(v.Y - y));
                    if (dist < best) best = dist;
                }

                if (best <= 15) { ok++; }
                else
                {
                    suspect++;
                    from.SendMessage($"SUSPECT: '{name}' ({x},{y}) — nearest vendor {(best == double.MaxValue ? "none" : ((int)best) + " tiles")}");
                    Console.WriteLine($"[destaudit] SUSPECT '{name}' ({x},{y}) nearest vendor {(int)Math.Min(best, 9999)} tiles");
                }
            }
            from.SendMessage($"Audit: {ok} look fine (vendor within 15 tiles), {suspect} suspect.");
        }

        // ---- [exportdestinations ---------------------------------------------
        private static void Export_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            var vendors = WorldVendors();
            from.SendMessage($"Scanning {vendors.Count} vendor NPCs...");

            // Cluster: same type within 10 tiles = one shop.
            var clusters = new List<(string type, List<Mobile> members)>();
            foreach (var v in vendors)
            {
                var t = TypeFor(v);
                var c = clusters.FirstOrDefault(c => c.type == t && c.members.Any(m =>
                    Math.Max(Math.Abs(m.X - v.X), Math.Abs(m.Y - v.Y)) <= 10));
                if (c.members != null) c.members.Add(v);
                else clusters.Add((t, new List<Mobile> { v }));
            }

            var counters = new Dictionary<string, int>();
            var entries = new List<Dictionary<string, object>>();
            int gaps = 0;

            foreach (var (type, members) in clusters)
            {
                // Representative: member nearest the cluster centroid — an
                // exact tile a real NPC occupies (true interior Z included).
                double cx = members.Average(m => (double)m.X);
                double cy = members.Average(m => (double)m.Y);
                var rep = members.OrderBy(m =>
                    (m.X - cx) * (m.X - cx) + (m.Y - cy) * (m.Y - cy)).First();

                string city = NearestCity(rep.X, rep.Y);
                string key = $"{city}|{type}";
                counters.TryGetValue(key, out var n); counters[key] = n + 1;
                string nice = type.StartsWith("Vendor") ? type.Substring(6) : type;
                string name = $"{city} {nice}" + (n > 0 ? $" {n + 1}" : "");

                string wp = "";
                var node = WaypointRegistry.Graph.FindNearestNode(rep.Location);
                if (node != null)
                {
                    int wd = Math.Max(Math.Abs(node.Location.X - rep.X),
                                      Math.Abs(node.Location.Y - rep.Y));
                    wp = node.Name;
                    if (wd > 38)
                    {
                        gaps++;
                        Console.WriteLine($"[export] GAP: '{name}' ({rep.X},{rep.Y}) nearest waypoint '{node.Name}' is {wd} tiles — [MarkWay needed");
                    }
                }

                entries.Add(new Dictionary<string, object>
                {
                    ["Name"] = name, ["X"] = rep.X, ["Y"] = rep.Y, ["Z"] = rep.Z,
                    ["Type"] = type, ["City"] = city, ["NearestWaypoint"] = wp,
                });
            }

            var output = new Dictionary<string, object>
            {
                ["_comment"] = "GENERATED by [exportdestinations from live vendor NPCs. " +
                               "Review, then replace destinations.json and run [ReloadDestinations.",
                ["Destinations"] = entries.OrderBy(d => d["City"]).ThenBy(d => d["Name"]).ToList(),
            };

            File.WriteAllText(OutPath, JsonSerializer.Serialize(output,
                new JsonSerializerOptions { WriteIndented = true }));

            from.SendMessage($"Wrote {entries.Count} destinations to destinations_generated.json.");
            from.SendMessage($"{gaps} waypoint coverage GAP(s) — see console for [MarkWay spots.");
        }
    }
}
