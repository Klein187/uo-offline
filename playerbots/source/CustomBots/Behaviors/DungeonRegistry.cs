// =========================================================================
// DungeonRegistry.cs — scoped queries over the dungeon points authored in
// destinations.json (Type = DungeonRoom / DungeonDescend / DungeonAscend,
// tagged with a Dungeon name + Level).
//
// A DungeonCrawler is "dumb and local": each cycle it asks this registry
// for ONE next point among its OWN dungeon's points on its CURRENT level,
// then roams/fights toward it. Depth is EMERGENT, not planned — the only
// lever is the weight on down-teleporters, which scales with skill so
// low-tier bots rarely descend and high-tier bots often do. No global
// pathing, no target-depth.
//
//   Normal mode  — pool = rooms + descend points; weighted, skill-aware.
//   Exit mode    — pool = THIS level's ascend points; nearest one.
//
// Everything is read from DestinationCatalog, so authoring a dungeon in
// the map editor (or by hand in destinations.json) is all it takes — no
// code change to add a dungeon or a level.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Regions;

namespace Server.CustomBots
{
    public static class DungeonRegistry
    {
        // -------------------------------------------------------------------
        // Is this mobile physically inside a dungeon? Region-based, so it
        // covers EVERY dungeon (not just ones with authored crawl points).
        // Wrapped so a missing/odd region setup fails CLOSED (not a dungeon)
        // rather than converting surface bots.
        // -------------------------------------------------------------------
        public static bool IsInDungeon(Mobile m)
        {
            if (m == null || m.Deleted || m.Map == null || m.Map == Map.Internal)
            {
                return false;
            }

            try
            {
                return m.Region?.IsPartOf<DungeonRegion>() == true;
            }
            catch
            {
                return false;
            }
        }

        // Per-skill-tier weight applied to DOWN-teleporters. Indexed by
        // BotSkillTier rank (Novice=0 .. Grandmaster=6). A Novice almost
        // never rolls a descend (stays shallow); a Grandmaster rolls one
        // readily (goes deep). Room points always weigh 1.0, so these are
        // relative to that — at GM a descend is twice as likely as any
        // given room; at Novice it's a tenth as likely. This is the whole
        // "population thins with depth" mechanic.
        private static readonly double[] DescendWeightByRank =
        {
            0.10, // Novice
            0.20, // Apprentice
            0.40, // Journeyman
            0.70, // Adept
            1.00, // Expert
            1.50, // Master
            2.00, // Grandmaster
        };

        private const double RoomWeight = 1.0;

        // A room the crawler already cleared this run keeps a sliver of
        // weight so a fully-swept floor still rolls SOMETHING, but fresh
        // rooms dominate — the crawl sweeps the floor instead of ping-
        // ponging between the two nearest rooms.
        private const double VisitedRoomWeight = 0.15;

        // A point (or the bot itself) counts as "on the graph" only when
        // its nearest waypoint is within one A* search box — beyond that
        // PathFollower can't walk the connecting leg anyway.
        private const int GraphAnchorMaxDist = WaypointGraph.MaxLegDistance;

        private static bool IsInteriorPoint(DestinationType t) =>
            t == DestinationType.DungeonRoom ||
            t == DestinationType.DungeonDescend ||
            t == DestinationType.DungeonAscend;

        private static bool SameDungeon(string a, string b) =>
            !string.IsNullOrEmpty(a) &&
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // -------------------------------------------------------------------
        // All interior points for a (dungeon, level).
        // -------------------------------------------------------------------
        public static List<BotDestination> PointsFor(string dungeon, int level)
        {
            var pts = new List<BotDestination>();
            foreach (var d in DestinationCatalog.All)
            {
                if (!SameDungeon(d.Dungeon, dungeon)) continue;
                if (d.Level != level) continue;
                if (!IsInteriorPoint(d.Type)) continue;
                pts.Add(d);
            }
            return pts;
        }

        // -------------------------------------------------------------------
        // All interior points on the SAME WALKABLE FLOOR as `from`: points
        // whose nearest waypoint sits in the same connected component of
        // the waypoint graph as the bot's nearest waypoint. Dungeon floors
        // are separate components (a teleporter is not a walk edge), so
        // this pools every point the bot can physically reach — regardless
        // of how the points' Dungeon/Level tags were authored, which in
        // practice are inconsistent ("Despise lvl1 ratmen" vs "Despise
        // lvl1"). Empty when the graph doesn't cover this area; callers
        // fall back to the tag-based pool.
        // -------------------------------------------------------------------
        public static List<BotDestination> ReachablePoints(Point3D from)
        {
            var pts = new List<BotDestination>();

            var graph = WaypointRegistry.Graph;
            if (graph == null || graph.NodeCount == 0)
            {
                return pts;
            }

            var anchor = graph.FindNearestNode(from);
            if (anchor == null ||
                ChebyshevDist(from, anchor.Location) > GraphAnchorMaxDist)
            {
                return pts;
            }

            var component = graph.ReachableFrom(anchor.Name);

            foreach (var d in DestinationCatalog.All)
            {
                if (!IsInteriorPoint(d.Type))
                {
                    continue;
                }

                // Anchor the point by proximity, not its stored
                // NearestWaypoint — those go stale after graph edits.
                var node = graph.FindNearestNode(d.Location);
                if (node == null ||
                    ChebyshevDist(d.Location, node.Location) > GraphAnchorMaxDist)
                {
                    continue;
                }
                if (!component.Contains(node.Name))
                {
                    continue;
                }

                pts.Add(d);
            }
            return pts;
        }

