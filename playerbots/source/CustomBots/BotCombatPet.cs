// =========================================================================
// BotCombatPet.cs — the tamer's FIGHTING pet.
//
// "Nightmares, dragons, and white wyrms dominated PvM." A Tamer-class
// bot heading out to hunt brings a real controlled pet scaled to its
// tier — a Novice walks with a timber wolf, a Grandmaster with a
// nightmare or white wyrm — and USES it: the pet is ordered onto the
// tamer's combatant with the era's actual typed command ("all kill"),
// gets vet-bandaged when hurt, kept loyal (fed), and teleport-caught-up
// when a recall or stairwell leaves it behind.
//
// Everything here is driven by ONE central upkeep timer over a runtime
// registry, so the pet fights for its master under ANY behavior —
// adventurer, crawler, defender — with a single spawn hook at
// AdventurerBehavior.OnAttached.
//
// Lifecycle discipline (same doctrine as BotPackAnimal): the reference
// is runtime-only, orphans are reaped when the master is deleted or
// logged out, and world load sweeps every PlayerBot-controlled creature
// — the next hunt spawns a fresh pet. Zero leaks.
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

        // The registry the upkeep timer walks. Runtime-only.
        private static readonly List<(PlayerBot bot, BaseCreature pet)> _pets = new();
        private static Timer _upkeep;

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
        // Spawn the tier-appropriate pet for a hunting tamer. Called from
        // AdventurerBehavior.OnAttached (crawlers inherit it). Steps DOWN
        // the ladder if the roll lands above what the bot's Animal Taming
        // could actually control — orders that fail their control checks
        // make pets go wild, and a wild nightmare is a leak.
        // -------------------------------------------------------------------
        public static void EnsureFor(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null ||
                bot.Map == Map.Internal || !bot.Alive ||
                bot.Class != BotClass.Tamer)
            {
                return;
            }
            if (bot.CombatPet is { Deleted: false, Alive: true })
            {
                return; // already walking with one
            }
            if (bot.Skills[SkillName.AnimalTaming].Base < 50.0)
            {
                return; // not enough tamer to hold a fighting pet
            }

            var pet = RollPet(bot);
            if (pet == null)
            {
                return;
            }

            pet.Name = PetNames[Utility.Random(PetNames.Length)];
            pet.MoveToWorld(new Point3D(bot.X + 1, bot.Y + 1, bot.Z), bot.Map);
            if (!pet.SetControlMaster(bot))
            {
                pet.Delete();
                return;
            }
            pet.ControlTarget = bot;
            pet.ControlOrder = OrderType.Follow;
            pet.Loyalty = BaseCreature.MaxLoyalty;

            bot.CombatPet = pet;
            _pets.Add((bot, pet));
            BotScene.Play((1.0, bot, $"{pet.Name} follow me"));
            Console.WriteLine(
                $"[tamer] {bot.Name} ({bot.SkillTier}) heads out with " +
                $"{pet.Name} the {pet.GetType().Name}");
        }

        // The era's PvM ladder, tier-gated and control-checked.
        private static BaseCreature RollPet(PlayerBot bot)
        {
            double taming = bot.Skills[SkillName.AnimalTaming].Base;
            int rank = BotSkillTierHelper.Rank(bot.SkillTier);

            // Candidates from strongest the tier allows downward; first
            // one the bot could genuinely control wins.
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
        // The central upkeep pass: sic the pet on the master's combatant
        // ("all kill"), vet-bandage it, keep it fed/loyal, catch it up
        // after recalls and stairs, and reap orphans. Runs every 3s over
        // the (small) registry.
        // -------------------------------------------------------------------
        private static void Upkeep()
        {
            for (int i = _pets.Count - 1; i >= 0; i--)
            {
                var (bot, pet) = _pets[i];

                // Reap: master gone (deleted / logged out) or pet gone.
                if (bot == null || bot.Deleted ||
                    pet == null || pet.Deleted || !pet.Alive)
                {
                    if (pet is { Deleted: false } && (bot == null || bot.Deleted))
                    {
                        pet.Delete();
                    }
                    if (bot is { Deleted: false } &&
                        (pet == null || pet.Deleted || !pet.Alive))
                    {
                        if (pet != null && bot.CombatPet == pet)
                        {
                            bot.CombatPet = null; // died in the line of duty
                        }
                    }
                    _pets.RemoveAt(i);
                    continue;
                }
                if (bot.Map == Map.Internal)
                {
                    pet.Delete(); // master logged out mid-hunt
                    _pets.RemoveAt(i);
                    continue;
                }
                if (!bot.Alive)
                {
                    continue; // pet waits by the ghost, like Bessie does
                }

                // Fed and happy — loyalty decay must never free a nightmare.
                pet.Loyalty = BaseCreature.MaxLoyalty;

                // Catch-up: a recall, gate or stairwell left it behind.
                if (pet.Map != bot.Map ||
                    !pet.InRange(bot.Location, 20))
                {
                    pet.MoveToWorld(
                        new Point3D(bot.X + 1, bot.Y + 1, bot.Z), bot.Map);
                    pet.ControlTarget = bot;
                    pet.ControlOrder = OrderType.Follow;
                }

                // Combat: master fighting → pet fights the same target.
                if (bot.Combatant is Mobile foe && !foe.Deleted && foe.Alive &&
                    foe.Map == bot.Map && foe != pet)
                {
                    if (pet.ControlTarget != foe ||
                        pet.ControlOrder != OrderType.Attack)
                    {
                        pet.ControlTarget = foe;
                        pet.ControlOrder = OrderType.Attack;
                        // The era's most typed sentence, sometimes aloud.
                        if (Utility.RandomDouble() < 0.35)
                        {
                            bot.Say("all kill");
                        }
                        Console.WriteLine(
                            $"[tamer] {bot.Name} sics {pet.Name} on {foe.Name}");
                    }
                }
                else if (pet.ControlOrder == OrderType.Attack &&
                         (pet.Combatant is not Mobile pc || !pc.Alive))
                {
                    // Fight's over — heel.
                    pet.ControlTarget = bot;
                    pet.ControlOrder = OrderType.Follow;
                }

                // Veterinary: a hurt pet in reach gets the bandage (the
                // template carries GM Vet for exactly this). Never while
                // the TAMER is badly hurt — self-care wins the bandage.
                if (pet.Hits < pet.HitsMax * 0.6 &&
                    bot.Hits > bot.HitsMax * 0.5 &&
                    bot.InRange(pet.Location, 2) &&
                    bot.Skills[SkillName.Veterinary].Base >= 50.0 &&
                    Utility.RandomDouble() < 0.5)
                {
                    TryVetBandage(bot, pet);
                }
            }
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
