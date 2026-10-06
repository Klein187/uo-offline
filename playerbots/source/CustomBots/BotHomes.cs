// =========================================================================
// BotHomes.cs — bots that own houses and live in them.
//
// Only regulars (BotRegulars) own houses: a house needs an owner who comes
// back. Three ways a bot ends up with one:
//
//   1. It saves up. A regular with the price of a deed (from the Architect's
//      own buy list) in its pack or bank goes to the nearest Architect, buys
//      a real deed, picks a spot in the countryside, walks there and places
//      it through HouseDeed.OnPlacement: the same checks, keys and owner
//      setup a player gets.
//   2. Seeded. A world that has been set up gets SeedTarget furnished homes,
//      each with a new regular who owns it. First Time Setup seeds them, and
//      a world set up before this existed gets them on its next boot.
//   3. Adopted. The ownerless roadside houses [BotHouses scatter laid down
//      get an owner, a key and furniture.
//
// An owner uses the house: it goes home now and then (more often with a
// heavy pack or near the end of its session), lets itself in with its key,
// puts spare loot in its chest, stays a while, and often logs out inside.
// A regular logs back in where it logged out, so those come back at home.
//
// The house is Ageless (RestrictDecay): bots have no account to refresh it.
//
//   [BotHomes               — counts
//   homes_request.txt       — headless test, "token verb [n]":
//       seed N    place N seeded homes now
//       rich N    give N homeless online regulars the deed money and send
//                 them to buy
//       visit N   send N online homeowners home now
//       status    counts only
//     Ack: homes_ack.json.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using Server.Commands;
using Server.Items;
using Server.Mobiles;
using Server.Multis;
using Server.Multis.Deeds;

namespace Server.CustomBots
{
    public static class BotHomes
    {
        // ---- Knobs ----

        public static bool Enabled = true;

        // Furnished homes a set-up world starts with.
        public static int SeedTarget = 30;

        // Walking money a bot keeps after buying its deed.
        private const int Reserve = 1000;

        // Fallback if no Architect is around to quote a price.
        private const int FallbackDeedPrice = 43800;

        // Chance per minute that an idle homeowner heads home.
        private const double VisitChancePerTick = 0.04;

        // A pack this full (share of max weight) sends its owner home.
        private const double HeavyPack = 0.55;

        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ErrandTimeout = TimeSpan.FromMinutes(90);

        // ---- State ----

        private static readonly Dictionary<PlayerBot, BaseHouse> _homeOf = new();
        private static readonly Dictionary<BaseHouse, string> _destOf = new();
        private static readonly List<BotDestination> _synthetic = new();
        private static readonly Dictionary<PlayerBot, Errand> _errands = new();
        private static readonly List<(BotDestination dest, Architect npc)> _architects = new();
        private static bool _booted;

        private enum Stage { ToArchitect, ToPlot }

        private sealed class Errand
        {
            public Stage Stage;
            public string Dest;
            public int MultiId;
            public Point3D Site;
            public int Tries;
            public DateTime Started = Core.Now;
        }

        public static int HomeCount => _homeOf.Count;

        public static void Initialize()
        {
            CommandSystem.Register("BotHomes", AccessLevel.GameMaster, e =>
                e.Mobile.SendMessage($"Bot homes: {_homeOf.Count} owned, {_errands.Count} buying now, " +
                    $"{_architects.Count} architects known."));

            _lastToken = ReadToken(out _, out _) ?? 0;

            // After the catalog, the waypoint graph and the spawners are up.
            Timer.DelayCall(TimeSpan.FromSeconds(25), Boot);
            Timer.DelayCall(TickInterval, TickInterval, Tick);
            Timer.DelayCall(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), PollRequest);
        }

        private static void Boot()
        {
            if (!Enabled)
            {
                return;
            }
            RegisterArchitects();
            RegisterOwnedHomes();
            int adopted = AdoptOwnerless();
            int seeded = EnsureSeeded();
            if (WorldIsSetUp())
            {
                BotVendorHouses.EnsureAll();
            }
            BotHouseShopping.RegisterShops();
            _booted = true;
            Console.WriteLine($"[homes] {_homeOf.Count} bot homes ({adopted} adopted, {seeded} seeded now), " +
                $"{_architects.Count} architects");
        }

        // Called by DestinationCatalog after a reload wipes synthetic entries.
        public static void OnCatalogReloaded()
        {
            DestinationCatalog.RegisterSynthetic(_synthetic);
        }

