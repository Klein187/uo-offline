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
            var defs = PKSpawnData.Load();
            var map = Map.Felucca;
            int placed = 0, totalPKs = 0;

            foreach (var s in defs)
            {
                // Bounds hug the hunt polygon so bots spawn inside their
                // leash; a poly-less spawn falls back to a small box.
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

                var spawner = new PlayerBotSpawner("PK", s.Amount, MinDelay, MaxDelay)
                {
                    Name = $"PK Spawner ({s.Name})",
                };
                spawner.SpawnBounds = bounds;
                spawner.MoveToWorld(s.Location, map);
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
