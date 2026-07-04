// =========================================================================
// BankSitterBehavior.cs — Stands at a fixed spot, chats heavily.
//
// The bank-crowd archetype. Bots with this behavior:
//   - Capture their starting location as home (in OnAttached)
//   - Almost never move. If shoved off their tile by another mobile,
//     drift back over the next few ticks.
//   - Chat frequently from bank-relevant categories: WTS, WTB, LFG,
//     bank_actions, small_talk
//
// Use this for bots placed at banks, market squares, gathering spots —
// anywhere you want crowd buzz without movement chaos.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public class BankSitterBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "BankSitter";

        // How far the bot is allowed to drift from home before walking
        // back. 1 tile = "I got shoved" tolerance.
        public int HomeRadius { get; set; } = 1;

        public Point3D Home { get; private set; }
        public Map HomeMap   { get; private set; }

        public BankSitterBehavior()
        {
            // Bank-crowd chatter: everything trade-related plus small talk.
            // bank_actions are short ("bank", "withdraw 1000") so they land
            // hard; WTS/WTB are the meat; LFG appears here because banks
            // were historically where you'd find groups.
            ChatCategories = new[]
            {
                "small_talk",
                "bank_actions",
                "wts",
                "wtb",
                "lfg"
            };

            // Higher chat chance and faster cooldown than the wandering
            // archetype — banks are loud places.
            ChatChance      = 0.25;
            MinChatCooldown = TimeSpan.FromSeconds(15);
            MaxChatCooldown = TimeSpan.FromSeconds(45);
        }

        // How far from the arrival point a bank bot may scatter when it
        // settles in. Bank buildings are large; without this every bot
        // homes on the exact tile it arrived at and the crowd stacks.
        public int ScatterRadius { get; set; } = 5;

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            HomeMap = bot.Map;
            Home    = PickScatteredHome(bot);

            // Gear progression (IDEAS 4.3): dungeon runs pay. Three
            // survived runs and this bank visit becomes shopping day —
            // tier promotion, fresh skills, visibly better kit. Regulars
            // you keep seeing at the bank get better gear over time.
            if (bot.DungeonRunsSurvived >= 3 &&
                bot.SkillTier < BotSkillTier.Grandmaster)
            {
                bot.DungeonRunsSurvived = 0;
                bot.SkillTier++;
                bot.ReinitializeAsClass(bot.Class);
                bot.EquipFactionShield();
                var line = ChatLibrary.PickRandom("gear_up");
                if (!string.IsNullOrEmpty(line))
                {
                    bot.Say(line);
                }
                Console.WriteLine(
                    $"[gear] {bot.Name} promoted to {bot.SkillTier} (dungeon runs paid off)");
            }
        }

        // Pick a home tile near the arrival point instead of standing
        // exactly where we landed — spreads the bank crowd out across the
        // building. Tries a handful of random nearby tiles, takes the
        // first that the bot can actually stand on; falls back to the
        // arrival tile if none pan out.
        private Point3D PickScatteredHome(PlayerBot bot)
        {
            var arrival = bot.Location;
            var map     = bot.Map;
            if (map == null || map == Map.Internal) return arrival;

            for (int attempt = 0; attempt < 12; attempt++)
            {
                int ox = Utility.RandomMinMax(-ScatterRadius, ScatterRadius);
                int oy = Utility.RandomMinMax(-ScatterRadius, ScatterRadius);
                if (ox == 0 && oy == 0) continue;

                int nx = arrival.X + ox;
                int ny = arrival.Y + oy;
                int nz = arrival.Z;

                // CanFit checks the tile is walkable and the bot's body
                // fits there (no wall, no other blocker).
                if (map.CanFit(nx, ny, nz, 16, false, false))
                {
                    return new Point3D(nx, ny, nz);
                }
            }

            // Nothing walkable found nearby — just stay where we landed.
            return arrival;
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            // If this is a timed destination visit, return to traveling
            // once the visit window is up.
            if (CheckVisitExpired(bot)) return;

            // Speak first; chatter is the whole point of this behavior.
            TrySpeak(bot);

            // If we got shoved off our home tile, walk back. One step
            // per tick toward home until we're there.
            var dx = bot.Location.X - Home.X;
            var dy = bot.Location.Y - Home.Y;
            if (dx == 0 && dy == 0)
            {
                return;
            }

            var distSquared = dx * dx + dy * dy;
            if (distSquared <= HomeRadius * HomeRadius)
            {
                return;
            }

            var d = bot.GetDirectionTo(Home);
            if (bot.Direction != d)
            {
                bot.Direction = d;
            }
            bot.Move(d);
        }
    }
}
