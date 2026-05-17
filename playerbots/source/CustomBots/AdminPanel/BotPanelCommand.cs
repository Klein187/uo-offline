// =========================================================================
// BotPanelCommand.cs — [BotPanel admin command. Opens the admin gump.
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
            CommandSystem.Register("BotPanel", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("BotPanel")]
        [Description("Opens the PlayerBot admin panel for streamlined GM operations.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null) return;

            from.SendGump(new BotPanelGump(from));
        }
    }
}
