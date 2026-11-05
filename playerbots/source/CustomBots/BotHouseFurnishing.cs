// =========================================================================
// BotHouseFurnishing.cs — what is inside a bot's house.
//
// Every house gets its own layout. The owner's way of living is rolled
// (plain, homely, scholar, drinker, hoarder, wealthy) and decides which
// rooms get set up and how full they are. The owner's trade adds a workshop:
// a forge and anvil for a smith, a loom and wheel for a tailor, and so on.
// A bigger house gets more, upstairs too.
//
// Only real floor gets furniture: a house floor tile with something over it
// (so no roofs, patios or castle yards), which can be walked to from the
// door or up the stairs. Doors and stairs keep clear space around them, and
// no piece may cut a floor in two, so the owner can always walk through.
//
// The first container locked down is the owner's chest (StashLoot and the
// vendor restock look for it there), so the chest always goes in first.
//
// Houses furnished before this have 6 pieces or fewer. Upgrade() adds to
// those on boot and leaves what is already there.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Items;
using Server.Mobiles;
using Server.Multis;

namespace Server.CustomBots
{
    public static class BotHouseFurnishing
    {
        // ---- Knobs ----

        // One piece of furniture per this many floor tiles, before the
        // owner's way of living changes it.
        private const int TilesPerPiece = 6;
        private const int MinPieces = 6, MaxPieces = 70;

        // Never fill more than this share of a floor.
        private const double MaxFloorShare = 0.45;

        // An upstairs floor smaller than this is a landing, not a room.
        private const int MinRoomTiles = 6;

        // Old furnishing locked down this many pieces at most (the five
        // pieces and the house's own one).
        private const int OldStylePieces = 6;

        private enum Living { Plain, Homely, Scholar, Drinker, Hoarder, Wealthy }

        // ---- The floor plan ----

        private readonly struct Tile : IEquatable<Tile>
        {
            public readonly int X, Y, Z;
            public Tile(int x, int y, int z) { X = x; Y = y; Z = z; }
            public bool Equals(Tile o) => X == o.X && Y == o.Y && Z == o.Z;
            public override bool Equals(object o) => o is Tile t && Equals(t);
            public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        }

        // One connected floor: its tiles, and those still open for furniture.
        private sealed class Room
        {
            public HashSet<Tile> Floor = new();
            public HashSet<Tile> Free = new();
            public HashSet<Tile> Blocked = new();
            public bool Ground;
            public int Limit;
        }

        public readonly struct Report
        {
            public readonly int Pieces, Rooms, FloorTiles;
            public readonly string Style;
            public Report(int pieces, int rooms, int floorTiles, string style)
            {
                Pieces = pieces;
                Rooms = rooms;
                FloorTiles = floorTiles;
                Style = style;
            }
        }

