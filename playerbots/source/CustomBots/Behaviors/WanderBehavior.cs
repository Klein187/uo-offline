// =========================================================================
// WanderBehavior.cs — Bot walks randomly within a home radius.
//
// On attach, remembers the bot's current location as home. On each tick:
//   1. Maybe say something (small_talk or traveling line)
//   2. Maybe move one step (random direction, biased toward home if far)
//
// Pair with ambient chat (built-in via TrySpeak) and the bot feels like
// a wandering NPC going about their day.
// =========================================================================

using System;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public class WanderBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Wander";

        // How far the bot is allowed to roam from its home point.
        public int HomeRadius { get; set; } = 8;

        // Chance per tick that the bot moves at all. Lower = more idle.
        public double MoveChance { get; set; } = 0.5;

        public Point3D Home { get; private set; }
        public Map HomeMap { get; private set; }

        public WanderBehavior()
        {
            // Wanderers pull from generic small-talk AND traveling lines.
            // Equal-weighted by category so traveling lines (which feel
            // more "in motion") aren't drowned out.
            ChatCategories = new[] { "small_talk", "traveling" };
            ChatChance     = 0.15;
        }

        public override void OnAttached(PlayerBot bot)
        {
            // IMPORTANT: call base first so chat cooldown gets randomized.
            base.OnAttached(bot);

            Home = bot.Location;
            HomeMap = bot.Map;
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            // Speak first, move second — feels more natural if a bot says
            // "i should head to britain" and THEN takes a step.
            TrySpeak(bot);

            // Movement chance gate. Most ticks the bot just stands.
            if (Utility.RandomDouble() > MoveChance)
            {
                return;
            }

            Direction d;
            var dx = bot.Location.X - Home.X;
            var dy = bot.Location.Y - Home.Y;
            var distSquared = dx * dx + dy * dy;

            if (distSquared > HomeRadius * HomeRadius)
            {
                // Outside home radius: head back.
                d = bot.GetDirectionTo(Home);
            }
            else
            {
                d = (Direction)Utility.Random(8);
            }

            if (bot.Direction != d)
            {
                bot.Direction = d;
            }
            bot.Move(d);
        }
    }
}
