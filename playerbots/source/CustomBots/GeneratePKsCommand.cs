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

        private static readonly PKSpot[] Spots =
        {
            // Britain -> Trinsic road — the main hunting ground.
            new("Felucca", 1416, 2104, 15, 3),  // brit-trin crossroads
            new("Felucca", 1492, 2225,  5, 3),  // mid Trinsic road
            new("Felucca", 1601, 2415,  5, 2),  // lower Trinsic road
            new("Felucca", 1691, 2741, 10, 3),  // Trinsic approach
            // Dungeon approaches.
            new("Felucca", 1384, 1495, 10, 2),  // near Britain graveyard
            // Wilderness chokepoints.
            new("Felucca", 1367, 1756, 10, 2),  // Britain west road
            new("Felucca", 1995, 2100,  0, 2),  // open country
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