        // Tiles a person can stand on: flat surfaces of the house with
        // something over them.
        private static List<Room> ReadRooms(BaseHouse house)
        {
            var rooms = new List<Room>();
            var map = house.Map;
            if (map == null || map == Map.Internal)
            {
                return rooms;
            }

            var floor = new HashSet<Tile>();
            var stairs = new HashSet<Tile>();
            var topAt = new Dictionary<(int, int), int>();
            var list = house.Components.List;
            foreach (var e in list)
            {
                var key = (house.X + e.OffsetX, house.Y + e.OffsetY);
                int z = house.Z + e.OffsetZ;
                if (!topAt.TryGetValue(key, out int top) || z > top)
                {
                    topAt[key] = z;
                }
            }
            foreach (var e in list)
            {
                var data = TileData.ItemTable[e.ItemId & TileData.MaxItemValue];
                if (!data.Surface)
                {
                    continue;
                }
                var t = new Tile(house.X + e.OffsetX, house.Y + e.OffsetY, house.Z + e.OffsetZ);
                if (data.Height > 0)
                {
                    stairs.Add(t);
                }
                else if (topAt[(t.X, t.Y)] > t.Z && house.IsInside(new Point3D(t.X, t.Y, t.Z), 16) &&
                    map.CanFit(t.X, t.Y, t.Z, 16, true, false))
                {
                    floor.Add(t);
                }
            }

            // The door's room is the ground floor.
            Tile? entry = null;
            if (HomeVisitBehavior.FindDoor(house, out _, out var inside, out _))
            {
                foreach (var t in floor)
                {
                    if (t.X == inside.X && t.Y == inside.Y && Math.Abs(t.Z - inside.Z) <= 3)
                    {
                        entry = t;
                        break;
                    }
                }
            }

            var seen = new HashSet<Tile>();
            foreach (var start in floor)
            {
                if (seen.Contains(start))
                {
                    continue;
                }
                var room = new Room();
                var todo = new Stack<Tile>();
                todo.Push(start);
                seen.Add(start);
                while (todo.Count > 0)
                {
                    var t = todo.Pop();
                    room.Floor.Add(t);
                    foreach (var n in Neighbors(t))
                    {
                        if (floor.Contains(n) && seen.Add(n))
                        {
                            todo.Push(n);
                        }
                    }
                }
                room.Ground = entry.HasValue && room.Floor.Contains(entry.Value);
                // Sealed gaps in the walls get nothing. Upstairs counts when
                // a staircase reaches it.
                if (room.Ground || room.Floor.Count >= MinRoomTiles && TouchesStairs(room, stairs))
                {
                    rooms.Add(room);
                }
            }
            rooms.Sort((a, b) => b.Ground != a.Ground ? (b.Ground ? 1 : -1) : b.Floor.Count - a.Floor.Count);

            var doors = new List<Point3D>();
            foreach (var d in house.Doors)
            {
                if (d != null)
                {
                    doors.Add(d.Location);
                }
            }
            foreach (var room in rooms)
            {
                foreach (var t in room.Floor)
                {
                    if (!NearAny(t, doors, 2) && !NearStairs(t, stairs) && TileIsEmpty(map, t))
                    {
                        room.Free.Add(t);
                    }
                    else if (!TileIsEmpty(map, t))
                    {
                        room.Blocked.Add(t);
                    }
                }
                room.Limit = (int)(room.Floor.Count * MaxFloorShare) - room.Blocked.Count;
            }
            return rooms;
        }

        private static IEnumerable<Tile> Neighbors(Tile t)
        {
            yield return new Tile(t.X + 1, t.Y, t.Z);
            yield return new Tile(t.X - 1, t.Y, t.Z);
            yield return new Tile(t.X, t.Y + 1, t.Z);
            yield return new Tile(t.X, t.Y - 1, t.Z);
        }

        private static bool TouchesStairs(Room room, HashSet<Tile> stairs)
        {
            foreach (var t in room.Floor)
            {
                if (NearStairs(t, stairs))
                {
                    return true;
                }
            }
            return false;
        }

