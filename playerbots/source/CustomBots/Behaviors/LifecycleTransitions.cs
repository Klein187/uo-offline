// =========================================================================
// LifecycleTransitions.cs — Where do bots go when they transition?
//
// Each behavior has a placement policy:
//   - BankSitter → teleport to a random bank
//   - Adventurer → stay in place (hunt the local wilderness)
//   - Traveler / Wander / Idle → stay in place
//
// All placements here are SYNCHRONOUS: the bot is moved (or left) immediately
// and the caller (BotLifecycleManager.TransitionBot) swaps Behavior right
// after.
//
// DUNGEONS are no longer reached by teleporting an Adventurer to a hardcoded
// coordinate. Instead a bot rolls a DungeonEntrance destination as a normal
// Traveler, walks onto the surface Teleporter item, the game carries it
// inside, and TravelerBehavior converts it to a DungeonCrawler (see
// DungeonCrawlerBehavior / TravelerBehavior's entry handoff). A lifecycle
// "Adventurer" now just hunts wherever it already is.
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
                    // Hunts the local wilderness from wherever it is. Dungeon
                    // diving is no longer a placement — it happens when a
                    // Traveler rolls a DungeonEntrance and walks into it.
                    return new PlacementResult("stays in place (hunts locally)");

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
    }
}
