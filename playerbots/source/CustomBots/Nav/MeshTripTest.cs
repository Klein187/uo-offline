// =========================================================================
// MeshTripTest.cs — one bot, a list of stops, walked on the drawn mesh only.
//
// Test hook: Data/Live/meshtrip_request.txt =
//     "<token> <stop> | <stop> | <stop> ..."
// Each stop is a destination name with a drawn Area (a shop or a bank).
// The rig picks a free bot standing on the mesh near the first stop and
// walks it from stop to stop with a strict ZoneFollower: no waypoints and
// no engine walker. If the mesh can't take it somewhere, that leg fails
// and the trip stops there with the reason.
//
// At a shop the bot walks up to the vendor, says "vendor buy" and buys
// something real (what it needs, or else the cheapest thing it can pay
// for). At a bank it says "bank" and puts what it bought in its bank box.
//
// Every leg records the zones it crossed, its steps, how many of those
// steps ended off the drawn ground (should be 0), and how long it took.
// The answer is written to Data/Live/meshtrip_ack.json as the trip goes.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Server;
using Server.Items;
using Server.Mobiles;
using MoveDelays = Server.Movement.Movement;

namespace Server.CustomBots
{
    public static class MeshTripTest
    {
        private static string Req => Path.Combine(Core.BaseDirectory, "Data", "Live", "meshtrip_request.txt");
        private static string Ack => Path.Combine(Core.BaseDirectory, "Data", "Live", "meshtrip_ack.json");

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
            string text;
            try
            {
                text = File.ReadAllText(Req).Trim();
                File.Delete(Req);
            }
            catch
            {
                return;
            }
            int sp = text.IndexOf(' ');
            string token = sp > 0 ? text[..sp] : "0";
            var names = (sp > 0 ? text[(sp + 1)..] : "")
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Start(token, names);
        }

        public sealed class Stop
        {
            public string Name;
            public DestinationType Type;
            public PaintedZone Area;
            public Point3D Goal;
            public BaseVendor Vendor;
        }

        public sealed class LegReport
        {
            public string to;
            public string from;
            public string goal;
            public List<string> zones = new();
            public int steps;
            public int offMeshSteps;
            public List<string> offMeshTiles = new();
            public double seconds;
            public string result = "walking";
            public string action;
        }

        public sealed class Report
        {
            public string token;
            public string bot;
            public string start;
            public string status = "running";
            public string error;
            public List<string> plan = new();
            public List<LegReport> legs = new();
        }