        // A stair tile beside this one, at about this height.
        private static bool NearStairs(Tile t, HashSet<Tile> stairs)
        {
            foreach (var s in stairs)
            {
                if (Math.Abs(s.X - t.X) <= 1 && Math.Abs(s.Y - t.Y) <= 1 && Math.Abs(s.Z - t.Z) <= 12)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool NearAny(Tile t, List<Point3D> points, int range)
        {
            foreach (var p in points)
            {
                if (Math.Abs(p.X - t.X) <= range && Math.Abs(p.Y - t.Y) <= range && Math.Abs(p.Z - t.Z) <= 16)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TileIsEmpty(Map map, Tile t)
        {
            foreach (var item in map.GetItemsInRange(new Point3D(t.X, t.Y, t.Z), 0))
            {
                if (item.Z >= t.Z - 2 && item.Z <= t.Z + 16)
                {
                    return false;
                }
            }
            return true;
        }

        // Would blocking these tiles cut the room's open floor in two?
        private static bool WouldSplit(Room room, List<Tile> cells)
        {
            var open = new HashSet<Tile>(room.Floor);
            open.ExceptWith(room.Blocked);
            open.ExceptWith(cells);
            if (open.Count == 0)
            {
                return true;
            }
            var todo = new Stack<Tile>();
            var seen = new HashSet<Tile>();
            foreach (var t in open)
            {
                todo.Push(t);
                seen.Add(t);
                break;
            }
            while (todo.Count > 0)
            {
                foreach (var n in Neighbors(todo.Pop()))
                {
                    if (open.Contains(n) && seen.Add(n))
                    {
                        todo.Push(n);
                    }
                }
            }
            return seen.Count != open.Count;
        }

        // ---- Placing things ----

        public static Item Make(string typeName)
        {
            var type = AssemblyHandler.FindTypeByName(typeName);
            if (type == null)
            {
                Console.WriteLine($"[furnish] no item type '{typeName}'");
                return null;
            }
            try
            {
                // Many items take only optional arguments (an amount),
                // which Activator won't fill in.
                foreach (var ctor in type.GetConstructors())
                {
                    var args = ctor.GetParameters();
                    var values = new object[args.Length];
                    var ok = true;
                    for (int i = 0; i < args.Length && ok; i++)
                    {
                        ok = args[i].HasDefaultValue;
                        values[i] = ok ? args[i].DefaultValue : null;
                    }
                    if (ok)
                    {
                        return ctor.Invoke(values) as Item;
                    }
                }
                Console.WriteLine($"[furnish] '{typeName}' will not build bare");
                return null;
            }
            catch
            {
                Console.WriteLine($"[furnish] '{typeName}' will not build bare");
                return null;
            }
        }

        private sealed class Placer
        {
            public BaseHouse House;
            public PlayerBot Owner;
            public int Pieces;
            public int Budget;

            public bool Spent => Pieces >= Budget;

            private void Fix(Item item, Point3D p)
            {
                item.MoveToWorld(p, House.Map);
                if (!House.LockDown(Owner, item, false))
                {
                    item.Movable = false;
                }
                Pieces++;
            }

            private static bool IsWallSide(Room room, Tile t)
            {
                foreach (var n in Neighbors(t))
                {
                    if (!room.Floor.Contains(n))
                    {
                        return true;
                    }
                }
                return false;
            }

            // Open tiles to try, shuffled, against the wall first if asked,
            // and close to the anchor if there is one.
            private static List<Tile> Candidates(Room room, bool wall, Point3D? near, int range)
            {
                var best = new List<Tile>();
                var rest = new List<Tile>();
                foreach (var t in room.Free)
                {
                    if (near.HasValue && (Math.Abs(t.X - near.Value.X) > range || Math.Abs(t.Y - near.Value.Y) > range))
                    {
                        continue;
                    }
                    (wall && !IsWallSide(room, t) ? rest : best).Add(t);
                }
                Shuffle(best);
                Shuffle(rest);
                best.AddRange(rest);
                return best;
            }

            private static void Shuffle(List<Tile> list)
            {
                for (int i = list.Count - 1; i > 0; i--)
                {
                    int j = Utility.Random(i + 1);
                    (list[i], list[j]) = (list[j], list[i]);
                }
            }

            private static bool Take(Room room, List<Tile> cells)
            {
                if (room.Limit < cells.Count || WouldSplit(room, cells))
                {
                    return false;
                }
                foreach (var c in cells)
                {
                    room.Free.Remove(c);
                    room.Blocked.Add(c);
                }
                room.Limit -= cells.Count;
                return true;
            }

            // A one-tile piece on the floor. Returns where it went.
            public Point3D? Put(Room room, Item item, bool wall = true, Point3D? near = null, int range = 2)
            {
                if (item == null)
                {
                    return null;
                }
                var tried = 0;
                foreach (var t in Candidates(room, wall, near, range))
                {
                    if (++tried > 12)
                    {
                        break;
                    }
                    if (Take(room, new List<Tile> { t }))
                    {
                        var p = new Point3D(t.X, t.Y, t.Z);
                        Fix(item, p);
                        return p;
                    }
                }
                item.Delete();
                return null;
            }

            public Point3D? Put(Room room, string[] oneOf, bool wall = true, Point3D? near = null, int range = 2) =>
                Put(room, Make(oneOf[Utility.Random(oneOf.Length)]), wall, near, range);

            // A piece standing on another: a plate on a table.
            public void On(Point3D? under, string[] oneOf)
            {
                if (under == null)
                {
                    return;
                }
                var item = Make(oneOf[Utility.Random(oneOf.Length)]);
                if (item == null)
                {
                    return;
                }
                var p = under.Value;
                int height = 0;
                foreach (var below in House.Map.GetItemsInRange(p, 0))
                {
                    if (below.Parent == null && below != item)
                    {
                        height = Math.Max(height, below.Z - p.Z + below.ItemData.CalcHeight);
                    }
                }
                Fix(item, new Point3D(p.X, p.Y, p.Z + height));
            }

            // A piece of several tiles (a bed, a loom). All its tiles open.
            public bool PutAddon(Room room, string[] oneOf)
            {
                if (Make(oneOf[Utility.Random(oneOf.Length)]) is not BaseAddon addon)
                {
                    return false;
                }
                foreach (var t in Candidates(room, true, null, 0))
                {
                    var cells = new List<Tile>();
                    var fits = true;
                    foreach (var c in addon.Components)
                    {
                        var cell = new Tile(t.X + c.Offset.X, t.Y + c.Offset.Y, t.Z);
                        if (!cells.Contains(cell))
                        {
                            cells.Add(cell);
                        }
                        fits = fits && room.Free.Contains(cell);
                    }
                    if (fits && Take(room, cells))
                    {
                        addon.MoveToWorld(new Point3D(t.X, t.Y, t.Z), House.Map);
                        House.Addons.Add(addon);
                        Pieces++;
                        return true;
                    }
                }
                addon.Delete();
                return false;
            }
        }

        // ---- What goes where ----

        private static readonly string[] Seats = { "WoodenChair", "WoodenChairCushion", "BambooChair", "Stool", "FootStool" };
        private static readonly string[] FineSeats = { "FancyWoodenChairCushion", "WoodenChairCushion", "Throne", "WoodenThrone", "StoneChair" };
        private static readonly string[] Tables = { "LargeTable", "YewWoodTable", "PlainLowTable", "ElegantLowTable" };
        private static readonly string[] TableWare = { "Plate", "Goblet", "Pitcher", "CeramicMug", "PewterMug", "Fork", "Knife", "Spoon", "BreadLoaf", "Apple", "CheeseWheel" };
        private static readonly string[] Drinks = { "Pitcher", "CeramicMug", "PewterMug", "Goblet" };
        private static readonly string[] Reading = { "RedBook", "BlueBook", "BrownBook", "TanBook", "Globe", "Hourglass", "Candle" };
        private static readonly string[] Shelves = { "FullBookcase", "FullBookcase", "EmptyBookcase" };
        private static readonly string[] Clothes = { "Armoire", "FancyArmoire", "Drawer", "FancyDrawer" };
        private static readonly string[] Lights = { "Candelabra", "CandelabraStand", "CandleLarge", "CandleLong", "Brazier", "BrazierTall" };
        private static readonly string[] Finery = { "PottedPlant", "PottedPlant1", "PottedPlant2", "PottedTree", "PottedTree1", "Vase", "LargeVase", "SmallUrn", "StatueSouth", "StatueSouth2", "StatueEast", "StatueEast2", "BustSouth", "BustEast", "StatuePegasus", "SmallTowerSculpture" };
        private static readonly string[] Storage = { "LargeCrate", "MediumCrate", "SmallCrate", "Barrel", "Keg", "WoodenBox", "MetalChest" };
        private static readonly string[] Beds = { "SmallBedSouthAddon", "SmallBedEastAddon" };
        private static readonly string[] BigBeds = { "LargeBedSouthAddon", "LargeBedEastAddon" };
        private static readonly string[] Hearths = { "StoneOvenEastAddon", "StoneOvenSouthAddon", "StoneFireplaceEastAddon", "StoneFireplaceSouthAddon", "SandstoneFireplaceEastAddon", "SandstoneFireplaceSouthAddon", "GrayBrickFireplaceEastAddon", "GrayBrickFireplaceSouthAddon" };

        private static Living RollLiving(PlayerBot owner, int floorTiles)
        {
            int roll = Utility.Random(100);
            if (floorTiles >= 100 && roll < 25)
            {
                return Living.Wealthy;
            }
            if (owner.Class == BotClass.Mage && roll < 50)
            {
                return Living.Scholar;
            }
            return Utility.Random(6) switch
            {
                0 => Living.Plain,
                1 => Living.Homely,
                2 => Living.Scholar,
                3 => Living.Drinker,
                4 => Living.Hoarder,
                _ => Living.Homely,
            };
        }

        private static void Bedroom(Placer p, Room room, Living living)
        {
            if (living == Living.Plain && room.Floor.Count < 60)
            {
                p.Put(room, new[] { "Bedroll" });
            }
            else if (!p.PutAddon(room, living == Living.Wealthy || room.Floor.Count >= 80 && Utility.RandomBool() ? BigBeds : Beds))
            {
                p.PutAddon(room, Beds);
            }
            p.Put(room, Clothes);
            if (Utility.RandomBool())
            {
                var stand = p.Put(room, new[] { "Nightstand" });
                p.On(stand, new[] { "Candle", "RedBook", "Goblet" });
            }
        }

        private static void Dining(Placer p, Room room, string[] onTable, string[] seats)
        {
            var table = p.Put(room, Tables, wall: Utility.RandomBool());
            if (table == null)
            {
                return;
            }
            for (int i = Utility.RandomMinMax(1, 4); i > 0; i--)
            {
                p.Put(room, seats, wall: false, near: table, range: 1);
            }
            p.On(table, onTable);
        }

        private static void Study(Placer p, Room room)
        {
            var desk = p.Put(room, new[] { "WritingTable" });
            p.Put(room, Seats, wall: false, near: desk, range: 1);
            p.On(desk, Reading);
            for (int i = Utility.RandomMinMax(1, 3); i > 0; i--)
            {
                p.Put(room, Shelves);
            }
            if (Utility.RandomBool())
            {
                var stand = p.Put(room, new[] { "PlainLowTable" });
                p.On(stand, new[] { "Globe", "Spyglass", "Sextant", "Hourglass" });
            }
        }

        private static void Hearth(Placer p, Room room)
        {
            p.PutAddon(room, Hearths);
            p.Put(room, new[] { "Stool", "WoodenBench" }, wall: false);
            p.Put(room, new[] { "Barrel", "Keg", "SackFlour" });
        }

        private static void Parlour(Placer p, Room room, bool fine)
        {
            p.Put(room, fine ? FineSeats : Seats, wall: false);
            p.Put(room, new[] { "WoodenBench", "WoodenChairCushion" });
            for (int i = Utility.RandomMinMax(1, fine ? 4 : 2); i > 0; i--)
            {
                p.Put(room, Finery);
            }
            p.Put(room, Lights);
        }

        private static void Storeroom(Placer p, Room room)
        {
            for (int i = Utility.RandomMinMax(3, 7); i > 0; i--)
            {
                p.Put(room, Storage);
            }
        }

        // The owner's trade, set out as a work corner.
        private static void Workshop(Placer p, Room room, PlayerBot owner)
        {
            Point3D? bench = null;
            void Bench(params string[] tools)
            {
                bench = p.Put(room, new[] { "LargeTable", "PlainLowTable" });
                p.On(bench, tools);
            }
            switch (owner.Class)
            {
                case BotClass.Smith:
                    p.Put(room, new[] { "Forge" });
                    p.Put(room, new[] { "Anvil" });
                    Bench("Tongs", "SmithHammer", "Scales");
                    p.Put(room, new[] { "Barrel" });
                    break;
                case BotClass.Tailor:
                    p.PutAddon(room, new[] { "LoomEastAddon", "LoomSouthAddon" });
                    p.PutAddon(room, new[] { "SpinningWheelEastAddon", "SpinningWheelSouthAddon" });
                    Bench("SewingKit", "Scissors", "BoltOfCloth", "Dyes");
                    p.Put(room, new[] { "DyeTub" });
                    break;
                case BotClass.Carpenter:
                    Bench("Saw", "Hammer", "DovetailSaw", "Nails");
                    p.Put(room, new[] { "Barrel", "SmallCrate" });
                    p.Put(room, new[] { "EmptyBookcase", "Drawer" });
                    break;
                case BotClass.Mage:
                    Bench("MortarPestle", "FullVialsWRack", "EmptyVialsWRack", "SmallFlask", "CurvedFlask");
                    p.Put(room, Shelves);
                    p.Put(room, new[] { "CandleSkull", "Candelabra" });
                    break;
                case BotClass.Healer:
                    Bench("MortarPestle", "RedBottle", "GreenBottle", "SmallFlask");
                    p.Put(room, new[] { "Bedroll" });
                    p.Put(room, new[] { "Keg" });
                    break;
                case BotClass.Bard:
                    p.Put(room, new[] { "Harp", "Drums" });
                    Bench("Lute", "LapHarp", "Tambourine");
                    p.Put(room, new[] { "WoodenBench", "Stool" });
                    break;
                case BotClass.Fisherman:
                    p.Put(room, new[] { "Barrel" });
                    Bench("FishingPole", "RawFishSteak", "Sextant", "Spyglass");
                    break;
                case BotClass.Lumberjack:
                    p.Put(room, new[] { "Hatchet", "Axe" });
                    p.Put(room, new[] { "Barrel", "LargeCrate" });
                    break;
                case BotClass.Miner:
                    p.Put(room, new[] { "Pickaxe", "Shovel" });
                    p.Put(room, new[] { "LargeCrate", "Barrel" });
                    break;
                case BotClass.Tamer:
                case BotClass.Ranger:
                    p.PutAddon(room, new[] { "WaterTroughEastAddon", "WaterTroughSouthAddon" });
                    p.Put(room, new[] { "SackFlour", "Barrel" });
                    p.Put(room, new[] { "ShepherdsCrook", "Bedroll" });
                    break;
                case BotClass.Merchant:
                    Bench("Scales", "Hourglass", "RedBook");
                    p.Put(room, new[] { "MetalChest", "LargeCrate" });
                    p.Put(room, Storage);
                    break;
                case BotClass.Archer:
                    p.Put(room, new[] { "Bow", "Crossbow" });
                    p.Put(room, new[] { "Barrel" });
                    break;
                case BotClass.Fencer:
                    p.Put(room, new[] { "Kryss", "WarFork" });
                    p.Put(room, new[] { "Barrel" });
                    break;
                default:
                    p.Put(room, new[] { "Longsword", "Halberd", "HeaterShield", "MetalKiteShield" });
                    p.Put(room, new[] { "Barrel", "SmallCrate" });
                    break;
            }
        }

        // -------------------------------------------------------------------
        // Furnish a house. Empty or old-style houses alike: what stands in the
        // house stays, and new pieces go around it.
        // -------------------------------------------------------------------
        public static Report Furnish(BaseHouse house, PlayerBot owner)
        {
            var rooms = ReadRooms(house);
            int floorTiles = 0;
            foreach (var r in rooms)
            {
                floorTiles += r.Floor.Count;
            }
            if (rooms.Count == 0)
            {
                return new Report(0, 0, 0, "no floor");
            }

            var living = RollLiving(owner, floorTiles);
            double fullness = living switch
            {
                Living.Plain   => 0.55,
                Living.Hoarder => 1.5,
                Living.Wealthy => 1.25,
                _              => 1.0,
            };
            var p = new Placer
            {
                House = house,
                Owner = owner,
                Budget = Math.Clamp((int)(floorTiles / TilesPerPiece * fullness), MinPieces, MaxPieces),
            };

            var ground = rooms[0];
            var upstairs = rooms.Count > 1 ? rooms[1] : ground;

            // The chest first (see the top of the file), unless the house
            // already has one.
            if (ChestOf(house) == null)
            {
                var chest = new WoodenChest();
                BotHomes.FillChest(chest, owner);
                p.Put(ground, chest);
            }

            // What every home has, then the trade.
            Bedroom(p, upstairs, living);
            Dining(p, ground, living == Living.Drinker ? Drinks : TableWare, living == Living.Wealthy ? FineSeats : Seats);
            Workshop(p, ground, owner);

            // Then rooms by way of living, until the budget or the floor
            // runs out.
            int guard = 0;
            while (!p.Spent && ++guard <= 30)
            {
                var room = rooms[Utility.Random(rooms.Count)];
                switch (Pick(living))
                {
                    case 0: Bedroom(p, room, living); break;
                    case 1: Dining(p, room, living == Living.Drinker ? Drinks : TableWare, Seats); break;
                    case 2: Study(p, room); break;
                    case 3: Hearth(p, room); break;
                    case 4: Parlour(p, room, living == Living.Wealthy); break;
                    default: Storeroom(p, room); break;
                }
            }
            return new Report(p.Pieces, rooms.Count, floorTiles, living.ToString());
        }

        // 0 bedroom, 1 dining, 2 study, 3 hearth, 4 parlour, 5 storeroom
        private static int Pick(Living living)
        {
            int[] table = living switch
            {
                Living.Plain   => new[] { 3, 5, 1 },
                Living.Scholar => new[] { 2, 2, 2, 4, 0 },
                Living.Drinker => new[] { 1, 1, 1, 5, 3 },
                Living.Hoarder => new[] { 5, 5, 5, 5, 1 },
                Living.Wealthy => new[] { 4, 4, 1, 2, 0, 3 },
                _              => new[] { 1, 3, 4, 5, 0, 2 },
            };
            return table[Utility.Random(table.Length)];
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

        // Houses from before this file: furnish around what they have. A
        // house that already has more than the old pieces is left alone.
        public static bool Upgrade(BaseHouse house, PlayerBot owner)
        {
            if (house.LockDowns.Count > OldStylePieces || house.Addons.Count > 0)
            {
                return false;
            }
            var done = Furnish(house, owner);
            if (done.Pieces == 0)
            {
                Console.WriteLine($"[furnish] nothing went in the {house.GetType().Name} at {house.Location}: " +
                    $"{done.Rooms} floor(s), {done.FloorTiles} tiles, {done.Style}");
            }
            return done.Pieces > 0;
        }
    }
}
