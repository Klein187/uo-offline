// =========================================================================
// ExitWalk.cs — find the way out of a building to the road.
//
// A trip starts at the nearest waypoint the bot can walk to. Inside a shop,
// an inn or a house the nearest waypoints are often on the far side of a
// wall: the door faces another street, or the way round is longer than
// the engine's 38-tile A* box allows. Every one of the four nearest then
// fails, the trip falls back to the closest by straight line, and the bot
// walks into the wall until the stuck ladder gives up (the 10/6 audit:
// two thirds of all STUCK lines were the first leg of a trip).
//
// A player walks out the door. This floods outward from the bot over the
// tiles the real movement code allows, through closed doors (PlayerBot.Move
// opens them), and stops at the first tile next to a waypoint. The walk
// back along the flood is the way out. FindWayOut returns null when there
// is none within reach (a roof, a sealed room); the caller rescues.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public static class ExitWalk
    {
        // How far the flood reaches. A shop door and the street beyond it
        // are always closer than this.
        public const int Radius = 40;

        // Safety cap on the flood (a 40-tile box is 6,561 tiles).
        private const int MaxTiles = 7000;

        private static long Key(int x, int y) => ((long)(uint)x << 32) | (uint)y;

        private static Direction DirFor(int dx, int dy) => (dx, dy) switch
        {
            (0, -1)  => Direction.North,
            (1, -1)  => Direction.Right,
            (1, 0)   => Direction.East,
            (1, 1)   => Direction.Down,
            (0, 1)   => Direction.South,
            (-1, 1)  => Direction.Left,
            (-1, 0)  => Direction.West,
            _        => Direction.Up
        };

        // sealedIn: the flood ran out of tiles without touching its edge, so
        // there is no way out on foot at all (not just none within reach).
        public static List<Point3D> FindWayOut(PlayerBot bot, out WaypointNode exitNode, out bool sealedIn) =>
            FindWayOut(bot, bot.Location, out exitNode, out sealedIn, out _);

        public static List<Point3D> FindWayOut(PlayerBot bot, Point3D from, out WaypointNode exitNode,
                                               out bool sealedIn, out int flooded)
        {
            exitNode = null;
            sealedIn = false;
            flooded = 0;
            var map = bot.Map;
            var graph = WaypointRegistry.Graph;
            if (map == null || map == Map.Internal || graph == null || graph.NodeCount == 0)
            {
                return null;
            }

            // Waypoints in reach, by the tiles next to them.
            var near = new List<WaypointNode>(32);
            graph.FindNearestNodes(from, 32, near);
            var byTile = new Dictionary<long, WaypointNode>();
            foreach (var n in near)
            {
                var l = n.Location;
                if (Math.Max(Math.Abs(l.X - from.X), Math.Abs(l.Y - from.Y)) > Radius)
                {
                    continue;
                }
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        byTile.TryAdd(Key(l.X + dx, l.Y + dy), n);
                    }
                }
            }
            if (byTile.Count == 0)
            {
                return null;
            }

            int sx = from.X, sy = from.Y;
            var cost = new Dictionary<long, int>();
            var zAt = new Dictionary<long, int>();
            var parent = new Dictionary<long, long>();
            var pq = new PriorityQueue<(int x, int y), int>();
            long start = Key(sx, sy);
            cost[start] = 0;
            zAt[start] = from.Z;
            pq.Enqueue((sx, sy), 0);
            int popped = 0;
            bool touchedEdge = false;

            while (pq.TryDequeue(out var cur, out int cc))
            {
                long ck = Key(cur.x, cur.y);
                if (cost[ck] < cc)
                {
                    continue;
                }
                flooded = popped + 1;
                if (++popped > MaxTiles)
                {
                    touchedEdge = true;
                    break;
                }

                int cz = zAt[ck];
                if (byTile.TryGetValue(ck, out var hit) && Math.Abs(cz - hit.Location.Z) <= 20 &&
                    (cur.x != sx || cur.y != sy))
                {
                    exitNode = hit;
                    var path = new List<Point3D>();
                    long k = ck;
                    while (k != start)
                    {
                        path.Add(new Point3D((int)(k >> 32), (int)(uint)k, zAt[k]));
                        k = parent[k];
                    }
                    path.Reverse();
                    return path;
                }

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }
                        int nx = cur.x + dx, ny = cur.y + dy;
                        if (Math.Abs(nx - sx) > Radius || Math.Abs(ny - sy) > Radius)
                        {
                            touchedEdge = true;
                            continue;
                        }
                        // The real movement rules, so canal bridges and dock
                        // ramps count; a closed door counts as open.
                        bool ok = Server.Movement.Movement.CheckMovement(
                            bot, map, new Point3D(cur.x, cur.y, cz), DirFor(dx, dy), out int nz);
                        if (!ok && Walkable.ClosedDoorAt(map, nx, ny, cz))
                        {
                            ok = true;
                            nz = cz;
                        }
                        if (!ok)
                        {
                            continue;
                        }
                        int nc = cc + (dx != 0 && dy != 0 ? 14 : 10);
                        long nk = Key(nx, ny);
                        if (!cost.TryGetValue(nk, out int old) || nc < old)
                        {
                            cost[nk] = nc;
                            zAt[nk] = nz;
                            parent[nk] = ck;
                            pq.Enqueue((nx, ny), nc);
                        }
                    }
                }
            }
            sealedIn = !touchedEdge;
            return null;
        }
    }

    // Walks a tile path found by ExitWalk, one step per call. Off the path
    // (a nudge, a shove) it rejoins at the nearest tile ahead; if it can't,
    // the engine walker takes the rest of the leg.
    public sealed class ExitPathFollower : ILegFollower
    {
        private readonly PlayerBot _bot;
        private readonly List<Point3D> _path;
        private readonly Point3D _goal;
        private int _idx;
        private EnginePathFollower _fallback;
        private int _blocked;

        public ExitPathFollower(PlayerBot bot, List<Point3D> path, Point3D goal)
        {
            _bot = bot;
            _path = path;
            _goal = goal;
        }

        // Tiles still to walk; the Traveler's stuck check measures progress
        // by this, since the way out often leads away from the waypoint
        // before it comes back.
        public int Remaining => _fallback != null ? -1 : _path.Count - _idx;

        private bool TryMove(Direction d, bool run)
        {
            d = run ? d | Direction.Running : d;
            _bot.SetDirection(d);
            if (_bot.Move(d))
            {
                _blocked = 0;
                return true;
            }
            return false;
        }

        public Point3D GetGoalLocation() => _fallback?.GetGoalLocation() ?? _goal;

        public void ForceRepath() => _fallback?.ForceRepath();

        public bool Follow(bool run, int range)
        {
            if (PathFollower.Check(_bot.Location, _goal, range))
            {
                return true;
            }
            if (_fallback != null)
            {
                return _fallback.Follow(run, range);
            }

            // Skip tiles already reached; rejoin after a nudge.
            int best = -1;
            for (int i = _idx; i < _path.Count; i++)
            {
                var t = _path[i];
                if (Math.Max(Math.Abs(t.X - _bot.X), Math.Abs(t.Y - _bot.Y)) <= 1)
                {
                    best = i;
                }
            }
            if (best < 0)
            {
                _fallback = new EnginePathFollower(_bot, _goal);
                return _fallback.Follow(run, range);
            }
            var next = _path[best];
            if (next.X == _bot.X && next.Y == _bot.Y)
            {
                _idx = best + 1;
                if (_idx >= _path.Count)
                {
                    _fallback = new EnginePathFollower(_bot, _goal);
                    return _fallback.Follow(run, range);
                }
                next = _path[_idx];
            }
            else
            {
                _idx = best;
            }

            // Straight along the path; someone standing on it, a step to
            // either side (the next call rejoins the path).
            var d = _bot.GetDirectionTo(next) & Direction.Mask;
            if (!TryMove(d, run) && !TryMove((Direction)(((int)d + 1) & 7), run) &&
                !TryMove((Direction)(((int)d + 7) & 7), run) && ++_blocked >= 6)
            {
                // Blocked for good (a vendor in the doorway): let the engine
                // walker have it.
                _fallback = new EnginePathFollower(_bot, _goal);
            }
            return PathFollower.Check(_bot.Location, _goal, range);
        }
    }
}
