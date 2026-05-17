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
        // Rolled fresh each time a new path is planned.
        private int _finalOffsetX;
        private int _finalOffsetY;

        private PathFollower _follower;
        private bool _running;
        private bool _hasArrived;
        private DateTime _arrivedAt;

        // Stuck detection at the leg level.
        private Point3D _lastLoc;
        private DateTime _lastProgressAt;
        private int _legAttempts;
        private static readonly TimeSpan StuckTimeout = TimeSpan.FromSeconds(15);
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

            // Pick a destination if none was set.
            if (string.IsNullOrEmpty(DestinationName))
            {
                DestinationName = WaypointRegistry.Graph.PickRandomName();
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
                    // Combat ended; resume current leg.
                    StartCurrentLeg(bot, running: false);
                    return;
                }

                if (bot.Hits < bot.HitsMax * RetreatHpFraction)
                {
                    bot.Combatant = null;
                    // Retreat: just resume the path running. The bot was
                    // making progress before combat; keep going (running).
                    StartCurrentLeg(bot, running: true);
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
            if (bot.Location != _lastLoc)
            {
                _lastLoc = bot.Location;
                _lastProgressAt = Core.Now;
                _legAttempts = 0;  // making progress; reset attempts
            }
            else if (Core.Now - _lastProgressAt > StuckTimeout)
            {
                _legAttempts++;
                _lastProgressAt = Core.Now;

                if (_legAttempts >= MaxLegAttempts)
                {
                    // Multiple failed attempts at the current leg. If the
                    // leg target is too far (more than PathFollower can
                    // handle), the bot is functionally stuck — rescue them
                    // directly to the leg target instead of replanning.
                    var legNode = (_plannedPath.Count > 0 && _legIndex < _plannedPath.Count)
                        ? WaypointRegistry.Graph.Get(_plannedPath[_legIndex])
                        : null;
                    if (legNode != null)
                    {
                        int sdx = bot.X - legNode.Location.X;
                        int sdy = bot.Y - legNode.Location.Y;
                        int sDist = (int)Math.Sqrt(sdx * sdx + sdy * sdy);
                        if (sDist > MaxApproachDistance)
                        {
                            Log(bot, $"STUCK on unreachable leg target '{legNode.Name}' ({sDist} tiles) — teleporting there");
                            bot.MoveToWorld(legNode.Location, bot.Map);
                            _lastLoc = bot.Location;
                            _legAttempts = 0;
                            _lastLoggedLeg = null;
                            return;
                        }
                    }

                    Log(bot, $"STUCK after {_legAttempts} attempts — replanning from current location");
                    PlanPath(bot);
                    _legAttempts = 0;
                    _lastLoggedLeg = null;
                }
                else
                {
                    Log(bot, $"stuck on leg, forcing repath (attempt {_legAttempts}/{MaxLegAttempts})");
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

            // -- 6. Make sure we're walking the current leg --
            StartCurrentLeg(bot, running: false);
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

            // If destination is unknown, pick something new.
            if (graph.Get(DestinationName) == null)
            {
                DestinationName = graph.PickRandomName();
            }

            _plannedPath = graph.FindPath(nearest.Name, DestinationName);
            _legIndex = 0;

            // Roll a small offset for the final leg target so multiple
            // bots arriving at the same waypoint don't pile up on the same
            // exact tile. -3..+3 in each axis keeps them within a 7x7
            // visual cluster around the waypoint.
            _finalOffsetX = Utility.RandomMinMax(-3, 3);
            _finalOffsetY = Utility.RandomMinMax(-3, 3);

            // If we're already at the destination node, mark arrived.
            if (_plannedPath.Count == 0 ||
                (_plannedPath.Count == 1 && nearest.Name == DestinationName &&
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
                Log(bot, $"NO PATH from '{nearest.Name}' to '{DestinationName}'");
            }
            else
            {
                Log(bot, $"Plan ({_plannedPath.Count} legs): {string.Join(" -> ", _plannedPath)}");
            }
        }

        // Track the last leg name we logged so we don't spam every tick.
        private string _lastLoggedLeg;

        // -------------------------------------------------------------------
        // StartCurrentLeg — ensure PathFollower is targeted at the current
        // leg's waypoint and the step timer is running.
        // -------------------------------------------------------------------
        private void StartCurrentLeg(PlayerBot bot, bool running)
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

            // If we're already at this waypoint, advance to the next.
            if (bot.InRange(node.Location, LegArrivalRange))
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

            // Log when we actually start a new leg (not every tick).
            if (_lastLoggedLeg != node.Name)
            {
                int dx = bot.X - node.Location.X;
                int dy = bot.Y - node.Location.Y;
                int dist = (int)Math.Sqrt(dx * dx + dy * dy);
                Log(bot, $"Walking leg {_legIndex + 1}/{_plannedPath.Count}: {node.Name} ({dist} tiles away)");
                _lastLoggedLeg = node.Name;
            }

            // Target this leg's waypoint with PathFollower. If this is the
            // FINAL leg, apply the per-bot offset so multiple arrivals at
            // the same waypoint don't stack on the exact same tile.
            Point3D legTarget = node.Location;
            bool isFinalLeg = _legIndex == _plannedPath.Count - 1;
            if (isFinalLeg)
            {
                legTarget = new Point3D(
                    node.Location.X + _finalOffsetX,
                    node.Location.Y + _finalOffsetY,
                    node.Location.Z
                );
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

            string next = DestinationName;
            for (int i = 0; i < 5 && next == DestinationName; i++)
            {
                next = graph.PickRandomName();
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
        private Mobile FindNearbyEnemy(PlayerBot bot)
        {
            Mobile best = null;
            int bestDistSq = int.MaxValue;
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, SightRange))
            {
                if (m == bot || m.Deleted || !m.Alive) continue;
                if (m is not BaseCreature bc) continue;
                if (bc.ControlMaster != null || bc.Summoned) continue;
                if (!bc.AlwaysAttackable && bc.FightMode == FightMode.None) continue;

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
            if (_stepTimer != null && _running == running)
                return;

            StopStepTimer();
            _running = running;
            int delayMs = running ? MoveDelays.RunFootDelay : MoveDelays.WalkFootDelay;
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
