// =========================================================================
// BotShop.cs — a hawker's actual stock, and the haggling over it.
//
// The bank's WTS spam used to be pure theater: a bot picked a random line
// out of wts.txt and shouted it while holding nothing. Ask what it was
// selling and it shrugged at you. This makes the claim true.
//
// A hawker is STOCKED — one real item, in its real backpack, rolled from
// the table below. The WTS line is generated FROM that item, so what it
// shouts is what it has. When the item leaves the pack (sold, or looted
// off its corpse) the entry evaporates and it stops advertising.
//
// Everything downstream reads from here:
//   BankSitterBehavior  stocks a hawker and shouts the generated line
//   BotEconomy          runs the bot-to-bot sale (walk over, haggle, pay)
//   BotSpeechResponder  lets a real player haggle by talking
//   PlayerBot           opens the real trade window for the payoff
//
// Test hooks: [BotShop [stock|list]  +  headless shop_request.txt token.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Text;
using Server.Commands;
using Server.Items;

namespace Server.CustomBots
{
    // What kind of thing is on offer. Drives the rarity roll, and lets
    // the loud end of the table be rare on purpose — nobody at the bank
    // is hawking a keep every ten minutes.
    public enum GoodsKind
    {
        Bulk,      // regs, ingots, leather, bandages, arrows — the daily grind
        Gear,      // weapons and armor, usually exceptional
        Scroll,    // recall/gate/mark and the useful circles
        Rare,      // magic weapons and armor, hues worth naming
        BigTicket, // house deeds — the "WTS tower 100k" end of the corpus
    }

    // How hard the seller bargains. Rolled per stock, not per bot: the
    // same trader is stubborn about a vanq and flexible about leather.
    public enum HaggleTemper
    {
        Firm,   // barely moves, and says so
        Normal,
        Eager,  // wants it gone, will take a real cut
    }

    // ---------------------------------------------------------------------
    // One row of the stock table.
    // ---------------------------------------------------------------------
    internal sealed class Goods
    {
        public string Type;          // fully-qualified item type
        public string Noun;          // what a player would call it ("halberd")
        public GoodsKind Kind;
        public int Weight;           // rarity weight in the roll
        public int MinPrice;         // for the whole lot, not per unit
        public int MaxPrice;
        public int MinAmount = 1;    // stackables only
        public int MaxAmount = 1;
        public bool Exceptional;     // roll Quality = Exceptional on gear
        public bool Magic;           // roll T2A magic properties
        public bool RareHue;         // roll a dye-tub hue worth naming
    }

    // ---------------------------------------------------------------------
    // What one bot currently has for sale.
    // ---------------------------------------------------------------------
    public sealed class ShopStock
    {
        public Serial ItemSerial;
        public string Noun;          // "exceptional halberd", "100 mandrake"
        public int Asking;           // the number it shouts
        public int Floor;            // it will not go below this
        public HaggleTemper Temper;
        public GoodsKind Kind;
        public DateTime StockedAt;

        // Per-customer bargaining state, so two buyers working the same
        // hawker don't share one counter-offer ladder.
        public readonly Dictionary<Serial, HaggleSession> Sessions = new();
    }

    // One buyer's negotiation with one seller.
    public sealed class HaggleSession
    {
        public int Rounds;
        public int AgreedPrice;      // > 0 once a number is settled
        public DateTime LastAt;
        public DateTime AgreedUntil; // a handshake goes stale
    }

    // ---------------------------------------------------------------------
    public static class BotShop
    {
        public static bool Enabled { get; set; } = true;

        // A settled price is good for this long: long enough to walk to
        // the bank and back, short enough that it isn't a standing offer.
        public static readonly TimeSpan AgreementWindow = TimeSpan.FromMinutes(5);

        // A negotiation with no word from the buyer goes cold.
        private static readonly TimeSpan SessionIdle = TimeSpan.FromMinutes(3);

        private static readonly Dictionary<Serial, ShopStock> _stock = new();

        public static void Configure()
        {
            CommandSystem.Register("BotShop", AccessLevel.GameMaster, OnCommand);
        }

        public static int StockedCount => _stock.Count;

