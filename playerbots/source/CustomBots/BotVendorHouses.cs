// =========================================================================
// BotVendorHouses.cs — shops in houses at the busy spots.
//
// In T2A the good stuff was not at the NPCs. It was on player vendors in
// houses along the busy roads. This puts bot-owned ones there.
//
// Hot spots are drawn in the map editor (EDIT house spots): a polygon and
// how many vendor houses it should hold. They live in
// Data/CustomSpawns/house_spots.json. On boot, at the end of First Time
// Setup, and on request, every spot is filled up to its count:
//
//   - a new regular (a trader, a crafter or an adventurer) places a real
//     house inside the polygon, as BotHomes does for any home
//   - a real PlayerVendor stands just inside the door, with a shop name
//   - it opens with stock rolled from the hawker table for the owner's
//     line of work
//
// After that the shop runs on the bot economy. Each time the owner comes
// home (HomeVisitBehavior) it collects the vendor's takings into its bank,
// pays the vendor's wages ahead, and restocks: whatever it brought home
// from dungeons and gathering, and a crafter adds a few pieces of its own
// work. Real players buy the normal way, and the gold lands in the owner's
// bank, which is what lets it buy the next thing.
//
//   homes_request.txt "token vendors" places any missing vendor houses now.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Server.Items;
using Server.Mobiles;
using Server.Multis;

namespace Server.CustomBots
{
    public static class BotVendorHouses
    {
        // ---- Knobs ----

        // Stock a vendor carries at most.
        private const int MaxStock = 14;

        // What a shop opens with.
        private const int OpeningStockMin = 8, OpeningStockMax = 12;

        // Pieces of its own work a crafter adds per visit.
        private const int CraftPerVisit = 2;

        // The owner keeps this much ahead in the vendor's wage account.
        private const int WageReserve = 3000;

        // Houses in a hot spot sit closer together and nearer the road than
        // countryside homes, but never on the trail itself.
        private const int SpotMinTrail = 8, SpotSpacing = 16;

        private static readonly BotClass[] OwnerClasses =
        {
            BotClass.Merchant, BotClass.Smith, BotClass.Tailor, BotClass.Mage, BotClass.Warrior,
            BotClass.Miner, BotClass.Lumberjack, BotClass.Fisherman, BotClass.Archer,
        };

        private static string SpotsPath =>
            Path.Combine(Core.BaseDirectory, "Data", "CustomSpawns", "house_spots.json");

        public sealed class Spot
        {
            public string Name;
            public int Count;
            public Point2D[] Poly;
        }