        // First Time Setup: the world was just spawned.
        public static int SeedNow()
        {
            if (!_booted)
            {
                RegisterArchitects();
                RegisterOwnedHomes();
                _booted = true;
            }
            AdoptOwnerless();
            int seeded = EnsureSeeded();
            BotVendorHouses.EnsureAll();
            BotHouseShopping.RegisterShops();
            return seeded;
        }

        // -------------------------------------------------------------------
        // Destinations
        // -------------------------------------------------------------------

        internal static BotDestination AddDestination(string name, DestinationType type, Point3D near)
        {
            var existing = DestinationCatalog.GetByName(name);
            if (existing != null)
            {
                return existing;
            }
            var node = WaypointRegistry.Graph?.FindNearestNode(near);
            if (node == null)
            {
                return null;
            }
            var d = new BotDestination
            {
                Name            = name,
                Location        = node.Location,
                Type            = type,
                City            = "",
                NearestWaypoint = node.Name,
            };
            _synthetic.Add(d);
            DestinationCatalog.RegisterSynthetic(new[] { d });
            return d;
        }

        private static void RegisterArchitects()
        {
            _architects.Clear();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not Architect a || a.Deleted || a.Map != Map.Felucca)
                {
                    continue;
                }
                string town = NearestTownName(a.Location) ?? $"{a.X},{a.Y}";
                var d = AddDestination($"{town} Architect", DestinationType.Architect, a.Location);
                if (d != null && Distance(d.Location, a.Location) <= 30)
                {
                    _architects.Add((d, a));
                }
            }
        }

        private static string NearestTownName(Point3D p)
        {
            string best = null;
            int bestD = int.MaxValue;
            foreach (var d in DestinationCatalog.All)
            {
                if (string.IsNullOrEmpty(d.City))
                {
                    continue;
                }
                int dist = Distance(d.Location, p);
                if (dist < bestD)
                {
                    bestD = dist;
                    best = d.City;
                }
            }
            return bestD < 200 ? best : null;
        }

        private static int Distance(Point3D a, Point3D b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private static void RegisterHome(BaseHouse house, PlayerBot owner)
        {
            _homeOf[owner] = house;
            var d = AddDestination($"Home of {owner.Name}", DestinationType.Home, house.BanLocation);
            if (d != null)
            {
                _destOf[house] = d.Name;
            }
        }

        private static void RegisterOwnedHomes()
        {
            foreach (var h in BaseHouse.AllHouses)
            {
                if (h != null && !h.Deleted && h.Owner is PlayerBot pb && !pb.Deleted && pb.Regular)
                {
                    h.RestrictDecay = true;
                    RegisterHome(h, pb);
                }
            }
        }

        // The road node a house's destination sits on.
        public static Point3D? RoadOf(BaseHouse house) =>
            house != null && _destOf.TryGetValue(house, out var name) &&
            DestinationCatalog.GetByName(name) is BotDestination d ? d.Location : null;

        public static BaseHouse HomeOf(PlayerBot bot) =>
            bot != null && _homeOf.TryGetValue(bot, out var h) && h != null && !h.Deleted ? h : null;

        // Called from PlayerBot.OnAfterDelete. The house stays (Ageless).
        public static void Forget(PlayerBot bot)
        {
            _homeOf.Remove(bot);
            _errands.Remove(bot);
        }

        // -------------------------------------------------------------------
        // The minute tick: who starts saving up for a deed, who goes home.
        // -------------------------------------------------------------------
        private static void Tick()
        {
            if (!Enabled || !_booted)
            {
                return;
            }

            var bots = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot b && b.Regular && !b.Deleted && b.Map != Map.Internal && !b.LoggingOut)
                {
                    bots.Add(b);
                }
            }

            foreach (var bot in bots)
            {
                if (_errands.TryGetValue(bot, out var errand))
                {
                    if (Core.Now - errand.Started > ErrandTimeout)
                    {
                        Console.WriteLine($"[homes] {bot.Name} gave up on the house errand ({errand.Stage})");
                        _errands.Remove(bot);
                    }
                    else if (!OnTrack(bot, errand) && IsFree(bot))
                    {
                        // Something else took the bot off its errand (a
                        // finished Recall hands out a fresh trip, a gatherer
                        // goes for its pack beast). Put it back on.
                        Travel(bot, errand.Dest);
                    }
                    continue;
                }

                if (!IsFree(bot))
                {
                    continue;
                }

                var home = HomeOf(bot);
                if (home == null)
                {
                    // Holding a deed it could not place yet: look for a spot
                    // again before anything else.
                    if (DeedIn(bot) is HouseDeed held)
                    {
                        StartPlacing(bot, held);
                    }
                    else if (_architects.Count > 0 && CanAfford(bot))
                    {
                        StartBuying(bot);
                    }
                    continue;
                }

                if (AtHome(bot, home) || HeadingHome(bot, home))
                {
                    continue;
                }

                bool heavy = bot.Backpack != null && bot.MaxWeight > 0 &&
                    bot.TotalWeight >= bot.MaxWeight * HeavyPack;
                bool late = bot.SessionEndsAt != DateTime.MinValue &&
                    bot.SessionEndsAt - Core.Now < TimeSpan.FromMinutes(25);
                if (heavy || late || Utility.RandomDouble() < VisitChancePerTick)
                {
                    SendHome(bot);
                }
            }
        }

        // Free to drop what it is doing: a plain Traveler, alive, not in a
        // fight or a group.
        private static bool IsFree(PlayerBot bot) =>
            bot.Alive && bot.Combatant == null && !bot.CorpseRunPending &&
            bot.Behavior is TravelerBehavior t && !t.Subordinate && !t.MagicTravelPending &&
            !BotPartyManager.IsInParty(bot) && !BotPlayerParty.InPlayerParty(bot) &&
            !bot.IsPermanent;

        private static bool AtHome(PlayerBot bot, BaseHouse home) =>
            bot.Map == home.Map && (home.IsInside(bot) || Distance(bot.Location, home.Location) < 12);

        private static bool OnTrack(PlayerBot bot, Errand errand) =>
            bot.Behavior is ErrandWalkBehavior or HomeVisitBehavior ||
            bot.Behavior is TravelerBehavior t && t.DestinationName == errand.Dest;

        // The lifecycle manager leaves these bots alone.
        public static bool IsBusy(PlayerBot bot) =>
            _errands.ContainsKey(bot) || bot.Behavior is ErrandWalkBehavior or HomeVisitBehavior;

        private static bool HeadingHome(PlayerBot bot, BaseHouse home) =>
            bot.Behavior is TravelerBehavior t && _destOf.TryGetValue(home, out var d) &&
            t.DestinationName == d;

        private static void Travel(PlayerBot bot, string dest)
        {
            bot.Behavior = new TravelerBehavior { DestinationName = dest };
        }

        public static void SendHome(PlayerBot bot)
        {
            var home = HomeOf(bot);
            if (home == null || !_destOf.TryGetValue(home, out var dest))
            {
                return;
            }
            Console.WriteLine($"[homes] {bot.Name} heads home");
            Travel(bot, dest);
        }

        // -------------------------------------------------------------------
        // Buying
        // -------------------------------------------------------------------

        private static int DeedPrice(Architect a)
        {
            try
            {
                foreach (var info in a.GetBuyInfo())
                {
                    if (info is GenericBuyInfo g && typeof(HouseDeed).IsAssignableFrom(g.Type))
                    {
                        return g.Price;
                    }
                }
            }
            catch
            {
                // fall through
            }
            return FallbackDeedPrice;
        }

        private static int CheapestDeed()
        {
            int best = int.MaxValue;
            foreach (var (_, a) in _architects)
            {
                if (a != null && !a.Deleted)
                {
                    best = Math.Min(best, DeedPrice(a));
                }
            }
            return best == int.MaxValue ? FallbackDeedPrice : best;
        }

        private static int PackGold(PlayerBot bot) => bot.Backpack?.GetAmount(typeof(Gold)) ?? 0;

        private static bool CanAfford(PlayerBot bot)
        {
            int need = CheapestDeed() + Reserve;
            return PackGold(bot) >= need || Banker.GetBalance(bot) >= need;
        }

        private static void StartBuying(PlayerBot bot)
        {
            BotDestination best = null;
            int bestD = int.MaxValue;
            foreach (var (d, a) in _architects)
            {
                if (a == null || a.Deleted)
                {
                    continue;
                }
                int dist = Distance(d.Location, bot.Location);
                if (dist < bestD)
                {
                    bestD = dist;
                    best = d;
                }
            }
            if (best == null)
            {
                return;
            }

            _errands[bot] = new Errand { Stage = Stage.ToArchitect, Dest = best.Name };
            Console.WriteLine($"[homes] {bot.Name} has the money for a house, off to the {best.Name}");
            SayFrom(bot, "house_saving");
            Travel(bot, best.Name);
        }

        // -------------------------------------------------------------------
        // Arrival handoff from TravelerBehavior (Home / Architect types).
        // True = BotHomes took the bot over.
        // -------------------------------------------------------------------
        public static bool OnArrived(PlayerBot bot, string destName)
        {
            if (_errands.TryGetValue(bot, out var errand) && errand.Dest == destName)
            {
                if (errand.Stage == Stage.ToArchitect)
                {
                    return ReachArchitect(bot, errand);
                }
                return ReachPlot(bot, errand);
            }

            var home = HomeOf(bot);
            if (home != null && _destOf.TryGetValue(home, out var mine) && mine == destName)
            {
                bot.Behavior = new HomeVisitBehavior(home);
                return true;
            }
            return false;
        }

        private static bool ReachArchitect(PlayerBot bot, Errand errand)
        {
            Architect npc = null;
            foreach (var (d, a) in _architects)
            {
                if (d.Name == errand.Dest)
                {
                    npc = a;
                }
            }
            if (npc == null || npc.Deleted)
            {
                _errands.Remove(bot);
                return false;
            }

            bot.Behavior = new ErrandWalkBehavior(npc.Location, 2, "buying a house deed", b => BuyDeed(b, npc),
                b => { _errands.Remove(b); Travel(b, null); });
            return true;
        }

        private static void BuyDeed(PlayerBot bot, Architect npc)
        {
            if (!_errands.TryGetValue(bot, out var errand))
            {
                Travel(bot, null);
                return;
            }

            int price = DeedPrice(npc);
            bool paid = false;
            if (PackGold(bot) >= price)
            {
                paid = bot.Backpack.ConsumeTotal(typeof(Gold), price);
            }
            else if (price >= 2000 && Banker.GetBalance(bot) >= price)
            {
                // What the vendor does for any player: a big purchase is
                // drawn from the bank.
                paid = Banker.Withdraw(bot, price);
            }
            if (!paid)
            {
                Console.WriteLine($"[homes] {bot.Name} came up short at the architect");
                _errands.Remove(bot);
                Travel(bot, null);
                return;
            }

            int multiId = BotHousing.HouseMultiIds[Utility.Random(BotHousing.HouseMultiIds.Length)];
            var deed = BotHousing.DeedFor(multiId);
            bot.AddToBackpack(deed);
            bot.Direction = bot.GetDirectionTo(npc);
            Console.WriteLine($"[homes] {bot.Name} bought {deed.GetType().Name} for {price} gold " +
                $"(bank now {Banker.GetBalance(bot)})");

            errand.MultiId = multiId;
            if (!PickPlot(bot, errand))
            {
                Console.WriteLine($"[homes] {bot.Name} found nowhere to build; keeping the deed for later");
                _errands.Remove(bot);
                Travel(bot, null);
                return;
            }
            Travel(bot, errand.Dest);
        }

        private static HouseDeed DeedIn(PlayerBot bot)
        {
            if (bot.Backpack == null)
            {
                return null;
            }
            foreach (var item in bot.Backpack.Items)
            {
                if (item is HouseDeed hd && !hd.Deleted)
                {
                    return hd;
                }
            }
            return null;
        }

        private static void StartPlacing(PlayerBot bot, HouseDeed deed)
        {
            var errand = new Errand { MultiId = deed.MultiID };
            if (!PickPlot(bot, errand))
            {
                return;
            }
            _errands[bot] = errand;
            Console.WriteLine($"[homes] {bot.Name} has a deed and a spot picked out, heading there");
            Travel(bot, errand.Dest);
        }

        // A spot not too far from where the bot spends its time.
        private static bool PickPlot(PlayerBot bot, Errand errand)
        {
            var probe = new Rat { Controlled = true, Blessed = true, Hidden = true };
            try
            {
                if (!BotHousing.TryFindRuralSite(bot.Map, probe, errand.MultiId, bot.Location, 500,
                        out var center, out var node))
                {
                    return false;
                }
                errand.Stage = Stage.ToPlot;
                errand.Site = center;
                errand.Tries++;
                var d = AddDestination($"Plot of {bot.Name} {errand.Tries}", DestinationType.Home, node.Location);
                if (d == null)
                {
                    return false;
                }
                errand.Dest = d.Name;
                return true;
            }
            finally
            {
                probe.Delete();
            }
        }

        private static bool ReachPlot(PlayerBot bot, Errand errand)
        {
            bot.Behavior = new ErrandWalkBehavior(errand.Site, 6, "looking over a building spot",
                b => PlaceHouse(b), b => PlaceHouse(b));
            return true;
        }

        private static void PlaceHouse(PlayerBot bot)
        {
            if (!_errands.TryGetValue(bot, out var errand))
            {
                Travel(bot, null);
                return;
            }

            HouseDeed deed = null;
            foreach (var item in bot.Backpack.Items)
            {
                if (item is HouseDeed hd)
                {
                    deed = hd;
                    break;
                }
            }
            if (deed == null)
            {
                _errands.Remove(bot);
                Travel(bot, null);
                return;
            }

            var p = new Point3D(errand.Site.X + deed.Offset.X, errand.Site.Y + deed.Offset.Y,
                errand.Site.Z + deed.Offset.Z);
            deed.OnPlacement(bot, p);

            var house = FindOwnedHouse(bot);
            if (house == null)
            {
                // Somebody built there first, or the ground was no good after
                // all. Try another spot, twice more.
                if (errand.Tries < 3 && PickPlot(bot, errand))
                {
                    Console.WriteLine($"[homes] {bot.Name} could not build there, trying another spot");
                    Travel(bot, errand.Dest);
                }
                else
                {
                    Console.WriteLine($"[homes] {bot.Name} could not place the house; keeping the deed");
                    _errands.Remove(bot);
                    Travel(bot, null);
                }
                return;
            }

            _errands.Remove(bot);
            house.RestrictDecay = true;
            RegisterHome(house, bot);
            BotEventJournal.Record("house", bot);
            SayFrom(bot, "house_placed");
            Console.WriteLine($"[homes] {bot.Name} placed a house at {house.Location}");
            bot.Behavior = new HomeVisitBehavior(house);
        }

        private static BaseHouse FindOwnedHouse(PlayerBot bot)
        {
            foreach (var h in BaseHouse.AllHouses)
            {
                if (h != null && !h.Deleted && h.Owner == bot)
                {
                    return h;
                }
            }
            return null;
        }

        // -------------------------------------------------------------------
        // Seeded and adopted homes
        // -------------------------------------------------------------------

        private static int EnsureSeeded()
        {
            // A world that has not had First Time Setup has no spawners yet;
            // setup calls SeedNow when it is done.
            if (!WorldIsSetUp())
            {
                return 0;
            }
            int want = SeedTarget - _homeOf.Count;
            return want > 0 ? Seed(want) : 0;
        }

        private static bool WorldIsSetUp()
        {
            foreach (var item in World.Items.Values)
            {
                if (item is PlayerBotSpawner)
                {
                    return true;
                }
            }
            return false;
        }

        public static int Seed(int count)
        {
            var map = Map.Felucca;
            var probe = new Rat { Controlled = true, Blessed = true, Hidden = true };
            int placed = 0;
            try
            {
                for (int i = 0; i < count * 3 && placed < count; i++)
                {
                    int multiId = BotHousing.HouseMultiIds[Utility.Random(BotHousing.HouseMultiIds.Length)];
                    if (!BotHousing.TryFindRuralSite(map, probe, multiId, null, 0, out var center, out _, 600))
                    {
                        continue;
                    }
                    var owner = NewOwner();
                    var house = PlaceWithDeed(owner, multiId, center, map);
                    if (house == null)
                    {
                        owner.Delete();
                        continue;
                    }
                    SettleIn(house, owner, seeded: true);
                    placed++;
                }
            }
            finally
            {
                probe.Delete();
            }
            if (placed > 0)
            {
                Console.WriteLine($"[homes] seeded {placed} furnished home(s)");
            }
            return placed;
        }

        // A new owner places a house through a real deed: the same checks,
        // keys and owner setup a player gets. Null if the spot was refused.
        internal static BaseHouse PlaceWithDeed(PlayerBot owner, int multiId, Point3D center, Map map)
        {
            owner.MoveToWorld(center, map);
            var deed = BotHousing.DeedFor(multiId);
            owner.AddToBackpack(deed);
            deed.OnPlacement(owner, new Point3D(center.X + deed.Offset.X, center.Y + deed.Offset.Y,
                center.Z + deed.Offset.Z));
            var house = FindOwnedHouse(owner);
            if (house == null && !deed.Deleted)
            {
                deed.Delete();
            }
            return house;
        }

        // The roadside houses from [BotHouses scatter have nobody living in
        // them. Give each one an owner.
        private static int AdoptOwnerless()
        {
            var list = new List<BaseHouse>();
            foreach (var h in BaseHouse.AllHouses)
            {
                if (h != null && !h.Deleted && h.Owner == null && h.RestrictDecay && h.Map == Map.Felucca &&
                    h is SmallOldHouse or LogCabin)
                {
                    list.Add(h);
                }
            }
            foreach (var h in list)
            {
                var owner = NewOwner();
                h.Owner = owner;
                if (h.Doors.Count > 0 && h.Doors[0] is BaseDoor d0)
                {
                    uint value = d0.KeyValue;
                    if (value == 0)
                    {
                        value = Key.RandomValue();
                        foreach (var door in h.Doors)
                        {
                            if (door is BaseDoor bd)
                            {
                                bd.KeyValue = value;
                            }
                        }
                    }
                    owner.AddToBackpack(new Key(KeyType.Gold) { KeyValue = value, LootType = LootType.Newbied });
                    owner.BankBox?.DropItem(new Key(KeyType.Gold) { KeyValue = value, LootType = LootType.Newbied });
                }
                if (h.Sign != null)
                {
                    h.Sign.Name = $"{owner.Name}'s house";
                }
                SettleIn(h, owner, seeded: true);
            }
            if (list.Count > 0)
            {
                Console.WriteLine($"[homes] adopted {list.Count} ownerless roadside house(s)");
            }
            return list.Count;
        }

        // A settled player: mid to high skill, any class but the outlaw ones.
        internal static PlayerBot NewOwner(BotClass[] only = null)
        {
            BotClass[] classes = only ?? new[]
            {
                BotClass.Warrior, BotClass.Mage, BotClass.Fencer, BotClass.Archer, BotClass.Tamer,
                BotClass.Healer, BotClass.Bard, BotClass.Ranger, BotClass.Merchant, BotClass.Smith,
                BotClass.Tailor, BotClass.Fisherman, BotClass.Lumberjack, BotClass.Miner,
            };
            BotSkillTier[] tiers =
            {
                BotSkillTier.Journeyman, BotSkillTier.Adept, BotSkillTier.Expert, BotSkillTier.Master,
                BotSkillTier.Grandmaster,
            };
            var bot = new PlayerBot(classes[Utility.Random(classes.Length)], tiers[Utility.Random(tiers.Length)]);
            return bot;
        }

        internal static void SettleIn(BaseHouse house, PlayerBot owner, bool seeded)
        {
            house.RestrictDecay = true;
            var inside = HomeVisitBehavior.InteriorTiles(house);
            Furnish(house, owner, inside);

            // Some savings, as an established player would have.
            if (seeded)
            {
                Banker.Deposit(owner, Utility.RandomMinMax(2000, 15000));
            }

            BotRegulars.AdoptNew(owner, inside.Count > 0 ? inside[Utility.Random(inside.Count)] : house.BanLocation,
                house.Map);
            RegisterHome(house, owner);
        }

        // -------------------------------------------------------------------
        // Furniture: a table and chair, a chest and a bookcase, locked down,
        // with the owner's trade goods in the chest.
        // -------------------------------------------------------------------
        private static void Furnish(BaseHouse house, PlayerBot owner, List<Point3D> tiles)
        {
            if (tiles.Count < 4)
            {
                return;
            }
            var free = new List<Point3D>(tiles);

            Item Place(Item item)
            {
                if (free.Count == 0)
                {
                    item.Delete();
                    return null;
                }
                int i = Utility.Random(free.Count);
                var p = free[i];
                free.RemoveAt(i);
                item.MoveToWorld(p, house.Map);
                if (!house.LockDown(owner, item, false))
                {
                    item.Movable = false;
                }
                return item;
            }

            Place(new WritingTable());
            Place(new WoodenChair());
            var chest = new WoodenChest();
            FillChest(chest, owner);
            Place(chest);
            Place(Utility.RandomBool() ? new FullBookcase() : new Armoire());
            if (free.Count > 2)
            {
                Place(new Candelabra());
            }
        }

        private static void Add(Container c, string typeName, int amount)
        {
            var type = AssemblyHandler.FindTypeByName(typeName);
            if (type == null)
            {
                return;
            }
            try
            {
                if (Activator.CreateInstance(type) is not Item item)
                {
                    return;
                }
                if (item.Stackable)
                {
                    item.Amount = Math.Max(1, amount);
                }
                c.DropItem(item);
            }
            catch
            {
                // a type that will not build bare is simply left out
            }
        }

        private static void FillChest(Container chest, PlayerBot owner)
        {
            Add(chest, "Gold", Utility.RandomMinMax(100, 900));
            switch (owner.Class)
            {
                case BotClass.Smith:
                case BotClass.Miner:
                    Add(chest, "IronIngot", Utility.RandomMinMax(60, 240));
                    Add(chest, "IronOre", Utility.RandomMinMax(20, 80));
                    break;
                case BotClass.Tailor:
                    Add(chest, "Cloth", Utility.RandomMinMax(40, 160));
                    Add(chest, "Leather", Utility.RandomMinMax(20, 100));
                    break;
                case BotClass.Lumberjack:
                    Add(chest, "Log", Utility.RandomMinMax(80, 300));
                    break;
                case BotClass.Fisherman:
                    Add(chest, "FishSteak", Utility.RandomMinMax(10, 40));
                    break;
                case BotClass.Mage:
                    foreach (var r in new[] { "BlackPearl", "Bloodmoss", "Garlic", "Ginseng",
                                 "MandrakeRoot", "Nightshade", "SulfurousAsh", "SpidersSilk" })
                    {
                        Add(chest, r, Utility.RandomMinMax(15, 60));
                    }
                    Add(chest, "BlankScroll", Utility.RandomMinMax(10, 40));
                    break;
                case BotClass.Healer:
                    Add(chest, "Bandage", Utility.RandomMinMax(50, 200));
                    Add(chest, "Garlic", Utility.RandomMinMax(20, 50));
                    Add(chest, "Ginseng", Utility.RandomMinMax(20, 50));
                    break;
                default:
                    Add(chest, "Bandage", Utility.RandomMinMax(30, 120));
                    Add(chest, "Arrow", Utility.RandomMinMax(0, 200));
                    Add(chest, "GreaterHealPotion", Utility.RandomMinMax(1, 5));
                    break;
            }
        }

        // -------------------------------------------------------------------
        // Home chores: spare loot goes in the chest.
        // -------------------------------------------------------------------
        public static int StashLoot(PlayerBot bot, BaseHouse house)
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
            if (chest == null || bot.Backpack == null)
            {
                return 0;
            }

            var keep = new HashSet<Type>();
            foreach (var (t, _) in BotSupplies.WantedStacks(bot))
            {
                keep.Add(t);
            }

            var move = new List<Item>();
            foreach (var item in bot.Backpack.Items)
            {
                if (item is Gold or Key or HouseDeed or Spellbook or Runebook or RecallRune or BaseTool ||
                    item.LootType is LootType.Newbied or LootType.Blessed || keep.Contains(item.GetType()))
                {
                    continue;
                }
                // Loot only. Supplies (reagents, bandages, arrows) stay in the
                // pack: a mage that left its regs at home could not cast.
                if (item is BaseWeapon or BaseArmor or BaseJewel or BaseClothing ||
                    item.GetType().Name.EndsWith("Ingot") || item.GetType().Name.EndsWith("Ore") ||
                    IsGem(item) || item is Log or Board or Hides or Leather)
                {
                    move.Add(item);
                }
            }
            foreach (var item in move)
            {
                chest.DropItem(item);
            }
            return move.Count;
        }

        private static readonly HashSet<string> _gems = new()
        {
            "Amber", "Amethyst", "Citrine", "Diamond", "Emerald", "Ruby", "Sapphire",
            "StarSapphire", "Tourmaline",
        };

        private static bool IsGem(Item item) => _gems.Contains(item.GetType().Name);

        // -------------------------------------------------------------------
        // Chat
        // -------------------------------------------------------------------
        private static void SayFrom(PlayerBot bot, string category)
        {
            var line = ChatLibrary.PickRandom(category);
            if (!string.IsNullOrEmpty(line))
            {
                bot.Say(line);
            }
        }

        // -------------------------------------------------------------------
        // Headless test hook
        // -------------------------------------------------------------------
        private static long _lastToken;

        private static string RequestFile =>
            Path.Combine(Core.BaseDirectory, "Data", "Live", "homes_request.txt");

        private static string AckFile =>
            Path.Combine(Core.BaseDirectory, "Data", "Live", "homes_ack.json");

        private static long? ReadToken(out string verb, out int count)
        {
            verb = null;
            count = 1;
            try
            {
                if (!File.Exists(RequestFile))
                {
                    return null;
                }
                var parts = File.ReadAllText(RequestFile).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !long.TryParse(parts[0], out var token))
                {
                    return null;
                }
                verb = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";
                count = parts.Length > 2 && int.TryParse(parts[2], out var c) ? c : 1;
                return token;
            }
            catch
            {
                return null;
            }
        }

        private static void PollRequest()
        {
            var token = ReadToken(out var verb, out var count);
            if (token == null || token.Value == _lastToken)
            {
                return;
            }
            _lastToken = token.Value;

            var names = new List<string>();
            switch (verb)
            {
                case "seed":
                    names.Add($"{Seed(count)} seeded");
                    break;
                case "vendors":
                    names.Add($"{BotVendorHouses.EnsureAll()} vendor houses placed");
                    BotHouseShopping.RegisterShops();
                    break;
                case "shop":
                    names.AddRange(BotHouseShopping.SendShoppers(count));
                    break;
                case "rich":
                case "visit":
                    {
                        var picks = new List<PlayerBot>();
                        var offline = new List<PlayerBot>();
                        foreach (var m in World.Mobiles.Values)
                        {
                            if (m is not PlayerBot b || !b.Regular || b.Deleted || _errands.ContainsKey(b) ||
                                (verb == "rich" ? HomeOf(b) != null : HomeOf(b) == null))
                            {
                                continue;
                            }
                            if (b.Map != Map.Internal && !b.LoggingOut && b.Alive)
                            {
                                picks.Add(b);
                            }
                            else if (b.Map == Map.Internal)
                            {
                                offline.Add(b);
                            }
                        }
                        // Nobody suitable online: log some in.
                        while (picks.Count < count && offline.Count > 0)
                        {
                            var b = offline[^1];
                            offline.RemoveAt(offline.Count - 1);
                            if (BotRegulars.LogInNow(b))
                            {
                                picks.Add(b);
                            }
                        }
                        for (int i = 0; i < count && picks.Count > 0; i++)
                        {
                            int j = Utility.Random(picks.Count);
                            var b = picks[j];
                            picks.RemoveAt(j);
                            b.Combatant = null;
                            if (verb == "rich")
                            {
                                Banker.Deposit(b, CheapestDeed() + Reserve + 500);
                                StartBuying(b);
                            }
                            else
                            {
                                SendHome(b);
                            }
                            names.Add(b.Name);
                        }
                        break;
                    }
            }

            var line = $"{{\"token\":{token.Value},\"verb\":\"{verb}\",\"homes\":{_homeOf.Count}," +
                $"\"errands\":{_errands.Count},\"names\":\"{string.Join(", ", names).Replace("\"", "'")}\"}}";
            try { File.WriteAllText(AckFile, line); } catch { }
            Console.WriteLine($"[homes] RESULT {line}");
        }
    }

    // =====================================================================
    // ErrandWalkBehavior — walk to a spot off the road, then do one thing.
    // =====================================================================
    public class ErrandWalkBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Traveler"; // a reload just travels on

        private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(3);

        private readonly Point3D _goal;
        private readonly int _range;
        private readonly string _status;
        private readonly Action<PlayerBot> _onArrive;
        private readonly Action<PlayerBot> _onFail;
        private PathFollower _follower;
        private Timer _timer;
        private DateTime _started;
        private bool _done;

        public ErrandWalkBehavior(Point3D goal, int range, string status, Action<PlayerBot> onArrive,
            Action<PlayerBot> onFail)
        {
            _goal = goal;
            _range = range;
            _status = status;
            _onArrive = onArrive;
            _onFail = onFail;
        }

        public override string GetStatusLine(PlayerBot bot) => _status;

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            _started = Core.Now;
            _timer = Timer.DelayCall(TimeSpan.Zero, StepInterval, 0, () => Step(bot));
        }

        public override void OnDetached(PlayerBot bot)
        {
            _timer?.Stop();
            _timer = null;
            base.OnDetached(bot);
        }

        private void Step(PlayerBot bot)
        {
            if (_done || bot.Deleted || bot.Behavior != this || !bot.Alive)
            {
                _timer?.Stop();
                return;
            }
            if (bot.InRange(_goal, _range))
            {
                Finish(bot, true);
                return;
            }
            if (Core.Now - _started > GiveUpAfter)
            {
                Finish(bot, false);
                return;
            }
            _follower ??= new PathFollower(bot, _goal);
            _follower.Follow(run: false, range: _range);
        }

        private void Finish(PlayerBot bot, bool arrived)
        {
            _done = true;
            _timer?.Stop();
            (arrived ? _onArrive : _onFail)?.Invoke(bot);
        }
    }
}
