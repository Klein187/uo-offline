// =========================================================================
// SetLifecycleCommand.cs — [SetLifecycle true|false
//
// Toggles the global BotLifecycleManager. Off by default until you're
// ready to let bots start drifting between behaviors.
// =========================================================================

using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class SetLifecycleCommand
    {
        private static bool _registered;

        public static void Configure()
        {
            if (_registered) return;
            _registered = true;

            CommandSystem.Register("SetLifecycle",     AccessLevel.GameMaster, OnSetCommand);
            CommandSystem.Register("LifecycleStatus",  AccessLevel.GameMaster, OnStatusCommand);
        }

        [Usage("SetLifecycle true|false")]
        [Description("Toggle the BotLifecycleManager.")]
        public static void OnSetCommand(CommandEventArgs e)
        {
            if (e.Length < 1)
            {
                e.Mobile.SendMessage($"Lifecycle is currently {(BotLifecycleManager.Enabled ? "ON" : "OFF")}.");
                e.Mobile.SendMessage("Usage: [SetLifecycle true   or   [SetLifecycle false");
                return;
            }

            string arg = e.GetString(0).ToLowerInvariant();
            bool? v = arg switch
            {
                "true" or "1" or "on"  or "yes" => true,
                "false" or "0" or "off" or "no" => false,
                _ => null,
            };

            if (v == null)
            {
                e.Mobile.SendMessage($"Couldn't parse '{arg}'; use true/false.");
                return;
            }

            BotLifecycleManager.Enabled = v.Value;
            e.Mobile.SendMessage($"Lifecycle = {(v.Value ? "ON" : "OFF")}");
        }

        [Usage("LifecycleStatus")]
        [Description("Show lifecycle status and a few sample bot personalities.")]
        public static void OnStatusCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage($"Lifecycle enabled: {BotLifecycleManager.Enabled}");
            e.Mobile.SendMessage($"Tick interval: {BotLifecycleManager.TickInterval.TotalSeconds:F0}s");
            e.Mobile.SendMessage($"Max transitions per tick: {BotLifecycleManager.MaxTransitionsPerTick}");

            int assigned = 0, total = 0;
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted && bot.Map != Map.Internal)
                {
                    total++;
                    if (bot.Personality.IsAssigned) assigned++;
                }
            }
            e.Mobile.SendMessage($"Bots with personality: {assigned}/{total}");
        }
    }
}
