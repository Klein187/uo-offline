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

        // Production tries a crafter makes at home per visit (most make
        // nothing, as at its station).
        private const int CraftTriesAtHome = 20;

        // A shelf under this sends the owner on a stock run.
        private const int LowShelf = 6;

        // Stock runs: who can afford one, how often, what it keeps back,
        // how much of a thing it buys, and the lot size it sells in.
        private const int StockRunMinGold = 1500;
        private static readonly TimeSpan StockRunCooldown = TimeSpan.FromMinutes(45);
        private const int StockRunReserve = 800;
        private const int ResaleLot = 150;
        private const int SaleLot = 50;
        private const int RunLots = 4;

        // The owner keeps this much ahead in the vendor's wage account.
        private const int WageReserve = 3000;

        // Houses in a hot spot sit closer together and nearer the road than
        // countryside homes, but never on the trail itself.
        private const int SpotMinTrail = 8, SpotSpacing = 16;

        private static readonly BotClass[] OwnerClasses =
        {
            BotClass.Merchant, BotClass.Smith, BotClass.Tailor, BotClass.Mage, BotClass.Warrior,
            BotClass.Miner, BotClass.Lumberjack, BotClass.Fisherman, BotClass.Archer, BotClass.Carpenter,
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
                        $"{house.Location} with {vendor?.Backpack?.Items.Count ?? 0} lots, vendor at " +
                        $"{vendor?.Location} ({(vendor != null && house.IsInside(vendor) ? "on the steps" : "in the yard")})");
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
            var vendor = new PlayerVendor(owner, house)
            {
                ShopName = $"{owner.Name}'s goods",
            };
            if (!PlaceOnDoorstep(vendor, house))
            {
                Console.WriteLine($"[vendorhouses] no room on the doorstep of the house at {house.Location}");
                vendor.Delete();
                return null;
            }
            vendor.BankAccount = WageReserve;
            return vendor;
        }

        // -------------------------------------------------------------------
        // The vendor stands outside on the doorstep, beside the walk from the
        // door to the yard, never on it: customers walk up to it outside, and
        // the owner can still get in and out.
        // -------------------------------------------------------------------
        public static bool PlaceOnDoorstep(PlayerVendor vendor, BaseHouse house)
        {
            if (!DoorstepSpot(house, out var spot, out var facing))
            {
                return false;
            }
            vendor.MoveToWorld(spot, house.Map);
            vendor.Direction = facing;
            return true;
        }

        private static bool DoorstepSpot(BaseHouse house, out Point3D spot, out Direction facing)
        {
            spot = Point3D.Zero;
            facing = Direction.South;
            var map = house.Map;
            if (!HomeVisitBehavior.FindDoor(house, out var outside, out _, out var dirIn))
            {
                return false;
            }

            // The walk out: from the door, straight out to the first tile
            // past the house. dirIn points in, so step the other way.
            int ox = 0, oy = 0;
            switch (dirIn)
            {
                case Direction.North: oy = 1; break;
                case Direction.South: oy = -1; break;
                case Direction.West:  ox = 1; break;
                case Direction.East:  ox = -1; break;
            }
            // Sideways is across that line.
            int sx = oy != 0 ? 1 : 0, sy = ox != 0 ? 1 : 0;

            BaseDoor door = null;
            foreach (var d in house.Doors)
            {
                if (d != null && Math.Abs(d.X - outside.X) + Math.Abs(d.Y - outside.Y) <= 3)
                {
                    door = d;
                    break;
                }
            }
            if (door == null)
            {
                return false;
            }

            // Nearest first: beside the steps, then beside the first yard
            // tile, on either side.
            for (int k = 1; k <= 4; k++)
            {
                foreach (int side in new[] { 1, -1 })
                {
                    int x = door.X + ox * k + sx * side * 1;
                    int y = door.Y + oy * k + sy * side * 1;
                    if (!Walkable.TryFindSeedZ(map, x, y, outside.Z, out var z) ||
                        !map.CanFit(x, y, z, 16, false, true) || Math.Abs(z - outside.Z) > 6)
                    {
                        continue;
                    }
                    spot = new Point3D(x, y, z);
                    // Face out toward the yard, where customers come from.
                    facing = dirIn switch
                    {
                        Direction.North => Direction.South,
                        Direction.South => Direction.North,
                        Direction.West  => Direction.East,
                        _               => Direction.West,
                    };
                    return true;
                }
            }
            return false;
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
        // The owner is home: takings, wages, and restocking from what it
        // actually has. Three sources and no others:
        //   - found: loot it brought back from dungeons and fights (stashed
        //     in the chest by BotHomes.StashLoot)
        //   - made:  a crafter works its materials into goods at home, and
        //     keeps what it made at its town station for its own shelf
        //   - bought: lots it bought in bulk at an NPC shop on a stock run
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

            int bought = ListResale(vendor, bot);
            int made = CraftAtHome(vendor, bot, house);
            int found = RestockFromChest(vendor, house, bot);

            if (takings > 0 || bought + made + found > 0)
            {
                Console.WriteLine($"[vendorhouses] {bot.Name} took {takings} gold from the vendor, " +
                    $"put out {found} found, {made} made, {bought} bought ({Count(vendor)} for sale)");
            }
        }

        private static int Count(PlayerVendor v) => v.Backpack?.Items.Count ?? 0;

        public static bool ShelfLow(PlayerBot bot)
        {
            var home = BotHomes.HomeOf(bot);
            var v = home != null ? VendorOf(home) : null;
            return v != null && Count(v) < LowShelf;
        }

        private static Container ChestOf(BaseHouse house)
        {
            foreach (var item in house.LockDowns)
            {
                if (item is Container c && !c.Deleted)
                {
                    return c;
                }
            }
            return null;
        }

        private static int Price(Item item, double markupMin, double markupMax)
        {
            int value = BotAppraisal.Value(item);
            if (value <= 0)
            {
                return 0;
            }
            double m = markupMin + Utility.RandomDouble() * (markupMax - markupMin);
            return Math.Max(5, BotShop.RoundPrice((int)(value * m)));
        }

        // ---- found ----

        private static bool Sellable(Item item, PlayerBot owner)
        {
            if (item.LootType != LootType.Regular || item is Gold or Key or Container)
            {
                return false;
            }
            if (item is BaseWeapon or BaseArmor or BaseJewel or BaseClothing or SpellScroll ||
                BotHomes.IsGem(item))
            {
                return true;
            }
            // Reagents and raw materials sell unless the owner works them.
            var mats = CrafterProfiles.For(owner.Class).Materials;
            if (Array.IndexOf(mats, item.GetType()) >= 0)
            {
                return false;
            }
            return item is BaseReagent or Hides or Leather or Cloth or Log or Board ||
                item.GetType().Name.EndsWith("Ingot") || item.GetType().Name.EndsWith("Ore");
        }

        private static int RestockFromChest(PlayerVendor vendor, BaseHouse house, PlayerBot owner)
        {
            var chest = ChestOf(house);
            if (chest == null)
            {
                return 0;
            }
            var sellable = new List<Item>();
            foreach (var item in chest.Items)
            {
                if (Sellable(item, owner))
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
                int price = Price(item, 1.1, 1.4);
                if (price > 0 && List(vendor, item, price))
                {
                    n++;
                }
            }
            return n;
        }

        // ---- made ----

        // A crafter with materials (in its pack or its chest) works a few
        // pieces at home, the same production its town station runs: real
        // materials used up, most tries making nothing.
        private static int CraftAtHome(PlayerVendor vendor, PlayerBot owner, BaseHouse house)
        {
            if (!WorksAtHome(owner.Class))
            {
                return 0;
            }
            var profile = CrafterProfiles.For(owner.Class);

            // Materials from the chest into the pack, where production
            // takes them from.
            var chest = ChestOf(house);
            if (chest != null)
            {
                foreach (var item in new List<Item>(chest.Items))
                {
                    if (Array.IndexOf(profile.Materials, item.GetType()) >= 0)
                    {
                        owner.Backpack.DropItem(item);
                    }
                }
            }

            // Finished pieces go on the shelf while there is room, and into
            // the chest after that (the next visit puts them out).
            int n = 0;
            for (int i = 0; i < CraftTriesAtHome; i++)
            {
                var item = CrafterProduction.TryProduce(owner, profile);
                if (item == null)
                {
                    continue;
                }
                n++;
                int price = Price(item, 1.2, 1.5);
                if (Count(vendor) >= MaxStock || price <= 0 || !List(vendor, item, price))
                {
                    if (chest != null)
                    {
                        chest.DropItem(item);
                    }
                }
            }

            // Leftover materials back in the chest.
            if (chest != null)
            {
                foreach (var item in new List<Item>(owner.Backpack.Items))
                {
                    if (Array.IndexOf(profile.Materials, item.GetType()) >= 0)
                    {
                        chest.DropItem(item);
                    }
                }
            }
            return n;
        }

        // The trades a bot works at a bench: the ones with real materials.
        // (CrafterProfiles.For falls back to the smith for anyone else.)
        private static bool WorksAtHome(BotClass c) =>
            c is BotClass.Smith or BotClass.Tailor or BotClass.Carpenter;

        // Pieces a crafter made at its town station go on its own shelf
        // instead of over the shop counter. CrafterBehavior asks this before
        // selling a piece off.
        public static bool KeepsOwnWork(PlayerBot bot)
        {
            var home = BotHomes.HomeOf(bot);
            return home != null && VendorOf(home) != null;
        }

        // ---- bought ----

        // What an owner buys in bulk at an NPC shop to sell on, by the shop
        // it goes to for its line of goods.
        private static readonly Type[] MageResale =
        {
            typeof(BlackPearl), typeof(Bloodmoss), typeof(Garlic), typeof(Ginseng),
            typeof(MandrakeRoot), typeof(Nightshade), typeof(SpidersSilk), typeof(SulfurousAsh),
            typeof(RecallScroll),
        };

        private static readonly Type[] SupplyResale =
        {
            typeof(Bandage), typeof(Arrow), typeof(Bolt), typeof(Garlic), typeof(Ginseng),
        };

        // Lots bought for resale, waiting to go on the shelf, with what each
        // one cost.
        private static readonly Dictionary<Serial, int> _boughtFor = new();
        private static readonly Dictionary<PlayerBot, string> _stockRuns = new();
        private static readonly Dictionary<PlayerBot, DateTime> _nextStockRun = new();

        private static readonly Dictionary<PlayerBot, DateTime> _stockRunSince = new();

        public static bool OnStockRun(PlayerBot bot)
        {
            if (!_stockRuns.ContainsKey(bot))
            {
                return false;
            }
            // A run that never arrived (knocked off course, died) lapses.
            if (_stockRunSince.TryGetValue(bot, out var since) && Core.Now - since > TimeSpan.FromMinutes(60))
            {
                _stockRuns.Remove(bot);
                _stockRunSince.Remove(bot);
                return false;
            }
            return true;
        }

        // Bought to sell on: BotHomes.StashLoot leaves these in the pack.
        public static bool IsResale(Item item) => _boughtFor.ContainsKey(item.Serial);

        // Test hook: a crafter owner works a session at home now. Returns
        // the pieces it put on the shelf, or -1 if it has no trade to work.
        public static int TestCraft(PlayerBot owner)
        {
            var home = BotHomes.HomeOf(owner);
            var vendor = home != null ? VendorOf(home) : null;
            if (vendor == null || !WorksAtHome(owner.Class))
            {
                return -1;
            }
            var chest = ChestOf(home);
            int before = 0;
            foreach (var m in CrafterProfiles.For(owner.Class).Materials)
            {
                before += (chest?.GetAmount(m) ?? 0) + owner.Backpack.GetAmount(m);
            }
            int made = CraftAtHome(vendor, owner, home);
            int after = 0;
            foreach (var m in CrafterProfiles.For(owner.Class).Materials)
            {
                after += (chest?.GetAmount(m) ?? 0) + owner.Backpack.GetAmount(m);
            }
            Console.WriteLine($"[vendorhouses] TEST {owner.Name} ({owner.Class}) crafted {made} piece(s) at home, " +
                $"materials {before} -> {after}");
            return made;
        }

        // Test hook: start a stock run now, whatever the shelf and the clock.
        public static bool ForceStockRun(PlayerBot bot)
        {
            _nextStockRun.Remove(bot);
            _stockRuns.Remove(bot);
            return TryStartStockRun(bot);
        }

        // Called by BotHomes' minute tick for a free owner whose shelf is low.
        public static bool TryStartStockRun(PlayerBot bot)
        {
            if (OnStockRun(bot) ||
                _nextStockRun.TryGetValue(bot, out var next) && Core.Now < next ||
                BotBanking.Wealth(bot) < StockRunMinGold)
            {
                return false;
            }
            _nextStockRun[bot] = Core.Now + StockRunCooldown;

            // The nearest real NPC shop that sells what this owner resells.
            var wanted = ResaleFor(bot.Class);
            BotDestination best = null;
            int bestD = int.MaxValue;
            foreach (var (dest, sells) in StockShops())
            {
                bool any = false;
                foreach (var t in wanted)
                {
                    if (sells.Contains(t))
                    {
                        any = true;
                        break;
                    }
                }
                if (!any || !RedTerritory.MayGoTo(bot, dest))
                {
                    continue;
                }
                int dist = Math.Max(Math.Abs(dest.Location.X - bot.X), Math.Abs(dest.Location.Y - bot.Y));
                if (dist < bestD)
                {
                    bestD = dist;
                    best = dest;
                }
            }
            if (best == null)
            {
                return false;
            }
            _stockRuns[bot] = best.Name;
            _stockRunSince[bot] = Core.Now;
            Console.WriteLine($"[vendorhouses] {bot.Name}'s shelf is low, off to '{best.Name}' to buy stock");
            bot.Behavior = new TravelerBehavior { DestinationName = best.Name };
            return true;
        }

        // TravelerBehavior arrival. True = taken over (bought, now heading
        // home).
        public static bool OnArrivedAt(PlayerBot bot, string destName)
        {
            if (!_stockRuns.TryGetValue(bot, out var want) || want != destName)
            {
                return false;
            }
            _stockRuns.Remove(bot);
            _stockRunSince.Remove(bot);

            int spent = 0;
            var bought = new List<string>();
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 26))
            {
                if (m is not BaseVendor npc || npc.Deleted)
                {
                    continue;
                }
                if (bought.Count >= RunLots)
                {
                    break;
                }
                spent += BuyForResale(bot, npc, bought);
            }

            if (bought.Count > 0)
            {
                Console.WriteLine($"[vendorhouses] {bot.Name} bought {string.Join(", ", bought)} " +
                    $"for {spent} gold to sell on");
            }
            else
            {
                var seen = new List<string>();
                foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 26))
                {
                    if (m is BaseVendor npc)
                    {
                        seen.Add($"{npc.Name} ({npc.GetType().Name})");
                    }
                }
                Console.WriteLine($"[vendorhouses] {bot.Name} found nothing to buy for the shop at '{destName}' " +
                    $"(standing {bot.Location}; vendors in range: {(seen.Count == 0 ? "none" : string.Join(", ", seen))})");
            }
            BotHomes.SendHome(bot);
            return true;
        }

        private static int BuyForResale(PlayerBot bot, BaseVendor npc, List<string> bought)
        {
            IBuyItemInfo[] infos;
            try
            {
                npc.UpdateBuyInfo();
                infos = npc.GetBuyInfo();
            }
            catch
            {
                return 0;
            }
            if (infos == null)
            {
                return 0;
            }

            var wanted = ResaleFor(bot.Class);

            int spent = 0;
            foreach (var bi in infos)
            {
                if (bi is not GenericBuyInfo g || g.Type == null || bi.Amount <= 0 ||
                    Array.IndexOf(wanted, g.Type) < 0 || bought.Exists(x => x.EndsWith(" " + g.Type.Name)))
                {
                    continue;
                }
                int budget = BotBanking.Wealth(bot) - StockRunReserve;
                int price = Math.Max(1, bi.Price);
                int qty = Math.Min(Math.Min(ResaleLot, bi.Amount), budget / price);
                if (qty < 10)
                {
                    continue;
                }
                if (bi.GetEntity() is not Item item)
                {
                    continue;
                }
                int cost = qty * price;
                int fromPack = bot.Backpack.ConsumeUpTo(typeof(Gold), cost);
                if (fromPack < cost && !Banker.Withdraw(bot, cost - fromPack))
                {
                    bot.Backpack.DropItem(new Gold(fromPack));
                    item.Delete();
                    continue;
                }
                if (item.Stackable)
                {
                    item.Amount = qty;
                }
                bot.Backpack.DropItem(item);
                bi.Amount -= qty;
                spent += cost;
                _boughtFor[item.Serial] = cost;
                bought.Add($"{qty} {item.GetType().Name}");
                if (bought.Count >= RunLots)
                {
                    break;
                }
            }
            return spent;
        }

        private static Type[] ResaleFor(BotClass c) =>
            c is BotClass.Mage or BotClass.TreasureHunter ? MageResale :
            c == BotClass.Merchant ? Concat(MageResale, SupplyResale) : SupplyResale;

        // Real NPC shops that sell resale goods, each with the catalog
        // destination nearest it (within reach of the counter). Built once:
        // destination names and types do not always match what stands
        // there, so this goes by the NPCs themselves.
        private static List<(BotDestination dest, HashSet<Type> sells)> _stockShops;

        private static List<(BotDestination dest, HashSet<Type> sells)> StockShops()
        {
            if (_stockShops != null)
            {
                return _stockShops;
            }
            _stockShops = new List<(BotDestination, HashSet<Type>)>();
            var all = Concat(MageResale, SupplyResale);
            var dests = DestinationCatalog.All;
            var seen = new HashSet<string>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not BaseVendor npc || npc.Deleted || npc.Map != Map.Felucca ||
                    npc is PlayerVendor)
                {
                    continue;
                }
                IBuyItemInfo[] infos;
                try
                {
                    infos = npc.GetBuyInfo();
                }
                catch
                {
                    continue;
                }
                var sells = new HashSet<Type>();
                foreach (var bi in infos ?? Array.Empty<IBuyItemInfo>())
                {
                    if (bi is GenericBuyInfo g && g.Type != null && Array.IndexOf(all, g.Type) >= 0)
                    {
                        sells.Add(g.Type);
                    }
                }
                if (sells.Count == 0)
                {
                    continue;
                }
                BotDestination near = null;
                int nearD = 19;
                foreach (var d in dests)
                {
                    if (!string.IsNullOrEmpty(d.Dungeon))
                    {
                        continue;
                    }
                    int dd = Math.Max(Math.Abs(d.Location.X - npc.X), Math.Abs(d.Location.Y - npc.Y));
                    if (dd < nearD)
                    {
                        nearD = dd;
                        near = d;
                    }
                }
                if (near != null && seen.Add(near.Name))
                {
                    _stockShops.Add((near, sells));
                }
            }
            Console.WriteLine($"[vendorhouses] {_stockShops.Count} NPC shops sell stock owners resell");
            return _stockShops;
        }

        private static Type[] Concat(Type[] a, Type[] b)
        {
            var r = new Type[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);
            return r;
        }

        // Resale lots in the owner's pack go on the shelf at a markup over
        // what they cost, split into lots a customer can afford.
        private static int ListResale(PlayerVendor vendor, PlayerBot owner)
        {
            int n = 0;
            foreach (var item in new List<Item>(owner.Backpack.Items))
            {
                if (!_boughtFor.TryGetValue(item.Serial, out var cost) || Count(vendor) >= MaxStock)
                {
                    continue;
                }
                _boughtFor.Remove(item.Serial);
                double unit = cost / (double)Math.Max(1, item.Amount);
                while (item.Amount > SaleLot + 20 && Count(vendor) < MaxStock - 1)
                {
                    Item part;
                    try
                    {
                        part = (Item)Activator.CreateInstance(item.GetType());
                    }
                    catch
                    {
                        break;
                    }
                    part.Amount = SaleLot;
                    part.Hue = item.Hue;
                    item.Amount -= SaleLot;
                    if (List(vendor, part, Markup(unit * part.Amount)))
                    {
                        n++;
                    }
                }
                if (List(vendor, item, Markup(unit * item.Amount)))
                {
                    n++;
                }
            }
            return n;
        }

        private static int Markup(double cost) =>
            Math.Max(5, BotShop.RoundPrice((int)(cost * Utility.RandomMinMax(130, 160) / 100.0)));

        // Opening stock for a brand-new shop: the owner's goods from before
        // it had a vendor. After that, restocking is only what it finds,
        // makes or buys (OnOwnerHome).
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
