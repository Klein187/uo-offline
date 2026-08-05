// =========================================================================
// CrafterProduction.cs — the "crafting system" (illusion, not simulation).
//
// Crafters don't consume materials or run real craft skills. On a cadence
// the bot plays its work animation, and on each work cycle this runs TWO
// rolls (DESIGN-PLAN.md §2):
//
//   Roll 1 — did anything get made this cycle? Frequency scales with skill
//            tier (novice almost never, GM fairly regularly). NOT every cycle.
//   Roll 2 — what is it? Weighted heavily toward the COMMON band for EVERYONE.
//            Minor/Rare bands only open up at higher tiers, and even at GM the
//            rare slice is tiny — a GM smith still mostly turns out plain
//            pieces; the skill shows in the OCCASIONAL exceptional/rare item.
//
// GM output of a markable subtype (smith/tailor) gets UO's real maker's mark:
// Exceptional quality + "crafted by <name>" — authentic tooltips.
// =========================================================================

using System;
using Server;
using Server.Items;

namespace Server.CustomBots
{
    public static class CrafterProduction
    {
        // Roll 1 — chance per work cycle that an item is produced, by tier.
        // At a ~6-14s cycle this makes a GM produce roughly one item a minute
        // and a novice one every several minutes. Tunable.
        private static double MakeChance(BotSkillTier tier) => tier switch
        {
            BotSkillTier.Novice      => 0.02,
            BotSkillTier.Apprentice  => 0.04,
            BotSkillTier.Journeyman  => 0.06,
            BotSkillTier.Adept       => 0.09,
            BotSkillTier.Expert      => 0.13,
            BotSkillTier.Master      => 0.18,
            BotSkillTier.Grandmaster => 0.25,
            _                        => 0.06,
        };

        // Roll 2 — relative band weights (common, minor, rare) by tier.
        // Common dominates at EVERY tier. Low tiers make only common goods;
        // rare only appears at Master+ and stays a sliver even at GM (~2%),
        // matching the design's ~90/8/2 target.
        private static (int common, int minor, int rare) BandWeights(BotSkillTier tier) => tier switch
        {
            BotSkillTier.Novice      => (100, 0,  0),
            BotSkillTier.Apprentice  => (100, 0,  0),
            BotSkillTier.Journeyman  => (100, 0,  0),
            BotSkillTier.Adept       => (96,  4,  0),
            BotSkillTier.Expert      => (93,  7,  0),
            BotSkillTier.Master      => (91,  8,  1),
            BotSkillTier.Grandmaster => (90,  8,  2),
            _                        => (100, 0,  0),
        };

        // Run one production attempt for this work cycle. Returns the item it
        // made (already placed in the bot's pack) or null if nothing was made.
        public static Item TryProduce(PlayerBot bot, CrafterProfile profile)
        {
            if (bot == null || bot.Deleted || profile == null)
            {
                return null;
            }

            // Roll 1 — most cycles make nothing.
            if (Utility.RandomDouble() >= MakeChance(bot.SkillTier))
            {
                return null;
            }

            // Materials are REAL now: a finished piece eats stock from the
            // pack (ingots/cloth/boards). No stock, no output — the behavior
            // notices the dry spell and goes looking for materials instead.
            if (profile.Materials.Length > 0 &&
                !CrafterStock.Consume(bot, profile,
                    Utility.RandomMinMax(profile.CostMin, profile.CostMax)))
            {
                return null;
            }

            // Roll 2 — pick a band, then an item within it.
            var band = PickBand(bot.SkillTier, profile);
            if (band.Length == 0)
            {
                return null;
            }

            Item item;
            try
            {
                item = band[Utility.Random(band.Length)]?.Invoke(bot);
            }
            catch
            {
                // A factory should never throw, but never let one break the
                // tick if a content type misbehaves.
                item = null;
            }

            if (item == null)
            {
                return null;
            }

            if (profile.Markable && bot.SkillTier == BotSkillTier.Grandmaster)
            {
                ApplyMakersMark(item, bot);
            }

            if (!bot.AddToBackpack(item))
            {
                // Pack full / couldn't place — discard rather than leak.
                item.Delete();
                return null;
            }

            return item;
        }

        // Choose a band by tier weight. An empty Minor/Rare band falls back to
        // Common so a roll never wastes a produced item.
        private static Func<PlayerBot, Item>[] PickBand(BotSkillTier tier, CrafterProfile profile)
        {
            var (c, m, r) = BandWeights(tier);
            int total = c + m + r;
            if (total <= 0)
            {
                return profile.Common;
            }

            int roll = Utility.Random(total);
            if (roll < c)
            {
                return profile.Common;
            }
            if (roll < c + m)
            {
                return profile.Minor.Length > 0 ? profile.Minor : profile.Common;
            }
            return profile.Rare.Length > 0 ? profile.Rare : profile.Common;
        }

        // Stamp UO's authentic maker's mark on GM smith/tailor output:
        // Exceptional quality + the "crafted by <name>" property. The switch
        // naturally no-ops on item types that can't be marked (e.g. a fish),
        // though fisherman profiles are flagged non-markable anyway.
        private static void ApplyMakersMark(Item item, PlayerBot bot)
        {
            var name = bot.RawName;
            switch (item)
            {
                case BaseWeapon w:
                    w.Quality = WeaponQuality.Exceptional;
                    w.Crafter = name;
                    w.PlayerConstructed = true;
                    break;
                case BaseArmor a:
                    a.Quality = ArmorQuality.Exceptional;
                    a.Crafter = name;
                    a.PlayerConstructed = true;
                    break;
                case BaseClothing cl:
                    cl.Quality = ClothingQuality.Exceptional;
                    cl.Crafter = name;
                    cl.PlayerConstructed = true;
                    break;
            }
        }
    }
}
