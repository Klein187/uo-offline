// =========================================================================
// BotHangouts.cs — bots spend most of their time near home.
//
// Players had a town. Somebody from Britain banked at Britain, shopped
// there, hunted the nearby dungeons and wandered the roads around it; a
// trip to Moonglow was an outing, not a coin flip. Bots picked from the
// whole map, weighted only by what kind of place it was, so they spent
// their days crossing water by Recall (about 8,000 casts an hour).
//
// Home is the town nearest where the bot lives: where its spawner put it,
// or its house. The destination roll (DestinationCatalog.PickWeighted)
// then scales every place by how far it is from home:
//
//   - home town itself                                 x HomeTown
//   - same landmass as home: near / middling / far     x Near / Mid / Far
//   - can't be walked to from where the bot stands
//     (another island, across water)                   x OverWater
//   - and a bot that has wandered far from home is pulled back toward it
//
// Dungeons and the countryside have no town; they are scaled by distance
// and landmass the same way, so a Britain bot hunts Despise and Covetous
// and only now and then sails off to Hythloth.
// =========================================================================

using System;
using System.Collections.Generic;

namespace Server.CustomBots
{
    public static class BotHangouts
    {
        // ---- Knobs ----

        private const double HomeTown = 6.0;
        private const double Near = 2.0, Mid = 1.0, Far = 0.4;
        private const int NearDist = 250, MidDist = 600;
        private const double OverWater = 0.12;

        // Further than this from home, home is pulled for harder.
        private const int AwayDist = 400;
        private const double ComeHome = 3.0;

        // A bot that lives out in the country calls the nearest town home,
        // however far; past this it is somewhere no town is (the far
        // reaches of the map), and keeps the home it was given.
        private const int HomeReach = 1500;

        // ---- City centres and landmasses (built once per catalog) ----

        private static Dictionary<string, Point3D> _centres;
        private static Dictionary<string, int> _destComp;
        private static WaypointGraph _builtFor;

        private static void EnsureBuilt()
        {
            var graph = WaypointRegistry.Graph;
            if (_centres != null && _builtFor == graph)
            {
                return;
            }
            _builtFor = graph;
            var sums = new Dictionary<string, (long x, long y, int n)>(StringComparer.OrdinalIgnoreCase);
            _destComp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DestinationCatalog.All)
            {
                if (!string.IsNullOrEmpty(d.City) && string.IsNullOrEmpty(d.Dungeon))
                {
                    var s = sums.GetValueOrDefault(d.City);
                    sums[d.City] = (s.x + d.Location.X, s.y + d.Location.Y, s.n + 1);
                }
                if (graph != null && !string.IsNullOrEmpty(d.NearestWaypoint))
                {
                    _destComp[d.Name] = graph.ComponentOf(d.NearestWaypoint);
                }
            }
            _centres = new Dictionary<string, Point3D>(StringComparer.OrdinalIgnoreCase);
            foreach (var (city, s) in sums)
            {
                _centres[city] = new Point3D((int)(s.x / s.n), (int)(s.y / s.n), 0);
            }
        }

        public static Point3D? CentreOf(string city)
        {
            EnsureBuilt();
            return city != null && _centres.TryGetValue(city, out var c) ? c : null;
        }

        // The town nearest a spot, if one is close enough to be home.
        public static string TownNear(Point3D p)
        {
            EnsureBuilt();
            string best = null;
            int bestD = HomeReach + 1;
            foreach (var (city, c) in _centres)
            {
                int d = Math.Max(Math.Abs(c.X - p.X), Math.Abs(c.Y - p.Y));
                if (d < bestD)
                {
                    bestD = d;
                    best = city;
                }
            }
            return best;
        }

        private static int Comp(Point3D p)
        {
            var node = WaypointRegistry.Graph?.FindNearestNode(p);
            return node != null ? WaypointRegistry.Graph.ComponentOf(node.Name) : -1;
        }

        // ---- The roll ----

        // Per-roll context, so the per-destination factor is cheap.
        public sealed class Context
        {
            public Point3D Home;
            public string HomeTown;
            public int HomeComp;
            public int HereComp;
            public bool Away;
        }

        public static Context For(PlayerBot bot)
        {
            if (bot == null || bot.Map == null || bot.Map == Map.Internal)
            {
                return null;
            }
            EnsureBuilt();
            var homeTown = bot.HomeCity;
            Point3D home;
            var house = BotHomes.HomeOf(bot);
            if (house != null)
            {
                home = house.Location;
            }
            else if (CentreOf(homeTown) is Point3D c)
            {
                home = c;
            }
            else
            {
                return null;
            }
            return new Context
            {
                Home = home,
                HomeTown = homeTown,
                HomeComp = Comp(home),
                HereComp = Comp(bot.Location),
                Away = Math.Max(Math.Abs(bot.X - home.X), Math.Abs(bot.Y - home.Y)) > AwayDist,
            };
        }

        public static double Factor(Context ctx, BotDestination d)
        {
            if (ctx == null)
            {
                return 1.0;
            }
            double f;
            bool homeTown = !string.IsNullOrEmpty(ctx.HomeTown) &&
                string.Equals(d.City, ctx.HomeTown, StringComparison.OrdinalIgnoreCase);
            if (homeTown)
            {
                f = HomeTown * (ctx.Away ? ComeHome : 1.0);
            }
            else
            {
                int dist = Math.Max(Math.Abs(d.Location.X - ctx.Home.X), Math.Abs(d.Location.Y - ctx.Home.Y));
                f = dist <= NearDist ? Near : dist <= MidDist ? Mid : Far;
            }

            // Somewhere it cannot walk to from here: a Recall, a gate or a
            // boat. An outing, now and then.
            int comp = _destComp.TryGetValue(d.Name, out var c) ? c : -1;
            if (comp >= 0 && ctx.HereComp >= 0 && comp != ctx.HereComp)
            {
                // Going home is never an outing.
                f *= homeTown || comp == ctx.HomeComp ? 1.0 : OverWater;
            }
            return f;
        }
    }
}
