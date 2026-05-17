// =========================================================================
// BotPanelActions.cs — Server-side action handlers for the admin panel.
//
// The gump's OnResponse hands off to methods here, then re-sends a fresh
// gump. Each action records a one-line summary to the panel's log so the
// user sees feedback without leaving the gump.
//
// We deliberately implement most actions by invoking the existing command
// system — this keeps a single source of truth ([GenerateBots, [Decorate,
// etc. behave identically whether typed or clicked).
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class BotPanelActions
    {
        // ---- Spawner placement ----

        // Commits the current draft: each (behavior, count) pair becomes
        // a separate PlayerBotSpawner at the admin's current location.
        // Multiple behaviors stack — different bot pools at the same tile.
        public static void CommitDraft(Mobile from)
        {
            if (from.Map == null || from.Map == Map.Internal)
            {
                BotPanelState.Log(from, "Can't place spawner on the internal map.");
                return;
            }

            var draft = BotPanelState.GetDraft(from);
            int valid = 0;
            int total = 0;

            foreach (var entry in draft)
            {
                if (entry.Count < 1)
                {
                    continue;
                }

                // Verify behavior name is real (avoid silent Idle fallback)
                var probe = BehaviorRegistry.Create(entry.BehaviorName);
                if (!string.Equals(probe.SerializableName, entry.BehaviorName,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    BotPanelState.Log(from, $"Skipped unknown behavior '{entry.BehaviorName}'.");
                    continue;
                }

                const int boundsRadius = 10;
                var bounds = new Rectangle3D(
                    new Point3D(from.X - boundsRadius, from.Y - boundsRadius, from.Z - 5),
                    new Point3D(from.X + boundsRadius, from.Y + boundsRadius, from.Z + 20)
                );

                var spawner = new PlayerBotSpawner(
                    behaviorName: probe.SerializableName,
                    amount:       entry.Count,
                    minDelay:     TimeSpan.FromMinutes(5),
                    maxDelay:     TimeSpan.FromMinutes(15)
                );
                spawner.SpawnBounds   = bounds;
                spawner.UseSpiralScan = true;
                spawner.MoveToWorld(from.Location, from.Map);
                spawner.Respawn();

                valid++;
                total += entry.Count;
            }

            if (valid > 0)
            {
                BotPanelState.Log(from,
                    $"Placed {valid} spawner(s) targeting {total} bots at ({from.X},{from.Y}).");
            }
            else
            {
                BotPanelState.Log(from,
                    "Draft has nothing to commit (all rows had 0 count or unknown behavior).");
            }
            // We intentionally keep the draft so the user can stamp the same
            // config at the next city. "Clear Draft" empties it explicitly.
        }

        // ---- Cleanup ----

        public static void ClearBotsHere(Mobile from)
        {
            int removed = ClearBotsInternal(from, 30, false);
            BotPanelState.Log(from, $"Cleared {removed} bot(s) within 30 tiles.");
        }

        public static void ClearBotsAll(Mobile from)
        {
            int removed = ClearBotsInternal(from, 0, true);
            BotPanelState.Log(from, $"Cleared {removed} bot(s) worldwide.");
        }

        private static int ClearBotsInternal(Mobile from, int range, bool worldwide)
        {
            var victims = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted) continue;
                if (!worldwide)
                {
                    if (bot.Map != from.Map) continue;
                    if (!bot.InRange(from.Location, range)) continue;
                }
                victims.Add(bot);
            }
            foreach (var v in victims) v.Delete();
            return victims.Count;
        }

        // ---- World/save ----

        public static void SaveWorld(Mobile from)
        {
            try
            {
                World.Save();
                BotPanelState.Log(from, "World save started.");
            }
            catch (Exception ex)
            {
                BotPanelState.Log(from, $"Save failed: {ex.Message}");
            }
        }

        // ---- Travel ----

        // Bank coordinates used by the Travel section. These mirror what
        // GenerateBotsCommand's fallback list uses — refining one place
        // updates both. (Could share a constant later.)
        public static readonly Dictionary<string, Point3D> CityCoords = new()
        {
            ["Britain"]    = new Point3D(1434, 1697, 0),
            ["Vesper"]     = new Point3D(2891,  678, 0),
            ["Trinsic"]    = new Point3D(1832, 2839, 0),
            ["Yew"]        = new Point3D( 643,  858, 0),
            ["Minoc"]      = new Point3D(2511,  564, 0),
            ["Magincia"]   = new Point3D(3680, 2155, 0),
            ["Jhelom"]     = new Point3D(1417, 3821, 0),
            ["Skara Brae"] = new Point3D( 591, 2147, 0),
            ["Moonglow"]   = new Point3D(4471, 1175, 0),
        };

        public static void GoToCity(Mobile from, string city)
        {
            if (!CityCoords.TryGetValue(city, out var p))
            {
                BotPanelState.Log(from, $"Unknown city: {city}");
                return;
            }
            // Resolve Z dynamically from the map's surface height at (X,Y).
            // Hardcoded Z values from old UO docs are often wrong for
            // ModernUO's data — wrong Z either puts you inside a wall (stuck)
            // or below a floor (black screen). GetAverageZ asks the map for
            // "what's the floor here?" and gives a safe landing height.
            var z = Map.Felucca.GetAverageZ(p.X, p.Y);
            from.MoveToWorld(new Point3D(p.X, p.Y, z), Map.Felucca);
            BotPanelState.Log(from, $"Teleported to {city} ({p.X},{p.Y},{z}).");
        }

        // Dungeon ENTRANCE coords on the overworld — the spot just outside
        // the dungeon archway, not inside the dungeon itself. These are
        // standard overworld terrain (Z resolves cleanly via GetAverageZ)
        // and give you the actual "I am about to enter a dungeon" arrival
        // moment instead of dropping you blindly into level 1.
        public static readonly Dictionary<string, Point3D> DungeonCoords = new()
        {
            ["Despise"]   = new Point3D(1305, 1080, 0),  // north of Britain
            ["Destard"]   = new Point3D(1170, 2640, 0),  // south Britain woods
            ["Covetous"]  = new Point3D(2498,  921, 0),  // near Vesper
            ["Deceit"]    = new Point3D(4111,  432, 0),  // Dagger Isle
            ["Hythloth"]  = new Point3D(4720, 3814, 0),  // south of Magincia
            ["Shame"]     = new Point3D( 512, 1561, 0),  // west of Britain
            ["Wrong"]     = new Point3D(2042,  224, 0),  // near Yew
            ["Ice"]       = new Point3D(1996,   81, 0),  // Dagger Isle north
            ["Fire"]      = new Point3D(2922, 3416, 0),  // Volcano isle south
        };

        public static void GoToDungeon(Mobile from, string dungeon)
        {
            if (!DungeonCoords.TryGetValue(dungeon, out var p))
            {
                BotPanelState.Log(from, $"Unknown dungeon: {dungeon}");
                return;
            }
            var z = Map.Felucca.GetAverageZ(p.X, p.Y);
            from.MoveToWorld(new Point3D(p.X, p.Y, z), Map.Felucca);
            BotPanelState.Log(from, $"Teleported to {dungeon} dungeon ({p.X},{p.Y},{z}).");
        }

        // Dungeon INTERIOR coords — actual spots inside the dungeon, past
        // the teleport entrance. Use these to drop yourself in directly so
        // you can place Adventurer spawners that start IN the dungeon.
        // Despise interior verified via [where; others are placeholders that
        // mirror the entrance coords (will resolve to overworld, just like
        // the regular GoToDungeon) until you visit and record them.
        public static readonly Dictionary<string, Point3D> DungeonInsideCoords = new()
        {
            ["Despise"]   = new Point3D(5490,  670, 20),   // verified
            // The rest default to entrance coords — set these to real
            // interior coords as you visit each dungeon and [where them.
            ["Destard"]   = new Point3D(1170, 2640, 0),
            ["Covetous"]  = new Point3D(2498,  921, 0),
            ["Deceit"]    = new Point3D(4111,  432, 0),
            ["Hythloth"]  = new Point3D(4720, 3814, 0),
            ["Shame"]     = new Point3D( 512, 1561, 0),
            ["Wrong"]     = new Point3D(2042,  224, 0),
            ["Ice"]       = new Point3D(1996,   81, 0),
            ["Fire"]      = new Point3D(2922, 3416, 0),
        };

        public static void GoToDungeonInside(Mobile from, string dungeon)
        {
            if (!DungeonInsideCoords.TryGetValue(dungeon, out var p))
            {
                BotPanelState.Log(from, $"Unknown dungeon interior: {dungeon}");
                return;
            }
            // Interior coords have explicit Z values, no GetAverageZ needed.
            // (Dungeon floors are at fixed Z; the overworld Z resolution
            // would put us on the ground above the dungeon.)
            from.MoveToWorld(new Point3D(p.X, p.Y, p.Z), Map.Felucca);
            BotPanelState.Log(from, $"Teleported inside {dungeon} ({p.X},{p.Y},{p.Z}).");
        }

        // ---- Run-a-command helper ----

        // Many panel actions are just "send this command as the admin".
        // Wrap that in a helper so the panel doesn't need to know command
        // internals. Captures the result message into the log.
        public static void RunCommand(Mobile from, string commandLine)
        {
            try
            {
                CommandSystem.Handle(from, $"{CommandSystem.Prefix}{commandLine}");
                BotPanelState.Log(from, $"Ran: {commandLine}");
            }
            catch (Exception ex)
            {
                BotPanelState.Log(from, $"Command failed: {ex.Message}");
            }
        }

        // ---- Test spawn ----

        public static void SpawnTestBot(Mobile from, string behaviorName)
        {
            if (from.Map == null || from.Map == Map.Internal)
            {
                BotPanelState.Log(from, "Can't spawn on the internal map.");
                return;
            }
            var bot = new PlayerBot();
            bot.MoveToWorld(from.Location, from.Map);
            bot.Behavior = BehaviorRegistry.Create(behaviorName);
            BotPanelState.Log(from, $"Spawned {bot.Name} ({behaviorName}, hue {bot.SpeechHue}).");
        }

        // ---- Stats for the header ----

        public static (int botCount, int spawnerCount, string region)
            CountNearby(Mobile from, int range)
        {
            int bots = 0;
            int spawners = 0;

            if (from.Map != null && from.Map != Map.Internal)
            {
                foreach (var m in from.Map.GetMobilesInRange(from.Location, range))
                {
                    if (m is PlayerBot pb && !pb.Deleted) bots++;
                }
                foreach (var item in from.Map.GetItemsInRange(from.Location, range))
                {
                    if (item is PlayerBotSpawner pbs && !pbs.Deleted) spawners++;
                }
            }

            string region = from.Region?.Name ?? "Unknown";
            return (bots, spawners, region);
        }
    }
}
