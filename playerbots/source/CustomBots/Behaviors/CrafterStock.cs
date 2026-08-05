// =========================================================================
// CrafterStock.cs — a crafter's REAL working stock.
//
// The crafter rebuild's production is no longer a pure illusion: a smith
// keeps actual iron ingots in the pack, a tailor cloth and leather, a
// carpenter boards — and every finished piece consumes some. Stock comes
// from three real sources:
//
//   - starter stock seeded at spawn (CrafterProfiles.StarterProps)
//   - buying a gatherer's raw haul in person (BotEconomy.DeliverMaterials:
//     ore/logs for gold, refined on the spot into ingots/boards)
//   - a shop restock when dry too long and no hauler has come through
//     (gold leaves the purse, materials appear — the counter purchase
//     nobody needs to see)
//
// Everything works on the bot's real backpack via GetAmount/ConsumeTotal,
// so a snooped crafter pack tells the true story.
// =========================================================================

using System;
using Server;
using Server.Items;

namespace Server.CustomBots
{
    public static class CrafterStock
    {
        // Packs never balloon: stock above the cap is turned away at buy
        // time ("that's all I can store").
        public const int StockCap = 250;

        // Units of material this trade currently has on hand.
        public static int Count(PlayerBot bot, CrafterProfile profile)
        {
            var pack = bot?.Backpack;
            if (pack == null || profile == null || profile.Materials.Length == 0)
            {
                return 0;
            }
            return pack.GetAmount(profile.Materials, recurse: false);
        }

        // Consume `amount` units across the trade's material types (greedy,
        // in profile order). Returns false (and consumes nothing) if the
        // pack doesn't hold enough in total.
        public static bool Consume(PlayerBot bot, CrafterProfile profile, int amount)
        {
            var pack = bot?.Backpack;
            if (pack == null || amount <= 0)
            {
                return false;
            }
            if (Count(bot, profile) < amount)
            {
                return false;
            }

            foreach (var type in profile.Materials)
            {
                if (amount <= 0)
                {
                    break;
                }
                int have = pack.GetAmount(type, recurse: false);
                if (have <= 0)
                {
                    continue;
                }
                int take = Math.Min(have, amount);
                pack.ConsumeTotal(type, take, recurse: false);
                amount -= take;
            }
            return true;
        }

        // Add `amount` units of the trade's staple material, respecting the
        // stock cap. Returns how many units were actually accepted.
        public static int Add(PlayerBot bot, CrafterProfile profile, int amount)
        {
            if (bot == null || profile?.MakeMaterial == null || amount <= 0)
            {
                return 0;
            }

            int room = StockCap - Count(bot, profile);
            int accept = Math.Min(amount, Math.Max(0, room));
            if (accept <= 0)
            {
                return 0;
            }

            var stack = profile.MakeMaterial(accept);
            if (stack == null)
            {
                return 0;
            }
            if (!bot.AddToBackpack(stack))
            {
                stack.Delete();
                return 0;
            }
            return accept;
        }

        // The bot's purse.
        public static int GoldOnHand(PlayerBot bot) =>
            bot?.Backpack?.GetAmount(typeof(Gold), recurse: false) ?? 0;

        // Spend real gold from the pack. False (nothing spent) if short.
        public static bool SpendGold(PlayerBot bot, int amount)
        {
            var pack = bot?.Backpack;
            if (pack == null || amount <= 0)
            {
                return false;
            }
            return pack.ConsumeTotal(typeof(Gold), amount, recurse: false);
        }
    }
}
