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
        // Begin a moongate trip. Picks a random moongate OTHER than the one
        // named by `fromMoongateName`, teleports the bot there after a short
        // delay, and hands off a fresh Traveler aimed at the new area.
        //
        // Returns true if a trip was started (caller's behavior is now
        // detached and must return). Returns false if travel couldn't
        // happen (only one moongate exists, etc.) — caller proceeds normally.
        // -------------------------------------------------------------------
        public static bool BeginTrip(PlayerBot bot, string fromMoongateName)
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

            // Pick a random destination gate.
            var target = others[Utility.Random(others.Count)];

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

                // Hand off a fresh Traveler. Leaving DestinationName null
                // makes the Traveler pick a new destination on its first
                // tick — and since the bot is now standing at the target
                // moongate, the nearest-waypoint routing starts it
                // exploring whatever city it arrived in.
                try
                {
                    var traveler = new TravelerBehavior();
                    bot.Behavior = traveler;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[MoongateTravel] {bot.Name}: handoff failed: {ex.Message}");
                }

                Console.WriteLine(
                    $"[MoongateTravel] {bot.Name}: {fromMoongateName} -> {target.Name}");
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
