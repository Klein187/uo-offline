// =========================================================================
// MagicTravel.cs — Recall and Gate Travel for traveling bots.
//
// A bot with real Magery doesn't walk everywhere — that's the whole point
// of the skill. When a Traveler starts a LONG trip, this rolls whether it
// travels by magic instead:
//
//   - Recall (Magery >= 40, 11 mana): mantra + cast beat, then the bot
//     vanishes in a flash and reappears at the destination. Kal Ort Por.
//   - Gate Travel (Magery >= 90, 40 mana): mantra + cast beat, then a
//     REAL pair of Moongate items opens (one here, one there), the bot
//     steps through, and the gates linger ~30s. Real players — and even
//     other bots — can hop through while they stand; the gate is a
//     genuine world object.
//
// Illusion, not simulation (same philosophy as the crafters): we don't
// run the real RecallSpell/GateTravelSpell classes — those need a marked
// rune target and the full targeting flow. We say the words of power,
// play the cast beat and effects, charge the mana, and do the move with
// the proven moongate-style DelayCall pattern. To a bystander it reads
// exactly like a player recalling out.
//
// The sequence ends by attaching a FRESH TravelerBehavior with the same
// destination. The bot lands a couple of tiles off the arrival point, so
// the new Traveler's PlanPath immediately runs the normal arrival flow
// (drift, handoff, dungeon-entrance walk-on). Nothing downstream knows
// the trip was magical.
// =========================================================================

using System;
using Server;
using Server.Items;

namespace Server.CustomBots
{
    public static class MagicTravel
    {
        // Skill and mana gates. Recall is 4th circle (11 mana) — mid-tier
        // mages own it; Gate Travel is 7th circle (40 mana), GM territory.
        public const double RecallMinMagery = 40.0;
        public const int    RecallManaCost  = 11;
        public const double GateMinMagery   = 90.0;
        public const int    GateManaCost    = 40;

        // Only trips at least this long (straight-line tiles) justify the
        // mana — short hops stay on foot so streets keep their traffic.
        public const int MinTripDistance = 80;

        // Of eligible long trips, how many actually go by magic (mages
        // still walk sometimes — an all-teleport world empties the roads)…
        public const double MagicTripChance = 0.5;
        // …and of those, how many a gate-capable mage opens a gate for.
        public const double GateShare = 0.4;

        private const int RecallSound  = 0x1FC;
        private const int GateSound    = 0x20E;
        private const int SparkleId    = 0x3728;

        // Cast beat: mantra -> effect/move. Roughly a real cast delay.
        private static readonly TimeSpan CastBeat         = TimeSpan.FromSeconds(2.0);
        // Gate only: pause between the gate opening and stepping through.
        private static readonly TimeSpan StepThroughDelay = TimeSpan.FromSeconds(1.5);
        // How long a conjured gate pair stays open for anyone to use.
        private static readonly TimeSpan GateLinger       = TimeSpan.FromSeconds(30.0);

        // -------------------------------------------------------------------
        // TryBeginTrip — roll and, if the dice land, run a magic trip to
        // destCoord. Returns true if a trip started: the calling Traveler
        // must freeze and stop stepping (the sequence detaches it when the
        // fresh Traveler attaches on the far side).
        // -------------------------------------------------------------------
        public static bool TryBeginTrip(
            PlayerBot bot, string destName, Point3D destCoord, DestinationType destType)
        {
            if (bot == null || bot.Deleted || !bot.Alive) return false;
            if (bot.Map == null || bot.Map == Map.Internal) return false;

            double magery = bot.Skills[SkillName.Magery].Base;
            if (magery < RecallMinMagery || bot.Mana < RecallManaCost) return false;

            int dist = Math.Max(Math.Abs(destCoord.X - bot.X),
                                Math.Abs(destCoord.Y - bot.Y));
            if (dist < MinTripDistance) return false;

            if (Utility.RandomDouble() >= MagicTripChance) return false;

            bool gate = magery >= GateMinMagery && bot.Mana >= GateManaCost &&
                        Utility.RandomDouble() < GateShare;

            var landing = PickLanding(destCoord, destType);

            if (gate)
            {
                BeginGateTrip(bot, destName, landing);
            }
            else
            {
                BeginRecallTrip(bot, destName, landing);
            }
            return true;
        }

