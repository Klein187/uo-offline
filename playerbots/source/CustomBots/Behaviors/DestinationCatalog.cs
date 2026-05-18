// =========================================================================
// DestinationCatalog.cs — In-memory registry of destinations parsed from
// Data/Destinations/destinations.json. Loaded on world start by ModernUO
// (Configure method). Hot-reloadable via [ReloadDestinations.
//
// Each destination is a place of interest a Traveler bot might want to
// go: a specific bank spot, tavern table, vendor counter, etc. Bots roll
// destinations weighted by their BotClass (see DestinationType.cs).
//
// Routing flow:
//   1. Bot picks a Destination (class-weighted random)
//   2. Bot looks up Destination.NearestWaypoint
//   3. Waypoint graph finds path from bot's nearest waypoint to that one
//   4. TravelerBehavior walks the path
//   5. On reaching the final waypoint, take one more hop to the
//      destination's actual coord (within a few tiles, via PathFollower)
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Server;

namespace Server.CustomBots
{
    public sealed class BotDestination
    {
        public string Name              { get; init; }
        public Point3D Location         { get; init; }
        public DestinationType Type     { get; init; }
        public string City              { get; init; }
        public string NearestWaypoint   { get; init; }

        public override string ToString() =>
            $"{Name} [{Type}] @ ({Location.X},{Location.Y},{Location.Z}) ↦ wp:{NearestWaypoint}";
    }

    public static class DestinationCatalog
    {
        private static readonly object _lock = new();
        private static List<BotDestination> _all = new();
        private static Dictionary<string, BotDestination> _byName =
            new(StringComparer.OrdinalIgnoreCase);

        public static int Count => _all.Count;

        public static IReadOnlyList<BotDestination> All
        {
            get { lock (_lock) return _all.ToArray(); }
        }

        public static BotDestination GetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                _byName.TryGetValue(name, out var dest);
                return dest;
            }
        }

        // -------------------------------------------------------------------
        // Weighted-random pick. Iterates all destinations, computes each's
        // weight for the bot's class, rolls weighted random.
        //
        // Returns null only if there are zero destinations loaded.
        // -------------------------------------------------------------------
        public static BotDestination PickWeighted(BotClass cls)
        {
            BotDestination[] snapshot;
            lock (_lock) snapshot = _all.ToArray();

            if (snapshot.Length == 0) return null;

            double total = 0;
            var weights = new double[snapshot.Length];
            for (int i = 0; i < snapshot.Length; i++)
            {
                double w = DestinationWeights.GetWeight(snapshot[i].Type, cls);
                if (w < 0) w = 0;
                weights[i] = w;
                total += w;
            }

            if (total <= 0)
            {
                // All zero-weight — just uniform random.
                return snapshot[Utility.Random(snapshot.Length)];
            }

            double r = Utility.RandomDouble() * total;
            double acc = 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                acc += weights[i];
                if (r <= acc) return snapshot[i];
            }
            return snapshot[snapshot.Length - 1]; // numeric edge case
        }

        // -------------------------------------------------------------------
        // JSON load. Reads Data/Destinations/destinations.json.
        // -------------------------------------------------------------------
        public static void Configure()
        {
            try { Load(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[DestinationCatalog] load failed: {ex.Message}");
            }
        }

        public static int Load()
        {
            var path = Path.Combine(Core.BaseDirectory, "Data", "Destinations", "destinations.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[DestinationCatalog] no destinations.json at {path}; 0 destinations loaded");
                lock (_lock)
                {
                    _all = new List<BotDestination>();
                    _byName = new(StringComparer.OrdinalIgnoreCase);
                }
                return 0;
            }

            string text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (!doc.RootElement.TryGetProperty("Destinations", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine($"[DestinationCatalog] destinations.json missing 'Destinations' array");
                return 0;
            }

            var loaded = new List<BotDestination>();
            int warnings = 0;

            foreach (var el in arr.EnumerateArray())
            {
                try
                {
                    string name = el.GetProperty("Name").GetString();
                    int x = el.GetProperty("X").GetInt32();
                    int y = el.GetProperty("Y").GetInt32();
                    int z = el.TryGetProperty("Z", out var zv) ? zv.GetInt32() : 0;
                    string typeStr = el.GetProperty("Type").GetString();
                    string city = el.TryGetProperty("City", out var cv) ? cv.GetString() : "";
                    string wpName = el.TryGetProperty("NearestWaypoint", out var wpv)
                        ? wpv.GetString() : "";

                    if (!Enum.TryParse<DestinationType>(typeStr, ignoreCase: true, out var type))
                    {
                        Console.WriteLine($"[DestinationCatalog] WARN: unknown type '{typeStr}' for {name}; skipping");
                        warnings++;
                        continue;
                    }

                    loaded.Add(new BotDestination
                    {
                        Name             = name,
                        Location         = new Point3D(x, y, z),
                        Type             = type,
                        City             = city ?? "",
                        NearestWaypoint  = wpName ?? "",
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DestinationCatalog] WARN: bad entry: {ex.Message}");
                    warnings++;
                }
            }

            var byName = new Dictionary<string, BotDestination>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in loaded)
            {
                if (byName.ContainsKey(d.Name))
                {
                    Console.WriteLine($"[DestinationCatalog] WARN: duplicate name '{d.Name}'");
                    warnings++;
                    continue;
                }
                byName[d.Name] = d;
            }

            lock (_lock)
            {
                _all = loaded;
                _byName = byName;
            }

            Console.WriteLine($"[DestinationCatalog] loaded {loaded.Count} destination(s) with {warnings} warning(s)");
            return loaded.Count;
        }
    }
}
