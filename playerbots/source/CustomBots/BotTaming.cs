// =========================================================================
// BotTaming.cs — taming as a visible activity (IDEAS 2.4).
//
// Tamer-class bots already have the skill on their sheet; this makes it
// THEATER. A tamer crossing the wilds spots a wild animal, stalks up to
// it, and works it with the classic client taming spam ("I've always
// wanted an animal like you") — sometimes it shies away, sometimes it
// submits. A tamed animal FOLLOWS the tamer through town (half the
// flavor is dragging pets down the high street), gets hawked at the bank
// ("selling frenzied ostard 2k"), and either sells to a bystander bot or
// gets released with a shrug.
//
// Moving parts:
//   - BotTaming (manager): slow cadence; pairs an idle Tamer bot with a
//     wild tamable creature nearby, attaches TamerBehavior. Also owns the
//     AFTER-tame lifecycle: ripens each claim, plays the sale scene when
//     a buyer is standing around, releases unsold/orphaned pets so no
//     controlled creature ever leaks.
//   - TamerBehavior: stalk -> taming attempts -> claim, then walks to
//     town as an ordinary Traveler (pet auto-follows its new master).
//
// Test hooks: [BotTame [force]  +  headless tame_request.txt token.
// Journal types "tame" / "tame_sold" (Gossip/*.txt templates).
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotTaming
    {
        public static bool Enabled { get; set; } = true;

        private static readonly TimeSpan AttemptMin   = TimeSpan.FromMinutes(6);
        private static readonly TimeSpan AttemptMax   = TimeSpan.FromMinutes(16);
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);
        private const int MaxActiveTames = 2;

        // How long a tamer parades the pet around before trying to sell,
        // and how long an UNSOLD pet stays before release.
        private static readonly TimeSpan RipenAfter   = TimeSpan.FromMinutes(6);
        private static readonly TimeSpan ReleaseAfter = TimeSpan.FromMinutes(14);

        // A sold pet follows its buyer for a while, then slips away —
        // controlled creatures must never accumulate.
        private static readonly TimeSpan SoldRelease  = TimeSpan.FromMinutes(12);

        private const int QuarryRange = 30;
        private const int BuyerRange  = 14;

        private sealed class Claim
        {
            public Serial Tamer;
            public Serial Pet;
            public DateTime ClaimedAt;
            public bool SaleTried;
        }

        private static DateTime _nextAttempt = DateTime.MinValue;
        private static readonly List<Claim> _claims = new();
        private static readonly List<(Serial pet, DateTime releaseAt)> _sold = new();

        public static void Configure()
        {
            Timer.DelayCall(TickInterval, TickInterval, OnTick);
            CommandSystem.Register("BotTame", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            if (e.Length > 0 && e.GetString(0).ToLowerInvariant() == "force")
            {
                e.Mobile.SendMessage(TryStartTaming(force: true)
                    ? "Taming attempt started."
                    : "No tamer/quarry pair found.");
                return;
            }
            e.Mobile.SendMessage(
                $"Taming: {_claims.Count} walked pet(s), {_sold.Count} sold pet(s) pending release. " +
                $"([BotTame force)");
        }

        private static void OnTick()
        {
            if (!Enabled)
            {
                return;
            }

            ResolveClaims();
            ResolveSold();

            if (Core.Now < _nextAttempt || CountWorking() >= MaxActiveTames)
            {
                return;
            }
            _nextAttempt = Core.Now + TimeSpan.FromSeconds(
                Utility.RandomMinMax((int)AttemptMin.TotalSeconds,
                                     (int)AttemptMax.TotalSeconds));

            TryStartTaming(force: false);
        }

        private static int CountWorking()
        {
            int n = _claims.Count;
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot { Deleted: false } bot &&
                    bot.Behavior is TamerBehavior)
                {
                    n++;
                }
            }
            return n;
        }

        private static bool IsEligibleTamer(PlayerBot bot) =>
            bot != null && !bot.Deleted && bot.Alive &&
            bot.Class == BotClass.Tamer &&
            !bot.LifecycleExempt && !bot.LoggingOut &&
            !bot.CorpseRunPending &&
            bot.Combatant == null &&
            (bot.Behavior is TravelerBehavior or BankSitterBehavior
                          or IdleBehavior or WanderBehavior) &&
            !BotPartyManager.IsInParty(bot) &&
            !DungeonRegistry.IsInDungeon(bot);

        public static bool IsGoodQuarry(BaseCreature bc, PlayerBot tamer) =>
            bc != null && !bc.Deleted && bc.Alive &&
            bc.Tamable && !bc.Controlled && !bc.Summoned &&
            !bc.IsDeadPet && bc.Combatant == null &&
            bc.Map == tamer.Map;

        public static bool TryStartTaming(bool force)
        {
            var tamers = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && IsEligibleTamer(bot))
                {
                    tamers.Add(bot);
                }
            }
            if (tamers.Count == 0)
            {
                return false;
            }

            // Shuffle-lite: random start index.
            int start = Utility.Random(tamers.Count);
            int range = force ? QuarryRange * 2 : QuarryRange;
            for (int i = 0; i < tamers.Count; i++)
            {
                var tamer = tamers[(start + i) % tamers.Count];
                BaseCreature quarry = null;
                foreach (var m in tamer.GetMobilesInRange(range))
                {
                    if (m is BaseCreature bc && IsGoodQuarry(bc, tamer))
                    {
                        quarry = bc;
                        break;
                    }
                }
                if (quarry == null)
                {
                    continue;
                }

                tamer.Behavior = new TamerBehavior { QuarrySerial = quarry.Serial };
                Console.WriteLine(
                    $"[tame] {tamer.Name} stalks {quarry.Name} at ({quarry.X},{quarry.Y})");
                return true;
            }
            return false;
        }

        // Called by TamerBehavior on a successful tame.
        public static void OnTamed(PlayerBot tamer, BaseCreature pet)
        {
            _claims.Add(new Claim
            {
                Tamer = tamer.Serial,
                Pet = pet.Serial,
                ClaimedAt = Core.Now,
            });
        }

        // -------------------------------------------------------------------
        // Claim lifecycle: parade -> sale attempt -> sold or released.
        // -------------------------------------------------------------------
        private static void ResolveClaims()
        {
            for (int i = _claims.Count - 1; i >= 0; i--)
            {
                var claim = _claims[i];
                var tamer = World.FindEntity<Mobile>(claim.Tamer) as PlayerBot;
                var pet = World.FindEntity<Mobile>(claim.Pet) as BaseCreature;

                if (pet == null || pet.Deleted || !pet.Alive || !pet.Controlled)
                {
                    _claims.RemoveAt(i);
                    continue;
                }

                // Tamer gone (session end, death): the pet slips its leash.
                if (tamer == null || tamer.Deleted || !tamer.Alive)
                {
                    Release(pet);
                    _claims.RemoveAt(i);
                    continue;
                }

                var age = Core.Now - claim.ClaimedAt;

                // Ripe: try the sale once, wherever the tamer ended up —
                // best case that's a bank, which is exactly the fiction.
                if (!claim.SaleTried && age >= RipenAfter)
                {
                    claim.SaleTried = true;
                    TrySell(tamer, pet);
                    continue;
                }

                // Nobody bought it — set it loose with a shrug.
                if (age >= ReleaseAfter)
                {
                    var line = BotScene.Pick("tame_release", "{pet}", pet.Name);
                    if (!string.IsNullOrEmpty(line))
                    {
                        BotScene.Deliver(tamer, line);
                    }
                    Release(pet);
                    _claims.RemoveAt(i);
                }
            }
        }

        private static void TrySell(PlayerBot tamer, BaseCreature pet)
        {
            PlayerBot buyer = null;
            foreach (var m in tamer.GetMobilesInRange(BuyerRange))
            {
                if (m is PlayerBot bot && !bot.Deleted && bot.Alive &&
                    bot != tamer &&
                    bot.Combatant == null &&
                    !bot.LifecycleExempt && !bot.LoggingOut &&
                    !bot.CorpseRunPending &&
                    (bot.Behavior is BankSitterBehavior or IdleBehavior
                                  or WanderBehavior or TravelerBehavior))
                {
                    buyer = bot;
                    break;
                }
            }

            var hawkLine = BotScene.Pick("tame_sell", "{pet}", pet.Name)
                ?? $"selling {pet.Name}, going cheap";
            BotScene.Deliver(tamer, hawkLine);

            if (buyer == null)
            {
                return; // keep parading; release timer will handle it
            }

            var b = buyer;
            var p = pet;
            var t = tamer;
            Timer.DelayCall(TimeSpan.FromSeconds(Utility.RandomMinMax(3, 6)), () =>
            {
                if (b.Deleted || !b.Alive || p.Deleted || !p.Alive ||
                    !p.Controlled || t.Deleted)
                {
                    return;
                }

                var buyLine = BotScene.Pick("tame_buy", "{pet}", p.Name)
                    ?? "deal. come along then";
                BotScene.Deliver(b, buyLine);

                if (p.SetControlMaster(b))
                {
                    p.ControlTarget = b;
                    p.ControlOrder = OrderType.Follow;
                    BotEventJournal.Record("tame_sold", t, p.Name);
                    Console.WriteLine($"[tame] {t.Name} sold {p.Name} to {b.Name}");

                    for (int i = _claims.Count - 1; i >= 0; i--)
                    {
                        if (_claims[i].Pet == p.Serial)
                        {
                            _claims.RemoveAt(i);
                        }
                    }
                    _sold.Add((p.Serial, Core.Now + SoldRelease));
                }
            });
        }

        // Sold pets eventually slip away — no permanent controlled
        // creatures from this system, ever.
        private static void ResolveSold()
        {
            for (int i = _sold.Count - 1; i >= 0; i--)
            {
                var (serial, releaseAt) = _sold[i];
                var pet = World.FindEntity<Mobile>(serial) as BaseCreature;
                if (pet == null || pet.Deleted || !pet.Alive || !pet.Controlled)
                {
                    _sold.RemoveAt(i);
                    continue;
                }
                if (Core.Now >= releaseAt)
                {
                    Release(pet);
                    _sold.RemoveAt(i);
                }
            }
        }

        private static void Release(BaseCreature pet)
        {
            if (pet == null || pet.Deleted)
            {
                return;
            }
            pet.ControlTarget = null;
            pet.ControlOrder = OrderType.None;
            pet.SetControlMaster(null);
        }
    }

    // ---------------------------------------------------------------------
    // The stalk-and-tame behavior.
    // ---------------------------------------------------------------------
    public class TamerBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Tamer";

        public Serial QuarrySerial { get; set; }

        private enum Phase { Stalking, Taming }

        private Phase _phase = Phase.Stalking;
        private DateTime _giveUpAt;
        private DateTime _nextAttempt;
        private int _attemptsLeft;
        private PathFollower _follower;

        // Close enough to start sweet-talking.
        private const int TameRange = 4;

        public override string GetStatusLine(PlayerBot bot) =>
            _phase == Phase.Stalking ? "stalking a wild animal" : "taming an animal";

        public TamerBehavior()
        {
            ChatCategories  = System.Array.Empty<string>();
            ChatChance      = 0.0; // all speech is scripted attempt beats
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            _giveUpAt = Core.Now + TimeSpan.FromSeconds(150);
            _attemptsLeft = Utility.RandomMinMax(3, 5);
            _nextAttempt = Core.Now;
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal || bot.Deleted || !bot.Alive)
            {
                return;
            }

            // Jumped mid-approach: the wilds bite back. Same defender swap
            // gatherers use; ordinary life resumes after.
            if (bot.Combatant is Mobile threat && threat.Alive && !threat.Deleted)
            {
                bot.Behavior = new AdventurerBehavior
                {
                    DefenderMode = true,
                    DefenderRetreatHpFraction = 0.45,
                };
                return;
            }

            var quarry = World.FindEntity<Mobile>(QuarrySerial) as BaseCreature;
            if (quarry == null || !BotTaming.IsGoodQuarry(quarry, bot) ||
                Core.Now >= _giveUpAt)
            {
                GiveUp(bot, quarry);
                return;
            }

            int dist = (int)bot.GetDistanceToSqrt(quarry.Location);
            if (dist > 60)
            {
                GiveUp(bot, quarry); // it bolted across the map
                return;
            }

            if (dist > TameRange)
            {
                _phase = Phase.Stalking;
                // Real short-range pathfinding at the quarry (the goal is
                // the MOBILE, so the path tracks it as it grazes). Raw
                // directional stepping stalled on every tree line — four
                // stalks, four timeouts, zero tames on the first soak.
                _follower ??= new PathFollower(bot, quarry);
                bool run = dist > 10;
                _follower.Follow(run, TameRange);
                if (!bot.InRange(quarry.Location, TameRange))
                {
                    _follower.Follow(run, TameRange);
                }
                return;
            }

            // In range: work the animal.
            _follower = null;
            _phase = Phase.Taming;
            if (Core.Now < _nextAttempt)
            {
                return;
            }
            _nextAttempt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(6, 10));

            bot.Direction = bot.GetDirectionTo(quarry.Location);

            var attempt = BotScene.Pick("tame_attempt", "{pet}", quarry.Name);
            if (!string.IsNullOrEmpty(attempt))
            {
                BotScene.Deliver(bot, attempt);
            }

            _attemptsLeft--;

            // Later attempts land better — the classic grind.
            bool success = _attemptsLeft <= 0
                ? Utility.RandomDouble() < 0.60
                : Utility.RandomDouble() < 0.35;

            if (success && quarry.SetControlMaster(bot))
            {
                quarry.ControlTarget = bot;
                quarry.ControlOrder = OrderType.Follow;

                var line = BotScene.Pick("tame_success", "{pet}", quarry.Name);
                if (!string.IsNullOrEmpty(line))
                {
                    BotScene.Deliver(bot, line);
                }

                BotEventJournal.Record("tame", bot, quarry.Name);
                BotTaming.OnTamed(bot, quarry);
                Console.WriteLine($"[tame] {bot.Name} tamed {quarry.Name}");

                // Parade it to town: ordinary Traveler trip, pet in tow.
                bot.Behavior = BehaviorRegistry.Create("Traveler");
                return;
            }

            if (_attemptsLeft <= 0)
            {
                GiveUp(bot, quarry);
                return;
            }

            // A miss: the animal shies off a few tiles.
            var fail = BotScene.Pick("tame_fail", "{pet}", quarry.Name);
            if (!string.IsNullOrEmpty(fail))
            {
                BotScene.Deliver(bot, fail);
            }
            var away = (Direction)Utility.Random(8);
            quarry.Direction = away;
            quarry.Move(away);
            quarry.Move(away);
        }

        private void GiveUp(PlayerBot bot, BaseCreature quarry)
        {
            if (quarry != null && !quarry.Deleted)
            {
                var line = BotScene.Pick("tame_giveup", "{pet}", quarry.Name);
                if (!string.IsNullOrEmpty(line))
                {
                    BotScene.Deliver(bot, line);
                }
            }
            Console.WriteLine(
                $"[tame] {bot.Name} gave up ({(quarry == null || quarry.Deleted ? "quarry gone" : $"dist {(int)bot.GetDistanceToSqrt(quarry.Location)}, {_attemptsLeft} attempts left")})");
            bot.Behavior = BehaviorRegistry.Create("Traveler");
        }
    }
}
