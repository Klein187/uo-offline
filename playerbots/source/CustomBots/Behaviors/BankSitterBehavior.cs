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

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            Home    = bot.Location;
            HomeMap = bot.Map;
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

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
