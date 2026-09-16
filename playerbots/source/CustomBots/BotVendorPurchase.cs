// =========================================================================
// BotVendorPurchase.cs — a bot buys something real from a real vendor.
//
// A bot has no client, so the buy gump is not an option. This does what
// the gump does on the server: read the vendor's live buy list, pick what
// the bot actually wants, take the gold out of its pack, take the stock
// off the vendor, put the item in the pack. A bot with no gold buys
// nothing. Prices are the vendor's own, town tax included.
//
// What a bot wants here:
//   - the supplies it is short of (arrows, bandages, reagents, recall
//     scrolls, ribs for a pet), from any vendor that stocks them
//   - at a weaponsmith or blacksmith, a better weapon for its best
//     fighting skill than the one it holds
//
// Shops with a drawn vendor Area use this instead of the old invisible
// restock in BotSupplies. Shops without one keep the old behaviour.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotVendorPurchase
    {
        public static int PurchaseCount { get; private set; }

        private static readonly List<string> _recent = new();
        private const int RecentCap = 20;
        public static IReadOnlyList<string> Recent => _recent;

        private static void Remember(string line)
        {
            PurchaseCount++;
            _recent.Insert(0, $"{Core.Now:HH:mm:ss} {line}");
            if (_recent.Count > RecentCap)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }
            Console.WriteLine($"[vendor] {line}");
        }

        // A destination whose shop floor has been drawn as a vendor Area.
        public static bool HasDrawnVendorArea(string destName)
        {
            if (string.IsNullOrEmpty(destName))
            {
                return false;
            }
            var area = ZoneRegistry.AreaForDestination(destName, Point3D.Zero);
            return area != null && area.IsVendorArea;
        }

        // Which NPC classes run a shop of this area type. Anything else
        // that is a BaseVendor standing inside the polygon is the fallback.
        private static bool VendorFits(string areaType, BaseVendor v) => (areaType ?? "") switch
        {
            "VendorWeaponer"   => v is Weaponsmith,
            "VendorSmith"      => v is Blacksmith or Armorer or Weaponsmith,
            "VendorMage"       => v is Mage or Scribe,
            "VendorTailor"     => v is Tailor or Weaver,
            "VendorCarpenter"  => v is Carpenter,
            "VendorBowyer"     => v is Bowyer,
            "VendorAlchemist"  => v is Alchemist or Herbalist,
            "VendorProvisioner"=> v is Provisioner or Baker or Butcher or Cook,
            _                  => true,
        };

        // The vendor that serves this area: a matching class inside or
        // just outside the polygon first, else any vendor inside it.
        public static BaseVendor FindVendor(PaintedZone area, Map map)
        {
            if (area == null || map == null)
            {
                return null;
            }
            int half = Math.Max(area.MaxX - area.MinX, area.MaxY - area.MinY) / 2 + 4;
            var center = new Point3D(area.CenterX, area.CenterY, 0);
            BaseVendor best = null, any = null;
            int bestD = int.MaxValue, anyD = int.MaxValue;
            foreach (var m in map.GetMobilesInRange(center, half))
            {
                if (m is not BaseVendor v || v.Deleted || !v.Alive)
                {
                    continue;
                }
                bool inside = area.Contains(v.X, v.Y);
                bool near = inside || area.ContainsGrown(v.X, v.Y) ||
                            (v.X >= area.MinX - 3 && v.X <= area.MaxX + 3 &&
                             v.Y >= area.MinY - 3 && v.Y <= area.MaxY + 3);
                if (!near)
                {
                    continue;
                }
                int d = Math.Max(Math.Abs(v.X - area.CenterX), Math.Abs(v.Y - area.CenterY));
                if (VendorFits(area.Type, v) && d < bestD)
                {
                    bestD = d;
                    best = v;
                }
                if (inside && d < anyD)
                {
                    anyD = d;
                    any = v;
                }
            }
            return best ?? any;
        }

        // -----------------------------------------------------------------
        // The purchase. Returns what was bought, or null for nothing.
        // -----------------------------------------------------------------

        public static string TryBuy(PlayerBot bot, BaseVendor vendor, PaintedZone area)
        {
            var pack = bot?.Backpack;
            if (pack == null || bot.Deleted || vendor == null || vendor.Deleted)
            {
                return null;
            }

            IBuyItemInfo[] infos;
            try
            {
                vendor.UpdateBuyInfo();
                infos = vendor.GetBuyInfo();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[vendor] {vendor.Name} buy list failed: {ex.Message}");
                return null;
            }
            if (infos == null || infos.Length == 0)
            {
                return null;
            }

            int gold = pack.GetAmount(typeof(Gold));
            int spent = 0;
            var bought = new List<string>();

            // 1. Supplies the bot is short of, from this vendor's real shelf.
            foreach (var (type, target) in BotSupplies.WantedStacks(bot))
            {
                var bi = infos.FirstOrDefault(i => i is GenericBuyInfo g && g.Type == type && i.Amount > 0);
                if (bi == null)
                {
                    continue;
                }
                int have = pack.GetAmount(type);
                int want = Math.Min(target - have, bi.Amount);
                int price = Math.Max(1, bi.Price);
                int canAfford = (gold - spent) / price;
                int qty = Math.Min(want, canAfford);
                if (qty <= 0)
                {
                    continue;
                }
                if (bi.GetEntity() is not Item item)
                {
                    continue;
                }
                if (item.Stackable)
                {
                    item.Amount = qty;
                }
                else
                {
                    qty = 1;
                }
                pack.DropItem(item);
                bi.Amount -= qty;
                spent += qty * price;
                bought.Add($"{qty} {Noun(item, bi)}");
            }

            // 2. A better weapon, at a smith's.
            if (area != null && area.Type is "VendorWeaponer" or "VendorSmith")
            {
                var line = TryBuyWeapon(bot, pack, infos, gold - spent, out int weaponCost);
                if (line != null)
                {
                    spent += weaponCost;
                    bought.Add(line);
                }
            }

            if (bought.Count == 0)
            {
                Console.WriteLine($"[vendor] {bot.Name} ({bot.Class}) looked at {vendor.Name}'s wares and bought nothing " +
                                  $"({gold}gp in the purse, {infos.Length} item(s) on the shelf)");
                return null;
            }

            if (spent > 0)
            {
                pack.ConsumeTotal(typeof(Gold), Math.Min(spent, gold));
            }

            string what = string.Join(", ", bought);
            try
            {
                vendor.Say($"The total of thy purchase is {spent} gold. My thanks for the patronage.");
            }
            catch { }
            Remember($"{bot.Name} bought {what} from {vendor.Name} for {spent}gp" +
                     (area != null ? $" at {area.Name}" : ""));
            return what;
        }

        private static string Noun(Item item, IBuyItemInfo bi)
        {
            string n = !string.IsNullOrEmpty(bi.Name) ? bi.Name
                     : !string.IsNullOrEmpty(item.Name) ? item.Name
                     : item.GetType().Name;
            return n.ToLowerInvariant();
        }

        // The skill the bot fights with, if it fights at all.
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

        private static string TryBuyWeapon(PlayerBot bot, Container pack, IBuyItemInfo[] infos,
                                           int budget, out int cost)
        {
            cost = 0;
            var skill = WeaponSkillOf(bot);
            if (skill == null || budget <= 0)
            {
                return null;
            }
            var held = HeldWeapon(bot);
            int heldMax = held?.MaxDamage ?? 0;
            bool hasShield = bot.FindItemOnLayer(Layer.TwoHanded) is BaseShield;

            BaseWeapon pick = null;
            IBuyItemInfo pickInfo = null;
            foreach (var bi in infos)
            {
                if (bi is not GenericBuyInfo g || g.Type == null || bi.Amount <= 0 ||
                    !typeof(BaseWeapon).IsAssignableFrom(g.Type) || bi.Price > budget)
                {
                    continue;
                }
                BaseWeapon w;
                try
                {
                    w = bi.GetEntity() as BaseWeapon;
                }
                catch
                {
                    continue;
                }
                if (w == null)
                {
                    continue;
                }
                bool fits = w.Skill == skill.Value &&
                            w.MaxDamage > heldMax &&
                            !(hasShield && w.Layer == Layer.TwoHanded) &&
                            (pick == null || w.MaxDamage > pick.MaxDamage);
                if (fits)
                {
                    pick?.Delete();
                    pick = w;
                    pickInfo = bi;
                }
                else
                {
                    w.Delete();
                }
            }

            if (pick == null)
            {
                return null;
            }

            cost = Math.Max(1, pickInfo.Price);
            pickInfo.Amount -= 1;

            // Wear it like a player would: the old weapon goes in the pack.
            if (held != null)
            {
                pack.DropItem(held);
            }
            if (!bot.EquipItem(pick))
            {
                pack.DropItem(pick);
            }
            string name = pick.Name ?? pick.GetType().Name;
            return $"a {name.ToLowerInvariant()} (equipped)";
        }
    }
}
