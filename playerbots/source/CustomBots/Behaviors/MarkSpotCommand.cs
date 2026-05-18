// =========================================================================
// MarkSpotCommand.cs — [MarkSpot <name...> <type>
//
// Records the GM's current position as a destination candidate. Writes
// a JSON-ready snippet to Distribution/destinations-draft.txt, ready
// to paste into destinations.json.
//
// Usage in-game:
//   [MarkSpot Britain Smithy Forge VendorSmith
//   [MarkSpot Britain Bank Counter Bank
//   [MarkSpot Britain Tavern Bar Tavern
//
// The last argument is always the type. Everything before is the name.
// Auto-detects the nearest waypoint (using the current WaypointGraph).
//
// At end of session, paste destinations-draft.txt content into the
// "Destinations" array in destinations.json, then [ReloadDestinations.
// =========================================================================

using System;
using System.IO;
using System.Text;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class MarkSpotCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("MarkSpot", AccessLevel.GameMaster, OnCommand);
            CommandSystem.Register("MarkSpotClear", AccessLevel.GameMaster, OnClearCommand);
            CommandSystem.Register("MarkSpotShow",  AccessLevel.GameMaster, OnShowCommand);
        }

        [Usage("MarkSpot <name words...> <Type>")]
        [Description("Append a destination at the current position to destinations-draft.txt.")]
        public static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null) return;

            if (e.Length < 2)
            {
                from.SendMessage("Usage: [MarkSpot <name words...> <Type>");
                from.SendMessage("Example: [MarkSpot Britain Smithy Forge VendorSmith");
                SendTypeList(from);
                return;
            }

            // Last arg is Type, everything else is name (joined by spaces).
            string typeArg = e.GetString(e.Length - 1);
            if (!Enum.TryParse<DestinationType>(typeArg, ignoreCase: true, out var type))
            {
                from.SendMessage($"Unknown type '{typeArg}'.");
                SendTypeList(from);
                return;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < e.Length - 1; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(e.GetString(i));
            }
            string name = sb.ToString().Trim();
            if (name.Length == 0)
            {
                from.SendMessage("Name cannot be empty.");
                return;
            }

            // Find nearest waypoint (so user doesn't have to know).
            string nearestWp = "";
            int wpDist = -1;
            var graph = WaypointRegistry.Graph;
            if (graph != null && graph.NodeCount > 0)
            {
                var nearest = graph.FindNearestNode(from.Location);
                if (nearest != null)
                {
                    nearestWp = nearest.Name;
                    int dx = from.X - nearest.Location.X;
                    int dy = from.Y - nearest.Location.Y;
                    wpDist = (int)Math.Sqrt(dx * dx + dy * dy);
                }
            }

            // Build JSON-ready snippet.
            string snippet =
                $"    {{\n" +
                $"      \"Name\": \"{Escape(name)}\",\n" +
                $"      \"X\": {from.X}, \"Y\": {from.Y}, \"Z\": {from.Z},\n" +
                $"      \"Type\": \"{type}\",\n" +
                $"      \"City\": \"\",\n" +
                $"      \"NearestWaypoint\": \"{Escape(nearestWp)}\"\n" +
                $"    }},\n";

            string draftPath = Path.Combine(Core.BaseDirectory, "destinations-draft.txt");
            try
            {
                File.AppendAllText(draftPath, snippet);
            }
            catch (Exception ex)
            {
                from.SendMessage($"Write failed: {ex.Message}");
                return;
            }

            // Confirm in chat with the same info, plus a warning if the
            // nearest waypoint is farther than the safe A* range.
            from.SendMessage(0x35, $"Marked: {name} [{type}]");
            from.SendMessage(0x3B2,
                $"  ({from.X}, {from.Y}, {from.Z}) | nearest wp: {nearestWp} ({wpDist} tiles)");
            if (wpDist > 30)
            {
                from.SendMessage(0x22,
                    $"  WARNING: nearest waypoint is {wpDist} tiles — bots may fail to path here. Add a closer waypoint.");
            }
        }

        // Wipe the draft file. Useful when starting a fresh session.
        [Usage("MarkSpotClear")]
        [Description("Clear destinations-draft.txt.")]
        public static void OnClearCommand(CommandEventArgs e)
        {
            string draftPath = Path.Combine(Core.BaseDirectory, "destinations-draft.txt");
            try
            {
                File.WriteAllText(draftPath, "");
                e.Mobile.SendMessage("Cleared destinations-draft.txt");
            }
            catch (Exception ex)
            {
                e.Mobile.SendMessage($"Clear failed: {ex.Message}");
            }
        }

        // Echo the file's contents in chat (last N lines).
        [Usage("MarkSpotShow")]
        [Description("Show the recent contents of destinations-draft.txt.")]
        public static void OnShowCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            string draftPath = Path.Combine(Core.BaseDirectory, "destinations-draft.txt");
            if (!File.Exists(draftPath))
            {
                from.SendMessage("destinations-draft.txt is empty / missing.");
                return;
            }

            try
            {
                var lines = File.ReadAllLines(draftPath);
                int from_idx = Math.Max(0, lines.Length - 40);
                from.SendMessage(0x35, $"destinations-draft.txt (last {lines.Length - from_idx} of {lines.Length} lines):");
                for (int i = from_idx; i < lines.Length; i++)
                {
                    from.SendMessage(0x3B2, lines[i]);
                }
            }
            catch (Exception ex)
            {
                from.SendMessage($"Read failed: {ex.Message}");
            }
        }

        private static void SendTypeList(Mobile m)
        {
            m.SendMessage(0x3B2, "Valid types: " + string.Join(", ",
                Enum.GetNames(typeof(DestinationType))));
        }

        private static string Escape(string s) => s.Replace("\"", "\\\"");
    }
}
