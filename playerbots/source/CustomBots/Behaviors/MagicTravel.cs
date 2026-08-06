// =========================================================================
// MagicTravel.cs — Recall and Gate Travel for traveling bots.
//
// A bot with real Magery doesn't walk everywhere — that's the whole point
// of the skill. When a Traveler starts a LONG trip, this rolls whether it
// travels by magic instead:
//
//   - Recall: the REAL RecallSpell, end to end. The bot pulls a marked
//     recall rune from its pack (kept and re-marked between trips, like
//     a player's rune collection), casts from the book — engine words of
//     power, cast delay, real BlackPearl/Bloodmoss/MandrakeRoot burned
//     from the pack, real mana, real fizzles — or reads a genuine recall
//     scroll (consumed on success, easier skill check, exactly why shaky
//     casters carried them). When the target cursor comes up we aim it
//     at the rune and the engine does the rest: travel checks, the 0x1FC
//     sound at both ends, the move. A fizzle is retried a couple of
//     times (burning more reagents — fizzles always did); a trip that
//     won't come off is abandoned and the bot walks.
//   - Gate Travel (Magery >= 90, 40 mana): mantra + cast beat + real
//     gate reagents burned, then a REAL pair of Moongate items opens
//     (one here, one there), the bot steps through, and the gates linger
//     ~30s. Real players — and even other bots — can hop through while
//     they stand. (The gate pair stays our own BotTravelGate objects —
//     the stock moongate would gump-block bots — so this half keeps the
//     scripted move; the spell COST is real.)
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
        // Skill gates, derived from the REAL 4th-circle difficulty (pre-ML
        // table: book min 30 / max 70, scroll min 10 / max 50):
        //   - Book cast needs Magery 40+ to be worth attempting (25% at
        //     40, 50% at 50, sure thing at 70) plus the three reagents.
        //   - A scroll casts two circles easier — 25% at Magery 20, sure
        //     thing at 50 — which is exactly why the era's shaky casters
        //     read scrolls. Below Magery 20 even a scroll won't take:
        //     those bots walk (and no longer shop for scrolls).
        //   - Anyone under 55 with a scroll on hand prefers it over the
        //     book — better odds, and that's how people actually played.
        public const double BookMinMagery       = 40.0;
        public const double ScrollMinMagery     = 20.0;
        public const double ScrollPreferredBelowMagery = 55.0;
        public const int    RecallManaCost      = 11;
        public const double GateMinMagery       = 90.0;
        public const int    GateManaCost        = 40;

        // The real spell components. Recall burns these from the pack on
        // every attempt (fizzles included — fizzles always ate reagents);
        // gate burns its own trio when the portal opens.
        public static readonly Type[] RecallReagents =
            { typeof(BlackPearl), typeof(Bloodmoss), typeof(MandrakeRoot) };
        public static readonly Type[] GateReagents =
            { typeof(BlackPearl), typeof(MandrakeRoot), typeof(SulfurousAsh) };

        // A trip that fizzles this many casts in a row gets abandoned —
        // the bot shrugs and walks. Five, because a shaky caster in 1999
        // just kept mashing the macro; three straight fizzles at 25-50%
        // a cast were routine, not a reason to hoof it.
        private const int MaxCastAttempts = 5;

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

        private const int GateSound    = 0x20E;

        // Cast beat: mantra -> effect/move. Roughly a real cast delay.
        // (Gate only — recall runs the engine's own cast timing now.)
        private static readonly TimeSpan CastBeat         = TimeSpan.FromSeconds(2.0);
        // Gate only: pause between the gate opening and stepping through.
        private static readonly TimeSpan StepThroughDelay = TimeSpan.FromSeconds(1.5);
        // How long a conjured gate pair stays open for anyone to use.
        private static readonly TimeSpan GateLinger       = TimeSpan.FromSeconds(30.0);

        // -------------------------------------------------------------------
        // Capability — can this bot travel by magic RIGHT NOW? A book cast
        // needs the magery, the mana, AND the three reagents in the pack;
        // a scroll needs the scroll plus enough magery to read it. Below
        // both bars the bot walks — exactly like every reagent-dry mage
        // and zero-magery character did.
        // -------------------------------------------------------------------
        public static bool HasReagents(PlayerBot bot, Type[] regs)
        {
            var pack = bot?.Backpack;
            if (pack == null)
            {
                return false;
            }
            foreach (var t in regs)
            {
                if (pack.GetAmount(t) < 1)
                {
                    return false;
                }
            }
            return true;
        }

        public static bool CanCastRecall(PlayerBot bot) =>
            bot != null &&
            bot.Skills[SkillName.Magery].Base >= BookMinMagery &&
            bot.Mana >= RecallManaCost &&
            HasReagents(bot, RecallReagents);

        public static bool HasRecallScroll(PlayerBot bot) =>
            bot?.Backpack?.FindItemByType(typeof(RecallScroll)) != null;

        public static bool CanScrollRecall(PlayerBot bot) =>
            bot != null &&
            bot.Skills[SkillName.Magery].Base >= ScrollMinMagery &&
            HasRecallScroll(bot);

        public static bool CanTravel(PlayerBot bot) =>
            bot is { Deleted: false, Alive: true } &&
            (CanCastRecall(bot) || CanScrollRecall(bot));

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
                        HasReagents(bot, GateReagents) &&
                        Utility.RandomDouble() < GateShare;

            var landing = PickLanding(bot, destName, destCoord, destType);

            if (gate)
            {
                BeginGateTrip(bot, destName, landing);
                return true;
            }

            // False = the real cast couldn't even start (criminal flag,
            // recent combat, hands tied up) — the caller just keeps
            // walking, no harm done.
            return TryCastRecall(bot, destName, landing, attempt: 1);
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
            return TryCastRecall(bot, null, landing, attempt: 1);
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

            // The destination's approach waypoint — entrances ALWAYS aim
            // here instead of the pad's doorstep; everyone else keeps it
            // as the fallback anchor for when the doorstep itself can't
            // take a landing (a pier ringed by water, a shop interior
            // packed wall-to-wall) — the node sits on proven-walkable
            // ground a few steps out.
            Point3D? nodePoint = null;
            {
                var dest = DestinationCatalog.GetByName(destName);
                var node = dest != null && !string.IsNullOrEmpty(dest.NearestWaypoint)
                    ? WaypointRegistry.Graph?.Get(dest.NearestWaypoint)
                    : null;
                if (node != null)
                {
                    nodePoint = node.Location;
                }
            }

            var basePoint = entrance && nodePoint != null ? nodePoint.Value : destCoord;

            // Spread candidates, validated against the real map. Two key
            // details, both learned from the real RecallSpell refusing
            // landings the old scripted teleport silently forced:
            //   - Each spot is tried at TWO heights: the authored point's
            //     Z first (docks and shop floors sit ABOVE what
            //     GetAverageZ reports — averaging under a pier returns
            //     the water level), then the averaged ground Z.
            //   - CanSpawnMobile counts MOBILES, and popular arrival
            //     points are permanently crowded (bank sitters, pinned
            //     artisans, gate camps) — so the spread ESCALATES until a
            //     free tile turns up, exactly like a player recalling in
            //     beside the crowd. The fresh Traveler walks the last few
            //     tiles regardless.
            // Entrance landings additionally refuse tiles beside the pad.
            Point3D? ScanRings(Point3D anchor)
            {
                foreach (var ring in new[] { 2, 4, 6, 8 })
                {
                    int spread = entrance && anchor == destCoord && ring < 4 ? 4 : ring;
                    for (int i = 0; i < 8; i++)
                    {
                        int x = anchor.X + Utility.RandomMinMax(-spread, spread);
                        int y = anchor.Y + Utility.RandomMinMax(-spread, spread);
                        if (entrance &&
                            Math.Max(Math.Abs(x - destCoord.X), Math.Abs(y - destCoord.Y)) <= 1)
                        {
                            continue; // on/beside the teleporter pad
                        }
                        if (map.CanSpawnMobile(x, y, anchor.Z))
                        {
                            return new Point3D(x, y, anchor.Z);
                        }
                        int z = map.GetAverageZ(x, y);
                        if (z != anchor.Z && map.CanSpawnMobile(x, y, z))
                        {
                            return new Point3D(x, y, z);
                        }
                    }
                }
                return null;
            }

            if (ScanRings(basePoint) is Point3D hit)
            {
                return hit;
            }

            // Doorstep won't take a landing at all (pier ringed by water,
            // interior packed solid) — land at the approach waypoint and
            // let the fresh Traveler walk in, same as entrances do.
            if (!entrance && nodePoint != null && nodePoint.Value != basePoint &&
                ScanRings(nodePoint.Value) is Point3D nodeHit)
            {
                return nodeHit;
            }

            // The base point itself — authored Z first (arrival points and
            // waypoint nodes carry engine-verified heights), averaged Z as
            // the backup.
            if (map.CanSpawnMobile(basePoint.X, basePoint.Y, basePoint.Z))
            {
                return basePoint;
            }
            int bz = map.GetAverageZ(basePoint.X, basePoint.Y);
            if (map.CanSpawnMobile(basePoint.X, basePoint.Y, bz))
            {
                return new Point3D(basePoint.X, basePoint.Y, bz);
            }

            // Last resort — old behavior, at least at the authored coord.
            return basePoint;
        }

        // -------------------------------------------------------------------
        // Recall — the REAL spell. Pull the bot's recall rune, mark it at
        // the (pre-validated) landing, cast RecallSpell (book or scroll),
        // and when the engine hands over the target cursor, aim it at the
        // rune. Everything downstream is the genuine pipeline: skill
        // check, reagent/mana/scroll consumption, travel restrictions,
        // the 0x1FC sound at both ends, the move itself.
        //
        // Returns false when the cast couldn't START (the caller keeps
        // walking). Fizzles after a successful start are retried by the
        // watcher; a trip that won't come off hands the bot a fresh
        // Traveler so it finishes the journey on foot.
        // -------------------------------------------------------------------
        private static bool TryCastRecall(PlayerBot bot, string destName, Point3D landing, int attempt)
        {
            if (bot == null || bot.Deleted || !bot.Alive) return false;
            if (bot.Map == null || bot.Map == Map.Internal) return false;

            double magery = bot.Skills[SkillName.Magery].Base;
            bool bookAble   = CanCastRecall(bot);
            bool scrollAble = CanScrollRecall(bot);
            if (!bookAble && !scrollAble)
            {
                return false; // a fizzle burned the last reagents, most likely
            }

            // The era choice: a scroll casts two circles easier, so
            // anyone shaky (or bookless) reads the scroll; a confident
            // caster saves the gold and casts from the book.
            bool useScroll = scrollAble &&
                             (!bookAble || magery < ScrollPreferredBelowMagery);
            var scroll = useScroll
                ? bot.Backpack?.FindItemByType(typeof(RecallScroll)) as RecallScroll
                : null;
            if (useScroll && scroll == null)
            {
                if (!bookAble) return false;
                useScroll = false;
            }

            var rune = GetTravelRune(bot, destName, landing);
            if (rune == null)
            {
                return false;
            }

            var spell = new BotRecallSpell(bot, useScroll ? scroll : null);
            if (!spell.Cast())
            {
                return false; // criminal / fresh combat / mid-something — walk
            }

            new RecallCastWatcher(bot, destName, landing, rune, attempt).Start();
            return true;
        }

        // The genuine RecallSpell minus the anti-macro cast-recovery gate.
        // That gate exists to stop human macro spam; the bots' retry
        // pacing already throttles harder than it does, and other bot
        // systems casting in the same window kept re-stamping
        // NextSpellTime and starving honest retries. Everything REAL
        // stays: skill roll, reagents, mana, scroll consumption, travel
        // checks, fizzles.
        private sealed class BotRecallSpell : Server.Spells.Fourth.RecallSpell
        {
            public BotRecallSpell(Mobile caster, Item scroll) : base(caster, scroll) { }
            public override bool CheckNextSpellTime => false;
        }

        // The bot's own recall rune — ONE per bot, kept in the pack and
        // re-marked for each trip (from outside: pulls a rune, casts,
        // gone; snooping the pack shows a genuinely marked rune).
        private static RecallRune GetTravelRune(PlayerBot bot, string destName, Point3D landing)
        {
            var pack = bot.Backpack;
            if (pack == null)
            {
                return null;
            }

            var rune = pack.FindItemByType(typeof(RecallRune)) as RecallRune;
            if (rune == null)
            {
                rune = new RecallRune();
                if (!bot.AddToBackpack(rune))
                {
                    rune.Delete();
                    return null;
                }
            }

            rune.Target      = landing;
            rune.TargetMap   = bot.Map;
            rune.Marked      = true;
            rune.Description = string.IsNullOrEmpty(destName)
                ? "somewhere safe"
                : destName.ToLowerInvariant();
            return rune;
        }

        // -------------------------------------------------------------------
        // RecallCastWatcher — waits out the engine's cast delay, aims the
        // target cursor at the rune, and reads the outcome. The Effect
        // pipeline is synchronous once the cursor is invoked, so "did the
        // bot move to the landing" IS the success test.
        // -------------------------------------------------------------------
        private sealed class RecallCastWatcher
        {
            private readonly PlayerBot _bot;
            private readonly string _destName;
            private readonly Point3D _landing;
            private readonly RecallRune _rune;
            private readonly int _attempt;
            // Set on the FIRST tick, not at construction: casts started
            // during world load sit in the timer queue until the game
            // loop begins, and clocking from construction made every one
            // of them look ancient the moment it first ticked (the boot
            // flood of phantom "wouldn't take" failures).
            private DateTime _started = DateTime.MinValue;
            private Timer _timer;

            public RecallCastWatcher(PlayerBot bot, string destName,
                Point3D landing, RecallRune rune, int attempt)
            {
                _bot = bot; _destName = destName;
                _landing = landing; _rune = rune; _attempt = attempt;
            }

            public void Start() =>
                _timer = Timer.DelayCall(
                    TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250), Tick);

            private void Stop() => _timer?.Stop();

            private void Tick()
            {
                if (_started == DateTime.MinValue)
                {
                    _started = Core.Now;
                }

                if (_bot == null || _bot.Deleted || !_bot.Alive ||
                    _bot.Map == null || _bot.Map == Map.Internal)
                {
                    Stop(); // death/despawn mid-cast — their systems own it
                    return;
                }

                // Cursor's up — target the rune. The whole Effect chain
                // (CheckSequence: skill roll, reagents, mana, scroll,
                // travel checks, the move) runs synchronously inside
                // Invoke, so the outcome is readable right after.
                if (_bot.Target != null && _bot.Spell is Server.Spells.Fourth.RecallSpell)
                {
                    Stop();
                    try { _bot.Target.Invoke(_bot, _rune); } catch { }

                    if (_bot.X == _landing.X && _bot.Y == _landing.Y)
                    {
                        RearmWeapon(_bot);
                        HandOffFreshTraveler(_bot, _destName,
                            _attempt > 1 ? $"Recall (attempt {_attempt})" : "Recall");
                    }
                    else
                    {
                        RetryOrWalk(); // fizzle (or the landing got blocked)
                    }
                    return;
                }

                // Chant broken (shoved, hit) — no spell and no cursor.
                if (_bot.Spell == null && Core.Now - _started > TimeSpan.FromSeconds(1.5))
                {
                    Stop();
                    RetryOrWalk();
                    return;
                }

                // Belt + suspenders: truly wedged (a cast that never
                // resolves). Never fires while the chant is merely slow —
                // the spell object is checked, not just the clock.
                if (Core.Now - _started > TimeSpan.FromSeconds(15))
                {
                    Stop();
                    if (_bot.Spell is Server.Spells.Fourth.RecallSpell stuck)
                    {
                        try { stuck.Disturb(Server.Spells.DisturbType.Kill); } catch { }
                    }
                    RetryOrWalk();
                }
            }

            private void RetryOrWalk()
            {
                RearmWeapon(_bot);

                if (_attempt >= MaxCastAttempts)
                {
                    WalkInstead();
                    return;
                }

                // Breathe, then try the cast again (real players spammed
                // the macro until it took). If the engine refuses to even
                // START the cast (recovery window, lingering state), give
                // it one grace beat before writing the trip off — a
                // refused START costs nothing, so patience is free.
                Timer.DelayCall(
                    TimeSpan.FromMilliseconds(Utility.RandomMinMax(3000, 4500)), () =>
                    {
                        if (_bot == null || _bot.Deleted || !_bot.Alive) return;
                        if (TryCastRecall(_bot, _destName, _landing, _attempt + 1))
                        {
                            return;
                        }
                        Timer.DelayCall(TimeSpan.FromMilliseconds(1500), () =>
                        {
                            if (_bot == null || _bot.Deleted || !_bot.Alive) return;
                            if (!TryCastRecall(_bot, _destName, _landing, _attempt + 1))
                            {
                                WalkInstead();
                            }
                        });
                    });
            }

            private void WalkInstead()
            {
                if (_bot == null || _bot.Deleted || !_bot.Alive) return;

                if (Utility.RandomDouble() < 0.35)
                {
                    try { _bot.Say("cant get this spell off"); } catch { }
                }
                Console.WriteLine(
                    $"[MagicTravel] {_bot.Name}: recall wouldn't take " +
                    $"(attempt {_attempt}, dest '{_destName ?? "fresh"}' at {_landing}) — continuing on foot");
                try
                {
                    _bot.Behavior = new TravelerBehavior { DestinationName = _destName };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MagicTravel] {_bot.Name}: walk handoff failed: {ex.Message}");
                }
            }
        }

        // Casting pocketed the weapon (pre-AOS ClearHands) — put it back
        // in hand once the trip resolves, success or not.
        private static void RearmWeapon(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Spell != null)
            {
                return;
            }
            if (bot.FindItemOnLayer(Layer.TwoHanded) is BaseWeapon ||
                bot.FindItemOnLayer(Layer.OneHanded) is BaseWeapon)
            {
                return;
            }
            var pack = bot.Backpack;
            if (pack == null)
            {
                return;
            }
            foreach (var item in pack.Items)
            {
                if (item is BaseWeapon w && w.Skill != SkillName.Wrestling &&
                    bot.Skills[w.Skill].Base >= 45.0)
                {
                    bot.EquipItem(w);
                    return;
                }
            }
        }

        // -------------------------------------------------------------------
        // Gate Travel — Vas Rel Por, a real gate pair opens, step through.
        // -------------------------------------------------------------------
        private static void BeginGateTrip(PlayerBot bot, string destName, Point3D landing)
        {
            bot.Mana = Math.Max(0, bot.Mana - GateManaCost);

            // The spell's real components leave the pack (black pearl,
            // mandrake, sulfurous ash) — TryBeginTrip verified they're
            // there before rolling the gate.
            var pack = bot.Backpack;
            if (pack != null)
            {
                foreach (var t in GateReagents)
                {
                    pack.ConsumeTotal(t, 1);
                }
            }

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

    }
}
