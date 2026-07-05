// =========================================================================
// FerryTravel.cs — bot boat travel between paired docks.
//
// The islands without moongates (Valor Isle, Humility Isle, later
// Honesty/Nujel'm/Occlo) are reached by "ferry": a Dock destination with
// FerryTo set names its partner dock, and a bot that walks to the terminal
// can board the boat — a short boarding beat on the pier, then it is
// carried to the partner dock and steps off with an "arrived by boat"
// line. No ship item ever exists; like MoongateTravel this is teleport
// theater, but anchored to real piers so it reads as a scheduled ferry.
//
// Flow (modeled on MoongateTravel):
//   1. Boarding line/emote at the departure pier.
//   2. Crossing delay — longer than a gate's step-through; it's a boat.
//   3. Teleport to the partner dock's arrival spot.
//   4. Arrival line, hand off a fresh Traveler (resume-aware, so a bot
//      routed here mid-trip continues toward its real destination).
//
// Pairs are data: add "FerryTo" to two Dock records in destinations.json
// and the route exists — no code change needed.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public static class FerryTravel
    {
        private const int PlacementSpread = 2;

        // Boats take a moment: long enough to read as a crossing, short
        // enough that a watcher at the far pier plausibly "saw it dock".
        private static readonly TimeSpan CrossingDelayMin = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan CrossingDelayMax = TimeSpan.FromSeconds(12);

        // Resolve the partner dock of a ferry terminal. Null when the
        // destination isn't a terminal or its pair is missing/stale.
        public static BotDestination PartnerOf(BotDestination dock)
        {
            if (dock == null || string.IsNullOrEmpty(dock.FerryTo))
            {
                return null;
            }

            var pair = DestinationCatalog.GetByName(dock.FerryTo);
            if (pair == null || pair == dock)
            {
                return null;
            }

            return pair;
        }

        // -------------------------------------------------------------------
        // Begin a ferry crossing from the named terminal dock. Returns true
        // if the trip started (caller's Traveler must freeze and let the
        // handoff happen); false when the dock isn't a ferry terminal or the
        // pair record is stale — caller proceeds like any dock arrival.
        // -------------------------------------------------------------------
        public static bool BeginTrip(PlayerBot bot, string fromDockName,
            string resumeDestination = null)
        {
            if (bot == null || bot.Deleted || !bot.Alive)
            {
                return false;
            }

            if (bot.Map == null || bot.Map == Map.Internal)
            {
                return false;
            }

            var from = DestinationCatalog.GetByName(fromDockName);
            var pair = PartnerOf(from);
            if (pair == null)
            {
                return false;
            }

            // Validate the resume name now so a stale one degrades to a
            // plain crossing instead of aiming the far-side Traveler at
            // nothing.
            if (!string.IsNullOrEmpty(resumeDestination) &&
                DestinationCatalog.GetByName(resumeDestination) == null)
            {
                resumeDestination = null;
            }

            // Boarding beat on the pier.
            var line = ChatLibrary.PickRandom("ferry_board");
            if (!string.IsNullOrEmpty(line))
            {
                bot.Say(line);
            }

            var delay = TimeSpan.FromSeconds(Utility.RandomMinMax(
                (int)CrossingDelayMin.TotalSeconds,
                (int)CrossingDelayMax.TotalSeconds));

            Timer.DelayCall(delay, () =>
            {
                if (bot == null || bot.Deleted || !bot.Alive)
                {
                    return;
                }

                // Step off at the partner pier — prefer its authored
                // arrival spot (a tile on the walkable pier apron), with a
                // small spread so multiple riders don't stack.
                var spot = pair.PickArrival();
                var basePoint = spot?.Point ?? pair.ArrivalPoint ?? pair.Location;
                int tx = basePoint.X + Utility.RandomMinMax(-PlacementSpread, PlacementSpread);
                int ty = basePoint.Y + Utility.RandomMinMax(-PlacementSpread, PlacementSpread);

                bot.MoveToWorld(new Point3D(tx, ty, basePoint.Z), bot.Map);

                var arriveLine = ChatLibrary.PickRandom("ferry_arrive");
                if (!string.IsNullOrEmpty(arriveLine))
                {
                    bot.Say(arriveLine);
                }

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
                        $"[FerryTravel] {bot.Name}: handoff failed: {ex.Message}");
                }

                Console.WriteLine(
                    $"[FerryTravel] {bot.Name}: {fromDockName} -> {pair.Name}" +
                    (resumeDestination != null ? $" (continuing to '{resumeDestination}')" : ""));
            });

            return true;
        }
    }
}
