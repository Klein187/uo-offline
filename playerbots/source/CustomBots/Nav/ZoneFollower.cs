// =========================================================================
// ZoneFollower.cs — the leg walker for the painted walk mesh.
//
// Same three calls as the engine's PathFollower (Follow, GetGoalLocation,
// ForceRepath), so TravelerBehavior swaps one for the other and nothing
// else in it changes. LegFollowers.Create picks: a ZoneFollower when the
// bot walks zones and both ends of the leg sit inside mesh zones, else
// the engine walker wrapped in EnginePathFollower.
//
// Inside a zone the bot steps straight at its target. When the next tile
// is blocked (a lamp post, a bench, another bot) it tries a step to
// either side, and failing that runs a small A* over the tiles of the
// current and next zone only. Hand-drawn zones are not assumed clean;
// this is what makes a street drawn as one shape walkable.
//
// If routing fails or the bot stalls twice on the mesh, the leg falls
// back to the engine walker. Bots never get worse than today.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using CalcMoves = Server.Movement.Movement;

namespace Server.CustomBots
{
    public interface ILegFollower
    {
        bool Follow(bool run, int range);
        Point3D GetGoalLocation();
        void ForceRepath();
    }

    public sealed class EnginePathFollower : ILegFollower
    {
        private readonly PathFollower _inner;
        public EnginePathFollower(Mobile from, Point3D goal) => _inner = new PathFollower(from, goal);
        public bool Follow(bool run, int range) => _inner.Follow(run, range);
        public Point3D GetGoalLocation() => _inner.GetGoalLocation();
        public void ForceRepath() => _inner.ForceRepath();
    }

    public static class LegFollowers
    {
        public static ILegFollower Create(PlayerBot bot, Point3D goal, bool? useZones)
        {
            if (bot?.Map != null && ZoneNav.WantsZones(bot, useZones) &&
                ZoneNav.CoversBoth(bot.Map, bot.Location, goal))
            {
                if (ZoneNav.Verbose || useZones == true)
                {
                    Console.WriteLine($"[zones] {bot.Name} walks zones: ({bot.X},{bot.Y}) -> ({goal.X},{goal.Y})");
                }
                return new ZoneFollower(bot, goal);
            }
            return new EnginePathFollower(bot, goal);
        }
    }

    public sealed class ZoneFollower : ILegFollower
    {
        private readonly PlayerBot _bot;
        private readonly Point3D _goal;
        private readonly Map _map;

        private ZoneNav.Route _route;
        private int _idx;
        private int _replans;
        private EnginePathFollower _fallback;
        public string FallbackReason { get; private set; }

        // Local detour inside the current zone.
        private List<Point3D> _local;
        private int _localIdx;
        private DateTime _nextLocalPlan = DateTime.MinValue;

        // Progress toward the current target.
        private int _bestDist = int.MaxValue;
        private DateTime _lastProgress = DateTime.MinValue;
        private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan LocalPlanEvery = TimeSpan.FromMilliseconds(800);

        private const int LocalMaxExpansions = 2500;
        private const int LocalHalfBox = 48;

        public ZoneFollower(PlayerBot bot, Point3D goal)
        {
            _bot = bot;
            _goal = goal;
            _map = bot.Map;
        }

        public bool OnMesh => _fallback == null;
        public ZoneNav.Route Route => _route;
        public int TargetIndex => _idx;
        public ZoneLink CurrentLink =>
            _route != null && _idx < _route.Links.Count ? _route.Links[_idx] : null;

        public Point3D GetGoalLocation()
        {
            if (_fallback != null)
            {
                return _fallback.GetGoalLocation();
            }
            if (_route != null && _idx < _route.Targets.Count)
            {
                return _route.Targets[_idx];
            }
            return _goal;
        }

        public void ForceRepath()
        {
            _local = null;
            _nextLocalPlan = DateTime.MinValue;
            if (_fallback != null)
            {
                _fallback.ForceRepath();
                return;
            }
            _route = null;
        }

