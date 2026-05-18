// =========================================================================
// TravelerBehavior.cs — Bots that traverse Britannia using a waypoint graph.
//
// Architecture:
//   - Bot has a final destination (a node name from the WaypointGraph)
//   - On spawn (or after arrival), bot finds the nearest waypoint to its
//     current location, then runs Dijkstra in the graph to get a path
//     of waypoint names from there to the destination
//   - Bot walks each leg with PathFollower. Each leg is ≤38 tiles so A*
//     succeeds.
//   - On reaching the next waypoint, advances to the leg after it.
//   - Stuck recovery: if a leg fails repeatedly, recompute from current
//     location. If recompute also fails, pick a new destination.
//
// Step timer: same pattern as Adventurer — fires every WalkFootDelay,
// calls PathFollower.Follow() once per fire.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Mobiles;
using MoveDelays = Server.Movement.Movement;

namespace Server.CustomBots
{
    public enum ArrivalStyle
    {
        Linger,
        Wait,
        Wander,
    }

    public class TravelerBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Traveler";

        // ---- Diagnostics ----
        // When true, log state transitions to the server console so you can
        // watch bots' navigation decisions live via tail -f modernuo.log.
        // Toggle with [SetBotVerbose true/false  (see TravelerVerboseCommand).
        public static bool Verbose = true;

        private static void Log(PlayerBot bot, string msg)
        {
            if (!Verbose) return;
            Console.WriteLine($"[Bot {bot.Name}] {msg}");
        }

        // ---- Tunables ----
        public int SightRange { get; set; } = 8;
        public double RetreatHpFraction { get; set; } = 0.30;
        public int LegArrivalRange { get; set; } = 3;

        // If a bot is farther than this from the nearest graph node when
        // planning, we consider them "lost" and teleport-rescue them onto
        // the graph. Larger than PathFollower's 38-tile A* range, with
        // buffer for terrain irregularities.
        public int MaxApproachDistance { get; set; } = 50;

        // ---- State ----
        public string DestinationName { get; set; }
        public ArrivalStyle Arrival { get; set; } = ArrivalStyle.Linger;

        // Planned path through the graph (sequence of node names). Index
        // _legIndex is the current leg target.
        private List<string> _plannedPath = new();
        private int _legIndex = 0;

        // Per-bot offset applied ONLY to the final leg's target. Avoids
        // stacking when multiple bots arrive at the same waypoint — each
        // aims for a slightly different spot within a small radius.
        // Rolled fresh each time a new path is planned. Used only when
        // routing to a bare waypoint (no Destination object).
        private int _finalOffsetX;
        private int _finalOffsetY;

        // When DestinationName resolves to a real BotDestination, this
        // holds the actual final coord (the destination's Location). The
        // final leg of the path retargets to this coord instead of the
        // last waypoint's tile. Null means "use the waypoint coord with
        // the per-bot offset above" (legacy / fallback path).
        private Point3D? _finalCoord;

        private PathFollower _follower;
        private bool _running;
        // Last known mount state — combined with _running to decide if the
        // step timer needs to restart at a different rate.
        private bool _wasMounted;
        private bool _hasArrived;
        private DateTime _arrivedAt;

        // Stuck detection at the leg level.
        private Point3D _lastLoc;
        private DateTime _lastProgressAt;
        private int _legAttempts;
        // Best (smallest) distance to current leg target the bot has achieved.
        // Used as the "made progress" test instead of "any movement" — a bot
        // jiggling around a lightpost moves but doesn't get closer to goal.
        private int _bestDistToLeg = int.MaxValue;
        private static readonly TimeSpan StuckTimeout = TimeSpan.FromSeconds(4);
        private const int MaxLegAttempts = 3;

        private DateTime _pauseUntil = DateTime.MinValue;

        private Timer _stepTimer;

        private static readonly string[] AmbientChat = { "traveling", "small_talk" };
        private static readonly string[] CombatChat  = { "combat_actions" };

