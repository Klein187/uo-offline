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
                    bot.Behavior?.Tick(bot);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PlayerBot tick error on {bot.Name}: {ex.Message}");
                }
            }

            _scratch.Clear();  // release references so bots can be GC'd
        }
    }
}
