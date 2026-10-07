// =========================================================================
// GhostExitBehavior.cs — a ghost climbing out of a dungeon.
//
// Dungeon deaths used to end with "a wandering healer found them" fired
// on a timer, five floors down, with no healer anywhere. That is where
// nearly every "the bot just randomly came back to life" came from.
//
// A dungeon has no answer of its own: there are no healer NPCs down
// there, and exactly one resurrection ankh in all twelve Felucca dungeons
// (Destard's). So the ghost does what a player did — it walks OUT. One
// floor at a time, the same dumb local reflex the crawler's exit mode
// uses: find THIS floor's up-stair, ride it, look around, repeat. The
// climb-out is emergent, not planned.
//
// Above ground it hands off to an ordinary GhostBehavior, which walks to
// a real ankh or a real healer.
//
// It subclasses GhostBehavior for the three checks every ghost wants
// running the whole way: someone alive raised us, we're standing at a
// resurrection site (Destard's ankh counts, and so does a bot with
// Resurrection who came down the stairs behind us), or we've been dead
// long enough that the stranding net has to fire.
//
// What it does NOT inherit is fighting. Monsters ignore a ghost, so the
// climb is a walk, not a second crawl.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public class GhostExitBehavior : GhostBehavior
    {
        public override string SerializableName => "GhostExit";

        public override string GetStatusLine(PlayerBot bot) =>
            string.IsNullOrEmpty(_dungeonName)
                ? "dead — a ghost looking for the stairs"
                : $"dead — climbing out of {_dungeonName} L{_level}";

        // Which floor we're scoped to. Re-derived from position after every
        // transition, exactly like the crawler does it.
        private string _dungeonName = "";
        private int _level;

        // ---- Routing ----
        private const int ArrivalRange  = 2;
        private const int RouteDriftMax = 35;

        private List<Point3D> _route;
        private int _routeIndex;
        private BotDestination _targetPoint;

        // ---- Stair pad ----
        // A Descend/Ascend point sits ON a real Teleporter item. The ghost
        // walks onto the exact tile (range 0) and the GAME moves it; a
        // sudden big jump is the "it fired" signal. Same machinery as the
        // crawler's pad walk, minus the combat freeze it needed.
        private bool _padPending;
        private bool _padStepped;
        private Point3D _padTile;
        private DateTime _padStartedAt;
        private static readonly TimeSpan PadTimeout = TimeSpan.FromSeconds(20);
        private const int PadJumpDistance = 20;

        // A move is one tile; anything bigger with no pad walk in flight is
        // a teleporter the ghost drifted onto by accident.
        private const int AccidentalJump = 20;
        private Point3D _lastTickLoc;
        private Map _lastTickMap;

        // ---- Watchdogs ----
        // No floor gained in this long means there is no reachable way up
        // from here (an unauthored side area, a stair the ghost can't
        // path to). Walk out "off-screen" to the dungeon's surface
        // entrance rather than drift down here until the stranding net.
        private static readonly TimeSpan NoProgressLimit = TimeSpan.FromMinutes(5);
        private DateTime _progressAt = DateTime.MinValue;

        // Revolving door: landing on the floor we were on two transitions
        // ago. Some dungeons' stairs are mislabeled, so the pad we take up
        // can drop us back down; two bounces means stop trying.
        private string _floorPrev;
        private string _floorPrev2;
        private int _bounces;

        private bool _warnedNoExit;

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            _progressAt  = Core.Now;
            _lastTickLoc = bot.Location;
            _lastTickMap = bot.Map;
            TryRecoverContext(bot);
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot == null || bot.Deleted ||
                bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            // The three things that end any ghost's story wherever it is.
            if (CheckRevived(bot)) return;
            if (CheckResSite(bot)) return;
            if (CheckStranded(bot)) return;

            // Mid-transition: standing on the pad waiting for it to fire.
            if (_padPending)
            {
                PadCheck(bot);
                return;
            }

            // Drifted onto a stair tile without meaning to.
            var prevLoc = _lastTickLoc;
            var prevMap = _lastTickMap;
            _lastTickLoc = bot.Location;
            _lastTickMap = bot.Map;
            if (prevMap == bot.Map && prevMap != null &&
                Dist(prevLoc, bot.Location) > AccidentalJump)
            {
                ResolveLanding(bot);
                return;
            }

            // Out. Hand back to an ordinary ghost, which will walk to a
            // real ankh or healer from here.
            if (!DungeonRegistry.IsInDungeon(bot))
            {
                StopWalk();
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost climbed out of " +
                    $"{(string.IsNullOrEmpty(_dungeonName) ? "the dungeon" : _dungeonName)}");
                bot.Behavior = new GhostBehavior { SkipHaunt = true };
                return;
            }

            // A mage or a bandage-carrier down here with us.
            if (CheckAid(bot)) return;

            TrySpeak(bot);

            if (string.IsNullOrEmpty(_dungeonName) && !TryRecoverContext(bot))
            {
                // No authored points reachable. Scope to the region name so
                // the watchdog below can still walk us out by entrance.
                var regionName = bot.Region?.Name;
                _dungeonName = string.IsNullOrEmpty(regionName)
                    ? "Unknown Dungeon"
                    : regionName;
                _level = 0;
            }

            // No way up from here for a whole window — take the entrance.
            if (Core.Now - _progressAt > NoProgressLimit)
            {
                if (EmergeAtEntrance(bot, "no reachable way up")) return;
                _progressAt = Core.Now; // nothing to emerge to; try again later
            }

            RunRoute(bot);
        }

        // -------------------------------------------------------------------
        // Walk the current route, or roll a new one. The route is graph
        // hops (each short enough for A*) ending at this floor's nearest
        // up-stair.
        // -------------------------------------------------------------------
        private void RunRoute(PlayerBot bot)
        {
            // The hop is unreachable (a door the graph thinks is open, a
            // ledge). Throw the plan away and roll again — the watchdog
            // above is what stops this becoming a pace.
            if (WalkStalled)
            {
                _route = null;
                _targetPoint = null;
                StopWalk();
            }

            // Mid-route: advance when the current hop is reached.
            if (_route != null)
            {
                if (_routeIndex >= _route.Count)
                {
                    _route = null;
                }
                else if (WalkArrived || Dist(bot.Location, _route[_routeIndex]) <= ArrivalRange)
                {
                    _routeIndex++;
                    while (_routeIndex < _route.Count &&
                           Dist(bot.Location, _route[_routeIndex]) <= ArrivalRange)
                    {
                        _routeIndex++;
                    }

                    if (_routeIndex >= _route.Count)
                    {
                        // End of the line: the stair pad itself.
                        _route = null;
                        var reached = _targetPoint;
                        _targetPoint = null;
                        if (reached != null)
                        {
                            BeginPadWalk(bot, reached.Location);
                            return;
                        }
                    }
                }

                if (_route != null)
                {
                    // A hop that drifted out of A*'s reach means the plan
                    // is stale — drop it and roll again.
                    if (Dist(bot.Location, _route[_routeIndex]) > RouteDriftMax)
                    {
                        _route = null;
                        _targetPoint = null;
                    }
                    else
                    {
                        BeginWalk(bot, _route[_routeIndex], ArrivalRange);
                        return;
                    }
                }
            }

            // Roll this floor's nearest way up. Exit mode also accepts a
            // Descend-labeled pad when no Ascend is authored — several
            // dungeons have their up-stairs recorded the wrong way round,
            // and the landing decides what the pad really was.
            var p = DungeonRegistry.RollPoint(
                _dungeonName, _level, bot.SkillTier, exitMode: true, bot.Location);

            if (p == null)
            {
                if (!_warnedNoExit)
                {
                    _warnedNoExit = true;
                    Console.WriteLine(
                        $"[death] {bot.Name}'s ghost finds no way up from " +
                        $"{_dungeonName} L{_level} — drifting until one is reachable");
                }
                StopWalk();
                return;
            }

            _targetPoint = p;
            if (PlanRoute(bot, p))
            {
                BeginWalk(bot, _route[_routeIndex], ArrivalRange);
                return;
            }

            // No graph coverage — head straight at it and let A* cope.
            _route = null;
            BeginWalk(bot, p.Location, ArrivalRange);
        }

        private bool PlanRoute(PlayerBot bot, BotDestination target)
        {
            _route = null;
            _routeIndex = 0;
            if (target == null) return false;

            var graph = WaypointRegistry.Graph;
            if (graph == null || graph.NodeCount == 0) return false;

            var start = graph.FindNearestNode(bot.Location);
            var end   = graph.FindNearestNode(target.Location);
            if (start == null || end == null) return false;
            if (Dist(bot.Location, start.Location) > RouteDriftMax) return false;
            if (Dist(target.Location, end.Location) > RouteDriftMax) return false;

            var names = graph.FindPath(start.Name, end.Name);
            if (names == null || names.Count == 0) return false;

            var route = new List<Point3D>(names.Count + 1);
            foreach (var name in names)
            {
                var node = graph.Get(name);
                if (node != null)
                {
                    route.Add(node.Location);
                }
            }
            route.Add(target.Location);

            _route = route;
            _routeIndex = 0;
            while (_routeIndex < route.Count - 1 &&
                   Dist(bot.Location, route[_routeIndex]) <= ArrivalRange)
            {
                _routeIndex++;
            }
            return true;
        }

        // -------------------------------------------------------------------
        // Stair pad: step onto the exact tile and let the game's Teleporter
        // do the rest. Teleporters don't care whether you're breathing.
        // -------------------------------------------------------------------
        private void BeginPadWalk(PlayerBot bot, Point3D pad)
        {
            _padPending   = true;
            _padStepped   = false;
            _padTile      = pad;
            _padStartedAt = Core.Now;
            BeginWalk(bot, pad, 0);
        }

        private void PadCheck(PlayerBot bot)
        {
            int dist = Dist(bot.Location, _padTile);

            // A whole floor away in one beat — the teleporter fired.
            if (dist > PadJumpDistance)
            {
                ResolveLanding(bot);
                return;
            }

            // Standing ON the stair already (died on the landing tile, or the
            // walk ended on it) fires nothing: a teleporter only works when
            // something steps onto it. Step off and back on, once.
            if (!_padStepped && bot.X == _padTile.X && bot.Y == _padTile.Y &&
                Core.Now - _padStartedAt > TimeSpan.FromSeconds(3))
            {
                _padStepped = true;
                foreach (Direction d in new[] { Direction.North, Direction.East, Direction.South, Direction.West })
                {
                    if (bot.Move(d))
                    {
                        Console.WriteLine($"[death] {bot.Name}'s ghost was standing on the stair — stepping off and back on");
                        _padStartedAt = Core.Now;
                        BeginWalk(bot, _padTile, 0);
                        return;
                    }
                }
            }

            if (Core.Now - _padStartedAt > PadTimeout)
            {
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost stood on the stair at {_padTile} " +
                    $"({_dungeonName} L{_level}) and nothing happened — trying elsewhere");
                StuckTelemetry.Record(bot, "ghost_pad_timeout",
                    $"{_dungeonName} L{_level} pad {_padTile}");
                AbortPad();
            }
        }

        private void AbortPad()
        {
            _padPending = false;
            _targetPoint = null;
            _route = null;
            StopWalk();
        }

        // -------------------------------------------------------------------
        // The stair carried us somewhere. Re-scope to the floor we landed
        // on; if there's no dungeon here at all, the next tick sees the
        // surface and hands off.
        // -------------------------------------------------------------------
        private void ResolveLanding(PlayerBot bot)
        {
            _padPending = false;
            _route = null;
            _targetPoint = null;
            StopWalk();

            var p = DungeonRegistry.NearestPointOnFloor(bot.Location, 30)
                    ?? DungeonRegistry.NearestPoint(bot.Location, 30);

            if (p == null)
            {
                var pool = DungeonRegistry.ReachablePoints(bot.Location);
                if (pool.Count > 0)
                {
                    p = pool[0];
                }
            }

            if (p == null)
            {
                // Either the surface (handled next tick) or an unauthored
                // floor. Scope to the region and keep the watchdog armed.
                if (DungeonRegistry.IsInDungeon(bot))
                {
                    var regionName = bot.Region?.Name;
                    _dungeonName = string.IsNullOrEmpty(regionName)
                        ? "Unknown Dungeon"
                        : regionName;
                    _level = 0;
                    _warnedNoExit = false;
                }
                return;
            }

            string from = $"{_dungeonName}|{_level}";
            string to   = $"{p.Dungeon}|{p.Level}";

            bool bounce = string.Equals(to, _floorPrev2, StringComparison.OrdinalIgnoreCase);
            _bounces = bounce ? _bounces + 1 : 0;
            _floorPrev2 = from;
            _floorPrev = to;

            _dungeonName = p.Dungeon;
            _level = p.Level;
            _warnedNoExit = false;

            // Only real progress refreshes the watchdog — a ping-pong pair
            // of stairs used to reset it forever and starve the rescue.
            if (!bounce)
            {
                _progressAt = Core.Now;
            }

            Console.WriteLine(
                $"[death] {bot.Name}'s ghost is now on L{_level} of {_dungeonName}" +
                (bounce ? $" (bounce {_bounces})" : ""));

            if (_bounces >= 2)
            {
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost is going in circles on the way out of " +
                    $"{_dungeonName}");
                _bounces = 0;
                EmergeAtEntrance(bot, "revolving door");
            }
        }

        // -------------------------------------------------------------------
        // Dead end. Put the ghost at its dungeon's surface entrance and let
        // the ordinary ghost flow take it from there. This is the same
        // "walk out off-screen" the crawler uses when it can't find stairs,
        // and it still ends at a real ankh — it only skips the stairs.
        // -------------------------------------------------------------------
        private bool EmergeAtEntrance(PlayerBot bot, string why)
        {
            BotDestination entrance = null;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.DungeonEntrance) continue;
                if (string.IsNullOrEmpty(d.Dungeon)) continue;
                if (!string.IsNullOrEmpty(_dungeonName) &&
                    _dungeonName.StartsWith(d.Dungeon, StringComparison.OrdinalIgnoreCase))
                {
                    entrance = d;
                    break;
                }
            }

            if (entrance == null)
            {
                return false;
            }

            Console.WriteLine(
                $"[death] {bot.Name}'s ghost gave up on the stairs of {_dungeonName} " +
                $"L{_level} ({why}) — surfacing at {entrance.Name}");
            StuckTelemetry.Record(bot, "ghost_exit_rescue",
                $"{why} — {_dungeonName} L{_level} -> {entrance.Name}");

            StopWalk();
            bot.MoveToWorld(entrance.ArrivalPoint ?? entrance.Location, bot.Map);
            bot.Behavior = new GhostBehavior { SkipHaunt = true };
            return true;
        }

        // -------------------------------------------------------------------
        // Which floor are we standing on? Proximity to the nearest authored
        // interior point, same as the crawler's context recovery.
        // -------------------------------------------------------------------
        private bool TryRecoverContext(PlayerBot bot)
        {
            var p = DungeonRegistry.NearestPointOnFloor(bot.Location, 30)
                    ?? DungeonRegistry.NearestPoint(bot.Location, 30);

            if (p == null)
            {
                var pool = DungeonRegistry.ReachablePoints(bot.Location);
                if (pool.Count > 0)
                {
                    p = pool[0];
                }
            }

            if (p == null) return false;

            _dungeonName = p.Dungeon;
            _level = p.Level;
            return true;
        }
    }
}