        // -----------------------------------------------------------------
        // The table. Weights are relative across the whole list — Bulk is
        // deliberately heavy, because most of what anyone ever hawked was
        // a pile of regs, and a tower is supposed to turn heads.
        // -----------------------------------------------------------------
        // Internal, not private: BotAppraisal reads these rows to value an
        // item a PLAYER is selling. Same prices in both directions.
        internal static readonly Goods[] Table =
        {
            // ---- Bulk: the daily grind ----
            new() { Type = "Server.Items.BlackPearl",   Noun = "black pearl",   Kind = GoodsKind.Bulk, Weight = 55, MinAmount = 40,  MaxAmount = 200, MinPrice = 120, MaxPrice = 900 },
            new() { Type = "Server.Items.Bloodmoss",    Noun = "blood moss",    Kind = GoodsKind.Bulk, Weight = 55, MinAmount = 40,  MaxAmount = 200, MinPrice = 120, MaxPrice = 900 },
            new() { Type = "Server.Items.Garlic",       Noun = "garlic",        Kind = GoodsKind.Bulk, Weight = 45, MinAmount = 50,  MaxAmount = 250, MinPrice = 60,  MaxPrice = 500 },
            new() { Type = "Server.Items.Ginseng",      Noun = "ginseng",       Kind = GoodsKind.Bulk, Weight = 45, MinAmount = 50,  MaxAmount = 250, MinPrice = 60,  MaxPrice = 500 },
            new() { Type = "Server.Items.MandrakeRoot", Noun = "mandrake",      Kind = GoodsKind.Bulk, Weight = 55, MinAmount = 40,  MaxAmount = 200, MinPrice = 150, MaxPrice = 1100 },
            new() { Type = "Server.Items.Nightshade",   Noun = "nightshade",    Kind = GoodsKind.Bulk, Weight = 50, MinAmount = 40,  MaxAmount = 200, MinPrice = 100, MaxPrice = 800 },
            new() { Type = "Server.Items.SpidersSilk",  Noun = "spider silk",   Kind = GoodsKind.Bulk, Weight = 50, MinAmount = 40,  MaxAmount = 200, MinPrice = 90,  MaxPrice = 700 },
            new() { Type = "Server.Items.SulfurousAsh", Noun = "sulfurous ash", Kind = GoodsKind.Bulk, Weight = 50, MinAmount = 40,  MaxAmount = 200, MinPrice = 90,  MaxPrice = 700 },
            new() { Type = "Server.Items.Bandage",      Noun = "bandages",      Kind = GoodsKind.Bulk, Weight = 60, MinAmount = 100, MaxAmount = 400, MinPrice = 400, MaxPrice = 1800 },
            new() { Type = "Server.Items.IronIngot",    Noun = "iron ingots",   Kind = GoodsKind.Bulk, Weight = 55, MinAmount = 100, MaxAmount = 500, MinPrice = 500, MaxPrice = 3000 },
            new() { Type = "Server.Items.Leather",      Noun = "leather",       Kind = GoodsKind.Bulk, Weight = 45, MinAmount = 50,  MaxAmount = 250, MinPrice = 150, MaxPrice = 900 },
            new() { Type = "Server.Items.Hides",        Noun = "hides",         Kind = GoodsKind.Bulk, Weight = 40, MinAmount = 50,  MaxAmount = 250, MinPrice = 120, MaxPrice = 800 },
            new() { Type = "Server.Items.Board",        Noun = "boards",        Kind = GoodsKind.Bulk, Weight = 40, MinAmount = 100, MaxAmount = 400, MinPrice = 200, MaxPrice = 1200 },
            new() { Type = "Server.Items.Arrow",        Noun = "arrows",        Kind = GoodsKind.Bulk, Weight = 35, MinAmount = 200, MaxAmount = 600, MinPrice = 200, MaxPrice = 900 },
            new() { Type = "Server.Items.Bolt",         Noun = "bolts",         Kind = GoodsKind.Bulk, Weight = 30, MinAmount = 200, MaxAmount = 600, MinPrice = 200, MaxPrice = 900 },

            // ---- Gear: exceptional weapons and armor ----
            new() { Type = "Server.Items.Halberd",         Noun = "halberd",        Kind = GoodsKind.Gear, Weight = 26, MinPrice = 2500, MaxPrice = 7000, Exceptional = true },
            new() { Type = "Server.Items.Bardiche",        Noun = "bardiche",       Kind = GoodsKind.Gear, Weight = 20, MinPrice = 2200, MaxPrice = 6000, Exceptional = true },
            new() { Type = "Server.Items.Katana",          Noun = "katana",         Kind = GoodsKind.Gear, Weight = 26, MinPrice = 1500, MaxPrice = 4500, Exceptional = true },
            new() { Type = "Server.Items.Kryss",           Noun = "kryss",          Kind = GoodsKind.Gear, Weight = 24, MinPrice = 1500, MaxPrice = 4500, Exceptional = true },
            new() { Type = "Server.Items.Broadsword",      Noun = "broadsword",     Kind = GoodsKind.Gear, Weight = 22, MinPrice = 1400, MaxPrice = 4000, Exceptional = true },
            new() { Type = "Server.Items.Longsword",       Noun = "longsword",      Kind = GoodsKind.Gear, Weight = 22, MinPrice = 1400, MaxPrice = 4000, Exceptional = true },
            new() { Type = "Server.Items.WarHammer",       Noun = "war hammer",     Kind = GoodsKind.Gear, Weight = 18, MinPrice = 1600, MaxPrice = 4500, Exceptional = true },
            new() { Type = "Server.Items.Mace",            Noun = "mace",           Kind = GoodsKind.Gear, Weight = 16, MinPrice = 900,  MaxPrice = 2600, Exceptional = true },
            new() { Type = "Server.Items.Maul",            Noun = "maul",           Kind = GoodsKind.Gear, Weight = 14, MinPrice = 900,  MaxPrice = 2600, Exceptional = true },
            new() { Type = "Server.Items.WarFork",         Noun = "war fork",       Kind = GoodsKind.Gear, Weight = 14, MinPrice = 1200, MaxPrice = 3200, Exceptional = true },
            new() { Type = "Server.Items.Spear",           Noun = "spear",          Kind = GoodsKind.Gear, Weight = 14, MinPrice = 1100, MaxPrice = 3000, Exceptional = true },
            new() { Type = "Server.Items.Scimitar",        Noun = "scimitar",       Kind = GoodsKind.Gear, Weight = 14, MinPrice = 1200, MaxPrice = 3200, Exceptional = true },
            new() { Type = "Server.Items.Bow",             Noun = "bow",            Kind = GoodsKind.Gear, Weight = 20, MinPrice = 1200, MaxPrice = 3400, Exceptional = true },
            new() { Type = "Server.Items.Crossbow",        Noun = "crossbow",       Kind = GoodsKind.Gear, Weight = 16, MinPrice = 1300, MaxPrice = 3600, Exceptional = true },
            new() { Type = "Server.Items.HeavyCrossbow",   Noun = "heavy xbow",     Kind = GoodsKind.Gear, Weight = 20, MinPrice = 2000, MaxPrice = 5500, Exceptional = true },
            new() { Type = "Server.Items.PlateChest",      Noun = "plate tunic",    Kind = GoodsKind.Gear, Weight = 22, MinPrice = 1800, MaxPrice = 5000, Exceptional = true },
            new() { Type = "Server.Items.PlateLegs",       Noun = "plate legs",     Kind = GoodsKind.Gear, Weight = 18, MinPrice = 1400, MaxPrice = 4000, Exceptional = true },
            new() { Type = "Server.Items.PlateArms",       Noun = "plate arms",     Kind = GoodsKind.Gear, Weight = 16, MinPrice = 1200, MaxPrice = 3400, Exceptional = true },
            new() { Type = "Server.Items.PlateHelm",       Noun = "plate helm",     Kind = GoodsKind.Gear, Weight = 16, MinPrice = 1100, MaxPrice = 3000, Exceptional = true },
            new() { Type = "Server.Items.PlateGorget",     Noun = "plate gorget",   Kind = GoodsKind.Gear, Weight = 14, MinPrice = 800,  MaxPrice = 2200, Exceptional = true },
            new() { Type = "Server.Items.ChainChest",      Noun = "chain tunic",    Kind = GoodsKind.Gear, Weight = 14, MinPrice = 1000, MaxPrice = 2800, Exceptional = true },
            new() { Type = "Server.Items.StuddedChest",    Noun = "studded tunic",  Kind = GoodsKind.Gear, Weight = 14, MinPrice = 700,  MaxPrice = 2000, Exceptional = true },
            new() { Type = "Server.Items.RingmailChest",   Noun = "ringmail tunic", Kind = GoodsKind.Gear, Weight = 12, MinPrice = 700,  MaxPrice = 2000, Exceptional = true },
            new() { Type = "Server.Items.HeaterShield",    Noun = "heater shield",  Kind = GoodsKind.Gear, Weight = 16, MinPrice = 900,  MaxPrice = 2600, Exceptional = true },
            new() { Type = "Server.Items.MetalKiteShield", Noun = "kite shield",    Kind = GoodsKind.Gear, Weight = 14, MinPrice = 800,  MaxPrice = 2400, Exceptional = true },

            // ---- Scrolls ----
            new() { Type = "Server.Items.RecallScroll",      Noun = "recall scrolls", Kind = GoodsKind.Scroll, Weight = 30, MinAmount = 5, MaxAmount = 25, MinPrice = 300, MaxPrice = 1500 },
            new() { Type = "Server.Items.GateTravelScroll",  Noun = "gate scrolls",   Kind = GoodsKind.Scroll, Weight = 20, MinAmount = 3, MaxAmount = 15, MinPrice = 400, MaxPrice = 1800 },
            new() { Type = "Server.Items.MarkScroll",        Noun = "mark scrolls",   Kind = GoodsKind.Scroll, Weight = 20, MinAmount = 3, MaxAmount = 15, MinPrice = 250, MaxPrice = 1200 },
            new() { Type = "Server.Items.GreaterHealScroll", Noun = "gheal scrolls",  Kind = GoodsKind.Scroll, Weight = 18, MinAmount = 5, MaxAmount = 25, MinPrice = 200, MaxPrice = 1000 },
            new() { Type = "Server.Items.EnergyBoltScroll",  Noun = "ebolt scrolls",  Kind = GoodsKind.Scroll, Weight = 16, MinAmount = 5, MaxAmount = 25, MinPrice = 250, MaxPrice = 1300 },

            // ---- Rare: magic gear and hues worth naming ----
            new() { Type = "Server.Items.Kryss",        Noun = "kryss",         Kind = GoodsKind.Rare, Weight = 6, MinPrice = 9000,  MaxPrice = 30000, Magic = true },
            new() { Type = "Server.Items.Katana",       Noun = "katana",        Kind = GoodsKind.Rare, Weight = 6, MinPrice = 9000,  MaxPrice = 30000, Magic = true },
            new() { Type = "Server.Items.Halberd",      Noun = "halberd",       Kind = GoodsKind.Rare, Weight = 5, MinPrice = 12000, MaxPrice = 40000, Magic = true },
            new() { Type = "Server.Items.WarHammer",    Noun = "war hammer",    Kind = GoodsKind.Rare, Weight = 4, MinPrice = 10000, MaxPrice = 32000, Magic = true },
            new() { Type = "Server.Items.Bow",          Noun = "bow",           Kind = GoodsKind.Rare, Weight = 4, MinPrice = 9000,  MaxPrice = 28000, Magic = true },
            new() { Type = "Server.Items.PlateChest",   Noun = "plate tunic",   Kind = GoodsKind.Rare, Weight = 5, MinPrice = 8000,  MaxPrice = 26000, Magic = true },
            new() { Type = "Server.Items.PlateLegs",    Noun = "plate legs",    Kind = GoodsKind.Rare, Weight = 4, MinPrice = 7000,  MaxPrice = 22000, Magic = true },
            new() { Type = "Server.Items.HeaterShield", Noun = "heater shield", Kind = GoodsKind.Rare, Weight = 3, MinPrice = 6000,  MaxPrice = 20000, Magic = true },
            new() { Type = "Server.Items.Cloak",        Noun = "cloak",         Kind = GoodsKind.Rare, Weight = 7, MinPrice = 2000,  MaxPrice = 15000, RareHue = true },
            new() { Type = "Server.Items.Robe",         Noun = "robe",          Kind = GoodsKind.Rare, Weight = 7, MinPrice = 2000,  MaxPrice = 15000, RareHue = true },
            new() { Type = "Server.Items.FancyShirt",   Noun = "fancy shirt",   Kind = GoodsKind.Rare, Weight = 5, MinPrice = 1500,  MaxPrice = 10000, RareHue = true },
            new() { Type = "Server.Items.Kilt",         Noun = "kilt",          Kind = GoodsKind.Rare, Weight = 4, MinPrice = 1500,  MaxPrice = 10000, RareHue = true },
            new() { Type = "Server.Items.Doublet",      Noun = "doublet",       Kind = GoodsKind.Rare, Weight = 4, MinPrice = 1500,  MaxPrice = 10000, RareHue = true },

            // ---- Big ticket: the lines that used to be lies ----
            new() { Type = "Server.Multis.Deeds.SmallBrickHouseDeed",     Noun = "small brick deed", Kind = GoodsKind.BigTicket, Weight = 3, MinPrice = 28000,  MaxPrice = 55000 },
            new() { Type = "Server.Multis.Deeds.StonePlasterHouseDeed",   Noun = "small stone deed", Kind = GoodsKind.BigTicket, Weight = 3, MinPrice = 28000,  MaxPrice = 55000 },
            new() { Type = "Server.Multis.Deeds.LogCabinDeed",            Noun = "log cabin deed",   Kind = GoodsKind.BigTicket, Weight = 3, MinPrice = 32000,  MaxPrice = 60000 },
            new() { Type = "Server.Multis.Deeds.ThatchedRoofCottageDeed", Noun = "cottage deed",     Kind = GoodsKind.BigTicket, Weight = 2, MinPrice = 30000,  MaxPrice = 58000 },
            new() { Type = "Server.Multis.Deeds.SmallTowerDeed",          Noun = "small tower deed", Kind = GoodsKind.BigTicket, Weight = 2, MinPrice = 45000,  MaxPrice = 80000 },
            new() { Type = "Server.Multis.Deeds.VillaDeed",               Noun = "villa deed",       Kind = GoodsKind.BigTicket, Weight = 2, MinPrice = 50000,  MaxPrice = 90000 },
            new() { Type = "Server.Multis.Deeds.LargeMarbleDeed",         Noun = "marble deed",      Kind = GoodsKind.BigTicket, Weight = 1, MinPrice = 70000,  MaxPrice = 120000 },
            new() { Type = "Server.Multis.Deeds.TowerDeed",               Noun = "tower deed",       Kind = GoodsKind.BigTicket, Weight = 1, MinPrice = 90000,  MaxPrice = 160000 },
            new() { Type = "Server.Multis.Deeds.KeepDeed",                Noun = "keep deed",        Kind = GoodsKind.BigTicket, Weight = 1, MinPrice = 160000, MaxPrice = 280000 },
        };

