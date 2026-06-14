// =========================================================================
// ShopperBehavior.cs — a bot shopping at a vendor area.
//
// SIMPLIFIED: arrival is zone-based, so by the time a bot becomes a
// Shopper it is ALREADY inside the vendor's painted area — exactly where
// it needs to be. So the Shopper does NOT hunt for a vendor mobile, does
// NOT path anywhere, and does NOT touch doors. It simply stands in the
// area, says vendor trigger lines ("vendor buy", etc.) and browsing
// chatter now and then, shifts facing occasionally as if examining wares,
// and returns to traveling when the timed visit expires.
//
// No movement = no wall-grinding. The bot shops by being present and
// speaking, which is all an idle, living-world vendor visit needs.
// =========================================================================

using System;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public class ShopperBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Shopper";

        // Speech range UO vendors respond within; informational only here.
        public int VendorSpeakRange { get; set; } = 3;

        public Point3D Home { get; private set; }
        public Map     HomeMap { get; private set; }

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

            // Say a vendor trigger line on its own cadence — this is the
            // "shopping" action now that there's no walking to a counter.
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
    }
}
