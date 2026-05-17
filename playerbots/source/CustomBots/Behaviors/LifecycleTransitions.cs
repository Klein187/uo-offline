// =========================================================================
// LifecycleTransitions.cs — When a bot transitions phases, where do they
// go to start the new behavior?
//
// Most behaviors need an "appropriate" location to operate:
//   - BankSitter wants to be at a bank
//   - Adventurer wants to be in a dungeon (or other dangerous area)
//   - Traveler picks its own destination; just stay in place
//   - Wander / Idle: stay in place
//
// Smart placement uses BotPanelActions' CityCoords (banks) and
// DungeonInsideCoords (dungeons) as the candidate set. The bot teleports
// to a random pick from the appropriate set when transitioning.
//
// Each placement applies a small random offset (-3..+3 in X and Y) so
// multiple bots transitioning to the same destination spread into a
// small cluster instead of stacking on the exact same tile.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public static class LifecycleTransitions
    {
        // Half-width of the random spread applied to placements.
        // Bots land in a (2*PlacementSpread+1) × (2*PlacementSpread+1) area.
        private const int PlacementSpread = 3;

        // Apply a placement policy for the given target behavior. The bot
        // is teleported (or left in place) so they're ready to operate.
        //
        // Returns a description string used for logging.
        public static string ApplyPlacement(PlayerBot bot, string targetBehavior)
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
                    // Stay in place. Traveler will pick its own destination.
                    return "stays in place";
            }
        }

        private static string PlaceAtRandomBank(PlayerBot bot)
        {
            var coords = BotPanelActions.CityCoords;
            if (coords == null || coords.Count == 0)
                return "no bank coords available; stays in place";

            // Pick a random city.
            var keys = new List<string>(coords.Keys);
            string city = keys[Utility.Random(keys.Count)];
            var p = coords[city];

            // Spread arrivals so multiple bots don't pile on one tile.
            int ox = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
            int oy = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
            int fx = p.X + ox;
            int fy = p.Y + oy;
            int fz = Map.Felucca.GetAverageZ(fx, fy);

            bot.MoveToWorld(new Point3D(fx, fy, fz), Map.Felucca);
            return $"placed at {city} bank ({fx},{fy},{fz})";
        }

        private static string PlaceAtRandomDungeon(PlayerBot bot)
        {
            var coords = BotPanelActions.DungeonInsideCoords;
            if (coords == null || coords.Count == 0)
                return "no dungeon coords available; stays in place";

            var keys = new List<string>(coords.Keys);
            string dungeon = keys[Utility.Random(keys.Count)];
            var p = coords[dungeon];

            // Spread inside the dungeon. We use the dungeon's exact Z (not
            // GetAverageZ) because dungeon floors are at fixed Z that
            // doesn't match the overworld surface above.
            int ox = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
            int oy = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
            int fx = p.X + ox;
            int fy = p.Y + oy;

            bot.MoveToWorld(new Point3D(fx, fy, p.Z), Map.Felucca);
            return $"placed inside {dungeon} ({fx},{fy},{p.Z})";
        }
    }
}
