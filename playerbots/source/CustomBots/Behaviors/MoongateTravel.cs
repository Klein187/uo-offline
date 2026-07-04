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

            var gates = AllMoongates();

            // Need at least one gate that ISN'T the one we're standing on.
            var others = new List<BotDestination>();
            foreach (var g in gates)
            {
                if (!string.Equals(g.Name, fromMoongateName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    others.Add(g);
                }
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

            // Pick the destination gate: nearest to where the trip is
            // ultimately headed, or random for a plain wander.
            BotDestination target = null;
            if (resumeCoord.HasValue)
            {
                int bestDist = int.MaxValue;
                foreach (var g in others)
                {
                    int d = Math.Max(Math.Abs(g.Location.X - resumeCoord.Value.X),
                                     Math.Abs(g.Location.Y - resumeCoord.Value.Y));
                    if (d < bestDist)
                    {
                        bestDist = d;
                        target = g;
                    }
                }
            }
            target ??= others[Utility.Random(others.Count)];
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
                // so multiple arrivals don't perfectly overlap.
                int ox = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
                int oy = Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
                int tx = target.Location.X + ox;
                int ty = target.Location.Y + oy;
                int tz = target.Location.Z;

                bot.MoveToWorld(new Point3D(tx, ty, tz), bot.Map);

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
                    var traveler = new TravelerBehavior();
                    if (resumeDestination != null)
                    {
                        traveler.DestinationName = resumeDestination;
                    }
                    bot.Behavior = traveler;
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
