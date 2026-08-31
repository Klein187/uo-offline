// =========================================================================
// RedTerritory.cs — places the honest bots stay out of.
//
// Buccaneer's Den is the pirate town. In the era it was where murderers
// went because the guards weren't looking, and a blue who wandered in got
// killed for it. Ordinary bots treating it as just another stop on the
// rota was both wrong for the setting and, on this shard, actively broken.
//
// It is an island, and the waypoint graph knows it: from the moongate
// there, a bot can reach 27 of 4013 waypoints and 9 of 480 destinations on
// foot. So a blue who gated in rolled a destination it could not walk to,
// gave up, walked back to the gate and left again. With a gate hop landing
// there roughly one time in eight, the moongate grew a permanent crowd of
// bots arriving, standing about and leaving.
//
// Reds are unaffected: PKBehavior reads DestinationCatalog.All directly
// rather than going through the weighted roll, so it keeps the run of the
// place, which is the point.
// =========================================================================

using System.Collections.Generic;
using Server;
using Server.Regions;

namespace Server.CustomBots
{
    public static class RedTerritory
    {
        // Buccaneer's Den, with margin. Taken from the reachable set of the
        // island's own waypoint component (x 2636-2770, y 2092-2252) and
        // rounded outward so a waypoint added at the shoreline later is
        // still covered.
        private const int MinX = 2600;
        private const int MaxX = 2800;
        private const int MinY = 2060;
        private const int MaxY = 2290;

        public static bool Contains(int x, int y) =>
            x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

        public static bool Contains(Point3D p) => Contains(p.X, p.Y);

        // Where a destination actually puts a bot down.
        public static bool Contains(BotDestination d)
        {
            if (d == null)
            {
                return false;
            }

            var p = d.ArrivalPoint ?? d.Location;
            return Contains(p);
        }

        // A red is anyone the guards would kill: a standing murderer, or one
        // of the born-red PK crews. Behaviour alone is not enough - a PK that
        // the lifecycle later hands a different brain is still a murderer and
        // still gets cut down at a town gate.
        public static bool IsRed(PlayerBot bot) =>
            bot != null && (bot.Murderer || bot.Behavior is PKBehavior);

        // Reds live there. Everyone else keeps away.
        public static bool AllowedFor(PlayerBot bot) => IsRed(bot);

        // Public moongates stand in guarded towns, so a red stepping out of
        // one is dead where it lands. They walk, or they stay where they are.
        public static bool MayUseMoongates(PlayerBot bot) => !IsRed(bot);

        // Reds do their banking in the pirate town and nowhere else - every
        // other bank in the world has guards standing over it.
        public static bool MayBankAt(PlayerBot bot, BotDestination d) =>
            !IsRed(bot) || Contains(d);

        // The common question: should this bot avoid this place?
        public static bool ShouldAvoid(PlayerBot bot, BotDestination d) =>
            Contains(d) && !AllowedFor(bot);

        // ---- Guarded towns ----------------------------------------------
        //
        // The other half of the same rule, and the one that was missing. A
        // red inside a guarded region is killed by guards on sight, so every
        // guarded destination is a death sentence for one. Bots were still
        // being sent to them: a reagent errand picks the NEAREST vendor, and
        // the nearest vendor is almost always in a town. The bot died, a
        // wandering healer stood it back up on the same tile, and the guards
        // killed it again — thirteen times over for one bot in one evening.
        //
        // Region lookup is not free and the destination list is walked on
        // every errand roll, so the answer is cached. Regions do not move
        // once the world is up.
        private static readonly Dictionary<Point3D, bool> _guardedCache = new();

        public static bool IsGuardedPlace(Point3D p, Map map)
        {
            if (map == null || map == Map.Internal)
            {
                return false;
            }

            if (_guardedCache.TryGetValue(p, out var cached))
            {
                return cached;
            }

            var guarded = Region.Find(p, map).IsPartOf<GuardedRegion>();
            _guardedCache[p] = guarded;
            return guarded;
        }

        public static bool IsGuardedPlace(BotDestination d, Map map) =>
            d != null && IsGuardedPlace(d.ArrivalPoint ?? d.Location, map);

        // Cleared when the destination catalog reloads, so an edited arrival
        // point is not answered from a stale entry.
        public static void ClearCache() => _guardedCache.Clear();

        // THE question any trip should ask before setting out: can this bot
        // go here and still be alive when it arrives? Covers both rules —
        // the pirate town a blue keeps out of, and the guarded towns a red
        // cannot survive.
        public static bool MayGoTo(PlayerBot bot, BotDestination d) =>
            !ShouldAvoid(bot, d) &&
            (!IsRed(bot) || !IsGuardedPlace(d, bot?.Map));
    }
}
