// =========================================================================
// BotCombatPet.cs — the tamer's FIGHTING pet, run by PLAYER RULES.
//
// "Nightmares, dragons, and white wyrms dominated PvM." A Tamer-class
// bot hunts with a real controlled pet — and everything about it works
// the way it worked for a 1999 player, no shortcuts:
//
//   - The pet comes OUT OF THE STABLES: a tamer heading to a hunt
//     without its pet detours to the stables first (TravelerBehavior's
//     MaybeStableFirst), says "vendor claim" at the counter, and the
//     beast walks out of the pens (ClaimAt). No pets appear mid-field.
//   - Orders are ALWAYS typed out loud, exactly the commands players
//     hammered: "all kill" on every target, "all stay" before a vet
//     bandage, "all follow me" to heel.
//   - FEEDING IS REAL: no loyalty pinning. The tamer carries raw ribs,
//     feeds the pet when it gets unhappy (real +10-per-piece engine
//     rate, munch sounds), and restocks ribs on supply errands. A tamer
//     that runs dry watches loyalty decay until the ENGINE frees the
//     pet — a wild ex-pet loose in the world, the era's own tax.
//
// One central upkeep timer drives it all under any behavior. Lifecycle
// discipline (same doctrine as BotPackAnimal): runtime-only reference,
// orphans reaped when the master is deleted or logged out, world load
// sweeps every PlayerBot-controlled creature.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotCombatPets
    {
        // Era pet names — what players actually called them.
        private static readonly string[] PetNames =
        {
            "Fang", "Shadow", "Killer", "Fluffy", "Rex", "Ghost",
            "Smoke", "Storm", "Blaze", "Duke", "Onyx", "Ember",
            "Grim", "Talon", "Frost", "Midnight",
        };

        private sealed class PetRec
        {
            public PlayerBot Bot;
            public BaseCreature Pet;
            public bool Staying;          // "all stay" issued for a bandage
            public DateTime StayUntil;
            public DateTime NextFeedThink;
            public Serial LastFoe;        // throttle the "all kill" spam —
            public DateTime NextKillSayAt; // re-orders stay silent for 15s
        }

        private static readonly List<PetRec> _pets = new();
        private static Timer _upkeep;

        // Ribs run out → loyalty decays → the engine frees the pet.
        private const int FeedBelowLoyalty = 70;

        public static void Initialize()
        {
            // World load: any surviving bot-controlled creature is a stray
            // from a restart that caught a hunt mid-flight. Sweep them all
            // (pack animals have their own sweep; double-delete is a no-op).
            var strays = new List<Mobile>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is BaseCreature bc && bc.Controlled &&
                    bc.ControlMaster is PlayerBot)
                {
                    strays.Add(m);
                }
            }
            foreach (var s in strays)
            {
                if (!s.Deleted)
                {
                    s.Delete();
                }
            }
            if (strays.Count > 0)
            {
                Console.WriteLine(
                    $"[BotCombatPets] {strays.Count} stray bot pet(s) cleaned up.");
            }

            _upkeep = Timer.DelayCall(TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(3), Upkeep);
        }

        // -------------------------------------------------------------------
        // Claim the pet at the STABLES counter. Called from the traveler's
        // stables-arrival handoff right after the bot says "vendor claim"
        // — the beast comes out of the pens beside the tamer, and the
        // follow order is spoken like every other command.
        // -------------------------------------------------------------------
        public static BaseCreature ClaimAt(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null ||
                bot.Map == Map.Internal || !bot.Alive ||
                bot.Class != BotClass.Tamer)
            {
                return null;
            }
            if (bot.CombatPet is { Deleted: false, Alive: true } existing)
            {
                return existing;
            }
            if (bot.Skills[SkillName.AnimalTaming].Base < 50.0)
            {
                return null; // not enough tamer to hold a fighting pet
            }

            var pet = RollPet(bot);
            if (pet == null)
            {
                return null;
            }

            pet.Name = PetNames[Utility.Random(PetNames.Length)];
            pet.MoveToWorld(new Point3D(bot.X + 1, bot.Y + 1, bot.Z), bot.Map);
            if (!pet.SetControlMaster(bot))
            {
                pet.Delete();
                return null;
            }
            pet.ControlTarget = bot;
            pet.ControlOrder = OrderType.Follow;

            bot.CombatPet = pet;
            _pets.Add(new PetRec { Bot = bot, Pet = pet });
            BotScene.Play((1.5, bot, "all follow me"));
            Console.WriteLine(
                $"[tamer] {bot.Name} ({bot.SkillTier}) claims " +
                $"{pet.Name} the {pet.GetType().Name} from the stables");
            return pet;
        }

        // The era's PvM ladder, tier-gated — and stepped DOWN past
        // anything the bot's Animal Taming couldn't genuinely control
        // (failed control checks drain loyalty and free the pet).
        private static BaseCreature RollPet(PlayerBot bot)
        {
            double taming = bot.Skills[SkillName.AnimalTaming].Base;
            int rank = BotSkillTierHelper.Rank(bot.SkillTier);

            var ladder = new List<BaseCreature>();
            if (rank >= 6)
            {
                // The GM flex: white wyrm or nightmare; the odd dragon.
                if (Utility.RandomDouble() < 0.15)
                {
                    ladder.Add(new Dragon());
                }
                ladder.Add(Utility.RandomBool()
                    ? new WhiteWyrm()
                    : (BaseCreature)new Nightmare());
            }
            if (rank >= 5)
            {
                ladder.Add(Utility.RandomBool()
                    ? new Nightmare()
                    : (BaseCreature)new Drake());
            }
            if (rank >= 4)
            {
                ladder.Add(Utility.RandomBool()
                    ? new Drake()
                    : (BaseCreature)new HellHound());
            }
            if (rank >= 3)
            {
                ladder.Add(Utility.RandomBool()
                    ? new HellHound()
                    : (BaseCreature)new DireWolf());
            }
            if (rank >= 2)
            {
                ladder.Add(Utility.RandomBool()
                    ? new GrizzlyBear()
                    : (BaseCreature)new Panther());
            }
            ladder.Add(Utility.RandomBool()
                ? new TimberWolf()
                : (BaseCreature)new BlackBear());

            BaseCreature pick = null;
            foreach (var c in ladder)
            {
                if (pick == null && taming >= c.MinTameSkill)
                {
                    pick = c;
                }
                else
                {
                    c.Delete(); // unused rung
                }
            }
            return pick;
        }

        public static void Release(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }
            var pet = bot.CombatPet;
            bot.CombatPet = null;
            if (pet is { Deleted: false })
            {
                pet.Delete();
            }
        }

        // -------------------------------------------------------------------
        // The central upkeep pass, every 3s over the (small) registry.
        // -------------------------------------------------------------------
        private static void Upkeep()
        {
            for (int i = _pets.Count - 1; i >= 0; i--)
            {
                var rec = _pets[i];
                var bot = rec.Bot;
                var pet = rec.Pet;

                // Reap: master or pet gone.
                if (bot == null || bot.Deleted ||
                    pet == null || pet.Deleted || !pet.Alive)
                {
                    if (pet is { Deleted: false } && (bot == null || bot.Deleted))
                    {
                        pet.Delete();
                    }
                    if (bot is { Deleted: false } && bot.CombatPet == pet)
                    {
                        bot.CombatPet = null; // died in the line of duty
                    }
                    _pets.RemoveAt(i);
                    continue;
                }
                if (bot.Map == Map.Internal)
                {
                    // Logged out — the pet went into the stables with them
                    // (a player stabled before logging; the beast simply
                    // isn't in the world any more).
                    pet.Delete();
                    bot.CombatPet = null;
                    _pets.RemoveAt(i);
                    continue;
                }

                // WENT WILD — loyalty hit zero and the engine freed it, or
                // enough orders failed. The tamer lost its pet for real;
                // the creature stays loose in the world. The era's tax.
                if (pet.ControlMaster != bot || !pet.Controlled)
                {
                    Console.WriteLine(
                        $"[tamer] {pet.Name} has gone WILD on {bot.Name} " +
                        $"(loyalty ran out) — loose at ({pet.X},{pet.Y})");
                    if (bot.CombatPet == pet)
                    {
                        bot.CombatPet = null;
                    }
                    _pets.RemoveAt(i);
                    continue;
                }

                if (!bot.Alive)
                {
                    continue; // pet waits by the ghost, like Bessie does
                }

                // Catch-up: gates and stairs carry a following pet through
                // in the era too — never across a fight, only a lost pet.
                if (pet.Map != bot.Map || !pet.InRange(bot.Location, 20))
                {
                    pet.MoveToWorld(
                        new Point3D(bot.X + 1, bot.Y + 1, bot.Z), bot.Map);
                    pet.ControlTarget = bot;
                    pet.ControlOrder = OrderType.Follow;
                    rec.Staying = false;
                }

                // FEED — the real chore. Unhappy pet + ribs in the pack →
                // feed at the engine's own rate. No ribs? Loyalty keeps
                // sliding and eventually the pet frees itself (above).
                if (Core.Now >= rec.NextFeedThink)
                {
                    rec.NextFeedThink = Core.Now + TimeSpan.FromSeconds(20);
                    if (pet.Loyalty < FeedBelowLoyalty)
                    {
                        TryFeed(bot, pet);
                    }
                }

                // Combat: master fighting → pet ordered onto the target,
                // command ALWAYS typed out loud.
                if (bot.Combatant is Mobile foe && !foe.Deleted && foe.Alive &&
                    foe.Map == bot.Map && foe != pet)
                {
                    if (pet.ControlTarget != foe ||
                        pet.ControlOrder != OrderType.Attack)
                    {
                        pet.ControlTarget = foe;
                        pet.ControlOrder = OrderType.Attack;
                        rec.Staying = false;
                        // The engine consumes orders, so this re-issues
                        // every pass — but the SAY only fires on a new
                        // target (or a long fight): players hammered
                        // "all kill", not ten times per zombie.
                        if (rec.LastFoe != foe.Serial ||
                            Core.Now >= rec.NextKillSayAt)
                        {
                            rec.LastFoe = foe.Serial;
                            rec.NextKillSayAt = Core.Now + TimeSpan.FromSeconds(15);
                            bot.Say("all kill");
                            Console.WriteLine(
                                $"[tamer] {bot.Name} sics {pet.Name} on {foe.Name}");
                        }
                    }
                    continue; // no bandaging mid-melee — survive first
                }

                // Fight over and the pet still has attack orders — heel.
                if (pet.ControlOrder == OrderType.Attack &&
                    (pet.Combatant is not Mobile pc || !pc.Alive))
                {
                    pet.ControlTarget = bot;
                    pet.ControlOrder = OrderType.Follow;
                    bot.Say("all follow me");
                }

                // Veterinary. The real ritual: "all stay", bandage, then
                // "all follow me" when it's patched up. Tamer self-care
                // wins the bandage when both are hurt.
                if (rec.Staying)
                {
                    if (pet.Hits >= pet.HitsMax * 0.95 ||
                        Core.Now >= rec.StayUntil)
                    {
                        rec.Staying = false;
                        pet.ControlTarget = bot;
                        pet.ControlOrder = OrderType.Follow;
                        bot.Say("all follow me");
                    }
                    else if (bot.InRange(pet.Location, 2))
                    {
                        TryVetBandage(bot, pet);
                    }
                }
                else if (pet.Hits < pet.HitsMax * 0.6 &&
                         bot.Hits > bot.HitsMax * 0.5 &&
                         bot.InRange(pet.Location, 3) &&
                         bot.Skills[SkillName.Veterinary].Base >= 50.0)
                {
                    rec.Staying = true;
                    rec.StayUntil = Core.Now + TimeSpan.FromSeconds(25);
                    pet.ControlTarget = bot;
                    pet.ControlOrder = OrderType.Stay;
                    bot.Say("all stay");
                    TryVetBandage(bot, pet);
                }
            }
        }

        // Feed ribs from the pack at the engine's own loyalty rate
        // (+10 per piece), with the real munch sounds and eat animation.
        private static void TryFeed(PlayerBot bot, BaseCreature pet)
        {
            var pack = bot.Backpack;
            var ribs = pack?.FindItemByType(typeof(RawRibs));
            if (ribs == null)
            {
                return; // out of pet food — the decay is the tamer's problem
            }

            int want = (BaseCreature.MaxLoyalty - pet.Loyalty) /
                       BaseCreature.LoyaltyIncreasePerFood;
            int feed = Math.Clamp(Math.Min(want, ribs.Amount), 1, 5);

            ribs.Consume(feed);
            pet.Loyalty += feed * BaseCreature.LoyaltyIncreasePerFood;
            pet.PlaySound(Utility.RandomList(0x3A, 0x3B, 0x3C)); // munch
            if (pet.Body.IsAnimal)
            {
                pet.Animate(3, 5, 1, true, false, 0);
            }

            Console.WriteLine(
                $"[tamer] {bot.Name} feeds {pet.Name} {feed} rib(s) " +
                $"(loyalty {pet.Loyalty}/{BaseCreature.MaxLoyalty})");
        }

        // BandageContext.BeginHeal(healer, patient) via reflection — same
        // soft dependency the self-heal uses.
        private static void TryVetBandage(PlayerBot bot, BaseCreature pet)
        {
            var pack = bot.Backpack;
            var bandage = pack?.FindItemByType(typeof(Bandage));
            if (bandage == null)
            {
                return;
            }
            try
            {
                var ctxType = Type.GetType("Server.Items.BandageContext, UOContent");
                var begin = ctxType?.GetMethod("BeginHeal",
                    new[] { typeof(Mobile), typeof(Mobile) });
                if (begin?.Invoke(null, new object[] { bot, pet }) != null)
                {
                    bandage.Consume(1);
                }
            }
            catch
            {
                // API mismatch — vet care silently unavailable
            }
        }
    }
}
