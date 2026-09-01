// =========================================================================
// BotWants.cs — what a bot is actually in the market for.
//
// "WTB GM hally" from a mage was always just a line off a list. Nothing
// read it, nothing checked whether the speaker had any use for a halberd,
// and nothing checked whether it could pay. This is the appetite half:
// given a class and a priced noun, would that bot really buy it?
//
// The shouting itself is still random — a mage can still say the halberd
// line, and when it does, the want simply is not backed and stays flavour,
// exactly as every WTB did before. What changed is that the lines a bot
// SHOULD want are now real offers somebody can answer. Partitioning the
// shout lists per class is the other half and is deliberately not done
// here; it means restructuring the chat corpus.
//
// Everyone wants travel scrolls. That is not a simplification — recall
// and gate scrolls were the one thing every character on the shard bought,
// every week, forever.
// =========================================================================

using System;

namespace Server.CustomBots
{
    public static class BotWants
    {
        // Would this class actually buy this?
        public static bool Wants(BotClass cls, GoodsKind kind, string noun)
        {
            if (string.IsNullOrEmpty(noun))
            {
                return false;
            }

            // The universal trade. Recall, gate and mark moved everybody
            // around, and nobody ever had enough of them.
            if (kind == GoodsKind.Scroll)
            {
                return true;
            }

            // Nobody is buying a keep off a shout at the bank.
            if (kind == GoodsKind.BigTicket)
            {
                return false;
            }

            return cls switch
            {
                BotClass.Mage or BotClass.Healer or BotClass.Bard =>
                    Any(noun, Reagents) || Any(noun, LightArmor) || Any(noun, Robes),

                BotClass.Warrior or BotClass.Fencer =>
                    Any(noun, Blades) || Any(noun, HeavyArmor) || Any(noun, Shields) ||
                    Any(noun, Consumables),

                BotClass.Archer or BotClass.Ranger =>
                    Any(noun, Bows) || Any(noun, Ammo) || Any(noun, LightArmor) ||
                    Any(noun, Consumables),

                BotClass.Tamer =>
                    Any(noun, Consumables) || Any(noun, LightArmor) || Any(noun, Hides),

                BotClass.Thief =>
                    Any(noun, Blades) || Any(noun, LightArmor),

                // The trades buy their own raw material, and they buy it in
                // bulk. A smith wanting ingots is the most ordinary want on
                // the shard.
                BotClass.Smith => Any(noun, Ingots) || Any(noun, Blades) || Any(noun, HeavyArmor),
                BotClass.Tailor => Any(noun, Hides) || Any(noun, LightArmor) || Any(noun, Robes),
                BotClass.Fisherman => Any(noun, Consumables) || Any(noun, Boards),

                // Legacy and everything unlisted: the daily-grind goods
                // only, so an unknown class never claims to want a vanq.
                _ => kind == GoodsKind.Bulk,
            };
        }

        // Convenience for callers that only have the bot.
        public static bool Wants(PlayerBot bot, GoodsKind kind, string noun) =>
            bot != null && Wants(bot.Class, kind, noun);

        // The coarse form, for the one caller that is sweeping the room and
        // has a KIND but no noun yet. Kept deliberately loose: it decides
        // who bothers to look up at a shout, and the noun-level test above
        // still has to pass before anybody hands over coin. Answering "no"
        // here on a guess would silence bots that would in fact have bought.
        public static bool CouldWant(BotClass cls, GoodsKind kind) => cls switch
        {
            // The town mule buys anything it thinks it can move on.
            BotClass.Merchant => true,

            BotClass.Mage or BotClass.Healer or BotClass.Bard or BotClass.Tamer =>
                kind is GoodsKind.Bulk or GoodsKind.Scroll or GoodsKind.Rare,

            BotClass.Warrior or BotClass.Fencer or BotClass.Archer or BotClass.Ranger
                or BotClass.Thief =>
                kind is GoodsKind.Gear or GoodsKind.Rare or GoodsKind.Bulk
                     or GoodsKind.Scroll,

            BotClass.TreasureHunter =>
                kind is GoodsKind.Rare or GoodsKind.Scroll or GoodsKind.Bulk,

            // Artisans and gatherers buy materials, and travel scrolls like
            // everybody else.
            _ => kind is GoodsKind.Bulk or GoodsKind.Scroll,
        };

        // ---- the lists ---------------------------------------------------
        // Matched as substrings of the stock table's own nouns, so these
        // have to stay in the table's words.

        private static readonly string[] Reagents =
        {
            "black pearl", "blood moss", "garlic", "ginseng", "mandrake",
            "nightshade", "spider silk", "sulfurous ash",
        };

        private static readonly string[] Blades =
        {
            "katana", "kryss", "longsword", "broadsword", "scimitar", "spear",
            "war fork", "halberd", "bardiche", "mace", "maul", "war hammer",
        };

        private static readonly string[] Bows = { "bow", "crossbow", "heavy xbow" };

        private static readonly string[] Ammo = { "arrows", "bolts" };

        private static readonly string[] HeavyArmor =
        {
            "plate", "chain tunic", "ringmail tunic",
        };

        private static readonly string[] LightArmor = { "leather", "studded tunic" };

        private static readonly string[] Shields = { "kite shield", "heater shield" };

        private static readonly string[] Robes = { "robe", "cloak", "doublet", "fancy shirt", "kilt" };

        private static readonly string[] Consumables = { "bandages" };

        private static readonly string[] Ingots = { "iron ingots" };

        private static readonly string[] Hides = { "hides", "leather" };

        private static readonly string[] Boards = { "boards" };

        private static bool Any(string noun, string[] words)
        {
            for (var i = 0; i < words.Length; i++)
            {
                if (noun.Contains(words[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
