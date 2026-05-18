// =========================================================================
// LifecycleTransitions.cs — Where do bots go when they transition?
//
// Each behavior has a placement policy:
//   - BankSitter → teleport to a random bank
//   - Adventurer → enter a random dungeon (multi-stage; see DungeonEntry)
//   - Traveler / Wander / Idle → stay in place
//
// Most placements are SYNCHRONOUS: the bot is moved immediately and the
// caller (BotLifecycleManager.TransitionBot) swaps Behavior right after.
//
// Adventurer placement is ASYNCHRONOUS: the bot starts an entry sequence
// (walk-or-teleport to entrance, then teleport to interior, possibly via
// Recall). The behavior swap is deferred until the bot is actually inside.
// In that case ApplyPlacement returns IsAsync=true and the caller skips
// the synchronous Behavior swap; DungeonEntry handles it via timer.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public readonly struct PlacementResult
    {
        public readonly string Description;
        public readonly bool   IsAsync;     // true = behavior swap is deferred

        public PlacementResult(string description, bool isAsync = false)
        {
            Description = description;
            IsAsync     = isAsync;
        }
    }

    public static class LifecycleTransitions
    {
        // Random offset applied to bank placements so multiple bots don't
        // stack on one tile.
        private const int PlacementSpread = 3;

        public static PlacementResult ApplyPlacement(PlayerBot bot, string targetBehavior)
        {
            switch (targetBehavior)
            {
                case "BankSitter":
                    return PlaceAtRandomBank(bot);

                case "Adventurer":
                    return PlaceAtRandomDungeon(bot);

                case "Traveler":
                case "Wander":
                case "Idle":
                default:
                    return new PlacementResult("stays in place");
            }
        }

        private static PlacementResult PlaceAtRandomBank(PlayerBot bot)
        {
            var coords = BotPanelActions.CityCoords;
            if (coords == null || coords.Count == 0)
                return new PlacementResult("no bank coords available; stays in place");

            var keys = new List<string>(coords.Keys);
            string city = keys[Utility.Random(keys.Count)];
            var p = coords[city];

            int ox = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
            int oy = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
            int fx = p.X + ox;
            int fy = p.Y + oy;
            int fz = Map.Felucca.GetAverageZ(fx, fy);

            bot.MoveToWorld(new Point3D(fx, fy, fz), Map.Felucca);
            return new PlacementResult($"placed at {city} bank ({fx},{fy},{fz})");
        }

        private static PlacementResult PlaceAtRandomDungeon(PlayerBot bot)
        {
            var insideCoords  = BotPanelActions.DungeonInsideCoords;
            var entranceCoords = BotPanelActions.DungeonCoords;
            if (insideCoords == null || insideCoords.Count == 0)
                return new PlacementResult("no dungeon coords available; stays in place");

            var keys = new List<string>(insideCoords.Keys);
            string dungeon = keys[Utility.Random(keys.Count)];

            // Hand off to DungeonEntry. It picks the right path (recall, walk
            // from waypoint, or teleport-to-entrance + pause) and schedules
            // the behavior swap once the bot is actually inside.
            DungeonEntry.BeginEntry(bot, dungeon);

            return new PlacementResult(
                $"entering {dungeon} (entry sequence in progress)",
                isAsync: true);
        }
    }
}
