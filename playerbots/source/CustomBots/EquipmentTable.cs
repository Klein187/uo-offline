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
                    // Fighters carry a real bandage PILE (no invisible
                    // refills — this stock has to last until the next
                    // provisioner run) and a few healing potions.
                    AddToPack(bot, "Server.Items.Bandage",
                              Utility.RandomMinMax(30, 60));
                    MaybeAddToPack(bot, "Server.Items.HealPotion", 0.5,
                                   Utility.RandomMinMax(1, 4));
                    // ...and often a backup weapon riding in the pack.
                    if (Utility.RandomDouble() < 0.30)
                    {
                        PackSpareWeapon(bot, tier);
                    }
                    break;

                case BotClass.Mage:
                    // Mages carry potions and a spare reagent stash.
                    // (Total refresh, NOT mana potions — mana potions
                    // didn't exist in T2A; a winded mage meditated.)
                    MaybeAddToPack(bot, "Server.Items.HealPotion", 0.4,
                                   Utility.RandomMinMax(1, 3));
                    MaybeAddToPack(bot, "Server.Items.TotalRefreshPotion",
                                   0.3, Utility.RandomMinMax(1, 2));
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
                case BotClass.Smith:
                case BotClass.Tailor:
                case BotClass.Fisherman:
                    // Seed a few finished pieces of the bot's trade so a
                    // freshly-seen artisan already looks established. The set
                    // is data-driven per class (CrafterProfiles.StarterProps)
                    // — the same engine the production loop draws from.
                    SeedCrafterStarterProps(bot);
                    break;

                case BotClass.Bard:
                    MaybeAddToPack(bot, "Server.Items.HealPotion", 0.4,
                                   Utility.RandomMinMax(1, 3));
                    break;

                case BotClass.Lumberjack:
                case BotClass.Miner:
                    // A working stash from the last shift, bandages for the
                    // wolf bites, and camp kit for nights in the wild.
                    AddToPack(bot, cls == BotClass.Miner
                        ? "Server.Items.IronOre" : "Server.Items.Log",
                        Utility.RandomMinMax(3, 15));
                    AddToPack(bot, "Server.Items.Bandage",
                              Utility.RandomMinMax(3, 10));
                    MaybeAddToPack(bot, "Server.Items.Kindling", 0.5,
                                   Utility.RandomMinMax(2, 5));
                    break;

                case BotClass.Tamer:
                    AddToPack(bot, "Server.Items.Bandage",
                              Utility.RandomMinMax(5, 15));
                    // Something to keep the pets keen.
                    MaybeAddToPack(bot, "Server.Items.RawRibs", 0.5,
                                   Utility.RandomMinMax(2, 6));
                    break;
            }

            // --- Spell scrolls — casters carry a working selection ---
            AddSpellScrolls(bot, cls, tier);

            // --- Travel magic — marked recall runes ---
            AddTravelMagic(bot, cls, tier);

            // --- Recall scrolls — the era's bus ticket, but a PAID one.
            // Veterans kept a stack; a fresh character walked everywhere
            // (18gp a scroll was real money). Tier decides the wallet:
            //   Novice/Apprentice — none; they walk like every newbie did.
            //   Journeyman       — sometimes one or two saved-up escapes.
            //   Adept and up     — a working stack, growing with wealth.
            // Casters need none of this — enough Magery casts it.
            // Looped adds (not one stack) so it works whether or not
            // scrolls stack. No invisible refills — the mage shop sells
            // more (BotSupplies, same tier rules).
            int recallScrolls = BotSkillTierHelper.Rank(tier) switch
            {
                0 or 1 => 0,
                2      => Utility.RandomDouble() < 0.30 ? Utility.RandomMinMax(1, 2) : 0,
                3      => Utility.RandomMinMax(1, 3),
                4      => Utility.RandomMinMax(2, 5),
                _      => Utility.RandomMinMax(4, 8),
            };
            for (int i = 0; i < recallScrolls; i++)
            {
                AddToPack(bot, "Server.Items.RecallScroll", 1);
            }

            // --- Potion belt — heal/cure/refresh and the odd extra ---
            AddPotionKit(bot, cls, tier);

            // --- Valuables — gems and jewelry, tier-scaled ---
            AddValuables(bot, cls, tier);

            // --- A bag or pouch with themed contents inside ---
            AddBagOfGoodies(bot, cls, tier);

            // --- The random-person layer — class-blind grab bag ---
            AddRandomPersonLayer(bot, tier);

            // --- Odds and ends most travelers carry ---
            AddOddsAndEnds(bot, cls);
        }

        // -------------------------------------------------------------------
        // The random-person layer. Real players' packs were never on-theme:
        // a mage hauled a magic axe he looted and can't use, a warrior kept
        // scrolls he can't read to sell, a smith bought reagents for a
        // friend. Every bot gets 1-3 draws from this CLASS-BLIND grab bag —
        // it's what breaks the "RPG vendor NPC" feel.
        // -------------------------------------------------------------------
        private static readonly string[] ReagentTypes =
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

        // T2A weapons that plausibly turn up in anyone's pack (loot, a
        // purchase, something to sell).
        private static readonly string[] SpareWeaponTypes =
        {
            "Server.Items.Katana",
            "Server.Items.Cutlass",
            "Server.Items.Mace",
            "Server.Items.Maul",
            "Server.Items.WarAxe",
            "Server.Items.BattleAxe",
            "Server.Items.WarFork",
            "Server.Items.Spear",
            "Server.Items.Bow",
            "Server.Items.QuarterStaff",
        };

        private static readonly string[] BookTypes =
        {
            "Server.Items.RedBook",
            "Server.Items.BlueBook",
            "Server.Items.TanBook",
            "Server.Items.BrownBook",
        };

        private static readonly string[] InstrumentTypes =
        {
            "Server.Items.Lute",
            "Server.Items.Drums",
            "Server.Items.Tambourine",
        };

        // A loose weapon in the pack — possibly magic (looted, bought,
        // held to sell). Class-blind: this is how a mage ends up hauling
        // a katana of might.
        private static void PackSpareWeapon(PlayerBot bot, BotSkillTier tier)
        {
            var w = TryNewItem(SpareWeaponTypes[Utility.Random(SpareWeaponTypes.Length)]);
            if (w == null) return;
            if (w is BaseWeapon weapon)
            {
                MaybeEnchantWeapon(weapon, tier);
            }
            bot.AddToBackpack(w);
        }

        private static void AddRandomPersonLayer(PlayerBot bot, BotSkillTier tier)
        {
            int draws = 1 + Utility.Random(3);
            for (int n = 0; n < draws; n++)
            {
                switch (Utility.Random(12))
                {
                    case 0: // a weapon they don't necessarily use
                        PackSpareWeapon(bot, tier);
                        break;

                    case 1: // scrolls — loot/merchandise, no caster needed
                        int scrolls = Utility.RandomMinMax(1, 2);
                        for (int i = 0; i < scrolls; i++)
                        {
                            AddToPack(bot,
                                ScrollPool[Utility.Random(ScrollPool.Length)].type, 1);
                        }
                        break;

                    case 2: // a small reagent bundle (anyone buys for a friend)
                        int kinds = Utility.RandomMinMax(2, 3);
                        for (int i = 0; i < kinds; i++)
                        {
                            AddToPack(bot,
                                ReagentTypes[Utility.Random(ReagentTypes.Length)],
                                Utility.RandomMinMax(3, 10));
                        }
                        break;

                    case 3: // bandages
                        AddToPack(bot, "Server.Items.Bandage",
                                  Utility.RandomMinMax(5, 15));
                        break;

                    case 4: // a stray potion
                        string[] pots =
                        {
                            "Server.Items.HealPotion",
                            "Server.Items.CurePotion",
                            "Server.Items.RefreshPotion",
                            "Server.Items.AgilityPotion",
                            "Server.Items.StrengthPotion",
                        };
                        AddToPack(bot, pots[Utility.Random(pots.Length)], 1);
                        break;

                    case 5: // a book
                        AddToPack(bot, BookTypes[Utility.Random(BookTypes.Length)], 1);
                        break;

                    case 6: // lockpicks — everyone's a little shady
                        AddToPack(bot, "Server.Items.Lockpick",
                                  Utility.RandomMinMax(2, 6));
                        break;

                    case 7: // a fishing pole — anyone might fish
                        AddToPack(bot, "Server.Items.FishingPole", 1);
                        break;

                    case 8: // an instrument
                        AddToPack(bot,
                            InstrumentTypes[Utility.Random(InstrumentTypes.Length)], 1);
                        break;

                    case 9: // camping kit — bedroll and kindling, pure T2A
                        AddToPack(bot, "Server.Items.Bedroll", 1);
                        AddToPack(bot, "Server.Items.Kindling",
                                  Utility.RandomMinMax(2, 5));
                        break;

                    case 10: // raw materials being hauled somewhere
                        string[] mats =
                        {
                            "Server.Items.IronIngot",
                            "Server.Items.Cloth",
                            "Server.Items.Leather",
                        };
                        AddToPack(bot, mats[Utility.Random(mats.Length)],
                                  Utility.RandomMinMax(5, 15));
                        break;

                    default: // loose coin outside the main purse
                        AddToPack(bot, "Server.Items.Gold",
                                  Utility.RandomMinMax(20, 80));
                        break;
                }
            }
        }

        // -------------------------------------------------------------------
        // Potion belt. Real T2A players rarely left town without heal,
        // cure, and refresh potions — bots shouldn't either. Potion grade
        // scales with tier (lesser -> regular -> greater), and top-tier
        // fighters sometimes pack purple pots.
        // -------------------------------------------------------------------
        private static void AddPotionKit(PlayerBot bot, BotClass cls, BotSkillTier tier)
        {
            if (Utility.RandomDouble() >= 0.55) return;

            int rank = BotSkillTierHelper.Rank(tier);
            string grade = rank <= 1 ? "Lesser" : rank <= 4 ? "" : "Greater";

            AddToPack(bot, $"Server.Items.{grade}HealPotion",
                      Utility.RandomMinMax(1, 3));

            if (Utility.RandomDouble() < 0.50)
            {
                AddToPack(bot, $"Server.Items.{grade}CurePotion",
                          Utility.RandomMinMax(1, 2));
            }

            if (Utility.RandomDouble() < 0.40)
            {
                AddToPack(bot, rank >= 4
                        ? "Server.Items.TotalRefreshPotion"
                        : "Server.Items.RefreshPotion",
                    Utility.RandomMinMax(1, 2));
            }

            if (Utility.RandomDouble() < 0.15)
            {
                AddToPack(bot, Utility.RandomBool()
                    ? "Server.Items.AgilityPotion"
                    : "Server.Items.StrengthPotion", 1);
            }

            MaybeAddToPack(bot, "Server.Items.NightSightPotion", 0.10, 1);

            // Purple pots — the veteran fighter's surprise.
            bool fighter = cls is BotClass.Warrior or BotClass.Fencer
                or BotClass.Archer or BotClass.Ranger;
            if (fighter && rank >= 5 && Utility.RandomDouble() < 0.15)
            {
                AddToPack(bot, "Server.Items.GreaterExplosionPotion",
                          Utility.RandomMinMax(1, 2));
            }
        }

        // -------------------------------------------------------------------
        // A bag/pouch WITH CONTENTS — nothing reads "real player" like
        // opening a snooped backpack and finding another bag with someone's
        // kit organized inside it. One themed container per bot (40%);
        // thieves also carry someone ELSE'S pouch.
        // -------------------------------------------------------------------
        private static void AddBagOfGoodies(PlayerBot bot, BotClass cls, BotSkillTier tier)
        {
            int rank = BotSkillTierHelper.Rank(tier);

            if (Utility.RandomDouble() < 0.40)
            {
                var bag = NewCarryBag();

                // Mages favor a spare reagent pouch; everyone else rolls a
                // theme.
                int theme = cls == BotClass.Mage && Utility.RandomDouble() < 0.5
                    ? 4
                    : Utility.Random(4);

                switch (theme)
                {
                    case 0: // rainy-day purse
                        AddToBag(bag, "Server.Items.Gold",
                                 Utility.RandomMinMax(40, 60 + 60 * rank));
                        if (Utility.RandomDouble() < 0.5)
                        {
                            AddToBag(bag, GemTypes[Utility.Random(GemTypes.Length)], 1);
                        }
                        break;

                    case 1: // traveler's kit
                        AddToBag(bag, FoodTypes[Utility.Random(FoodTypes.Length)],
                                 Utility.RandomMinMax(1, 2));
                        AddToBag(bag, FoodTypes[Utility.Random(FoodTypes.Length)], 1);
                        AddToBag(bag, "Server.Items.Torch", 1);
                        AddToBag(bag, "Server.Items.Bandage",
                                 Utility.RandomMinMax(3, 10));
                        break;

                    case 2: // potion pouch
                        AddToBag(bag, "Server.Items.HealPotion",
                                 Utility.RandomMinMax(1, 2));
                        AddToBag(bag, "Server.Items.CurePotion", 1);
                        if (Utility.RandomDouble() < 0.5)
                        {
                            AddToBag(bag, "Server.Items.RefreshPotion", 1);
                        }
                        break;

                    case 3: // trinket bag
                        AddToBag(bag, "Server.Items.Dices", 1);
                        AddToBag(bag, GemTypes[Utility.Random(GemTypes.Length)], 1);
                        if (Utility.RandomDouble() < 0.4)
                        {
                            AddToBag(bag, JewelryTypes[Utility.Random(JewelryTypes.Length)], 1);
                        }
                        break;

                    default: // mage's spare reagent pouch
                        AddToBag(bag, "Server.Items.BlackPearl",
                                 Utility.RandomMinMax(5, 15));
                        AddToBag(bag, "Server.Items.MandrakeRoot",
                                 Utility.RandomMinMax(5, 15));
                        AddToBag(bag, "Server.Items.SulfurousAsh",
                                 Utility.RandomMinMax(5, 15));
                        AddToBag(bag, "Server.Items.SpidersSilk",
                                 Utility.RandomMinMax(5, 15));
                        break;
                }

                if (bag.Items.Count > 0)
                {
                    bot.AddToBackpack(bag);
                }
                else
                {
                    bag.Delete();
                }
            }

            // The thief's second pouch is not, strictly speaking, theirs.
            if (cls == BotClass.Thief)
            {
                var pinched = NewCarryBag();
                AddToBag(pinched, "Server.Items.Gold",
                         Utility.RandomMinMax(60, 250));
                if (Utility.RandomDouble() < 0.6)
                {
                    AddToBag(pinched, JewelryTypes[Utility.Random(JewelryTypes.Length)], 1);
                }
                if (Utility.RandomDouble() < 0.4)
                {
                    AddToBag(pinched, GemTypes[Utility.Random(GemTypes.Length)], 1);
                }
                bot.AddToBackpack(pinched);
            }
        }

        private static Container NewCarryBag() =>
            Utility.RandomBool() ? (Container)new Bag() : new Pouch();

        // Drop an item into a carry bag; reflection-graceful like AddToPack.
        private static void AddToBag(Container bag, string itemType, int amount)
        {
            var item = TryNewItem(itemType);
            if (item == null) return;
            if (amount > 1 && item.Stackable) item.Amount = amount;
            bag.DropItem(item);
        }

        // -------------------------------------------------------------------
        // Spell scrolls. Mages and Healers always carry a few (scribed or
        // bought — a caster's working stock); Bards dabble. The pool is
        // gated by tier so a Novice carries Cure and Teleport scrolls while
        // a Grandmaster's pack turns up Flamestrike and Gate Travel.
        // -------------------------------------------------------------------
        private static readonly (string type, int minRank)[] ScrollPool =
        {
            ("Server.Items.CureScroll",         0),
            ("Server.Items.TeleportScroll",     0),
            ("Server.Items.FireballScroll",     1),
            ("Server.Items.RecallScroll",       1),
            ("Server.Items.GreaterHealScroll",  2),
            ("Server.Items.LightningScroll",    2),
            ("Server.Items.MagicReflectScroll", 3),
            ("Server.Items.EnergyBoltScroll",   4),
            ("Server.Items.MarkScroll",         4),
            ("Server.Items.FlamestrikeScroll",  5),
            ("Server.Items.GateTravelScroll",   5),
        };

        private static void AddSpellScrolls(PlayerBot bot, BotClass cls, BotSkillTier tier)
        {
            bool caster = cls == BotClass.Mage || cls == BotClass.Healer;
            bool dabbler = cls == BotClass.Bard;
            if (!caster && !(dabbler && Utility.RandomDouble() < 0.30)) return;

            int rank = BotSkillTierHelper.Rank(tier);

            // Eligible slice of the pool for this tier.
            int eligible = 0;
            for (int i = 0; i < ScrollPool.Length; i++)
            {
                if (ScrollPool[i].minRank <= rank) eligible++;
            }
            if (eligible == 0) return;

            // Casters carry more scrolls at higher tiers; a bard just one.
            int count = dabbler ? 1 : 1 + rank / 2;
            for (int n = 0; n < count; n++)
            {
                var pick = ScrollPool[Utility.Random(eligible)];
                AddToPack(bot, pick.type, 1);
            }
        }

        // -------------------------------------------------------------------
        // Travel magic. Casters carry recall runes MARKED for real places —
        // snoop a mage and find "a recall rune for Britain Bank". Rune
        // targets come from the live destination catalog, so they're
        // genuine locations. Master+ mages sometimes carry a runebook, and
        // any bot may have picked up a blank rune somewhere.
        // -------------------------------------------------------------------
        private static void AddTravelMagic(PlayerBot bot, BotClass cls, BotSkillTier tier)
        {
            int rank = BotSkillTierHelper.Rank(tier);

            // NO runebooks — those arrived with UO:R (2000). In T2A you
            // carried loose marked runes, so that's what bots carry.
            if (cls == BotClass.Mage)
            {
                if (rank >= 2 && Utility.RandomDouble() < 0.70)
                {
                    // A working mage keeps a small rune collection; the
                    // best of them carry a proper handful.
                    int runes = Utility.RandomMinMax(1, rank >= 5 ? 4 : 2);
                    for (int i = 0; i < runes; i++)
                    {
                        AddMarkedRune(bot);
                    }
                }
            }
            else if (cls == BotClass.Healer)
            {
                if (Utility.RandomDouble() < 0.40)
                {
                    AddMarkedRune(bot);
                }
            }
            else if (Utility.RandomDouble() < 0.20)
            {
                // Everyone else: 1-in-5 keeps a single marked rune — the
                // classic "rune to home" every real player carried.
                AddMarkedRune(bot);
            }

            // Anyone might have a blank rune rattling around the pack.
            MaybeAddToPack(bot, "Server.Items.RecallRune", 0.08, 1);
        }

        // A recall rune marked for a random authored destination. Falls
        // back to a blank rune when the catalog isn't loaded yet.
        private static void AddMarkedRune(PlayerBot bot)
        {
            try
            {
                BotDestination dest = null;
                int seen = 0;
                foreach (var d in DestinationCatalog.All)
                {
                    // Reservoir-pick a random destination in one pass.
                    seen++;
                    if (Utility.Random(seen) == 0) dest = d;
                }

                var rune = new RecallRune();
                if (dest != null)
                {
                    rune.Marked      = true;
                    rune.Target      = dest.ArrivalPoint ?? dest.Location;
                    rune.TargetMap   = Map.Felucca;
                    rune.Description = dest.Name;
                }
                bot.AddToBackpack(rune);
            }
            catch { }
        }

        // -------------------------------------------------------------------
        // Valuables — the "worth robbing" layer. Gem and jewelry chances
        // scale with tier (rich veterans, threadbare novices). Thieves are
        // the exception: whatever their skill, their packs are suspiciously
        // full of OTHER people's valuables.
        // -------------------------------------------------------------------
        private static readonly string[] GemTypes =
        {
            "Server.Items.Amethyst",
            "Server.Items.Citrine",
            "Server.Items.Diamond",
            "Server.Items.Emerald",
            "Server.Items.Ruby",
            "Server.Items.Sapphire",
            "Server.Items.StarSapphire",
            "Server.Items.Tourmaline",
        };

        private static readonly string[] JewelryTypes =
        {
            "Server.Items.GoldRing",
            "Server.Items.SilverRing",
            "Server.Items.GoldBracelet",
            "Server.Items.GoldNecklace",
        };

        private static void AddValuables(PlayerBot bot, BotClass cls, BotSkillTier tier)
        {
            int rank = BotSkillTierHelper.Rank(tier);
            bool thief = cls == BotClass.Thief;

            // Gems: a handful, likelier and deeper with tier.
            double gemChance = thief ? 0.90 : 0.15 + 0.08 * rank;
            if (Utility.RandomDouble() < gemChance)
            {
                int gems = Utility.RandomMinMax(1, thief ? 4 : 1 + rank / 2);
                for (int i = 0; i < gems; i++)
                {
                    AddToPack(bot, GemTypes[Utility.Random(GemTypes.Length)], 1);
                }
            }

            // Jewelry: one loose piece, rare except on thieves.
            double jewelChance = thief ? 0.60 : 0.05 + 0.04 * rank;
            if (Utility.RandomDouble() < jewelChance)
            {
                AddToPack(bot, JewelryTypes[Utility.Random(JewelryTypes.Length)], 1);
            }
        }

        // -------------------------------------------------------------------
        // Odds and ends — the human clutter that makes a snooped pack read
        // like a person: half a meal, a torch, dice, a spyglass.
        // -------------------------------------------------------------------
        private static readonly string[] FoodTypes =
        {
            "Server.Items.BreadLoaf",
            "Server.Items.Apple",
            "Server.Items.Pear",
            "Server.Items.CheeseWedge",
            "Server.Items.FishSteak",
        };

        private static void AddOddsAndEnds(PlayerBot bot, BotClass cls)
        {
            // Most people carry SOMETHING to eat on the road.
            if (Utility.RandomDouble() < 0.60)
            {
                AddToPack(bot, FoodTypes[Utility.Random(FoodTypes.Length)],
                          Utility.RandomMinMax(1, 3));
            }

            MaybeAddToPack(bot, "Server.Items.Torch", 0.3, 1);

            // Trinkets. Scouts favor a spyglass; sailors' tools and tavern
            // dice turn up on anyone.
            double spyglassChance =
                cls == BotClass.Ranger || cls == BotClass.Archer ? 0.15 : 0.05;
            MaybeAddToPack(bot, "Server.Items.Spyglass", spyglassChance, 1);
            MaybeAddToPack(bot, "Server.Items.Dices", 0.08, 1);
            MaybeAddToPack(bot, "Server.Items.Sextant", 0.04, 1);

            // Spare equipment riding loose in the pack: a backup dagger,
            // a folded cloak, a spare bandana.
            MaybeAddToPack(bot, "Server.Items.Dagger", 0.20, 1);
            if (Utility.RandomDouble() < 0.12)
            {
                var spareCloak = TryNewItem("Server.Items.Cloak", PaletteHue());
                if (spareCloak != null) bot.AddToBackpack(spareCloak);
            }
            if (Utility.RandomDouble() < 0.10)
            {
                var spareBandana = TryNewItem("Server.Items.Bandana", PaletteHue());
                if (spareBandana != null) bot.AddToBackpack(spareBandana);
            }
        }

        // Add a spread of the 8 standard magery reagents to the pack.
        // Amount per reagent scales with skill tier — a higher-skill caster
        // casts more often and with costlier spells, so carries a deeper
        // reagent supply. Each reagent gets its tier base ±30% spread.
        private static void AddReagentStash(PlayerBot bot, BotSkillTier tier)
        {
            // Era-sized stash — combat casting genuinely consumes these
            // and nothing refills them but a real mage-shop run.
            int regBase = tier switch
            {
                BotSkillTier.Novice      => 25,
                BotSkillTier.Apprentice  => 35,
                BotSkillTier.Journeyman  => 45,
                BotSkillTier.Adept       => 60,
                BotSkillTier.Expert      => 75,
                BotSkillTier.Master      => 90,
                BotSkillTier.Grandmaster => 100,
                _                        => 40,
            };

            foreach (var r in ReagentTypes)
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

        // Seed a crafter's starter props from its subtype profile — a few
        // finished goods so a fresh crafter isn't empty-handed. Factories
        // build real item instances (compile-checked, not reflection).
        private static void SeedCrafterStarterProps(PlayerBot bot)
        {
            var profile = CrafterProfiles.For(bot.Class);
            foreach (var make in profile.StarterProps)
            {
                Item item;
                try { item = make?.Invoke(bot); }
                catch { item = null; }

                if (item != null)
                {
                    bot.AddToBackpack(item);
                }
            }
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
                case BotClass.Crafter:
                case BotClass.Smith:
                case BotClass.Tailor:
                case BotClass.Fisherman: RollCrafterLook(bot, tier); break;
                case BotClass.Lumberjack:
                case BotClass.Miner:   RollGathererLook(bot, tier);  break;
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
            // The LOOK never rolls a weapon — armor/robe only, hands free
            // for casting. Tank-mage variants get their halberd/mace/spear
            // separately via EquipTankMageWeapon (PlayerBot calls it after
            // the outfit roll); pure scribe-mages stay weaponless.
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
                Add(bot, book, 0);   // spellbooks weren't dyeable in T2A
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

            EquipWeapon(bot, tier, new[] { 20, 21, 22 }); // kryss/war fork/spear
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

            // Bow — Archers should have one. Plain wood (no dyed bows),
            // but it can be a MAGIC bow.
            var archBow = TryNewItem("Server.Items.Bow");
            if (archBow != null)
            {
                if (archBow is BaseWeapon bowWeapon) MaybeEnchantWeapon(bowWeapon, tier);
                Add(bot, archBow, 0);
            }

            if (bot.FindItemOnLayer(Layer.TwoHanded) == null)
            {
                var xbow = TryNewItem("Server.Items.Crossbow");
                if (xbow != null)
                {
                    if (xbow is BaseWeapon xbowWeapon) MaybeEnchantWeapon(xbowWeapon, tier);
                    Add(bot, xbow, 0);
                }
            }

            // Ammunition — a ranged weapon needs ammo in the pack or it
            // won't fire, and there are NO invisible refills: this is the
            // era-sized quiver stock that lasts until the bowyer run.
            AddToPack(bot, "Server.Items.Arrow", Utility.RandomMinMax(120, 200));
            AddToPack(bot, "Server.Items.Bolt",  Utility.RandomMinMax(50, 100));
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

        // -------------------------------------------------------------------
        // GATHERER — hardy work clothes and the trade tool IN HAND. The
        // hatchet/pickaxe is a real weapon (Swords skill), so the same tool
        // that fells trees fends off wolves.
        // -------------------------------------------------------------------
        private static void RollGathererLook(PlayerBot bot, BotSkillTier tier)
        {
            int roll = Utility.Random(100);
            //  0–59: commoner work clothes
            // 60–89: leather (been out in the wild a while)
            // 90–99: studded (the veterans)
            if (roll < 60)      CommonerUp(bot, tier);
            else if (roll < 90) LeatherUp(bot, tier);
            else                StuddedUp(bot, tier);

            // Half apron — the working man's badge.
            if (Utility.RandomDouble() < 0.40)
            {
                var apron = TryNewItem("Server.Items.HalfApron", DrabHue());
                if (apron != null) Add(bot, apron, 0);
            }

            // The tool, equipped.
            var tool = TryNewItem(bot.Class == BotClass.Miner
                ? "Server.Items.Pickaxe"
                : "Server.Items.Hatchet", 0);
            if (tool != null && !bot.EquipItem(tool))
            {
                tool.Delete();
            }
        }

        private static void RollCrafterLook(PlayerBot bot, BotSkillTier tier)
        {
            // FISHERMAN — a weathered dockworker look with real variety so a
            // row of them doesn't read as identical peasants.
            if (bot.Class == BotClass.Fisherman)
            {
                // Clothes: mostly muted, but a good third in brighter
                // sea-faring colors. CommonerUp already varies shirt/doublet,
                // pants/skirt, hues, and footwear per bot.
                CommonerUp(bot, tier, bright: Utility.RandomDouble() < 0.35);

                // A gutting apron, sometimes.
                if (Utility.RandomDouble() < 0.30)
                {
                    Item apron = Utility.RandomBool() ? (Item)new FullApron() : new HalfApron();
                    Add(bot, apron, DrabHue());
                }

                // Varied headgear — not everyone in the same straw hat.
                RollFisherHat(bot);

                // An occasional weathered cloak.
                if (Utility.RandomDouble() < 0.20)
                {
                    var cloak = TryNewItem("Server.Items.Cloak", DrabHue());
                    if (cloak != null) Add(bot, cloak, 0);
                }

                // Equip the hand tool (the fishing pole).
                EquipCrafterTool(bot);
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
                int hue = IsHighTier(tier) ? RichHue() : DrabHue();
                Add(bot, apron, hue);
            }

            // Equip the subtype's hand tool. Smiths get a hammer; tailors
            // have no hand-held tool (their sewing kit / scissors ride in the
            // pack via StarterProps), so this no-ops for them.
            EquipCrafterTool(bot);
        }

        // Equip the bot's crafter subtype hand tool from its profile, if it
        // has one (smith hammer, fishing pole). Tailors have a null Tool.
        private static void EquipCrafterTool(PlayerBot bot)
        {
            var tool = CrafterProfiles.For(bot.Class).Tool?.Invoke(bot);
            if (tool != null)
            {
                Add(bot, tool, 0);
            }
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
                    if (bow is BaseWeapon rangerBow) MaybeEnchantWeapon(rangerBow, tier);
                    Add(bot, bow, 0);
                    // Bow needs arrows in the pack to fire — era-sized
                    // stock, restocked only by a real bowyer visit.
                    AddToPack(bot, "Server.Items.Arrow",
                              Utility.RandomMinMax(120, 200));
                }
                else EquipWeapon(bot, tier, new[] { 0, 1 });
            }
            else
            {
                EquipWeapon(bot, tier, new[] { 0, 1 });
            }
        }

        // -------------------------------------------------------------------
        // T2A ITEM MAGIC + ORE METALS
        //
        // Magic gear uses the REAL era system: weapons roll damage
        // (Ruin/Might/Force/Power/Vanquishing) and accuracy (Accurate ..
        // Supremely Accurate) levels; armor rolls protection (Defense ..
        // Invulnerability); both may add a durability level. Chance and
        // ceiling scale with tier — a Novice almost never carries magic,
        // a Grandmaster often does, but Vanq/Invulnerability stay rare
        // even then, as they were on OSI.
        //
        // Colored metal armor comes from the ORE it was smithed from
        // (BaseArmor.Resource → the real dull copper/shadow/gold/valorite
        // hues), never from dye — plate was not dyeable in T2A.
        // -------------------------------------------------------------------

        // The colored-ore ladder, common to rare. Iron (the default) is
        // not listed. Order matches the mining skill progression.
        private static readonly CraftResource[] OreLadder =
        {
            CraftResource.DullCopper,
            CraftResource.ShadowIron,
            CraftResource.Copper,
            CraftResource.Bronze,
            CraftResource.Gold,
            CraftResource.Agapite,
            CraftResource.Verite,
            CraftResource.Valorite,
        };

        // Roll the metal for a whole suit (a smithed suit is one ore, so
        // the pieces match). Mostly plain iron; colored ore gets likelier
        // and reaches deeper down the ladder with tier.
        private static CraftResource RollMetal(BotSkillTier tier)
        {
            int rank = BotSkillTierHelper.Rank(tier);

            // Novice ~10% colored at all; Grandmaster ~46%.
            if (Utility.RandomDouble() >= 0.10 + 0.06 * rank)
            {
                return CraftResource.Iron;
            }

            // Ladder depth is tier-capped (a Novice might own dull copper;
            // valorite is Grandmaster territory), weighted shallow.
            int cap = 2 + rank;
            if (cap > OreLadder.Length) cap = OreLadder.Length;

            int idx = 0;
            while (idx < cap - 1 && Utility.RandomDouble() < 0.45)
            {
                idx++;
            }
            return OreLadder[idx];
        }

        // Roll a magic level 1..5 (enum values above Regular), weighted
        // hard toward the low end and tier-capped: low tiers top out at
        // Might/Guarding, only Master+ can roll Vanq/Invulnerability.
        private static int RollMagicLevel(int rank)
        {
            int cap = rank switch
            {
                <= 1 => 2,
                <= 3 => 3,
                4    => 4,
                _    => 5,
            };

            int level = 1;
            while (level < cap && Utility.RandomDouble() < 0.4)
            {
                level++;
            }
            return level;
        }

        private static void MaybeEnchantWeapon(BaseWeapon w, BotSkillTier tier)
        {
            int rank = BotSkillTierHelper.Rank(tier);

            // Novice ~6% magic weapon; Grandmaster ~48%.
            if (Utility.RandomDouble() >= 0.06 + 0.07 * rank) return;

            w.DamageLevel = (WeaponDamageLevel)RollMagicLevel(rank);
            if (Utility.RandomDouble() < 0.5)
            {
                w.AccuracyLevel = (WeaponAccuracyLevel)RollMagicLevel(rank);
            }
            if (Utility.RandomDouble() < 0.3)
            {
                w.DurabilityLevel = (WeaponDurabilityLevel)RollMagicLevel(rank);
            }
            // Bots wear their gear openly — show the name, not "a magic item".
            w.Identified = true;
        }

        private static void MaybeEnchantArmor(BaseArmor a, BotSkillTier tier)
        {
            int rank = BotSkillTierHelper.Rank(tier);

            // Per PIECE, so a suit averages roughly one magic piece at the
            // top tiers and almost never at the bottom.
            if (Utility.RandomDouble() >= 0.02 + 0.03 * rank) return;

            a.ProtectionLevel = (ArmorProtectionLevel)RollMagicLevel(rank);
            if (Utility.RandomDouble() < 0.3)
            {
                a.Durability = (ArmorDurabilityLevel)RollMagicLevel(rank);
            }
            a.Identified = true;
        }

        // Equip one armor piece: ore resource for metal suits (sets the
        // real ore hue), optional leather tint, and an item-magic roll.
        private static void AddArmor(PlayerBot bot, Item item, BotSkillTier tier,
                                     CraftResource? metal = null, int hue = 0)
        {
            if (item is BaseArmor armor)
            {
                if (metal.HasValue && metal.Value != CraftResource.Iron)
                {
                    try { armor.Resource = metal.Value; } catch { }
                }
                MaybeEnchantArmor(armor, tier);
            }
            if (hue != 0) item.Hue = hue;
            bot.AddItem(item);
        }

        // -------------------------------------------------------------------
        // ARMOR ARCHETYPES — building blocks called by class roll functions.
        // -------------------------------------------------------------------

        private static void PlatesUp(PlayerBot bot, BotSkillTier tier)
        {
            var metal = RollMetal(tier);
            AddArmor(bot, bot.Female ? new FemalePlateChest() : (Item)new PlateChest(), tier, metal);
            AddArmor(bot, new PlateLegs(),   tier, metal);
            AddArmor(bot, new PlateArms(),   tier, metal);
            AddArmor(bot, new PlateGloves(), tier, metal);
            AddArmor(bot, new PlateGorget(), tier, metal);
            // Plate Helm only sometimes — let the accessories pass add a hat instead
            if (Utility.RandomDouble() < 0.45)
            {
                AddArmor(bot, new PlateHelm(), tier, metal);
            }
            Add(bot, new Boots(), 0);
        }

        private static void ChainsUp(PlayerBot bot, BotSkillTier tier)
        {
            var metal = RollMetal(tier);
            AddArmor(bot, new ChainChest(), tier, metal);
            AddArmor(bot, new ChainLegs(),  tier, metal);
            if (Utility.RandomDouble() < 0.6)
            {
                AddArmor(bot, new ChainCoif(), tier, metal);
            }
            Add(bot, new Boots(), 0);
        }

        private static void StuddedUp(PlayerBot bot, BotSkillTier tier, bool darkColors = false, bool foresty = false)
        {
            // Leather tones only — studded wasn't dyeable rainbow in T2A.
            // Thieves get a dark set, rangers an earthy one, everyone else
            // plain leather.
            int hue = darkColors ? DarkHue() : foresty ? ForestHue() : 0;

            AddArmor(bot, bot.Female ? new FemaleStuddedChest() : (Item)new StuddedChest(), tier, null, hue);
            AddArmor(bot, new StuddedLegs(),   tier, null, hue);
            AddArmor(bot, new StuddedArms(),   tier, null, hue);
            AddArmor(bot, new StuddedGloves(), tier, null, hue);
            AddArmor(bot, new StuddedGorget(), tier, null, hue);
            Add(bot, new Boots(), 0);
        }

        private static void LeatherUp(PlayerBot bot, BotSkillTier tier, bool darkColors = false, bool foresty = false)
        {
            int hue = darkColors ? DarkHue() : foresty ? ForestHue() : 0;

            AddArmor(bot, bot.Female ? new FemaleLeatherChest() : (Item)new LeatherChest(), tier, null, hue);
            AddArmor(bot, new LeatherLegs(),   tier, null, hue);
            AddArmor(bot, new LeatherArms(),   tier, null, hue);
            AddArmor(bot, new LeatherGloves(), tier, null, hue);
            AddArmor(bot, new LeatherGorget(), tier, null, hue);
            Add(bot, new Boots(), 0);
        }

        // Robe-and-mage: robe + wizard's hat + sandals, classic mage look
        private static void RobeAndMage(PlayerBot bot, BotSkillTier tier,
                                       bool withHat = true, bool healerWhite = false)
        {
            int robeHue;
            if (healerWhite) robeHue = 0; // pure white default
            else if (IsHighTier(tier)) robeHue = RichHue();
            else robeHue = DrabHue();

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
                          (IsHighTier(tier) ? RichHue() : DrabHue());
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
                pantsHue = DrabHue();
            }
            else
            {
                topHue   = DrabHue();
                pantsHue = DrabHue();
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

        // T2A tank mages carry a real weapon alongside the spellbook.
        // Called after the outfit roll for Mage-class bots (the mage look
        // itself never rolls weapons); the weapon matches whichever combat
        // skill the template variant rolled. Swords = the famous halberd.
        // Casting auto-pockets it (pre-AOS ClearHands) — AdventurerBehavior
        // re-arms it between casts.
        public static void EquipTankMageWeapon(PlayerBot bot, BotSkillTier tier)
        {
            double sw = bot.Skills[SkillName.Swords].Base;
            double mc = bot.Skills[SkillName.Macing].Base;
            double fn = bot.Skills[SkillName.Fencing].Base;
            double best = Math.Max(sw, Math.Max(mc, fn));
            if (best < BotSkillTemplates.TankWeaponSkillMin)
            {
                return; // scribe mage (or too green to wield) — no weapon
            }

            // All two-handed on purpose: the spellbook rides on the
            // OneHanded layer, so a one-handed weapon can't equip past it
            // (and RearmTankWeapon would fail the same way mid-fight).
            BaseWeapon w = best == sw ? new Halberd()        // the "Hally Mage"
                         : best == mc ? new WarHammer()
                         : (BaseWeapon)new Spear();

            MaybeEnchantWeapon(w, tier);
            Add(bot, w, 0);
        }

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
                21 => new WarFork(),   // T2A fencing — no wakizashi (SE-era)
                22 => new ShortSpear(),
                _  => new Dagger()
            };
            MaybeEnchantWeapon(w, tier);
            Add(bot, w, 0);
        }

        private static void EquipShield(PlayerBot bot, BotSkillTier tier)
        {
            Item shield = Utility.Random(4) switch
            {
                0 => new WoodenShield(),
                1 => new MetalKiteShield(),
                2 => new BronzeShield(),
                _ => new HeaterShield()
            };
            // Magic shields existed too (a heater of guarding).
            if (shield is BaseArmor shieldArmor)
            {
                MaybeEnchantArmor(shieldArmor, tier);
            }
            Add(bot, shield, 0);
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

            // -- HALF-APRON (kitchen-y look, secondary chance for non-artisans;
            //    artisans already get their apron in RollCrafterLook) --
            if (cls != BotClass.Crafter && !BotClassHelper.IsArtisan(cls) &&
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
            // T2A-era only: no ninja hoods (Samurai Empire, 2004) and no
            // bear/deer/tribal masks (savage era, UO:R 2000). OrcHelm
            // stays — orcish helms dropped from orcs since launch.
            "Server.Items.OrcHelm",
        };

        // Headgear pool for fishermen — practical, weathered hats. ~20% go
        // bareheaded. Hue varies so even matching hats look different.
        private static readonly string[] _fisherHats =
        {
            "Server.Items.StrawHat",
            "Server.Items.TallStrawHat",
            "Server.Items.WideBrimHat",
            "Server.Items.FloppyHat",
            "Server.Items.TricorneHat",
            "Server.Items.Bandana",
            "Server.Items.Skullcap",
            "Server.Items.Bonnet",
        };

        private static void RollFisherHat(PlayerBot bot)
        {
            if (Utility.RandomDouble() < 0.20)
            {
                return; // bareheaded
            }

            var hat = TryNewItem(_fisherHats[Utility.Random(_fisherHats.Length)]);
            if (hat != null)
            {
                Add(bot, hat, DrabHue());
            }
        }

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
        //
        // ERA RULE: in T2A the ONLY way clothing got color was the dye tub,
        // and the tub's spread is hues 2–1001 (0x0002–0x03E9). Everything
        // above that — the 0x0400+ "special dye" tones, the 1801–1908
        // neutral band Utility.RandomNeutralHue() returns — arrived with
        // later-era reward dyes and reads as a time traveler to anyone who
        // played in 1998. Every helper below stays inside the tub range.
        // The single exception: TRUE BLACK (0x0001), the 1998 holiday
        // black dye tub — real in-era, rare, and prized (which is exactly
        // why the thieves wear it).
        //
        // (Metal armor color comes from ORE — CraftResource, era-correct —
        // and horses keep RandomNeutralHue: that's a natural coat, not dye.)

        private static bool IsHighTier(BotSkillTier t) => (int)t >= (int)BotSkillTier.Expert;

        private const int TrueBlack = 0x0001; // holiday black dye tub, 1998

        // Muted working-clothes tones — the greys, browns and faded shades
        // from the drab end of the dye tub. Replaces RandomNeutralHue on
        // clothing (that helper's 1801+ band isn't tub-reachable in era).
        private static int DrabHue() => Utility.Random(8) switch
        {
            0 => 0x0002, // undyed grey
            1 => 0x0004, // pale grey
            2 => 0x0007, // stone grey
            3 => 0x0009, // charcoal
            4 => 0x0023, // brown
            5 => 0x0026, // ash
            6 => 0x0029, // faded rose
            _ => 0x0035, // dusty orange
        };

        // Rich, saturated colors for high-tier gear — the DEEP end of the
        // tub's color families.
        private static int RichHue() => Utility.Random(8) switch
        {
            0 => 0x0022, // deep red
            1 => 0x0248, // forest green
            2 => 0x038A, // royal blue
            3 => 0x008A, // golden tan
            4 => 0x03B2, // purple
            5 => 0x0027, // crimson
            6 => 0x0026, // ash gray
            _ => 0x02D8, // teal
        };

        // Wider palette including pastels — for hats, cloaks, sashes,
        // things people use for self-expression. All tub-reachable.
        private static int PaletteHue() => Utility.Random(20) switch
        {
            0  => 0x0022, // deep red
            1  => 0x0248, // forest green
            2  => 0x038A, // royal blue
            3  => 0x008A, // golden tan
            4  => 0x03B2, // purple
            5  => 0x0027, // crimson
            6  => 0x02D8, // teal
            7  => 0x002B, // pale yellow
            8  => 0x0021, // soft pink
            9  => 0x0085, // ivory / pale gold
            10 => 0x0035, // dusty orange
            11 => 0x0288, // dark green
            12 => 0x0240, // emerald
            13 => 0x023E, // bright cyan
            14 => 0x011D, // hot pink
            15 => 0x0386, // sky blue
            16 => TrueBlack, // the 1998 black — rare flex
            17 => 0x0026, // ash
            18 => 0x0023, // brown
            _  => 0x0037, // rust
        };

        // Dark colors for thieves. True black leads — the era's most
        // recognizable "up to no good" outfit.
        private static int DarkHue() => Utility.Random(6) switch
        {
            0 => TrueBlack,
            1 => 0x0026, // ash
            2 => 0x0044, // dark navy
            3 => 0x0023, // brown
            4 => 0x0288, // dark green
            _ => 0x0027, // wine red
        };

        // Forest greens / earth tones for rangers
        private static int ForestHue() => Utility.Random(6) switch
        {
            0 => 0x0248, // forest green
            1 => 0x0240, // emerald
            2 => 0x0290, // mossy green
            3 => 0x0288, // dark green
            4 => 0x0023, // brown
            _ => 0x0037, // rust
        };

        // Bright/loud colors for bards
        private static int BrightHue() => Utility.Random(8) switch
        {
            0 => 0x011D, // hot pink
            1 => 0x023E, // bright cyan
            2 => 0x0035, // dusty orange
            3 => 0x0028, // bright red
            4 => 0x0055, // turquoise
            5 => 0x03B2, // purple
            6 => 0x002B, // pale yellow
            _ => 0x02D8, // teal
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

                // Fall back to the single-int ctor — but that int means
                // AMOUNT on stackables (Gold, Bandage, Kindling...) and
                // HUE elsewhere. Passing 0 to an amount-ctor trips the
                // engine's "Item.Amount <= 0" error on EVERY construction
                // (the storm of ERR lines at each spawn wave). Inspect the
                // parameter's name and pass a value that's safe for what
                // it actually is.
                var ctorInt = t.GetConstructor(new[] { typeof(int) });
                if (ctorInt != null)
                {
                    bool isAmount = string.Equals(
                        ctorInt.GetParameters()[0].Name, "amount",
                        StringComparison.OrdinalIgnoreCase);
                    return ctorInt.Invoke(new object[] { isAmount ? 1 : 0 }) as Item;
                }

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
