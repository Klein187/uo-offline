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
        Crafter  = 5,  // LEGACY — no longer rolled; old saves migrate to Smith/Tailor/Fisherman
        Healer   = 6,
        Thief    = 7,
        Bard     = 8,
        Ranger   = 9,

        // Artisan classes — each replaces a former Crafter subtype. They
        // station at, and "work", a specific destination (see StationFor).
        Smith     = 10,  // forge — weapons & armor
        Tailor    = 11,  // tailor shop — cloth & leather
        Fisherman = 12,  // dock — fish & rare sea finds

        // Gatherer classes — the "lumberjack in the middle of nowhere"
        // (IDEAS 1.5). They work synthetic wilderness GatherSpots, fill
        // their packs with raw materials, and haul the load back to town
        // (bank, forge, carpenter) — the visible supply side of the
        // economy. They carry real tools that double as weapons, so a
        // lumberjack defends itself with its axe.
        Lumberjack = 13, // forests — logs
        Miner      = 14, // mountains — ore

        // The two remaining classic T2A templates. The Treasure Hunter
        // (Cartography/Lockpicking/Remove Trap + real Magery) digs the
        // wilderness chests and clears the guardians with spells; the
        // Merchant (Item ID/Taste ID — UO titles ItemID "Merchant") is
        // the town mule: banks, shops, appraises, never fights.
        TreasureHunter = 15,
        Merchant       = 16,
    }

    public static class BotClassHelper
    {
        // Class roll weights — must sum to 100. Slightly heavier on the
        // common combat classes (Warrior/Mage/Fencer/Archer) and lighter
        // on more specialized ones.
        private static readonly (BotClass cls, int weight)[] ClassWeights = new[]
        {
            (BotClass.Warrior,  14),
            (BotClass.Mage,     14),
            (BotClass.Fencer,   11),
            (BotClass.Archer,   11),
            // The old Crafter share (12) split across the three artisans.
            (BotClass.Smith,     5),
            (BotClass.Tailor,    4),
            (BotClass.Fisherman, 4),
            (BotClass.Healer,    7),
            (BotClass.Bard,      7),
            (BotClass.Ranger,    6),
            (BotClass.Tamer,     5),
            (BotClass.Thief,     3),
            // Wilderness gatherers — a small but very visible population.
            (BotClass.Lumberjack, 3),
            (BotClass.Miner,      2),
            // Specialist templates — rare, like the real thing (both were
            // expensive second characters).
            (BotClass.TreasureHunter, 2),
            (BotClass.Merchant,       2),
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
                BotClass.Smith     => "Blacksmith",
                BotClass.Tailor    => "Tailor",
                BotClass.Fisherman => "Fisherman",
                BotClass.Lumberjack => "Lumberjack",
                BotClass.Miner      => "Miner",
                BotClass.TreasureHunter => "Treasure Hunter",
                BotClass.Merchant       => "Merchant",
                _                => "Wanderer",
            };
        }

        // The destination type an artisan class stations at and "works".
        // Returns null for non-artisan classes (they have no work station).
        public static DestinationType? StationFor(BotClass cls)
        {
            return cls switch
            {
                BotClass.Smith     => DestinationType.Forge,
                BotClass.Tailor    => DestinationType.VendorTailor,
                BotClass.Fisherman => DestinationType.Dock,
                _                  => null,
            };
        }

        public static bool IsArtisan(BotClass cls) =>
            cls is BotClass.Smith or BotClass.Tailor or BotClass.Fisherman;

        // Wilderness resource gatherers. NOT artisans: artisans station at
        // a town fixture forever; gatherers run a loop (wilderness spot →
        // work → haul the load to town → back out). They also fight — the
        // tool is a real weapon.
        public static bool IsGatherer(BotClass cls) =>
            cls is BotClass.Lumberjack or BotClass.Miner;
    }
}
