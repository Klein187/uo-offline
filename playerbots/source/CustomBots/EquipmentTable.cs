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
            if (roll < 80)       RobeAndMage(bot, tier, withHat: true);
            else if (roll < 93)  RobeAndMage(bot, tier, withHat: false);
            else if (roll < 99)  { StuddedUp(bot, tier); EquipWeapon(bot, tier, new[] { 10 }); /*staff*/ }
            else                 { PlatesUp(bot, tier); EquipWeapon(bot, tier, new[] { 10 }); }

            // Spellbook — usually present for any mage-class.
            if (Utility.RandomDouble() < 0.85)
            {
                Add(bot, new Spellbook(), IsHighTier(tier) ? RichHue() : 0);
            }

            // Staff weapon if no weapon yet (e.g. the robe paths)
            if (bot.FindItemOnLayer(Layer.OneHanded) == null &&
                bot.FindItemOnLayer(Layer.TwoHanded) == null)
            {
                EquipWeapon(bot, tier, new[] { 10 }); // staff
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
                if (bow != null) Add(bot, bow, 0);
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
            "Server.Items.OrcMask",
            "Server.Items.BearMask",
            "Server.Items.DeerMask",
            "Server.Items.TribalMask",
        };

        private static Item RollRandomHat()
        {
            // Try up to N random picks. If reflection-instantiation fails
            // for one (type doesn't exist or has no parameterless constructor),
            // move to the next.
            for (int tries = 0; tries < 5; tries++)
            {
                string typeName = _hatTypes[Utility.Random(_hatTypes.Length)];
                try
                {
                    var t = Type.GetType(typeName + ", UOContent")
                         ?? Type.GetType(typeName);
                    if (t == null) continue;
                    var inst = Activator.CreateInstance(t) as Item;
                    if (inst != null) return inst;
                }
                catch
                {
                    // Try next type.
                }
            }
            // Last resort: a FloppyHat which we know is in every build.
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
        // Reflection helpers — try to instantiate an item by type name. Used
        // for items that may not exist in every ModernUO build (e.g.
        // SmithHammer, Lute, JesterHat). Returns null if the type isn't
        // found, has no parameterless constructor, or instantiation throws.
        // -------------------------------------------------------------------
        private static Item TryNewItem(string typeName)
        {
            try
            {
                var t = Type.GetType(typeName + ", UOContent")
                     ?? Type.GetType(typeName);
                if (t == null) return null;
                return Activator.CreateInstance(t) as Item;
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
                var t = Type.GetType(typeName + ", UOContent")
                     ?? Type.GetType(typeName);
                if (t == null) return null;
                // Try (int) constructor first (most cloth items)
                var ctor = t.GetConstructor(new[] { typeof(int) });
                if (ctor != null)
                {
                    return ctor.Invoke(new object[] { hueArg }) as Item;
                }
                // Fall back to default ctor + setting hue on the item
                var inst = Activator.CreateInstance(t) as Item;
                if (inst != null && hueArg != 0) inst.Hue = hueArg;
                return inst;
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
    }
}
