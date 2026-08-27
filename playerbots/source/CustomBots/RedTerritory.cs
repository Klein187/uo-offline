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

using Server;

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
    }
}