        private static int _tableWeight;

        private static Goods RollGoods()
        {
            if (_tableWeight == 0)
            {
                foreach (var g in Table)
                {
                    _tableWeight += g.Weight;
                }
            }

            int r = Utility.Random(_tableWeight);
            foreach (var g in Table)
            {
                r -= g.Weight;
                if (r < 0)
                {
                    return g;
                }
            }
            return Table[0];
        }

        // -----------------------------------------------------------------
        // Stocking.
        // -----------------------------------------------------------------

        // What a bot currently has for sale, or null. Self-healing: an
        // entry whose item has left the pack (sold, looted off the corpse,
        // deleted with the bot) is dropped here rather than lingering as a
        // claim the bot can no longer honour.
        public static ShopStock StockOf(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || !_stock.TryGetValue(bot.Serial, out var stock))
            {
                return null;
            }

            // RootParent, not IsChildOf(Backpack), and the difference
            // matters: while a trade window is open the goods sit in a
            // SecureTradeContainer hanging off the bot, NOT in its pack.
            // Testing the pack made the stock look sold the instant the
            // window opened — the hawker would clear the entry, restock,
            // and start advertising a second item it had conjured while
            // the first one was still on the table in front of you.
            var item = World.FindItem(stock.ItemSerial);
            if (item == null || item.Deleted || item.RootParent != bot)
            {
                _stock.Remove(bot.Serial);
                return null;
            }

