// =========================================================================
// GatherSpots.cs — synthetic wilderness work sites (IDEAS 1.5's
// "lumberjack in the middle of nowhere").
//
// Nobody hand-authors forty logging camps: instead, at startup we scan
// the waypoint graph for nodes FAR from any city destination — genuine
// wilderness that's still nav-reachable — and register them in the
// DestinationCatalog as GatherSpot destinations. From there the whole
// existing machinery just works: gatherer classes roll them heavily,
// Travelers walk out to them, the arrival handoff attaches
// GathererBehavior, and the haul home is an ordinary town trip.
//
// Spots are spread out (min spacing) so gatherers scatter across the
// map instead of forming one weird logging convention.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public static class GatherSpots
    {
        // A node this far (chebyshev) from every city-tagged destination
        // counts as wilderness.
        private const int MinCityDistance = 45;

        // Keep spots apart so the workforce scatters.
        private const int MinSpotSpacing = 70;

        private const int MaxSpots = 40;

        // Dungeon interior coordinate space — never a work site.
        private const int DungeonSpaceX = 5000;

        private static List<BotDestination> _generated;

        // ModernUO calls Initialize() after the world loads; the waypoint
        // graph and destination catalog are both up by then. Idempotent —
        // BotStartupManager also calls it defensively before respawning.
        public static void Initialize() => EnsureRegistered();

        public static void EnsureRegistered()
        {
            if (_generated != null)
            {
                DestinationCatalog.RegisterSynthetic(_generated);
                return;
            }

            var graph = WaypointRegistry.Graph;
            if (graph.NodeCount == 0 || DestinationCatalog.Count == 0)
            {
                return; // nothing to work with (fresh install) — try later
            }

            // City reference points: any destination that belongs to a city.
            var cityPoints = new List<Point3D>();
            foreach (var d in DestinationCatalog.All)
            {
                if (!string.IsNullOrEmpty(d.City))
                {
                    cityPoints.Add(d.Location);
                }
            }

            var picked = new List<WaypointNode>();
            foreach (var node in graph.AllNodes)
            {
                var loc = node.Location;
                if (loc.X >= DungeonSpaceX)
                {
                    continue;
                }

                bool nearCity = false;
                foreach (var c in cityPoints)
                {
                    if (Math.Max(Math.Abs(c.X - loc.X), Math.Abs(c.Y - loc.Y)) < MinCityDistance)
                    {
                        nearCity = true;
                        break;
                    }
                }
                if (nearCity)
                {
                    continue;
                }

                bool crowded = false;
                foreach (var p in picked)
                {
                    if (Math.Max(Math.Abs(p.Location.X - loc.X),
                                 Math.Abs(p.Location.Y - loc.Y)) < MinSpotSpacing)
                    {
                        crowded = true;
                        break;
                    }
                }
                if (crowded)
                {
                    continue;
                }

                picked.Add(node);
                if (picked.Count >= MaxSpots)
                {
                    break;
                }
            }

            _generated = new List<BotDestination>();
            int i = 1;
            foreach (var node in picked)
            {
                _generated.Add(new BotDestination
                {
                    Name            = $"Gather Spot {i++}",
                    Location        = node.Location,
                    Type            = DestinationType.GatherSpot,
                    City            = "",
                    NearestWaypoint = node.Name,
                });
            }

            DestinationCatalog.RegisterSynthetic(_generated);
            Console.WriteLine($"[GatherSpots] registered {_generated.Count} wilderness work site(s).");
        }

        // The catalog wipes on reload ([ReloadDestinations / editor token);
        // re-add our synthetic entries afterward.
        public static void OnCatalogReloaded()
        {
            if (_generated != null)
            {
                DestinationCatalog.RegisterSynthetic(_generated);
            }
        }
    }
}
