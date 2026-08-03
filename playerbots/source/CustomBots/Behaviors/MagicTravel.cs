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
        // Skill and mana gates. Recall is 4th circle (11 mana) — and in
        // the era EVERYONE traveled by it: most templates kept ~25-30
        // Magery just for Recall, and everyone else burned recall
        // SCROLLS. Bots mirror that: enough Magery casts it; no Magery
        // reads a scroll from the pack (kits carry a few, restocked
        // offscreen in town). Gate Travel stays 7th circle GM territory.
        public const double RecallMinMagery = 26.0;
        public const int    RecallManaCost  = 11;
        public const double GateMinMagery   = 90.0;
        public const int    GateManaCost    = 40;

        // Only trips at least this long (straight-line tiles) justify the
        // mana — short hops stay on foot so streets keep their traffic.
        public const int MinTripDistance = 80;

        // Of eligible long trips, how many go by magic — scaled by
        // distance, because that's how players actually chose: nobody
        // walked half the continent, plenty of people walked to the next
        // town over. (Kept below 1.0 even for epic trips — an
        // all-teleport world empties the roads.)
        public static double MagicTripChanceFor(int dist) =>
            dist >= 300 ? 0.85
            : dist >= 150 ? 0.65
            : 0.45;
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
        // Capability — can this bot travel by magic RIGHT NOW? Casting
        // needs the magery + mana; failing that, a recall scroll in the
        // pack does the job (that's how the non-mage half of Britannia
        // got around).
        // -------------------------------------------------------------------
        public static bool CanCastRecall(PlayerBot bot) =>
            bot != null &&
            bot.Skills[SkillName.Magery].Base >= RecallMinMagery &&
            bot.Mana >= RecallManaCost;

        public static bool HasRecallScroll(PlayerBot bot) =>
            bot?.Backpack?.FindItemByType(typeof(RecallScroll)) != null;

        public static bool CanTravel(PlayerBot bot) =>
            bot is { Deleted: false, Alive: true } &&
            (CanCastRecall(bot) || HasRecallScroll(bot));

        // (No offscreen scroll restock — un-T2A. Scrolls come from the
        // mage shop like everything else: BotSupplies turns "low on
        // scrolls" into a real errand.)

        // -------------------------------------------------------------------
        // TryBeginTrip — roll and, if the dice land, run a magic trip to
        // destCoord. Returns true if a trip started: the calling Traveler
        // must freeze and stop stepping (the sequence detaches it when the
        // fresh Traveler attaches on the far side).
        //
        // `required` skips the distance and chance gates — used when the
        // destination is unreachable on foot (an island): magic is the
        // only way there, so a capable bot always takes it.
        // -------------------------------------------------------------------
        public static bool TryBeginTrip(
            PlayerBot bot, string destName, Point3D destCoord, DestinationType destType,
            bool required = false)
        {
            if (bot == null || bot.Deleted || !bot.Alive) return false;
            if (bot.Map == null || bot.Map == Map.Internal) return false;

            if (!CanTravel(bot)) return false;

            int dist = Math.Max(Math.Abs(destCoord.X - bot.X),
                                Math.Abs(destCoord.Y - bot.Y));
            if (!required)
            {
                if (dist < MinTripDistance) return false;
                double chance = MagicTripChanceFor(dist);
                if (!CanCastRecall(bot))
                {
                    // Scrolls cost gold. A caster recalls freely (mana
                    // regenerates); a scroll user saves the stack for
                    // genuinely long hauls — which is also what keeps a
                    // scroll in the pack for the day it's WEDGED and
                    // needs the emergency escape.
                    if (dist < 200) return false;
                    chance *= 0.6;
                }
                if (Utility.RandomDouble() >= chance) return false;
            }

            double magery = bot.Skills[SkillName.Magery].Base;
            bool gate = magery >= GateMinMagery && bot.Mana >= GateManaCost &&
                        Utility.RandomDouble() < GateShare;

            var landing = PickLanding(bot, destName, destCoord, destType);

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
        // EmergencyEscape — the era-true stuck recovery: a jammed or
        // stranded bot RECALLS out (cast, or a scroll) instead of silently
        // teleporting — exactly what a real player did when wedged. Lands
        // near `dest` and attaches a fresh Traveler that picks its own next
        // destination (handoffDest null) — never aimed back at the place it
        // just escaped. Returns false when the bot has no way to recall;
        // the caller falls back to its silent rescue.
        // -------------------------------------------------------------------
        public static bool EmergencyEscape(PlayerBot bot, BotDestination dest)
        {
            if (bot == null || bot.Deleted || !bot.Alive || dest == null) return false;
            if (bot.Map == null || bot.Map == Map.Internal) return false;
            if (!CanTravel(bot)) return false;

            var landing = PickLanding(
                bot, dest.Name, dest.ArrivalPoint ?? dest.Location, dest.Type);
            BeginRecallTrip(bot, null, landing);
            return true;
        }

        // -------------------------------------------------------------------
        // Where to land. Normal destinations: a small spread around the
        // arrival point so simultaneous arrivals don't stack.
        //
        // Dungeon entrances: NEVER land on or beside the pad — the arrival
        // tile sits on a real Teleporter, and materializing onto it would
        // skip the walk-on entry flow that arms the crawler conversion.
        // Aim at the entrance's approach WAYPOINT (a tile the nav audit has
        // already proven walkable) so the fresh Traveler walks the last
        // steps through the normal armed path.
        //
        // Every candidate is validated with Map.CanSpawnMobile: a blind
        // coordinate offset at a cliff-face entrance regularly landed the
        // bot INSIDE the mountain, where it could never step out again —
        // that was the "recalled into the rocks and stuck" epidemic at the
        // Orc Cave / Wrong / Ice ledges.
        // -------------------------------------------------------------------
        private static Point3D PickLanding(PlayerBot bot, string destName,
            Point3D destCoord, DestinationType destType)
        {
            var map = bot.Map;
            bool entrance = destType == DestinationType.DungeonEntrance ||
                            destType == DestinationType.Dungeon;

            // Base point: entrances aim at their approach node instead of
            // the pad's doorstep.
            var basePoint = destCoord;
            if (entrance)
            {
                var dest = DestinationCatalog.GetByName(destName);
                var node = dest != null && !string.IsNullOrEmpty(dest.NearestWaypoint)
                    ? WaypointRegistry.Graph?.Get(dest.NearestWaypoint)
                    : null;
                if (node != null)
                {
                    basePoint = node.Location;
                }
            }

            // Spread candidates, validated against the real map. Entrance
            // landings additionally refuse tiles beside the pad itself.
            for (int i = 0; i < 10; i++)
            {
                int spread = entrance && basePoint == destCoord ? 4 : 2;
                int x = basePoint.X + Utility.RandomMinMax(-spread, spread);
                int y = basePoint.Y + Utility.RandomMinMax(-spread, spread);
                if (entrance &&
                    Math.Max(Math.Abs(x - destCoord.X), Math.Abs(y - destCoord.Y)) <= 1)
                {
                    continue; // on/beside the teleporter pad
                }
                int z = map.GetAverageZ(x, y);
                if (map.CanSpawnMobile(x, y, z))
                {
                    return new Point3D(x, y, z);
                }
            }

            // The base point itself (waypoint nodes are engine-verified).
            int bz = map.GetAverageZ(basePoint.X, basePoint.Y);
            if (map.CanSpawnMobile(basePoint.X, basePoint.Y, bz))
            {
                return new Point3D(basePoint.X, basePoint.Y, bz);
            }

            // Last resort — old behavior, at least at the authored coord.
            return basePoint;
        }

        // -------------------------------------------------------------------
        // Recall — Kal Ort Por, flash, gone. Pays with mana when the bot
        // can cast it; otherwise burns a recall scroll from the pack (the
        // scroll still speaks the words — that's how scrolls work).
        // -------------------------------------------------------------------
        private static void BeginRecallTrip(PlayerBot bot, string destName, Point3D landing)
        {
            if (CanCastRecall(bot))
            {
                bot.Mana = Math.Max(0, bot.Mana - RecallManaCost);
            }
            else if (bot.Backpack?.FindItemByType(typeof(RecallScroll)) is RecallScroll scroll)
            {
                scroll.Consume(1);
                BotScene.Deliver(bot, "*reads a recall scroll*");
            }
            else
            {
                return; // no way to pay (callers gate on CanTravel — belt+suspenders)
            }
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
                // stand. Both dissolve after the linger window, and the
                // BotTravelGate class self-cleans at world load so a
                // restart mid-linger can't orphan permanent gates.
                BotTravelGate here = null;
                BotTravelGate there = null;
                try
                {
                    here = new BotTravelGate(landing, map);
                    here.MoveToWorld(origin, map);
                    there = new BotTravelGate(origin, map);
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

            Console.WriteLine(
                $"[MagicTravel] {bot.Name}: {how} -> {destName ?? "(fresh pick)"}");
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
