// =========================================================================
// BotSkillTemplate.cs — Real UO-style skill templates for each BotClass.
//
// UO players had "templates" — coherent sets of 5-7 skills that all
// advanced together. A grandmaster swordsman didn't have GM Sword and 65
// Tactics; they had GM Sword AND GM Tactics AND GM Anatomy. The whole
// template scaled together. Class is the SET of skills; SkillTier is
// the LEVEL they're all at.
//
// Each class has a primary skill — the one that defines the paperdoll
// title (e.g. Magery → "Grandmaster Mage"). The other skills in the
// template are slightly lower than the primary so the title displays
// correctly (UO shows the title of the highest skill above 50).
//
// Stats also scale by class — Warriors are Str-heavy, Mages Int-heavy,
// Fencers/Archers Dex-heavy, etc.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    // The 6 skills a class advances. The first slot is the primary
    // skill — the one that defines the paperdoll title.
    public readonly struct SkillTemplate
    {
        public readonly SkillName Primary;
        public readonly SkillName[] Secondary; // 5 more skills

        public SkillTemplate(SkillName primary, params SkillName[] secondary)
        {
            Primary = primary;
            Secondary = secondary;
        }
    }

    public static class BotSkillTemplates
    {
        // ---- Stat profiles ----
        // (Str, Dex, Int) at TIER MAX (Grandmaster). Lower tiers scale down.
        // All three sum to ~225 (UO's stat cap).
        private static readonly (int s, int d, int i) StatsWarrior  = (100, 75, 50);
        private static readonly (int s, int d, int i) StatsMage     = ( 50, 50, 125);
        private static readonly (int s, int d, int i) StatsFencer   = ( 60, 125, 40);
        private static readonly (int s, int d, int i) StatsArcher   = ( 60, 125, 40);
        private static readonly (int s, int d, int i) StatsTamer    = ( 70, 60, 95);
        private static readonly (int s, int d, int i) StatsCrafter  = ( 95, 65, 65);
        private static readonly (int s, int d, int i) StatsHealer   = ( 60, 65, 100);
        private static readonly (int s, int d, int i) StatsThief    = ( 50, 125, 50);
        private static readonly (int s, int d, int i) StatsBard     = ( 60, 70, 95);
        private static readonly (int s, int d, int i) StatsRanger   = ( 75, 100, 50);

        // ---- Templates ----
        public static SkillTemplate GetTemplate(BotClass cls)
        {
            return cls switch
            {
                BotClass.Warrior => new SkillTemplate(
                    SkillName.Swords,
                    SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                    SkillName.Parry,   SkillName.MagicResist),

                BotClass.Mage => new SkillTemplate(
                    SkillName.Magery,
                    SkillName.EvalInt,    SkillName.Meditation, SkillName.Wrestling,
                    SkillName.Inscribe,   SkillName.MagicResist),

                BotClass.Fencer => new SkillTemplate(
                    SkillName.Fencing,
                    SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                    SkillName.Parry,   SkillName.MagicResist),

                BotClass.Archer => new SkillTemplate(
                    SkillName.Archery,
                    SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                    SkillName.Tracking, SkillName.MagicResist),

                BotClass.Tamer => new SkillTemplate(
                    SkillName.AnimalTaming,
                    SkillName.AnimalLore, SkillName.Veterinary, SkillName.Magery,
                    SkillName.EvalInt,    SkillName.MagicResist),

                BotClass.Crafter => new SkillTemplate(
                    SkillName.Blacksmith,
                    SkillName.ArmsLore, SkillName.Mining, SkillName.Tinkering,
                    SkillName.Carpentry, SkillName.Tailoring),

                BotClass.Healer => new SkillTemplate(
                    SkillName.Healing,
                    SkillName.Anatomy,    SkillName.Veterinary, SkillName.SpiritSpeak,
                    SkillName.Magery,     SkillName.MagicResist),

                BotClass.Thief => new SkillTemplate(
                    SkillName.Stealing,
                    SkillName.Snooping, SkillName.Hiding, SkillName.Stealth,
                    SkillName.Lockpicking, SkillName.MagicResist),

                BotClass.Bard => new SkillTemplate(
                    SkillName.Provocation,
                    SkillName.Musicianship, SkillName.Peacemaking, SkillName.Discordance,
                    SkillName.MagicResist,  SkillName.Healing),

                BotClass.Ranger => new SkillTemplate(
                    SkillName.Archery,
                    SkillName.Tactics, SkillName.Anatomy, SkillName.Tracking,
                    SkillName.Camping, SkillName.MagicResist),

                _ => new SkillTemplate(SkillName.Swords,
                    SkillName.Tactics, SkillName.Anatomy, SkillName.Healing,
                    SkillName.Parry, SkillName.MagicResist),
            };
        }

        // -------------------------------------------------------------------
        // Target skill value for a given tier. Primary skill is at the top
        // of the range; secondary skills are 3-5 points below so the
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
                BotClass.Warrior  => StatsWarrior,
                BotClass.Mage     => StatsMage,
                BotClass.Fencer   => StatsFencer,
                BotClass.Archer   => StatsArcher,
                BotClass.Tamer    => StatsTamer,
                BotClass.Crafter  => StatsCrafter,
                BotClass.Healer   => StatsHealer,
                BotClass.Thief    => StatsThief,
                BotClass.Bard     => StatsBard,
                BotClass.Ranger   => StatsRanger,
                _                 => StatsWarrior,
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
