// =========================================================================
// SpeechHues.cs — Curated palette of speech colors for PlayerBots.
//
// In real UO, players set their chat color in the options menu. Some
// stayed default white, some picked red, blue, pink, hot orange. It was
// a personality identifier — you'd recognize regulars by their color
// before reading the name.
//
// We give each bot a random hue at creation, persisted across saves.
// Most bots get a color from the curated palette; about 10% stay
// default white. This is the inverse of the real-UO ratio — we want
// the offline shard to feel visibly colorful, not blandly typical.
//
// UO hue numbers are 0-65535. The values below are hand-picked for:
//   - Good contrast against the typical game backgrounds
//   - Distinct from each other (no two "kind of blue"s)
//   - Period-feeling palette (no neon clashing)
// =========================================================================

using Server;

namespace Server.CustomBots
{
    public static class SpeechHues
    {
        // Hue 0 = default (white/light-gray system chat color).
        public const int Default = 0;

        // Curated palette. Hue numbers verified against ModernUO's hue
        // table; these all render as intended speech colors.
        public static readonly int[] Palette =
        {
            33,    // red — aggressive, mage red, PK favorite
            53,    // bright yellow
            63,    // bright cyan
            73,    // bright pink — "trying too hard" pink
            88,    // bright green — trader-feel
            93,    // bright blue — classic mage blue
            113,   // bright orange — attention-grabber
            153,   // bright purple — "I'm important"
            1153,  // royal blue — more subtle than 93
            1175,  // dusky gray — brooding
            1281,  // near-black — edge lord, PK favorite
            1287,  // crimson
            1361,  // gold
            1430,  // turquoise
            1502,  // rose

            // A few softer hues so the palette isn't all-bright
            38,    // muted forest green
            68     // dusty sky blue
        };

        // Probability that a bot keeps the default (no color set).
        // Lowered to 0.10 — most bots get a colored speech hue. The
        // crowd looks more visibly varied this way, and finding a
        // default-white bot becomes the unusual case rather than typical.
        private const double DefaultProbability = 0.10;

        // -------------------------------------------------------------------
        // PickRandom — returns a hue for a newly created bot.
        // -------------------------------------------------------------------
        public static int PickRandom()
        {
            if (Utility.RandomDouble() < DefaultProbability)
            {
                return Default;
            }
            return Palette[Utility.Random(Palette.Length)];
        }
    }
}