        public static void Write(Report r)
        {
            try
            {
                File.WriteAllText(Ack, JsonSerializer.Serialize(r, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    IncludeFields = true,
                }));
            }
            catch
            {
                // the next write tries again
            }
        }

        private static void Start(string token, string[] names)
        {
            var report = new Report { token = token };
            var map = ZoneRegistry.MeshMap;
            if (map == null || names.Length == 0)
            {
                report.status = "error";
                report.error = names.Length == 0 ? "no stops given" : "no mesh map";
                Write(report);
                return;
            }

            var stops = new List<Stop>();
            foreach (var n in names)
            {
                var stop = ResolveStop(n, map, out string why);
                if (stop == null)
                {
                    report.status = "error";
                    report.error = $"{n}: {why}";
                    Write(report);
                    return;
                }
                stops.Add(stop);
            }

            // A free bot on the mesh, nearest the first stop, that the mesh
            // can route to it.
            PlayerBot pick = null;
            int best = int.MaxValue;
            foreach (var m in map.GetMobilesInRange(stops[0].Goal, 120))
            {
                if (m is not PlayerBot bot || bot.Deleted || !bot.Alive || bot.LifecycleExempt ||
                    bot.LoggingOut || bot.CorpseRunPending || bot.Combatant != null ||
                    BotPartyManager.IsInParty(bot) || bot.Mounted ||
                    bot.Behavior is not (TravelerBehavior or BankSitterBehavior or IdleBehavior or ShopperBehavior or VisitorBehavior))
                {
                    continue;
                }
                if (ZoneRegistry.MeshZoneAt(bot.Location) == null)
                {
                    continue;
                }
                int d = Math.Max(Math.Abs(bot.X - stops[0].Goal.X), Math.Abs(bot.Y - stops[0].Goal.Y));
                if (d < 25 || d >= best)
                {
                    continue;   // far enough that the first leg is a real walk
                }
                if (ZoneNav.FindRoute(map, bot.Location, stops[0].Goal) == null)
                {
                    continue;
                }
                best = d;
                pick = bot;
            }
            if (pick == null)
            {
                report.status = "error";
                report.error = "no free bot on the mesh with a zone route to the first stop";
                Write(report);
                return;
            }

            // The whole trip planned up front, so a gap shows before walking.
            var at = pick.Location;
            foreach (var s in stops)
            {
                var r = ZoneNav.FindRoute(map, at, s.Goal);
                report.plan.Add(r == null
                    ? $"{s.Name}: NO ZONE ROUTE from ({at.X},{at.Y},{at.Z})"
                    : $"{s.Name}: {string.Join(" > ", r.Zones.Select(z => z.Name))}");
                at = s.Goal;
            }

            int gold = pick.Backpack?.GetAmount(typeof(Gold)) ?? 0;
            if (gold < 500 && pick.Backpack != null)
            {
                pick.Backpack.DropItem(new Gold(500 - gold));
            }

            report.bot = pick.Name;
            report.start = $"({pick.X},{pick.Y},{pick.Z}) in {ZoneRegistry.MeshZoneAt(pick.Location)?.Name}";
            Console.WriteLine($"[meshtrip] {pick.Name} starts at {report.start}: {string.Join(" -> ", names)}");
            Write(report);
            pick.Behavior = new MeshTripBehavior(report, stops);
        }

        private static Stop ResolveStop(string name, Map map, out string why)
        {
            why = null;
            var dest = DestinationCatalog.GetByName(name);
            if (dest == null)
            {
                why = "unknown destination";
                return null;
            }
            var area = ZoneRegistry.AreaForDestination(dest.Name, dest.Location);
            if (area == null || !area.IsMesh)
            {
                why = "no drawn area";
                return null;
            }
            var stop = new Stop { Name = dest.Name, Type = dest.Type, Area = area };

            if (area.IsVendorArea)
            {
                stop.Vendor = BotVendorPurchase.FindVendor(area, map);
                if (stop.Vendor == null)
                {
                    why = "no vendor in the drawn area";
                    return null;
                }
                // The drawn tile nearest the vendor: the counter.
                var v = stop.Vendor.Location;
                Point3D? goal = null;
                int bd = int.MaxValue;
                for (int dx = -4; dx <= 4; dx++)
                {
                    for (int dy = -4; dy <= 4; dy++)
                    {
                        int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        if (d == 0 || d >= bd)
                        {
                            continue;
                        }
                        int x = v.X + dx, y = v.Y + dy;
                        int z = map.GetAverageZ(x, y);
                        foreach (var cz in new[] { v.Z, z })
                        {
                            if (ReferenceEquals(ZoneRegistry.MeshZoneAt(x, y, cz), area) &&
                                map.CanFit(x, y, cz, 16, false, false))
                            {
                                goal = new Point3D(x, y, cz);
                                bd = d;
                                break;
                            }
                        }
                    }
                }
                if (goal == null)
                {
                    why = $"no drawn tile next to {stop.Vendor.Name} at ({v.X},{v.Y},{v.Z})";
                    return null;
                }
                stop.Goal = goal.Value;
            }
            else
            {
                var g = dest.Location;
                if (!ReferenceEquals(ZoneRegistry.MeshZoneAt(g), area))
                {
                    var r = area.RandomStandable(map, g.Z);
                    if (r == null)
                    {
                        why = "no standable tile in the area";
                        return null;
                    }
                    g = r.Value;
                }
                stop.Goal = g;
            }
            return stop;
        }
    }

    public class MeshTripBehavior : PlayerBotBehavior
    {
        // Saved mid-trip it wakes up as a plain Traveler.
        public override string SerializableName => "Traveler";

        private readonly MeshTripTest.Report _report;
        private readonly List<MeshTripTest.Stop> _stops;
        private readonly List<Item> _bought = new();
        private int _leg = -1;
        private ZoneFollower _walker;
        private MeshTripTest.LegReport _cur;
        private Timer _stepTimer;
        private DateTime _legStart;
        private Point3D _last;
        private bool _done;
        private static readonly TimeSpan LegTimeout = TimeSpan.FromMinutes(4);

        public MeshTripBehavior(MeshTripTest.Report report, List<MeshTripTest.Stop> stops)
        {
            _report = report;
            _stops = stops;
        }

        public override string GetStatusLine(PlayerBot bot) =>
            _leg >= 0 && _leg < _stops.Count ? $"mesh test: walking to {_stops[_leg].Name}" : "mesh test";

        public override Point3D? NavGoal(PlayerBot bot) => _walker?.GetGoalLocation();
        public override ILegFollower ActiveLegFollower => _stepTimer != null ? _walker : null;

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            bot.LifecycleExempt = true;
            NextLeg(bot);
        }

        public override void OnDetached(PlayerBot bot)
        {
            Stop();
            bot.LifecycleExempt = false;
            if (!_done)
            {
                Finish(bot, "error", $"taken off the trip by {bot.Behavior?.SerializableName ?? "nothing"}");
            }
            base.OnDetached(bot);
        }

        private void Stop()
        {
            _stepTimer?.Stop();
            _stepTimer = null;
        }

        private void NextLeg(PlayerBot bot)
        {
            _leg++;
            if (_leg >= _stops.Count)
            {
                Finish(bot, "passed", null);
                bot.Behavior = new TravelerBehavior();
                return;
            }
            var s = _stops[_leg];
            _cur = new MeshTripTest.LegReport
            {
                to = s.Name,
                from = $"({bot.X},{bot.Y},{bot.Z})",
                goal = $"({s.Goal.X},{s.Goal.Y},{s.Goal.Z})",
            };
            _report.legs.Add(_cur);
            _walker = new ZoneFollower(bot, s.Goal) { Strict = true };
            _legStart = Core.Now;
            _last = bot.Location;
            Note(ZoneRegistry.MeshZoneAt(bot.Location));
            Console.WriteLine($"[meshtrip] {bot.Name} leg {_leg + 1}: {_cur.from} -> {s.Name} {_cur.goal}");
            MeshTripTest.Write(_report);

            Stop();
            var interval = TimeSpan.FromMilliseconds(MoveDelays.WalkFootDelay);
            _stepTimer = Timer.DelayCall(interval, interval, () => Step(bot));
        }

        private void Note(PaintedZone z)
        {
            string n = z?.Name ?? "(off the mesh)";
            if (_cur.zones.Count == 0 || _cur.zones[^1] != n)
            {
                _cur.zones.Add(n);
            }
        }

        private void Step(PlayerBot bot)
        {
            if (bot.Deleted || !ReferenceEquals(bot.Behavior, this))
            {
                Stop();
                return;
            }
            if (!bot.Alive || bot.Combatant != null)
            {
                FailLeg(bot, bot.Alive ? $"pulled into a fight with {bot.Combatant?.Name}" : "died");
                return;
            }

            bool arrived = _walker.Follow(false, 1);

            if (bot.Location != _last)
            {
                _last = bot.Location;
                _cur.steps++;
                var z = ZoneRegistry.MeshZoneAt(bot.Location);
                if (z == null)
                {
                    _cur.offMeshSteps++;
                    var p = bot.Location;
                    var drawn = ZoneRegistry.All.FirstOrDefault(a => a.IsMesh && a.Contains(p.X, p.Y));
                    _cur.offMeshTiles.Add(drawn != null
                        ? $"({p.X},{p.Y},{p.Z}) inside {drawn.Name} but at the wrong height"
                        : $"({p.X},{p.Y},{p.Z}) outside every drawn shape");
                }
                Note(z);
            }

            if (_walker.Failed)
            {
                FailLeg(bot, $"{_walker.FailReason} at ({bot.X},{bot.Y},{bot.Z})");
                return;
            }
            if (Core.Now - _legStart > LegTimeout)
            {
                FailLeg(bot, $"timed out at ({bot.X},{bot.Y},{bot.Z})");
                return;
            }
            if (!arrived)
            {
                return;
            }

            Stop();
            _cur.seconds = Math.Round((Core.Now - _legStart).TotalSeconds, 1);
            _cur.result = "arrived";
            var s = _stops[_leg];
            Console.WriteLine($"[meshtrip] {bot.Name} reached {s.Name} in {_cur.seconds}s, " +
                              $"{_cur.steps} steps, {_cur.offMeshSteps} off the mesh");
            MeshTripTest.Write(_report);

            if (s.Vendor != null)
            {
                Shop(bot, s);
            }
            else if (s.Type == DestinationType.Bank)
            {
                Bank(bot);
            }
            else
            {
                Timer.DelayCall(TimeSpan.FromSeconds(2), () => Continue(bot));
            }
        }

        private void Shop(PlayerBot bot, MeshTripTest.Stop s)
        {
            bot.Direction = bot.GetDirectionTo(s.Vendor);
            try { bot.Say("vendor buy"); } catch { }
            Timer.DelayCall(TimeSpan.FromMilliseconds(1500), () =>
            {
                if (bot.Deleted || !ReferenceEquals(bot.Behavior, this))
                {
                    return;
                }
                var before = bot.Backpack?.Items.ToList() ?? new List<Item>();
                string what = BotVendorPurchase.TryBuy(bot, s.Vendor, s.Area) ??
                              BotVendorPurchase.TryBuyCheapest(bot, s.Vendor);
                if (bot.Backpack != null)
                {
                    foreach (var i in bot.Backpack.Items)
                    {
                        if (!before.Contains(i))
                        {
                            _bought.Add(i);
                        }
                    }
                }
                _cur.action = what != null
                    ? $"bought {what} from {s.Vendor.Name} ({Math.Max(Math.Abs(bot.X - s.Vendor.X), Math.Abs(bot.Y - s.Vendor.Y))} tiles away)"
                    : $"bought nothing from {s.Vendor.Name}";
                MeshTripTest.Write(_report);
                Timer.DelayCall(TimeSpan.FromSeconds(2), () => Continue(bot));
            });
        }

        private void Bank(PlayerBot bot)
        {
            try { bot.Say("bank"); } catch { }
            Timer.DelayCall(TimeSpan.FromMilliseconds(1500), () =>
            {
                if (bot.Deleted || !ReferenceEquals(bot.Behavior, this))
                {
                    return;
                }
                var box = bot.BankBox;
                int moved = 0;
                foreach (var i in _bought)
                {
                    if (!i.Deleted && i.Parent == bot.Backpack && box != null)
                    {
                        box.DropItem(i);
                        moved++;
                    }
                }
                _cur.action = $"put {moved} item(s) in the bank box";
                MeshTripTest.Write(_report);
                Timer.DelayCall(TimeSpan.FromSeconds(2), () => Continue(bot));
            });
        }

        private void Continue(PlayerBot bot)
        {
            if (!bot.Deleted && ReferenceEquals(bot.Behavior, this))
            {
                NextLeg(bot);
            }
        }

        private void FailLeg(PlayerBot bot, string why)
        {
            Stop();
            _cur.seconds = Math.Round((Core.Now - _legStart).TotalSeconds, 1);
            _cur.result = "failed: " + why;
            Console.WriteLine($"[meshtrip] {bot.Name} leg {_leg + 1} to {_stops[_leg].Name} failed: {why}");
            Finish(bot, "failed", why);
            if (ReferenceEquals(bot.Behavior, this))
            {
                bot.Behavior = new TravelerBehavior();
            }
        }

        private void Finish(PlayerBot bot, string status, string error)
        {
            if (_done)
            {
                return;
            }
            _done = true;
            _report.status = status;
            _report.error = error;
            Console.WriteLine($"[meshtrip] {bot.Name} trip {status}{(error != null ? ": " + error : "")}");
            MeshTripTest.Write(_report);
        }

        public override void Tick(PlayerBot bot)
        {
            // The step timer does the walking.
        }
    }
}
