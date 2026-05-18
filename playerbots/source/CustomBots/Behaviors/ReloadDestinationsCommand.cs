// =========================================================================
// ReloadDestinationsCommand.cs — [ReloadDestinations
//
// Hot-reload destinations.json without restarting the server.
// =========================================================================

using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class ReloadDestinationsCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("ReloadDestinations", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("ReloadDestinations")]
        [Description("Reloads Data/Destinations/destinations.json.")]
        public static void OnCommand(CommandEventArgs e)
        {
            try
            {
                int n = DestinationCatalog.Load();
                e.Mobile.SendMessage($"Reloaded destination catalog: {n} destination(s).");
            }
            catch (System.Exception ex)
            {
                e.Mobile.SendMessage($"Reload failed: {ex.Message}");
            }
        }
    }
}
