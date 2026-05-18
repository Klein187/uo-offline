// =========================================================================
// MarkWayCommand.cs — [MarkWay <name...>
//
// Records the GM's current position as a waypoint candidate. Writes a
// JSON-ready snippet to Distribution/waypoints-draft.txt, ready to paste
// into waypoints.json's Waypoints array.
//
// Auto-detects neighbor waypoints within 38 tiles (PathFollower's A*
// range) and adds them to the Connects array. Bidirectional connection
// is handled by the WaypointRegistry loader, so we only need to list
// neighbors here.
//
// Usage in-game:
//   [MarkWay Britain North Road A
//   [MarkWay Trinsic Gate Approach
//
// All args become the name (joined by spaces).
//
// At end of session, paste waypoints-draft.txt content into the
// "Waypoints" array in waypoints.json, then [ReloadWaypoints.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class MarkWayCommand
    {
        // PathFollower's A* range. We auto-connect to any existing waypoint
        // within this distance.
        private const int NeighborRange = 38;

        public static void Configure()
        {
            CommandSystem.Register("MarkWay",      AccessLevel.GameMaster, OnCommand);
            CommandSystem.Register("MarkWayClear", AccessLevel.GameMaster, OnClearCommand);
            CommandSystem.Register("MarkWayShow",  AccessLevel.GameMaster, OnShowCommand);
        }

        [Usage("MarkWay <name words...>")]
        [Description("Append a waypoint at the current position to waypoints-draft.txt.")]
        public static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null) return;

            if (e.Length < 1)
            {
                from.SendMessage("Usage: [MarkWay <name words...>");
                from.SendMessage("Example: [MarkWay Britain North Road A");
                return;
            }

            // All args = the name.
            var sb = new StringBuilder();
            for (int i = 0; i < e.Length; i++)
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

            // Auto-detect neighbor waypoints within NeighborRange.
            var neighbors = new List<(string n, int d)>();
            var graph = WaypointRegistry.Graph;
            if (graph != null)
            {
                foreach (string wpName in graph.AllNames)
                {
                    var node = graph.Get(wpName);
                    if (node == null) continue;
                    int dx = from.X - node.Location.X;
                    int dy = from.Y - node.Location.Y;
                    int dist = (int)Math.Sqrt(dx * dx + dy * dy);
                    if (dist <= NeighborRange)
                    {
                        neighbors.Add((wpName, dist));
                    }
                }
            }

            // Sort by distance — closer first reads more naturally.
            neighbors.Sort((a, b) => a.d.CompareTo(b.d));

            // Build Connects array literal.
            string connects;
            if (neighbors.Count == 0)
            {
                connects = "[]";
            }
            else
            {
                var quoted = new List<string>(neighbors.Count);
                foreach (var (n, _) in neighbors) quoted.Add($"\"{Escape(n)}\"");
                connects = "[" + string.Join(", ", quoted) + "]";
            }

            // Build JSON-ready snippet.
            string snippet =
                $"    {{\n" +
                $"      \"Name\": \"{Escape(name)}\",\n" +
                $"      \"X\": {from.X}, \"Y\": {from.Y}, \"Z\": {from.Z},\n" +
                $"      \"Connects\": {connects}\n" +
                $"    }},\n";

            string draftPath = Path.Combine(Core.BaseDirectory, "waypoints-draft.txt");
            try
            {
                File.AppendAllText(draftPath, snippet);
            }
            catch (Exception ex)
            {
                from.SendMessage($"Write failed: {ex.Message}");
                return;
            }

            // Confirm with helpful diagnostics.
            from.SendMessage(0x35, $"Marked waypoint: {name}");
            from.SendMessage(0x3B2,
                $"  ({from.X}, {from.Y}, {from.Z})");
            if (neighbors.Count == 0)
            {
                from.SendMessage(0x22,
                    "  WARNING: no existing waypoint within 38 tiles. This waypoint will be isolated " +
                    "until another waypoint is added near it.");
            }
            else
            {
                var parts = new List<string>();
                foreach (var (n, d) in neighbors) parts.Add($"{n} ({d}t)");
                from.SendMessage(0x3B2, "  Auto-connects to: " + string.Join(", ", parts));
            }
        }

        [Usage("MarkWayClear")]
        [Description("Clear waypoints-draft.txt.")]
        public static void OnClearCommand(CommandEventArgs e)
        {
            string draftPath = Path.Combine(Core.BaseDirectory, "waypoints-draft.txt");
            try
            {
                File.WriteAllText(draftPath, "");
                e.Mobile.SendMessage("Cleared waypoints-draft.txt");
            }
            catch (Exception ex)
            {
                e.Mobile.SendMessage($"Clear failed: {ex.Message}");
            }
        }

        [Usage("MarkWayShow")]
        [Description("Show recent contents of waypoints-draft.txt.")]
        public static void OnShowCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            string draftPath = Path.Combine(Core.BaseDirectory, "waypoints-draft.txt");
            if (!File.Exists(draftPath))
            {
                from.SendMessage("waypoints-draft.txt is empty / missing.");
                return;
            }

            try
            {
                var lines = File.ReadAllLines(draftPath);
                int from_idx = Math.Max(0, lines.Length - 40);
                from.SendMessage(0x35, $"waypoints-draft.txt (last {lines.Length - from_idx} of {lines.Length} lines):");
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

        private static string Escape(string s) => s.Replace("\"", "\\\"");
    }
}
