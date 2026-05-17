// =========================================================================
// ReloadWaypointsCommand.cs — [ReloadWaypoints
// Hot-reloads the waypoint graph from JSON. Useful for iterating on the
// graph without restarting the server.
// =========================================================================

using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class ReloadWaypointsCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("ReloadWaypoints", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("ReloadWaypoints")]
        [Description("Reloads the bot waypoint graph from Data/Waypoints/waypoints.json.")]
        public static void OnCommand(CommandEventArgs e)
        {
            int n = WaypointRegistry.Load();
            e.Mobile.SendMessage($"Reloaded waypoint graph: {n} node(s).");
        }
    }
}
