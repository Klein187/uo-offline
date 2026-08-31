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

using System;
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

            // IsPartOf<GuardedRegion> is NOT the question. TownRegion derives
            // from GuardedRegion, so every town answers yes to it — including
            // Buccaneer's Den, which is a town whose guards are switched off.
            // Asking the wrong question would have driven reds out of the one
            // town that is theirs. What matters is whether the guards there
            // actually turn out.
            var region = Region.Find(p, map).GetRegion<GuardedRegion>();
            var guarded = region != null && !region.IsDisabled();
            _guardedCache[p] = guarded;
            return guarded;
        }

        public static bool IsGuardedPlace(BotDestination d, Map map) =>
            d != null && IsGuardedPlace(d.ArrivalPoint ?? d.Location, map);

        // Cleared when the destination catalog reloads, so an edited arrival
        // point is not answered from a stale entry.
        public static void ClearCache()
        {
            _guardedCache.Clear();
            _guardedWaypoints = null;
        }

        // ---- Routes, not just destinations ------------------------------
        //
        // Keeping reds out of guarded towns as DESTINATIONS was only half of
        // it. The roads run through the towns. A murderer walking from the
        // Honor trail to the Spirituality shrine — both places it is welcome
        // — was handed a route reading "... Honor Trail 1, WP 140, trinbank,
        // WP 138, trin2, trinsicgate ..." and died in Trinsic on the way
        // past. Same for Vesper on the Sacrifice trail.
        //
        // So guarded waypoints are made expensive rather than forbidden. A
        // detour is taken when one exists, and a road that only runs through
        // a town is still a road: a hard block would strand reds in pockets
        // of the graph and hand us the marooning bug instead.
        private const double GuardedDetourCost = 30.0;

        private static HashSet<string> _guardedWaypoints;

        private static bool IsGuardedWaypoint(string name)
        {
            if (_guardedWaypoints == null)
            {
                _guardedWaypoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var graph = WaypointRegistry.Graph;
                if (graph != null)
                {
                    foreach (var node in graph.AllNodes)
                    {
                        if (IsGuardedPlace(node.Location, Map.Felucca))
                        {
                            _guardedWaypoints.Add(node.Name);
                        }
                    }
                }

                Console.WriteLine(
                    $"[RedTerritory] {_guardedWaypoints.Count} waypoints stand under guards; " +
                    $"murderers route around them.");
            }

            return _guardedWaypoints.Contains(name);
        }

        // Hand this to WaypointGraph.FindPath. Null for anyone the guards do
        // not want, which leaves their routing exactly as it was.
        public static Func<string, double> RouteCost(PlayerBot bot) =>
            IsRed(bot)
                ? static name => IsGuardedWaypoint(name) ? GuardedDetourCost : 1.0
                : null;

        // Buccaneer's Den has no watch. That is the whole idea of the place:
        // in the era it is where murderers went BECAUSE the guards were not
        // looking, and every rule above is built on it.
        //
        // regions.json already ships GuardsDisabled for it, and nothing in
        // the code summons guards into a disabled region -- but the flag is
        // one [toggleguards away from being flipped in game, and a flip
        // survives in the save. Assert it at boot so the pirate town cannot
        // quietly acquire a watch.
        public static void EnsureUnguarded()
        {
            var anchor = new Point3D(2706, 2163, 0);
            var region = Region.Find(anchor, Map.Felucca)?.GetRegion<GuardedRegion>();

            if (region == null)
            {
                Console.WriteLine(
                    "[RedTerritory] no region at Buccaneer's Den — guards not asserted.");
                return;
            }

            if (region.GuardsDisabled)
            {
                return;
            }

            region.GuardsDisabled = true;
            ClearCache();
            Console.WriteLine(
                $"[RedTerritory] guards were ENABLED in '{region.Name}' — switched off " +
                $"(the pirate town has no watch).");
        }

        // THE question any trip should ask before setting out: can this bot
        // go here and still be alive when it arrives? Covers both rules —
        // the pirate town a blue keeps out of, and the guarded towns a red
        // cannot survive.
        public static bool MayGoTo(PlayerBot bot, BotDestination d) =>
            !ShouldAvoid(bot, d) &&
            (!IsRed(bot) || !IsGuardedPlace(d, bot?.Map));
    }
}
