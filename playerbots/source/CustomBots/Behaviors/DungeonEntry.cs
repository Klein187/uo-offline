// =========================================================================
// DungeonEntry.cs — Multi-stage dungeon entry for lifecycle transitions.
//
// When the lifecycle decides a bot should become an Adventurer in a
// specific dungeon, this class handles GETTING them there. Three paths,
// tried in this order:
//
//   1. RECALL — if the bot has Magery >= 50 AND enough mana:
//        - Play recall sound + particles at current location
//        - 2-second delay (cast time feel)
//        - Teleport directly to interior
//        - Drain 11 mana (Recall's cost)
//
//   2. WALK FROM WAYPOINT (future, not yet implemented) — if the
//      waypoint graph has a node within ~38 tiles of the dungeon
//      entrance:
//        - Run/walk to the entrance via the graph, no matter how far.
//          Distance is NOT a reason to fall back. If the bot committed
//          to going there, they go there.
//        - Pause at entrance, then teleport to interior.
//
//   3. TELEPORT-TO-ENTRANCE + PAUSE — fallback ONLY when:
//        - Bot can't recall (low Magery or no mana)
//        - AND the waypoint graph doesn't reach near the entrance
//        Then teleport to entrance, hold 3 seconds, teleport inside.
//
// In all paths, the behavior swap to Adventurer happens AT THE END,
// once the bot is actually inside. This way the bot doesn't try to do
// adventurer things while mid-transit.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public static class DungeonEntry
    {
        // -- Tuning constants --
        private const int    PlacementSpread = 3;
        private const double MinMageryForRecall = 50.0;
        private const int    RecallManaCost = 11;
        private const int    RecallSoundId = 0x1FC; // standard recall sound
        // Particle effect ID for a generic "spell completed" sparkle.
        private const int    RecallEffectId = 0x376A;

        private static readonly TimeSpan RecallCastDelay     = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan EntranceEnterDelay  = TimeSpan.FromSeconds(3);

        // -------------------------------------------------------------------
        // Entry point. Lifecycle calls this; it picks the right path and
        // schedules the work asynchronously. The behavior swap happens in
        // the final timer callback.
        // -------------------------------------------------------------------
        public static void BeginEntry(PlayerBot bot, string dungeonName)
        {
            if (bot == null || bot.Deleted) return;

            var inside = BotPanelActions.DungeonInsideCoords;
            var entrance = BotPanelActions.DungeonCoords;

            if (inside == null || !inside.TryGetValue(dungeonName, out var insideCoord))
            {
                // No coord for this dungeon. Fall back to behavior swap only.
                SwapToAdventurer(bot, $"no coords for {dungeonName}");
                return;
            }

            // Recall path — Mage class or anyone with enough Magery.
            if (CanRecall(bot))
            {
                BeginRecall(bot, dungeonName, insideCoord);
                return;
            }

            // Walk-from-entrance path is not implemented yet (waypoint graph
            // doesn't reach dungeons). Falls through to teleport-to-entrance.

            // Teleport-to-entrance + pause path.
            if (entrance != null && entrance.TryGetValue(dungeonName, out var entranceCoord))
            {
                BeginTeleportToEntrance(bot, dungeonName, entranceCoord, insideCoord);
                return;
            }

            // No entrance coord either — just go straight inside.
            TeleportToInside(bot, dungeonName, insideCoord);
            SwapToAdventurer(bot, $"placed inside {dungeonName} (no entrance defined)");
        }

        // -------------------------------------------------------------------
        // Path 1: Recall
        // -------------------------------------------------------------------
        private static bool CanRecall(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || !bot.Alive) return false;
            double magery = bot.Skills[SkillName.Magery].Base;
            return magery >= MinMageryForRecall && bot.Mana >= RecallManaCost;
        }

        private static void BeginRecall(PlayerBot bot, string dungeonName, Point3D insideCoord)
        {
            // Play recall visuals at current position.
            try
            {
                bot.PlaySound(RecallSoundId);
                bot.FixedParticles(RecallEffectId, 9, 32, 5008, EffectLayer.Waist);
            }
            catch
            {
                // Visuals are nice-to-have; don't fail the entry if any
                // effect call throws.
            }

            // Schedule the actual teleport for 2 seconds later — simulates
            // cast time.
            Timer.DelayCall(RecallCastDelay, () =>
            {
                if (bot == null || bot.Deleted) return;

                // Drain mana — recall costs 11.
                bot.Mana = Math.Max(0, bot.Mana - RecallManaCost);

                TeleportToInside(bot, dungeonName, insideCoord);

                // Particle effect at the arrival location too.
                try
                {
                    bot.PlaySound(RecallSoundId);
                    bot.FixedParticles(RecallEffectId, 9, 32, 5008, EffectLayer.Waist);
                }
                catch { }

                SwapToAdventurer(bot, $"recalled into {dungeonName}");
            });
        }

        // -------------------------------------------------------------------
        // Path 3: Teleport to entrance, pause, then teleport inside.
        // -------------------------------------------------------------------
        private static void BeginTeleportToEntrance(
            PlayerBot bot,
            string dungeonName,
            Point3D entranceCoord,
            Point3D insideCoord)
        {
            // Place bot at the entrance with the standard spread.
            int ox = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
            int oy = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
            int ex = entranceCoord.X + ox;
            int ey = entranceCoord.Y + oy;
            int ez = Map.Felucca.GetAverageZ(ex, ey);

            bot.MoveToWorld(new Point3D(ex, ey, ez), Map.Felucca);

            // Hold at entrance for the entry delay, then teleport inside.
            Timer.DelayCall(EntranceEnterDelay, () =>
            {
                if (bot == null || bot.Deleted) return;
                TeleportToInside(bot, dungeonName, insideCoord);
                SwapToAdventurer(bot, $"entered {dungeonName} via the entrance");
            });
        }

        // -------------------------------------------------------------------
        // Final placement: drop bot inside the dungeon with a small spread.
        // -------------------------------------------------------------------
        private static void TeleportToInside(PlayerBot bot, string dungeonName, Point3D insideCoord)
        {
            int ox = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
            int oy = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
            int fx = insideCoord.X + ox;
            int fy = insideCoord.Y + oy;

            // Keep the dungeon's exact Z — dungeon floors don't use GetAverageZ.
            bot.MoveToWorld(new Point3D(fx, fy, insideCoord.Z), Map.Felucca);
        }

        // -------------------------------------------------------------------
        // Swap the bot's Behavior to Adventurer. Logs the completion.
        // -------------------------------------------------------------------
        private static void SwapToAdventurer(PlayerBot bot, string description)
        {
            if (bot == null || bot.Deleted) return;
            try
            {
                bot.Behavior = BehaviorRegistry.Create("Adventurer");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DungeonEntry] failed swap for {bot.Name}: {ex.Message}");
            }
            Console.WriteLine($"[DungeonEntry] {bot.Name}: {description}");
        }
    }
}
