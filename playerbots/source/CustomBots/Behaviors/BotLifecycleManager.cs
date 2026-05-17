// =========================================================================
// BotLifecycleManager.cs — Global tick that gives every bot a "life":
// periodically transitions them between behaviors (Banker, Adventurer,
// Traveler, etc) based on their personality.
//
// Tick rate: 60 seconds. Each tick:
//   1. Snapshot all PlayerBots (defensive against mid-loop mutations).
//   2. For each bot:
//      - If no personality yet, assign one and stamp PhaseStartedAt to now.
//        First-seen bots get a fresh phase clock — they won't immediately
//        transition. They serve their full initial phase first.
//      - If their phase duration has elapsed, transition them: pick a new
//        behavior weighted by personality, then place them appropriately.
//   3. Stagger transitions: limit how many bots can transition per tick
//      so we never get a cascade where 100 bots all teleport at once.
//
// Toggle with [SetLifecycle true/false. Off by default in case anything
// goes sideways on first deploy.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotLifecycleManager
    {
        // ---- Knobs ----

        // Global enable/disable. On by default — the world should feel
        // alive whenever the server is running. Toggle off with
        // [SetLifecycle false if you ever need to freeze transitions
        // (e.g. while debugging or doing bulk world edits).
        public static bool Enabled = true;

        // Verbose logs every assignment / transition / decision.
        public static bool Verbose = true;

        // How often the manager evaluates bots.
        public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

        // Max transitions per tick. Prevents the cascade-of-teleports
        // scenario where every overdue bot transitions simultaneously.
        public const int MaxTransitionsPerTick = 5;

        // ---- Internals ----

        private static Timer _timer;
        private static readonly List<PlayerBot> _scratch = new();

        public static void Configure()
        {
            // First tick after one full interval, recurring forever.
            // ModernUO discovers SetLifecycleCommand.Configure independently
            // and registers its commands.
            _timer = Timer.DelayCall(TickInterval, TickInterval, () => OnTick());
        }

        // -------------------------------------------------------------------
        // Tick.
        // -------------------------------------------------------------------
        private static void OnTick()
        {
            if (!Enabled) return;

            // Snapshot. Iterating World.Mobiles.Values live can throw if
            // anything during a transition mutates the dictionary (death,
            // teleport, spawner add).
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

            int transitions = 0;

            for (int i = 0; i < _scratch.Count; i++)
            {
                var bot = _scratch[i];
                if (bot.Deleted || bot.Map == Map.Internal) continue;

                // First sight: assign personality + stamp phase clock.
                if (!bot.Personality.IsAssigned)
                {
                    AssignPersonality(bot);
                    continue;  // don't immediately transition this same tick
                }

                // Skip if cap reached.
                if (transitions >= MaxTransitionsPerTick) continue;

                // Has the current phase duration elapsed?
                var elapsed = Core.Now - bot.PhaseStartedAt;
                if (elapsed < bot.Personality.AveragePhaseDuration)
                    continue;

                // Time to transition.
                try
                {
                    TransitionBot(bot);
                    transitions++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Lifecycle] transition error on {bot.Name}: {ex.Message}");
                }
            }

            _scratch.Clear();
        }

        // -------------------------------------------------------------------
        // Pick the next behavior for the bot based on personality, then
        // place them and swap their Behavior.
        //
        // internal so the [ForceLifecycleTick command can drive this directly
        // without re-implementing transition logic.
        // -------------------------------------------------------------------
        internal static void AssignPersonality(PlayerBot bot)
        {
            bot.Personality = BotPersonality.RollRandom();
            bot.PhaseStartedAt = Core.Now;
            Log($"[{bot.Name}] personality assigned: {bot.Personality}");
        }

        internal static void TransitionBot(PlayerBot bot)
        {
            var p = bot.Personality;
            string current = bot.Behavior?.SerializableName ?? "Idle";
            string target  = PickNextBehavior(p, current);

            if (target == current)
            {
                // Same behavior re-picked. Just refresh the phase clock; no
                // teleport needed, just "I want to keep doing this."
                bot.PhaseStartedAt = Core.Now;
                Log($"[{bot.Name}] phase refresh: continues as {current}");
                return;
            }

            // Place the bot appropriately for the new behavior.
            string placement = LifecycleTransitions.ApplyPlacement(bot, target);

            // Swap the brain. Behavior's setter resets PhaseStartedAt for us.
            bot.Behavior = BehaviorRegistry.Create(target);

            Log($"[{bot.Name}] transition: {current} -> {target} ({placement})");
        }

        // -------------------------------------------------------------------
        // Weighted random pick among the bot's tendencies. Heavily favors
        // changing to a DIFFERENT behavior than the current one (gives a
        // 50% boost to all non-current weights).
        // -------------------------------------------------------------------
        private static string PickNextBehavior(BotPersonality p, string current)
        {
            // Tuple list to keep the order deterministic.
            var weights = new List<(string name, double w)>
            {
                ("BankSitter", p.BankerTendency),
                ("Adventurer", p.AdventurerTendency),
                ("Traveler",   p.TravelerTendency),
                ("Wander",     p.WanderTendency),
                ("Idle",       p.IdleTendency),
            };

            double total = 0;
            for (int i = 0; i < weights.Count; i++)
            {
                double w = Math.Max(weights[i].w, 0.01);  // floor so no zero
                // Bias against staying in current behavior.
                if (weights[i].name == current) w *= 0.5;
                weights[i] = (weights[i].name, w);
                total += w;
            }

            if (total <= 0) return current;

            double r = Utility.RandomDouble() * total;
            double acc = 0;
            foreach (var (name, w) in weights)
            {
                acc += w;
                if (r <= acc) return name;
            }
            return current;
        }

        private static void Log(string msg)
        {
            if (!Verbose) return;
            Console.WriteLine($"[Lifecycle] {msg}");
        }
    }
}
