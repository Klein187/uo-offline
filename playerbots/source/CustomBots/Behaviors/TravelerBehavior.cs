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

        // A Recall/Gate DelayCall sequence is in flight — the bot is about
        // to teleport and attach a fresh Traveler. Group formation must
        // not build a convoy around a leader that's mid-teleport.
        public bool MagicTravelPending => _magicTravelPending;

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

        public override string GetStatusLine(PlayerBot bot)
        {
            if (string.IsNullOrEmpty(DestinationName))
            {
                return "picking a destination";
            }

            var verb = bot.HaulPending ? "hauling goods → " : "→ ";
            var leg = CurrentLegWaypoint;
            return leg == null
                ? $"{verb}{DestinationName} · arriving"
                : $"{verb}{DestinationName} · leg {LegProgress} via {leg}";
        }

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

        // How far outside a painted work site a gatherer may clock in and
        // walk the rest of the way itself (GathererBehavior.TickWalkIn).
        // Beyond this it was never really routed to that site.
        private const int GatherWalkInRange = 45;

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
        private static readonly string[] GhostChat   = { "ghost" };

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

        // ---- Dungeon entry (walk-onto-a-real-teleporter) ----
        // A dungeon entrance is an ORDINARY destination the bot walks to,
        // whose arrival tile is placed on a real in-game Teleporter item.
        // The bot walks onto the pad and the GAME teleports it inside (via
        // Teleporter.OnMoveOver) — there is no custom teleport here, unlike
        // moongates. When the bot vanishes into the dungeon we convert it to
        // a DungeonCrawler.
        //
        //   _dungeonEntry      — current destination is such an entrance.
        //   _dungeonEntryTile  — the teleporter tile (the arrival coord).
        //   _dungeonEntryArmed — bot has reached the pad's vicinity; a sudden
        //                        jump away from it now means "teleported in".
        //   _dungeonEntryWalking — final approach is driving the bot straight
        //                        onto the exact pad tile (range 0).
        private bool _dungeonEntry;
        private Point3D _dungeonEntryTile;
        private bool _dungeonEntryArmed;
        private DateTime _dungeonEntryArmedAt;
        private bool _dungeonEntryWalking;
        // Busy entrances queue: another bot standing ON the pad tile keeps
        // this one from stepping onto it. Extend the timeout a few times
        // while that's the case instead of abandoning the whole trip.
        private int _dungeonEntryWaits;
        private const int DungeonEntryMaxWaits = 3;

        // A single Move can step at most one tile; a jump larger than this in
        // one move/tick can only be the teleporter firing.
        private const int DungeonEntryJump = 20;
        private const int DungeonEntryArmRange = 3;
        // If the bot reaches the pad but never gets teleported (inactive
        // teleporter, or a CanTeleport gate like combat/criminal), give up
        // after this long rather than loiter on the tile forever.
        private static readonly TimeSpan DungeonEntryTimeout = TimeSpan.FromSeconds(20);

        // Magic travel (Recall / Gate Travel) — true while the cast +
        // teleport sequence runs. The Traveler freezes and waits; the
        // sequence ends by attaching a fresh Traveler on the far side,
        // which detaches this one.
        private bool _magicTravelPending;

        // The trip's REAL destination while the route is temporarily aimed
        // at a moongate (island escape, long-haul shortcut, marooned
        // rescue). At the gate the bot ALWAYS steps through, exits at the
        // gate nearest this destination, and the far-side Traveler resumes
        // toward it. Null = the current DestinationName is the real trip.
        private string _gateResumeDestination;

        // Pack-animal logistics (gatherers). The stables is the beast's
        // real anchor: a miner/lumberjack heading to a shift detours to
        // the stables nearest the SITE first, leads the beast out, and
        // walks on with it; after the delivery it walks the beast back
        // and stables it. _stablePickupResume holds the site to continue
        // to after the pickup; _stableDropoff marks the walk back.
        private string _stablePickupResume;
        private bool _stableDropoff;
        private const int StablesMaxDetour = 300;

        // One-shot override for the next destination pick. Mid-arrival
        // redirects (stables pickup/drop-off) must go through the FULL
        // PickNewDestination reset — hand-planting DestinationName +
        // PlanPath leaves the arrival/loiter state armed, and the loiter
        // timer re-picks over the redirect seconds later.
        private string _forcedNextDestination;

        // Re-entrancy guard for PlanPath's salvage retries (unreachable
        // destination -> pick a reachable one -> re-plan). One level only.
        private int _planDepth;

        // Long-haul gate shortcut: walking trips at least this long
        // (straight-line tiles) consider the moongate network instead...
        private const int GateShortcutMinDistance = 200;
        // ...and this fraction of them actually take it (the rest walk,
        // keeping some long-haul foot traffic on the roads).
        private const double GateShortcutChance = 0.8;

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

            // A mage with the skill and mana may skip the walk entirely.
            TryMagicTravel(bot);
        }

        // -------------------------------------------------------------------
        // TryMagicTravel — called whenever a NEW trip has just been
        // planned. Rolls whether this bot Recalls / Gates to the
        // destination instead of walking (MagicTravel gates on Magery,
        // mana, trip length, and chance). On success the Traveler freezes;
        // the sequence attaches a fresh Traveler at the far end.
        // -------------------------------------------------------------------
        private void TryMagicTravel(PlayerBot bot)
        {
            if (_hasArrived || _magicTravelPending) return;

            // Ghosts walk. (A dead Traveler is a ghost on its way to a
            // healer — Recall needs words, and the dead have none.)
            if (!bot.Alive) return;

            // A party leader walks — Recalling out from under three bots
            // mid-march would strand the group. (Members aren't Travelers,
            // but the check is cheap and future-proof.)
            if (BotPartyManager.IsInParty(bot)) return;

            // The route may be temporarily aimed at a moongate (island
            // escape / long-haul shortcut). A caster doesn't need the gate
            // dance — Recall goes straight to the trip's REAL destination,
            // ocean or no ocean.
            string destName = DestinationName;
            Point3D? coord = _finalCoord;
            var destType = _destType;
            if (_gateResumeDestination != null)
            {
                var resumeObj = DestinationCatalog.GetByName(_gateResumeDestination);
                if (resumeObj != null)
                {
                    destName = _gateResumeDestination;
                    coord    = resumeObj.ArrivalPoint ?? resumeObj.Location;
                    destType = resumeObj.Type;
                }
            }

            if (!coord.HasValue) return;

            if (MagicTravel.TryBeginTrip(bot, destName, coord.Value, destType))
            {
                _magicTravelPending = true;
                StopStepTimer();
                Log(bot, $"Traveling to '{destName}' by magic");
            }
        }

        // -------------------------------------------------------------------
        // Pick a destination name for this bot, weighted by their class.
        //
        // Prefer DestinationCatalog (real places of interest). If catalog
        // is empty, fall back to a random waypoint name. The fallback
        // also lets older save data continue to work — TravelerBehavior
        // can still route to a bare waypoint if no destinations exist.
        // -------------------------------------------------------------------
        // Set by outlaw brains (PKBehavior's patrol): never pick a town
        // destination, never cross water for one (the ferry piers and
        // moongates that carry cross-water trips sit in guard zones), and
        // rescue to wilderness instead of a moongate. A red that walks
        // into Britain is a dead red.
        public bool AvoidTowns { get; set; }

        private string PickNewDestinationName(PlayerBot bot)
        {
            // A mid-arrival redirect (stables pickup resume / beast
            // drop-off walk) owns the next trip outright — before even
            // the supply errand; the shopping can wait one leg.
            if (_forcedNextDestination != null)
            {
                var forced = _forcedNextDestination;
                _forcedNextDestination = null;
                if (DestinationCatalog.GetByName(forced) != null)
                {
                    return forced;
                }
            }

            // Low on arrows / reagents / bandages / recall scrolls? Real
            // players stopped what they were doing and went SHOPPING —
            // the errand overrides the leisure roll, and the arrival
            // handoff does the actual buying. Outlaws excepted (vendors
            // sit in guard zones — a red's supplies just run dry).
            if (!AvoidTowns &&
                BotSupplies.PickErrandDestination(bot) is string errand)
            {
                return errand;
            }

            var botNode = NearestReachableNode(bot);

            // Reroll picks that land on a gateless island (e.g. Buccaneer's
            // Den): no moongate arrives in that waypoint component, so a bot
            // elsewhere can never reach them — it just gate-hops forever
            // chasing a trip that can't complete. A bot ALREADY on such an
            // island may still pick local spots.
            string lastPick = null;
            for (int attempt = 0; attempt < (AvoidTowns ? 16 : 8); attempt++)
            {
                var dest = DestinationCatalog.PickWeighted(bot);
                if (dest == null)
                {
                    break;
                }
                if (AvoidTowns && !IsWildernessPick(dest, botNode))
                {
                    continue;
                }
                lastPick = dest.Name;
                // A gateless island is only "stranded" for a bot that
                // can't Recall — anyone with the magery or a scroll goes
                // anyway (PlanPath's no-road branch casts the trip). This
                // is what keeps Valor and Humility on the pilgrimage map.
                if (botNode == null || !IsStrandedIsland(dest, botNode.Name) ||
                    MagicTravel.CanTravel(bot))
                {
                    return MaybeStableFirst(bot, dest.Name);
                }
            }
            if (lastPick != null)
            {
                return MaybeStableFirst(bot, lastPick);
            }
            if (AvoidTowns)
            {
                var wild = PickWildRescueSpot(bot);
                if (wild != null)
                {
                    return wild.Name;
                }
            }
            return WaypointRegistry.Graph.PickRandomName();
        }

        // A destination an outlaw can visit: no city tag, a wilderness
        // type, and on the SAME landmass (a cross-water pick routes the
        // bot through a guarded ferry pier or moongate).
        private static bool IsWildernessPick(BotDestination dest, WaypointNode botNode)
        {
            if (!string.IsNullOrEmpty(dest.City))
            {
                return false;
            }
            switch (dest.Type)
            {
                case DestinationType.GatherSpot:
                case DestinationType.MiningSpot:
                case DestinationType.LumberSpot:
                case DestinationType.TreasureSite:
                case DestinationType.Shrine:
                case DestinationType.Graveyard:
                case DestinationType.Crossroads:
                case DestinationType.Bridge:
                case DestinationType.Dungeon:
                case DestinationType.DungeonEntrance:
                    break;
                default:
                    return false;
            }
            if (botNode == null || string.IsNullOrEmpty(dest.NearestWaypoint))
            {
                return true;
            }
            var graph = WaypointRegistry.Graph;
            return graph.ComponentOf(botNode.Name) ==
                   graph.ComponentOf(dest.NearestWaypoint);
        }

        // Somewhere wild on the bot's own landmass � the outlaw's rescue
        // target and last-resort destination (gather spots and dig sites
        // are always rural by construction).
        private BotDestination PickWildRescueSpot(PlayerBot bot)
        {
            var graph = WaypointRegistry.Graph;
            var botNode = NearestReachableNode(bot);
            int myComp = botNode != null ? graph.ComponentOf(botNode.Name) : -1;

            BotDestination fallback = null;
            var pool = new List<BotDestination>();
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.GatherSpot &&
                    d.Type != DestinationType.TreasureSite)
                {
                    continue;
                }
                fallback ??= d;
                if (myComp >= 0 && !string.IsNullOrEmpty(d.NearestWaypoint) &&
                    graph.ComponentOf(d.NearestWaypoint) == myComp)
                {
                    pool.Add(d);
                }
            }
            if (pool.Count > 0)
            {
                return pool[Utility.Random(pool.Count)];
            }
            return fallback;
        }

        // -------------------------------------------------------------------
        // NearestReachableNode — plug the bot into the graph at a node it
        // can actually WALK to. Plain nearest-by-distance can pick a node
        // across a river or wall (straight-line distance is blind to
        // terrain), and every downstream decision then inherits the lie:
        // empty plans, bogus island reroutes, MAROONED rescues. Probe the
        // closest few candidates with a real A* check and take the first
        // that resolves; fall back to the plain nearest so behavior never
        // degrades below the old lookup.
        // -------------------------------------------------------------------
        private static WaypointNode NearestReachableNode(PlayerBot bot)
        {
            var graph = WaypointRegistry.Graph;
            var candidates = new List<WaypointNode>(4);
            graph.FindNearestNodes(bot.Location, 4, candidates);
            if (candidates.Count == 0)
            {
                return null;
            }

            foreach (var n in candidates)
            {
                int d = Math.Max(Math.Abs(n.Location.X - bot.X),
                                 Math.Abs(n.Location.Y - bot.Y));
                if (d <= 2)
                {
                    return n; // effectively standing on it
                }
                if (d > WaypointGraph.MaxLegDistance)
                {
                    break; // beyond A*'s search box — probes can't verify
                }
                if (new MovementPath(bot, n.Location).Success)
                {
                    return n;
                }
            }
            return candidates[0];
        }

        // -------------------------------------------------------------------
        // A gatherer heading out to a work site without its beast detours
        // to the stables nearest the SITE first (that's where the animal
        // lives): the pick becomes a stables trip, and the arrival handoff
        // leads the beast out and continues to the remembered site. Sites
        // with no stables in reach keep the old spawn-at-clock-in fallback.
        // -------------------------------------------------------------------
        private string MaybeStableFirst(PlayerBot bot, string destName)
        {
            if (destName == null || !BotClassHelper.IsGatherer(bot.Class) ||
                bot.PackAnimal is { Deleted: false })
            {
                return destName;
            }

            var dest = DestinationCatalog.GetByName(destName);
            if (dest == null ||
                dest.Type is not (DestinationType.GatherSpot
                    or DestinationType.MiningSpot or DestinationType.LumberSpot))
            {
                return destName;
            }

            var stables = NearestStables(dest.Location, StablesMaxDetour);
            if (stables == null)
            {
                return destName;
            }

            _stablePickupResume = destName;
            Log(bot, $"Fetching the pack animal from '{stables.Name}' before " +
                     $"the shift at '{destName}'");
            return stables.Name;
        }

        // Nearest Stables destination to a point, or null when none is
        // within maxDist (Chebyshev).
        private static BotDestination NearestStables(Point3D from, int maxDist)
        {
            BotDestination best = null;
            int bd = maxDist + 1;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.Stables)
                {
                    continue;
                }
                int dist = Math.Max(Math.Abs(d.Location.X - from.X),
                                    Math.Abs(d.Location.Y - from.Y));
                if (dist < bd)
                {
                    bd = dist;
                    best = d;
                }
            }
            return best;
        }

        // -------------------------------------------------------------------
        // True when `dest` sits in a waypoint component that (a) the bot is
        // not in, and (b) contains no moongate — unreachable on foot AND by
        // gate. Dungeon interiors match too (their components hold no gates),
        // which is correct: Travelers enter dungeons via DungeonEntrance
        // points on the surface, never by targeting interior points.
        // -------------------------------------------------------------------
        private static bool IsStrandedIsland(BotDestination dest, string botNodeName)
        {
            var graph = WaypointRegistry.Graph;

            var destWp = dest.NearestWaypoint;
            if (string.IsNullOrEmpty(destWp) || graph.Get(destWp) == null)
            {
                destWp = graph.FindNearestNode(dest.Location)?.Name;
            }
            if (destWp == null)
            {
                return false; // no graph data — let PlanPath's salvage handle it
            }

            int destComp = graph.ComponentOf(destWp);
            if (destComp < 0 || destComp == graph.ComponentOf(botNodeName))
            {
                return false;
            }

            foreach (var mg in DestinationCatalog.All)
            {
                if (mg.Type != DestinationType.Moongate)
                {
                    continue;
                }
                if (graph.ComponentOf(mg.NearestWaypoint) == destComp)
                {
                    return false;
                }
            }
            return true;
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

            // -- Frozen-position watchdog (catch-all) --
            // MUST run before the pending-flag early returns below: a
            // Recall/gate/ferry whose delayed sequence died leaves its
            // flag set forever, and this is the only path that can still
            // reach such a bot.
            if (CheckFrozenWatchdog(bot))
            {
                return;
            }

            // Mid-Recall/Gate — hold still, the words are spoken. The
            // DelayCall sequence moves the bot and swaps in a fresh
            // Traveler; this one just has to not wander off the spot.
            if (_magicTravelPending) return;

            // -- Trip progress watchdog --
            // The stuck-recovery ladder can starve forever: a hard-blocked
            // bot repaths, instantly "reaches" hop-0 (the node it's already
            // standing at), the cycle counter resets, and it paces at 'cycle
            // 1/3' for the rest of its session — 12k such events in one
            // soak. Plans lie; DISTANCE doesn't: if the bot hasn't gotten
            // meaningfully closer to its destination in TripStallLimit, the
            // trip is dead — give it up. Three dead trips without progress
            // from the same spot → rescue-teleport to the nearest moongate
            // (same precedent as the MAROONED rescue).
            if (!_hasArrived && !_moongateTripPending && bot.Alive &&
                !bot.CorpseRunPending && _finalCoord.HasValue)
            {
                int dNow = Math.Max(Math.Abs(bot.X - _finalCoord.Value.X),
                                    Math.Abs(bot.Y - _finalCoord.Value.Y));
                if (_tripBestDist == int.MaxValue)
                {
                    // Baseline for a fresh trip. Deliberately does NOT
                    // clear the stall streak � every new trip records a
                    // "best" on its first tick, and clearing here made the
                    // streak permanently unreachable (the same reset-
                    // starvation bug this watchdog exists to fix).
                    _tripStartDist = dNow;
                    _tripBestDist = dNow;
                    _tripBestAt = Core.Now;
                }
                else if (dNow + TripProgressTiles <= _tripBestDist)
                {
                    _tripBestDist = dNow;
                    _tripBestAt = Core.Now;
                    if (_tripStartDist - dNow >= 30)
                    {
                        _tripStalls = 0; // genuine travel � not a jam streak
                    }
                }
                else if (_tripBestAt != DateTime.MinValue &&
                         Core.Now - _tripBestAt > TripStallLimit)
                {
                    _tripStalls++;
                    if (_tripStalls >= 2)
                    {
                        _tripStalls = 0;
                        if (TeleportJamEscape(bot,
                                "STALLED 2 trips running", "trip_rescue"))
                        {
                            return; // escape handled replan/recall itself
                        }
                    }
                    else
                    {
                        Log(bot, $"No progress toward '{DestinationName}' in " +
                                 $"{(int)TripStallLimit.TotalMinutes} min " +
                                 $"(stall {_tripStalls}/3) — giving up the trip");
                        StuckTelemetry.Record(bot, "trip_stall",
                            $"toward '{DestinationName}'");
                    }
                    PickNewDestination(bot);
                    return;
                }
            }

            // Dungeon entry: if the bot just stepped onto the entrance
            // teleporter, the game whisked it inside — convert it to a crawler
            // before doing anything else.
            if (DungeonEntryCheck(bot)) return;

            // Ghost overdue for its res (wedged route, blocked gate...) —
            // a wandering healer finds it right here. Bounded death story.
            if (!bot.Alive && BotDeathManager.CheckGhostRescue(bot))
            {
                return; // resurrected; behavior swapped to the corpse run
            }

            // Corpse run: once the body is close, stop riding waypoints
            // and walk straight at it.
            if (bot.Alive && bot.CorpseRunPending &&
                BotDeathManager.TryCorpseApproach(bot))
            {
                return; // behavior swapped to CorpseReclaim
            }

            // Ambient/combat chatter while traveling. When the bot has
            // ARRIVED, chat is handled by DoArrivalActivity instead (with
            // destination-appropriate categories), so skip it here to
            // avoid a traveling bot saying "still on the road" while
            // standing in a bank. A DEAD traveler is a ghost walking to a
            // healer — it moans instead (the client garbles ghost speech
            // for the living, which is exactly the effect).
            if (!_hasArrived)
            {
                ChatCategories = !bot.Alive ? GhostChat
                    : bot.Combatant != null ? CombatChat
                    : AmbientChat;
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
            //
            // Ghosts have no quarrels — a dead Traveler (a ghost walking
            // to its res) never acquires a threat, so the whole defender
            // machinery is skipped while dead.
            var combatant = bot.Alive ? bot.Combatant : null;
            Mobile threat = combatant as Mobile;
            if (bot.Alive && (threat == null || threat.Deleted || !threat.Alive))
                threat = FindNearbyEnemy(bot);

            // A foe we've already proven unreachable (e.g. a giant rat walled
            // inside a building hitting us through the wall) must NOT trigger a
            // defender swap — that just makes the bot pile on the wall, give
            // up, revert, and get re-hit in an endless loop. Drop it and keep
            // traveling. (The bot-level ignore survives the behavior swap, so
            // this stays sticky across the revert.)
            if (threat != null && bot.IsUnreachable(threat))
            {
                if (bot.Combatant == threat)
                    bot.Combatant = null;
                threat = null;
            }

            if (threat != null && !threat.Deleted && threat.Alive)
            {
                // Artisans are tradespeople — they have no combat training
                // and shouldn't be fighting monsters. Their version of
                // fleeing is to abandon the route and RUN somewhere else.
                // Replans are cooldown-limited: the old every-tick abandon
                // made a glued attacker into a treadmill — the bot stood
                // in its teeth replanning every 2s instead of moving.
                if (BotClassHelper.IsArtisan(bot.Class) || bot.Class == BotClass.Crafter)
                {
                    if (ArtisanThreatResponse(bot, threat))
                    {
                        return;
                    }
                    // Replan on cooldown — sprint the current plan (the
                    // pause rolls and walk pace below are overridden while
                    // _forceRunUntil is live) instead of standing still.
                }
                else
                {
                    Log(bot, $"Attacked while traveling — switching to defender");
                    StopStepTimer();

                    var defender = new AdventurerBehavior
                    {
                        DefenderMode = true,
                        DefenderRetreatHpFraction = 0.40,
                        // Resume the REAL trip, not the portal we were routed
                        // through: after an island reroute DestinationName is
                        // the gate/ferry dock, and resuming at the portal
                        // "arrives" there with the through-trip forgotten.
                        ResumeDestination = _gateResumeDestination ?? DestinationName,
                    };
                    bot.Combatant = threat;
                    // Swapping Behavior detaches this Traveler. Return at once.
                    bot.Behavior = defender;
                    return;
                }
            }

            // -- 3. Arrived? --
            if (_hasArrived)
            {
                // Dungeon entrance: never linger or hand off. Keep walking the
                // last tiles straight onto the teleporter pad; the game does
                // the rest (DungeonEntryCheck converts us once it fires).
                if (_dungeonEntry)
                {
                    WalkOntoEntrance(bot);
                    return;
                }

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
                        StuckTelemetry.Record(bot, "leg_giveup",
                            $"'{DestinationName}' leg {_legIndex + 1}/{_plannedPath.Count} at {stuckLeg}{flag}");
                        // The whole nudge/repath ladder failed to cross this
                        // edge — give it a strike so the fleet detours around
                        // it for a while (and it shows up in the report).
                        if (_legIndex > 0 && _legIndex < _plannedPath.Count)
                        {
                            NavEdgeHealth.ReportFailure(
                                _plannedPath[_legIndex - 1], _plannedPath[_legIndex]);
                        }
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

            // -- 5. Pause occasionally -- (never mid-sprint: a fleeing
            //       artisan doesn't stop to admire the scenery)
            if (Core.Now >= _forceRunUntil)
            {
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
            }

            // -- 6. Make sure we're walking/running the current leg.
            //       StartCurrentLeg auto-picks based on leg distance.
            StartCurrentLeg(bot, forceRunning: Core.Now < _forceRunUntil);
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

            var nearest = NearestReachableNode(bot);
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
                StuckTelemetry.Record(bot, "lost_rescue",
                    $"{rDist} tiles from '{nearest.Name}'");
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
            // landmass); the gate's step-through carries it off-island and
            // CONTINUES the trip: the exit gate is chosen nearest the real
            // destination and the far-side Traveler stays aimed at it.
            if (_plannedPath == null || _plannedPath.Count == 0)
            {
                // Across water, and the bot can Recall (cast or scroll)?
                // Kal Ort Por straight to the destination — that's how
                // players crossed water. For a GATELESS island (Valor,
                // Humility, Dagger/Fire Isle) magic is the ONLY way, so a
                // capable bot always casts (`required`); for gated islands
                // (Skara, Jhelom, Moonglow) the distance-scaled roll
                // applies, and the moongate network below keeps its share
                // of the traffic. Party members excepted — a leader
                // recalling out from under its convoy strands the group.
                bool destGateless = destObj != null &&
                    IsStrandedIsland(destObj, nearest.Name);

                if (_planDepth == 0 && _finalCoord.HasValue &&
                    !BotPartyManager.IsInParty(bot) &&
                    MagicTravel.TryBeginTrip(
                        bot, DestinationName, _finalCoord.Value, _destType,
                        required: destGateless))
                {
                    _magicTravelPending = true;
                    StopStepTimer();
                    _plannedPath = new List<string>();
                    Log(bot, $"No road to '{DestinationName}' — traveling by magic");
                    return;
                }

                string bestGate = null; string bestGateName = null;
                int bestDist = int.MaxValue;
                foreach (var mg in DestinationCatalog.All)
                {
                    if (mg.Type != DestinationType.Moongate) continue;
                    // The destination itself may BE an unreachable gate —
                    // don't "reroute" to the place we can't reach.
                    if (string.Equals(mg.Name, DestinationName,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(mg.NearestWaypoint)) continue;
                    var gatePath = graph.FindPath(nearest.Name, mg.NearestWaypoint);
                    if (gatePath == null || gatePath.Count == 0) continue; // unreachable gate
                    int d = Math.Max(Math.Abs(mg.Location.X - bot.X),
                                     Math.Abs(mg.Location.Y - bot.Y));
                    if (d < bestDist)
                    { bestDist = d; bestGate = mg.NearestWaypoint; bestGateName = mg.Name; }
                }

                // (Ferries removed — not a T2A thing. Cross-water trips go
                // through the moongate network or Recall.) A GATELESS
                // destination never takes the gate route — no exit lands
                // on its island, so the trip would churn gate-to-gate
                // forever; a bot that couldn't recall above falls through
                // to the salvage instead and goes somewhere reachable.
                if (bestGate != null && !destGateless)
                {
                    Log(bot, $"Destination '{DestinationName}' unreachable by foot " +
                             $"(across water) — routing to moongate '{bestGateName}' " +
                             $"(wp '{bestGate}') to gate toward it");
                    // Become a moongate-bound bot: retarget the route AND the
                    // destination identity, so on arrival the Moongate step-
                    // through branch fires instead of a stale Healer/vendor
                    // handoff — but REMEMBER the real trip so the gate
                    // continues it instead of scattering the bot randomly.
                    _gateResumeDestination = DestinationName;
                    DestinationName = bestGateName;
                    _destType = DestinationType.Moongate;
                    routeTargetWaypoint = bestGate;
                    var gateObj = DestinationCatalog.GetByName(bestGateName);
                    _finalCoord = gateObj != null
                        ? (gateObj.ArrivalPoint ?? gateObj.Location)
                        : (Point3D?)null;
                    _plannedPath = graph.FindPath(nearest.Name, routeTargetWaypoint);
                }
                else if (_planDepth == 0)
                {
                    // No reachable gate either — the BOT's side is the
                    // gateless one (a crawler that climbed out onto Dagger
                    // Isle, say). The earlier magic attempt may have
                    // declined by CHANCE; with no gate it's mandatory now —
                    // a bot holding a recall scroll doesn't wade home.
                    if (_finalCoord.HasValue && !BotPartyManager.IsInParty(bot) &&
                        MagicTravel.TryBeginTrip(
                            bot, DestinationName, _finalCoord.Value, _destType,
                            required: true))
                    {
                        _magicTravelPending = true;
                        StopStepTimer();
                        _plannedPath = new List<string>();
                        Log(bot, $"Stranded with no gate — recalling to '{DestinationName}'");
                        return;
                    }

                    // Salvage 1: pick a destination we CAN walk to and
                    // re-plan toward that instead.
                    var salvage = PickReachableDestination(bot, nearest.Name);
                    if (salvage != null)
                    {
                        Log(bot, $"Destination '{DestinationName}' unreachable and no " +
                                 $"gate in reach — retargeting to reachable '{salvage}'");
                        DestinationName = salvage;
                        _gateResumeDestination = null;
                        _planDepth++;
                        PlanPath(bot);
                        _planDepth--;
                        return;
                    }

                    // Salvage 2: truly marooned (a waypoint pocket with no
                    // gate and no reachable destination — a data hole).
                    // Rescue-teleport to the nearest moongate and let the
                    // gate network carry the trip on; matches the existing
                    // LOST-rescue precedent above.
                    var rescueGate = AvoidTowns
                        ? PickWildRescueSpot(bot)
                        : NearestMoongate(bot);
                    if (rescueGate != null)
                    {
                        Log(bot, $"MAROONED — no reachable destination or gate from here; " +
                                 $"rescue-teleporting to moongate '{rescueGate.Name}'");
                        StuckTelemetry.Record(bot, "marooned_rescue",
                            $"toward '{DestinationName}' → {rescueGate.Name}");
                        _gateResumeDestination = DestinationName;
                        bot.MoveToWorld(rescueGate.ArrivalPoint ?? rescueGate.Location, bot.Map);
                        DestinationName = rescueGate.Name;
                        _planDepth++;
                        PlanPath(bot);
                        _planDepth--;
                        return;
                    }
                    // No moongates authored at all — fall through to the
                    // legacy empty-path handling below.
                }
            }

            // ----- long-haul gate shortcut --------------------------------
            // Even when a walking path EXISTS, a very long haul goes faster
            // through the moongate network: walk to the nearest reachable
            // gate, step through to the gate nearest the destination, and
            // walk the last stretch. Only taken when the two gate legs are
            // together much shorter than the direct walk.
            if (_plannedPath != null && _plannedPath.Count > 0 &&
                _destType != DestinationType.Moongate &&
                _gateResumeDestination == null && _finalCoord.HasValue &&
                !BotPartyManager.IsInParty(bot)) // party leaders march on foot
            {
                int walkDist = Math.Max(Math.Abs(_finalCoord.Value.X - bot.X),
                                        Math.Abs(_finalCoord.Value.Y - bot.Y));
                if (walkDist >= GateShortcutMinDistance &&
                    Utility.RandomDouble() < GateShortcutChance)
                {
                    BotDestination entry = null, exit = null;
                    int entryDist = int.MaxValue, exitDist = int.MaxValue;
                    foreach (var mg in DestinationCatalog.All)
                    {
                        if (mg.Type != DestinationType.Moongate) continue;
                        if (string.IsNullOrEmpty(mg.NearestWaypoint)) continue;

                        int dDest = Math.Max(Math.Abs(mg.Location.X - _finalCoord.Value.X),
                                             Math.Abs(mg.Location.Y - _finalCoord.Value.Y));
                        if (dDest < exitDist)
                        {
                            exitDist = dDest;
                            exit = mg;
                        }

                        int dMe = Math.Max(Math.Abs(mg.Location.X - bot.X),
                                           Math.Abs(mg.Location.Y - bot.Y));
                        if (dMe < entryDist)
                        {
                            var gp = graph.FindPath(nearest.Name, mg.NearestWaypoint);
                            if (gp != null && gp.Count > 0)
                            {
                                entryDist = dMe;
                                entry = mg;
                            }
                        }
                    }

                    if (entry != null && exit != null && entry != exit &&
                        entryDist + exitDist < walkDist / 2)
                    {
                        Log(bot, $"Long haul to '{DestinationName}' ({walkDist} tiles) — " +
                                 $"taking the moongate at '{entry.Name}' instead " +
                                 $"(gate legs {entryDist}+{exitDist} tiles)");
                        _gateResumeDestination = DestinationName;
                        DestinationName = entry.Name;
                        _destType = DestinationType.Moongate;
                        routeTargetWaypoint = entry.NearestWaypoint;
                        _finalCoord = entry.ArrivalPoint ?? entry.Location;
                        _plannedPath = graph.FindPath(nearest.Name, routeTargetWaypoint);
                    }
                }
            }
            // Dungeon entrance: a normal destination the bot walks to, whose
            // arrival tile sits on a real in-game Teleporter. The bot walks
            // onto the pad and the GAME teleports it inside (no custom
            // teleport); DungeonEntryCheck then converts it to a crawler.
            _dungeonEntry        = false;
            _dungeonEntryArmed   = false;
            _dungeonEntryWalking = false;
            if ((_destType == DestinationType.DungeonEntrance ||
                 _destType == DestinationType.Dungeon) && _finalCoord.HasValue)
            {
                var de = DestinationCatalog.GetByName(DestinationName);
                if (de != null && !string.IsNullOrEmpty(de.Dungeon))
                {
                    _dungeonEntry     = true;
                    _dungeonEntryTile = _finalCoord.Value;
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

        // -------------------------------------------------------------------
        // PickReachableDestination — roll the class-weighted catalog a few
        // times and return the first candidate with a walkable path from
        // fromNode. Null if nothing reachable turned up (marooned).
        // -------------------------------------------------------------------
        private string PickReachableDestination(PlayerBot bot, string fromNode)
        {
            var graph = WaypointRegistry.Graph;

            for (int i = 0; i < 8; i++)
            {
                var cand = PickNewDestinationName(bot);
                if (string.IsNullOrEmpty(cand) ||
                    string.Equals(cand, DestinationName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string wp = null;
                var cObj = DestinationCatalog.GetByName(cand);
                if (cObj != null)
                {
                    wp = cObj.NearestWaypoint;
                    if (string.IsNullOrEmpty(wp) || graph.Get(wp) == null)
                    {
                        var n = graph.FindNearestNode(cObj.ArrivalPoint ?? cObj.Location);
                        wp = n?.Name;
                    }
                }
                else if (graph.Get(cand) != null)
                {
                    wp = cand;   // bare waypoint destination
                }

                if (wp == null) continue;

                var path = graph.FindPath(fromNode, wp);
                if (path != null && path.Count > 0)
                {
                    return cand;
                }
            }

            return null;
        }

        // Nearest moongate destination by straight-line distance.
        // Reachability deliberately NOT required — this is the marooned
        // rescue, where nothing is reachable by definition.
        private static BotDestination NearestMoongate(PlayerBot bot)
        {
            BotDestination best = null;
            int bestDist = int.MaxValue;
            foreach (var mg in DestinationCatalog.All)
            {
                if (mg.Type != DestinationType.Moongate) continue;
                int d = Math.Max(Math.Abs(mg.Location.X - bot.X),
                                 Math.Abs(mg.Location.Y - bot.Y));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = mg;
                }
            }
            return best;
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
            // Dungeon entrances don't drift to "near" the coord — they must
            // step exactly ONTO the teleporter pad. WalkOntoEntrance (driven
            // from the arrived branch in Tick) handles that; don't start the
            // ordinary drift/field approach here.
            if (_dungeonEntry) { StopStepTimer(); return; }

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
        //   Wait   — light activity 3-6 min, then new destination.
        //   Wander — leave again immediately.
        // -------------------------------------------------------------------
        private void HandleArrival(PlayerBot bot)
        {
            // A ghost completing its walk to a healer/shrine: resurrect
            // here and start the corpse run. (Only the death flow ever
            // creates a dead Traveler.)
            if (!bot.Alive)
            {
                StopStepTimer();
                BotDeathManager.OnTravelerArrival(bot);
                return;
            }

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
                // Handoffs only when this Traveler IS the bot's active
                // behavior. An INTERNAL traveler (PKBehavior's patrol
                // drives one) must never swap the bot's brain — a red
                // arriving at a graveyard was retiring into a friendly
                // Adventurer, and a dig-site arrival made it a treasure
                // hunter mid-prowl.
                if (bot.Behavior == this && TryHandoffToDestinationBehavior(bot))
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
                    // "Indefinitely until the lifecycle moves it" in
                    // practice parked 40% of arrivals at '· arriving' for
                    // entire sessions — the status page read as a stuck-bot
                    // epidemic. Wait is now just a LONG linger.
                    var wait = TimeSpan.FromSeconds(Utility.RandomMinMax(180, 360));
                    if (Core.Now - _arrivedAt > wait)
                    {
                        PickNewDestination(bot);
                    }
                    else
                    {
                        DoArrivalActivity(bot);
                    }
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
                || type == DestinationType.GatherSpot   // wilderness work site — nothing to loiter for
                || type == DestinationType.MiningSpot
                || type == DestinationType.LumberSpot
                || type == DestinationType.TreasureSite // dig site — dig or leave
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
                // A bot that was ROUTED here to continue a longer trip
                // (island escape, long-haul shortcut) ALWAYS steps through
                // — the gate is the whole reason it came. A bot that
                // picked the gate as a destination in its own right
                // usually steps through, sometimes just has a look.
                double useChance = _gateResumeDestination != null ? 1.0 : 0.80;
                if (Utility.RandomDouble() < useChance)
                {
                    if (MoongateTravel.BeginTrip(bot, DestinationName,
                            _gateResumeDestination))
                    {
                        Log(bot, $"Stepping through the moongate at " +
                                 $"'{DestinationName}'" +
                                 (_gateResumeDestination != null
                                     ? $" toward '{_gateResumeDestination}'"
                                     : ""));
                        // BeginTrip teleports + swaps behavior after a
                        // short delay. Until then, freeze this Traveler so
                        // it doesn't walk the bot off the gate.
                        _moongateTripPending = true;
                        return true;
                    }
                }

                // Gate travel couldn't start (single-gate world). A routed
                // bot must not camp a gate that goes nowhere — drop the
                // resume and start a fresh trip instead.
                if (_gateResumeDestination != null)
                {
                    Log(bot, $"Moongate at '{DestinationName}' goes nowhere — " +
                             $"abandoning trip to '{_gateResumeDestination}'");
                    _gateResumeDestination = null;
                    PickNewDestination(bot);
                    return true;
                }

                // Didn't roll the chance, or only one gate exists — fall
                // through; the bot just lingers at the gate like any other
                // non-handoff destination.
                return false;
            }

            // (Ferry boarding removed — docks are just piers now: fisherman
            // stations and scenery, exactly what a T2A dock was.)

            // Stables: the pack animal's anchor. A gatherer passing
            // through on the way to a shift leads its beast out and walks
            // on; one returning from a delivery stables the beast here.
            if (_destType == DestinationType.Stables)
            {
                if (_stableDropoff)
                {
                    _stableDropoff = false;
                    if (bot.PackAnimal is { Deleted: false } dropBeast)
                    {
                        // The era ritual, commands only — park the beast,
                        // then say it to the stablemaster. It stands
                        // through the exchange and goes into the pens.
                        BotScene.Play(
                            (0.0, bot, $"{dropBeast.Name} stay"),
                            (1.8, bot, "vendor stable"));
                        Timer.DelayCall(TimeSpan.FromSeconds(3.0),
                            () => BotPackAnimals.Release(bot));
                        Log(bot, "Pack animal stabled after the haul");
                    }
                    // fall through — linger at the stables like any stop
                }

                // Tamers live at the stables counter: one arriving mounted
                // sometimes boards the horse ("vendor stable", hops off, the
                // horse goes to the pens); one on foot usually claims a ride
                // out. Same spoken ritual as everyone else, then the normal
                // stables visit continues.
                if (bot.Class == BotClass.Tamer && !_stableDropoff &&
                    _stablePickupResume == null)
                {
                    if (bot.Mounted && Utility.RandomDouble() < 0.40)
                    {
                        BotScene.Play((0.0, bot, "vendor stable"));
                        Timer.DelayCall(TimeSpan.FromSeconds(1.8),
                            () => BotMountHelper.DismountAndDelete(bot));
                    }
                    else if (!bot.Mounted && Utility.RandomDouble() < 0.60)
                    {
                        BotScene.Play((0.0, bot, "vendor claim"));
                        Timer.DelayCall(TimeSpan.FromSeconds(1.8),
                            () => BotMountHelper.TryMountRandom(bot));
                    }
                }

                if (_stablePickupResume != null)
                {
                    var resume = _stablePickupResume;
                    _stablePickupResume = null;
                    if (DestinationCatalog.GetByName(resume) != null)
                    {
                        // Say it to the stablemaster FIRST — the beast
                        // comes out of the pens and SpawnFor's follow
                        // order ("Bessie follow me") lands right after.
                        BotScene.Play((0.0, bot, "vendor claim"));
                        if (BotPackAnimals.SpawnFor(bot) != null)
                        {
                            Log(bot, $"Pack animal in tow — on to '{resume}'");
                            _forcedNextDestination = resume;
                            PickNewDestination(bot);
                            return true; // trip continues; no visit at the stables
                        }
                    }
                    // beast or site fell through — carry on as a normal stop
                }
            }

            // Dungeon entrances are no longer handled here. A dungeon entrance
            // is an ordinary destination whose arrival tile sits on a real
            // teleporter; the bot walks onto it, the game teleports it inside,
            // and DungeonEntryCheck converts it to a crawler. There is no
            // custom teleport/freeze to roll for at arrival.

            string targetBehavior = null;
            double chance = 0.0;
            int visitMinMinutes = 5;
            int visitMaxMinutes = 15;

            // Artisan at its own station: a Smith/Tailor/Fisherman that has
            // arrived at the destination type it works (Smith->Forge,
            // Tailor->VendorTailor, Fisherman->Dock) settles in to "work"
            // there. High chance and a long visit — an artisan stays at its
            // station, it doesn't just pass through. Only the matching class
            // converts, so a fisherman never "works" a forge and nobody but
            // an artisan ever becomes a crafter. Set the handoff variables
            // and let the shared build block below construct the behavior.
            // A loaded gatherer arriving in town: deliver the haul first
            // (scene at a crafter if one's working nearby, else a bank
            // deposit) — then fall through to the ordinary handoff rolls.
            if (bot.HaulPending && BotClassHelper.IsGatherer(bot.Class) &&
                _destType is DestinationType.Bank or DestinationType.Forge
                          or DestinationType.VendorSmith
                          or DestinationType.VendorCarpenter)
            {
                BotEconomy.DeliverMaterials(bot, _destType);

                // The unloaded beast goes back where it lives: walk it to
                // the stables and put it away. Only when no stables is in
                // reach does it get turned loose offscreen like before.
                if (bot.PackAnimal is { Deleted: false } beast)
                {
                    var stables = NearestStables(bot.Location, StablesMaxDetour);
                    if (stables != null)
                    {
                        _stableDropoff = true;
                        Log(bot, $"Leading the pack animal back to '{stables.Name}'");
                        _forcedNextDestination = stables.Name;
                        PickNewDestination(bot);
                        return true; // the walk to the stables is the next trip
                    }

                    bot.PackAnimal = null;
                    Timer.DelayCall(TimeSpan.FromSeconds(8), () =>
                    {
                        if (!beast.Deleted)
                        {
                            beast.Delete();
                        }
                    });
                }
            }

            // Arrived somewhere that sells what we're short of (an errand
            // destination, or just passing a vendor while low) — buy it,
            // visibly. No-op when nothing is needed.
            BotSupplies.TryRestockAtArrival(bot, _destType);

            // A dig site is only ever a destination because the treasure-
            // hunt manager sent this bot here (weight 0 in the roll) —
            // start digging.
            if (_destType == DestinationType.TreasureSite)
            {
                targetBehavior  = "TreasureHunter";
                chance          = 1.0;
                visitMinMinutes = 10;
                visitMaxMinutes = 12; // the behavior restamps its own window
            }
            // A gatherer arriving at a wilderness work site clocks in.
            // (Typed sites only ever attract their own class — the weight
            // table zeroes the cross-class roll.)
            //
            // The last-leg drift is short and a wilderness waypoint sits on
            // the road, so this handoff routinely fires OUTSIDE the painted
            // site. That's fine — GathererBehavior walks itself in and swings
            // nothing until it's inside — but a bot that stopped half a map
            // away was never really routed here, so it doesn't clock in.
            else if (BotClassHelper.IsGatherer(bot.Class) &&
                _destType is DestinationType.GatherSpot or DestinationType.MiningSpot
                          or DestinationType.LumberSpot)
            {
                var site = ZoneRegistry.AreaForDestination(
                    DestinationName, _finalCoord ?? bot.Location);
                if (site != null && !site.Contains(bot.X, bot.Y) &&
                    !bot.InRange(new Point3D(site.CenterX, site.CenterY, bot.Z), GatherWalkInRange))
                {
                    Log(bot, $"No Gatherer handoff at '{DestinationName}' — stopped " +
                             $"outside the site and too far to walk in");
                    return false;
                }
                targetBehavior  = "Gatherer";
                chance          = 0.95;
                visitMinMinutes = 4;
                visitMaxMinutes = 8;
            }
            else if (BotClassHelper.IsArtisan(bot.Class) &&
                BotClassHelper.StationFor(bot.Class) == _destType)
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
                    // Mostly ordinary sitters, but every bank develops its
                    // street life: the beggar and the lost newbie (IDEAS
                    // 1.5) are rare arrival outcomes of the same roll.
                    double who = Utility.RandomDouble();
                    targetBehavior = who < 0.08 ? "Beggar"
                                   : who < 0.15 ? "Newbie"
                                   : "BankSitter";
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

                case DestinationType.Healer:
                case DestinationType.Inn:
                case DestinationType.Stables:
                case DestinationType.Shrine:
                case DestinationType.Tavern:
                    // Non-vendor stops used to fall through with nothing to
                    // do � bots stood at healers/stables looking broken.
                    // Now they VISIT: themed lines/actions for a short
                    // window (shrines chant their virtue mantra), then on.
                    targetBehavior  = "Visitor";
                    chance          = 0.85;
                    visitMinMinutes = 1;
                    visitMaxMinutes = 3;
                    break;

                default:
                    return false;
                }
            }

            if (targetBehavior == null) return false;
            if (Utility.RandomDouble() > chance) return false;

            // Build the visit behavior and stamp its expiry.
            var visit = BehaviorRegistry.Create(targetBehavior);
            if (visit == null) return false;
            if (visit is VisitorBehavior vb)
            {
                vb.ConfigureFor(_destType, DestinationName);
            }
            // The gatherer needs to know WHICH site it was sent to: the
            // painted polygon is the mine, and it only works inside it.
            if (visit is GathererBehavior gb)
            {
                gb.SiteName = DestinationName;
            }
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

        // -------------------------------------------------------------------
        // Dungeon entry (walk-onto-a-real-teleporter)
        //
        // Called from both the decision tick and the step timer. Once the bot
        // is in the entrance pad's vicinity it "arms"; a subsequent sudden
        // jump away from the pad means the game's Teleporter just carried it
        // inside, so we convert it to a DungeonCrawler. A safety timeout gives
        // up if the pad never fires (inactive, or a CanTeleport gate).
        //
        // Returns true if it handled the bot (converted or bailed) and the
        // caller must return immediately.
        // -------------------------------------------------------------------
        private bool DungeonEntryCheck(PlayerBot bot)
        {
            if (!_dungeonEntry) return false;

            int dist = (int)bot.GetDistanceToSqrt(_dungeonEntryTile);

            if (!_dungeonEntryArmed)
            {
                if (dist <= DungeonEntryArmRange)
                {
                    _dungeonEntryArmed   = true;
                    _dungeonEntryArmedAt = Core.Now;
                    _dungeonEntryWaits   = 0;
                }
                return false;
            }

            // Armed and now far from the pad in a single move/tick → the
            // teleporter fired and moved us into the dungeon.
            if (dist > DungeonEntryJump)
            {
                EnterDungeonAsCrawler(bot);
                return true;
            }

            // On/near the pad but it never teleported us — don't loiter.
            if (Core.Now - _dungeonEntryArmedAt > DungeonEntryTimeout)
            {
                // Someone else is standing ON the pad tile (boot cohorts pile
                // up at popular entrances) — wait our turn a few rounds
                // before writing the pad off.
                if (_dungeonEntryWaits < DungeonEntryMaxWaits &&
                    PadOccupiedByOther(bot, _dungeonEntryTile))
                {
                    _dungeonEntryWaits++;
                    _dungeonEntryArmedAt = Core.Now;
                    Log(bot, $"Dungeon entrance '{DestinationName}' pad is occupied — " +
                             $"waiting ({_dungeonEntryWaits}/{DungeonEntryMaxWaits})");
                    return false;
                }

                Log(bot, $"Dungeon entrance '{DestinationName}' didn't teleport " +
                         $"(inactive pad?) — picking a new destination");
                StuckTelemetry.Record(bot, "entry_timeout", DestinationName);
                PickNewDestination(bot);
                return true;
            }

            return false;
        }

        // Is a different live mobile standing exactly on the teleporter tile?
        internal static bool PadOccupiedByOther(PlayerBot bot, Point3D pad)
        {
            if (bot?.Map == null || bot.Map == Map.Internal)
            {
                return false;
            }
            foreach (var m in bot.Map.GetMobilesInRange(pad, 0))
            {
                if (m != bot && !m.Deleted && m.Alive)
                {
                    return true;
                }
            }
            return false;
        }

        // Drive the bot the last few tiles straight onto the teleporter pad.
        // The follower targets the exact tile (range applied in StepOnce); the
        // game teleports the bot the instant it steps on.
        private void WalkOntoEntrance(PlayerBot bot)
        {
            if (!_dungeonEntryWalking || _follower == null)
            {
                _follower = new PathFollower(bot, _dungeonEntryTile);
                _dungeonEntryWalking = true;
            }
            EnsureStepTimer(bot, running: false);
        }

        // The teleporter carried the bot into the dungeon. Hand off to a
        // DungeonCrawler with no preset context: it derives its dungeon +
        // level from the nearest interior point where it landed (see
        // DungeonCrawlerBehavior.TryRecoverContext), and falls back to an
        // ordinary wilderness hunter if it landed somewhere unmapped.
        private void EnterDungeonAsCrawler(PlayerBot bot)
        {
            StopStepTimer();
            Log(bot, $"Teleported into a dungeon via '{DestinationName}' — becoming a crawler");
            bot.Behavior = new DungeonCrawlerBehavior();
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
            _magicTravelPending = false;
            // Fresh trip, fresh progress watchdog (the stall STREAK counter
            // deliberately survives — three dead trips in a row from the
            // same wedge is what triggers the gate rescue).
            _tripBestDist = int.MaxValue;
            _tripBestAt = Core.Now;
            _gateResumeDestination = null;
            _dungeonEntry = false;
            _dungeonEntryArmed = false;
            _dungeonEntryWalking = false;
            _nextIdleTurn = DateTime.MinValue;
            _lastLoggedLeg = null;
            PlanPath(bot);

            // A fresh trip is another chance to Recall/Gate instead of walk.
            TryMagicTravel(bot);
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
        // Consecutive NudgeAway calls where not one of the 8 directions
        // produced a step — the physically-wedged detector.
        private int _hopelessNudges;

        // Trip progress watchdog (see Tick): closest the bot has been to
        // _finalCoord this trip, when that record was set, and how many
        // trips in a row died without progress.
        private static readonly TimeSpan TripStallLimit = TimeSpan.FromMinutes(3);
        private const int TripProgressTiles = 5;
        private int _tripBestDist = int.MaxValue;
        private int _tripStartDist = int.MaxValue;
        private DateTime _tripBestAt = DateTime.MinValue;
        private int _tripStalls;

        // Frozen-position watchdog (see Tick): the catch-all beneath every
        // other rescue in this file. Trip/leg watchdogs all watch TRIP
        // state, and each has holes — a Recall/gate/ferry whose DelayCall
        // sequence dies leaves its pending flag set forever, and a
        // bare-waypoint trip has no _finalCoord so the trip watchdog never
        // arms. Position can't lie: a live bot that hasn't moved more than
        // FrozenMoveTiles from its anchor in FrozenLimit gets every hold
        // flag cleared and a fresh destination; still rooted a full window
        // later → teleport rescue.
        private static readonly TimeSpan FrozenLimit = TimeSpan.FromSeconds(60);
        private const int FrozenMoveTiles = 2;
        private Point3D _frozenAnchor;
        private DateTime _frozenAnchorAt = DateTime.MinValue;
        private bool _frozenRepicked;

        // Sprint override (set by the artisan threat response): while live,
        // pause rolls are skipped and legs run regardless of length.
        private DateTime _forceRunUntil = DateTime.MinValue;


        // Artisan threat response state — how many times in a row the route
        // was abandoned under attack (streak forgiven after 90s of calm),
        // and the replan cooldown that forces MOVEMENT between abandons.
        private int _artisanAbandonStreak;
        private DateTime _artisanCalmAt = DateTime.MinValue;
        private DateTime _artisanNextReplan = DateTime.MinValue;

        // Returns true if it acted (repicked or teleported) and Tick must
        // return immediately.
        private bool CheckFrozenWatchdog(PlayerBot bot)
        {
            int moved = Math.Max(Math.Abs(bot.X - _frozenAnchor.X),
                                 Math.Abs(bot.Y - _frozenAnchor.Y));
            if (_frozenAnchorAt == DateTime.MinValue || moved > FrozenMoveTiles)
            {
                _frozenAnchor = bot.Location;
                _frozenAnchorAt = Core.Now;
                _frozenRepicked = false;
                return false;
            }

            // Standing still is legitimate while dead (ghost rescue owns
            // that), on a corpse run (death manager owns that), or in the
            // post-arrival linger/wait activity (HandleArrival bounds it).
            // Keep the timer parked so a legit stand doesn't count toward
            // the frozen window.
            if (!bot.Alive || bot.CorpseRunPending ||
                (_hasArrived && !_moongateTripPending && !_magicTravelPending &&
                 !_dungeonEntry && !_isApproaching && !_isDrifting))
            {
                _frozenAnchorAt = Core.Now;
                return false;
            }

            if (Core.Now - _frozenAnchorAt <= FrozenLimit)
            {
                return false;
            }

            if (!_frozenRepicked)
            {
                _frozenRepicked = true;
                _frozenAnchorAt = Core.Now; // fresh window for stage 2
                Log(bot, $"FROZEN {(int)FrozenLimit.TotalSeconds}s at {bot.Location} " +
                         $"(dest '{DestinationName}', arrived={_hasArrived}, " +
                         $"magic={_magicTravelPending}, gate={_moongateTripPending}, " +
                         $"entry={_dungeonEntry}) — clearing holds, picking a new destination");
                StuckTelemetry.Record(bot, "frozen_repick",
                    $"dest '{DestinationName}' arrived={_hasArrived} " +
                    $"magic={_magicTravelPending} gate={_moongateTripPending}");
                StopStepTimer();
                _isApproaching = false;
                _isDrifting = false;
                // PickNewDestination clears _magicTravelPending,
                // _moongateTripPending, _dungeonEntry and plans fresh.
                PickNewDestination(bot);
                return true;
            }

            // A full second window without a single step even after the
            // forced repick — the spot itself is the problem. Recall out
            // (or teleport, for a bot with no magic and no scroll; wilds
            // for outlaws, random gate for everyone else).
            _frozenAnchor = bot.Location;
            _frozenAnchorAt = Core.Now;
            _frozenRepicked = false;
            StopStepTimer();
            if (!TeleportJamEscape(bot, "STILL frozen after forced repick", "frozen_rescue"))
            {
                PickNewDestination(bot);
            }
            return true;
        }

        // -------------------------------------------------------------------
        // TeleportJamEscape — the shared jam escape under the trip-stall,
        // frozen-watchdog and artisan-pinned rescues. Outlaws go to the
        // wilds (gates sit in guard zones and a teleported-in red dies
        // there); everyone else to a RANDOM gate, since the nearest gate
        // re-jams a bot that is stuck AT a gate.
        //
        // The era-true exit comes first: a bot that can Recall (cast or
        // scroll) casts its way out — mantra, flash, gone — which is
        // exactly what a wedged player did. Only a bot with no magic and
        // no scroll gets the silent teleport. FULLY SELF-CONTAINED: the
        // recall path hands off a fresh Traveler by itself, the silent
        // path replans here — callers must NOT PickNewDestination after a
        // true return (a second plan would race the pending recall).
        // Returns false only when nothing is authored to rescue to.
        // -------------------------------------------------------------------
        private bool TeleportJamEscape(PlayerBot bot, string reason, string kind)
        {
            var rescue = AvoidTowns ? PickWildRescueSpot(bot) : null;
            if (rescue == null && !AvoidTowns)
            {
                var gates = MoongateTravel.AllMoongates();
                rescue = gates.Count > 0 ? gates[Utility.Random(gates.Count)] : null;
            }
            if (rescue == null)
            {
                return false;
            }

            // Record BEFORE the move so the hotspot is the jam, not the exit.
            if (MagicTravel.EmergencyEscape(bot, rescue))
            {
                Log(bot, $"{reason} — recalling out toward '{rescue.Name}'");
                StuckTelemetry.Record(bot, kind, $"{reason} → {rescue.Name} (recall)");
                _magicTravelPending = true;
                StopStepTimer();
                return true;
            }

            Log(bot, $"{reason} — rescue-teleporting to '{rescue.Name}'");
            StuckTelemetry.Record(bot, kind, $"{reason} → {rescue.Name}");
            bot.MoveToWorld(rescue.ArrivalPoint ?? rescue.Location, bot.Map);
            PickNewDestination(bot);
            return true;
        }

        // -------------------------------------------------------------------
        // ArtisanThreatResponse — an artisan under threat abandons the route
        // and RUNS. Replans are cooldown-limited so a glued attacker can't
        // turn this into a stand-still replan treadmill (between replans the
        // bot keeps moving, at a sprint), and three abandons without a calm
        // spell in between mean the attacker is pinned to us — teleport-
        // escape, same precedent as the trip-stall rescue. Returns true when
        // it consumed the tick (replanned or teleported).
        // -------------------------------------------------------------------
        private bool ArtisanThreatResponse(PlayerBot bot, Mobile threat)
        {
            bot.Combatant = null;
            _forceRunUntil = Core.Now + TimeSpan.FromSeconds(20);

            if (Core.Now > _artisanCalmAt)
            {
                _artisanAbandonStreak = 0; // left alone for a while — forgiven
            }
            _artisanCalmAt = Core.Now + TimeSpan.FromSeconds(90);

            if (Core.Now < _artisanNextReplan)
            {
                return false; // keep running the current plan
            }
            _artisanNextReplan = Core.Now + TimeSpan.FromSeconds(15);
            _artisanAbandonStreak++;

            if (_artisanAbandonStreak >= 3)
            {
                _artisanAbandonStreak = 0;
                if (TeleportJamEscape(bot,
                        $"Artisan pinned by '{threat.Name}'", "artisan_pinned"))
                {
                    return true; // escape handled replan/recall itself
                }
            }

            Log(bot, $"Artisan attacked by '{threat.Name}' while traveling — " +
                     $"abandoning route and running");
            StuckTelemetry.Record(bot, "artisan_flee", threat.Name);
            PickNewDestination(bot);
            return true;
        }

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
            if (lockedDir == null)
            {
                // Hopelessly walled in this tick. Once is often a transient
                // crowd; twice in a row means the bot is physically WEDGED —
                // teleported into rock (bad magic landing), boxed in by a
                // bank crowd, or trapped by decor. Extract it to the nearest
                // engine-valid tile; nothing else can ever free it.
                if (++_hopelessNudges >= 2)
                {
                    _hopelessNudges = 0;
                    if (BotStuckEscape.TryExtract(bot, "traveler"))
                    {
                        _follower?.ForceRepath();
                    }
                }
                return;
            }
            _hopelessNudges = 0;

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

                // Skip foes already proven unreachable (walled-in critters
                // attacking through a wall) so we don't pick one as the next
                // threat over a foe we could actually fight.
                if (bot.IsUnreachable(bc)) continue;

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

            // Per-node arrival tolerance (door/entrance waypoints can set a
            // tighter ArrivalRange in JSON). A dungeon-entry walk aims for the
            // exact pad tile (range 0) so the bot actually steps ON the
            // teleporter rather than stopping a few tiles short.
            int followRange = LegArrivalRange;
            if (_dungeonEntryWalking)
            {
                followRange = 0;
            }
            else if (_plannedPath.Count > 0 && _legIndex < _plannedPath.Count)
            {
                var curNode = WaypointRegistry.Graph.Get(_plannedPath[_legIndex]);
                if (curNode != null && curNode.ArrivalRange > 0)
                {
                    followRange = curNode.ArrivalRange;
                }
            }

            bool arrivedLeg = _follower.Follow(_running, followRange);

            // Did that step carry us onto the entrance teleporter? If so the
            // game already moved us inside — convert and stop.
            if (DungeonEntryCheck(bot)) return;

            // While walking onto the pad, skip the ordinary leg-advance/arrival
            // machinery. If we reached the tile without teleporting (inactive
            // pad), the timeout in DungeonEntryCheck eventually gives up.
            if (_dungeonEntryWalking)
            {
                if (arrivedLeg) StopStepTimer();
                _lastStepLoc = bot.Location;
                return;
            }

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
                            BotStuckEscape.SidestepAny(bot);
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
