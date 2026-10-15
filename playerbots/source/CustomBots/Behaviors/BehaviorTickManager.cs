// =========================================================================
// BehaviorTickManager.cs — Global timer that ticks all PlayerBots.
//
// Wakes up every TickInterval and calls Behavior.Tick() on every active
// PlayerBot in the world. One timer for all bots — much cheaper than one
// timer per bot, and avoids ordering issues during world saves.
//
// We walk World.Mobiles each tick rather than maintaining our own
// registration list. With <500 bots this is trivially cheap; we can
// switch to a registration list later if perf demands it.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BehaviorTickManager
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
        private static Timer _timer;

        // Reusable buffer for snapshot — saves on GC churn since we'd
        // otherwise allocate a new array every 2 seconds.
        private static readonly List<PlayerBot> _scratch = new();

        public static void Configure()
        {
            BehaviorRegistry.Configure();
            _timer = Timer.DelayCall(TickInterval, TickInterval, 0, OnTick);
        }

        private static void OnTick()
        {
            // SNAPSHOT first. Iterating World.Mobiles.Values directly throws
            // "Collection was modified" if anything during a Tick adds or
            // removes a Mobile (bot death + spawner replacement, teleporter
            // step, monster spawn from aggression, etc). Copy bot references
            // into a scratch list, then iterate that.
            _scratch.Clear();
            foreach (var mobile in World.Mobiles.Values)
            {
                if (mobile is PlayerBot bot &&
                    !bot.Deleted &&
                    bot.Map != Map.Internal)
                {
                    _scratch.Add(bot);
                }
            }

            for (int i = 0; i < _scratch.Count; i++)
            {
                var bot = _scratch[i];
                // Re-check after snapshot — bot could have been deleted by
                // a prior iteration's side effect.
                if (bot.Deleted || bot.Map == Map.Internal)
                {
                    continue;
                }

                try
                {
                    // A real player may have invited this bot to a party
                    // since the last tick — answer before acting.
                    BotPlayerParty.CheckInvite(bot);
                    bot.Behavior?.Tick(bot);

                    // Judged from OUTSIDE the brain, after it has had its
                    // say: is this bot actually getting closer to wherever
                    // it claims to be going? Every other stuck detector
                    // belongs to one behavior and counts its own attempts,
                    // so a bot that keeps retrying resets them all and jams
                    // in silence. See BotNavWatch.
                    BotNavWatch.Observe(bot);
                }
                catch (Exception ex)
                {
                    // The top of the stack says which code it was; the
                    // message alone ("collection was modified") does not.
                    var where = ex.StackTrace?.Split('\n', 3) ?? new[] { "?" };
                    var top = string.Join(" <- ", where[..Math.Min(2, where.Length)]).Replace("\r", "").Trim();
                    Console.WriteLine($"PlayerBot tick error on {bot.Name} ({bot.Behavior?.SerializableName}): " +
                        $"{ex.Message} at {top}");
                }
            }

            _scratch.Clear();  // release references so bots can be GC'd
        }
    }
}
