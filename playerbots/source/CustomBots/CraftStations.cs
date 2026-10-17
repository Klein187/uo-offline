// =========================================================================
// CraftStations.cs — where a smith or a tailor actually stands to work.
//
// A smith works within 2 tiles of an anvil and a forge (the range the real
// blacksmithy check uses). A tailor works next to a spinning wheel or a
// loom. The fixtures are decoration items in most towns and map statics
// in a few, so both are searched.
//
// Two uses:
//   HasFixture   the destination roll skips a station with no anvil or
//                wheel near it (Britain Tailor 5 has neither, so a tailor
//                sent there could never work at a wheel).
//   FindWorkTile the free standable tile beside the fixture that the
//                crafter walks to before it starts work.
//
// Test hook: Data/Live/crafters_request.txt = "<token>" lists every smith
// and tailor at work and how far each stands from the nearest anvil or
// wheel, in Data/Live/crafters_ack.json. "<token> send Smith|Tailor" sends
// a traveling one to the nearest station that has a fixture, to watch the
// arrival.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;

namespace Server.CustomBots
{
    public static class CraftStations
    {
        // How far from the station point a fixture may be and still count
        // as that station's.
        public const int SearchRange = 10;

        private static bool IsAnvil(int id) => id is 0x0FAF or 0x0FB0 or 11733 or 11734;
        private static bool IsForge(int id) => id is 0x0FB1 or (>= 0x197A and <= 0x19A9) or 11736;
        private static bool IsWheel(int id) => id is (>= 0x1015 and <= 0x101E) or (>= 0x10A4 and <= 0x10A6);
        private static bool IsLoom(int id) => id is >= 0x105F and <= 0x1066;

        // Classes that need a fixture to work. Carpenters and fishermen don't.
        public static bool NeedsFixture(BotClass cls) => cls is BotClass.Smith or BotClass.Tailor;

        private static readonly Dictionary<string, bool> _hasFixture = new();

        private static string Req => Path.Combine(Core.BaseDirectory, "Data", "Live", "crafters_request.txt");
        private static string Ack => Path.Combine(Core.BaseDirectory, "Data", "Live", "crafters_ack.json");

        public static void Initialize()
        {
            Timer.DelayCall(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), Poll);
        }

