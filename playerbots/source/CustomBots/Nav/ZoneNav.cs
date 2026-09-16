// =========================================================================
// ZoneNav.cs — routing over the painted walk mesh.
//
// The waypoint graph still plans every trip. This layer only replaces the
// WALKER for a leg whose two ends both sit inside mesh zones (a Walk zone
// or a painted Area): it finds the chain of zone links from here to
// there and hands ZoneFollower a list of tiles to step to. Everything
// outside zone coverage keeps the engine PathFollower exactly as before.
//
// Mode (the per-bot flag from the design brief, made fleet-wide so it can
// be flipped without a restart):
//     [zonenav off     nobody uses zones
//     [zonenav half    half the bots do (by serial), the other half the
//                      engine walker — the A/B soak
//     [zonenav on      everybody
//
// Link strikes: when a bot fails to cross a link the link gets a strike,
// the same way NavEdgeHealth scores a waypoint edge, and routes avoid it
// for ten minutes. The stuck report counts these as zone_stall.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public enum ZoneNavMode { Off, Half, On }

    public static class ZoneNav
    {
        public static ZoneNavMode Mode { get; set; } = ZoneNavMode.Half;
        public static string ModeName => Mode.ToString().ToLowerInvariant();

        // [zonenav verbose on|off: log every zone-walked leg. For soaks.
        public static bool Verbose { get; set; }

        public static void Configure()
        {
            CommandSystem.Register("zonenav", AccessLevel.GameMaster, OnZoneNav);
        }

        private static void OnZoneNav(CommandEventArgs e)
        {
            string arg = e.Arguments.Length > 0 ? e.Arguments[0].ToLowerInvariant() : "";
            switch (arg)
            {
                case "off":  Mode = ZoneNavMode.Off; break;
                case "half": Mode = ZoneNavMode.Half; break;
                case "on":   Mode = ZoneNavMode.On; break;
                case "verbose":
                    Verbose = e.Arguments.Length < 2 || e.Arguments[1].ToLowerInvariant() != "off";
                    e.Mobile.SendMessage($"Zone walking log: {(Verbose ? "on" : "off")}.");
                    return;
                case "":
                    break;
                default:
                    e.Mobile.SendMessage("Usage: [zonenav off|half|on|verbose [off]");
                    return;
            }
            e.Mobile.SendMessage($"Zone walking: {ModeName}. Zones {ZoneRegistry.All.Count}, links {ZoneRegistry.Links.Count}.");
        }

        // Does this bot walk zones? A per-bot override wins; otherwise the
        // fleet mode decides, with the half split fixed by serial so a bot
        // stays on one side of the soak for its whole life.
        public static bool WantsZones(PlayerBot bot, bool? overrideFlag)
        {
            if (overrideFlag.HasValue)
            {
                return overrideFlag.Value;
            }
            return Mode switch
            {
                ZoneNavMode.On   => true,
                ZoneNavMode.Half => bot != null && ((int)bot.Serial.Value & 1) == 0,
                _                => false,
            };
        }

        // -----------------------------------------------------------------
        // Link health
        // -----------------------------------------------------------------

        private sealed class Strike { public int Count; public DateTime Expires; }
        private static readonly Dictionary<int, Strike> _strikes = new();
        private static readonly TimeSpan StrikeLife = TimeSpan.FromMinutes(10);

        public static void ReportLinkFailure(ZoneLink link, PlayerBot bot, string why)
        {
            if (link == null)
            {
                return;
            }
            if (!_strikes.TryGetValue(link.Id, out var s) || Core.Now >= s.Expires)
            {
                s = new Strike();
                _strikes[link.Id] = s;
            }
            s.Count++;
            s.Expires = Core.Now + StrikeLife;
            StuckTelemetry.Record(bot, "zone_stall", $"{link} ({why}, strike {s.Count})");
        }

        public static double PenaltyFor(ZoneLink link)
        {
            if (link == null || !_strikes.TryGetValue(link.Id, out var s) || Core.Now >= s.Expires)
            {
                return 1.0;
            }
            return 1.0 + 3.0 * s.Count;
        }

        public static int StrikesOn(ZoneLink link) =>
            link != null && _strikes.TryGetValue(link.Id, out var s) && Core.Now < s.Expires ? s.Count : 0;

        // -----------------------------------------------------------------
        // Routing
        // -----------------------------------------------------------------

        public sealed class Route
        {
            public PaintedZone StartZone;
            public PaintedZone GoalZone;
            // Zone entered after crossing Links[i]. Zones[0] is StartZone.
            public readonly List<PaintedZone> Zones = new();
            public readonly List<ZoneLink> Links = new();
            // Link midpoints then the goal itself.
            public readonly List<Point3D> Targets = new();
        }

        public static bool CoversBoth(Map map, Point3D from, Point3D to)
        {
            if (map != ZoneRegistry.MeshMap)
            {
                return false;
            }
            return ZoneRegistry.MeshZoneAt(from) != null && ZoneRegistry.MeshZoneAt(to) != null;
        }

        private static int Cheb(int ax, int ay, int bx, int by) =>
            Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

        // A* over links. A search node is "standing on link L having come
        // out of zone Z", so the zone entered is fixed by the pair.
        public static Route FindRoute(Map map, Point3D from, Point3D to)
        {
            if (map != ZoneRegistry.MeshMap)
            {
                return null;
            }
            var start = ZoneRegistry.MeshZoneAt(from);
            var goal = ZoneRegistry.MeshZoneAt(to);
            if (start == null || goal == null)
            {
                return null;
            }

            int startPart = start.PartNear(from.X, from.Y);
            int goalPart = goal.PartNear(to.X, to.Y);

            var route = new Route { StartZone = start, GoalZone = goal };
            route.Zones.Add(start);
            if (ReferenceEquals(start, goal) && (startPart < 0 || goalPart < 0 || startPart == goalPart))
            {
                route.Targets.Add(to);
                return route;
            }
            if (startPart < 0 || goalPart < 0)
            {
                return null;   // off the walkable ground of its own zone
            }

            var links = ZoneRegistry.Links;
            if (links.Count == 0)
            {
                return null;
            }

            // Node key = link id * 2 + side entered (0 = entered A, 1 = B).
            var open = new PriorityQueue<int, double>();
            var g = new Dictionary<int, double>();
            var came = new Dictionary<int, int>();   // key -> previous key, -1 for start
            int Key(ZoneLink l, PaintedZone entered) => l.Id * 2 + (ReferenceEquals(entered, l.A) ? 0 : 1);
            ZoneLink LinkOf(int key) => links[key / 2];
            PaintedZone EnteredOf(int key) { var l = links[key / 2]; return (key & 1) == 0 ? l.A : l.B; }

            foreach (var l in start.Links)
            {
                var entered = l.Other(start);
                if (!entered.IsMesh || l.PartOf(start) != startPart)
                {
                    continue;
                }
                int k = Key(l, entered);
                double cost = Cheb(from.X, from.Y, l.MidX, l.MidY) * start.Cost * PenaltyFor(l);
                g[k] = cost;
                came[k] = -1;
                open.Enqueue(k, cost + Cheb(l.MidX, l.MidY, to.X, to.Y));
            }

            var closed = new HashSet<int>();
            int bestGoalKey = -1;
            double bestGoalCost = double.MaxValue;
            int expansions = 0;

            while (open.Count > 0)
            {
                open.TryDequeue(out int k, out double f);
                if (closed.Contains(k))
                {
                    continue;
                }
                if (f >= bestGoalCost)
                {
                    break;
                }
                closed.Add(k);
                if (++expansions > 20000)
                {
                    break;
                }

                var l = LinkOf(k);
                var zone = EnteredOf(k);
                double gk = g[k];

                if (ReferenceEquals(zone, goal) && l.PartOf(zone) == goalPart)
                {
                    double total = gk + Cheb(l.MidX, l.MidY, to.X, to.Y) * zone.Cost;
                    if (total < bestGoalCost)
                    {
                        bestGoalCost = total;
                        bestGoalKey = k;
                    }
                    continue;
                }

                int here = l.PartOf(zone);
                foreach (var m in zone.Links)
                {
                    if (ReferenceEquals(m, l) || m.PartOf(zone) != here)
                    {
                        continue;
                    }
                    var next = m.Other(zone);
                    if (!next.IsMesh)
                    {
                        continue;
                    }
                    int nk = Key(m, next);
                    if (closed.Contains(nk))
                    {
                        continue;
                    }
                    double ng = gk + Math.Max(1, Cheb(l.MidX, l.MidY, m.MidX, m.MidY)) * zone.Cost * PenaltyFor(m);
                    if (g.TryGetValue(nk, out var old) && old <= ng)
                    {
                        continue;
                    }
                    g[nk] = ng;
                    came[nk] = k;
                    open.Enqueue(nk, ng + Cheb(m.MidX, m.MidY, to.X, to.Y));
                }
            }

            if (bestGoalKey < 0)
            {
                return null;
            }

            var chain = new List<int>();
            for (int k = bestGoalKey; k >= 0; k = came[k])
            {
                chain.Add(k);
            }
            chain.Reverse();
            foreach (var k in chain)
            {
                var l = LinkOf(k);
                route.Links.Add(l);
                route.Zones.Add(EnteredOf(k));
                route.Targets.Add(l.Mid);
            }
            route.Targets.Add(to);
            return route;
        }
    }
}