        public TravelerBehavior()
        {
            ChatCategories  = AmbientChat;
            ChatChance      = 0.10;
            MinChatCooldown = TimeSpan.FromSeconds(30);
            MaxChatCooldown = TimeSpan.FromSeconds(90);
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);

            // Pick a destination if none was set. Use the class-weighted
            // destination catalog. If catalog is empty (no destinations.json
            // loaded), fall back to a random waypoint name so the bot still
            // does something.
            if (string.IsNullOrEmpty(DestinationName))
            {
                DestinationName = PickNewDestinationName(bot);
            }

            // Roll arrival style: 40% Linger / 40% Wait / 20% Wander.
            double r = Utility.RandomDouble();
            Arrival = r < 0.40 ? ArrivalStyle.Linger
                    : r < 0.80 ? ArrivalStyle.Wait
                    : ArrivalStyle.Wander;

            _lastLoc        = bot.Location;
            _lastProgressAt = Core.Now;

            // Plan the initial path.
            PlanPath(bot);
        }

        // -------------------------------------------------------------------
        // Pick a destination name for this bot, weighted by their class.
        //
        // Prefer DestinationCatalog (real places of interest). If catalog
        // is empty, fall back to a random waypoint name. The fallback
        // also lets older save data continue to work — TravelerBehavior
        // can still route to a bare waypoint if no destinations exist.
        // -------------------------------------------------------------------
        private static string PickNewDestinationName(PlayerBot bot)
        {
            var dest = DestinationCatalog.PickWeighted(bot.Class);
            if (dest != null) return dest.Name;
            return WaypointRegistry.Graph.PickRandomName();
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopStepTimer();
            base.OnDetached(bot);
        }

        // -------------------------------------------------------------------
        // Decision tick
        // -------------------------------------------------------------------
        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal || bot.Deleted)
            {
                StopStepTimer();
                return;
            }

            ChatCategories = bot.Combatant != null ? CombatChat : AmbientChat;
            TrySpeak(bot);

            // -- 1. Combat --
            var combatant = bot.Combatant;
            if (combatant is Mobile foe)
            {
                if (foe.Deleted || !foe.Alive ||
                    foe.Map != bot.Map ||
                    !bot.InRange(foe.Location, SightRange + 4))
                {
                    bot.Combatant = null;
                    // Combat ended; resume current leg with auto walk/run.
                    StartCurrentLeg(bot);
                    return;
                }

                if (bot.Hits < bot.HitsMax * RetreatHpFraction)
                {
                    bot.Combatant = null;
                    // Retreat: force running regardless of leg distance —
                    // we want OUT of this fight ASAP.
                    StartCurrentLeg(bot, forceRunning: true);
                    return;
                }

                if (!bot.InRange(foe.Location, 1))
                {
                    SetGoalToFoe(bot, foe.Location);
                }
                else
                {
                    // Adjacent — combat tick handles swings.
                    StopStepTimer();
                }
                return;
            }

            // -- 2. Watch for enemies --
            var enemy = FindNearbyEnemy(bot);
            if (enemy != null)
            {
                bot.Combatant = enemy;
                SetGoalToFoe(bot, enemy.Location);
                return;
            }

            // -- 3. Arrived? --
            if (_hasArrived)
            {
                HandleArrival(bot);
                return;
            }

            // -- 4. Stuck check --
            //
            // "Made progress" means the bot got CLOSER to its current leg
            // target than it ever has on this leg, not just that it moved.
            // A bot pinned against a lightpost might jiggle around without
            // ever closing the gap.
            //
            // After StuckTimeout (6s) without progress, escalate:
            //   attempt 1: ForceRepath (PathFollower retries A*)
            //   attempt 2: ForceRepath + small random nudge to break the wedge
            //   attempt 3+: teleport to current leg's waypoint (no distance limit)
            int legDist = int.MaxValue;
            Point3D? curLegLoc = null;
            if (_plannedPath.Count > 0 && _legIndex < _plannedPath.Count)
            {
                var legNode = WaypointRegistry.Graph.Get(_plannedPath[_legIndex]);
                if (legNode != null)
                {
                    curLegLoc = legNode.Location;
                    int dxL = bot.X - legNode.Location.X;
                    int dyL = bot.Y - legNode.Location.Y;
                    legDist = (int)Math.Sqrt(dxL * dxL + dyL * dyL);
                }
            }

            if (legDist < _bestDistToLeg)
            {
                _bestDistToLeg = legDist;
                _lastProgressAt = Core.Now;
                _lastLoc = bot.Location;
                _legAttempts = 0;
            }
            else if (Core.Now - _lastProgressAt > StuckTimeout)
            {
                _legAttempts++;
                _lastProgressAt = Core.Now;

                if (_legAttempts >= MaxLegAttempts)
                {
                    // Three+ failed attempts at this leg. Instead of
                    // teleporting (which looks like a "blip" forward),
                    // nudge the bot one tile in a random walkable direction
                    // and re-repath. The new position is usually enough
                    // for A* to find a clean route. If the bot is STILL
                    // stuck after the nudge, the next stuck-check cycle
                    // nudges again, and again — they slowly walk out of
                    // whatever wedge they're in rather than being yanked
                    // across the map.
                    Log(bot, $"STUCK x{_legAttempts} on '{(_plannedPath.Count > 0 ? _plannedPath[_legIndex] : "?")}' " +
                             $"({legDist} tiles) — nudging + repath, resetting counter");
                    NudgeAway(bot);
                    _follower?.ForceRepath();
                    // Reset the attempt counter so subsequent stuck cycles
                    // start the escalation over (each cycle: repath, then
                    // nudge+repath, then nudge again).
                    _legAttempts = 0;
                    _bestDistToLeg = int.MaxValue;
                }
                else if (_legAttempts == 2)
                {
                    // Mid-escalation: force a repath AND nudge the bot one
                    // tile in a random direction. The nudge breaks them out
                    // of a corner/post wedge so the next repath has a
                    // different starting position.
                    Log(bot, $"stuck on leg (attempt {_legAttempts}/{MaxLegAttempts}) — nudge + repath");
                    NudgeAway(bot);
                    _follower?.ForceRepath();
                }
                else
                {
                    // First failure: just force repath. PathFollower's A*
                    // may simply have stale cached info; recomputing often
                    // shakes loose terrain that looked unreachable.
                    Log(bot, $"stuck on leg (attempt {_legAttempts}/{MaxLegAttempts}) — repath");
                    _follower?.ForceRepath();
                }
            }

            // -- 5. Pause occasionally --
            if (Core.Now < _pauseUntil)
            {
                StopStepTimer();
                return;
            }
            if (Utility.RandomDouble() < 0.03)
            {
                _pauseUntil = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(2, 4));
                StopStepTimer();
                return;
            }

            // -- 6. Make sure we're walking/running the current leg.
            //       StartCurrentLeg auto-picks based on leg distance.
            StartCurrentLeg(bot);
        }

        // -------------------------------------------------------------------
        // PlanPath — find nearest graph node from current position, then
        // Dijkstra to the destination. Stores the result in _plannedPath.
        // -------------------------------------------------------------------
        private void PlanPath(PlayerBot bot)
        {
            var graph = WaypointRegistry.Graph;
            if (graph.NodeCount == 0)
            {
                // No graph data — nothing to do. Mark arrived so the bot
                // doesn't get stuck in a non-functioning state.
                _hasArrived = true;
                _arrivedAt  = Core.Now;
                _plannedPath = new List<string>();
                StopStepTimer();
                return;
            }

            var nearest = graph.FindNearestNode(bot.Location);
            if (nearest == null || string.IsNullOrEmpty(DestinationName))
            {
                _hasArrived = true;
                _arrivedAt  = Core.Now;
                _plannedPath = new List<string>();
                StopStepTimer();
                return;
            }

            // Rescue if too far from the nearest graph node — PathFollower's
            // A* has a 38-tile search radius, so anything beyond that is
            // unreachable. The bot is "lost"; teleport them onto the graph
            // at the nearest waypoint and continue planning from there.
            int rdx = bot.X - nearest.Location.X;
            int rdy = bot.Y - nearest.Location.Y;
            int rDist = (int)Math.Sqrt(rdx * rdx + rdy * rdy);
            if (rDist > MaxApproachDistance)
            {
                Log(bot, $"LOST — {rDist} tiles from nearest waypoint '{nearest.Name}'; teleporting to rescue");
                bot.MoveToWorld(nearest.Location, bot.Map);
                _lastLoc = bot.Location;
                _lastProgressAt = Core.Now;
                _lastLoggedLeg = null;
                // 'nearest' is now also our position. Fall through and
                // recompute the path from this point.
            }

            // Resolve DestinationName. It might be either:
            //   - A real destination (from DestinationCatalog)
            //   - A bare waypoint name (legacy / fallback)
            //
            // Real destination wins: we route to its NearestWaypoint and
            // remember the destination's actual coord as the final stop.
            // Otherwise treat DestinationName as a waypoint name directly.
            string routeTargetWaypoint = DestinationName;
            _finalCoord = null;

            var destObj = DestinationCatalog.GetByName(DestinationName);
            if (destObj != null)
            {
                routeTargetWaypoint = destObj.NearestWaypoint;
                _finalCoord = destObj.Location;
            }
            else if (graph.Get(DestinationName) == null)
            {
                // Neither a known destination nor a known waypoint. Re-roll.
                DestinationName = PickNewDestinationName(bot);
                destObj = DestinationCatalog.GetByName(DestinationName);
                if (destObj != null)
                {
                    routeTargetWaypoint = destObj.NearestWaypoint;
                    _finalCoord = destObj.Location;
                }
                else
                {
                    routeTargetWaypoint = DestinationName;
                }
            }

            // Final safety — if the route target waypoint doesn't exist
            // in the graph, pick a random one we know.
            if (graph.Get(routeTargetWaypoint) == null)
            {
                routeTargetWaypoint = graph.PickRandomName();
            }

            _plannedPath = graph.FindPath(nearest.Name, routeTargetWaypoint);
            _legIndex = 0;
            // Fresh path, fresh best-distance tracker.
            _bestDistToLeg = int.MaxValue;

            // Roll a small offset for the final leg target so multiple
            // bots arriving at the same waypoint/destination don't pile
            // up on the same exact tile. -5..+5 in each axis keeps them
            // within an 11x11 cluster around the destination — enough
            // spread to visually distinguish individual bots while keeping
            // them recognizably "at the same place".
            _finalOffsetX = Utility.RandomMinMax(-5, 5);
            _finalOffsetY = Utility.RandomMinMax(-5, 5);

            // If we're already at the route's target node, mark arrived.
            if (_plannedPath.Count == 0 ||
                (_plannedPath.Count == 1 && nearest.Name == routeTargetWaypoint &&
                 bot.InRange(nearest.Location, LegArrivalRange)))
            {
                _hasArrived = true;
                _arrivedAt  = Core.Now;
                StopStepTimer();
                Log(bot, $"Already at destination '{DestinationName}'");
                return;
            }

            if (_plannedPath.Count == 0)
            {
                Log(bot, $"NO PATH from '{nearest.Name}' to '{routeTargetWaypoint}' (destination '{DestinationName}')");
            }
            else
            {
                Log(bot, $"Plan ({_plannedPath.Count} legs): {string.Join(" -> ", _plannedPath)}");
            }
        }

        // Track the last leg name we logged so we don't spam every tick.
        private string _lastLoggedLeg;

        // Long legs (> this many tiles) trigger running automatically.
        // Short hops between adjacent waypoints walk for a more natural
        // look. Retreating from combat always overrides to running.
        private const int RunThresholdTiles = 25;

        // -------------------------------------------------------------------
        // StartCurrentLeg — ensure PathFollower is targeted at the current
        // leg's waypoint and the step timer is running. Auto-picks walk vs
        // run based on the leg distance (longer legs run). Pass
        // forceRunning=true to override (used by combat retreat).
        // -------------------------------------------------------------------
        private void StartCurrentLeg(PlayerBot bot, bool forceRunning = false)
        {
            if (_plannedPath.Count == 0 || _legIndex >= _plannedPath.Count)
            {
                _hasArrived = true;
                _arrivedAt  = Core.Now;
                StopStepTimer();
                return;
            }

            var graph = WaypointRegistry.Graph;
            var node = graph.Get(_plannedPath[_legIndex]);
            if (node == null)
            {
                // Graph mutated — replan.
                PlanPath(bot);
                return;
            }

            // If we're already at this leg's target, advance to the next.
            // For the FINAL leg with a real destination, the target is the
            // destination coord PLUS the per-bot offset (so bots spread
            // across a few tiles around the destination). Arrival is
            // checked against the same offset point so bots don't endlessly
            // try to converge on the exact tile.
            bool isFinalLegCheck = _legIndex == _plannedPath.Count - 1;
            Point3D arrivalCheckLoc;
            if (isFinalLegCheck && _finalCoord.HasValue)
            {
                arrivalCheckLoc = new Point3D(
                    _finalCoord.Value.X + _finalOffsetX,
                    _finalCoord.Value.Y + _finalOffsetY,
                    _finalCoord.Value.Z
                );
            }
            else
            {
                arrivalCheckLoc = node.Location;
            }

            if (bot.InRange(arrivalCheckLoc, LegArrivalRange))
            {
                Log(bot, $"Reached leg {_legIndex + 1}/{_plannedPath.Count}: {node.Name}");
                _legIndex++;
                if (_legIndex >= _plannedPath.Count)
                {
                    _hasArrived = true;
                    _arrivedAt  = Core.Now;
                    StopStepTimer();
                    Log(bot, $"ARRIVED at destination '{DestinationName}'");
                    return;
                }
                node = graph.Get(_plannedPath[_legIndex]);
                if (node == null) { PlanPath(bot); return; }
            }

            // Compute leg distance — used for both the log message and the
            // walk/run decision.
            int dx = bot.X - node.Location.X;
            int dy = bot.Y - node.Location.Y;
            int dist = (int)Math.Sqrt(dx * dx + dy * dy);

            // Run when: forced (e.g. combat retreat), or the leg is long
            // enough that walking would feel tedious. Short hops keep
            // walking for a more natural look.
            bool running = forceRunning || dist > RunThresholdTiles;

            // Log when we actually start a new leg (not every tick).
            if (_lastLoggedLeg != node.Name)
            {
                string mode = running ? "Running" : "Walking";
                Log(bot, $"{mode} leg {_legIndex + 1}/{_plannedPath.Count}: {node.Name} ({dist} tiles away)");
                _lastLoggedLeg = node.Name;
                // Reset best-distance tracker for the new leg.
                _bestDistToLeg = int.MaxValue;
            }

            // Target this leg's waypoint with PathFollower. If this is the
            // FINAL leg, two cases:
            //   - We have a real destination (_finalCoord set): use the
            //     destination's actual coord PLUS the per-bot random offset
            //     so multiple bots arriving at the same destination spread
            //     across a few tiles instead of piling on the exact same
            //     spot.
            //   - No destination (bare waypoint route): apply the per-bot
            //     random offset so multiple arrivals don't stack.
            Point3D legTarget = node.Location;
            bool isFinalLeg = _legIndex == _plannedPath.Count - 1;
            if (isFinalLeg)
            {
                if (_finalCoord.HasValue)
                {
                    legTarget = new Point3D(
                        _finalCoord.Value.X + _finalOffsetX,
                        _finalCoord.Value.Y + _finalOffsetY,
                        _finalCoord.Value.Z
                    );
                }
                else
                {
                    legTarget = new Point3D(
                        node.Location.X + _finalOffsetX,
                        node.Location.Y + _finalOffsetY,
                        node.Location.Z
                    );
                }
            }
            _follower = new PathFollower(bot, legTarget);
            EnsureStepTimer(bot, running);
        }

        private void SetGoalToFoe(PlayerBot bot, Point3D loc)
        {
            _follower = new PathFollower(bot, loc);
            EnsureStepTimer(bot, running: true);
        }

        // -------------------------------------------------------------------
        // Arrival
        // -------------------------------------------------------------------
        private void HandleArrival(PlayerBot bot)
        {
            switch (Arrival)
            {
                case ArrivalStyle.Linger:
                    var linger = TimeSpan.FromSeconds(Utility.RandomMinMax(60, 120));
                    if (Core.Now - _arrivedAt > linger)
                    {
                        PickNewDestination(bot);
                    }
                    else
                    {
                        StopStepTimer();
                    }
                    break;

                case ArrivalStyle.Wait:
                    StopStepTimer();
                    break;

                case ArrivalStyle.Wander:
                    PickNewDestination(bot);
                    break;
            }
        }

        private void PickNewDestination(PlayerBot bot)
        {
            var graph = WaypointRegistry.Graph;
            if (graph.NodeCount == 0) return;

            // Prefer DestinationCatalog (class-weighted). Try a few times
            // to avoid picking the same destination we just left. If
            // catalog is empty, fall back to a random waypoint.
            string next = DestinationName;
            for (int i = 0; i < 5 && next == DestinationName; i++)
            {
                next = PickNewDestinationName(bot);
            }

            Log(bot, $"Picking new destination: '{next}' (was '{DestinationName}')");
            DestinationName = next;
            _hasArrived = false;
            _lastLoggedLeg = null;
            PlanPath(bot);
        }

        // -------------------------------------------------------------------
        // Enemy detection (same as Adventurer).
        // -------------------------------------------------------------------
        // -------------------------------------------------------------------
        // FindNearbyEnemy
        //
        // Only attack ACTUAL hostile monsters. The naive filter
        // (AlwaysAttackable OR FightMode != None) included wildlife (rabbits,
        // kingfishers, deer) and town NPCs. Wildlife defends itself if
        // attacked (FightMode.Aggressor), so they passed the filter — and
        // bots would attack them. Birds and similar inside buildings
        // (visible but unreachable) caused bots to get wedged against walls
        // trying to engage.
        //
        // Correct filter:
        //   - Skip controlled pets and summons.
        //   - Skip anything with Karma >= 0. Real monsters are deeply
        //     negative (-1000 to -10000); wildlife is 0 or slightly
        //     positive; town NPCs are positive.
        //   - Require FightMode != None as a final sanity check.
        //
        // Note: bots WILL fight monsters in guarded zones if zombies or
        // similar invade a town. The Karma filter alone handles this —
        // guards/townsfolk are Karma >= 0 so bots ignore them; invading
        // monsters are Karma < 0 so bots engage. This makes the "town
        // gets invaded by zombies" event work naturally.
        // -------------------------------------------------------------------
        // -------------------------------------------------------------------
        // NudgeAway
        //
        // Try to move the bot 2-3 tiles away from where they're stuck.
        // Picks a random direction; if blocked, tries the next. Continues
        // moving in the SAME direction once one works (so we get a clean
        // multi-tile shift rather than a zigzag dance). If a chosen
        // direction stops working partway, falls back to any walkable
        // direction for the remaining steps.
        // -------------------------------------------------------------------
        private void NudgeAway(PlayerBot bot)
        {
            const int NudgeTiles = 3;

            var dirs = new Direction[]
            {
                Direction.North, Direction.East, Direction.South, Direction.West,
                Direction.Up,    Direction.Down, Direction.Left,  Direction.Right,
            };
            // Shuffle.
            for (int i = dirs.Length - 1; i > 0; i--)
            {
                int j = Utility.Random(i + 1);
                (dirs[i], dirs[j]) = (dirs[j], dirs[i]);
            }

            // Find a direction that works for the first step.
            Direction? lockedDir = null;
            foreach (var d in dirs)
            {
                if (bot.Move(d)) { lockedDir = d; break; }
            }
            if (lockedDir == null) return; // hopelessly walled in this tick

            // Continue in the locked direction for additional steps; if a
            // step fails, try any walkable direction for the rest.
            int stepsLeft = NudgeTiles - 1;
            while (stepsLeft-- > 0)
            {
                if (bot.Move(lockedDir.Value)) continue;
                // Locked direction now blocked — try any.
                bool moved = false;
                foreach (var d in dirs)
                {
                    if (d == lockedDir.Value) continue;
                    if (bot.Move(d)) { moved = true; break; }
                }
                if (!moved) return;
            }
        }

        private Mobile FindNearbyEnemy(PlayerBot bot)
        {
            Mobile best = null;
            int bestDistSq = int.MaxValue;

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, SightRange))
            {
                if (m == bot || m.Deleted || !m.Alive) continue;
                if (m is not BaseCreature bc) continue;

                // Skip players' pets and summoned creatures.
                if (bc.ControlMaster != null || bc.Summoned) continue;

                // Skip anything that isn't actually hostile.
                if (bc.FightMode == FightMode.None) continue;

                // Karma test: real monsters are deeply negative. Wildlife
                // and NPCs are at 0 or positive. This is the key filter
                // that keeps bots from chasing rabbits or attacking guards.
                if (bc.Karma >= 0) continue;

                int dx = bc.X - bot.X;
                int dy = bc.Y - bot.Y;
                int distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = bc;
                }
            }
            return best;
        }

        // -------------------------------------------------------------------
        // Step timer
        // -------------------------------------------------------------------
        private void EnsureStepTimer(PlayerBot bot, bool running)
        {
            bool mounted = bot.Mounted;
            if (_stepTimer != null && _running == running && _wasMounted == mounted)
                return;

            StopStepTimer();
            _running = running;
            _wasMounted = mounted;

            // ModernUO ships separate delays for foot and mount movement.
            // Mount delays are roughly half of foot delays — a mounted bot
            // covers ground at the usual UO mounted speed.
            int delayMs;
            if (mounted)
            {
                delayMs = running ? MoveDelays.RunMountDelay : MoveDelays.WalkMountDelay;
            }
            else
            {
                delayMs = running ? MoveDelays.RunFootDelay : MoveDelays.WalkFootDelay;
            }

            var interval = TimeSpan.FromMilliseconds(delayMs);
            _stepTimer = Timer.DelayCall(interval, interval, () => StepOnce(bot));
        }

        private void StopStepTimer()
        {
            if (_stepTimer != null)
            {
                _stepTimer.Stop();
                _stepTimer = null;
            }
        }

        private void StepOnce(PlayerBot bot)
        {
            if (bot.Deleted || !bot.Alive || bot.Map == null || bot.Map == Map.Internal)
            {
                StopStepTimer();
                return;
            }

            if (_follower == null)
            {
                StopStepTimer();
                return;
            }

            // PathFollower's Follow returns true on arrival within the range.
            // When it does, we just stop the step timer. The decision tick's
            // StartCurrentLeg sees the bot in range of the current leg's
            // waypoint and advances _legIndex from there. ONE source of
            // truth for the advance — duplicating it here caused index
            // to overrun when StepOnce kept firing during the gap before
            // the next decision tick.
            bool arrivedLeg = _follower.Follow(_running, LegArrivalRange);
            if (arrivedLeg)
            {
                StopStepTimer();
            }
        }
    }
}
