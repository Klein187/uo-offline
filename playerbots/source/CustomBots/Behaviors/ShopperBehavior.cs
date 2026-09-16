// =========================================================================
// ShopperBehavior.cs — a bot shopping at a vendor area.
//
// Arrival is zone-based, so by the time a bot becomes a Shopper it is
// already inside the vendor's painted area. What it does there depends on
// whether the shop floor has been drawn:
//
//   Drawn vendor Area   The bot moves. It wanders the floor, pauses to
//                       look at wares, and once or twice per visit walks
//                       up to the vendor, says "vendor buy", and buys
//                       something real (BotVendorPurchase). Every step
//                       stays inside the polygon.
//
//   No area             The old behaviour: stand still, say vendor lines,
//                       shift facing now and then. No walking, no wall
//                       grinding.
//
// The visit timer and the Traveler handoff are unchanged either way.
// =========================================================================

using System;
using Server;
using Server.Mobiles;
using MoveDelays = Server.Movement.Movement;

namespace Server.CustomBots
{
    public class ShopperBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Shopper";

        public override string GetStatusLine(PlayerBot bot) => _phase switch
        {
            Phase.ToVendor => _vendor != null ? $"walking to {_vendor.Name}" : "looking for the vendor",
            Phase.Buying   => _vendor != null ? $"buying from {_vendor.Name}" : "buying",
            _              => _area != null ? $"browsing {_area.Name}" : "browsing the shops",
        };

        public override Point3D? NavGoal(PlayerBot bot) =>
            _stepTimer != null && _walker != null ? _walker.GetGoalLocation() : null;
        public override ILegFollower ActiveLegFollower => _stepTimer != null ? _walker : null;

        // Speech range UO vendors respond within; informational only here.
        public int VendorSpeakRange { get; set; } = 3;

        // ---- Spawn-pinned visit window ----
        //
        // Shoppers that arrive ORGANICALLY (a Traveler reaching a vendor)
        // already get a VisitExpiresAt stamped by the handoff, so they break
        // off and travel again after a minute or two. But shoppers PINNED at
        // vendor spots by [GenerateBots spawn straight into this behavior with
        // no timer — left alone they'd shop forever (until the slow 30-180min
        // lifecycle clock moves them).
        //
        // PlayerBot.OnAfterSpawn stamps a visit using this window so pinned
        // shoppers also break off into the roaming pool. The whole pinned
        // crowd spawns in the same instant, so the window is kept wide enough
        // to stagger their departures instead of emptying every vendor at
        // once. By the time they disperse, organic Traveler arrivals are
        // flowing in to keep the shops populated.
        public static TimeSpan SpawnVisitMin = TimeSpan.FromSeconds(30);
        public static TimeSpan SpawnVisitMax = TimeSpan.FromMinutes(5);

        public Point3D Home { get; private set; }
        public Map     HomeMap { get; private set; }

        private enum Phase { Browse, ToVendor, Buying }
        private Phase _phase = Phase.Browse;

        // The drawn shop floor, or null for the stand-still shopper.
        private PaintedZone _area;
        private BaseVendor _vendor;

        // Walking inside the area.
        private ILegFollower _walker;
        private Timer _stepTimer;
        private DateTime _walkStarted;
        private int _walkRange;
        private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(20);

        // Counters for the log line on the way out.
        private int _wanders;
        private int _purchases;
        private int _buysLeft;
        private DateTime _buyAt;

        // Stand-still "examining wares" window.
        private DateTime _examineUntil = DateTime.MinValue;

        // Cadence for saying a vendor trigger line.
        private DateTime _nextVendorLine = DateTime.MinValue;
        private static readonly TimeSpan VendorLineMin = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan VendorLineMax = TimeSpan.FromSeconds(28);

        private static readonly string[] VendorTriggers =
        {
            "vendor buy", "vendor buy", "vendor sell", "vendor view",
            "show me your wares", "i'd like to see what you have",
            "let me see your goods",
        };

