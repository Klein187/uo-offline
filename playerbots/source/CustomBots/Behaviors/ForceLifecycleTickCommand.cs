// =========================================================================
// ForceLifecycleTickCommand.cs — [ForceLifecycleTick [bot_name]
//
// Manually trigger a lifecycle pass without waiting for the natural tick
// interval or the bot's phase duration. Useful for testing: see
// transitions happen now instead of in 30+ minutes.
//
// Usage:
//   [ForceLifecycleTick               — assign personalities to any bot
//                                        missing one, then transition every
//                                        bot that has personality
//   [ForceLifecycleTick BotName       — transition a specific bot by name
//                                        (assigns personality first if needed)
//
// Respects: requires AccessLevel.GameMaster. Ignores duration but still
// goes through the same TransitionBot logic so we see real-world behavior.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class ForceLifecycleTickCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("ForceLifecycleTick", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("ForceLifecycleTick [name]")]
        [Description("Run a lifecycle pass now, ignoring phase duration. Optional bot name targets one bot only.")]
        public static void OnCommand(CommandEventArgs e)
        {
            string targetName = e.Length >= 1 ? e.GetString(0) : null;

            // Snapshot the bots first — transitions can move bots around
            // which mutates World.Mobiles.
            var snapshot = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted && bot.Map != Map.Internal)
                {
                    if (targetName == null ||
                        string.Equals(bot.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        snapshot.Add(bot);
                    }
                }
            }

            if (snapshot.Count == 0)
            {
                e.Mobile.SendMessage(targetName != null
                    ? $"No bot named '{targetName}' found."
                    : "No bots found.");
                return;
            }

            // Soft warning when transitioning many bots at once: this can
            // cause client lag (lots of moves at once) and pileups (multiple
            // bots arriving at the same dungeon entrance).
            if (snapshot.Count > 10)
            {
                e.Mobile.SendMessage(
                    $"WARNING: forcing transitions on {snapshot.Count} bots. " +
                    $"Expect heavy teleport activity and possible pileups.");
            }

            int assigned = 0;
            int transitioned = 0;
            int errors = 0;

            foreach (var bot in snapshot)
            {
                if (bot.Deleted || bot.Map == Map.Internal) continue;

                try
                {
                    if (!bot.Personality.IsAssigned)
                    {
                        BotLifecycleManager.AssignPersonality(bot);
                        assigned++;
                    }

                    BotLifecycleManager.TransitionBot(bot);
                    transitioned++;
                }
                catch (Exception ex)
                {
                    errors++;
                    Console.WriteLine($"[ForceLifecycleTick] error on {bot.Name}: {ex.Message}");
                }
            }

            e.Mobile.SendMessage(
                $"ForceLifecycleTick: {transitioned} transitioned, " +
                $"{assigned} newly assigned personalities, {errors} errors.");
        }
    }
}