        public static List<Spot> LoadSpots()
        {
            var list = new List<Spot>();
            try
            {
                if (!File.Exists(SpotsPath))
                {
                    return list;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(SpotsPath));
                if (!doc.RootElement.TryGetProperty("Spots", out var arr))
                {
                    return list;
                }
                foreach (var el in arr.EnumerateArray())
                {
                    var pts = new List<Point2D>();
                    if (el.TryGetProperty("poly", out var poly))
                    {
                        foreach (var p in poly.EnumerateArray())
                        {
                            pts.Add(new Point2D(p[0].GetInt32(), p[1].GetInt32()));
                        }
                    }
                    if (pts.Count < 3)
                    {
                        continue;
                    }
                    list.Add(new Spot
                    {
                        Name = el.TryGetProperty("name", out var n) ? n.GetString() : "spot",
                        Count = el.TryGetProperty("count", out var c) ? Math.Clamp(c.GetInt32(), 1, 10) : 2,
                        Poly = pts.ToArray(),
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[vendorhouses] house_spots.json unreadable: {ex.Message}");
            }
            return list;
        }

        // Fill every drawn spot up to its count. Returns houses placed.
        public static int EnsureAll()
        {
            int placed = 0;
            foreach (var spot in LoadSpots())
            {
                int have = 0;
                foreach (var h in BaseHouse.AllHouses)
                {
                    if (h != null && !h.Deleted && h.Map == Map.Felucca &&
                        h.Owner is PlayerBot && BotHousing.InPolygon(spot.Poly, h.X, h.Y))
                    {
                        have++;
                    }
                }
                for (int i = have; i < spot.Count; i++)
                {
                    if (PlaceOne(spot))
                    {
                        placed++;
                    }
                }
            }
            if (placed > 0)
            {
                Console.WriteLine($"[vendorhouses] placed {placed} vendor house(s)");
            }
            return placed;
        }

        private static bool PlaceOne(Spot spot)
        {
            var map = Map.Felucca;
            var probe = new Rat { Controlled = true, Blessed = true, Hidden = true };
            try
            {
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    int multiId = BotHousing.HouseMultiIds[Utility.Random(BotHousing.HouseMultiIds.Length)];
                    if (!BotHousing.TryFindSiteInPolygon(map, probe, multiId, spot.Poly, SpotMinTrail,
                            SpotSpacing, out var center))
                    {
                        continue;
                    }
                    var owner = BotHomes.NewOwner(OwnerClasses);
                    var house = BotHomes.PlaceWithDeed(owner, multiId, center, map);
                    if (house == null)
                    {
                        owner.Delete();
                        continue;
                    }
                    BotHomes.SettleIn(house, owner, seeded: true);
                    var vendor = AddVendor(house, owner);
                    if (vendor != null)
                    {
                        // Half the opening stock is the everyday stuff people
                        // come for (reagents, bandages, arrows, scrolls), the
                        // rest the owner's line of work.
                        for (int i = Utility.RandomMinMax(OpeningStockMin, OpeningStockMax); i > 0; i--)
                        {
                            if (i % 2 == 0)
                            {
                                AddSupplies(vendor);
                            }
                            else
                            {
                                AddCrafted(vendor, owner);
                            }
                        }
                    }
                    Console.WriteLine($"[vendorhouses] {owner.Name} opened a shop at '{spot.Name}' " +
                        $"{house.Location} with {vendor?.Backpack?.Items.Count ?? 0} lots");
                    return true;
                }
            }
            finally
            {
                probe.Delete();
            }
            Console.WriteLine($"[vendorhouses] no room left for another house at '{spot.Name}'");
            return false;
        }

        private static PlayerVendor AddVendor(BaseHouse house, PlayerBot owner)
        {
            if (!HomeVisitBehavior.FindDoor(house, out _, out var inside, out _))
            {
                Console.WriteLine($"[vendorhouses] no front door found on the house at {house.Location}");
                return null;
            }
            var vendor = new PlayerVendor(owner, house)
            {
                ShopName = $"{owner.Name}'s goods",
            };
            vendor.MoveToWorld(inside, house.Map);

            // Stand clear of the doorway, one step further in.
            var tiles = HomeVisitBehavior.InteriorTiles(house);
            Point3D best = inside;
            int bestD = int.MaxValue;
            foreach (var t in tiles)
            {
                int d = Math.Max(Math.Abs(t.X - inside.X), Math.Abs(t.Y - inside.Y));
                if (d >= 1 && d < bestD && house.Map.CanFit(t.X, t.Y, t.Z, 16, false, true))
                {
                    bestD = d;
                    best = t;
                }
            }
            vendor.MoveToWorld(best, house.Map);
            vendor.BankAccount = WageReserve;
            return vendor;
        }

        public static PlayerVendor VendorOf(BaseHouse house)
        {
            foreach (var v in house.PlayerVendors)
            {
                if (v is PlayerVendor pv && !pv.Deleted)
                {
                    return pv;
                }
            }
            return null;
        }

        // -------------------------------------------------------------------
        // The owner is home: takings, wages, restock.
        // -------------------------------------------------------------------
        public static void OnOwnerHome(PlayerBot bot, BaseHouse house)
        {
            var vendor = VendorOf(house);
            if (vendor == null || vendor.Owner != bot)
            {
                return;
            }

            int takings = vendor.HoldGold;
            if (takings > 0)
            {
                vendor.HoldGold = 0;
                Banker.Deposit(bot, takings);
            }

            int topUp = WageReserve - vendor.BankAccount;
            if (topUp > 0 && Banker.Withdraw(bot, topUp))
            {
                vendor.BankAccount += topUp;
            }

            int stocked = 0;
            stocked += RestockFromChest(vendor, house);
            if (IsCrafter(bot.Class))
            {
                for (int i = 0; i < CraftPerVisit && Count(vendor) < MaxStock; i++)
                {
                    if (AddCrafted(vendor, bot))
                    {
                        stocked++;
                    }
                }
            }

            // Keep a few everyday supplies on the shelf: the owner buys them
            // in bulk and sells them on, as players did.
            if (CountSupplies(vendor) < 3 && Count(vendor) < MaxStock && AddSupplies(vendor))
            {
                stocked++;
            }

            if (takings > 0 || stocked > 0)
            {
                Console.WriteLine($"[vendorhouses] {bot.Name} took {takings} gold from the vendor, " +
                    $"put out {stocked} new lot(s) ({Count(vendor)} for sale)");
            }
        }

        private static int Count(PlayerVendor v) => v.Backpack?.Items.Count ?? 0;

        private static int CountSupplies(PlayerVendor v)
        {
            int n = 0;
            if (v.Backpack != null)
            {
                foreach (var item in v.Backpack.Items)
                {
                    if (item is BaseReagent or Bandage or Arrow or Bolt or RecallScroll)
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        // What the owner brought home and stashed goes out on the vendor.
        private static int RestockFromChest(PlayerVendor vendor, BaseHouse house)
        {
            Container chest = null;
            foreach (var item in house.LockDowns)
            {
                if (item is Container c && !c.Deleted)
                {
                    chest = c;
                    break;
                }
            }
            if (chest == null)
            {
                return 0;
            }

            var sellable = new List<Item>();
            foreach (var item in chest.Items)
            {
                if (item is BaseWeapon or BaseArmor or BaseJewel or BaseClothing &&
                    item.LootType == LootType.Regular)
                {
                    sellable.Add(item);
                }
            }

            int n = 0;
            foreach (var item in sellable)
            {
                if (Count(vendor) >= MaxStock)
                {
                    break;
                }
                int value = BotAppraisal.Value(item);
                if (value <= 0)
                {
                    continue;
                }
                int price = BotShop.RoundPrice((int)(value * Utility.RandomMinMax(110, 140) / 100.0));
                if (List(vendor, item, Math.Max(5, price)))
                {
                    n++;
                }
            }
            return n;
        }

        private static bool IsCrafter(BotClass c) =>
            c is BotClass.Smith or BotClass.Tailor or BotClass.Miner or BotClass.Lumberjack or
                BotClass.Fisherman or BotClass.Mage or BotClass.Merchant;

        // A piece of the owner's own trade, from the hawker table.
        private static bool AddCrafted(PlayerVendor vendor, PlayerBot owner)
        {
            Func<Item, bool> fits = owner.Class switch
            {
                BotClass.Smith      => i => i is BaseWeapon or BaseArmor { MaterialType: not ArmorMaterialType.Leather and not ArmorMaterialType.Studded },
                BotClass.Tailor     => i => i is BaseClothing or BaseArmor { MaterialType: ArmorMaterialType.Leather or ArmorMaterialType.Studded } || i.GetType().Name is "Cloth" or "Leather",
                BotClass.Miner      => i => i.GetType().Name.EndsWith("Ingot") || i.GetType().Name.EndsWith("Ore"),
                BotClass.Lumberjack => i => i is Log or Board || i is BaseRanged,
                BotClass.Mage       => i => i is BaseReagent or SpellScroll,
                BotClass.Fisherman  => i => i.GetType().Name.Contains("Fish") || i is BaseReagent,
                _                   => null,
            };
            // Nothing of its own trade in the table: general goods instead.
            var item = BotShop.RollVendorGoods(fits, out int price) ?? BotShop.RollVendorGoods(null, out price);
            if (item == null)
            {
                return false;
            }
            if (!List(vendor, item, price))
            {
                item.Delete();
                return false;
            }
            return true;
        }

        private static bool AddSupplies(PlayerVendor vendor)
        {
            var item = BotShop.RollVendorGoods(null, out int price, bulkOnly: true);
            if (item == null)
            {
                return false;
            }
            if (!List(vendor, item, price))
            {
                item.Delete();
                return false;
            }
            return true;
        }

        // PlayerVendor prices an item through a prompt the player answers.
        // There is no public way to do it from code, so call the same
        // private SetVendorItem the prompt ends up in.
        private static MethodInfo _setVendorItem;

        private static bool List(PlayerVendor vendor, Item item, int price)
        {
            if (vendor.Backpack == null)
            {
                return false;
            }
            _setVendorItem ??= typeof(PlayerVendor).GetMethod("SetVendorItem",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(Item), typeof(int), typeof(string) }, null);
            if (_setVendorItem == null)
            {
                Console.WriteLine("[vendorhouses] PlayerVendor.SetVendorItem not found");
                return false;
            }
            vendor.Backpack.DropItem(item);
            try
            {
                _setVendorItem.Invoke(vendor, new object[] { item, price, "" });
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[vendorhouses] pricing failed: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }
    }
}
