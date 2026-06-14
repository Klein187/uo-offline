// =========================================================================
// CrafterType.cs — the craft specialization of a BotClass.Crafter bot.
//
// BotClass.Crafter is a single class; CrafterType is the SUBTYPE rolled
// once at creation. It drives:
//   - where the bot stations itself (Smith -> forge, Tailor -> tailor
//     vendor, Bowcrafter -> bowyer, the rest -> bank)
//   - which "making" animation it plays
//   - its craft-themed chatter
//   - its paperdoll title ("Blacksmith", "Tailor", ...)
//
// Only meaningful when the bot's BotClass is Crafter; ignored otherwise.
// =========================================================================

using Server;

namespace Server.CustomBots
{
    public enum CrafterType : byte
    {
        Smith          = 0,  // forge — armor & weapons
        Tailor         = 1,  // tailor vendor — clothing
        Tinker         = 2,  // bank — tools, clocks, traps
        Inscriptionist = 3,  // bank — scrolls
        Carpenter      = 4,  // bank — furniture
        Bowcrafter     = 5,  // bowyer vendor — bows & arrows
        Fisherman      = 6,  // dock — fish, lobster, sea wares
    }

    public static class CrafterTypeHelper
    {
        // Roughly even roll, smiths a touch heavier (forges are iconic).
        private static readonly (CrafterType type, int weight)[] Weights =
        {
            (CrafterType.Smith,          22),
            (CrafterType.Tailor,         18),
            (CrafterType.Bowcrafter,     14),
            (CrafterType.Tinker,         13),
            (CrafterType.Carpenter,      12),
            (CrafterType.Inscriptionist, 11),
            (CrafterType.Fisherman,      10),
        };

        public static CrafterType RollRandom()
        {
            int r = Utility.Random(100);
            int acc = 0;
            foreach (var (type, w) in Weights)
            {
                acc += w;
                if (r < acc) return type;
            }
            return CrafterType.Smith;
        }

        // Paperdoll title fragment — "the Grandmaster Blacksmith" etc.
        public static string DisplayName(CrafterType type)
        {
            return type switch
            {
                CrafterType.Smith          => "Blacksmith",
                CrafterType.Tailor         => "Tailor",
                CrafterType.Tinker         => "Tinker",
                CrafterType.Inscriptionist => "Scribe",
                CrafterType.Carpenter      => "Carpenter",
                CrafterType.Bowcrafter     => "Bowcrafter",
                CrafterType.Fisherman      => "Fisherman",
                _                          => "Crafter",
            };
        }

        // The destination type this crafter stations itself at.
        public static DestinationType StationFor(CrafterType type)
        {
            return type switch
            {
                CrafterType.Smith      => DestinationType.Forge,
                CrafterType.Tailor     => DestinationType.VendorTailor,
                CrafterType.Bowcrafter => DestinationType.VendorBowyer,
                CrafterType.Fisherman  => DestinationType.Dock,
                // Tinker / Inscriptionist / Carpenter work out of the bank.
                _                      => DestinationType.Bank,
            };
        }
    }
}