            return stock;
        }

        public static bool HasStock(PlayerBot bot) => StockOf(bot) != null;

        public static void Clear(PlayerBot bot)
        {
            if (bot != null)
            {
                _stock.Remove(bot.Serial);
            }
        }

        // Give a bot something real to sell. Returns the existing stock if
        // it already has some, null if the item couldn't be built.
        public static ShopStock Stock(PlayerBot bot)
        {
            if (!Enabled || bot == null || bot.Deleted || !bot.Alive || bot.Backpack == null)
            {
                return null;
            }

            var existing = StockOf(bot);
            if (existing != null)
            {
                return existing;
            }

            var goods = RollGoods();
            var item = Build(goods, out int amount);
            if (item == null)
            {
                return null;
            }

            if (!bot.AddToBackpack(item))
            {
                item.Delete();
                return null;
            }

            // Price follows the lot actually rolled, so 40 mandrake and
            // 200 mandrake are not the same money.
            int price;
            if (goods.MaxAmount > 1)
            {
                double span = Math.Max(1, goods.MaxAmount - goods.MinAmount);
                double t = (amount - goods.MinAmount) / span;
                price = (int)(goods.MinPrice + (goods.MaxPrice - goods.MinPrice) * t);
            }
            else
            {
                price = Utility.RandomMinMax(goods.MinPrice, goods.MaxPrice);
            }

            var temper = Utility.Random(100) switch
            {
                < 25 => HaggleTemper.Firm,
                < 80 => HaggleTemper.Normal,
                _    => HaggleTemper.Eager,
            };

            var stock = new ShopStock
            {
                ItemSerial = item.Serial,
                Noun       = Describe(item, goods, amount),
                Asking     = Math.Max(5, Round(price)),
                Kind       = goods.Kind,
                Temper     = temper,
                StockedAt  = Core.Now,
            };

            // The floor is what the seller will actually take. Firm barely
            // bends; eager wants the pack space back.
            double floorFraction = temper switch
            {
                HaggleTemper.Firm  => Utility.RandomMinMax(86, 94) / 100.0,
                HaggleTemper.Eager => Utility.RandomMinMax(55, 68) / 100.0,
                _                  => Utility.RandomMinMax(70, 84) / 100.0,
            };
            stock.Floor = Math.Clamp(Round((int)(stock.Asking * floorFraction)), 1, stock.Asking);

            _stock[bot.Serial] = stock;
            return stock;
        }