        private static readonly string[] ThanksLines = { "ty", "thx", "cheers", "k ty" };
        private static readonly string[] BrokeLines = { "too rich for me", "nm", "just looking", "maybe later" };

        public ShopperBehavior()
        {
            ChatCategories  = new[] { "shopping", "small_talk" };
            ChatChance      = 0.18;
            MinChatCooldown = TimeSpan.FromSeconds(20);
            MaxChatCooldown = TimeSpan.FromSeconds(50);
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            Home    = bot.Location;
            HomeMap = bot.Map;
            // First vendor line shortly after arriving.
            _nextVendorLine = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(2, 6));

            // The drawn floor under the bot, or one a few tiles away: a
            // handoff on the doorstep still counts, the first wander goal
            // walks the bot in.
            var area = ZoneRegistry.AreaAt(bot.X, bot.Y);
            if (area == null || !area.IsVendorArea)
            {
                area = null;
                foreach (var z in ZoneRegistry.All)
                {
                    if (z.IsVendorArea &&
                        bot.X >= z.MinX - 3 && bot.X <= z.MaxX + 3 &&
                        bot.Y >= z.MinY - 3 && bot.Y <= z.MaxY + 3 &&
                        (area == null || z.Area < area.Area))
                    {
                        area = z;
                    }
                }
            }
            _area = area;
            if (_area != null)
            {
                _buysLeft = Utility.RandomMinMax(1, 2);
                _buyAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(8, 30));
            }
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopStepTimer();
            if (_area != null)
            {
                Console.WriteLine($"[shopper] {bot.Name} left {_area.Name}: {_wanders} wander(s), {_purchases} purchase(s)");
            }
            base.OnDetached(bot);
        }

        private void ScheduleNextVendorLine()
        {
            int s = Utility.RandomMinMax((int)VendorLineMin.TotalSeconds,
                                         (int)VendorLineMax.TotalSeconds);
            _nextVendorLine = Core.Now + TimeSpan.FromSeconds(s);
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal) return;

            // Timed visit — when shopping ends, the base swaps back to Traveler.
            if (CheckVisitExpired(bot)) return;

            // Browsing chatter.
            TrySpeak(bot);

            if (_area != null)
            {
                TickInArea(bot);
                return;
            }

            // ---- No drawn floor: the stand-still shopper ----

            // Say a vendor trigger line on its own cadence — this is the
            // "shopping" action when there's no walking to a counter.
            if (Core.Now >= _nextVendorLine)
            {
                ScheduleNextVendorLine();
                var line = VendorTriggers[Utility.Random(VendorTriggers.Length)];
                try { bot.Say(line); } catch { }
                _examineUntil = Core.Now +
                    TimeSpan.FromSeconds(Utility.RandomMinMax(4, 8));
                return;
            }

            // "Examining wares" — stand still through the pause window.
            if (Core.Now < _examineUntil) return;

            // Occasional facing shift, as if looking over goods. No walking.
            if (Utility.RandomDouble() < 0.15)
            {
                bot.Direction = (Direction)Utility.Random(8);
                _examineUntil = Core.Now +
                    TimeSpan.FromSeconds(Utility.RandomMinMax(4, 10));
            }
        }

        // ---- The moving shopper ----

        private void TickInArea(PlayerBot bot)
        {
            // Mid-walk: the step timer is doing the work. Only watch the clock.
            if (_stepTimer != null)
            {
                if (Core.Now - _walkStarted > WalkTimeout)
                {
                    StopStepTimer();
                    if (_phase == Phase.ToVendor)
                    {
                        // Could not reach the counter this time. Try again
                        // later if a buy is still owed.
                        _phase = Phase.Browse;
                        _buyAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(15, 30));
                    }
                    _examineUntil = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(3, 6));
                }
                return;
            }

            if (_phase == Phase.Buying)
            {
                return;   // the purchase timer will hand back to Browse
            }

            if (Core.Now < _examineUntil)
            {
                return;
            }

            // Time to buy? Walk to the vendor.
            if (_buysLeft > 0 && Core.Now >= _buyAt)
            {
                _vendor = BotVendorPurchase.FindVendor(_area, bot.Map);
                if (_vendor == null)
                {
                    _buysLeft = 0;   // nobody to buy from; keep browsing
                }
                else
                {
                    _phase = Phase.ToVendor;
                    StartWalk(bot, _vendor.Location, range: 2, useZones: null);
                    return;
                }
            }

            // Otherwise wander to another spot on the floor.
            var goal = _area.RandomStandable(bot.Map, bot.Z);
            if (goal.HasValue && Math.Max(Math.Abs(goal.Value.X - bot.X), Math.Abs(goal.Value.Y - bot.Y)) >= 2)
            {
                _wanders++;
                _phase = Phase.Browse;
                StartWalk(bot, goal.Value, range: 0, useZones: true);
                return;
            }

            // Nowhere to go this tick: look over the goods where we stand.
            bot.Direction = (Direction)Utility.Random(8);
            _examineUntil = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(4, 10));
        }

        private void StartWalk(PlayerBot bot, Point3D goal, int range, bool? useZones)
        {
            _walker = LegFollowers.Create(bot, goal, useZones);
            _walkRange = range;
            _walkStarted = Core.Now;
            StopStepTimer();
            var interval = TimeSpan.FromMilliseconds(MoveDelays.WalkFootDelay);
            _stepTimer = Timer.DelayCall(interval, interval, () => StepOnce(bot));
        }

        private void StopStepTimer()
        {
            _stepTimer?.Stop();
            _stepTimer = null;
        }

        private void StepOnce(PlayerBot bot)
        {
            if (bot.Deleted || bot.Map == null || bot.Map == Map.Internal ||
                !ReferenceEquals(bot.Behavior, this) || _walker == null)
            {
                StopStepTimer();
                return;
            }

            bool arrived;
            try
            {
                arrived = _walker.Follow(false, _walkRange);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[shopper] {bot.Name} step failed: {ex.Message}");
                StopStepTimer();
                return;
            }
            if (!arrived)
            {
                return;
            }

            StopStepTimer();
            if (_phase == Phase.ToVendor)
            {
                AtTheCounter(bot);
            }
            else
            {
                // Arrived at a browsing spot: stop and look.
                bot.Direction = (Direction)Utility.Random(8);
                _examineUntil = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(4, 10));
            }
        }

        private void AtTheCounter(PlayerBot bot)
        {
            _phase = Phase.Buying;
            if (_vendor != null && !_vendor.Deleted)
            {
                bot.Direction = bot.GetDirectionTo(_vendor);
            }
            try { bot.Say("vendor buy"); } catch { }

            // The gump would take a moment to read; so does the bot.
            Timer.DelayCall(TimeSpan.FromMilliseconds(1500), () =>
            {
                if (bot.Deleted || !ReferenceEquals(bot.Behavior, this))
                {
                    return;
                }
                _buysLeft--;
                string what = null;
                try
                {
                    what = BotVendorPurchase.TryBuy(bot, _vendor, _area);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[vendor] {bot.Name} purchase failed: {ex.Message}");
                }
                if (what != null)
                {
                    _purchases++;
                    if (Utility.RandomDouble() < 0.5)
                    {
                        TrySpeakLine(bot, ThanksLines[Utility.Random(ThanksLines.Length)]);
                    }
                }
                else if (Utility.RandomDouble() < 0.5)
                {
                    TrySpeakLine(bot, BrokeLines[Utility.Random(BrokeLines.Length)]);
                }
                _phase = Phase.Browse;
                _buyAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(25, 60));
                _examineUntil = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(3, 6));
            });
        }
    }
}
