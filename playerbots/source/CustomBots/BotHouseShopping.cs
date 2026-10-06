// =========================================================================
// BotHouseShopping.cs — bots shop at the vendor houses too.
//
// Players in T2A bought most of their supplies and gear off player vendors
// in houses, not from the NPCs. Bots now do the same:
//
//   - Every bot-owned vendor house is a destination ("Name's shop") on the
//     road node nearest its door, and its doors are unlocked, as a public
//     shop's were.
//   - A supply errand (arrows, bandages, reagents, recall scrolls, pet
//     food) looks at the vendor houses as well as the town shops. A house
//     vendor that has what the bot is short of, at a sane price, wins when
//     it is no farther than the town shop plus a bit, and sometimes even
//     when it is a longer walk: people liked the vendor houses.
//   - Now and then a fighter with gold goes looking for a better weapon,
//     and a vendor house that has one is where it goes.
//
// At the shop the customer lets itself in, walks up to the vendor and buys
// the way a player's purchase completes (PlayerVendorBuyGump): whole lot,
// gold from the pack then the bank, the price into the vendor's till. The
// owner banks the till the next time it is home.
//
//   homes_request.txt "token shop N" sends N online bots to the nearest
//   vendor house that has something they want.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Items;
using Server.Mobiles;
using Server.Multis;

namespace Server.CustomBots
{
    public static class BotHouseShopping
    {
        // ---- Knobs ----

        // A house shop wins a supply errand when it is no farther than the
        // town shop plus this many tiles...
        private const int ExtraWalk = 150;

        // ...or, this often, when it is within FarShop tiles anyway.
        private const double PreferHouseChance = 0.4;
        private const int FarShop = 500;

        // Never pay more than this times what the goods are worth.
        private const double MaxMarkup = 1.6;

        // Gold a bot keeps back from any purchase.
        private const int KeepBack = 150;

        // Browsing for a weapon: chance per new trip, who, and how often.
        private const double BrowseChance = 0.08;
        private const int BrowseMinGold = 800;
        private static readonly TimeSpan BrowseCooldown = TimeSpan.FromMinutes(40);

        // ---- State ----

        private static readonly Dictionary<string, BaseHouse> _shopByDest = new();
        private static readonly Dictionary<BaseHouse, string> _destOfShop = new();
        private static readonly Dictionary<PlayerBot, DateTime> _nextBrowse = new();

        public static int ShopCount => _shopByDest.Count;

        // Every bot-owned house with a vendor in it. Called at boot, after
        // vendor houses are placed, and for each new one.
        public static void RegisterShops()
        {
            foreach (var h in BaseHouse.AllHouses)
            {
                if (h != null && !h.Deleted && h.Owner is PlayerBot && BotVendorHouses.VendorOf(h) != null)
                {
                    RegisterShop(h);
                }
            }
        }

        public static void RegisterShop(BaseHouse house)
        {
            if (_destOfShop.ContainsKey(house) || house.Owner is not PlayerBot owner)
            {
                return;
            }
            var d = BotHomes.AddDestination($"{owner.Name}'s shop", DestinationType.HouseShop, house.BanLocation);
            if (d == null)
            {
                return;
            }
            _shopByDest[d.Name] = house;
            _destOfShop[house] = d.Name;

            // A shop is open to the public.
            foreach (var door in house.Doors)
            {
                if (door != null)
                {
                    door.Locked = false;
                }
            }
        }

        public static Point3D? RoadOfShop(BaseHouse house) =>
            house != null && _destOfShop.TryGetValue(house, out var name) &&
            DestinationCatalog.GetByName(name) is BotDestination d ? d.Location : null;

        public static bool IsShop(string destName) => destName != null && _shopByDest.ContainsKey(destName);

        // -------------------------------------------------------------------
        // What a bot is looking for, and what a vendor has of it.
        // -------------------------------------------------------------------

        private sealed class Offer
        {
            public Item Item;
            public int Price;
            public bool Weapon;
        }

        private static int Budget(PlayerBot bot)
        {
            int pack = bot.Backpack?.GetAmount(typeof(Gold)) ?? 0;
            return Math.Max(0, pack + Banker.GetBalance(bot) - KeepBack);
        }

