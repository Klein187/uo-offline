// =========================================================================
// CombatDebugCommand.cs — [CombatDebug on|off
//
// Toggles AdventurerBehavior's verbose per-cast spell logging at runtime
// (it was a compile-time const; IDEAS 6.2 asked for a knob).
// =========================================================================

using System;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class CombatDebugCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("CombatDebug", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("CombatDebug [on|off]")]
        [Description("Toggles verbose bot combat/spell logging.")]
        private static void OnCommand(CommandEventArgs e)
        {
            if (e.Arguments.Length > 0)
            {
                AdventurerBehavior.CombatDebug =
                    string.Equals(e.Arguments[0], "on", StringComparison.OrdinalIgnoreCase);
            }
            e.Mobile.SendMessage(
                $"Combat debug logging: {(AdventurerBehavior.CombatDebug ? "ON" : "OFF")}.");
        }
    }
}
