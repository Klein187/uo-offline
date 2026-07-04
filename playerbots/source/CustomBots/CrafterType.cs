// =========================================================================
// CrafterType.cs — the craft specialization of a BotClass.Crafter bot.
//
// BotClass.Crafter is a single class; CrafterType is the SUBTYPE rolled
// once at creation. It drives:
//   - where the bot stations itself (Smith -> forge, Tailor -> tailor
//     vendor, Fisherman -> dock)
//   - which "making" animation it plays + craft sound
//   - what it produces (the per-subtype CrafterProfile loot tables)
//   - its craft-themed chatter and paperdoll title
//
// Only meaningful when the bot's BotClass is Crafter; ignored otherwise.
//
// NOTE on the subtype set: this was narrowed from seven subtypes to three
// (Smith / Tailor / Fisherman) when the crafter system was rebuilt around
// the production engine. More subtypes can be added later by extending this
// enum AND adding a matching CrafterProfile. Old saves that stored one of
// the retired subtypes (Tinker/Inscriptionist/Carpenter/Bowcrafter) are
// remapped on load — see MigrateLegacyByte.
// =========================================================================

using Server;

namespace Server.CustomBots
{
    public enum CrafterType : byte
    {
        Smith     = 0,  // forge — armor & weapons
        Tailor    = 1,  // tailor vendor — cloth & leather goods
        Fisherman = 2,  // dock — fish, rare sea finds
    }

    public static class CrafterTypeHelper
    {
        // Smiths are the iconic forge crafter, so they lead; tailors next,
        // fishermen rarest (docks are fewer and farther-flung).
        private static readonly (CrafterType type, int weight)[] Weights =
        {
            (CrafterType.Smith,     45),
            (CrafterType.Tailor,    30),
            (CrafterType.Fisherman, 25),
        };

        public static CrafterType RollRandom()
        {
            int r = Utility.Random(100);
            int acc = 0;
            foreach (var (type, w) in Weights)
            {
                acc += w;
                if (r < acc)
                {
                    return type;
                }
            }
            return CrafterType.Smith;
        }

        // Remap a CrafterSpec byte from a PRE-REBUILD save (serialization
        // v4) onto the current three-subtype enum. The old layout was:
        //   Smith=0 Tailor=1 Tinker=2 Inscriptionist=3 Carpenter=4
        //   Bowcrafter=5 Fisherman=6
        // Smith/Tailor keep their slots; the old Fisherman (6) maps to the
        // new Fisherman (2); the four retired subtypes re-roll into a valid
        // one so no bot loads with a meaningless spec. Only call this for v4
        // saves — v5+ stores the current layout and is read directly.
        public static CrafterType MigrateLegacyByte(byte raw)
        {
            return raw switch
            {
                0 => CrafterType.Smith,
                1 => CrafterType.Tailor,
                6 => CrafterType.Fisherman,
                _ => RollRandom(), // retired subtypes (2,3,4,5) / anything odd
            };
        }

        // Paperdoll title fragment — "the Grandmaster Blacksmith" etc.
        public static string DisplayName(CrafterType type)
        {
            return type switch
            {
                CrafterType.Smith     => "Blacksmith",
                CrafterType.Tailor    => "Tailor",
                CrafterType.Fisherman => "Fisherman",
                _                     => "Crafter",
            };
        }

        // The destination type this crafter stations itself at.
        public static DestinationType StationFor(CrafterType type)
        {
            return type switch
            {
                CrafterType.Smith     => DestinationType.Forge,
                CrafterType.Tailor    => DestinationType.VendorTailor,
                CrafterType.Fisherman => DestinationType.Dock,
                _                     => DestinationType.Bank,
            };
        }
    }
}