        private static void Poll()
        {
            if (!File.Exists(Req))
            {
                return;
            }
            string token;
            try
            {
                token = File.ReadAllText(Req).Trim();
                File.Delete(Req);
            }
            catch
            {
                return;
            }
            var parts = token.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[1].Equals("send", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<BotClass>(parts[2], true, out var sendCls))
            {
                Send(parts[0], sendCls);
                return;
            }
            var rows = new List<object>();
            int working = 0, near = 0, placing = 0;
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted || bot.Behavior is not CrafterBehavior cb ||
                    !NeedsFixture(bot.Class) || bot.Map == null || bot.Map == Map.Internal)
                {
                    continue;
                }
                var (work, _) = Collect(bot.Map, bot.Location, bot.Class);
                int d = int.MaxValue;
                foreach (var f in work)
                {
                    d = Math.Min(d, Cheb(bot.Location, f));
                }
                if (cb.Placed)
                {
                    working++;
                    if (d <= 2)
                    {
                        near++;
                    }
                }
                else
                {
                    placing++;
                }
                rows.Add(new
                {
                    name = bot.Name,
                    cls = bot.Class.ToString(),
                    at = $"({bot.X},{bot.Y},{bot.Z})",
                    placed = cb.Placed,
                    tilesFromFixture = d == int.MaxValue ? -1 : d,
                });
            }
            var doc = new { token, working, withinTwoOfFixture = near, stillWalkingIn = placing, crafters = rows };
            try
            {
                File.WriteAllText(Ack, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // the next request answers again
            }
        }

        private static void Send(string token, BotClass cls)
        {
            var station = BotClassHelper.StationFor(cls);
            var good = new List<BotDestination>();
            var skipped = new List<string>();
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type == station)
                {
                    if (HasFixture(d, cls))
                    {
                        good.Add(d);
                    }
                    else
                    {
                        skipped.Add(d.Name);
                    }
                }
            }
            PlayerBot pick = null;
            BotDestination best = null;
            int bd = int.MaxValue;
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot b || b.Deleted || !b.Alive || b.Class != cls ||
                    b.Behavior is not (TravelerBehavior or CrafterBehavior or BankSitterBehavior or ShopperBehavior or VisitorBehavior or IdleBehavior) || b.Combatant != null ||
                    b.Map != Map.Felucca || DungeonRegistry.IsInDungeon(b))
                {
                    continue;
                }
                foreach (var d in good)
                {
                    int dist = Cheb(b.Location, d.Location);
                    if (dist >= 20 && dist < bd)
                    {
                        bd = dist;
                        best = d;
                        pick = b;
                    }
                }
            }
            string line;
            if (pick == null)
            {
                int bots = 0;
                foreach (var m in World.Mobiles.Values)
                {
                    if (m is PlayerBot b && !b.Deleted && b.Class == cls)
                    {
                        bots++;
                    }
                }
                line = $"{{\"token\":\"{token}\",\"sent\":false,\"goodStations\":{good.Count}," +
                       $"\"skippedStations\":{skipped.Count},\"botsOfClass\":{bots}}}";
            }
            else
            {
                pick.Behavior = new TravelerBehavior { DestinationName = best.Name };
                Console.WriteLine($"[crafter] test: {pick.Name} ({cls}) sent to '{best.Name}', {bd} tiles; " +
                                  $"stations skipped for no fixture: {string.Join(", ", skipped)}");
                line = $"{{\"token\":\"{token}\",\"sent\":true,\"name\":\"{pick.Name}\",\"dest\":\"{best.Name}\",\"distance\":{bd}}}";
            }
            try
            {
                File.WriteAllText(Ack, line);
            }
            catch
            {
                // the next request answers again
            }
        }

        // The nearest station of this crafter's trade that has a fixture,
        // other than the one near `notNear` (the one it just failed at).
        public static BotDestination NearestGoodStation(PlayerBot bot, Point3D? notNear)
        {
            var station = BotClassHelper.StationFor(bot.Class);
            BotDestination best = null;
            int bd = int.MaxValue;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != station || !RedTerritory.MayGoTo(bot, d) || !HasFixture(d, bot.Class))
                {
                    continue;
                }
                if (notNear.HasValue && Cheb(d.Location, notNear.Value) <= SearchRange)
                {
                    continue;
                }
                int dist = Cheb(bot.Location, d.Location);
                if (dist < bd)
                {
                    bd = dist;
                    best = d;
                }
            }
            return best;
        }

        public static bool HasFixture(BotDestination d, BotClass cls)
        {
            if (d == null || !NeedsFixture(cls))
            {
                return true;
            }
            // Bots pick destinations while the world is still loading, before
            // the anvils and wheels are on the map. Every station looked bare
            // then, and caching that "no" hid all of them from smiths and
            // tailors. Until the world runs, don't judge and don't cache.
            if (World.WorldState is WorldState.Initial or WorldState.Loading)
            {
                return true;
            }
            var map = Map.Felucca;
            string key = $"{cls}|{d.Name}|{d.Location}";
            if (_hasFixture.TryGetValue(key, out var has))
            {
                return has;
            }
            var (work, _) = Collect(map, d.Location, cls);
            has = work.Count > 0;
            _hasFixture[key] = has;
            return has;
        }

        // The fixtures of the station near `center`: the ones a crafter
        // faces (anvil, wheel, loom) and, for a smith, the forges.
        private static (List<Point3D> work, List<Point3D> forges) Collect(Map map, Point3D center, BotClass cls)
        {
            var work = new List<Point3D>();
            var forges = new List<Point3D>();
            if (map == null || map == Map.Internal)
            {
                return (work, forges);
            }

            void Take(int id, Point3D p)
            {
                id &= TileData.MaxItemValue;
                if (cls == BotClass.Smith)
                {
                    if (IsAnvil(id))
                    {
                        work.Add(p);
                    }
                    else if (IsForge(id))
                    {
                        forges.Add(p);
                    }
                }
                else if (cls == BotClass.Tailor && (IsWheel(id) || IsLoom(id)))
                {
                    work.Add(p);
                }
            }

            foreach (var item in map.GetItemsInRange(center, SearchRange))
            {
                if (!item.Deleted && item.Parent == null && Math.Abs(item.Z - center.Z) <= 16)
                {
                    Take(item.ItemID, item.Location);
                }
            }
            for (int x = center.X - SearchRange; x <= center.X + SearchRange; x++)
            {
                for (int y = center.Y - SearchRange; y <= center.Y + SearchRange; y++)
                {
                    foreach (var st in map.Tiles.GetStaticTiles(x, y))
                    {
                        if (Math.Abs(st.Z - center.Z) <= 16)
                        {
                            Take(st.ID, new Point3D(x, y, st.Z));
                        }
                    }
                }
            }

            // A smith needs the forge too: keep only anvils with one in reach.
            if (cls == BotClass.Smith)
            {
                work.RemoveAll(a => !forges.Exists(f => Cheb(a, f) <= 4));
            }
            return (work, forges);
        }

        private static int Cheb(Point3D a, Point3D b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        // The tile to work from: standable, on the fixture's floor, beside
        // it (a smith also within 2 of a forge), with nobody standing on it.
        // Nearest the bot wins. `faceAt` is the fixture to turn toward.
        // Work tiles taken by a crafter at work or walking in, so two smiths
        // don't pick the same free tile and end up stacked on it.
        private static readonly Dictionary<Point3D, PlayerBot> _claims = new();

        public static void Claim(Point3D tile, PlayerBot bot)
        {
            Release(bot);
            _claims[tile] = bot;
        }

        public static void Release(PlayerBot bot)
        {
            Point3D? mine = null;
            foreach (var kv in _claims)
            {
                if (kv.Value == bot)
                {
                    mine = kv.Key;
                    break;
                }
            }
            if (mine.HasValue)
            {
                _claims.Remove(mine.Value);
            }
        }

        private static bool ClaimedByOther(Point3D p, PlayerBot bot) =>
            _claims.TryGetValue(p, out var who) && who != bot && !who.Deleted &&
            who.Behavior is CrafterBehavior;

        public static bool FindWorkTile(PlayerBot bot, Point3D center, out Point3D tile, out Point3D faceAt,
                                        ICollection<Point3D> skip = null)
        {
            tile = faceAt = Point3D.Zero;
            var map = bot.Map;
            var (work, forges) = Collect(map, center, bot.Class);
            if (work.Count == 0)
            {
                return false;
            }

            var cands = new List<(Point3D p, Point3D f, int ring, int d)>();
            // Ring 1 beside the fixture first; a smith may also stand 2 off.
            int maxRing = bot.Class == BotClass.Smith ? 2 : 1;
            for (int ring = 1; ring <= maxRing; ring++)
            {
                foreach (var f in work)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        for (int dy = -ring; dy <= ring; dy++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                            {
                                continue;
                            }
                            int x = f.X + dx, y = f.Y + dy;
                            if (!Walkable.TryFindStandZ(map, x, y, f.Z, out int z) || Math.Abs(z - f.Z) > 6)
                            {
                                continue;
                            }
                            var p = new Point3D(x, y, z);
                            if (bot.Class == BotClass.Smith && !forges.Exists(g => Cheb(p, g) <= 2))
                            {
                                continue;
                            }
                            if (Occupied(map, p, bot) || ClaimedByOther(p, bot) ||
                                skip?.Contains(p) == true)
                            {
                                continue;
                            }
                            cands.Add((p, f, ring, Cheb(bot.Location, p) + (Math.Abs(bot.Z - z) > 10 ? 50 : 0)));
                        }
                    }
                }
            }
            cands.Sort((a, b) => a.ring != b.ring ? a.ring.CompareTo(b.ring) : a.d.CompareTo(b.d));

            // A tile behind the anvil or the forge is standable but walled
            // off from the shop floor. Take the first one the pathfinder
            // can actually reach.
            int checks = 0;
            foreach (var c in cands)
            {
                if (c.p != bot.Location)
                {
                    if (++checks > 8)
                    {
                        break;
                    }
                    if (!new MovementPath(bot, c.p).Success)
                    {
                        continue;
                    }
                }
                tile = c.p;
                faceAt = c.f;
                return true;
            }
            // Nothing passed. The pathfinder treats a closed shop door as a
            // wall, so this can be a bot still out in the street: aim at
            // the nearest tile anyway and let the walker open the door.
            if (cands.Count > 0)
            {
                tile = cands[0].p;
                faceAt = cands[0].f;
                return true;
            }
            return false;
        }

        private static bool Occupied(Map map, Point3D p, Mobile self)
        {
            foreach (var m in map.GetMobilesAt(p))
            {
                if (m != self && !m.Deleted && Math.Abs(m.Z - p.Z) < 10)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
