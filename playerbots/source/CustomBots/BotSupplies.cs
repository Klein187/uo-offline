// =========================================================================
// BotSupplies.cs — running out is REAL; restocking is an errand.
//
// T2A had no magic refills: you carried a big stock of arrows, reagents,
// bandages and recall scrolls, and when you ran low you LEFT — back to
// town, to the bowyer or the mage shop or your bank box — and bought
// more. Bots now do exactly that:
//
//   - Kits spawn with era-sized stocks (hundreds of arrows, a fat
//     reagent stash, a bandage pile, a stack of recall scrolls).
//   - Nothing refills invisibly. When a class-relevant supply drops
//     below its threshold, the next destination pick becomes a SUPPLY
//     ERRAND to a vendor that sells it (or a bank — the bank box is the
//     stash). Hunters break off the hunt; crawlers cut the dungeon run
//     short and head up.
//   - The refill happens ON ARRIVAL, visibly: a purchase emote, gold
//     leaving the purse, a console line. A bot that happens to pass a
//     vendor while low tops up opportunistically ("while I'm here").
//
// Outlaws are excluded from errands (vendors sit in guard zones); their
// supplies simply run dry, as an outlaw's did.
// =========================================================================

using System;
using Server;
using Server.Items;

namespace Server.CustomBots
{
    public enum SupplyNeed
    {
        None,
        Ammo,
        Bandages,
        Reagents,
        Scrolls,
    }

    public static class BotSupplies
    {
        // ---- Thresholds (below = time to go shopping) and refill targets ----
        private const int AmmoLow = 25,     AmmoFull = 150;
        private const int BoltFull = 60;
        private const int BandageLow = 8,   BandageFull = 50;
        private const int ReagentLow = 8,   ReagentFull = 50;
        private const int ScrollLow = 2,    ScrollFull = 6;

        // Don't re-run errands back to back (a failed trip or an empty
        // purse shouldn't loop the bot at the counter).
        private static readonly TimeSpan ErrandCooldown = TimeSpan.FromMinutes(10);

        private static readonly Type[] ReagentTypes =
        {
            typeof(BlackPearl), typeof(Bloodmoss), typeof(Garlic),
            typeof(Ginseng), typeof(MandrakeRoot), typeof(Nightshade),
            typeof(SpidersSilk), typeof(SulfurousAsh),
        };

        private static bool UsesAmmo(PlayerBot bot) =>
            bot.Class is BotClass.Archer or BotClass.Ranger;

        private static bool UsesBandages(PlayerBot bot) =>
            bot.Class is BotClass.Warrior or BotClass.Fencer or BotClass.Archer
                      or BotClass.Ranger or BotClass.Healer or BotClass.Tamer;

        // Only real spell-slingers burn reagents (combat casting consumes
        // them; the Recall illusion doesn't).
        private static bool UsesReagents(PlayerBot bot) =>
            bot.Class == BotClass.Mage;

        // Casters recall on mana. Of the rest, only Journeyman-and-up can
        // afford the mage shop's tickets — a Novice or Apprentice walks
        // everywhere, exactly like every fresh character in the era did,
        // so they never shop for (or carry) recall scrolls at all.
        private static bool UsesScrolls(PlayerBot bot) =>
            bot.Skills[SkillName.Magery].Base < MagicTravel.RecallMinMagery &&
            BotSkillTierHelper.Rank(bot.SkillTier) >= 2;

        // How deep a stack this bot restocks to: mid tiers keep a couple
        // of escapes, veterans a real stack.
        private static int ScrollTarget(PlayerBot bot) =>
            BotSkillTierHelper.Rank(bot.SkillTier) >= 4 ? ScrollFull : 3;

