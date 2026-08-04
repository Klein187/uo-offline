// =========================================================================
// BotSkillTemplate.cs — Real T2A skill templates for each BotClass.
//
// T2A rules: the stat cap was 225 TOTAL with each individual stat capped
// at 100, and the skill cap was 700 — exactly seven grandmaster skills.
// Real players built coherent 7-skill templates around those caps, and
// the famous ones are reproduced here:
//
//   Tank Mage   (Mage class)    — 100/25/100. Magery/Eval/Med/Resist/
//       Wrestling plus a weapon line + Tactics. Rolled per bot: most are
//       Swords ("Hally Mage" — the king of T2A), some Macing or Fencing,
//       and a share stay pure scribe-mages with Inscription instead.
//   Pure Dexxer (Warrior/Fencer) — 100/100/25. Weapon/Tactics/Anatomy/
//       Healing/Resist/Wrestling, plus utility Magery (Recall, Cure).
//   Lumberjack  — the era's highest melee burst: GM Swords backed by the
//       GM Lumberjacking damage bonus on an axe.
//   Bard / Tamer / Smith / Fisherman — the classic PvM and working
//       templates, straight from the era.
//
// Class is the SET of skills; SkillTier is the LEVEL they're all at.
// The primary skill leads by ~4 points so the paperdoll title comes from
// it. UTILITY skills (a dexxer's recall Magery) sit at ~half the primary
// — enough to Recall and Cure, never GM, so travel-magic rarity math
// (Recall at 26+, Gate at 90+) stays intact.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    // The skills a class advances. The first slot is the primary skill —
    // the one that defines the paperdoll title. Secondary skills track
    // the primary (-4); Utility skills sit at ~half scale.
    public readonly struct SkillTemplate
    {
        public readonly SkillName Primary;
        public readonly SkillName[] Secondary;
        public readonly SkillName[] Utility;

        public SkillTemplate(SkillName primary, SkillName[] secondary, SkillName[] utility = null)
        {
            Primary = primary;
            Secondary = secondary;
            Utility = utility ?? Array.Empty<SkillName>();
        }
    }

    public static class BotSkillTemplates
    {
        // A Mage-class bot whose weapon skill rolled at least this high is
        // a TANK MAGE — it gets a real weapon at creation and holds its
        // ground in melee (AdventurerBehavior re-arms it between casts).
        public const double TankWeaponSkillMin = 45.0;

        // ---- Stat profiles ----
        // (Str, Dex, Int) at TIER MAX (Grandmaster). Lower tiers scale
        // down. T2A-legal: every stat <= 100, every total <= 225 — these
        // are the canonical era spreads.
        private static readonly (int s, int d, int i) StatsDexxer   = (100, 100,  25); // pure dexxer
        private static readonly (int s, int d, int i) StatsTankMage = (100,  25, 100); // tank mage
        private static readonly (int s, int d, int i) StatsArcher   = ( 90, 100,  35);
        private static readonly (int s, int d, int i) StatsCaster   = ( 80,  45, 100); // mage-skilled support
        private static readonly (int s, int d, int i) StatsHybrid   = ( 90,  45,  90); // balanced hybrid
        private static readonly (int s, int d, int i) StatsSmith    = (100,  45,  80); // strong from the forge
        private static readonly (int s, int d, int i) StatsTailor   = ( 80,  70,  75);
        private static readonly (int s, int d, int i) StatsThief    = ( 60, 100,  65);
        private static readonly (int s, int d, int i) StatsMiner    = (100,  80,  45); // swings a pick all day

        // ---- Templates ----
        // Rolled (not fetched) — the Mage class picks a tank-mage weapon
        // variant per bot, the Bard rolls its 7th skill. Called once at
        // bot creation; the result is baked into the mobile's skills.
        public static SkillTemplate RollTemplate(BotClass cls)
        {
            return cls switch
            {
                // Pure Dexxer — bandages and potions do the healing; a
                // little Magery covers Recall/Cure/utility.
                BotClass.Warrior => new SkillTemplate(
                    SkillName.Swords,
                    new[] { SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                            SkillName.Wrestling, SkillName.MagicResist },
                    new[] { SkillName.Magery }),

                BotClass.Mage => RollMageTemplate(),

                // Fencing dexxer — spear/war fork for the faster swings.
                BotClass.Fencer => new SkillTemplate(
                    SkillName.Fencing,
                    new[] { SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                            SkillName.Wrestling, SkillName.MagicResist },
                    new[] { SkillName.Magery }),

                BotClass.Archer => new SkillTemplate(
                    SkillName.Archery,
                    new[] { SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                            SkillName.Tracking, SkillName.MagicResist },
                    new[] { SkillName.Magery }),

                // The classic (expensive) tamer template.
                BotClass.Tamer => new SkillTemplate(
                    SkillName.AnimalTaming,
                    new[] { SkillName.AnimalLore, SkillName.Veterinary, SkillName.Magery,
                            SkillName.Meditation, SkillName.MagicResist, SkillName.Wrestling }),

                // The era smith carried real Magery/Med — recall runs to
                // the mines and back to the forge.
                BotClass.Crafter or BotClass.Smith => new SkillTemplate(
                    SkillName.Blacksmith,
                    new[] { SkillName.Mining, SkillName.Tinkering, SkillName.Magery,
                            SkillName.Meditation, SkillName.Lumberjacking, SkillName.Tailoring }),

                BotClass.Tailor => new SkillTemplate(
                    SkillName.Tailoring,
                    new[] { SkillName.ArmsLore, SkillName.Cooking, SkillName.Tinkering,
                            SkillName.Camping, SkillName.MagicResist }),

                // Treasure maps and sea serpents — the fighting fisherman.
                BotClass.Fisherman => new SkillTemplate(
                    SkillName.Fishing,
                    new[] { SkillName.Magery, SkillName.Meditation, SkillName.MagicResist,
                            SkillName.Hiding, SkillName.Camping, SkillName.Healing }),

                BotClass.Healer => new SkillTemplate(
                    SkillName.Healing,
                    new[] { SkillName.Anatomy,    SkillName.Veterinary, SkillName.SpiritSpeak,
                            SkillName.Magery,     SkillName.MagicResist }),

                BotClass.Thief => new SkillTemplate(
                    SkillName.Stealing,
                    new[] { SkillName.Snooping, SkillName.Hiding, SkillName.Stealth,
                            SkillName.Lockpicking, SkillName.MagicResist }),

                // The richest character in T2A — made monsters kill each
                // other. 7th skill rolls Hiding or Wrestling, as it did.
                BotClass.Bard => new SkillTemplate(
                    SkillName.Provocation,
                    new[] { SkillName.Musicianship, SkillName.Magery, SkillName.Meditation,
                            SkillName.EvalInt, SkillName.MagicResist,
                            Utility.RandomBool() ? SkillName.Hiding : SkillName.Wrestling }),

                BotClass.Ranger => new SkillTemplate(
                    SkillName.Archery,
                    new[] { SkillName.Tactics, SkillName.Anatomy, SkillName.Tracking,
                            SkillName.Camping, SkillName.MagicResist },
                    new[] { SkillName.Magery }),

                // The Lumberjack PvP template — probably the highest melee
                // burst of the era. Lumberjacking stays primary so the
                // paperdoll (and the gatherer identity) reads Lumberjack,
                // but the full dexxer fighting line rides underneath.
                BotClass.Lumberjack => new SkillTemplate(
                    SkillName.Lumberjacking,
                    new[] { SkillName.Swords, SkillName.Tactics, SkillName.Anatomy,
                            SkillName.Healing, SkillName.MagicResist },
                    new[] { SkillName.Magery }),

                BotClass.Miner => new SkillTemplate(
                    SkillName.Mining,
                    new[] { SkillName.Swords, SkillName.Tactics, SkillName.Camping,
                            SkillName.ArmsLore, SkillName.MagicResist }),

                // The classic map-runner: decode, dig, pick the chest,
                // pull the traps — and clear the guardians with real
                // Magery (no weapon line; spells were the T2A digger's
                // defense).
                BotClass.TreasureHunter => new SkillTemplate(
                    SkillName.Cartography,
                    new[] { SkillName.Lockpicking, SkillName.Magery, SkillName.Meditation,
                            SkillName.DetectHidden, SkillName.RemoveTrap, SkillName.Hiding }),

                // The merchant/mule — Item ID's skill title IS "Merchant".
                // Appraises everything, fights nothing.
                BotClass.Merchant => new SkillTemplate(
                    SkillName.ItemID,
                    new[] { SkillName.TasteID, SkillName.Magery, SkillName.Meditation,
                            SkillName.ArmsLore, SkillName.Hiding, SkillName.Camping }),

                _ => new SkillTemplate(
                    SkillName.Swords,
                    new[] { SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                            SkillName.Wrestling, SkillName.MagicResist },
                    new[] { SkillName.Magery }),
            };
        }

        // The Mage class rolls its T2A variant per bot:
        //   40% Hally Mage  (Swords)  — the king of T2A
        //   20% Mace Tank   (Macing)  — maces wrecked armor and stamina
        //   15% Fencer Tank (Fencing) — spear/war fork speed
        //   25% Scribe Mage           — pure caster with Inscription
        private static SkillTemplate RollMageTemplate()
        {
            int roll = Utility.Random(100);
            if (roll < 75)
            {
                SkillName weapon = roll < 40 ? SkillName.Swords
                                 : roll < 60 ? SkillName.Macing
                                 : SkillName.Fencing;
                return new SkillTemplate(
                    SkillName.Magery,
                    new[] { SkillName.EvalInt, SkillName.Meditation, SkillName.Wrestling,
                            SkillName.MagicResist, weapon, SkillName.Tactics });
            }

            return new SkillTemplate(
                SkillName.Magery,
                new[] { SkillName.EvalInt, SkillName.Meditation, SkillName.Wrestling,
                        SkillName.Inscribe, SkillName.MagicResist });
        }

        // -------------------------------------------------------------------
        // Target skill value for a given tier. Primary skill is at the top
        // of the range; secondary skills are 4 points below so the
        // paperdoll title comes from the primary. Each bot gets ±3..10
        // jitter applied at roll time so no two bots are identical.
        // -------------------------------------------------------------------
        public static double PrimarySkillTarget(BotSkillTier tier)
        {
            return tier switch
            {
                BotSkillTier.Novice      => 30.0,
                BotSkillTier.Apprentice  => 45.0,
                BotSkillTier.Journeyman  => 60.0,
                BotSkillTier.Adept       => 72.0,
                BotSkillTier.Expert      => 82.0,
                BotSkillTier.Master      => 90.0,
                BotSkillTier.Grandmaster => 99.0,
                _                        => 50.0,
            };
        }

        // Secondary skills are slightly below the primary so the title
        // comes from the primary. Same scaling though — a GM warrior has
        // ALL warrior skills high, not just Sword.
        public static double SecondarySkillTarget(BotSkillTier tier)
        {
            return Math.Max(0, PrimarySkillTarget(tier) - 4.0);
        }

        // Utility skills run at ~half scale — a GM dexxer's Magery lands
        // around 50: reliable Recall (26+), never Gate (90+), exactly the
        // "a little Magery for utility" of the era templates.
        public static double UtilitySkillTarget(BotSkillTier tier)
        {
            return PrimarySkillTarget(tier) * 0.5;
        }

        // ±3..10 point jitter. Sign random.
        public static double RollJitter()
        {
            double mag = Utility.RandomMinMax(3, 10);
            return Utility.RandomBool() ? mag : -mag;
        }

        // -------------------------------------------------------------------
        // Stat targets for a given class+tier. Scales linearly from 60% of
        // cap at Novice to 100% at Grandmaster.
        // -------------------------------------------------------------------
        public static (int str, int dex, int intel) StatTargets(BotClass cls, BotSkillTier tier)
        {
            (int s, int d, int i) max = cls switch
            {
                BotClass.Warrior    => StatsDexxer,
                BotClass.Mage       => StatsTankMage,
                BotClass.Fencer     => StatsDexxer,
                BotClass.Archer     => StatsArcher,
                BotClass.Tamer      => StatsCaster,
                BotClass.Crafter    => StatsSmith,
                BotClass.Smith      => StatsSmith,
                BotClass.Tailor     => StatsTailor,
                BotClass.Fisherman  => StatsHybrid,
                BotClass.Healer     => StatsCaster,
                BotClass.Thief      => StatsThief,
                BotClass.Bard       => StatsCaster,
                BotClass.Ranger     => StatsArcher,
                BotClass.Lumberjack => StatsDexxer,
                BotClass.Miner      => StatsMiner,
                BotClass.TreasureHunter => StatsHybrid, // digs, fights, casts
                BotClass.Merchant       => StatsCaster,
                _                   => StatsDexxer,
            };

            // Tier 0 (Novice) = 60% of cap, Tier 6 (GM) = 100%.
            double tFrac = 0.60 + (0.40 * ((int)tier / 6.0));
            return (
                (int)Math.Round(max.s * tFrac),
                (int)Math.Round(max.d * tFrac),
                (int)Math.Round(max.i * tFrac)
            );
        }
    }
}