        // Prices people actually said out loud. Nobody advertised 4,317gp.
        private static int Round(int price)
        {
            if (price >= 20000)
            {
                return price / 5000 * 5000;
            }
            if (price >= 5000)
            {
                return price / 1000 * 1000;
            }
            if (price >= 1000)
            {
                return price / 100 * 100;
            }
            if (price >= 100)
            {
                return price / 25 * 25;
            }
            return Math.Max(1, price / 5 * 5);
        }

        private static Item Build(Goods goods, out int amount)
        {
            amount = 1;

            var item = BotItemFactory.Create(goods.Type);
            if (item == null)
            {
                return null;
            }

            if (goods.MaxAmount > 1 && item.Stackable)
            {
                amount = Utility.RandomMinMax(goods.MinAmount, goods.MaxAmount);
                item.Amount = amount;
            }

            if (goods.RareHue)
            {
                item.Hue = RareHue();
            }

            // Exceptional is the craftsman's mark — the thing "GM smith"
            // in the old chat corpus was bragging about.
            if (goods.Exceptional)
            {
                switch (item)
                {
                    case BaseWeapon w: w.Quality = WeaponQuality.Exceptional; break;
                    case BaseArmor a:  a.Quality = ArmorQuality.Exceptional; break;
                }
            }

            // T2A magic properties. Ruin/Might/Force/Power/Vanq on the
            // damage line is the ladder everyone knew by name, and a rare
            // is supposed to BE one, so the roll sits at the top of it.
            if (goods.Magic)
            {
                switch (item)
                {
                    case BaseWeapon w:
                    {
                        w.DamageLevel = Utility.Random(100) switch
                        {
                            < 30 => WeaponDamageLevel.Force,
                            < 70 => WeaponDamageLevel.Power,
                            _    => WeaponDamageLevel.Vanq,
                        };
                        if (Utility.RandomDouble() < 0.6)
                        {
                            w.AccuracyLevel = (WeaponAccuracyLevel)Utility.RandomMinMax(1, 5);
                        }
                        if (Utility.RandomDouble() < 0.4)
                        {
                            w.DurabilityLevel = (WeaponDurabilityLevel)Utility.RandomMinMax(1, 4);
                        }
                        break;
                    }
                    case BaseArmor a:
                    {
                        a.ProtectionLevel = Utility.Random(100) switch
                        {
                            < 40 => ArmorProtectionLevel.Hardening,
                            < 80 => ArmorProtectionLevel.Fortification,
                            _    => ArmorProtectionLevel.Invulnerability,
                        };
                        if (Utility.RandomDouble() < 0.4)
                        {
                            a.Durability = (ArmorDurabilityLevel)Utility.RandomMinMax(1, 4);
                        }
                        break;
                    }
                }
            }

            return item;
        }