        // -------------------------------------------------------------------
        // Where to land. Normal destinations: a small spread around the
        // arrival point so simultaneous arrivals don't stack.
        //
        // Dungeon entrances: NEVER land on or beside the pad — the arrival
        // tile sits on a real Teleporter, and materializing onto it would
        // skip the walk-on entry flow that arms the crawler conversion.
        // Land a handful of tiles short; the fresh Traveler walks the last
        // steps through the normal armed path.
        // -------------------------------------------------------------------
        private static Point3D PickLanding(Point3D destCoord, DestinationType destType)
        {
            if (destType == DestinationType.DungeonEntrance ||
                destType == DestinationType.Dungeon)
            {
                int ox = Utility.RandomMinMax(3, 5) * (Utility.RandomBool() ? 1 : -1);
                int oy = Utility.RandomMinMax(3, 5) * (Utility.RandomBool() ? 1 : -1);
                return new Point3D(destCoord.X + ox, destCoord.Y + oy, destCoord.Z);
            }

            return new Point3D(
                destCoord.X + Utility.RandomMinMax(-2, 2),
                destCoord.Y + Utility.RandomMinMax(-2, 2),
                destCoord.Z);
        }

        // -------------------------------------------------------------------
        // Recall — Kal Ort Por, flash, gone.
        // -------------------------------------------------------------------
        private static void BeginRecallTrip(PlayerBot bot, string destName, Point3D landing)
        {
            bot.Mana = Math.Max(0, bot.Mana - RecallManaCost);
            SayMantra(bot, "Kal Ort Por");

            Timer.DelayCall(CastBeat, () =>
            {
                if (bot == null || bot.Deleted || !bot.Alive) return;
                if (bot.Map == null || bot.Map == Map.Internal) return;

                // Departure flash where the bot stood…
                SafeEffect(bot, RecallSound);

                bot.MoveToWorld(landing, bot.Map);

                // …and an arrival flash where it lands.
                SafeEffect(bot, RecallSound);

                HandOffFreshTraveler(bot, destName, "Recall");
            });
        }

        // -------------------------------------------------------------------
        // Gate Travel — Vas Rel Por, a real gate pair opens, step through.
        // -------------------------------------------------------------------
        private static void BeginGateTrip(PlayerBot bot, string destName, Point3D landing)
        {
            bot.Mana = Math.Max(0, bot.Mana - GateManaCost);

            // Gate etiquette (IDEAS 6.2): a public gate is a public
            // service — announce it. The pair lingers ~30s and anyone
            // (players, other bots) can hop through.
            if (Utility.RandomDouble() < 0.6)
            {
                bot.Say($"gate to {destName.ToLowerInvariant()} up, hurry");
            }

            SayMantra(bot, "Vas Rel Por");

            Timer.DelayCall(CastBeat, () =>
            {
                if (bot == null || bot.Deleted || !bot.Alive) return;

                var map = bot.Map;
                if (map == null || map == Map.Internal) return;

                var origin = bot.Location;

                // A REAL gate pair — anyone nearby can use them while they
                // stand. Both dissolve after the linger window.
                Moongate here = null;
                Moongate there = null;
                try
                {
                    here = new Moongate(landing, map);
                    here.MoveToWorld(origin, map);
                    there = new Moongate(origin, map);
                    there.MoveToWorld(landing, map);

                    Effects.PlaySound(origin, map, GateSound);
                    Effects.PlaySound(landing, map, GateSound);
                }
                catch { }

                Timer.DelayCall(GateLinger, () =>
                {
                    if (here != null && !here.Deleted) here.Delete();
                    if (there != null && !there.Deleted) there.Delete();
                });

                // The step-through beat, then the move.
                Timer.DelayCall(StepThroughDelay, () =>
                {
                    if (bot == null || bot.Deleted || !bot.Alive) return;
                    if (bot.Map == null || bot.Map == Map.Internal) return;

                    bot.MoveToWorld(landing, bot.Map);
                    try { bot.PlaySound(GateSound); } catch { }

                    HandOffFreshTraveler(bot, destName, "Gate Travel");
                });
            });
        }

        // -------------------------------------------------------------------
        // Attach a fresh Traveler aimed at the same destination. Landing a
        // couple tiles off the arrival point means its PlanPath goes
        // straight into the normal arrival flow.
        // -------------------------------------------------------------------
        private static void HandOffFreshTraveler(PlayerBot bot, string destName, string how)
        {
            try
            {
                var traveler = new TravelerBehavior { DestinationName = destName };
                bot.Behavior = traveler;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MagicTravel] {bot.Name}: handoff failed: {ex.Message}");
                return;
            }

            Console.WriteLine($"[MagicTravel] {bot.Name}: {how} -> {destName}");
        }

        // Words of power + a casting sweep. Visual only — must never
        // break the trip.
        private static void SayMantra(PlayerBot bot, string words)
        {
            try
            {
                bot.Say(words);
                bot.Animate(16, 7, 1, true, false, 0);
            }
            catch { }
        }

        // Sparkle + sound on the bot, swallowing effect errors.
        private static void SafeEffect(PlayerBot bot, int soundId)
        {
            try
            {
                bot.PlaySound(soundId);
                bot.FixedParticles(SparkleId, 9, 32, 5008, EffectLayer.Waist);
            }
            catch { }
        }
    }
}
