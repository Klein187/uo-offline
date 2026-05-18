// =========================================================================
// BotPanelCommand.cs — [GmPanel and [BotPanel admin commands.
// Both open the same admin gump. BotPanel kept as an alias for backward
// compatibility; GmPanel is the preferred name now that the panel handles
// general GM tasks beyond bots.
// =========================================================================

using Server;
using Server.Commands;
using Server.Gumps;

namespace Server.CustomBots
{
    public static class BotPanelCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("GmPanel",  AccessLevel.GameMaster, OnCommand);
            CommandSystem.Register("BotPanel", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("GmPanel")]
        [Description("Opens the GM admin panel for streamlined operations.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null) return;

            from.SendGump(new BotPanelGump(from));
        }
    }
}
