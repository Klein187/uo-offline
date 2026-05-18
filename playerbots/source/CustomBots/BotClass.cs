// =========================================================================
// BotClass.cs — The character "class" of a PlayerBot.
//
// Not tied to behavior (a Mage class bot might be a BankSitter today and
// an Adventurer tomorrow). Determines:
//   - What equipment they wear (via EquipmentTable)
//   - What their paperdoll title says ("Grandmaster Mage")
//   - In the future, which behaviors they're suited for (a Tamer class
//     bot will eventually get a tamed pet when the Tamer behavior lands)
//
// Classes are rolled once at bot creation and persist for the bot's life.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public enum BotClass : byte
    {
        Warrior  = 0,
        Mage     = 1,
        Fencer   = 2,
        Archer   = 3,
        Tamer    = 4,
        Crafter  = 5,
        Healer   = 6,
        Thief    = 7,
        Bard     = 8,
        Ranger   = 9,
    }

    public static class BotClassHelper
    {
        // Class roll weights — must sum to 100. Slightly heavier on the
        // common combat classes (Warrior/Mage/Fencer/Archer) and lighter
        // on more specialized ones.
        private static readonly (BotClass cls, int weight)[] ClassWeights = new[]
        {
            (BotClass.Warrior, 18),
            (BotClass.Mage,    16),
            (BotClass.Fencer,  12),
            (BotClass.Archer,  12),
            (BotClass.Crafter, 12),
            (BotClass.Healer,   8),
            (BotClass.Bard,     8),
            (BotClass.Ranger,   6),
            (BotClass.Tamer,    5),
            (BotClass.Thief,    3),
        };

        public static BotClass RollRandom()
        {
            int r = Utility.Random(100);
            int acc = 0;
            foreach (var (cls, w) in ClassWeights)
            {
                acc += w;
                if (r < acc) return cls;
            }
            return BotClass.Warrior;
        }

        // Display name for paperdoll title. "the Grandmaster Mage" etc.
        // For Novice tier, simpler title ("the Novice").
        public static string DisplayName(BotClass cls)
        {
            return cls switch
            {
                BotClass.Warrior => "Swordsman",
                BotClass.Mage    => "Mage",
                BotClass.Fencer  => "Fencer",
                BotClass.Archer  => "Archer",
                BotClass.Tamer   => "Tamer",
                BotClass.Crafter => "Crafter",
                BotClass.Healer  => "Healer",
                BotClass.Thief   => "Thief",
                BotClass.Bard    => "Bard",
                BotClass.Ranger  => "Ranger",
                _                => "Wanderer",
            };
        }
    }
}
