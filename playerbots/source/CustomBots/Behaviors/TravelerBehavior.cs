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

        // Final-leg arrival is more forgiving than mid-route legs. The
        // destination tile may sit inside a building (Stratics-scraped
        // destinations often do); a bot routing from the street can't
        // reach the inner tile and would grind the outer wall until
        // v66's give-up abandoned the trip. 8 tiles is "on the building's
        // doorstep" — close enough that the visit behavior (Shopper /
        // BankSitter) can take over and drift inside cosmetically.
        public int FinalLegArrivalRange { get; set; } = 8;

        // If a bot is farther than this from the nearest graph node when
        // planning, we consider them "lost" and teleport-rescue them onto
        // the graph. Larger than PathFollower's 38-tile A* range, with
        // buffer for terrain irregularities.
        public int MaxApproachDistance { get; set; } = 50;

        // ---- State ----
        public string DestinationName { get; set; }

        // Read-only view of the current leg waypoint (the waypoint the
        // bot is currently walking toward). Returns null if the bot has
        // arrived or hasn't planned a path yet. Used by [BotInfo for
        // diagnostics.
        public string CurrentLegWaypoint
        {
            get
            {
                if (_plannedPath == null) return null;
                if (_legIndex < 0 || _legIndex >= _plannedPath.Count) return null;
                return _plannedPath[_legIndex];
            }
        }

        // How many legs into the planned path the bot currently is.
        // 1-based for human readability. Returns "n/m" via the formatter,
        // not raw indices.
        public string LegProgress
        {
            get
            {
                if (_plannedPath == null || _plannedPath.Count == 0) return "—";
                return $"{_legIndex + 1}/{_plannedPath.Count}";
            }
        }
        public ArrivalStyle Arrival { get; set; } = ArrivalStyle.Linger;

        // Planned path through the graph (sequence of node names). Index
        // _legIndex is the current leg target.
        private List<string> _plannedPath = new();
        private int _legIndex = 0;

        // Read-only exposure for the live map view (LiveMapSnapshot draws a
        // selected bot's planned route). Names are resolved to coordinates
        // via WaypointRegistry at snapshot time.
        public IReadOnlyList<string> PlannedPath => _plannedPath;
        public int LegIndex => _legIndex;

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

        // Post-arrival drift: after a bot reaches its final waypoint and
        // marks arrived, they continue walking toward the actual destination
        // coord for a short time. Lets a bot heading to "Etheral Goods"
        // actually step inside the shop after reaching "Etheral Goods Door"
        // waypoint. Drift is purely cosmetic — _hasArrived is already true,
        // so the lifecycle is happy. If drift fails (can't path, stuck,
        // times out) the bot just stops wherever they ended up.
        private bool _isDrifting;
        // Field-based final approach: when the destination has a
        // precomputed DistanceField and the bot is inside its coverage,
        // we ride the field's gradient straight to the destination tile
        // instead of drifting via PathFollower. Deterministic; never
        // gets stuck on the doorway. Falls back to drift when no field.
        private bool _isApproaching;
        private DateTime _driftStartedAt;
        private int _driftBestDist = int.MaxValue;
        private DateTime _driftLastProgress;
        // Drift gives up after 6 seconds total, or 3 seconds without
        // progress, or once within 2 tiles of the destination coord.
        private TimeSpan DriftMaxDuration => TimeSpan.FromSeconds(6);
        private TimeSpan DriftStuckTimeout => TimeSpan.FromSeconds(3);
        private const int DriftArriveRange = 2;

        // Stuck detection at the leg level.
        private Point3D _lastLoc;
        private DateTime _lastProgressAt;
        // Counts how many FULL escalation cycles have completed on the
        // current leg. One cycle = stuck → repath → nudge+repath → nudge
        // again (the existing 3-step escalation). After this many cycles
        // on the same leg with no real progress, the bot abandons the
        // destination entirely and picks a new one — the wall/obstacle
        // is genuinely blocking the route and bouncing further won't fix
        // it. Reset when the bot makes progress (legDist drops).
        private int _legCyclesSpent;
        private const int MaxLegCycles = 3;
        private int _legAttempts;
        // Best (smallest) distance to current leg target the bot has achieved.
        // Used as the "made progress" test instead of "any movement" — a bot
        // jiggling around a lightpost moves but doesn't get closer to goal.
        private int _bestDistToLeg = int.MaxValue;
        private static readonly TimeSpan StuckTimeout = TimeSpan.FromMilliseconds(1200);
        private const int MaxLegAttempts = 3;

        private DateTime _pauseUntil = DateTime.MinValue;

        private Timer _stepTimer;

        // Last position recorded at the bottom of StepOnce. If we tick
        // again and bot.Location == _lastStepLoc, the Follow() call made
        // no progress — likely a wall or another bot blocking. Used by
        // the fast-path wall-detect to nudge immediately instead of
        // waiting for the StuckTimeout.
        private Point3D _lastStepLoc;
        private int _stuckStepCount;

        private static readonly string[] AmbientChat = { "traveling", "small_talk" };
        private static readonly string[] CombatChat  = { "combat_actions" };

        // Chat categories used while the bot is AT a destination, lingering.
        // Picked by the destination's DestinationType so a bot at a bank
        // talks shop, a bot at a tavern banters, etc. Falls back to small
        // talk for anything unmapped. Reuses existing chat files — when we
        // add dedicated "tavern" / "shopping" files later these can be
        // pointed at richer content.
        private static string[] ArrivalChatFor(DestinationType type)
        {
            switch (type)
            {
                case DestinationType.Bank:
                    return new[] { "bank_actions", "wts", "wtb", "small_talk" };
                case DestinationType.Tavern:
                case DestinationType.Inn:
                    return new[] { "small_talk", "lfg" };
                case DestinationType.VendorSmith:
                case DestinationType.VendorMage:
                case DestinationType.VendorTailor:
                case DestinationType.VendorCarpenter:
                case DestinationType.VendorBowyer:
                case DestinationType.VendorAlchemist:
                case DestinationType.VendorWeaponer:
                case DestinationType.VendorProvisioner:
                    return new[] { "wtb", "small_talk" };
                case DestinationType.Dungeon:
                    return new[] { "lfg", "combat_actions" };
                case DestinationType.Healer:
                case DestinationType.Graveyard:
                case DestinationType.Library:
                default:
                    return new[] { "small_talk" };
            }
        }

        // Resolved destination Type for the CURRENT destination. Set when a
        // path is planned (from the DestinationCatalog entry). Used to pick
        // arrival chatter. Defaults to CityCenter (-> small talk) for bare
        // waypoint routes with no catalog entry.
        private DestinationType _destType = DestinationType.CityCenter;

        // When lingering at a destination, occasionally turn to face a new
        // direction so the bot doesn't read as a frozen statue. Tracked so
        // we only turn every few seconds, not every tick.
        private DateTime _nextIdleTurn = DateTime.MinValue;

        // Whether we've already rolled the "hand off to a destination
        // behavior" decision for this arrival. Rolled once, the first time
        // HandleArrival runs after arriving. Prevents re-rolling every tick.
        private bool _handoffRolled;

        // Set true when a moongate trip has been scheduled. MoongateTravel
        // teleports the bot and swaps its behavior after a short delay; in
        // the meantime this Traveler must do NOTHING — not linger, not
        // wander, not pick a new destination — or it could walk the bot
        // off the gate before the teleport fires.
        private bool _moongateTripPending;

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

            // Ambient/combat chatter while traveling. When the bot has
            // ARRIVED, chat is handled by DoArrivalActivity instead (with
            // destination-appropriate categories), so skip it here to
            // avoid a traveling bot saying "still on the road" while
            // standing in a bank.
            if (!_hasArrived)
            {
                ChatCategories = bot.Combatant != null ? CombatChat : AmbientChat;
                TrySpeak(bot);
            }

            // -- 1. Combat / threat response --
            //
            // A Traveler doesn't fight inline. The moment it has a
            // combatant OR spots a hostile nearby, it hands off to an
            // Adventurer in DEFENDER mode — which has the good combat
            // (surround, chase-fleeing-monsters) and, crucially, swaps the
            // bot back to a Traveler the instant the fight is over so the
            // trip resumes. Defenders also retreat sooner than hunters.
            var combatant = bot.Combatant;
            Mobile threat = combatant as Mobile;
            if (threat == null || threat.Deleted || !threat.Alive)
                threat = FindNearbyEnemy(bot);

            if (threat != null && !threat.Deleted && threat.Alive)
            {
                // Crafters are tradespeople — they have no combat training
                // and shouldn't be fighting monsters. Instead of swapping
                // to a defender, they BREAK off the trip and pick a new
                // destination (one that's hopefully not in the threat's
                // direction). It's their version of fleeing: head somewhere
                // else and let the threat fall behind.
                if (bot.Class == BotClass.Crafter)
                {
                    Log(bot, $"Crafter attacked while traveling — abandoning route");
                    bot.Combatant = null;
                    PickNewDestination(bot);
                    return;
                }

                Log(bot, $"Attacked while traveling — switching to defender");
                StopStepTimer();

                var defender = new AdventurerBehavior
                {
                    DefenderMode = true,
                    DefenderRetreatHpFraction = 0.40,
                    ResumeDestination = DestinationName,
                };
                bot.Combatant = threat;
                // Swapping Behavior detaches this Traveler. Return at once.
                bot.Behavior = defender;
                return;
            }

            // -- 3. Arrived? --
            if (_hasArrived)
            {
                // While drifting toward the destination, tick the drift
                // logic. Drift ends naturally when close enough, stuck,
                // or timed out — then HandleArrival takes over.
                if (_isApproaching)
                {
                    var apField = DestinationFieldCache.Get(DestinationName);
                    var apResult = FieldApproach.Step(
                        bot, apField,
                        _finalCoord ?? bot.Location, DriftArriveRange);
                    switch (apResult)
                    {
                        case ApproachResult.Stepped:
                            return;
                        case ApproachResult.Arrived:
                        case ApproachResult.Blocked:
                        case ApproachResult.NoField:
                            _isApproaching = false;
                            StopStepTimer();
                            HandleArrival(bot);
                            return;
                    }
                }
                if (_isDrifting)
                {
                    TickDrift(bot);
                    return;
                }
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
                _legCyclesSpent = 0;  // genuine progress — reset the cycle counter too
            }
            else if (Core.Now - _lastProgressAt > StuckTimeout)
            {
                _legAttempts++;
                _lastProgressAt = Core.Now;

                if (_legAttempts >= MaxLegAttempts)
                {
                    // A full escalation cycle just completed (repath ->
                    // nudge+repath -> nudge). Count it.
                    _legCyclesSpent++;

                    // If we've burned too many cycles without making real
                    // progress, the bot is genuinely blocked — there's a
                    // wall or geometry the planner can't get around. Stop
                    // grinding on this destination and pick another one.
                    if (_legCyclesSpent >= MaxLegCycles)
                    {
                        string stuckLeg = (_plannedPath.Count > 0 && _legIndex < _plannedPath.Count)
                            ? _plannedPath[_legIndex] : "?";
                        bool stuckOnFinalLeg = (_legIndex == _plannedPath.Count - 1);
                        string flag = stuckOnFinalLeg ? " (FINAL leg — bad destination coord?)" : "";
                        Log(bot, $"GIVING UP on '{DestinationName}' at leg {_legIndex + 1}/{_plannedPath.Count} " +
                                 $"({stuckLeg}){flag} after {_legCyclesSpent} stuck cycles — picking new destination");
                        PickNewDestination(bot);
                        return;
                    }

                    // Otherwise: nudge + repath, reset counter for the
                    // next cycle.
                    Log(bot, $"STUCK x{_legAttempts} on '{(_plannedPath.Count > 0 ? _plannedPath[_legIndex] : "?")}' " +
                             $"({legDist} tiles, cycle {_legCyclesSpent}/{MaxLegCycles}) — nudging + repath");
                    NudgeAway(bot);
                    _follower?.ForceRepath();
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
            _destType = DestinationType.CityCenter;  // default for bare waypoints

            var destObj = DestinationCatalog.GetByName(DestinationName);
            if (destObj != null)
            {
                routeTargetWaypoint = destObj.NearestWaypoint;
                _finalCoord = destObj.ArrivalPoint ?? destObj.Location;
                _destType = destObj.Type;
                // Multi-arrival: pick a spot to stand at + its best waypoint.
                var pickedWp = ApplyArrival(destObj);
                if (pickedWp != null) routeTargetWaypoint = pickedWp;
            }
            else if (graph.Get(DestinationName) == null)
            {
                // Neither a known destination nor a known waypoint. Re-roll.
                DestinationName = PickNewDestinationName(bot);
                destObj = DestinationCatalog.GetByName(DestinationName);
                if (destObj != null)
                {
                    routeTargetWaypoint = destObj.NearestWaypoint;
                    _finalCoord = destObj.ArrivalPoint ?? destObj.Location;
                    _destType = destObj.Type;
                    var pickedWp2 = ApplyArrival(destObj);
                    if (pickedWp2 != null) routeTargetWaypoint = pickedWp2;
                }
                else
                {
                    routeTargetWaypoint = DestinationName;
                }
            }

            // Final safety — if the route target waypoint doesn't exist
            // in the graph, pick a random one we know.
            // Resolve the route target defensively. The stored NearestWaypoint
            // can be MISSING (node removed) or STALE — pointing at a waypoint
            // far from the destination's actual coordinates (observed: 220
            // tiles after remaps). Either way, recompute the nearest node to
            // the destination coord dynamically. Newly [MarkWay'd waypoints
            // are picked up automatically — no JSON editing needed.
            var targetNode = graph.Get(routeTargetWaypoint);
            if (_finalCoord.HasValue)
            {
                // A linked waypoint is authoritative: only recompute when the
                // linked node is genuinely MISSING (deleted from the graph),
                // not merely far. This honors deliberate hand-links that route
                // AROUND walls (farther than the wrong-but-near node through one).
                bool stale = targetNode == null;
                if (stale)
                {
                    var better = graph.FindNearestNode(_finalCoord.Value);
                    if (better != null && better.Name != routeTargetWaypoint)
                    {
                        Log(bot, $"Route target '{routeTargetWaypoint}' is " +
                                 (targetNode == null ? "missing" : "far from") +
                                 $" '{DestinationName}' — using nearest node '{better.Name}'");
                        routeTargetWaypoint = better.Name;
                        targetNode = better;
                    }
                }
            }
            if (targetNode == null && graph.Get(routeTargetWaypoint) == null)
            {
                // Last resort (no destination coord to recompute from):
                // a random node beats a null-route crash, barely.
                routeTargetWaypoint = graph.PickRandomName();
            }

            _plannedPath = graph.FindPath(nearest.Name, routeTargetWaypoint);

            // ----- island reroute (WaypointGraph-based) -------------------
            // If FindPath to the destination came back empty, the bot and its
            // destination are on disconnected landmasses (e.g. Skara Brae
            // island -> mainland) — it can't walk there. Route instead to the
            // nearest moongate the bot CAN reach (non-empty FindPath = same
            // landmass); the gate's step-through carries it off-island, and it
            // picks a fresh destination on the far side.
            if ((_plannedPath == null || _plannedPath.Count == 0) &&
                _destType != DestinationType.Moongate)
            {
                string bestGate = null; string bestGateName = null;
                int bestDist = int.MaxValue;
                foreach (var mg in DestinationCatalog.All)
                {
                    if (mg.Type != DestinationType.Moongate) continue;
                    if (string.IsNullOrEmpty(mg.NearestWaypoint)) continue;
                    var gatePath = graph.FindPath(nearest.Name, mg.NearestWaypoint);
                    if (gatePath == null || gatePath.Count == 0) continue; // unreachable gate
                    int d = Math.Max(Math.Abs(mg.Location.X - bot.X),
                                     Math.Abs(mg.Location.Y - bot.Y));
                    if (d < bestDist)
                    { bestDist = d; bestGate = mg.NearestWaypoint; bestGateName = mg.Name; }
                }
                if (bestGate != null)
                {
                    Log(bot, $"Destination '{DestinationName}' unreachable by foot " +
                             $"(across water) — routing to moongate '{bestGateName}' " +
                             $"(wp '{bestGate}') to gate off-island");
                    // Become a moongate-bound bot: retarget the route AND the
                    // destination identity, so on arrival the Moongate step-
                    // through branch fires instead of a stale Healer/vendor
                    // handoff. Without this the bot just stood at the gate.
                    DestinationName = bestGateName;
                    _destType = DestinationType.Moongate;
                    routeTargetWaypoint = bestGate;
                    var gateObj = DestinationCatalog.GetByName(bestGateName);
                    _finalCoord = gateObj != null
                        ? (gateObj.ArrivalPoint ?? gateObj.Location)
                        : (Point3D?)null;
                    _plannedPath = graph.FindPath(nearest.Name, routeTargetWaypoint);
                }
            }
            _legIndex = 0;
            // Fresh path, fresh best-distance tracker. NOTE: the stuck
            // cycle counter is deliberately NOT reset here; stuck recovery
            // ends every cycle in a repath that lands back in PlanPath, and
            // wiping the counter each time made the 3-cycle give-up
            // unreachable (bots paced forever at 'cycle 1/3'). It resets
            // only on REAL progress: a leg actually reached.
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
            // Also drift toward the destination coord if we're not on it
            // yet — same as the normal arrival flow.
            if (_plannedPath.Count == 0 ||
                (_plannedPath.Count == 1 && nearest.Name == routeTargetWaypoint &&
                 bot.InRange(nearest.Location, LegArrivalRange)))
            {
                _hasArrived = true;
                _arrivedAt  = Core.Now;
                Log(bot, $"Already at destination '{DestinationName}'");

                if (_finalCoord.HasValue &&
                    !bot.InRange(_finalCoord.Value, DriftArriveRange))
                {
                    BeginFinalApproach(bot);
                }
                else
                {
                    StopStepTimer();
                }
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

            // Arrival check is against the WAYPOINT location (with per-bot
            // offset on the final leg for visual spread). The destination
            // coord is intentionally NOT used for arrival — it's
            // informational only. See the long comment in StartCurrentLeg.
            bool isFinalLegCheck = _legIndex == _plannedPath.Count - 1;
            Point3D arrivalCheckLoc;
            if (isFinalLegCheck)
            {
                arrivalCheckLoc = new Point3D(
                    node.Location.X + _finalOffsetX,
                    node.Location.Y + _finalOffsetY,
                    node.Location.Z
                );
            }
            else
            {
                arrivalCheckLoc = node.Location;
            }

            // Per-node arrival tolerance: door/entrance waypoints with
            // ArrivalRange > 0 in JSON override the default (LegArrivalRange).
            // This lets us tighten tolerance on Z=27 doors etc. where the
            // bot must actually step onto the exact tile.
            //
            // FINAL leg gets a wider tolerance. Destinations are often
            // marked at a tile INSIDE a building (the shop center, the
            // vendor stand). A bot routing from the street can't reach
            // that exact tile through the wall; it would grind the outside
            // until v66's give-up abandoned the trip. So on the final leg
            // we accept "close enough" — being on the building's doorstep
            // counts as arrived. The visit behavior (Shopper, BankSitter)
            // can drift inside cosmetically from there.
            int effectiveRange = node.ArrivalRange > 0 ? node.ArrivalRange : LegArrivalRange;
            // ZONE-ENTRY ARRIVAL: if the destination has a painted Area
            // and the bot has physically stepped inside it mid-route, it is
            // THERE. Don't finish remaining legs, don't drift toward the
            // exact NPC coordinate behind the counter — a bank-bound bot
            // becomes a BankSitter the moment it walks into the bank.
            if (_finalCoord.HasValue)
            {
                var areaZone = ZoneRegistry.AreaForDestination(DestinationName, _finalCoord.Value);
                if (areaZone != null && areaZone.Contains(bot.X, bot.Y))
                {
                    Log(bot, $"Entered '{areaZone.Name}' — arrived at '{DestinationName}'");
                    _hasArrived = true;
                    _arrivedAt  = Core.Now;
                    _isDrifting = false;
                    StopStepTimer();
                    return;
                }
            }

            if (isFinalLegCheck)
            {
                effectiveRange = Math.Max(effectiveRange, FinalLegArrivalRange);
            }

            if (bot.InRange(arrivalCheckLoc, effectiveRange))
            {
                Log(bot, $"Reached leg {_legIndex + 1}/{_plannedPath.Count}: {node.Name}");
                _legCyclesSpent = 0;   // real progress clears the stuck ladder
                _legIndex++;
                if (_legIndex >= _plannedPath.Count)
                {
                    _hasArrived = true;
                    _arrivedAt  = Core.Now;
                    Log(bot, $"ARRIVED at destination '{DestinationName}'");

                    // If we have a destination coord that's distinct from
                    // where we landed, drift toward it for a few seconds
                    // so the bot visibly walks into the shop / onto the
                    // exact destination tile. Purely cosmetic — arrival
                    // is already recorded. If drift can't reach, no harm.
                    if (_finalCoord.HasValue &&
                        !bot.InRange(_finalCoord.Value, DriftArriveRange))
                    {
                        BeginFinalApproach(bot);
                    }
                    else
                    {
                        StopStepTimer();
                    }
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
                // Crossing into a new leg means the previous leg's
                // troubles are behind us — start the cycle counter fresh.
                _legCyclesSpent = 0;
            }

            // Target this leg's waypoint with PathFollower.
            //
            // IMPORTANT: even on the FINAL leg, we target the waypoint
            // location (with per-bot offset for spread), NOT the destination
            // coord. The destination is where the bot conceptually "wants
            // to go" but the waypoint is the actual physical reachable
            // tile. Pathfinding directly to destination coords frequently
            // fails because destinations are often interior tiles (Z=27
            // for shop upstairs, behind doors, etc) that A* can't reach
            // from a street tile. The waypoint should always be a known-
            // walkable position that bots actually reach.
            //
            // The destination's NearestWaypoint should be placed AT or
            // very near the destination's accessible entrance so a bot
            // standing on the waypoint LOOKS LIKE they arrived at the
            // destination.
            Point3D legTarget = new Point3D(
                node.Location.X + (_legIndex == _plannedPath.Count - 1 ? _finalOffsetX : 0),
                node.Location.Y + (_legIndex == _plannedPath.Count - 1 ? _finalOffsetY : 0),
                node.Location.Z
            );
            _follower = new PathFollower(bot, legTarget);
            EnsureStepTimer(bot, running);
        }

        // -------------------------------------------------------------------
        // Post-arrival drift
        //
        // After the bot reaches its final waypoint and marks arrived, drift
        // walks them the last few tiles toward the actual destination coord.
        // For shop destinations this means stepping into the building from
        // the door waypoint.
        //
        // _hasArrived is ALREADY true when drift starts — so the lifecycle
        // is happy regardless of whether the bot makes it the last few
        // tiles. Drift is a pure visual flourish.
        //
        // Ends when: within DriftArriveRange of destination, OR 6 seconds
        // total elapsed, OR 3 seconds without progress (likely wedged).
        // -------------------------------------------------------------------
        // Try field-based final approach first; fall back to drift.
        // Called wherever the bot used to StartDrift directly.
        private void BeginFinalApproach(PlayerBot bot)
        {
            if (!_finalCoord.HasValue) { StopStepTimer(); return; }

            var field = DestinationFieldCache.Get(DestinationName);
            if (field != null && field.Covers(bot.X, bot.Y))
            {
                _isApproaching = true;
                _isDrifting = false;
                EnsureStepTimer(bot, running: false);
                Log(bot, "Final approach via distance field");
                return;
            }
            // No field / not in coverage — use the legacy drift.
            StartDrift(bot);
        }

        private void StartDrift(PlayerBot bot)
        {
            if (!_finalCoord.HasValue) { StopStepTimer(); return; }

            _isDrifting = true;
            _driftStartedAt = Core.Now;
            _driftLastProgress = Core.Now;
            int dx = bot.X - _finalCoord.Value.X;
            int dy = bot.Y - _finalCoord.Value.Y;
            _driftBestDist = (int)Math.Sqrt(dx * dx + dy * dy);

            // Two-stage goal: if a portal serves this area and we haven't
            // reached it yet, aim at the PORTAL first so A* heads for the
            // doorway gap; once we're at/through it, aim at the area CENTER
            // so we walk on inside. The portal is a threshold to pass
            // through — arrival only fires INSIDE the area (see ZoneArrival).
            Point3D driftGoal = _finalCoord.Value;
            var areaZone = ZoneRegistry.AreaForDestination(DestinationName, _finalCoord.Value);
            if (areaZone != null)
            {
                driftGoal = new Point3D(areaZone.CenterX, areaZone.CenterY, _finalCoord.Value.Z);
                var portal = ZoneRegistry.NearestPortalTo(_finalCoord.Value, 20);
                if (portal != null && !areaZone.Contains(bot.X, bot.Y))
                {
                    int toPortal = System.Math.Max(System.Math.Abs(bot.X - portal.CenterX),
                                                   System.Math.Abs(bot.Y - portal.CenterY));
                    if (toPortal > 1)
                        driftGoal = new Point3D(portal.CenterX, portal.CenterY, _finalCoord.Value.Z);
                }
            }
            _follower = new PathFollower(bot, driftGoal);
            EnsureStepTimer(bot, running: false);  // walk into shops, don't run
            Log(bot, $"Drifting toward destination coord ({_driftBestDist} tiles)");
        }

        private void TickDrift(PlayerBot bot)
        {
            if (!_finalCoord.HasValue) { EndDrift(bot, "no coord"); return; }

            // Painted area? Walk WELL INSIDE before stopping — don't end
            // the moment the polygon edge is touched (that left bots loitering
            // at the threshold). Keep drifting toward the area center; end
            // only when close to center. If the follower stalls inside the
            // area, the stuck/timeout paths below still end it gracefully,
            // and arrival accepts anywhere inside.
            var az = ZoneRegistry.AreaForDestination(DestinationName, _finalCoord.Value);
            if (az != null && az.Contains(bot.X, bot.Y))
            {
                int cdx = bot.X - az.CenterX, cdy = bot.Y - az.CenterY;
                int cdist = System.Math.Max(System.Math.Abs(cdx), System.Math.Abs(cdy));
                if (cdist <= 2) { EndDrift(bot, "well inside the area"); return; }
                // inside but not yet central — let the follower keep walking
                // toward center; refresh progress so it isn't called "stuck"
                // merely for being inside.
                _driftLastProgress = Core.Now;
            }

            // Close enough?
            int dx = bot.X - _finalCoord.Value.X;
            int dy = bot.Y - _finalCoord.Value.Y;
            int dist = (int)Math.Sqrt(dx * dx + dy * dy);
            if (dist <= DriftArriveRange)
            {
                EndDrift(bot, "reached coord");
                return;
            }

            // Timed out?
            if (Core.Now - _driftStartedAt > DriftMaxDuration)
            {
                EndDrift(bot, "timeout");
                return;
            }

            // Stuck (no closer-progress in N seconds)?
            if (dist < _driftBestDist)
            {
                _driftBestDist = dist;
                _driftLastProgress = Core.Now;
            }
            else if (Core.Now - _driftLastProgress > DriftStuckTimeout)
            {
                EndDrift(bot, "stuck");
                return;
            }
            // Otherwise the existing step timer keeps walking via
            // _follower toward _finalCoord. No action needed here.
        }

        private void EndDrift(PlayerBot bot, string reason)
        {
            _isDrifting = false;
            StopStepTimer();
            Log(bot, $"Drift ended ({reason})");
        }

        // -------------------------------------------------------------------
        // Arrival
        //
        // When a bot reaches its destination, the FIRST tick after arrival
        // rolls a chance to HAND OFF to a behavior that matches the place:
        //
        //   Bank      -> chance to become a BankSitter (stands, chats)
        //   Graveyard -> chance to become an Adventurer (fights the undead)
        //
        // A handoff behavior is a TIMED VISIT (VisitExpiresAt set 5-15 min
        // out) — when the visit ends, the behavior returns the bot to
        // traveling automatically.
        //
        // If no handoff is rolled (or the destination type has no handoff
        // yet — vendors, taverns), the bot does the light "arrival
        // activity": destination-appropriate chatter and idle turning, for
        // the linger window, then moves on.
        //
        // Arrival styles still apply for the no-handoff path:
        //   Linger — light activity 60-120s, then new destination.
        //   Wait   — light activity indefinitely (until lifecycle moves it).
        //   Wander — leave again immediately.
        // -------------------------------------------------------------------
        private void HandleArrival(PlayerBot bot)
        {
            // A moongate trip is scheduled — MoongateTravel will teleport
            // the bot and swap its behavior shortly. Do nothing until then;
            // any movement here could carry the bot off the gate.
            if (_moongateTripPending)
            {
                StopStepTimer();
                return;
            }

            // First tick after arriving: roll the handoff decision once —
            // but WAIT for the final-approach drift to finish first. The
            // handoff gates measure distance to the destination coord, and
            // the drift is what closes that distance; rolling at the street
            // waypoint (pre-drift) made the gates reject nearly everyone.
            if (!_handoffRolled)
            {
                if (_isDrifting)
                {
                    return;   // drift timer is walking us in; roll when done
                }
                _handoffRolled = true;
                if (TryHandoffToDestinationBehavior(bot))
                {
                    // Behavior swapped — THIS Traveler is detached. Stop.
                    return;
                }

                // No handoff. For destinations where loitering as a
                // Traveler is wrong (dangerous areas, vendor shops),
                // leave immediately and pick another destination instead
                // of standing around doing nothing.
                if (IsLeaveImmediately(_destType))
                {
                    Log(bot, $"Not handing off at '{DestinationName}' " +
                             $"({_destType}) — leaving immediately");
                    PickNewDestination(bot);
                    return;
                }
            }

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
                        DoArrivalActivity(bot);
                    }
                    break;

                case ArrivalStyle.Wait:
                    DoArrivalActivity(bot);
                    break;

                case ArrivalStyle.Wander:
                    PickNewDestination(bot);
                    break;
            }
        }

        // Destinations where a bot that DOESN'T commit to a handoff
        // should leave immediately rather than loiter as a Traveler.
        // Two kinds:
        //   - Dangerous (graveyard, dungeon): standing as a Traveler
        //     means getting chewed on by monsters.
        //   - Vendor shops: a Traveler standing at the smith doing
        //     nothing makes no sense — they came to shop or they didn't,
        //     and if they didn't, they shouldn't be here.
        private static bool IsLeaveImmediately(DestinationType type)
        {
            return type == DestinationType.Graveyard
                || type == DestinationType.Dungeon
                || type == DestinationType.VendorSmith
                || type == DestinationType.VendorMage
                || type == DestinationType.VendorTailor
                || type == DestinationType.VendorCarpenter
                || type == DestinationType.VendorBowyer
                || type == DestinationType.VendorAlchemist
                || type == DestinationType.VendorWeaponer
                || type == DestinationType.VendorProvisioner;
        }

        // Roll whether to hand off to a destination-appropriate behavior.
        // Returns true if a handoff happened (bot.Behavior was swapped —
        // caller must return immediately).
        //
        // Only Bank and Graveyard have handoffs wired up so far. Other
        // types (vendors, taverns) fall through to the light arrival
        // activity until their dedicated behaviors are built.
        // Zone-aware arrival: a painted Area outline defines the place.
        // Inside it = arrived. Standing at a portal that serves it = arrived
        // (the doorstep, for doored buildings Travelers can't enter — the
        // visit behavior crosses the threshold). No zone painted = fall
        // back to the plain distance gate, so unpainted destinations keep
        // working exactly as before.
// PURE ZONE ARRIVAL: a painted Area defines the place. Inside it,
// or standing at a portal that serves it (doorstep for doored
// buildings), counts as arrived. NO Area painted = NOT arrived for
// handoff purposes — the destination point is only a routing guide
// now and has no bearing on becoming a Shopper/BankSitter.
        // Choose a route waypoint for an arrival spot: the spot's listed
        // waypoint whose graph node is nearest the spot. Returns null if the
        // spot lists none that exist (caller falls back to dynamic nearest).
        private string BestWaypointForSpot(ArrivalSpot spot)
        {
            if (spot == null || spot.Waypoints == null) return null;
            var graph = WaypointRegistry.Graph;
            if (graph == null) return null;
            string best = null; int bd = int.MaxValue;
            foreach (var wn in spot.Waypoints)
            {
                var node = graph.Get(wn);
                if (node == null) continue;
                int d = Math.Max(Math.Abs(node.Location.X - spot.Point.X),
                                 Math.Abs(node.Location.Y - spot.Point.Y));
                if (d < bd) { bd = d; best = wn; }
            }
            return best;
        }

        // Apply a picked arrival spot to the current route state. Sets
        // _finalCoord to the spot and returns the chosen route waypoint
        // (best of the spot's options), or null to use the existing target.
        private string ApplyArrival(BotDestination destObj)
        {
            var spot = destObj.PickArrival();
            if (spot == null) return null;
            _finalCoord = spot.Point;
            return BestWaypointForSpot(spot);
        }



private bool ZoneArrival(PlayerBot bot, int fallbackRange)
{
    if (!_finalCoord.HasValue) return false;

    // Arrival-point destinations: reaching the placed arrival tile IS
    // arrival — no painted area required (the go-to-a-spot path).
    var dObj = DestinationCatalog.GetByName(DestinationName);
    if (dObj != null && dObj.ArrivalPoint.HasValue)
        return bot.InRange(dObj.ArrivalPoint.Value, 3);  // ArrivalPoint reached = arrived
    var zone = ZoneRegistry.AreaForDestination(DestinationName, _finalCoord.Value);
    if (zone == null) return false;            // unpainted: never hand off
    // Arrival = INSIDE the area only. The portal is a threshold to pass
    // THROUGH on the way in, never a place to stop and hand off — that
    // made bots turn into Shoppers standing on the portal tile.
    if (zone.Contains(bot.X, bot.Y)) return true;
    return false;
}

        private bool TryHandoffToDestinationBehavior(PlayerBot bot)
        {
            // Moongate: a bot arriving at a moongate has a chance to step
            // through it and emerge at a random other moongate — this is
            // how bots spread between cities. Handled before the switch
            // because it's not a behavior-registry handoff; MoongateTravel
            // teleports the bot and attaches a fresh Traveler itself.
            if (_destType == DestinationType.Moongate)
            {
                const double useChance = 0.80;  // 80% step through the gate
                if (Utility.RandomDouble() < useChance)
                {
                    if (MoongateTravel.BeginTrip(bot, DestinationName))
                    {
                        Log(bot, $"Stepping through the moongate at " +
                                 $"'{DestinationName}'");
                        // BeginTrip teleports + swaps behavior after a
                        // short delay. Until then, freeze this Traveler so
                        // it doesn't walk the bot off the gate.
                        _moongateTripPending = true;
                        return true;
                    }
                }
                // Didn't roll the chance, or only one gate exists — fall
                // through; the bot just lingers at the gate like any other
                // non-handoff destination.
                return false;
            }

            string targetBehavior = null;
            double chance = 0.0;
            int visitMinMinutes = 5;
            int visitMaxMinutes = 15;

            // Crafter at its own station: a Crafter-class bot that has
            // arrived at the destination type matching its CrafterType
            // (Smith->Forge, Tailor->VendorTailor, Bowcrafter->VendorBowyer,
            // others->Bank) settles in to "work" there. High chance and a
            // long visit — a crafter stays at its station, it doesn't just
            // pass through. Set the handoff variables and let the shared
            // build block below construct and attach the behavior.
            if (bot.Class == BotClass.Crafter &&
                _destType == CrafterTypeHelper.StationFor(bot.CrafterSpec))
            {
                targetBehavior  = "Crafter";
                chance          = 0.95;
                // Crafters live at their station — they don't "visit" it,
                // they work there. Long sessions (3-6 hours of in-game
                // time) so a crafter is at the forge / dock / vendor for
                // the overwhelming majority of their life, and only
                // briefly elsewhere (bank/shop errands between bouts).
                // The visit eventually expires, the bot picks a short
                // destination, then a fresh roll usually sends them back
                // to their station — netting ~90% station time as
                // requested.
                visitMinMinutes = 180;
                visitMaxMinutes = 360;
            }
            else
            {
                switch (_destType)
                {
                case DestinationType.Bank:
                    // Only genuine bank-reachers sit. A bot that "arrived"
                    // via a waypoint gap 20 tiles down the street must not
                    // become a BankSitter on the cobblestones.
                    if (!ZoneArrival(bot, 15))
                    {
                        Log(bot, $"No BankSitter handoff at '{DestinationName}' — " +
                                 $"{(_finalCoord.HasValue ? bot.GetDistanceToSqrt(_finalCoord.Value).ToString("0") : "?")} tiles from the bank proper");
                        return false;
                    }
                    targetBehavior = "BankSitter";
                    chance = 0.40;  // many bank visitors just pass through
                    break;

                case DestinationType.Graveyard:
                    targetBehavior = "Adventurer";
                    chance = 0.75;  // you go to a graveyard to fight
                    break;

                case DestinationType.VendorSmith:
                case DestinationType.VendorMage:
                case DestinationType.VendorTailor:
                case DestinationType.VendorCarpenter:
                case DestinationType.VendorBowyer:
                case DestinationType.VendorAlchemist:
                case DestinationType.VendorWeaponer:
                case DestinationType.VendorProvisioner:
                    // The doorstep is a genuine arrival: Travelers can't
                    // open doors, so they deliver to the doorway and the
                    // SHOPPER walks in to the counter (it opens doors).
                    // 8 tiles accepts doorsteps, rejects street-stallers.
                    if (!ZoneArrival(bot, 15))
                    {
                        Log(bot, $"No Shopper handoff at '{DestinationName}' — " +
                                 $"{(_finalCoord.HasValue ? bot.GetDistanceToSqrt(_finalCoord.Value).ToString("0") : "?")} tiles from the vendor (leaving)");
                        return false;
                    }
                    targetBehavior = "Shopper";
                    // High chance — if the bot walked to a shop, it should
                    // shop. The leftover 20% LEAVE the destination (handled
                    // by IsLeaveImmediately below), they don't loiter as
                    // Travelers in a vendor's room.
                    chance = 0.80;
                    visitMinMinutes = 1;
                    visitMaxMinutes = 3;
                    break;

                // Taverns, inns, healers, etc: no dedicated behavior yet —
                // fall through to light arrival activity.
                default:
                    return false;
                }
            }

            if (targetBehavior == null) return false;
            if (Utility.RandomDouble() > chance) return false;

            // Build the visit behavior and stamp its expiry.
            var visit = BehaviorRegistry.Create(targetBehavior);
            if (visit == null) return false;
            visit.VisitExpiresAt = Core.Now + TimeSpan.FromMinutes(
                Utility.RandomMinMax(visitMinMinutes, visitMaxMinutes));

            Log(bot, $"Destination handoff: becoming {targetBehavior} " +
                     $"at '{DestinationName}' " +
                     $"(visit ends in {visitMinMinutes}-{visitMaxMinutes} min)");

            // Swapping Behavior detaches this Traveler. Caller returns.
            bot.Behavior = visit;
            return true;
        }

        // The "I'm here, doing something" loop. Lightweight — chat from
        // destination-appropriate categories and small idle turning. The
        // step timer stays stopped (the bot isn't walking anywhere) but the
        // bot is no longer a frozen statue.
        private void DoArrivalActivity(PlayerBot bot)
        {
            StopStepTimer();  // not walking — but the tick still runs

            if (bot.Map == null || bot.Map == Map.Internal) return;

            // Chat from categories that match the destination type. A bot
            // at a bank talks shop; a bot at a tavern banters; etc.
            ChatCategories = ArrivalChatFor(_destType);
            TrySpeak(bot);

            // Occasionally turn to face a new direction so the bot reads as
            // awake. Every 4-9 seconds, not every tick.
            if (Core.Now >= _nextIdleTurn)
            {
                bot.Direction = (Direction)Utility.Random(8);
                _nextIdleTurn = Core.Now +
                    TimeSpan.FromSeconds(Utility.RandomMinMax(4, 9));
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
            _isDrifting = false;
            _handoffRolled = false;
            _moongateTripPending = false;
            _nextIdleTurn = DateTime.MinValue;
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

        // Single-step sidestep — try each compass direction in random
        // order, take the first that succeeds. Cheaper than NudgeAway
        // (which moves multiple tiles); used by the fast-path wall-detect
        // to break a stuck-against-wall bot loose without overshooting.
        private static void SidestepOnce(PlayerBot bot)
        {
            Direction[] dirs =
            {
                Direction.North, Direction.East, Direction.South, Direction.West,
                Direction.Up,    Direction.Down, Direction.Left,  Direction.Right,
            };
            for (int i = dirs.Length - 1; i > 0; i--)
            {
                int j = Utility.Random(i + 1);
                (dirs[i], dirs[j]) = (dirs[j], dirs[i]);
            }
            foreach (var d in dirs)
            {
                if (bot.Direction != d) bot.Direction = d;
                if (bot.Move(d)) return;
            }
            // All 8 blocked — wait a tick.
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

            // Per-node arrival tolerance (door/entrance waypoints can set a
            // tighter ArrivalRange in JSON).
            int followRange = LegArrivalRange;
            if (_plannedPath.Count > 0 && _legIndex < _plannedPath.Count)
            {
                var curNode = WaypointRegistry.Graph.Get(_plannedPath[_legIndex]);
                if (curNode != null && curNode.ArrivalRange > 0)
                {
                    followRange = curNode.ArrivalRange;
                }
            }

            bool arrivedLeg = _follower.Follow(_running, followRange);
            if (!arrivedLeg)
            {
                // Wall-detect fast path: if Follow() didn't move the bot
                // at all this tick (same Location as last tick), it's
                // pressing against a wall, an item, or another bot. After
                // two consecutive frozen ticks, sidestep — try a single
                // step in any walkable direction. This breaks the bot out
                // of wall-loiter quickly, well before StuckTimeout (1.2s)
                // and the heavier nudge+repath escalation would fire.
                if (bot.Location == _lastStepLoc)
                {
                    _stuckStepCount++;
                    if (_stuckStepCount >= 2)
                    {
                        // A closed door is the most common "wall" — open it
                        // like a player would before resorting to sidesteps,
                        // so post-visit travelers can LEAVE buildings instead
                        // of grinding the interior walls forever.
                        if (!DoorHelper.TryOpenAdjacent(bot))
                            SidestepOnce(bot);
                        _stuckStepCount = 0;
                    }
                }
                else
                {
                    _stuckStepCount = 0;
                }
                _lastStepLoc = bot.Location;
                return;  // still walking this leg
            }
            _lastStepLoc = bot.Location;
            _stuckStepCount = 0;

            // Reached the current leg's waypoint. Instead of stopping and
            // waiting up to 2s for the decision tick to advance the leg
            // (which made the bot freeze at EVERY waypoint — the stop-start
            // the route looked like), advance the leg RIGHT HERE and keep
            // the timer running so the bot flows straight through.
            if (_isDrifting)
            {
                // Drift uses its own follower/logic — leave it to the
                // decision tick; just stop here.
                StopStepTimer();
                return;
            }

            if (_hasArrived)
            {
                // Already arrived (field approach in progress — it steps
                // from the decision tick). The step timer must NOT keep
                // advancing legs past the end of the path: doing so
                // re-logged ARRIVED and re-armed the final approach every
                // tick (the ARRIVED/approach ping-pong; leg counters like
                // 183/7), pacing bots in place at their destination.
                StopStepTimer();
                return;
            }

            _legCyclesSpent = 0;       // real progress clears the stuck ladder
            _legIndex++;

            if (_legIndex >= _plannedPath.Count)
            {
                // That was the final leg — arrived at the destination.
                _hasArrived = true;
                _arrivedAt  = Core.Now;
                Log(bot, $"ARRIVED at destination '{DestinationName}'");

                // Cosmetic drift onto the exact destination tile, if the
                // destination coord differs from where we landed.
                if (_finalCoord.HasValue &&
                    !bot.InRange(_finalCoord.Value, DriftArriveRange))
                {
                    BeginFinalApproach(bot);
                }
                else
                {
                    StopStepTimer();
                }
                return;
            }

            // More legs to go — re-aim the follower at the NEXT waypoint
            // immediately. StartCurrentLeg builds the new PathFollower and
            // keeps the step timer running, so there's no pause: the bot
            // rounds the waypoint and continues without stopping.
            _legAttempts = 0;
            StartCurrentLeg(bot);
        }
    }
}
