// =========================================================================
// BotSkillTier.cs — How experienced a bot is.
//
// Combined with BotClass, drives:
//   - Equipment quality (low tier = cheap gear, high tier = best in slot,
//     hued, sometimes named items)
//   - Paperdoll title (e.g. "Grandmaster Swordsman", "Novice Mage")
//
// Distribution is bell-curved — most bots are mid-tier; very few are
// Grandmasters. Rolled once at creation, persists for the bot's life.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public enum BotSkillTier : byte
    {
        Novice      = 0,
        Apprentice  = 1,
        Journeyman  = 2,
        Adept       = 3,
        Expert      = 4,
        Master      = 5,
        Grandmaster = 6,
    }

    public static class BotSkillTierHelper
    {
        // Bell-curve weights. Sums to 100.
        //   Novice:      15
        //   Apprentice:  20
        //   Journeyman:  20
        //   Adept:       20
        //   Expert:      15
        //   Master:       8
        //   Grandmaster:  2
        private static readonly (BotSkillTier tier, int weight)[] TierWeights = new[]
        {
            (BotSkillTier.Novice,      15),
            (BotSkillTier.Apprentice,  20),
            (BotSkillTier.Journeyman,  20),
            (BotSkillTier.Adept,       20),
            (BotSkillTier.Expert,      15),
            (BotSkillTier.Master,       8),
            (BotSkillTier.Grandmaster,  2),
        };

        public static BotSkillTier RollRandom()
        {
            int r = Utility.Random(100);
            int acc = 0;
            foreach (var (tier, w) in TierWeights)
            {
                acc += w;
                if (r < acc) return tier;
            }
            return BotSkillTier.Journeyman;
        }

        public static string DisplayName(BotSkillTier tier)
        {
            return tier switch
            {
                BotSkillTier.Novice      => "Novice",
                BotSkillTier.Apprentice  => "Apprentice",
                BotSkillTier.Journeyman  => "Journeyman",
                BotSkillTier.Adept       => "Adept",
                BotSkillTier.Expert      => "Expert",
                BotSkillTier.Master      => "Master",
                BotSkillTier.Grandmaster => "Grandmaster",
                _                        => "Apprentice",
            };
        }

        // Used by equipment rolls to ask "is this tier good enough for
        // hued/exceptional gear?" Tier-as-int makes range checks simple.
        public static int Rank(BotSkillTier tier) => (int)tier;
    }
}
