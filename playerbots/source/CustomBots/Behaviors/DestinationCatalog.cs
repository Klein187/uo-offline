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

        // Ferry pairing (see FerryTravel). A Dock with FerryTo set is a
        // ferry terminal: bots arriving here may "board the boat" and be
        // carried to the named partner dock. Pairs are authored in both
        // directions in destinations.json. Empty = an ordinary dock.
        public string FerryTo           { get; init; } = "";

        // ---- Dungeon scoping (see DungeonCrawlerBehavior / DungeonRegistry) ----
        // Dungeon tag. Empty for ordinary world destinations. A crawler only
        // sees/rolls points whose Dungeon matches its own — this is the
        // "dungeon tag scopes everything" mechanism from the design.
        public string Dungeon           { get; init; } = "";

        // Which floor this point sits on. 0 = surface. Entrances are surface
        // points whose TargetLevel is the level they deposit a bot onto.
        public int Level                { get; init; }

        // For teleporter points (DungeonEntrance/Descend/Ascend): where the
        // bot is placed after using it, and the level it lands on. Null Target
        // means "use Location" (a non-teleporter point).
        public Point3D? Target          { get; init; }
        public int? TargetLevel         { get; init; }

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
        // Append synthetic (code-generated) destinations — gather spots.
        // Skips names already present, so re-registration after a catalog
        // reload is idempotent.
        // -------------------------------------------------------------------
        public static void RegisterSynthetic(IReadOnlyList<BotDestination> dests)
        {
            if (dests == null || dests.Count == 0) return;
            lock (_lock)
            {
                foreach (var d in dests)
                {
                    if (d == null || string.IsNullOrEmpty(d.Name) ||
                        _byName.ContainsKey(d.Name))
                    {
                        continue;
                    }
                    _all.Add(d);
                    _byName[d.Name] = d;
                }
            }
        }

        // -------------------------------------------------------------------
        // Weighted-random pick. Iterates all destinations, computes each's
        // weight for the bot's class, rolls weighted random.
        //
        // The bot-aware overload layers the living-shard modifiers on top
        // of the class weight:
        //   - HOME BIAS (IDEAS 1.3): destinations in the bot's home city
        //     weigh extra, so regulars emerge — the same smith keeps
        //     turning up at the Britain forge.
        //   - DANGER (IDEAS 3.3): places with recent murders weigh less;
        //     danger is information that propagates through the population.
        //   - HAUL (gatherers): a loaded gatherer overrides everything and
        //     heads for town (bank / the crafter who buys its material).
        //
        // Returns null only if there are zero destinations loaded.
        // -------------------------------------------------------------------
        public static BotDestination PickWeighted(BotClass cls) =>
            PickWeightedCore(cls, null);

        public static BotDestination PickWeighted(PlayerBot bot) =>
            PickWeightedCore(bot?.Class ?? BotClass.Warrior, bot);

        private static BotDestination PickWeightedCore(BotClass cls, PlayerBot bot)
        {
            BotDestination[] snapshot;
            lock (_lock) snapshot = _all.ToArray();

            if (snapshot.Length == 0) return null;

            bool hauling = bot != null && bot.HaulPending &&
                           BotClassHelper.IsGatherer(bot.Class);

            // Authored typed sites SUPERSEDE the synthetic wilderness spots
            // for their class: once any MiningSpot is authored, miners work
            // mines — the 40 generic GatherSpots drop out of their roll
            // entirely (same for lumberjacks and LumberSpots). Delete the
            // authored sites and the synthetic spread takes over again.
            bool suppressGeneric = false;
            if (!hauling && BotClassHelper.IsGatherer(cls))
            {
                var wanted = cls == BotClass.Miner
                    ? DestinationType.MiningSpot
                    : DestinationType.LumberSpot;
                foreach (var d in snapshot)
                {
                    if (d.Type == wanted)
                    {
                        suppressGeneric = true;
                        break;
                    }
                }
            }

            double total = 0;
            var weights = new double[snapshot.Length];
            for (int i = 0; i < snapshot.Length; i++)
            {
                var d = snapshot[i];
                double w;

                if (hauling)
                {
                    // Loaded up — take the goods to town. Miners favor the
                    // forge/smith (their buyer), lumberjacks the carpenter;
                    // the bank takes anything.
                    w = d.Type switch
                    {
                        DestinationType.Bank => 4.0,
                        DestinationType.Forge or DestinationType.VendorSmith
                            when bot.Class == BotClass.Miner => 6.0,
                        DestinationType.VendorCarpenter
                            when bot.Class == BotClass.Lumberjack => 6.0,
                        _ => 0.02,
                    };
                }
                else
                {
                    w = DestinationWeights.GetWeight(d.Type, cls);
                    if (suppressGeneric && d.Type == DestinationType.GatherSpot)
                    {
                        w = 0;
                    }
                }

                if (w > 0 && bot != null)
                {
                    // Home bias — regulars emerge for free.
                    if (!string.IsNullOrEmpty(bot.HomeCity) &&
                        string.Equals(d.City, bot.HomeCity, StringComparison.OrdinalIgnoreCase))
                    {
                        w *= 2.5;
                    }

                    // Danger — everyone's heard about the murders there.
                    w *= BotDangerMap.Multiplier(d);
                }

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

                    // Dungeon scoping fields (all optional; absent on ordinary
                    // world destinations).
                    string ferryTo = el.TryGetProperty("FerryTo", out var fv)
                        ? (fv.GetString() ?? "") : "";

                    string dungeon = el.TryGetProperty("Dungeon", out var dv)
                        ? (dv.GetString() ?? "") : "";
                    int level = el.TryGetProperty("Level", out var lv) ? lv.GetInt32() : 0;

                    Point3D? target = null;
                    if (el.TryGetProperty("TargetX", out var txv) &&
                        el.TryGetProperty("TargetY", out var tyv))
                    {
                        int tz = el.TryGetProperty("TargetZ", out var tzv) ? tzv.GetInt32() : 0;
                        target = new Point3D(txv.GetInt32(), tyv.GetInt32(), tz);
                    }
                    int? targetLevel = el.TryGetProperty("TargetLevel", out var tlv)
                        ? tlv.GetInt32() : (int?)null;

                    loaded.Add(new BotDestination
                    {
                        Name             = name,
                        Location         = new Point3D(x, y, z),
                        Type             = type,
                        City             = city ?? "",
                        NearestWaypoint  = wpName ?? "",
                        ArrivalPoint     = arrival,
                        Arrivals         = arrivals,
                        FerryTo          = ferryTo,
                        Dungeon          = dungeon,
                        Level            = level,
                        Target           = target,
                        TargetLevel      = targetLevel,
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

            // A reload wiped any synthetic entries — let the generators
            // put theirs back.
            GatherSpots.OnCatalogReloaded();
            TreasureSites.OnCatalogReloaded();

            return loaded.Count;
        }
    }
}