        // -------------------------------------------------------------------
        // Roll the crawler's next point. Returns null if the floor has no
        // suitable points (caller falls back to local wandering).
        //
        // The pool is the bot's walkable floor (ReachablePoints); the old
        // (dungeon, level) tag pool is only a fallback for floors with no
        // waypoint coverage. `visited` (optional) is the set of room names
        // already cleared this run — unvisited rooms are strongly
        // preferred so the crawl sweeps forward.
        //
        //   exitMode == false → rooms + descend (skill-weighted descent)
        //   exitMode == true  → this floor's nearest ascend point
        // -------------------------------------------------------------------
        public static BotDestination RollPoint(
            string dungeon, int level, BotSkillTier tier, bool exitMode, Point3D from,
            HashSet<string> visited = null)
        {
            var pts = ReachablePoints(from);
            if (pts.Count == 0)
            {
                pts = PointsFor(dungeon, level);
            }
            if (pts.Count == 0) return null;

            if (exitMode)
            {
                // Seek THIS level's up-teleporter. Nearest one — the climb is
                // a local reflex, not a planned route.
                BotDestination best = null;
                int bestDist = int.MaxValue;
                foreach (var p in pts)
                {
                    if (p.Type != DestinationType.DungeonAscend) continue;
                    int dist = ChebyshevDist(from, p.Location);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = p;
                    }
                }
                if (best != null)
                {
                    return best;
                }

                // No ascend authored on this floor. Some dungeons' up-stairs
                // are mislabeled as Descend records (Covetous / Hythloth /
                // Deceit's scrambled level numbering) — trying ANY transition
                // pad beats wandering until the exit watchdog teleports the
                // bot out: take the nearest pad and let the landing decide
                // what it was. A pad that truly goes DOWN is caught by the
                // revolving-door breaker and the exit watchdog.
                foreach (var p in pts)
                {
                    if (p.Type != DestinationType.DungeonDescend) continue;
                    int dist = ChebyshevDist(from, p.Location);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = p;
                    }
                }
                return best; // null when the floor has no transition pads at all
            }

            // Normal mode: rooms + descend points, weighted. Ascend points are
            // excluded — a crawler only climbs in exit mode.
            int rank = BotSkillTierHelper.Rank(tier);
            if (rank < 0) rank = 0;
            if (rank >= DescendWeightByRank.Length) rank = DescendWeightByRank.Length - 1;
            double descendWeight = DescendWeightByRank[rank];

            double total = 0;
            var weights = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                double w = pts[i].Type switch
                {
                    DestinationType.DungeonRoom =>
                        visited != null && visited.Contains(pts[i].Name)
                            ? VisitedRoomWeight
                            : RoomWeight,
                    DestinationType.DungeonDescend => descendWeight,
                    _                              => 0.0, // ascend excluded
                };
                weights[i] = w;
                total += w;
            }

            if (total <= 0) return null;

            double r = Utility.RandomDouble() * total;
            double acc = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                acc += weights[i];
                if (r <= acc && weights[i] > 0) return pts[i];
            }
            // Numeric edge — return the last positive-weight point.
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                if (weights[i] > 0) return pts[i];
            }
            return null;
        }

        // -------------------------------------------------------------------
        // Nearest interior point on the bot's OWN walkable floor. Dungeon
        // levels sit side by side in the map strip, so plain 2D proximity
        // (NearestPoint) can grab a point through a wall on a DIFFERENT
        // floor and mis-scope the crawler after a stair landing. This form
        // only considers points whose waypoint component matches the bot's —
        // callers fall back to NearestPoint when the floor has no points.
        // -------------------------------------------------------------------
        public static BotDestination NearestPointOnFloor(Point3D from, int maxDist)
        {
            var pts = ReachablePoints(from);
            BotDestination best = null;
            int bestDist = maxDist + 1;
            for (int i = 0; i < pts.Count; i++)
            {
                int dist = ChebyshevDist(from, pts[i].Location);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = pts[i];
                }
            }
            return best;
        }

        // -------------------------------------------------------------------
        // Context recovery: which dungeon/level is a bot standing in? Used
        // when a crawler wakes with no context (server reload) — it adopts
        // the nearest interior point's tag/level. This is also the design's
        // "re-scope on arrival via the level's landing area" trick, done by
        // proximity instead of a painted polygon so it works with no zone
        // data. Returns the nearest interior point within maxDist, or null.
        // -------------------------------------------------------------------
        public static BotDestination NearestPoint(Point3D from, int maxDist)
        {
            BotDestination best = null;
            int bestDist = maxDist + 1;
            foreach (var d in DestinationCatalog.All)
            {
                if (string.IsNullOrEmpty(d.Dungeon)) continue;
                if (!IsInteriorPoint(d.Type)) continue;
                int dist = ChebyshevDist(from, d.Location);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }
            return best;
        }

        private static int ChebyshevDist(Point3D a, Point3D b)
        {
            int dx = a.X - b.X; if (dx < 0) dx = -dx;
            int dy = a.Y - b.Y; if (dy < 0) dy = -dy;
            return dx > dy ? dx : dy;
        }
    }
}
