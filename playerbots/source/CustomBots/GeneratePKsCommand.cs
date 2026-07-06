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

        // Reds ambush where the guards are far and the marks are alone.
        // The Brit->Trinsic corridor is the BUSIEST road on the shard —
        // packing PKs there got them mobbed by groups of blues within
        // minutes. One light presence stays there for the era feel; the
        // rest hunt the lonely roads and the dungeon halls (rooms from the
        // generated interior meshes — crawlers make perfect marks).
        private static readonly PKSpot[] Spots =
        {
            // One era-mandatory road ambush, kept light.
            new("Felucca", 1601, 2415,  5, 2),  // lower Trinsic road

            // Lonely roads and trails.
            new("Felucca",  327, 1480,  0, 3),  // Shame approach, far west of Yew
            new("Felucca",  885, 1285,  0, 2),  // Orc Cave woods
            new("Felucca", 1911,  440,  0, 2),  // Wrong approach, north mountains
            new("Felucca", 2768,  500, 15, 2),  // Minoc-Vesper high pass (125 tiles from town)
            new("Felucca", 1639, 3048,  0, 3),  // Honor jungle trail, S of Trinsic
            new("Felucca", 4172,  588,  0, 2),  // Dagger Isle trail (Deceit pilgrims)
            new("Felucca", 4559, 3742,  0, 2),  // Humility isle road to Hythloth

            // Dungeon halls.
            new("Felucca", 5407,  857,  0, 2),  // Despise L1
            new("Felucca", 5136,  648,  0, 2),  // Deceit L2
            new("Felucca", 5394,  126,  0, 2),  // Shame L1
            new("Felucca", 5388, 2026,  0, 2),  // Covetous L1
            new("Felucca", 5129,  907,  0, 2),  // Destard L1
            new("Felucca", 5689,  568,  0, 2),  // Wrong L2
            new("Felucca", 5681, 1436,  0, 2),  // Fire L1
            new("Felucca", 5904,   16,  0, 2),  // Hythloth L1
            new("Felucca", 5704,  145,  0, 2),  // Ice L1
        };

        // PK spawners respawn slower than town spawners — a road shouldn't
        // instantly refill with killers after you clear it.
        private static readonly TimeSpan MinDelay = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(20);

        private const int BoundsRadius = 14;

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
        public static (int placed, int totalPKs) PlaceDefault()
        {
            int placed = 0, totalPKs = 0;
            foreach (var s in Spots)
            {
                var map = Map.Parse(s.MapName);
                if (map == null || map == Map.Internal) continue;

                var loc = new Point3D(s.X, s.Y, s.Z);
                var spawner = new PlayerBotSpawner("PK", s.Amount, MinDelay, MaxDelay)
                {
                    Name = "PK Spawner",
                };
                int r = BoundsRadius;
                spawner.SpawnBounds = new Rectangle3D(
                    new Point3D(s.X - r, s.Y - r, s.Z - 5),
                    new Point3D(s.X + r, s.Y + r, s.Z + 20));

                spawner.MoveToWorld(loc, map);
                spawner.Respawn();

                placed++;
                totalPKs += s.Amount;
            }
            return (placed, totalPKs);
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
