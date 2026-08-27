// =========================================================================
// BotEconomy.cs — the visible economy (IDEAS 4.1 illusion loop + 4.2
// trades that conclude) and home cities (1.3).
//
// The insight from IDEAS: even a 90%-illusion economy reads as real
// because you SEE the transactions happen. Three kinds of theater:
//
//   DELIVERY  A hauling gatherer reaches town: if a crafter bot is
//             working nearby, they play the handoff scene (ore for
//             coins); otherwise the load goes "into the bank" with a
//             deposit emote. Real materials leave the pack either way.
//   PURCHASE  An adventurer at a crafter's station buys something:
//             ask → price → *hands over coins* → "sold!".
//   WTS DEAL  Two bank sitters conclude one of the endless WTS
//             broadcasts: offer → "ill take it" → *trade window* →
//             "sold!". The spam becomes believable the moment any of
//             it ever visibly works.
//
// BotHomeCities: every bot rolls a home city at creation (weighted by
// city size); DestinationCatalog.PickWeighted biases rolls toward home,
// so REGULARS emerge — the same smith at the same forge every evening.
//
//   [BotTrade — force one purchase/WTS scene (testing)
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    // ---------------------------------------------------------------------
    public static class BotHomeCities
    {
        private static List<(string city, int weight)> _cities;

        // Distinct cities from the destination catalog, weighted by how
        // many destinations each has (big cities get more residents).
        private static void EnsureBuilt()
        {
            if (_cities != null && _cities.Count > 0)
            {
                return;
            }
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DestinationCatalog.All)
            {
                if (!string.IsNullOrEmpty(d.City))
                {
                    counts[d.City] = counts.TryGetValue(d.City, out var n) ? n + 1 : 1;
                }
            }
            _cities = new List<(string, int)>();
            foreach (var kv in counts)
            {
                _cities.Add((kv.Key, kv.Value));
            }
        }

        public static string RollHome()
        {
            EnsureBuilt();
            if (_cities == null || _cities.Count == 0)
            {
                return "";
            }
            int total = 0;
            foreach (var (_, w) in _cities)
            {
                total += w;
            }
            int r = Utility.Random(total);
            foreach (var (city, w) in _cities)
            {
                r -= w;
                if (r < 0)
                {
                    return city;
                }
            }
            return _cities[0].city;
        }
    }

    // ---------------------------------------------------------------------
    public static class BotEconomy
    {
        public static bool Enabled = true;

        // A purchase or WTS scene plays at most this often.
        private static readonly TimeSpan SceneAttemptMin = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan SceneAttemptMax = TimeSpan.FromMinutes(6);

        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

        private static Timer _timer;
        private static DateTime _nextSceneAttempt = DateTime.MinValue;

        // Per-bot scene cooldown so the same pair doesn't run a market
        // stall on loop.
        private static readonly Dictionary<Serial, DateTime> _cooldowns = new();
        private static readonly TimeSpan BotCooldown = TimeSpan.FromMinutes(12);

        public static void Configure()
        {
            _timer = Timer.DelayCall(TickInterval, TickInterval, OnTick);
            CommandSystem.Register("BotTrade", AccessLevel.GameMaster, Force_OnCommand);
        }

        private static void OnTick()
        {
            if (!Enabled || Core.Now < _nextSceneAttempt)
            {
                return;
            }
            _nextSceneAttempt = Core.Now + TimeSpan.FromSeconds(
                Utility.RandomMinMax((int)SceneAttemptMin.TotalSeconds,
                                     (int)SceneAttemptMax.TotalSeconds));

            // Alternate between the two scene kinds.
            if (Utility.RandomBool())
            {
                if (!TryPurchaseScene())
                {
                    TryWtsScene();
                }
            }
            else
            {
                if (!TryWtsScene())
                {
                    TryPurchaseScene();
                }
            }
        }

        private static bool OnCooldown(PlayerBot bot) =>
            _cooldowns.TryGetValue(bot.Serial, out var until) && Core.Now < until;

        private static void SetCooldown(params PlayerBot[] bots)
        {
            if (_cooldowns.Count > 2000)
            {
                _cooldowns.Clear();
            }
            foreach (var b in bots)
            {
                _cooldowns[b.Serial] = Core.Now + BotCooldown;
            }
        }

        private static bool IsSceneReady(PlayerBot bot) =>
            bot != null && !bot.Deleted && bot.Alive &&
            !bot.LoggingOut && bot.Combatant == null &&
            !BotPartyManager.IsInParty(bot) &&
            !OnCooldown(bot);

        // -------------------------------------------------------------------
        // PURCHASE — someone buys from a working crafter.
        // -------------------------------------------------------------------
        private static bool TryPurchaseScene()
        {
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot crafter ||
                    crafter.Behavior is not CrafterBehavior ||
                    !IsSceneReady(crafter))
                {
                    continue;
                }

                // A would-be customer within browsing range.
                foreach (var n in crafter.Map.GetMobilesInRange(crafter.Location, 8))
                {
                    if (n is PlayerBot buyer && buyer != crafter &&
                        IsSceneReady(buyer) &&
                        !BotClassHelper.IsArtisan(buyer.Class) &&
                        buyer.Behavior is BankSitterBehavior or IdleBehavior
                                       or WanderBehavior or ShopperBehavior)
                    {
                        PlayPurchase(buyer, crafter);
                        return true;
                    }
                }
            }
            return false;
        }

        private static void PlayPurchase(PlayerBot buyer, PlayerBot seller)
        {
            SetCooldown(buyer, seller);

            // A REAL sale when the shelf and the purse allow: the finished
            // piece moves into the buyer's pack and the coin into the
            // crafter's. Either missing → the browse stays window-shopping.
            Item sold = null;
            int price = 0;
            if (seller.Behavior is CrafterBehavior shop)
            {
                var item = shop.TakeMadeItem();
                if (item != null)
                {
                    int asking = Utility.RandomMinMax(25, 80);
                    if (CrafterStock.GoldOnHand(buyer) >= asking &&
                        buyer.AddToBackpack(item) &&
                        CrafterStock.SpendGold(buyer, asking))
                    {
                        seller.AddToBackpack(new Server.Items.Gold(asking));
                        sold = item;
                        price = asking;
                    }
                }
            }

            Console.WriteLine(sold != null
                ? $"[economy] {buyer.Name} bought a {sold.GetType().Name} from {seller.Name} for {price}gp at ({seller.X},{seller.Y})"
                : $"[economy] {buyer.Name} browses at {seller.Name}'s station at ({seller.X},{seller.Y})");

            BotScene.Play(
                (0.0, buyer,  BotScene.Pick("trade_ask")),
                (2.5, seller, BotScene.Pick("trade_reply")),
                (2.5, buyer,  "ty"),
                (1.5, seller, BotScene.Pick("trade_close")));
        }

        // -------------------------------------------------------------------
        // WTS DEAL — a bank broadcast actually concludes.
        //
        // It used to be four lines of dialogue and nothing else: neither
        // bot moved, no item existed, no coin changed hands. Now the
        // seller is holding real stock (BotShop), a buyer with real money
        // walks over, they haggle over the real numbers, and the goods and
        // the gold actually move. BotShopDeal owns the state machine.
        // -------------------------------------------------------------------
        private static bool TryWtsScene()
        {
            // More than a couple of these running at once turns a bank
            // into a trading floor. Two is a busy day.
            if (BotShopDeal.ActiveCount >= 2)
            {
                return false;
            }

            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot seller ||
                    seller.Behavior is not BankSitterBehavior ||
                    !IsSceneReady(seller) ||
                    !BotShop.HasStock(seller))
                {
                    continue;
                }

                if (BotShopDeal.TryStart(seller, IsSceneReady))
                {
                    SetCooldown(seller);
                    return true;
                }
            }
            return false;
        }

        // -------------------------------------------------------------------
        // DELIVERY — a hauling gatherer arrives in town. Called from the
        // Traveler's arrival handoff. The haul is REAL trade now: a crafter
        // of the matching trade working nearby (smith for ore, carpenter
        // for logs) pays actual gold from its purse and the raw load is
        // refined into the crafter's working stock (ingots/boards) on the
        // spot. No matching crafter — or a broke one — and the load goes
        // to the bank/shop like before.
        // -------------------------------------------------------------------
        public static void DeliverMaterials(PlayerBot gatherer, DestinationType at)
        {
            gatherer.HaulPending = false;

            // Count and remove the haul — the bot's own pack AND the pack
            // beast's (that's where a shift with a beast put the yield).
            int hauled = 0;
            Type rawType = null;
            hauled += TakeYield(gatherer.Backpack, ref rawType);
            var beast = gatherer.PackAnimal;
            if (beast is { Deleted: false })
            {
                hauled += TakeYield(beast.Backpack, ref rawType);
            }

            // The beast stands through the unload with its packs empty.
            // Its disposition belongs to the caller now: the Traveler
            // walks it back to the nearest stables and puts it away (or
            // turns it loose offscreen when no stables is in reach).

            // A crafter of the matching trade working nearby buys the load.
            PlayerBot crafter = null;
            CrafterProfile buyingProfile = null;
            if (hauled > 0 && rawType != null)
            {
                foreach (var m in gatherer.Map.GetMobilesInRange(gatherer.Location, 12))
                {
                    if (m is PlayerBot c && c != gatherer && c.Alive &&
                        c.Behavior is CrafterBehavior)
                    {
                        var p = CrafterProfiles.For(c.Class);
                        if (p.RawGood == rawType)
                        {
                            crafter = c;
                            buyingProfile = p;
                            break;
                        }
                    }
                }
            }

            int price = hauled * Utility.RandomMinMax(2, 4);
            bool crafterPaid = crafter != null && hauled > 0 &&
                               CrafterStock.SpendGold(crafter, price);

            if (crafterPaid)
            {
                // Real coin changes hands; the raw haul is refined straight
                // into the crafter's stock (ore → ingots, logs → boards).
                // Anything past the stock cap "goes to the shop" with the
                // rest of the shipment.
                gatherer.AddToBackpack(new Server.Items.Gold(price));
                int accepted = CrafterStock.Add(crafter, buyingProfile, hauled);

                BotScene.Play(
                    (0.0, gatherer, BotScene.Pick("gather_deliver")),
                    (2.0, crafter,  BotScene.Pick("craft_buy")),
                    (2.0, gatherer, "ty"),
                    (1.5, crafter,  BotScene.Pick("trade_close")));

                Console.WriteLine(
                    $"[economy] {gatherer.Name} sold {hauled} raw materials to {crafter.Name} " +
                    $"for {price}gp at {at} ({accepted} into stock)" +
                    (beast != null ? " (pack beast unloaded)" : ""));
                return;
            }

            // No trade buyer — bank the load; the shop pays instead.
            if (hauled > 0)
            {
                gatherer.AddToBackpack(new Server.Items.Gold(price));
            }
            BotScene.Play(
                (0.0, gatherer, BotScene.Pick("gather_deliver")));

            Console.WriteLine(
                $"[economy] {gatherer.Name} delivered {hauled} materials at {at} (banked)" +
                (beast != null ? " (pack beast unloaded)" : ""));
        }

        // Remove all raw materials from a container; returns the amount and
        // reports the dominant raw type seen (a haul is single-type — logs
        // for lumberjacks, ore for miners).
        private static int TakeYield(Server.Items.Container pack, ref Type rawType)
        {
            if (pack == null)
            {
                return 0;
            }
            int taken = 0;
            var goods = new List<Item>();
            foreach (var item in pack.Items)
            {
                if (item is Server.Items.Log or Server.Items.IronOre)
                {
                    goods.Add(item);
                }
            }
            foreach (var item in goods)
            {
                taken += item.Amount;
                rawType = item is Server.Items.IronOre ? typeof(Server.Items.IronOre)
                                                       : typeof(Server.Items.Log);
                item.Delete();
            }
            return taken;
        }

        [Usage("BotTrade")]
        [Description("Forces one trade scene (purchase or WTS deal) somewhere in the world.")]
        private static void Force_OnCommand(CommandEventArgs e)
        {
            bool played = TryPurchaseScene() || TryWtsScene();
            e.Mobile.SendMessage(played
                ? "Trade scene started — watch the console/world."
                : "No eligible pair found (need a crafter with a browser, or two bank sitters).");
        }
    }
}
