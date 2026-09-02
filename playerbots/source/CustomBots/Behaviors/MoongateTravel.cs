// =========================================================================
// MoongateTravel.cs — bot moongate travel between cities.
//
// When a Traveler bot arrives at a Moongate destination, it has a chance
// to "use" the gate: step in, and emerge from a randomly chosen DIFFERENT
// moongate elsewhere in the world. This is how bots spread between cities
// (Britain <-> Trinsic, etc.) instead of being confined to one.
//
// Flow (modeled on DungeonEntry's teleport pattern):
//   1. Play moongate visuals + sound at the current gate.
//   2. Short delay — the "stepping through" beat.
//   3. Teleport the bot to a random other Moongate destination.
//   4. Play arrival visuals at the new gate.
//   5. Hand the bot a fresh Traveler with a destination in the new area,
//      so it then explores the city it arrived in.
//
// All moongates are discovered from DestinationCatalog (Type == Moongate),
// so adding a moongate destination to destinations.json automatically
// makes it a valid travel endpoint — no code change needed.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public static class MoongateTravel
    {
        // Moongate teleport visuals. 0x1FE is the standard gate-travel
        // sound; 0x3728 is a sparkle effect that reads as gate energy.
        private const int GateSoundId  = 0x1FE;
        private const int GateEffectId = 0x3728;
        private const int PlacementSpread = 2;

        private static readonly TimeSpan StepThroughDelay =
            TimeSpan.FromSeconds(2);

        // -------------------------------------------------------------------
        // How much is there to DO on the far side of each gate?
        //
        // A plain wander hop used to pick uniformly, so every gate was as
        // likely as every other. Three of the eight open onto islands with
        // almost nothing walkable from them: Moonglow reaches 4 destinations
        // of 480, Buccaneer's Den 3, Occlo 2, against 139 for any of the
        // three mainland gates. A bot landing on one of those has nothing to
        // walk to, immediately rolls somewhere off the island, and steps
        // back into the gate. In and out, forever, and with a shard this
        // size that is a permanent crowd standing on the moongate.
        //
        // Buccaneer's Den already had a hand-written exemption for exactly
        // this. It was right about the problem and too narrow: Moonglow and
        // Occlo do the same thing and nothing stopped them. So weigh every
        // gate by what a bot can actually reach from it, rather than naming
        // the bad ones one at a time.
        //
        // Weighted, not filtered. Moonglow is a real place a bot may
        // genuinely visit; it should just be rare rather than as likely as
        // Britain. On the current map that is about one hop in a hundred
        // instead of one in eight.
        // -------------------------------------------------------------------
        private static Dictionary<string, int> _reach;
        private static int _reachStamp;

        private static int OpportunityAt(BotDestination gate)
        {
            var graph = WaypointRegistry.Graph;
            if (graph == null || graph.NodeCount == 0 || gate == null ||
                string.IsNullOrEmpty(gate.NearestWaypoint))
            {
                return 1;
            }

            // Rebuild if the catalog or the graph changed underneath us
            // ([ReloadDestinations, [ReloadWaypoints).
            int stamp = DestinationCatalog.All.Count * 31 + graph.NodeCount;
            if (_reach == null || _reachStamp != stamp)
            {
                _reach = new Dictionary<string, int>();
                _reachStamp = stamp;

                var perComp = new Dictionary<int, int>();
                foreach (var d in DestinationCatalog.All)
                {
                    if (string.IsNullOrEmpty(d.NearestWaypoint))
                    {
                        continue;
                    }
                    int c = graph.ComponentOf(d.NearestWaypoint);
                    if (c < 0)
                    {
                        continue;
                    }
                    perComp.TryGetValue(c, out int n);
                    perComp[c] = n + 1;
                }

                foreach (var g in AllMoongates())
                {
                    if (string.IsNullOrEmpty(g.NearestWaypoint))
                    {
                        continue;
                    }
                    int c = graph.ComponentOf(g.NearestWaypoint);
                    _reach[g.Name] = c >= 0 && perComp.TryGetValue(c, out int n) ? n : 1;
                }
            }

            return _reach.TryGetValue(gate.Name, out int r) && r > 0 ? r : 1;
        }

        // Weighted choice: a gate onto 139 places is picked far more often
        // than one onto 2. Never zero, so nowhere becomes unreachable.
        private static BotDestination PickByOpportunity(List<BotDestination> gates)
        {
            if (gates == null || gates.Count == 0)
            {
                return null;
            }

            long total = 0;
            for (var i = 0; i < gates.Count; i++)
            {
                total += Math.Max(1, OpportunityAt(gates[i]));
            }

            if (total <= 0)
            {
                return gates[Utility.Random(gates.Count)];
            }

            long roll = Utility.RandomMinMax(0, (int)Math.Min(total - 1, int.MaxValue));
            for (var i = 0; i < gates.Count; i++)
            {
                roll -= Math.Max(1, OpportunityAt(gates[i]));
                if (roll < 0)
                {
                    return gates[i];
                }
            }
            return gates[gates.Count - 1];
        }

        // -------------------------------------------------------------------
        // Collect every Moongate destination in the catalog.
        // -------------------------------------------------------------------
        public static List<BotDestination> AllMoongates()
        {
            var gates = new List<BotDestination>();
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type == DestinationType.Moongate)
                    gates.Add(d);
            }
            return gates;
        }

        // -------------------------------------------------------------------
        // Begin a moongate trip.
        //
        // Exit gate choice:
        //   - resumeDestination given (the bot was ROUTED to this gate to
        //     continue a longer trip — off an island, or a long-haul
        //     shortcut): pick the gate CLOSEST to that destination, and
        //     hand off a Traveler still aimed at it. The trip continues.
        //   - no resumeDestination (the bot picked the gate as a
        //     destination in its own right): pick a random other gate —
        //     this is how bots spread between cities.
        //
        // Returns true if a trip was started (caller's behavior is now
        // detached and must return). Returns false if travel couldn't
        // happen (only one moongate exists, etc.) — caller proceeds normally.
        // -------------------------------------------------------------------
        public static bool BeginTrip(PlayerBot bot, string fromMoongateName,
            string resumeDestination = null)
        {
            if (bot == null || bot.Deleted || !bot.Alive) return false;
            if (bot.Map == null || bot.Map == Map.Internal) return false;

            // Every public moongate stands in a guarded town. A red that
            // steps out of one is killed on arrival, so they never take them.
            if (!RedTerritory.MayUseMoongates(bot)) return false;

            var gates = AllMoongates();

            // Need at least one gate that ISN'T the one we're standing on.
            var others = new List<BotDestination>();
            foreach (var g in gates)
            {
                if (string.Equals(g.Name, fromMoongateName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Don't send honest folk to the pirate town. Without this a
                // wandering hop lands there about one time in eight, and
                // since nothing on the island is walkable-to, every arrival
                // turns straight round - which is what grew the crowd on
                // that moongate.
                if (RedTerritory.ShouldAvoid(bot, g))
                {
                    continue;
                }

                others.Add(g);
            }
            if (others.Count == 0) return false;

            // Resolve the resume destination's coordinates, if any.
            Point3D? resumeCoord = null;
            if (!string.IsNullOrEmpty(resumeDestination))
            {
                var resumeObj = DestinationCatalog.GetByName(resumeDestination);
                if (resumeObj != null)
                {
                    resumeCoord = resumeObj.ArrivalPoint ?? resumeObj.Location;
                }
            }

            // Pick the destination gate: the one the trip can actually
            // CONTINUE from, or random for a plain wander.
            //
            // Plain nearest-coordinate exit choice loops on island
            // destinations: an island's nearest gates by coordinate are
            // often OTHER islands, so a routed bot ping-ponged between
            // them forever. Rank exits by continuation quality, using
            // waypoint components for O(1) reachability:
            //   tier 0 — resume walkable from the gate (same component):
            //            score = distance gate -> resume.
            //   tier 1 — no continuation known: coordinate distance only,
            //            heavily penalized (an exit you can walk on from
            //            always beats a scenic dead end).
            BotDestination target = null;
            if (resumeCoord.HasValue)
            {
                var graph = WaypointRegistry.Graph;
                int resumeComp = -1;
                if (graph != null && graph.NodeCount > 0)
                {
                    var resumeNode = graph.FindNearestNode(resumeCoord.Value);
                    if (resumeNode != null)
                    {
                        resumeComp = graph.ComponentOf(resumeNode.Name);
                    }
                }

                long bestScore = long.MaxValue;
                foreach (var g in others)
                {
                    long dResume = Math.Max(
                        Math.Abs(g.Location.X - resumeCoord.Value.X),
                        Math.Abs(g.Location.Y - resumeCoord.Value.Y));

                    long score;
                    int gateComp = graph != null && !string.IsNullOrEmpty(g.NearestWaypoint)
                        ? graph.ComponentOf(g.NearestWaypoint)
                        : -1;

                    if (resumeComp < 0 || gateComp < 0)
                    {
                        score = dResume; // no graph data — old behavior
                    }
                    else if (gateComp == resumeComp)
                    {
                        score = dResume; // tier 0: walk it from here
                    }
                    else
                    {
                        score = dResume + 1_000_000; // tier 1: dead end
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        target = g;
                    }
                }
            }
            target ??= PickByOpportunity(others);
            // Stale resume name that resolved to nothing — treat the trip
            // as a plain wander so the far side picks fresh.
            if (!resumeCoord.HasValue)
            {
                resumeDestination = null;
            }

            // Visuals at the departure gate.
            SafeGateEffect(bot);

            // Step-through delay, then teleport + hand off.
            Timer.DelayCall(StepThroughDelay, () =>
            {
                if (bot == null || bot.Deleted || !bot.Alive) return;

                // Place the bot at the destination gate with a small spread
                // so multiple arrivals don't perfectly overlap — VALIDATED:
                // a blind offset at a raised gate platform drops arrivals
                // onto off-Z edge tiles where they jam (Moonglow Road 1
                // was the worst stuck spot on the shard).
                var landing = target.Location;
                for (int i = 0; i < 8; i++)
                {
                    int tx = target.Location.X +
                             Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
                    int ty = target.Location.Y +
                             Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
                    int tz = bot.Map.GetAverageZ(tx, ty);
                    if (bot.Map.CanSpawnMobile(tx, ty, tz))
                    {
                        landing = new Point3D(tx, ty, tz);
                        break;
                    }
                }

                bot.MoveToWorld(landing, bot.Map);

                // Arrival visuals at the new gate.
                SafeGateEffect(bot);

                // Hand off a fresh Traveler. A resume destination keeps the
                // interrupted trip alive — the bot emerges from the gate
                // and continues toward where it was headed all along.
                // Otherwise DestinationName stays null and the Traveler
                // picks fresh on its first tick — since the bot now stands
                // at the target moongate, the nearest-waypoint routing
                // starts it exploring whatever city it arrived in.
                try
                {
                    bot.Behavior = RedTerritory.TravelBrain(bot, resumeDestination);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[MoongateTravel] {bot.Name}: handoff failed: {ex.Message}");
                }

                Console.WriteLine(
                    $"[MoongateTravel] {bot.Name}: {fromMoongateName} -> {target.Name}" +
                    (resumeDestination != null ? $" (continuing to '{resumeDestination}')" : ""));
            });

            return true;
        }

        // Play the gate sound + sparkle, swallowing any effect errors —
        // visuals are nice-to-have and must never break the trip.
        private static void SafeGateEffect(PlayerBot bot)
        {
            try
            {
                bot.PlaySound(GateSoundId);
                bot.FixedParticles(GateEffectId, 9, 32, 5008, EffectLayer.Waist);
            }
            catch { }
        }
    }
}
