// =========================================================================
// EquipmentTable.cs — Class + tier based equipment generation with rich
// variety. Inspired by classic UO bank-sitting scenes where every player
// looks distinct: hats, cloaks, sashes, masks, colors.
//
// Generation pipeline:
//   1. ROLL CLASS LOOK — armor archetype + class weapon. Each class has
//      a WEIGHTED set of armor possibilities (Warriors usually plate but
//      sometimes chain or studded; Mages usually robes but sometimes
//      studded; etc.). Class identity is preserved but not locked.
//
//   2. UNIVERSAL ACCESSORIES — hats, cloaks, sashes, gloves overlays,
//      bandanas. Rolled INDEPENDENTLY of class, with their own
//      probabilities. A Warrior in plate might also wear a feathered
//      hat and a green sash. Some bots get jester hats and skull caps
//      and animal masks. The visual chaos of real UO.
//
//   3. FOOTWEAR SAFETY NET — anyone without shoes gets some.
// =========================================================================

using System;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class EquipmentTable
    {
        // ---- Public entry ----

        public static void RollOutfit(PlayerBot bot, BotClass cls, BotSkillTier tier)
        {
            RollClassLook(bot, cls, tier);
            RollUniversalAccessories(bot, cls, tier);
            EnsureFootwear(bot);
            RollBackpackLoot(bot, cls, tier);
        }

        // -------------------------------------------------------------------
        // Backpack loot — gold and class-appropriate carry items, so a bot
        // is worth snooping/stealing from and drops a real corpse on death.
        //
        // Gold scales with skill tier (a Grandmaster carries a fatter
        // purse than a Novice). Class items are the consumables/tools that
        // class would plausibly carry: bandages for fighters, potions and
        // a spare reagent stash for mages, lockpicks for thieves, etc.
        // -------------------------------------------------------------------
        private static void RollBackpackLoot(PlayerBot bot, BotClass cls, BotSkillTier tier)
        {
            if (bot.Backpack == null) return;

            // --- Gold, scaled by tier ---
            int goldBase = tier switch
            {
                BotSkillTier.Novice      => 40,
                BotSkillTier.Apprentice  => 90,
                BotSkillTier.Journeyman  => 170,
                BotSkillTier.Adept       => 300,
                BotSkillTier.Expert      => 500,
                BotSkillTier.Master      => 850,
                BotSkillTier.Grandmaster => 1400,
                _                        => 100,
            };
            // ±35% spread so bots aren't all carrying identical purses.
            int gold = (int)(goldBase * Utility.RandomMinMax(65, 135) / 100.0);
            if (gold > 0) AddToPack(bot, "Server.Items.Gold", gold);

            // --- Class-appropriate carry items ---
            switch (cls)
            {
                case BotClass.Warrior:
                case BotClass.Fencer:
                case BotClass.Archer:
                case BotClass.Ranger:
                    // Fighters carry bandages and a few healing potions.
                    AddToPack(bot, "Server.Items.Bandage",
                              Utility.RandomMinMax(5, 20));
                    MaybeAddToPack(bot, "Server.Items.HealPotion", 0.5,
                                   Utility.RandomMinMax(1, 4));
                    break;

                case BotClass.Mage:
                    // Mages carry potions and a spare reagent stash.
                    MaybeAddToPack(bot, "Server.Items.HealPotion", 0.4,
                                   Utility.RandomMinMax(1, 3));
                    MaybeAddToPack(bot, "Server.Items.GreaterManaPotion",
                                   0.3, Utility.RandomMinMax(1, 3));
                    AddReagentStash(bot, tier);
                    break;

                case BotClass.Healer:
                    AddToPack(bot, "Server.Items.Bandage",
                              Utility.RandomMinMax(15, 40));
                    AddReagentStash(bot, tier);
                    break;

                case BotClass.Thief:
                    // Thieves carry the tools of the trade.
                    AddToPack(bot, "Server.Items.Lockpick",
                              Utility.RandomMinMax(3, 12));
                    MaybeAddToPack(bot, "Server.Items.Lantern", 0.4, 1);
                    break;

                case BotClass.Crafter:
                    if (bot.CrafterSpec == CrafterType.Fisherman)
                    {
                        // Fishing loot — what a day at the docks produces.
                        // Use stock ModernUO fishing-product types via
                        // reflection; missing types fail silently.
                        MaybeAddToPack(bot, "Server.Items.Fish", 0.85,
                                       Utility.RandomMinMax(2, 6));
                        MaybeAddToPack(bot, "Server.Items.RawFishSteak", 0.5,
                                       Utility.RandomMinMax(1, 4));
                        MaybeAddToPack(bot, "Server.Items.Lobster", 0.30,
                                       Utility.RandomMinMax(1, 3));
                        MaybeAddToPack(bot, "Server.Items.Crab", 0.20,
                                       Utility.RandomMinMax(1, 2));
                        // A spare reel — a fisherman always has one.
                        MaybeAddToPack(bot, "Server.Items.FishingPole", 0.15, 1);
                    }
                    else
                    {
                        // A few tools / raw materials.
                        MaybeAddToPack(bot, "Server.Items.IronIngot", 0.6,
                                       Utility.RandomMinMax(5, 25));
                        MaybeAddToPack(bot, "Server.Items.Bandage", 0.4,
                                       Utility.RandomMinMax(3, 10));
                    }
                    break;

                case BotClass.Bard:
                    MaybeAddToPack(bot, "Server.Items.HealPotion", 0.4,
                                   Utility.RandomMinMax(1, 3));
                    break;

                case BotClass.Tamer:
                    AddToPack(bot, "Server.Items.Bandage",
                              Utility.RandomMinMax(5, 15));
                    break;
            }

            // --- A common odd-and-end most travelers carry ---
            MaybeAddToPack(bot, "Server.Items.Torch", 0.3, 1);
        }

        // Add a spread of the 8 standard magery reagents to the pack.
        // Amount per reagent scales with skill tier — a higher-skill caster
        // casts more often and with costlier spells, so carries a deeper
        // reagent supply. Each reagent gets its tier base ±30% spread.
        private static void AddReagentStash(PlayerBot bot, BotSkillTier tier)
        {
            int regBase = tier switch
            {
                BotSkillTier.Novice      => 8,
                BotSkillTier.Apprentice  => 14,
                BotSkillTier.Journeyman  => 22,
                BotSkillTier.Adept       => 32,
                BotSkillTier.Expert      => 45,
                BotSkillTier.Master      => 60,
                BotSkillTier.Grandmaster => 80,
                _                        => 20,
            };

            string[] regs =
            {
                "Server.Items.BlackPearl",
                "Server.Items.Bloodmoss",
                "Server.Items.Garlic",
                "Server.Items.Ginseng",
                "Server.Items.MandrakeRoot",
                "Server.Items.Nightshade",
                "Server.Items.SulfurousAsh",
                "Server.Items.SpidersSilk",
            };
            foreach (var r in regs)
            {
                // ±30% per-reagent spread so the stash isn't uniform.
                int amt = (int)(regBase * Utility.RandomMinMax(70, 130) / 100.0);
                if (amt < 1) amt = 1;
                AddToPack(bot, r, amt);
            }
        }

        // Add to pack with a probability gate.
        private static void MaybeAddToPack(PlayerBot bot, string itemType,
                                           double chance, int amount)
        {
            if (Utility.RandomDouble() < chance)
                AddToPack(bot, itemType, amount);
        }

        // -------------------------------------------------------------------
        // Class look — armor archetype + weapon, weighted by class.
        //
        // Each class has a primary armor type (Plate for Warriors, Robe for
        // Mages, etc.) but occasionally rolls something different to add
        // variety. A Warrior in studded leather is uncommon but possible —
        // maybe they're "off-duty" or scouting.
        // -------------------------------------------------------------------
        private static void RollClassLook(PlayerBot bot, BotClass cls, BotSkillTier tier)
        {
            switch (cls)
            {
                case BotClass.Warrior: RollWarriorLook(bot, tier);  break;
                case BotClass.Mage:    RollMageLook(bot, tier);     break;
                case BotClass.Fencer:  RollFencerLook(bot, tier);   break;
                case BotClass.Archer:  RollArcherLook(bot, tier);   break;
                case BotClass.Tamer:   RollTamerLook(bot, tier);    break;
                case BotClass.Crafter: RollCrafterLook(bot, tier);  break;
                case BotClass.Healer:  RollHealerLook(bot, tier);   break;
                case BotClass.Thief:   RollThiefLook(bot, tier);    break;
                case BotClass.Bard:    RollBardLook(bot, tier);     break;
                case BotClass.Ranger:  RollRangerLook(bot, tier);   break;
                default:               RollWarriorLook(bot, tier);  break;
            }
        }

        // -------------------------------------------------------------------
        // WARRIOR
        // Primary: Plate. Sometimes Chain. Rarely Studded or Robe (off-duty).
        // -------------------------------------------------------------------
        private static void RollWarriorLook(PlayerBot bot, BotSkillTier tier)
        {
            int roll = Utility.Random(100);
            //  0–69: plate
            // 70–89: chain
            // 90–96: studded
            // 97–99: just robes (casual look — most players had "town clothes")
            if (roll < 70)       PlatesUp(bot, tier);
            else if (roll < 90)  ChainsUp(bot, tier);
            else if (roll < 97)  StuddedUp(bot, tier);
            else                 RobeAndCasual(bot, tier);

            // Weapon — Warriors always carry one.
            EquipWeapon(bot, tier, new int[] { 0, 1, 2, 3, 4 }); // sword/axe pool

            // 35% chance of a shield (one-handed weapons benefit).
            if (Utility.RandomDouble() < 0.35) EquipShield(bot, tier);
        }

        private static void RollMageLook(PlayerBot bot, BotSkillTier tier)
        {
            int roll = Utility.Random(100);
            //  0–79: robe + hat (classic)
            // 80–92: robe only, no wizard hat
            // 93–98: studded leather (battle-mage)
            // 99: plate (gish)
            //
            // Mages NEVER carry a weapon — they fight with spells and kite
            // at range, so a staff would just be dead weight (and ModernUO
            // would try to melee-swing it). Armor look only; hands stay
            // free for casting.
            if (roll < 80)       RobeAndMage(bot, tier, withHat: true);
            else if (roll < 93)  RobeAndMage(bot, tier, withHat: false);
            else if (roll < 99)  StuddedUp(bot, tier);   // battle-mage look
            else                 PlatesUp(bot, tier);    // gish look

            // Spellbook — usually present for any mage-class, and FILLED
            // with spells. An empty book reads as wrong (and a real mage
            // needs spells written to cast from it). Content is a 64-bit
            // mask, one bit per spell, 8 spells per circle. A bot knows
            // every spell from circle 1 up through a cap set by skill
            // tier — a Novice knows the first couple circles, a
            // Grandmaster knows all 8.
            if (Utility.RandomDouble() < 0.85)
            {
                int circles = tier switch
                {
                    BotSkillTier.Novice      => 2,
                    BotSkillTier.Apprentice  => 3,
                    BotSkillTier.Journeyman  => 4,
                    BotSkillTier.Adept       => 5,
                    BotSkillTier.Expert      => 6,
                    BotSkillTier.Master      => 7,
                    BotSkillTier.Grandmaster => 8,
                    _                        => 4,
                };
                // Low bit = circle 1 spell 1. circles*8 spells, all set.
                ulong content = (circles >= 8)
                    ? ulong.MaxValue
                    : (1UL << (circles * 8)) - 1UL;

                var book = new Spellbook();
                book.Content = content;
                Add(bot, book, IsHighTier(tier) ? RichHue() : 0);
            }
        }

        private static void RollFencerLook(PlayerBot bot, BotSkillTier tier)
        {
            int roll = Utility.Random(100);
            //  0–74: studded
            // 75–89: leather
            // 90–97: robe (light agile look)
            // 98–99: plate
            if (roll < 75)       StuddedUp(bot, tier);
            else if (roll < 90)  LeatherUp(bot, tier);
            else if (roll < 98)  RobeAndCasual(bot, tier);
            else                 PlatesUp(bot, tier);

            EquipWeapon(bot, tier, new[] { 20, 21, 22 }); // kryss/wakizashi/spear
        }

        private static void RollArcherLook(PlayerBot bot, BotSkillTier tier)
        {
            int roll = Utility.Random(100);
            //  0–79: leather (classic)
            // 80–92: studded
            // 93–99: just clothes (woodsman)
            if (roll < 80)       LeatherUp(bot, tier);
            else if (roll < 93)  StuddedUp(bot, tier);
            else                 CommonerUp(bot, tier);

            // Bow — Archers should have one.
            var archBow = TryNewItem("Server.Items.Bow");
            if (archBow != null) Add(bot, archBow, IsHighTier(tier) ? RichHue() : 0);

            if (bot.FindItemOnLayer(Layer.TwoHanded) == null)
            {
                var xbow = TryNewItem("Server.Items.Crossbow");
                if (xbow != null) Add(bot, xbow, 0);
            }

            // Ammunition — a ranged weapon needs ammo in the pack or it
            // won't fire. Give a generous stack of arrows, plus some bolts
            // in case the bot ended up with a crossbow.
            AddToPack(bot, "Server.Items.Arrow", Utility.RandomMinMax(40, 80));
            AddToPack(bot, "Server.Items.Bolt",  Utility.RandomMinMax(20, 40));
        }

        private static void RollTamerLook(PlayerBot bot, BotSkillTier tier)
        {
            int roll = Utility.Random(100);
            //  0–59: robe
            // 60–84: leather (practical for handling animals)
            // 85–99: studded
            if (roll < 60)       RobeAndCasual(bot, tier);
            else if (roll < 85)  LeatherUp(bot, tier);
            else                 StuddedUp(bot, tier);

            // Shepherd's crook or staff
            if (Utility.RandomDouble() < 0.5)
            {
                var crook = TryNewItem("Server.Items.ShepherdsCrook");
                if (crook != null) Add(bot, crook, 0);
                else EquipWeapon(bot, tier, new[] { 10 });
            }
        }

        private static void RollCrafterLook(PlayerBot bot, BotSkillTier tier)
        {
            // FISHERMAN — different look entirely. Simple shirt and pants,
            // a straw hat, and a fishing pole as their "tool/weapon." No
            // apron, no smith hammer. Skip the rest of the crafter rolling.
            if (bot.CrafterSpec == CrafterType.Fisherman)
            {
                CommonerUp(bot, tier);

                // Straw hat ~70% of the time; otherwise bandana / no hat.
                if (Utility.RandomDouble() < 0.7)
                {
                    var hat = TryNewItem("Server.Items.StrawHat");
                    if (hat != null) Add(bot, hat, Utility.RandomNeutralHue());
                }
                else if (Utility.RandomDouble() < 0.5)
                {
                    var bandana = TryNewItem("Server.Items.Bandana");
                    if (bandana != null) Add(bot, bandana, Utility.RandomNeutralHue());
                }

                // Fishing pole — the defining tool. Equipped like a weapon.
                var pole = TryNewItem("Server.Items.FishingPole");
                if (pole != null) Add(bot, pole, 0);
                return;
            }

            // Crafters mostly civilian. Aprons, simple shirts, occasional leather.
            int roll = Utility.Random(100);
            if (roll < 60) CommonerUp(bot, tier);
            else if (roll < 85) LeatherUp(bot, tier);
            else RobeAndCasual(bot, tier);

            // Apron — defining accessory
            if (Utility.RandomDouble() < 0.7)
            {
                Item apron = Utility.RandomBool() ? (Item)new FullApron() : new HalfApron();
                int hue = IsHighTier(tier) ? RichHue() : Utility.RandomNeutralHue();
                Add(bot, apron, hue);
            }

            // A blacksmith's hammer or similar tool as their "weapon"
            var hammer = TryNewItem("Server.Items.SmithHammer");
            if (hammer != null) Add(bot, hammer, 0);
        }

        private static void RollHealerLook(PlayerBot bot, BotSkillTier tier)
        {
            // Healers wear robes, often white or pastel.
            int roll = Utility.Random(100);
            if (roll < 75) RobeAndMage(bot, tier, withHat: false, healerWhite: true);
            else if (roll < 92) RobeAndCasual(bot, tier);
            else LeatherUp(bot, tier);

            // Staff or no weapon
            if (Utility.RandomDouble() < 0.4)
            {
                EquipWeapon(bot, tier, new[] { 10 });
            }
        }

        private static void RollThiefLook(PlayerBot bot, BotSkillTier tier)
        {
            // Thieves want dark, unobtrusive clothing.
            int roll = Utility.Random(100);
            if (roll < 70) LeatherUp(bot, tier, darkColors: true);
            else if (roll < 88) CommonerUp(bot, tier, darkColors: true);
            else StuddedUp(bot, tier, darkColors: true);

            // Dagger / kryss / shortspear (small concealable)
            EquipWeapon(bot, tier, new[] { 20, 21, 22 }); // fencing pool
        }

        private static void RollBardLook(PlayerBot bot, BotSkillTier tier)
        {
            // Bards are flashy — colorful clothes, hats, often no real armor.
            int roll = Utility.Random(100);
            if (roll < 55) CommonerUp(bot, tier, bright: true);
            else if (roll < 75) RobeAndCasual(bot, tier, bright: true);
            else if (roll < 90) LeatherUp(bot, tier);
            else StuddedUp(bot, tier);

            // Musical instrument as a "weapon" sometimes
            if (Utility.RandomDouble() < 0.4)
            {
                var lute = TryNewItem("Server.Items.Lute");
                if (lute != null) Add(bot, lute, 0);
                else EquipWeapon(bot, tier, new[] { 0, 1 });
            }
            else
            {
                EquipWeapon(bot, tier, new[] { 0, 1 }); // sword for self-defense
            }
        }

        private static void RollRangerLook(PlayerBot bot, BotSkillTier tier)
        {
            int roll = Utility.Random(100);
            //  0–69: leather (forest greens)
            // 70–89: studded
            // 90–99: chain
            if (roll < 70)       LeatherUp(bot, tier, foresty: true);
            else if (roll < 90)  StuddedUp(bot, tier, foresty: true);
            else                 ChainsUp(bot, tier);

            // Rangers usually have a bow
            if (Utility.RandomDouble() < 0.75)
            {
                var bow = TryNewItem("Server.Items.Bow");
                if (bow != null)
                {
                    Add(bot, bow, 0);
                    // Bow needs arrows in the pack to fire.
                    AddToPack(bot, "Server.Items.Arrow",
                              Utility.RandomMinMax(40, 80));
                }
                else EquipWeapon(bot, tier, new[] { 0, 1 });
            }
            else
            {
                EquipWeapon(bot, tier, new[] { 0, 1 });
            }
        }

        // -------------------------------------------------------------------
        // ARMOR ARCHETYPES — building blocks called by class roll functions.
        // -------------------------------------------------------------------

        private static void PlatesUp(PlayerBot bot, BotSkillTier tier)
        {
            int hue = IsHighTier(tier) && Utility.RandomDouble() < 0.5 ? RichHue() : 0;
            Add(bot, bot.Female ? new FemalePlateChest() : (Item)new PlateChest(), hue);
            Add(bot, new PlateLegs(),   hue);
            Add(bot, new PlateArms(),   hue);
            Add(bot, new PlateGloves(), hue);
            Add(bot, new PlateGorget(), hue);
            // Plate Helm only sometimes — let the accessories pass add a hat instead
            if (Utility.RandomDouble() < 0.45)
            {
                Add(bot, new PlateHelm(), hue);
            }
            Add(bot, new Boots(), IsHighTier(tier) ? RichHue() : 0);
        }

        private static void ChainsUp(PlayerBot bot, BotSkillTier tier)
        {
            int hue = IsHighTier(tier) && Utility.RandomDouble() < 0.5 ? RichHue() : 0;
            Add(bot, new ChainChest(), hue);
            Add(bot, new ChainLegs(),  hue);
            if (Utility.RandomDouble() < 0.6)
            {
                Add(bot, new ChainCoif(), hue);
            }
            Add(bot, new Boots(), 0);
        }

        private static void StuddedUp(PlayerBot bot, BotSkillTier tier, bool darkColors = false, bool foresty = false)
        {
            int hue;
            if (darkColors) hue = DarkHue();
            else if (foresty) hue = ForestHue();
            else hue = IsHighTier(tier) && Utility.RandomDouble() < 0.4 ? RichHue() : 0;

            Add(bot, bot.Female ? new FemaleStuddedChest() : (Item)new StuddedChest(), hue);
            Add(bot, new StuddedLegs(),   hue);
            Add(bot, new StuddedArms(),   hue);
            Add(bot, new StuddedGloves(), hue);
            Add(bot, new StuddedGorget(), hue);
            Add(bot, new Boots(), 0);
        }

        private static void LeatherUp(PlayerBot bot, BotSkillTier tier, bool darkColors = false, bool foresty = false)
        {
            int hue;
            if (darkColors) hue = DarkHue();
            else if (foresty) hue = ForestHue();
            else hue = IsHighTier(tier) && Utility.RandomDouble() < 0.4 ? RichHue() : 0;

            Add(bot, bot.Female ? new FemaleLeatherChest() : (Item)new LeatherChest(), hue);
            Add(bot, new LeatherLegs(),   hue);
            Add(bot, new LeatherArms(),   hue);
            Add(bot, new LeatherGloves(), hue);
            Add(bot, new LeatherGorget(), hue);
            Add(bot, new Boots(), 0);
        }

        // Robe-and-mage: robe + wizard's hat + sandals, classic mage look
        private static void RobeAndMage(PlayerBot bot, BotSkillTier tier,
                                       bool withHat = true, bool healerWhite = false)
        {
            int robeHue;
            if (healerWhite) robeHue = 0; // pure white default
            else if (IsHighTier(tier)) robeHue = RichHue();
            else robeHue = Utility.RandomNeutralHue();

            Add(bot, new Robe(robeHue),    0);
            if (withHat)
            {
                Add(bot, new WizardsHat(robeHue), 0);
            }
            Add(bot, new Sandals(), 0);
        }

        // Robe and casual — just a robe in any color, sandals or shoes
        private static void RobeAndCasual(PlayerBot bot, BotSkillTier tier, bool bright = false)
        {
            int robeHue = bright ? BrightHue() :
                          (IsHighTier(tier) ? RichHue() : Utility.RandomNeutralHue());
            Add(bot, new Robe(robeHue), 0);

            int shoeRoll = Utility.Random(3);
            Item shoes = shoeRoll switch
            {
                0 => new Sandals(),
                1 => new Shoes(),
                _ => new Boots()
            };
            Add(bot, shoes, 0);
        }

        // Commoner — plain shirt + pants. The "town clothes" look.
        private static void CommonerUp(PlayerBot bot, BotSkillTier tier,
                                      bool darkColors = false, bool bright = false)
        {
            int topHue;
            int pantsHue;
            if (darkColors)
            {
                topHue   = DarkHue();
                pantsHue = DarkHue();
            }
            else if (bright)
            {
                topHue   = BrightHue();
                pantsHue = Utility.RandomNeutralHue();
            }
            else
            {
                topHue   = Utility.RandomNeutralHue();
                pantsHue = Utility.RandomNeutralHue();
            }

            // Shirt or doublet
            if (Utility.RandomBool())
            {
                var doublet = TryNewItem("Server.Items.Doublet", topHue);
                if (doublet != null) Add(bot, doublet, 0);
                else Add(bot, new FancyShirt(topHue), 0);
            }
            else
            {
                Add(bot, new FancyShirt(topHue), 0);
            }

            // Pants or skirt
            if (bot.Female && Utility.RandomDouble() < 0.4)
            {
                var skirt = TryNewItem("Server.Items.Skirt", pantsHue);
                if (skirt != null) Add(bot, skirt, 0);
                else Add(bot, new LongPants(pantsHue), 0);
            }
            else
            {
                Add(bot, new LongPants(pantsHue), 0);
            }

            // Footwear — varied
            int shoeRoll = Utility.Random(4);
            Item shoes = shoeRoll switch
            {
                0 => new Sandals(),
                1 => new Shoes(),
                2 => new Boots(),
                _ => new ThighBoots()
            };
            Add(bot, shoes, 0);
        }

        // -------------------------------------------------------------------
        // WEAPONS
        // -------------------------------------------------------------------
        private static void EquipWeapon(PlayerBot bot, BotSkillTier tier, int[] pool)
        {
            // Pick from pool with tier-aware selection.
            int choice = pool[Utility.Random(pool.Length)];
            BaseWeapon w = choice switch
            {
                0  => new Longsword(),
                1  => new Broadsword(),
                2  => new Katana(),
                3  => new VikingSword(),
                4  => IsHighTier(tier) ? (BaseWeapon)new Halberd() : new Longsword(),
                10 => IsHighTier(tier) ? (BaseWeapon)new GnarledStaff() : new QuarterStaff(),
                20 => new Kryss(),
                21 => new Wakizashi(),
                22 => new ShortSpear(),
                _  => new Dagger()
            };
            Add(bot, w, 0);
        }

        private static void EquipShield(PlayerBot bot, BotSkillTier tier)
        {
            int hue = IsHighTier(tier) && Utility.RandomDouble() < 0.4 ? RichHue() : 0;
            Item shield = Utility.Random(4) switch
            {
                0 => new WoodenShield(),
                1 => new MetalKiteShield(),
                2 => new BronzeShield(),
                _ => new HeaterShield()
            };
            Add(bot, shield, hue);
        }

        // -------------------------------------------------------------------
        // UNIVERSAL ACCESSORIES — rolled independently after class look.
        //
        // These add the "self-expression" layer that made real UO bank
        // gatherings so visually rich. A Warrior in plate might also wear
        // a jester hat and a green sash and a black cloak.
        // -------------------------------------------------------------------
        private static void RollUniversalAccessories(PlayerBot bot, BotClass cls, BotSkillTier tier)
        {
            // -- HAT --
            // Only add a hat if there isn't a helm/wizard's hat already.
            // Plate helms etc occupy the same layer.
            if (bot.FindItemOnLayer(Layer.Helm) == null && Utility.RandomDouble() < 0.45)
            {
                Item hat = RollRandomHat();
                if (hat != null)
                {
                    // 60% of hats are colored, 40% plain
                    int hue = Utility.RandomDouble() < 0.6 ? PaletteHue() : 0;
                    Add(bot, hat, hue);
                }
            }

            // -- CLOAK --
            // High probability — cloaks were ubiquitous in UO.
            if (bot.FindItemOnLayer(Layer.Cloak) == null && Utility.RandomDouble() < 0.55)
            {
                int hue = Utility.RandomDouble() < 0.7 ? PaletteHue() : 0;
                Add(bot, new Cloak(), hue);
            }

            // -- BODY SASH --
            if (bot.FindItemOnLayer(Layer.MiddleTorso) == null && Utility.RandomDouble() < 0.30)
            {
                int hue = PaletteHue();
                Add(bot, new BodySash(), hue);
            }

            // -- HALF-APRON (kitchen-y look, secondary chance for non-crafters) --
            if (cls != BotClass.Crafter &&
                bot.FindItemOnLayer(Layer.OuterTorso) == null &&
                Utility.RandomDouble() < 0.05)
            {
                Add(bot, new HalfApron(), PaletteHue());
            }
        }

        // -------------------------------------------------------------------
        // HATS — randomly pick one of many. Uses reflection so missing
        // item types (e.g. some builds lack JesterHat or Bandana) don't
        // fail compile. If the named type isn't in this build, we just
        // try the next one in the list.
        // -------------------------------------------------------------------
        private static readonly string[] _hatTypes = new[]
        {
            "Server.Items.FloppyHat",
            "Server.Items.WideBrimHat",
            "Server.Items.TallStrawHat",
            "Server.Items.FeatheredHat",
            "Server.Items.TricorneHat",
            "Server.Items.Cap",
            "Server.Items.Skullcap",
            "Server.Items.Bonnet",
            "Server.Items.StrawHat",
            "Server.Items.JesterHat",
            "Server.Items.Bandana",
            "Server.Items.WizardsHat",
            "Server.Items.ClothNinjaHood",
            "Server.Items.OrcHelm",
            "Server.Items.BearMask",
            "Server.Items.DeerMask",
            "Server.Items.TribalMask",
        };

        private static Item RollRandomHat()
        {
            // Try up to N random picks. TryNewItem handles all the
            // edge cases — missing types, hue-only constructors, etc.
            // If a pick fails, try a different one.
            for (int tries = 0; tries < 5; tries++)
            {
                string typeName = _hatTypes[Utility.Random(_hatTypes.Length)];
                var hat = TryNewItem(typeName);
                if (hat != null) return hat;
            }
            // Last resort: a FloppyHat — known to exist.
            try { return new FloppyHat(); } catch { return null; }
        }

        // ---- Hue helpers ----

        private static bool IsHighTier(BotSkillTier t) => (int)t >= (int)BotSkillTier.Expert;

        // Rich, saturated colors for high-tier gear
        private static int RichHue() => Utility.Random(8) switch
        {
            0 => 0x0489, // deep red
            1 => 0x0501, // forest green
            2 => 0x055D, // royal blue
            3 => 0x06EE, // golden tan
            4 => 0x08AB, // purple
            5 => 0x044E, // crimson
            6 => 0x0026, // ash gray
            _ => 0x048D, // teal
        };

        // Wider palette including pastels and unusual colors — for hats,
        // cloaks, sashes, things people use for self-expression.
        private static int PaletteHue() => Utility.Random(20) switch
        {
            0  => 0x0489, // deep red
            1  => 0x0501, // forest green
            2  => 0x055D, // royal blue
            3  => 0x06EE, // golden tan
            4  => 0x08AB, // purple
            5  => 0x044E, // crimson
            6  => 0x048D, // teal
            7  => 0x002B, // pale yellow
            8  => 0x0021, // soft pink
            9  => 0x0481, // ivory / pale gold
            10 => 0x0035, // dusty orange
            11 => 0x0840, // forest green dark
            12 => 0x0855, // emerald
            13 => 0x023E, // bright cyan
            14 => 0x011D, // hot pink
            15 => 0x0386, // sky blue
            16 => 0x0590, // jet black
            17 => 0x0026, // ash
            18 => 0x0023, // brown
            _  => 0x044F, // rust
        };

        // Dark colors for thieves
        private static int DarkHue() => Utility.Random(6) switch
        {
            0 => 0x0590, // jet black
            1 => 0x0026, // ash
            2 => 0x0044, // dark navy
            3 => 0x0023, // brown
            4 => 0x0288, // dark green
            _ => 0x0455, // wine red
        };

        // Forest greens / earth tones for rangers
        private static int ForestHue() => Utility.Random(6) switch
        {
            0 => 0x0840, // forest green dark
            1 => 0x0855, // emerald
            2 => 0x0501, // forest green
            3 => 0x0288, // dark green
            4 => 0x0023, // brown
            _ => 0x044F, // rust
        };

        // Bright/loud colors for bards
        private static int BrightHue() => Utility.Random(8) switch
        {
            0 => 0x011D, // hot pink
            1 => 0x023E, // bright cyan
            2 => 0x0035, // dusty orange
            3 => 0x0028, // bright red
            4 => 0x0055, // turquoise
            5 => 0x08AB, // purple
            6 => 0x002B, // pale yellow
            _ => 0x048D, // teal
        };

        // ---- Footwear safety ----

        private static bool HasFootwear(PlayerBot bot) =>
            bot.FindItemOnLayer(Layer.Shoes) != null;

        private static void EnsureFootwear(PlayerBot bot)
        {
            if (HasFootwear(bot)) return;
            Add(bot, new Sandals(), 0);
        }

        // -------------------------------------------------------------------
        // Reflection helpers — try to instantiate an item by type name.
        //
        // Two challenges in ModernUO:
        //   1. We can't predict which assembly a content class lives in.
        //      Server.Items.WideBrimHat could be in ModernUO.dll directly,
        //      or in a content assembly with a different name. Type.GetType
        //      with an assembly qualifier requires us to know the assembly.
        //   2. Many ModernUO content classes have an (int hue = 0)
        //      constructor instead of a true parameterless constructor.
        //      Activator.CreateInstance(t) without args requires a real
        //      parameterless ctor, so it fails on these even though they
        //      LOOK like they have a default.
        //
        // Solution: search loaded assemblies for the type, then try ctors
        // in order: () -> (int) -> (int hue). Returns null on failure.
        // -------------------------------------------------------------------
        private static Type FindType(string fullName)
        {
            // First try the direct lookup (works if type is in calling asm).
            var t = Type.GetType(fullName);
            if (t != null) return t;

            // Walk every loaded assembly looking for the type.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch
                {
                    // Some assemblies throw on GetType for various reasons;
                    // skip them and try the next.
                }
            }
            return null;
        }

        private static Item TryNewItem(string typeName)
        {
            try
            {
                var t = FindType(typeName);
                if (t == null) return null;

                // Prefer a true parameterless ctor if present.
                var ctor0 = t.GetConstructor(Type.EmptyTypes);
                if (ctor0 != null) return ctor0.Invoke(null) as Item;

                // Fall back to (int) ctor with hue=0 (matches default value).
                var ctorInt = t.GetConstructor(new[] { typeof(int) });
                if (ctorInt != null) return ctorInt.Invoke(new object[] { 0 }) as Item;

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static Item TryNewItem(string typeName, int hueArg)
        {
            try
            {
                var t = FindType(typeName);
                if (t == null) return null;

                // Prefer (int) ctor since we have a hue to pass.
                var ctorInt = t.GetConstructor(new[] { typeof(int) });
                if (ctorInt != null) return ctorInt.Invoke(new object[] { hueArg }) as Item;

                // Fall back to parameterless ctor and set hue afterward.
                var ctor0 = t.GetConstructor(Type.EmptyTypes);
                if (ctor0 != null)
                {
                    var inst = ctor0.Invoke(null) as Item;
                    if (inst != null && hueArg != 0) inst.Hue = hueArg;
                    return inst;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static void Add(PlayerBot bot, Item item, int hue)
        {
            if (hue != 0) item.Hue = hue;
            bot.AddItem(item);
        }

        // Put an item in the bot's backpack (not an equip layer). Used for
        // consumables like arrows and reagents that combat/casting draws
        // from the pack. Falls back gracefully if the item can't be made.
        private static void AddToPack(PlayerBot bot, string itemType, int amount)
        {
            var item = TryNewItem(itemType);
            if (item == null) return;
            if (amount > 1 && item.Stackable) item.Amount = amount;
            bot.AddToBackpack(item);
        }
    }
}
