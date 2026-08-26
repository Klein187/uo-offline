// =========================================================================
// GeneratePKsCommand.cs — [GeneratePKs
//
// Places PK (player-killer) spawners along the roads and wilds. PKs are a
// SEPARATE population from the normal ~1000 bots — they're placed only by
// this command, never by [GenerateBots.
//
//   [GeneratePKs          place the default PK spawner set
//   [GeneratePKs clear    remove all PK spawners (and their PKs)
//
// "Noticeable" density: a handful of spawners on the travel routes, a few
// PKs each. Enough that the roads are dangerous without being a gauntlet.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class GeneratePKsCommand
    {
        // PK spawn points — roads, dungeon approaches, wilderness. Each
        // spawner makes a few PKs. Coords are on the Britain/Trinsic travel
        // corridor and known dangerous areas.
        private sealed record PKSpot(
            string MapName, int X, int Y, int Z, int Amount);

        // PK spawns are DATA now — drawn in the map editor and stored in
        // Data/CustomSpawns/pk_spawns.json (see PKSpawnData). No hardcoded
        // set: an empty file means no reds until you place some.

        // PK spawners respawn slower than town spawners — a road shouldn't
        // instantly refill with killers after you clear it.
        private static readonly TimeSpan MinDelay = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(20);

        private const int BoundsRadius = 14;

        // How close a spawner has to be to count as "this spawn is already placed".
        private const int ExistingSearchRange = 8;

        public static void Configure()
        {
            CommandSystem.Register("GeneratePKs", AccessLevel.Administrator, OnCommand);
        }

        [Usage("GeneratePKs [clear]")]
        [Description("Place (or clear) PK spawners along the roads.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null) return;

            bool clear = e.Length >= 1 &&
                e.GetString(0).Equals("clear", StringComparison.OrdinalIgnoreCase);

            // Always clear existing PK spawners first (whether clearing or
            // regenerating) so this never stacks.
            int removed = ClearPKSpawners();

            if (clear)
            {
                from.SendMessage(0x35, $"Removed {removed} PK spawner(s).");
                return;
            }

            var (placed, totalPKs) = PlaceDefault();

            from.SendMessage(0x35,
                $"Placed {placed} PK spawner(s) for ~{totalPKs} player-killers.");
            from.SendMessage(0x3B2,
                "The roads are dangerous now. [GeneratePKs clear removes them.");
            Console.WriteLine(
                $"[GeneratePKs] {from.Name}: {placed} spawners, ~{totalPKs} PKs.");
        }

        // Place the default PK spawner set. Shared by the [GeneratePKs
        // command and the editor bridge (pks_request.txt) so headless
        // sessions can arm the roads too. Does NOT clear first — callers
        // decide (both current callers clear before placing).
        // The name a spawn's spawner carries, and how we recognise one that
        // is already in the world.
        private static string SpawnerNameFor(PKSpawnDef s) => $"PK Spawner ({s.Name})";

        // Place one drawn spawn. Returns how many reds it will hold.
        private static int PlaceOne(PKSpawnDef s, Map map)
        {
            // Bounds hug the hunt polygon so bots spawn inside their leash;
            // a poly-less spawn falls back to a small box.
            Rectangle3D bounds;
            if (s.Hunt != null && s.Hunt.Length >= 3)
            {
                int minX = int.MaxValue, minY = int.MaxValue;
                int maxX = int.MinValue, maxY = int.MinValue;
                foreach (var p in s.Hunt)
                {
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                }
                bounds = new Rectangle3D(
                    new Point3D(minX, minY, s.Location.Z - 20),
                    new Point3D(maxX, maxY, s.Location.Z + 40));
            }
            else
            {
                int r = BoundsRadius;
                bounds = new Rectangle3D(
                    new Point3D(s.Location.X - r, s.Location.Y - r, s.Location.Z - 5),
                    new Point3D(s.Location.X + r, s.Location.Y + r, s.Location.Z + 20));
            }

            // Reds scale with the town population rather than on their own
            // dial in the data file -- see PKDensityMultiplier.
            var amount = Math.Max(1, s.Amount * BotPopulation.PKDensityMultiplier);

            var spawner = new PlayerBotSpawner("PK", amount, MinDelay, MaxDelay)
            {
                Name = SpawnerNameFor(s),
            };
            spawner.SpawnBounds = bounds;
            spawner.MoveToWorld(s.Location, map);
            spawner.Respawn();

            return amount;
        }

        // ---------------------------------------------------------------
        // Boot-time top-up.
        //
        // An update that adds red spawns should just have them, the same way
        // the bank crowds appear, rather than waiting for someone to know
        // that [GeneratePKs exists. So on every boot, any drawn spawn with
        // no spawner in the world gets one.
        //
        // Additive only: it never clears and never touches a spawn that is
        // already placed, so it cannot disturb a world that is already set
        // up, and it costs nothing on a boot where everything is present.
        // ---------------------------------------------------------------
        public static void Initialize()
        {
            Timer.DelayCall(TimeSpan.FromSeconds(14), EnsureAll);
        }

        private static void EnsureAll()
        {
            var defs = PKSpawnData.Load();
            if (defs.Count == 0)
            {
                return; // nothing drawn; synthesising is [GeneratePKs' job
            }

            var map = Map.Felucca;
            int placed = 0, reds = 0;

            foreach (var s in defs)
            {
                var wanted = SpawnerNameFor(s);

                var exists = false;
                foreach (var sp in map.GetItemsInRange<PlayerBotSpawner>(s.Location, ExistingSearchRange))
                {
                    if (!sp.Deleted && string.Equals(sp.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }

                if (exists)
                {
                    continue;
                }

                reds += PlaceOne(s, map);
                placed++;
                Console.WriteLine($"[pk] added missing spawn '{s.Name}' at ({s.Location.X},{s.Location.Y})");
            }

            if (placed > 0)
            {
                Console.WriteLine($"[pk] {placed} new red spawn(s) placed from pk_spawns.json (~{reds} reds).");
            }
        }

        public static (int placed, int totalPKs) PlaceDefault()
        {
            var defs = PKSpawnData.Load();

            // Nothing drawn in the editor — synthesize the classic set
            // from the destination catalog (crews leashed to dungeon
            // mouths, roaming crews at road chokepoints), persist it to
            // pk_spawns.json so leashes survive restarts and the editor
            // can adjust them, and reload.
            if (defs.Count == 0)
            {
                var synth = SynthesizeDefault();
                if (synth.Count > 0)
                {
                    WriteSpawnJson(synth);
                    defs = PKSpawnData.Load();
                    Console.WriteLine(
                        $"[GeneratePKs] no drawn spawns — synthesized " +
                        $"{defs.Count} default spawn(s) from the catalog.");
                }
            }

            var map = Map.Felucca;
            int placed = 0, totalPKs = 0;

            foreach (var s in defs)
            {
                totalPKs += PlaceOne(s, map);
                placed++;
            }
            return (placed, totalPKs);
        }

        // The synthesized default: crews of 3 leashed to a handful of
        // dungeon mouths (the era's most feared ground — the field PK
        // hunted entrances), plus free-roaming road crews at crossroads
        // and bridges who patrol and mount their own mouth ambushes.
        private static List<PKSpawnDef> SynthesizeDefault()
        {
            var mouths = new List<BotDestination>();
            var roads  = new List<BotDestination>();
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type == DestinationType.DungeonEntrance)
                {
                    mouths.Add(d);
                }
                else if (d.Type is DestinationType.Shrine
                                or DestinationType.GatherSpot)
                {
                    // Wilderness anchors for the roaming crews — shrine
                    // campers ganking fresh resurrections were an era
                    // institution.
                    roads.Add(d);
                }
            }
            Shuffle(mouths);
            Shuffle(roads);

            var defs = new List<PKSpawnDef>();
            for (int i = 0; i < mouths.Count && i < 5; i++)
            {
                var m = mouths[i];
                const int r = 30; // the leash box around the mouth
                defs.Add(new PKSpawnDef
                {
                    Name = $"{m.Name} reds",
                    Location = m.Location,
                    Amount = 3,
                    Hunt = new[]
                    {
                        new Point2D(m.Location.X - r, m.Location.Y - r),
                        new Point2D(m.Location.X + r, m.Location.Y - r),
                        new Point2D(m.Location.X + r, m.Location.Y + r),
                        new Point2D(m.Location.X - r, m.Location.Y + r),
                    },
                });
            }
            for (int i = 0; i < roads.Count && i < 5; i++)
            {
                var d = roads[i];
                defs.Add(new PKSpawnDef
                {
                    Name = $"{d.Name} reds",
                    Location = d.Location,
                    Amount = 3,
                    Hunt = null, // roaming crew
                });
            }
            return defs;
        }

        private static void Shuffle(List<BotDestination> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Utility.Random(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // Persist synthesized spawns in the editor's own schema.
        private static void WriteSpawnJson(List<PKSpawnDef> defs)
        {
            try
            {
                var path = System.IO.Path.Combine(
                    Core.BaseDirectory, "Data", "CustomSpawns", "pk_spawns.json");
                using var stream = System.IO.File.Create(path);
                using var w = new System.Text.Json.Utf8JsonWriter(stream,
                    new System.Text.Json.JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteStartArray("Spawns");
                foreach (var d in defs)
                {
                    w.WriteStartObject();
                    w.WriteString("name", d.Name);
                    w.WriteNumber("x", d.Location.X);
                    w.WriteNumber("y", d.Location.Y);
                    w.WriteNumber("z", d.Location.Z);
                    w.WriteNumber("amount", d.Amount);
                    if (d.Hunt != null && d.Hunt.Length >= 3)
                    {
                        w.WriteStartArray("hunt");
                        foreach (var p in d.Hunt)
                        {
                            w.WriteStartArray();
                            w.WriteNumberValue(p.X);
                            w.WriteNumberValue(p.Y);
                            w.WriteEndArray();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GeneratePKs] spawn-json write failed: {ex.Message}");
            }
        }

        public static int ClearPKSpawners()
        {
            // A PK spawner is a PlayerBotSpawner whose behavior is "PK".
            var spawners = new List<PlayerBotSpawner>();
            foreach (var item in World.Items.Values)
            {
                if (item is PlayerBotSpawner sp && !sp.Deleted &&
                    sp.BehaviorName == "PK")
                {
                    spawners.Add(sp);
                }
            }

            // Also remove the PK bots themselves.
            var pks = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted &&
                    bot.Behavior is PKBehavior)
                {
                    pks.Add(bot);
                }
            }

            foreach (var sp in spawners) { try { sp.Delete(); } catch { } }
            foreach (var pk in pks)      { try { pk.Delete(); } catch { } }
            return spawners.Count;
        }
    }
}