        private static List<Offer> OffersFor(PlayerBot bot, PlayerVendor vendor, bool weapons)
        {
            var offers = new List<Offer>();
            if (vendor?.Backpack == null || vendor.Deleted)
            {
                return offers;
            }
            int budget = Budget(bot);
            if (budget <= 0)
            {
                return offers;
            }

            var wanted = BotSupplies.WantedStacks(bot);
            var heldMax = HeldWeapon(bot)?.MaxDamage ?? 0;
            var skill = WeaponSkillOf(bot);
            bool hasShield = bot.FindItemOnLayer(Layer.TwoHanded) is BaseShield;
            Offer bestWeapon = null;

            foreach (var item in vendor.Backpack.Items)
            {
                var vi = vendor.GetVendorItem(item);
                if (vi == null || !vi.Valid || !vi.IsForSale || vi.Price <= 0 || vi.Price > budget)
                {
                    continue;
                }
                int worth = BotAppraisal.Value(item);
                if (worth > 0 && vi.Price > worth * MaxMarkup)
                {
                    continue;
                }

                foreach (var (type, target) in wanted)
                {
                    if (type.IsInstanceOfType(item) && bot.Backpack.GetAmount(type) < target)
                    {
                        offers.Add(new Offer { Item = item, Price = vi.Price });
                        break;
                    }
                }

                if (weapons && skill != null && item is BaseWeapon w && w.Skill == skill.Value &&
                    w.MaxDamage > heldMax && !(hasShield && w.Layer == Layer.TwoHanded) &&
                    (bestWeapon == null || w.MaxDamage > ((BaseWeapon)bestWeapon.Item).MaxDamage))
                {
                    bestWeapon = new Offer { Item = item, Price = vi.Price, Weapon = true };
                }
            }

            if (bestWeapon != null)
            {
                offers.Add(bestWeapon);
            }
            return offers;
        }

        // -------------------------------------------------------------------
        // Choosing a shop
        // -------------------------------------------------------------------

        private static string NearestShopWith(PlayerBot bot, bool weapons, int maxDist)
        {
            string best = null;
            int bestD = int.MaxValue;
            foreach (var (dest, house) in _shopByDest)
            {
                if (house.Deleted || house.Map != bot.Map || house.Owner == bot)
                {
                    continue;
                }
                int d = Math.Max(Math.Abs(house.X - bot.X), Math.Abs(house.Y - bot.Y));
                if (d > maxDist || d >= bestD)
                {
                    continue;
                }
                var offers = OffersFor(bot, BotVendorHouses.VendorOf(house), weapons);
                if (offers.Count > 0 && (!weapons || offers.Exists(o => o.Weapon)))
                {
                    best = dest;
                    bestD = d;
                }
            }
            return best;
        }

        // Supply errand: a house shop instead of the town one, when it makes
        // sense. townDist is how far the town shop is.
        public static string ShopForSupplies(PlayerBot bot, int townDist)
        {
            if (_shopByDest.Count == 0)
            {
                return null;
            }
            int reach = Utility.RandomDouble() < PreferHouseChance
                ? Math.Max(FarShop, townDist + ExtraWalk)
                : townDist + ExtraWalk;
            return NearestShopWith(bot, weapons: false, reach);
        }

        // A new trip for a fighter with money: go look for a better weapon.
        public static string PickBrowseTrip(PlayerBot bot)
        {
            if (_shopByDest.Count == 0 || WeaponSkillOf(bot) == null ||
                Utility.RandomDouble() >= BrowseChance ||
                _nextBrowse.TryGetValue(bot, out var next) && Core.Now < next ||
                Budget(bot) < BrowseMinGold)
            {
                return null;
            }
            _nextBrowse[bot] = Core.Now + BrowseCooldown;
            var dest = NearestShopWith(bot, weapons: true, FarShop);
            if (dest != null)
            {
                Console.WriteLine($"[houseshop] {bot.Name} goes to look at the weapons at '{dest}'");
            }
            return dest;
        }

        // -------------------------------------------------------------------
        // At the shop
        // -------------------------------------------------------------------

        // Traveler handoff. True = taken over.
        public static bool OnArrived(PlayerBot bot, string destName)
        {
            if (!_shopByDest.TryGetValue(destName, out var house) || house.Deleted)
            {
                return false;
            }
            var vendor = BotVendorHouses.VendorOf(house);
            if (vendor == null)
            {
                return false;
            }
            if (house.Owner == bot)
            {
                bot.Behavior = new HomeVisitBehavior(house);
                return true;
            }
            bot.Behavior = new HomeVisitBehavior(house, vendor);
            return true;
        }

