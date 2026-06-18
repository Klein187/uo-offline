// =========================================================================
// GenerateBotsCommand.cs — [GenerateBots admin command.
//
// Places PlayerBotSpawners across the world. Two modes:
//
//   1. JSON-driven (preferred). Reads Distribution/Data/PlayerBotSpawners.json
//      and creates a PlayerBotSpawner for each entry. This is how a user
//      who downloads from GitHub gets the canonical placements.
//
//   2. Fallback hardcoded list. If the JSON file isn't present, falls back
//      to a small built-in list of 9 city banks. Lets the command produce
//      *something* on fresh installs even before the JSON has been authored.
//
// Idempotent: deletes existing PlayerBotSpawners first, then re-creates.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class GenerateBotsCommand
    {
        private static readonly string JsonPath =
            Path.Combine(Core.BaseDirectory, "Data", "PlayerBotSpawners.json");

        // City spawn regions. Each gets a WEIGHTED share of the total
        // population (BotPopulation.TargetCount). Britain is the hub so it
        // carries the largest share; the rest split the remainder. Each
        // city's allotment is spread across multiple spawners so no single
        // A city may have multiple sub-anchor points — Britain in
        // particular sprawls east-west across enough tiles that one
        // anchor + ±35 jitter doesn't cover the whole city. Adding an
        // east anchor (1605, 1640) lets spawners land in both halves.
        // SubAnchors is OPTIONAL: cities with a null/empty list use only
        // the primary (X, Y) coord. The distributor jitters from a
        // randomly-picked anchor for each spawner.
        // Name is matched (case-insensitive) against BotDestination.City
        // so the distributor can pull a city's real destinations out of
        // the catalog when randomizing spawn points.
        private sealed record CityRegion(
            string Name, string MapName, int X, int Y, int Z, double Weight,
            Point2D[] SubAnchors = null);

        private static readonly CityRegion[] Cities =
        {
            // Britain — anchors for west (city hall / bank area) AND east
            // (alchemist / sewer entrance area). Spawners now distribute
            // across both halves instead of clustering on west side only.
            new("Britain",    "Felucca", 1434, 1697, 0, 2.5,
                SubAnchors: new[]
                {
                    new Point2D(1434, 1697),  // west — same as primary
                    new Point2D(1605, 1640),  // east — alchemist/sewer area
                }),
            new("Vesper",     "Felucca", 2891,  678, 0, 1.0),
            new("Trinsic",    "Felucca", 1832, 2839, 0, 1.0),
            new("Yew",        "Felucca",  643,  858, 0, 0.8),
            new("Minoc",      "Felucca", 2511,  564, 0, 0.8),
            new("Magincia",   "Felucca", 3680, 2155, 0, 0.8),
            new("Jhelom",     "Felucca", 1417, 3821, 0, 0.7),
            new("Skara Brae", "Felucca",  591, 2147, 0, 0.7),
            new("Moonglow",   "Felucca", 4471, 1175, 0, 0.7),
        };

        // Roaming-population mix, round-robin across the spawners that AREN'T
        // pinned to a destination. BankSitters and Shoppers are no longer in
        // here — they're placed explicitly, one spawner per bank/vendor
        // arrival point (see LoadFromFallback), so they spread evenly across
        // every point instead of piling onto one. Roamers still become
        // Shoppers/BankSitters organically when they arrive at a destination.
        private static readonly string[] BehaviorMix =
        {
            "Traveler", "Traveler", "Wander", "Traveler",
        };

        // Bots per arrival point for the pinned crowds. Small, so multiple
        // points read as a spread-out crowd rather than a clump on one tile.
        private const int BankSittersPerArrival = 2;
        private const int ShoppersPerArrival    = 1;

        public static void Configure()
        {
            CommandSystem.Register("GenerateBots", AccessLevel.Administrator, OnCommand);
        }

        // Called by BotStartupManager on world load. If the world has NO
        // PlayerBotSpawners at all (fresh server, or none ever placed), lay
        // down the default set so the world isn't empty. If spawners
        // already exist, does nothing — the startup manager respawns those
        // itself. Returns the number of spawners placed (0 if none needed).
        public static int EnsurePopulation()
        {
            // Already have spawners? Leave them; nothing to do here.
            foreach (var item in World.Items.Values)
            {
                if (item is PlayerBotSpawner sp && !sp.Deleted)
                    return 0;
            }

            // No spawners exist — lay down the default population. Prefer
            // the authored JSON, fall back to the built-in list. Passing
            // null for `from` is safe (TryPlace null-checks it).
            int placed = File.Exists(JsonPath)
                ? LoadFromJson(null)
                : LoadFromFallback(null);

            Console.WriteLine(
                $"[GenerateBots] EnsurePopulation: no spawners found, " +
                $"placed {placed} default spawner(s).");
            return placed;
        }

        [Usage("GenerateBots")]
        [Description(
            "Places PlayerBotSpawners across the world. " +
            "Reads Data/PlayerBotSpawners.json if present, else uses a built-in fallback list. " +
            "Removes existing PlayerBotSpawners first."
        )]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null)
            {
                return;
            }

            ClearExistingSpawners(from);

            int placed = 0;

            if (File.Exists(JsonPath))
            {
                from.SendMessage($"GenerateBots: loading {JsonPath}...");
                placed = LoadFromJson(from);
            }
            else
            {
                from.SendMessage("GenerateBots: no JSON file found; using built-in fallback list.");
                placed = LoadFromFallback(from);
            }

            from.SendMessage($"GenerateBots: placed {placed} spawner(s).");
            from.SendMessage("Walk to any bank to see the crowd. Bots spawn over the next few seconds.");
        }

        // Called by [SetBotPopulation. Clears existing spawners and lays
        // down a fresh set sized to the current BotPopulation.TargetCount.
        // Returns the number of spawners placed.
        public static int RegenerateForPopulation()
        {
            // Clear existing spawners (null-safe — no Mobile needed).
            var existing = new List<PlayerBotSpawner>();
            foreach (var item in World.Items.Values)
            {
                if (item is PlayerBotSpawner pbs && !pbs.Deleted)
                    existing.Add(pbs);
            }
            foreach (var pbs in existing)
            {
                try { pbs.Delete(); } catch { }
            }

            // Also clear existing bots so the old population doesn't linger
            // on top of the new one.
            var bots = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted)
                    bots.Add(bot);
            }
            foreach (var bot in bots)
            {
                try { bot.Delete(); } catch { }
            }

            // Place the fresh distributed set, then respawn it immediately.
            int placed = LoadFromFallback(null);

            // Collect the spawners FIRST, then respawn from the list. Respawn()
            // spawns bots and their equipment, which adds to World.Items — so
            // iterating World.Items.Values live while respawning throws
            // "Collection was modified".
            var fresh = new List<PlayerBotSpawner>();
            foreach (var item in World.Items.Values)
            {
                if (item is PlayerBotSpawner sp && !sp.Deleted)
                {
                    fresh.Add(sp);
                }
            }
            foreach (var sp in fresh)
            {
                try { sp.Respawn(); } catch { }
            }
            return placed;
        }

        private static void ClearExistingSpawners(Mobile from)
        {
            var existing = new List<PlayerBotSpawner>();
            foreach (var item in World.Items.Values)
            {
                if (item is PlayerBotSpawner pbs && !pbs.Deleted)
                {
                    existing.Add(pbs);
                }
            }
            foreach (var pbs in existing)
            {
                pbs.Delete();
            }
            if (existing.Count > 0)
            {
                from.SendMessage($"GenerateBots: removed {existing.Count} existing spawner(s).");
            }
        }

        private static int LoadFromJson(Mobile from)
        {
            int placed = 0;
            try
            {
                var json = File.ReadAllText(JsonPath);
                var data = JsonSerializer.Deserialize<ExportBotSpawnersCommand.Wrapper>(json);
                if (data?.spawners == null)
                {
                    from?.SendMessage("GenerateBots: JSON parsed but contains no spawners.");
                    return 0;
                }

                foreach (var rec in data.spawners)
                {
                    if (TryPlace(rec.map, rec.x, rec.y, rec.z, rec.behaviorName, rec.amount, rec.boundsRadius, from))
                    {
                        placed++;
                    }
                }
            }
            catch (Exception ex)
            {
                from?.SendMessage($"GenerateBots: failed to parse JSON ({ex.Message}). No spawners placed.");
            }
            return placed;
        }

        // Distribute BotPopulation.TargetCount across the cities. For each
        // city, the spawners are placed at RANDOM points: real destinations
        // (banks, shops, the forge, etc.) when the catalog has any for
        // this city, plus jittered offsets from the city center as filler.
        // This is the redesign that breaks up the old "everything stacks
        // on the city-center tile" behavior — spawners now spread across
        // each city's actual interesting spots.
        private static int LoadFromFallback(Mobile from)
        {
            int target = BotPopulation.TargetCount;
            if (target < 1) target = 1;

            double totalWeight = 0;
            foreach (var c in Cities) totalWeight += c.Weight;
            if (totalWeight <= 0) return 0;

            int placed = 0;
            int botsAssigned = 0;
            int behaviorIdx = 0;

            for (int ci = 0; ci < Cities.Length; ci++)
            {
                var city = Cities[ci];

                // This city's share of the target.
                int cityBots;
                if (ci == Cities.Length - 1)
                {
                    cityBots = target - botsAssigned;
                }
                else
                {
                    cityBots = (int)Math.Round(target * (city.Weight / totalWeight));
                }
                if (cityBots < 1) cityBots = 1;
                botsAssigned += cityBots;

                // Arrival-point pools (placed in the map editor) for the pinned
                // crowds, plus the general roaming pool.
                var bankSpots   = BuildArrivalSpots(city, BankTypes);
                var vendorSpots = BuildArrivalSpots(city, ShopVendorTypes);
                var roamSpots   = BuildCitySpots(city);

                // Bots consumed by the pinned crowds — subtracted from the
                // city's share so the roaming population fills the rest.
                int cityPinned = 0;

                // --- Pinned crowds: ONE spawner per arrival point, so bots
                // spread evenly across EVERY bank/vendor point instead of a
                // random few getting a pile and the rest getting none. Tight
                // bounds (3) keeps each little crowd on its point.
                foreach (var pt in bankSpots)
                {
                    if (TryPlace(city.MapName, pt.X, pt.Y, pt.Z,
                                 "BankSitter", BankSittersPerArrival, 3, from))
                    {
                        placed++;
                        cityPinned += BankSittersPerArrival;
                    }
                }
                // No authored bank arrival points, but we know the city's bank
                // coord: drop one banker crowd there so the bank isn't empty.
                if (bankSpots.Count == 0 &&
                    BotPanelActions.CityCoords.TryGetValue(city.Name, out var bankCoord))
                {
                    if (TryPlace(city.MapName, bankCoord.X, bankCoord.Y, bankCoord.Z,
                                 "BankSitter", BankSittersPerArrival, 3, from))
                    {
                        placed++;
                        cityPinned += BankSittersPerArrival;
                    }
                }
                foreach (var pt in vendorSpots)
                {
                    if (TryPlace(city.MapName, pt.X, pt.Y, pt.Z,
                                 "Shopper", ShoppersPerArrival, 3, from))
                    {
                        placed++;
                        cityPinned += ShoppersPerArrival;
                    }
                }

                // --- Roaming population: the rest of the city's share, spread
                // across jittered city points as Travelers/Wanderers (which
                // also become Shoppers/BankSitters organically on arrival).
                int remaining = cityBots - cityPinned;
                while (remaining > 0)
                {
                    int amount = Math.Min(BotPopulation.BotsPerSpawner, remaining);
                    remaining -= amount;

                    var spot = roamSpots[Utility.Random(roamSpots.Count)];
                    string behavior = BehaviorMix[behaviorIdx % BehaviorMix.Length];
                    behaviorIdx++;

                    if (TryPlace(city.MapName, spot.X, spot.Y, spot.Z,
                                 behavior, amount,
                                 BotPopulation.SpawnerBoundsRadius, from))
                    {
                        placed++;
                    }
                }
            }

            from?.SendMessage(
                $"GenerateBots: distributed ~{botsAssigned} bots across " +
                $"{Cities.Length} cities ({placed} spawners).");
            return placed;
        }

        // Build the pool of candidate spawn points for a city: every
        // matching destination in the catalog, plus several jittered
        // offsets from each anchor. The jittered offsets guarantee
        // variety even for cities with zero (or one) destinations marked.
        // Cities with SubAnchors (currently just Britain, with a west
        // and east anchor) distribute jitters across all anchors so
        // sprawling towns don't cluster their spawners in one quadrant.
        private static List<Point3D> BuildCitySpots(CityRegion city)
        {
            var spots = new List<Point3D>();

            // Real destinations belonging to this city.
            foreach (var d in DestinationCatalog.All)
            {
                if (string.Equals(d.City, city.Name,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    spots.Add(d.Location);
                }
            }

            // Anchors: use SubAnchors if defined, otherwise fall back to
            // the single primary (X, Y) coord. Each anchor gets the SAME
            // number of jitters — so a 2-anchor city like Britain ends up
            // with twice as many "anchor jitter" spots as a 1-anchor city,
            // which is the right scaling (more sprawl = more spots needed).
            var anchors = (city.SubAnchors != null && city.SubAnchors.Length > 0)
                ? city.SubAnchors
                : new[] { new Point2D(city.X, city.Y) };

            const int JittersPerAnchor = 8;
            const int JitterDist       = 35;
            foreach (var anchor in anchors)
            {
                for (int i = 0; i < JittersPerAnchor; i++)
                {
                    int dx = Utility.RandomMinMax(-JitterDist, JitterDist);
                    int dy = Utility.RandomMinMax(-JitterDist, JitterDist);
                    spots.Add(new Point3D(anchor.X + dx, anchor.Y + dy, city.Z));
                }
            }

            return spots;
        }

        // Vendor-shop destination types a Shopper browses at (Forge/Dock are
        // crafter stations, not shopping, so they're excluded).
        private static readonly HashSet<DestinationType> ShopVendorTypes = new()
        {
            DestinationType.VendorSmith, DestinationType.VendorMage,
            DestinationType.VendorTailor, DestinationType.VendorCarpenter,
            DestinationType.VendorBowyer, DestinationType.VendorAlchemist,
            DestinationType.VendorWeaponer, DestinationType.VendorProvisioner,
        };

        private static readonly HashSet<DestinationType> BankTypes = new()
        {
            DestinationType.Bank,
        };

        // Spawn spots for a destination-pinned behavior: the ARRIVAL POINTS
        // placed in the map editor for each matching destination in this city
        // (the precise standable tiles — counter/stall for shops, the teller
        // spot for banks), so bots land exactly where you placed them. Falls
        // back to a destination's own coord only if it has no arrival points.
        // Used for Shoppers (vendor types) and BankSitters (bank type).
        private static List<Point3D> BuildArrivalSpots(
            CityRegion city, HashSet<DestinationType> types)
        {
            var spots = new List<Point3D>();
            foreach (var d in DestinationCatalog.All)
            {
                if (!string.Equals(d.City, city.Name, StringComparison.OrdinalIgnoreCase)
                    || !types.Contains(d.Type))
                {
                    continue;
                }
                if (d.Arrivals != null && d.Arrivals.Count > 0)
                {
                    foreach (var a in d.Arrivals)
                    {
                        spots.Add(a.Point);
                    }
                }
                else
                {
                    spots.Add(d.Location);
                }
            }
            return spots;
        }

        // Common path used by both JSON and fallback. Returns true on
        // successful placement (false only on unknown map name).
        private static bool TryPlace(
            string mapName, int x, int y, int z,
            string behaviorName, int amount, int boundsRadius,
            Mobile from)
        {
            var map = Map.Parse(mapName);
            if (map == null || map == Map.Internal)
            {
                from?.SendMessage($"GenerateBots: unknown map '{mapName}', skipping.");
                return false;
            }

            if (boundsRadius < 1) boundsRadius = 10;
            if (amount       < 1) amount       = 1;

            var loc = new Point3D(x, y, z);
            var bounds = new Rectangle3D(
                new Point3D(x - boundsRadius, y - boundsRadius, z - 5),
                new Point3D(x + boundsRadius, y + boundsRadius, z + 20)
            );

            var spawner = new PlayerBotSpawner(
                behaviorName: behaviorName,
                amount:       amount,
                minDelay:     TimeSpan.FromMinutes(5),
                maxDelay:     TimeSpan.FromMinutes(15)
            );
            spawner.SpawnBounds   = bounds;
            spawner.UseSpiralScan = true;
            spawner.MoveToWorld(loc, map);
            spawner.Respawn();
            return true;
        }
    }
}
