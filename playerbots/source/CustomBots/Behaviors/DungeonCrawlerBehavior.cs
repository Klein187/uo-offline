// =========================================================================
// DungeonCrawlerBehavior.cs — a bot crawling a dungeon.
//
// It subclasses AdventurerBehavior to inherit ALL the working combat and
// navigation (enemy acquisition, melee/ranged/spell combat, flee/retreat,
// stuck recovery, pathfinding). The crawler adds only what's dungeon-
// specific, exactly as the design intends ("build the crawl loop on
// working nav + crude fighting"):
//
//   - Goal selection is scoped to the points on ITS walkable floor
//     (SelectPatrolGoal → DungeonRegistry.RollPoint). Skill-weighted
//     descent makes depth emerge: low-tier bots stay shallow, high-tier
//     bots go deep. The bot is dumb and local; depth is not planned.
//   - Travel BETWEEN points rides the waypoint graph: a rolled target is
//     reached via graph hops, each short enough for PathFollower's A*
//     (38-tile search box). Without this, most points on a floor are
//     simply unreachable and the bot mills around one room forever.
//   - ROOM CLEARING: on reaching a room the crawler lingers a while,
//     fighting whatever's there (combat always preempts patrol). When
//     the linger expires and no more monsters are on it, it moves on —
//     preferring rooms it hasn't cleared yet, so it sweeps the floor.
//   - CAMPERS: a small minority (~15%) settle at the first room they
//     reach and farm the respawns there for their whole run instead of
//     sweeping. Everyone still leaves when the run timer expires.
//   - A Descend/Ascend point sits ON a real in-game Teleporter item,
//     exactly like a dungeon entrance: on reaching one, the crawler walks
//     straight onto the pad and the GAME teleports it (Teleporter.
//     OnMoveOver). The landing spot decides what happened — near a known
//     interior point → same crawler re-scoped to that level; nowhere near
//     one → the surface, revert to a Traveler.
//   - A run timer flips the crawler into EXIT MODE: it then seeks THIS
//     level's up-teleporter, climbs one floor, repeats — a per-level reflex
//     with no whole-journey awareness. The full climb-out emerges from it.
//
// LOOP-BREAKER: a crawler stays a crawler the entire time it's inside,
// including the climb out. It never runs the "entered a dungeon → become a
// crawler" entry handoff (only Travelers do), so walking back through a
// landing/entry area never re-grabs it. De-conversion to Traveler happens
// only on the surface (ResolveLanding, no interior point near the landing).
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public class DungeonCrawlerBehavior : AdventurerBehavior
    {
        public override string SerializableName => "DungeonCrawler";

        // Idle chatter down here is nervier — whispers in the dark, loot
        // squabbles — not bank-plaza LFG spam.
        private static readonly string[] DungeonChat =
            { "dungeon_whispers", "small_talk" };
        protected override string[] AmbientChatCategories => DungeonChat;

        // Which dungeon + floor this crawler is scoped to. Set on entry, and
        // re-derived from position if a server reload wakes a contextless
        // crawler (see TryRecoverContext).
        public string DungeonName { get; set; } = "";
        public int    Level       { get; set; }

        // Exit-mode: seek this level's up-teleporter and climb out.
        public bool ExitMode { get; private set; }

        // Camper: farms the first room it reaches for the whole run
        // instead of sweeping the floor. Rolled once per crawl.
        public bool Camper { get; private set; }
        private bool _camperRolled;
        private const double CamperChance = 0.15;

        // How long the crawl lasts before exit-mode kicks in. Real (wall-
        // clock) minutes — the crawler tick runs on the same clock as the
        // rest of bot life.
        public int RunMinMinutes { get; set; } = 20;
        public int RunMaxMinutes { get; set; } = 45;

        public override string GetStatusLine(PlayerBot bot)
        {
            var where = string.IsNullOrEmpty(DungeonName)
                ? "a dungeon"
                : $"{DungeonName} L{Level}";
            if (bot.Combatant is Mobile foe && !foe.Deleted && foe.Alive)
            {
                return $"fighting {foe.Name} in {where}";
            }
            if (ExitMode)
            {
                return $"climbing out of {where}";
            }
            return Camper
                ? $"camping a room in {where}"
                : $"crawling {where}";
        }

        private DateTime _runExpiresAt = DateTime.MinValue;

        // Exit-mode progress watchdog. If a whole ExitRescueAfter window
        // passes in exit mode WITHOUT a floor transition (no ascend
        // authored/reachable — e.g. an unauthored side area), the bot
        // "finds its own way out": it's moved to its dungeon's surface
        // entrance and resumes traveling. Reset on every floor change so
        // a legitimate multi-floor climb never trips it.
        private DateTime _exitModeSince = DateTime.MinValue;
        private static readonly TimeSpan ExitRescueAfter = TimeSpan.FromMinutes(6);

        // ---- Level transition (walk-onto-a-real-teleporter) ----
        // Mirrors TravelerBehavior's dungeon-entry approach: a fast pad
        // timer drives the bot the last tiles straight onto the exact
        // Descend/Ascend tile (range 0); the game's Teleporter carries it
        // to the other floor. A sudden >PadJumpDistance move away from the
        // pad in one beat is the "it fired" signal.
        //
        // While _transitionPending the decision tick must do nothing — any
        // combat/patrol movement would carry the bot off the pad.
        private bool _transitionPending;
        private Point3D _padTile;
        private DateTime _padStartedAt;
        private PathFollower _padFollower;
        private Timer _padTimer;

        // A single Move is at most one tile; a bigger jump in one beat can
        // only be the teleporter firing.
        private const int PadJumpDistance = 20;
        // Reached the pad but never teleported (missing or inactive
        // Teleporter item) — give up rather than stand on it forever.
        private static readonly TimeSpan PadTimeout = TimeSpan.FromSeconds(20);
        // Pad-walk step cadence — a walking pace, like the entrance approach.
        private static readonly TimeSpan PadStepInterval = TimeSpan.FromMilliseconds(400);

        // The dungeon point currently being roamed to — remembered so
        // OnPatrolGoalReached knows whether the reached goal was a teleporter.
        private BotDestination _targetPoint;

        // Route to _targetPoint as waypoint-graph hops (last entry is the
        // target itself). Each leg is short enough for PathFollower's A*;
        // without the hops most of a floor is out of its 38-tile box.
        private List<Point3D> _route;
        private int _routeIndex;

        // If a fight dragged the bot this far off its next hop, replan the
        // route from where it now stands.
        private const int RouteDriftMax = 35;

        // Room-clearing linger. While set, patrol goals are small shuffles
        // around the room point; combat preempts as usual. A camper's
        // linger never expires (only exit mode ends it).
        private bool _lingering;
        private Point3D _lingerAnchor;
        private DateTime _lingerUntil = DateTime.MinValue;
        private const int LingerMinSeconds = 20;
        private const int LingerMaxSeconds = 45;
        private const int LingerShuffleRadius = 4;

        // Rooms already cleared this run — unvisited rooms are strongly
        // preferred by RollPoint, so the crawl sweeps the floor. Cleared
        // when the bot changes floors.
        private readonly HashSet<string> _visited =
            new(StringComparer.OrdinalIgnoreCase);

        // Log only once when a level has no exit authored, to avoid spamming.
        private bool _warnedNoExit;

        // -------------------------------------------------------------------
        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);

            // Crawlers retreat a little less eagerly than a road defender —
            // they came here to fight. (Base default RetreatHpFraction 0.55
            // is fine; left as-is.)

            if (_runExpiresAt == DateTime.MinValue)
            {
                _runExpiresAt = Core.Now +
                    TimeSpan.FromMinutes(Utility.RandomMinMax(RunMinMinutes, RunMaxMinutes));
            }

            // A minority of crawlers are campers: they settle at the first
            // room they reach and farm its respawns for the whole run —
            // and they came to farm, so their run lasts longer.
            if (!_camperRolled)
            {
                _camperRolled = true;
                Camper = Utility.RandomDouble() < CamperChance;
                if (Camper)
                {
                    _runExpiresAt += TimeSpan.FromMinutes(
                        Utility.RandomMinMax(RunMinMinutes, RunMaxMinutes));
                }
            }
        }

        // -------------------------------------------------------------------
        // Decision tick. Gate on the transition freeze and dungeon context,
        // then defer to the inherited Adventurer tick (combat + patrol). The
        // patrol step calls our SelectPatrolGoal / OnPatrolGoalReached.
        // -------------------------------------------------------------------
        public override void OnDetached(PlayerBot bot)
        {
            StopPadTimer();
            _padFollower = null;
            base.OnDetached(bot);
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot == null || bot.Deleted) return;

            // Mid-transition: walking onto the pad / waiting for it to fire.
            // The pad timer drives the steps; this is just the slow-tick
            // backstop for the jump/timeout checks.
            if (_transitionPending)
            {
                PadCheck(bot);
                return;
            }

            // No dungeon context (e.g. server reloaded and rebuilt us from
            // the saved name alone) — re-derive it from where we're standing.
            if (string.IsNullOrEmpty(DungeonName))
            {
                if (!TryRecoverContext(bot))
                {
                    // No authored points reachable from here. If we're
                    // still physically inside a dungeon (e.g. a teleporter
                    // dropped us on an unauthored floor), STAY a crawler —
                    // swapping to Adventurer just makes the dungeon rule
                    // convert us right back next lifecycle tick. Scope to
                    // the region name and hunt locally; the run timer and
                    // exit rescue still bound the stay.
                    if (DungeonRegistry.IsInDungeon(bot))
                    {
                        var regionName = bot.Region?.Name;
                        DungeonName = string.IsNullOrEmpty(regionName)
                            ? "Unknown Dungeon"
                            : regionName;
                        Level = 0;
                        Console.WriteLine(
                            $"[DungeonCrawler] {bot.Name}: unauthored floor — " +
                            $"hunting locally in {DungeonName}");
                    }
                    else
                    {
                        // Genuinely not in a dungeon — wilderness hunter.
                        bot.Behavior = new AdventurerBehavior();
                        return;
                    }
                }
            }

            // Physically OUTSIDE the dungeon with no transition in flight —
            // an exit teleporter caught us mid-walk (at Despise the entry
            // landing tiles double as the out-pads, so a wrong step ejects
            // the bot without ResolveLanding ever running). The crawl is
            // over; resume traveling from the surface.
            if (!string.IsNullOrEmpty(DungeonName) &&
                !DungeonRegistry.IsInDungeon(bot))
            {
                Console.WriteLine(
                    $"[DungeonCrawler] {bot.Name}: outside {DungeonName} — resuming travel");
                // Walked out alive: that's a survived run. Three of these
                // buy a visible gear upgrade at the next bank visit
                // (IDEAS 4.3).
                bot.DungeonRunsSurvived++;
                bot.Behavior = new TravelerBehavior();
                return;
            }

            // Run timer → exit mode. A crawler in exit mode keeps fighting,
            // but its goal selection now seeks the up-teleporter.
            if (!ExitMode && Core.Now >= _runExpiresAt)
            {
                ExitMode = true;
                _exitModeSince = Core.Now;
                // Drop whatever the crawl was doing — camping, lingering,
                // mid-route — and let goal selection seek the way up.
                _lingering = false;
                _route = null;
                _targetPoint = null;
                Console.WriteLine(
                    $"[DungeonCrawler] {bot.Name}: run over — exiting {DungeonName} from L{Level}");
            }

            // Exit-mode watchdog: a long stretch with no floor transition
            // means there's no reachable way up from here — walk out
            // "off-screen" rather than wander this floor forever.
            if (ExitMode && Core.Now - _exitModeSince > ExitRescueAfter)
            {
                if (RescueToSurface(bot)) return;
                _exitModeSince = Core.Now; // no entrance known — retry later
            }

            base.Tick(bot);
        }

        // -------------------------------------------------------------------
        // Goal selection — the crawl's brain.
        //
        //   1. Lingering at a room → small shuffles around it. A camper
        //      lingers until exit mode; everyone else until the timer runs
        //      out (combat preempts patrol, so a room with monsters gets
        //      cleared regardless — the timer only gates MOVING ON).
        //   2. Mid-route → the next waypoint hop (replanned if a fight
        //      dragged the bot too far off the line).
        //   3. Otherwise roll the next point on this floor (skill-weighted
        //      descent, unvisited rooms preferred; nearest ascend in exit
        //      mode) and plan a graph route to it.
        //
        // Null → base falls back to local wandering (e.g. a floor with no
        // points authored), so the bot is never frozen.
        // -------------------------------------------------------------------
        protected override Point3D? SelectPatrolGoal(PlayerBot bot)
        {
            // -- 1. Room linger --
            if (_lingering)
            {
                if (Camper || Core.Now < _lingerUntil)
                {
                    return new Point3D(
                        _lingerAnchor.X + Utility.RandomMinMax(-LingerShuffleRadius, LingerShuffleRadius),
                        _lingerAnchor.Y + Utility.RandomMinMax(-LingerShuffleRadius, LingerShuffleRadius),
                        _lingerAnchor.Z);
                }
                _lingering = false; // cleared the room — move on
            }

            // -- 2. Route in progress --
            if (_route != null)
            {
                if (_routeIndex >= _route.Count)
                {
                    _route = null;
                }
                else
                {
                    // Combat can carry the bot well off the route. If the
                    // next hop is out of A*'s reach, replan from here.
                    if (Dist(bot.Location, _route[_routeIndex]) > RouteDriftMax &&
                        !PlanRoute(bot, _targetPoint))
                    {
                        _route = null;
                        _targetPoint = null;
                    }
                    if (_route != null)
                    {
                        return _route[_routeIndex];
                    }
                }
            }

            // -- 3. Roll the next point on this floor --
            var p = DungeonRegistry.RollPoint(
                DungeonName, Level, bot.SkillTier, ExitMode, bot.Location, _visited);
            _targetPoint = p;

            if (p == null)
            {
                if (ExitMode && !_warnedNoExit)
                {
                    _warnedNoExit = true;
                    Console.WriteLine(
                        $"[DungeonCrawler] {bot.Name}: no exit point on " +
                        $"{DungeonName} L{Level} — wandering until one is reachable");
                }
                return null;
            }

            if (PlanRoute(bot, p))
            {
                return _route[_routeIndex];
            }

            // No graph coverage here — head straight at it and let A* /
            // stuck-recovery cope (the old behavior, kept as fallback).
            return p.Location;
        }

        // -------------------------------------------------------------------
        // Reached the current goal.
        //   Mid-route hop      → advance; base asks for the next hop.
        //   Level teleporter   → start the pad walk (the game teleports).
        //   Room point         → settle in to clear it: mark visited and
        //                        start the linger (a camper's never ends).
        //   Linger shuffle     → not claimed; base rolls the next shuffle.
        // -------------------------------------------------------------------
        protected override bool OnPatrolGoalReached(PlayerBot bot)
        {
            if (_route != null && _routeIndex < _route.Count - 1)
            {
                _routeIndex++;
                // Densely-placed waypoints can leave the next hop(s) already
                // within arrival range — skip them now rather than burning a
                // 2s decision tick on each.
                while (_routeIndex < _route.Count - 1 &&
                       Dist(bot.Location, _route[_routeIndex]) <= ArrivalRange)
                {
                    _routeIndex++;
                }
                return false;
            }

            var p = _targetPoint;
            _route = null;
            if (p == null) return false;
            _targetPoint = null;

            if (p.Type == DestinationType.DungeonDescend ||
                p.Type == DestinationType.DungeonAscend)
            {
                BeginPadWalk(bot, p.Location);
                return true;
            }

            _visited.Add(p.Name);
            _lingering = true;
            _lingerAnchor = p.Location;
            _lingerUntil = Core.Now +
                TimeSpan.FromSeconds(Utility.RandomMinMax(LingerMinSeconds, LingerMaxSeconds));

            if (Camper)
            {
                Console.WriteLine(
                    $"[DungeonCrawler] {bot.Name}: camping {p.Name} in {DungeonName}");
            }

            return false;
        }

        // -------------------------------------------------------------------
        // Plan a waypoint-graph route from the bot to `target`, storing it
        // in _route (graph hops, then the target itself). Each hop is one
        // graph edge — short enough for PathFollower's 38-tile A* box.
        // False when the graph doesn't cover both ends (unauthored floor,
        // bot far off the graph, or disconnected components).
        // -------------------------------------------------------------------
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

            // Skip leading hops already within arrival range — the first
            // hop is the NEAREST node to the bot, so it's usually satisfied
            // the moment the route is planned. Without the skip, a bot that
            // lands on top of its anchor waypoint starts its route on a
            // no-op hop.
            while (_routeIndex < route.Count - 1 &&
                   Dist(bot.Location, route[_routeIndex]) <= ArrivalRange)
            {
                _routeIndex++;
            }

            return true;
        }

        private static int Dist(Point3D a, Point3D b)
        {
            int dx = a.X - b.X; if (dx < 0) dx = -dx;
            int dy = a.Y - b.Y; if (dy < 0) dy = -dy;
            return dx > dy ? dx : dy;
        }

        // -------------------------------------------------------------------
        // Start the walk onto a Descend/Ascend teleporter pad. Freezes the
        // decision tick and hands the last few tiles to a fast pad timer so
        // the bot steps exactly ONTO the tile (patrol arrival stops 2 short).
        // -------------------------------------------------------------------
        private void BeginPadWalk(PlayerBot bot, Point3D pad)
        {
            _transitionPending = true;
            HaltMovement();

            _padTile      = pad;
            _padStartedAt = Core.Now;
            _padFollower  = new PathFollower(bot, pad);

            StopPadTimer();
            _padTimer = Timer.DelayCall(PadStepInterval, PadStepInterval, () => PadStep(bot));
        }

        // One beat of the pad walk: detect the teleport, otherwise take a
        // step toward the exact pad tile (range 0).
        private void PadStep(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || !bot.Alive ||
                bot.Map == null || bot.Map == Map.Internal)
            {
                StopPadTimer();
                return;
            }

            if (PadCheck(bot)) return;

            _padFollower ??= new PathFollower(bot, _padTile);
            _padFollower.Follow(false, 0);

            // The teleporter fires DURING the move (OnMoveOver) — check
            // again right away so the landing resolves this beat.
            PadCheck(bot);
        }

        // Jump/timeout check, run every pad-timer beat and (as a backstop)
        // every decision tick while a transition is pending. Returns true if
        // it handled the bot (landed or gave up).
        private bool PadCheck(PlayerBot bot)
        {
            int dist = (int)bot.GetDistanceToSqrt(_padTile);

            // Far from the pad in a single beat → the teleporter fired.
            if (dist > PadJumpDistance)
            {
                ResolveLanding(bot);
                return true;
            }

            // On/near the pad but nothing happened — missing or inactive
            // Teleporter item. Resume the crawl rather than loiter forever.
            if (Core.Now - _padStartedAt > PadTimeout)
            {
                Console.WriteLine(
                    $"[DungeonCrawler] {bot.Name}: teleporter at {_padTile} " +
                    $"({DungeonName} L{Level}) never fired — resuming crawl");
                AbortTransition();
                return true;
            }

            return false;
        }

        // -------------------------------------------------------------------
        // The teleporter carried the bot somewhere. Where it landed decides
        // the outcome:
        //   near a known interior point → SAME crawler instance, re-scoped
        //     to that dungeon+level (run timer + exit mode preserved);
        //   nowhere near one → the surface: the crawl is over, revert to a
        //     Traveler (the loop-breaker's only de-conversion point).
        // -------------------------------------------------------------------
        private void ResolveLanding(PlayerBot bot)
        {
            StopPadTimer();
            _padFollower = null;

            var p = DungeonRegistry.NearestPoint(bot.Location, 30);

            // No point within 30 tiles — but interior points reachable ON
            // FOOT from here mean we landed on another dungeon floor (the
            // surface graph is a separate component with no interior
            // points, so this can't false-positive on a real climb-out).
            if (p == null)
            {
                var pool = DungeonRegistry.ReachablePoints(bot.Location);
                if (pool.Count > 0)
                {
                    p = pool[0];
                }
            }

            if (p != null)
            {
                DungeonName = p.Dungeon;
                ResumeAfterTransition(p.Level);
                Console.WriteLine(
                    $"[DungeonCrawler] {bot.Name}: now on L{Level} of {DungeonName}");
            }
            else if (DungeonRegistry.IsInDungeon(bot))
            {
                // Nothing authored near the landing but we're still inside a
                // dungeon region — an unauthored floor, not the surface.
                // Reverting to Traveler here strands it (no waypoints to plan
                // with → MAROONED rescue to a random moongate). Stay a
                // crawler scoped to the region and hunt locally; the run
                // timer and exit rescue still bound the stay.
                var regionName = bot.Region?.Name;
                DungeonName = string.IsNullOrEmpty(regionName)
                    ? "Unknown Dungeon"
                    : regionName;
                ResumeAfterTransition(0);
                Console.WriteLine(
                    $"[DungeonCrawler] {bot.Name}: landed on an unauthored floor — " +
                    $"hunting locally in {DungeonName}");
            }
            else
            {
                bot.Behavior = new TravelerBehavior();
                Console.WriteLine(
                    $"[DungeonCrawler] {bot.Name}: climbed out to the surface");
            }
        }

        // Re-scope the same crawler instance to the floor it landed on —
        // run timer + exit mode preserved.
        private void ResumeAfterTransition(int newLevel)
        {
            Level = newLevel;
            _targetPoint = null;
            _route = null;
            _lingering = false;
            _visited.Clear();       // new floor, fresh sweep
            _warnedNoExit = false;
            _transitionPending = false;
            if (ExitMode)
            {
                _exitModeSince = Core.Now; // made floor progress — fresh window
            }
            HaltMovement(); // start fresh on the new floor next tick
        }

        // -------------------------------------------------------------------
        // Exit-mode dead end: no way up was found for a whole watchdog
        // window. Move the bot to its dungeon's surface entrance (matched
        // by Dungeon tag prefix — "Despise lvl1" finds the "Despise"
        // entrance) and hand it back to a Traveler. Mirrors Traveler's own
        // LOST rescue. Returns false when no entrance matches.
        // -------------------------------------------------------------------
        private bool RescueToSurface(PlayerBot bot)
        {
            BotDestination entrance = null;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.DungeonEntrance) continue;
                if (string.IsNullOrEmpty(d.Dungeon)) continue;
                if (DungeonName.StartsWith(d.Dungeon, StringComparison.OrdinalIgnoreCase))
                {
                    entrance = d;
                    break;
                }
            }

            if (entrance == null) return false;

            Console.WriteLine(
                $"[DungeonCrawler] {bot.Name}: no way up from {DungeonName} " +
                $"L{Level} — emerging at {entrance.Name}");
            HaltMovement();
            bot.MoveToWorld(entrance.Location, bot.Map);
            bot.Behavior = new TravelerBehavior();
            return true;
        }

        // Clear the transition freeze without moving — used when the pad
        // never teleported the bot.
        private void AbortTransition()
        {
            StopPadTimer();
            _padFollower = null;
            _transitionPending = false;
            _targetPoint = null;
            _route = null;
        }

        private void StopPadTimer()
        {
            if (_padTimer != null)
            {
                _padTimer.Stop();
                _padTimer = null;
            }
        }

        // -------------------------------------------------------------------
        // Re-derive dungeon + level from the nearest interior point. This is
        // the proximity form of the design's "entered-area re-scope" — it
        // recovers a crawler that was rebuilt from its saved name alone.
        // -------------------------------------------------------------------
        private bool TryRecoverContext(PlayerBot bot)
        {
            var p = DungeonRegistry.NearestPoint(bot.Location, 30);

            // Nothing close by — adopt any point on this walkable floor
            // (the entry landing can be a fair walk from the first room).
            if (p == null)
            {
                var pool = DungeonRegistry.ReachablePoints(bot.Location);
                if (pool.Count > 0)
                {
                    p = pool[0];
                }
            }

            if (p == null) return false;
            DungeonName = p.Dungeon;
            Level = p.Level;
            Console.WriteLine(
                $"[DungeonCrawler] {bot.Name}: recovered context → {DungeonName} L{Level}");
            return true;
        }
    }
}