        // The purchase, the way PlayerVendorBuyGump completes one.
        public static int Buy(PlayerBot bot, PlayerVendor vendor)
        {
            var offers = OffersFor(bot, vendor, weapons: true);
            var bought = new List<string>();
            int spent = 0;

            var targets = new Dictionary<Type, int>();
            foreach (var (t, n) in BotSupplies.WantedStacks(bot))
            {
                targets[t] = n;
            }

            foreach (var o in offers)
            {
                // Several lots of the same thing: stop once the bot is full.
                if (!o.Weapon)
                {
                    bool stillShort = false;
                    foreach (var (t, n) in targets)
                    {
                        if (t.IsInstanceOfType(o.Item) && bot.Backpack.GetAmount(t) < n)
                        {
                            stillShort = true;
                        }
                    }
                    if (!stillShort)
                    {
                        continue;
                    }
                }
                var vi = vendor.GetVendorItem(o.Item);
                if (vi == null || !vi.Valid || !o.Item.IsChildOf(vendor.Backpack) ||
                    o.Price > Budget(bot))
                {
                    continue;
                }

                var old = o.Weapon ? HeldWeapon(bot) : null;
                if (!bot.PlaceInBackpack(o.Item))
                {
                    continue;
                }

                int left = o.Price;
                left -= bot.Backpack.ConsumeUpTo(typeof(Gold), left);
                if (left > 0)
                {
                    Banker.Withdraw(bot, left);
                }
                vendor.HoldGold += o.Price;
                spent += o.Price;

                if (o.Weapon)
                {
                    if (old != null)
                    {
                        bot.Backpack.DropItem(old);
                    }
                    bot.EquipItem(o.Item);
                }
                bought.Add(Describe(o.Item));
            }

            if (bought.Count > 0)
            {
                vendor.Say(bot.Name);
                Console.WriteLine($"[houseshop] {bot.Name} bought {string.Join(", ", bought)} " +
                    $"off {vendor.Owner?.Name}'s vendor for {spent} gold");
            }
            else
            {
                Console.WriteLine($"[houseshop] {bot.Name} looked over {vendor.Owner?.Name}'s vendor " +
                    "and bought nothing");
            }
            return bought.Count;
        }

        private static string Describe(Item item)
        {
            string name = item.Name ?? item.GetType().Name;
            return item.Amount > 1 ? $"{item.Amount} {name}" : name;
        }

        private static SkillName? WeaponSkillOf(PlayerBot bot)
        {
            SkillName best = SkillName.Swords;
            double bestVal = 0;
            foreach (var s in new[] { SkillName.Swords, SkillName.Fencing, SkillName.Macing, SkillName.Archery })
            {
                double v = bot.Skills[s].Base;
                if (v > bestVal)
                {
                    bestVal = v;
                    best = s;
                }
            }
            return bestVal >= 30 ? best : null;
        }

        private static BaseWeapon HeldWeapon(PlayerBot bot) =>
            bot.FindItemOnLayer(Layer.OneHanded) as BaseWeapon ??
            bot.FindItemOnLayer(Layer.TwoHanded) as BaseWeapon;

        // Test hook: send n online bots that want something to a shop.
        public static List<string> SendShoppers(int n)
        {
            var names = new List<string>();
            var bots = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot b && !b.Deleted && b.Map == Map.Felucca && b.Alive && !b.LoggingOut &&
                    b.Behavior is TravelerBehavior && b.Combatant == null && b.Behavior is not PKBehavior)
                {
                    bots.Add(b);
                }
            }
            while (names.Count < n && bots.Count > 0)
            {
                int i = Utility.Random(bots.Count);
                var b = bots[i];
                bots.RemoveAt(i);
                // Test only: run the bot out of its supplies, so it has a
                // real reason to shop.
                foreach (var item in new List<Item>(b.Backpack.Items))
                {
                    if (item is BaseReagent or Bandage or Arrow or Bolt or RecallScroll)
                    {
                        item.Delete();
                    }
                }
                var dest = NearestShopWith(b, weapons: false, 6000) ?? NearestShopWith(b, weapons: true, 6000);
                if (dest == null)
                {
                    continue;
                }
                b.Behavior = new TravelerBehavior { DestinationName = dest };
                Console.WriteLine($"[houseshop] TEST {b.Name} sent to '{dest}'");
                names.Add(b.Name);
            }
            return names;
        }
    }
}