        private static int Cheb(Point3D a, Point3D b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private bool Plan()
        {
            _route = ZoneNav.FindRoute(_map, _bot.Location, _goal);
            _idx = 0;
            _local = null;
            _bestDist = int.MaxValue;
            _lastProgress = Core.Now;
            return _route != null;
        }

        private void UseFallback(string why)
        {
            FallbackReason = why;
            _fallback = new EnginePathFollower(_bot, _goal);
            _route = null;
        }

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
            if (_route == null)
            {
                if (!Plan())
                {
                    if (ZoneNav.Verbose)
                    {
                        Console.WriteLine($"[zones] {_bot.Name}: no zone route ({_bot.X},{_bot.Y}) -> ({_goal.X},{_goal.Y})");
                    }
                    UseFallback("no zone route");
                    return _fallback.Follow(run, range);
                }
            }

            // Crossed the current link? Then aim at the next one.
            while (_idx < _route.Targets.Count - 1)
            {
                var t = _route.Targets[_idx];
                var nextZone = _route.Zones[_idx + 1];
                var curZone = _route.Zones[_idx];
                int d = Cheb(_bot.Location, t);
                bool crossed = d <= 1 ||
                               (nextZone.Contains(_bot.X, _bot.Y) &&
                                (!curZone.Contains(_bot.X, _bot.Y) || d <= 3));
                if (!crossed)
                {
                    break;
                }
                _idx++;
                _local = null;
                _bestDist = int.MaxValue;
                _lastProgress = Core.Now;
            }

            var target = _route.Targets[_idx];
            StepToward(target, run);

            int dist = Cheb(_bot.Location, target);
            if (dist < _bestDist)
            {
                _bestDist = dist;
                _lastProgress = Core.Now;
            }
            else if (Core.Now - _lastProgress > StallAfter)
            {
                Stall();
            }

            return PathFollower.Check(_bot.Location, _goal, range);
        }

        private void Stall()
        {
            var link = CurrentLink;
            if (link != null)
            {
                ZoneNav.ReportLinkFailure(link, _bot, "no progress");
            }
            else
            {
                StuckTelemetry.Record(_bot, "zone_stall", $"final approach in '{_route?.Zones[_idx]?.Name}'");
            }
            _replans++;
            if (_replans <= 2 && Plan())
            {
                return;
            }
            UseFallback("stalled on the mesh");
        }

        // ---- stepping ----

        private bool TryMove(Direction d, bool run)
        {
            d = run ? d | Direction.Running : d & Direction.Mask;
            int x = _bot.X, y = _bot.Y;
            CalcMoves.Offset(d & Direction.Mask, ref x, ref y);
            if (!Allowed(x, y))
            {
                return false;
            }
            _bot.SetDirection(d);
            return _bot.Move(d);
        }

        private static Direction Rot(Direction d, int k) =>
            (Direction)((((int)d & 7) + k + 8) % 8);

        // Stay on the mesh: the current zone, the next one, or the goal's.
        private bool Allowed(int x, int y)
        {
            if (_route == null)
            {
                return true;
            }
            if (_route.Zones[_idx].ContainsGrown(x, y))
            {
                return true;
            }
            if (_idx + 1 < _route.Zones.Count && _route.Zones[_idx + 1].ContainsGrown(x, y))
            {
                return true;
            }
            return _route.GoalZone.ContainsGrown(x, y);
        }

        private void StepToward(Point3D target, bool run)
        {
            // A detour in progress: keep following it.
            if (_local != null)
            {
                while (_localIdx < _local.Count &&
                       _local[_localIdx].X == _bot.X && _local[_localIdx].Y == _bot.Y)
                {
                    _localIdx++;
                }
                if (_localIdx >= _local.Count)
                {
                    _local = null;
                }
                else
                {
                    var n = _local[_localIdx];
                    if (TryMove(_bot.GetDirectionTo(n), run))
                    {
                        return;
                    }
                    _local = null;   // something new in the way; fall through
                }
            }

            var d0 = _bot.GetDirectionTo(target) & Direction.Mask;
            if (TryMove(d0, run))
            {
                return;
            }
            if (TryMove(Rot(d0, 1), run) || TryMove(Rot(d0, -1), run))
            {
                return;
            }

            if (Core.Now < _nextLocalPlan)
            {
                return;
            }
            _nextLocalPlan = Core.Now + LocalPlanEvery;
            _local = LocalPath(target);
            _localIdx = 0;
            if (_local != null && _local.Count > 0)
            {
                if (TryMove(_bot.GetDirectionTo(_local[0]), run))
                {
                    _localIdx = 1;
                    return;
                }
                _local = null;
            }
            // Nothing worked this step. The stall clock is running.
        }