        // The 1998 dye tub, plus the black everyone wanted. Same era hue
        // rules the rest of the kit follows.
        private static int RareHue() =>
            Utility.RandomDouble() < 0.08 ? 0x0001 : Utility.RandomMinMax(2, 1001);

        // -----------------------------------------------------------------
        // Naming — what the bot calls the thing it is holding. The line it
        // shouts has to survive being read next to the item in the trade
        // window, so this describes the ACTUAL rolled properties.
        // -----------------------------------------------------------------
        private static string Describe(Item item, Goods goods, int amount)
        {
            // Longest this gets is "250 sulfurous ash" or "invulnerability
            // ringmail tunic" — a 64-char stack buffer covers every row in
            // the table with room to spare.
            using var sb = new ValueStringBuilder(stackalloc char[64]);

            if (amount > 1)
            {
                sb.Append($"{amount} ");
            }

            switch (item)
            {
                case BaseWeapon w:
                {
                    if (w.DamageLevel != WeaponDamageLevel.Regular)
                    {
                        sb.Append($"{DamageWord(w.DamageLevel)} ");
                    }
                    else if (w.Quality == WeaponQuality.Exceptional)
                    {
                        sb.Append(Utility.RandomBool() ? "GM " : "exceptional ");
                    }
                    break;
                }
                case BaseArmor a:
                {
                    if (a.ProtectionLevel != ArmorProtectionLevel.Regular)
                    {
                        sb.Append($"{a.ProtectionLevel:L} ");
                    }
                    else if (a.Quality == ArmorQuality.Exceptional)
                    {
                        sb.Append(Utility.RandomBool() ? "GM " : "exceptional ");
                    }
                    break;
                }
                default:
                {
                    if (goods.RareHue)
                    {
                        sb.Append(item.Hue == 0x0001 ? "black " : "rare hue ");
                    }
                    break;
                }
            }

            sb.Append(goods.Noun);
            return sb.ToString();
        }

