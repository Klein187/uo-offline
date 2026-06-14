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
    // One place a bot can stand to "use" a destination, with its own
    // route waypoint options. A destination may have several; a bot picks
    // one to stand at, then routes via the best of that spot's waypoints.
    public sealed class ArrivalSpot
    {
        public Point3D Point            { get; init; }
        public IReadOnlyList<string> Waypoints { get; init; } = System.Array.Empty<string>();
    }

    public sealed class BotDestination
    {
        public string Name              { get; init; }
        public Point3D Location         { get; init; }
        public DestinationType Type     { get; init; }
        public string City              { get; init; }
        public string NearestWaypoint   { get; init; }

        // Optional reachable arrival tile. When set, the bot routes here and
        // arrives here, instead of chasing Location (which may sit behind a
        // wall). Null = use Location as before.
        public Point3D? ArrivalPoint    { get; init; }

        // Multiple arrival spots (each with its own waypoint options). When
        // non-empty, a bot picks one spot to stand at. Falls back to a
        // single synthesized spot from ArrivalPoint/NearestWaypoint, then
        // to Location, so older data keeps working.
        public IReadOnlyList<ArrivalSpot> Arrivals { get; init; } =
            System.Array.Empty<ArrivalSpot>();

        public ArrivalSpot PickArrival()
        {
            if (Arrivals != null && Arrivals.Count > 0)
                return Arrivals[Utility.Random(Arrivals.Count)];
            return null;
        }

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
            // Numeric edge case — return the last NON-zero-weight entry so
            // an excluded destination (weight 0) can never slip through.
            for (int i = snapshot.Length - 1; i >= 0; i--)
                if (weights[i] > 0) return snapshot[i];
            return snapshot[snapshot.Length - 1];
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

                    Point3D? arrival = null;
                    if (el.TryGetProperty("ArrivalX", out var axv) &&
                        el.TryGetProperty("ArrivalY", out var ayv))
                    {
                        int az = el.TryGetProperty("ArrivalZ", out var azv) ? azv.GetInt32() : z;
                        arrival = new Point3D(axv.GetInt32(), ayv.GetInt32(), az);
                    }

                    // Multi-arrival: parse an "Arrivals" array if present.
                    var arrivals = new List<ArrivalSpot>();
                    if (el.TryGetProperty("Arrivals", out var arrEl) &&
                        arrEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in arrEl.EnumerateArray())
                        {
                            int ax = a.GetProperty("X").GetInt32();
                            int ay = a.GetProperty("Y").GetInt32();
                            int az = a.TryGetProperty("Z", out var azz) ? azz.GetInt32() : z;
                            var wps = new List<string>();
                            if (a.TryGetProperty("Waypoints", out var wEl) &&
                                wEl.ValueKind == JsonValueKind.Array)
                                foreach (var wn in wEl.EnumerateArray())
                                {
                                    var ws = wn.GetString();
                                    if (!string.IsNullOrEmpty(ws)) wps.Add(ws);
                                }
                            arrivals.Add(new ArrivalSpot
                            {
                                Point = new Point3D(ax, ay, az),
                                Waypoints = wps.ToArray(),
                            });
                        }
                    }
                    // Synthesize one spot from legacy fields if no array given.
                    if (arrivals.Count == 0 && arrival.HasValue)
                    {
                        var wps = new List<string>();
                        if (el.TryGetProperty("NearestWaypoint", out var nwv))
                        {
                            var ws = nwv.GetString();
                            if (!string.IsNullOrEmpty(ws)) wps.Add(ws);
                        }
                        arrivals.Add(new ArrivalSpot
                        {
                            Point = arrival.Value,
                            Waypoints = wps.ToArray(),
                        });
                    }

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
                        ArrivalPoint     = arrival,
                        Arrivals         = arrivals,
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