        // ---- local A* over the zone's own tiles ----

        private List<Point3D> LocalPath(Point3D target)
        {
            int minX = _bot.X - LocalHalfBox, maxX = _bot.X + LocalHalfBox;
            int minY = _bot.Y - LocalHalfBox, maxY = _bot.Y + LocalHalfBox;

            var start = _bot.Location;
            var open = new PriorityQueue<(int x, int y, int z), double>();
            var g = new Dictionary<(int, int), double>();
            var came = new Dictionary<(int, int), (int, int, int)>();
            g[(start.X, start.Y)] = 0;
            open.Enqueue((start.X, start.Y, start.Z), Cheb(start, target));

            (int, int, int)? found = null;
            int expansions = 0;
            var closed = new HashSet<(int, int)>();
            // A link midpoint can itself be the blocked tile (a door, a
            // post on the border); next to it is close enough.
            int tol = Cheb(start, target) > 2 ? 1 : 0;

            while (open.Count > 0)
            {
                open.TryDequeue(out var cur, out _);
                var ck = (cur.x, cur.y);
                if (!closed.Add(ck))
                {
                    continue;
                }
                if (Math.Max(Math.Abs(cur.x - target.X), Math.Abs(cur.y - target.Y)) <= tol)
                {
                    found = cur;
                    break;
                }
                if (++expansions > LocalMaxExpansions)
                {
                    break;
                }

                double gc = g[ck];
                for (int di = 0; di < 8; di++)
                {
                    var d = (Direction)di;
                    int nx = cur.x, ny = cur.y;
                    CalcMoves.Offset(d, ref nx, ref ny);
                    if (nx < minX || nx > maxX || ny < minY || ny > maxY)
                    {
                        continue;
                    }
                    var nk = (nx, ny);
                    if (closed.Contains(nk) || !Allowed(nx, ny))
                    {
                        continue;
                    }
                    var loc = new Point3D(cur.x, cur.y, cur.z);
                    int nz;
                    bool ok;
                    try
                    {
                        ok = CalcMoves.CheckMovement(_bot, _map, loc, d, out nz);
                    }
                    catch
                    {
                        ok = false;
                        nz = cur.z;
                    }
                    if (!ok)
                    {
                        // A closed door is not a wall: PlayerBot.Move opens it.
                        if (Walkable.ClosedDoorAt(_map, nx, ny, cur.z))
                        {
                            nz = cur.z;
                        }
                        else
                        {
                            continue;
                        }
                    }
                    bool diag = di % 2 == 1;
                    double ng = gc + (diag ? 1.41 : 1.0);
                    if (g.TryGetValue(nk, out var old) && old <= ng)
                    {
                        continue;
                    }
                    g[nk] = ng;
                    came[nk] = (cur.x, cur.y, cur.z);
                    open.Enqueue((nx, ny, nz), ng + Math.Max(Math.Abs(nx - target.X), Math.Abs(ny - target.Y)));
                }
            }

            if (found == null)
            {
                return null;
            }

            var path = new List<Point3D>();
            var (fx, fy, fz) = found.Value;
            var key = (fx, fy);
            path.Add(new Point3D(fx, fy, fz));
            while (came.TryGetValue(key, out var prev))
            {
                if (prev.Item1 == start.X && prev.Item2 == start.Y)
                {
                    break;
                }
                path.Add(new Point3D(prev.Item1, prev.Item2, prev.Item3));
                key = (prev.Item1, prev.Item2);
            }
            path.Reverse();
            return path;
        }
    }
}
