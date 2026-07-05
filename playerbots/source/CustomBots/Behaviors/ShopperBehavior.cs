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

        public override string GetStatusLine(PlayerBot bot) => "browsing the shops";

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
