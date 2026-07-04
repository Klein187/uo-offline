// =========================================================================
// BotSceneRunner.cs — timed multi-actor theater beats.
//
// A "scene" is a short choreographed exchange between bots: a trade that
// concludes ("how much for a katana" → "800gp" → *hands over coins* →
// "sold!"), a duel challenge, a gatherer delivering ingots. Each beat is
// (delay-after-previous, actor, line); lines starting with '*' render as
// real Emotes. Actors are re-checked at fire time so a bot that died or
// logged out mid-scene just goes quiet instead of crashing the beat.
//
// Scenes deliberately speak WITHOUT the player-nearby gate — they're the
// visible economy/social theater, and the cost of an unseen Say is nil.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public static class BotScene
    {
        public static void Play(params (double delay, PlayerBot actor, string line)[] beats)
        {
            double t = 0;
            foreach (var beat in beats)
            {
                t += beat.delay;
                var actor = beat.actor;
                var line = beat.line;
                if (actor == null || string.IsNullOrEmpty(line))
                {
                    continue;
                }
                Timer.DelayCall(TimeSpan.FromSeconds(t), () => Deliver(actor, line));
            }
        }

        public static void Deliver(PlayerBot bot, string line)
        {
            if (bot == null || bot.Deleted || !bot.Alive ||
                bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            if (line.Length > 1 && line[0] == '*')
            {
                bot.Emote(line.Trim('*', ' '));
            }
            else
            {
                bot.Say(line);
            }
        }

        // Pick a line from a chat category, with optional token substitution.
        public static string Pick(string category, string token = null, string value = null)
        {
            var line = ChatLibrary.PickRandom(category);
            if (line == null)
            {
                return null;
            }
            if (token != null && value != null)
            {
                line = line.Replace(token, value, StringComparison.Ordinal);
            }
            return line;
        }
    }
}