        // "vanq", not "Vanq" — this goes into a line a player typed.
        private static string DamageWord(WeaponDamageLevel level) => level switch
        {
            WeaponDamageLevel.Ruin  => "ruin",
            WeaponDamageLevel.Might => "might",
            WeaponDamageLevel.Force => "force",
            WeaponDamageLevel.Power => "power",
            WeaponDamageLevel.Vanq  => "vanq",
            _                       => "",
        };

        // -----------------------------------------------------------------
        // The shout. Templates live in wts_offer.txt so the corpus stays
        // where every other line lives; {item} and {price} come from the
        // real stock.
        // -----------------------------------------------------------------
        public static string WtsLine(ShopStock stock)
        {
            if (stock == null)
            {
                return null;
            }

            var template = ChatLibrary.PickRandom("wts_offer") ?? "WTS {item} {price}";
            return template
                .Replace("{item}", stock.Noun, StringComparison.Ordinal)
                .Replace("{price}", Coin(stock.Asking), StringComparison.Ordinal);
        }

        // How a player wrote a number in 1999: 5k, 12k, 850.
        public static string Coin(int gold)
        {
            if (gold >= 1000 && gold % 1000 == 0)
            {
                return $"{gold / 1000}k";
            }
            return gold.ToString();
        }

        // -----------------------------------------------------------------
        // Haggling.
        //
        // One ladder per buyer. An offer at or above the asking price is
        // taken on the spot; at or above the floor it is usually taken;
        // below the floor the seller counters, closing a share of the gap
        // each round and then planting its feet. An insult gets refused
        // outright.
        // -----------------------------------------------------------------
        public enum HaggleResult
        {
            Accepted,  // price agreed, the deal is on
            Countered, // seller named a different number
            Refused,   // not at that price, and it isn't moving
            Insulted,  // not worth continuing the conversation
        }

        public static HaggleSession SessionFor(ShopStock stock, Serial buyer)
        {
            if (!stock.Sessions.TryGetValue(buyer, out var s) ||
                Core.Now - s.LastAt > SessionIdle)
            {
                s = new HaggleSession();
                stock.Sessions[buyer] = s;
            }
            s.LastAt = Core.Now;
            return s;
        }

        // Has this buyer actually engaged with this seller? Vague words
        // like "ok" and "nah" only count as shop talk once a conversation
        // is under way — otherwise standing near a hawker would turn every
        // "sure" in the room into a purchase agreement.
        public static bool HasSession(ShopStock stock, Serial buyer) =>
            stock != null && stock.Sessions.TryGetValue(buyer, out var s) &&
            Core.Now - s.LastAt <= SessionIdle;