        // -------------------------------------------------------------------
        // What (if anything) does this bot need? Priority: the thing that
        // stops a fight first.
        // -------------------------------------------------------------------
        public static SupplyNeed FirstNeed(PlayerBot bot)
        {
            var pack = bot?.Backpack;
            if (pack == null || bot.Deleted)
            {
                return SupplyNeed.None;
            }

            if (UsesAmmo(bot) && pack.GetAmount(typeof(Arrow)) < AmmoLow)
            {
                return SupplyNeed.Ammo;
            }
            if (UsesBandages(bot) && pack.GetAmount(typeof(Bandage)) < BandageLow)
            {
                return SupplyNeed.Bandages;
            }
            if (UsesReagents(bot))
            {
                foreach (var t in ReagentTypes)
                {
                    if (pack.GetAmount(t) < ReagentLow)
                    {
                        return SupplyNeed.Reagents;
                    }
                }
            }
            if (UsesScrolls(bot) && pack.GetAmount(typeof(RecallScroll)) < ScrollLow)
            {
                return SupplyNeed.Scrolls;
            }
            return SupplyNeed.None;
        }

        // Which arrival types stock which need. Banks stock EVERYTHING —
        // the bank box is where a real player kept the reserve.
        private static bool Satisfies(DestinationType at, SupplyNeed need) =>
            at == DestinationType.Bank ||
            need switch
            {
                SupplyNeed.Ammo     => at is DestinationType.VendorBowyer
                                          or DestinationType.VendorProvisioner,
                SupplyNeed.Bandages => at is DestinationType.VendorProvisioner
                                          or DestinationType.Healer,
                SupplyNeed.Reagents => at is DestinationType.VendorMage
                                          or DestinationType.VendorAlchemist,
                SupplyNeed.Scrolls  => at == DestinationType.VendorMage,
                _                   => false,
            };

        // -------------------------------------------------------------------
        // The errand: when something's low (and the cooldown allows), pick
        // the nearest destination that sells it. Same-landmass preferred;
        // cross-water accepted as a fallback (Recall handles the crossing).
        // Returns null when nothing is needed.
        // -------------------------------------------------------------------
        public static string PickErrandDestination(PlayerBot bot)
        {
            if (bot == null || Core.Now < bot.NextSupplyErrandAt)
            {
                return null;
            }
            var need = FirstNeed(bot);
            if (need == SupplyNeed.None)
            {
                return null;
            }

            var graph = WaypointRegistry.Graph;
            var botNode = graph?.FindNearestNode(bot.Location);
            int botComp = botNode != null ? graph.ComponentOf(botNode.Name) : -1;

            BotDestination best = null, bestAnywhere = null;
            int bestDist = int.MaxValue, bestAnyDist = int.MaxValue;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type == DestinationType.Bank || !Satisfies(d.Type, need))
                {
                    // Vendors preferred over banks for the shopping theater;
                    // banks only serve arrivals that happen anyway.
                    continue;
                }
                int dist = Math.Max(Math.Abs(d.Location.X - bot.X),
                                    Math.Abs(d.Location.Y - bot.Y));

                if (dist < bestAnyDist)
                {
                    bestAnyDist = dist;
                    bestAnywhere = d;
                }

                if (botComp >= 0 && !string.IsNullOrEmpty(d.NearestWaypoint) &&
                    graph.ComponentOf(d.NearestWaypoint) != botComp)
                {
                    continue;
                }
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }

            best ??= bestAnywhere;
            if (best == null)
            {
                return null;
            }

