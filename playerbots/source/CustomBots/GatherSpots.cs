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
using System.IO;
using System.Text.Json;
using Server;

namespace Server.CustomBots
{
    public static class GatherSpots
    {
        // RETIRED (2026-07-06, by request): the synthetic wilderness work
        // sites are gone � gather/mining/lumber sites will be hand-authored
        // in the map editor as MiningSpot / LumberSpot / GatherSpot
        // destinations instead. All the machinery that CONSUMES the spots
        // (GathererBehavior, the weight tables, the arrival handoff) is
        // untouched; authored sites light it back up with zero code.
        public static void Initialize() { }
        public static void EnsureRegistered() { }
        public static void OnCatalogReloaded() { }
    }
}