        // The price this buyer has already shaken on, or 0.
        public static int AgreedPriceFor(ShopStock stock, Serial buyer)
        {
            if (stock == null || !stock.Sessions.TryGetValue(buyer, out var s))
            {
                return 0;
            }
            return s.AgreedPrice > 0 && Core.Now < s.AgreedUntil ? s.AgreedPrice : 0;
        }

        // Lock in a price without a round of haggling — a buyer who just
        // says "ill take it" at the asking price.
        public static void Agree(ShopStock stock, Serial buyer, int price)
        {
            var s = SessionFor(stock, buyer);
            s.AgreedPrice = price;
            s.AgreedUntil = Core.Now + AgreementWindow;
        }

        // Consider an offer. `counter` comes back as the number the seller
        // names — its own asking price when it hasn't moved.
        public static HaggleResult Consider(ShopStock stock, Serial buyer, int offer, out int counter)
        {
            counter = stock.Asking;

            int already = AgreedPriceFor(stock, buyer);
            if (already > 0)
            {
                counter = already;
                return HaggleResult.Accepted; // already shook on it
            }

            var s = SessionFor(stock, buyer);
            s.Rounds++;

            // An offer under half the floor is not a negotiation.
            if (offer > 0 && offer < stock.Floor / 2)
            {
                return HaggleResult.Insulted;
            }

            if (offer >= stock.Asking)
            {
                s.AgreedPrice = stock.Asking;
                s.AgreedUntil = Core.Now + AgreementWindow;
                counter = s.AgreedPrice;
                return HaggleResult.Accepted;
            }

            if (offer >= stock.Floor)
            {
                // At or above the floor a Firm seller still tries one more
                // squeeze; the others take the money.
                bool squeeze = stock.Temper == HaggleTemper.Firm &&
                               s.Rounds == 1 && Utility.RandomDouble() < 0.6;
                if (!squeeze)
                {
                    s.AgreedPrice = offer;
                    s.AgreedUntil = Core.Now + AgreementWindow;
                    counter = offer;
                    return HaggleResult.Accepted;
                }
            }

            // Below the floor: close a share of the gap, then stop.
            double give = stock.Temper switch
            {
                HaggleTemper.Firm  => s.Rounds switch { 1 => 0.15, 2 => 0.35, _ => 1.0 },
                HaggleTemper.Eager => s.Rounds switch { 1 => 0.55, 2 => 0.85, _ => 1.0 },
                _                  => s.Rounds switch { 1 => 0.35, 2 => 0.70, _ => 1.0 },
            };
            counter = Math.Max(Round((int)(stock.Asking - (stock.Asking - stock.Floor) * give)),
                               stock.Floor);

            // Planted. If the buyer still won't meet the floor, that's that.
            if (s.Rounds >= 4)
            {
                counter = stock.Floor;
                return HaggleResult.Refused;
            }

            return HaggleResult.Countered;
        }

        // -----------------------------------------------------------------
        // The sale. Hands back the stock item and forgets the entry — the
        // ONLY place stock leaves a bot's hands.
        // -----------------------------------------------------------------
        public static Item TakeStockItem(PlayerBot seller)
        {
            var stock = StockOf(seller);
            if (stock == null)
            {
                return null;
            }
            var item = World.FindItem(stock.ItemSerial);
            _stock.Remove(seller.Serial);
            return item;
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            string arg = e.Arguments.Length > 0 ? e.Arguments[0].ToLowerInvariant() : "";

            if (arg == "stock")
            {
                int n = 0;
                foreach (var m in World.Mobiles.Values)
                {
                    if (m is PlayerBot b && b.Behavior is BankSitterBehavior bs &&
                        bs.Role == BankSitterBehavior.BankRole.Hawker && Stock(b) != null)
                    {
                        n++;
                    }
                }
                from?.SendMessage($"Stocked {n} hawker(s).");
                return;
            }

            if (arg == "list")
            {
                int shown = 0;
                foreach (var kv in _stock)
                {
                    if (World.FindEntity<Mobile>(kv.Key) is not PlayerBot b)
                    {
                        continue;
                    }
                    from?.SendMessage(
                        $"{b.Name}: {kv.Value.Noun} - asking {kv.Value.Asking}, " +
                        $"floor {kv.Value.Floor} ({kv.Value.Temper})");
                    if (++shown >= 25)
                    {
                        break;
                    }
                }
                from?.SendMessage($"{_stock.Count} hawker(s) holding stock.");
                return;
            }

            from?.SendMessage($"BotShop: {_stock.Count} bot(s) holding stock. ([BotShop stock|list)");
        }
    }
}