            bot.NextSupplyErrandAt = Core.Now + ErrandCooldown;
            Console.WriteLine(
                $"[supplies] {bot.Name} is low on {need} — heading to '{best.Name}'");
            return best.Name;
        }

        // -------------------------------------------------------------------
        // Arrival: refill every need this stop can satisfy, visibly, for
        // gold. Called from the Traveler's arrival handoff for EVERY
        // destination — cheap no-op when nothing is needed.
        // -------------------------------------------------------------------
        public static void TryRestockAtArrival(PlayerBot bot, DestinationType at)
        {
            var pack = bot?.Backpack;
            if (pack == null || bot.Deleted)
            {
                return;
            }

            int cost = 0;
            var bought = new System.Collections.Generic.List<string>();

            if (UsesAmmo(bot) && Satisfies(at, SupplyNeed.Ammo) &&
                pack.GetAmount(typeof(Arrow)) < AmmoLow)
            {
                int added = TopUp(pack, typeof(Arrow), AmmoFull);
                added += TopUp(pack, typeof(Bolt), BoltFull);
                cost += added / 3;
                bought.Add("arrows");
            }
            if (UsesBandages(bot) && Satisfies(at, SupplyNeed.Bandages) &&
                pack.GetAmount(typeof(Bandage)) < BandageLow)
            {
                int added = TopUp(pack, typeof(Bandage), BandageFull);
                cost += added / 4;
                bought.Add("bandages");
            }
            if (UsesReagents(bot) && Satisfies(at, SupplyNeed.Reagents))
            {
                int added = 0;
                foreach (var t in ReagentTypes)
                {
                    if (pack.GetAmount(t) < ReagentLow)
                    {
                        added += TopUp(pack, t, ReagentFull);
                    }
                }
                if (added > 0)
                {
                    cost += added * 2;
                    bought.Add("reagents");
                }
            }
            if (UsesScrolls(bot) && Satisfies(at, SupplyNeed.Scrolls) &&
                pack.GetAmount(typeof(RecallScroll)) < ScrollLow)
            {
                int have = pack.GetAmount(typeof(RecallScroll));
                for (int i = have; i < ScrollTarget(bot); i++)
                {
                    pack.DropItem(new RecallScroll());
                    cost += 18;
                }
                bought.Add("recall scrolls");
            }

            if (bought.Count == 0)
            {
                return;
            }

            // Pay what the purse can cover (the illusion economy doesn't
            // refuse a sale — gold flows back in from hauls and loot).
            if (cost > 0)
            {
                int gold = pack.GetAmount(typeof(Gold));
                int pay = Math.Min(cost, gold);
                if (pay > 0)
                {
                    pack.ConsumeTotal(typeof(Gold), pay);
                }
            }

            BotScene.Deliver(bot, at == DestinationType.Bank
                ? "*fetches supplies from the bank box*"
                : "*buys supplies*");
            Console.WriteLine(
                $"[supplies] {bot.Name} restocked {string.Join(", ", bought)} " +
                $"at {at} (-{cost}gp; arrows now {pack.GetAmount(typeof(Arrow))}, " +
                $"bandages {pack.GetAmount(typeof(Bandage))}, " +
                $"scrolls {pack.GetAmount(typeof(RecallScroll))})");
        }

        // Refill a stackable to `target`; returns how many were added.
        private static int TopUp(Container pack, Type t, int target)
        {
            int have = pack.GetAmount(t);
            if (have >= target)
            {
                return 0;
            }
            int add = target - have;
            var item = CreateStack(t, add);
            if (item == null)
            {
                return 0;
            }
            pack.DropItem(item);
            return add;
        }

        // Item ctors here are `(int amount = 1)` — the default parameter is
        // compiler sugar, so a parameterless Activator.CreateInstance
        // throws ("no parameterless constructor"). Invoke the (int) ctor
        // with the amount; fall back to a true parameterless one.
        private static Item CreateStack(Type t, int amount)
        {
            try
            {
                var byAmount = t.GetConstructor(new[] { typeof(int) });
                if (byAmount != null)
                {
                    return byAmount.Invoke(new object[] { amount }) as Item;
                }
                var plain = t.GetConstructor(Type.EmptyTypes);
                if (plain?.Invoke(null) is Item item)
                {
                    if (item.Stackable)
                    {
                        item.Amount = amount;
                    }
                    return item;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[supplies] create {t.Name} failed: {ex.Message}");
            }
            return null;
        }
    }
}
