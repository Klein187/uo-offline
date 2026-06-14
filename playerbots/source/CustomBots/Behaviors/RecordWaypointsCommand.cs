// =========================================================================
// RecordWaypointsCommand.cs — auto-capture waypoints by walking.
//
// Instead of stopping every 20 tiles and typing [MarkWay, you start
// recording, walk a route at normal speed, and the command samples your
// position on a timer and writes one waypoint snippet per sample to
// waypoints-draft.txt. Stop when done.
//
// USAGE
//   [RecordWaypoints start <name-prefix>
//        Begin recording. Captures your position every ~3 seconds, naming
//        each waypoint "<prefix> N" (N is a counter). Skips samples that
//        haven't moved at least MinStepTiles from the last one, so
//        standing still doesn't make duplicates.
//
//   [RecordWaypoints stop
//        Stop the active recording. Reports how many were captured.
//
//   [RecordWaypoints status
//        Show the current recording state.
//
// PRACTICAL TIP
// Walk steadily at normal speed. Don't run (running covers 6+ tiles per
// second; the 3-second sample then makes 18-tile gaps which is fine but
// uneven). Walking gives ~3 tiles per sample on diagonal motion, which
// produces clean ~10-15 tile waypoint spacing — good for the A* pathfinder.
//
// OUTPUT
// Same draft file ([MarkWay uses): Distribution/waypoints-draft.txt.
// Each snippet auto-connects to the previous one captured in this run
// AND to any waypoint already on disk within 38 tiles. After you stop,
// paste the draft content into waypoints.json's Waypoints array and run
// [ReloadWaypoints.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class RecordWaypointsCommand
    {
        // Sample cadence. 3 seconds at walk speed produces ~10-12 tile
        // spacing on diagonal movement — good for A*.
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(3);

        // A sample at less than this distance from the last one is dropped
        // (you stood still or only nudged a step or two).
        private const int MinStepTiles = 4;

        // Auto-connect range — must match WaypointRegistry / MarkWayCommand.
        private const int NeighborRange = 38;

        private static readonly string DraftPath =
            Path.Combine(Core.BaseDirectory, "waypoints-draft.txt");

        // One active recording per GM. Keyed by Mobile.Serial.
        private static readonly Dictionary<Serial, Recording> _active = new();

        private sealed class Recording
        {
            public Mobile From;
            public string Prefix;
            public int    Counter;
            public Point3D LastSample;
            public bool    HasLastSample;
            public Timer   Timer;
            public string  LastNodeName;   // chains successive captures
        }

        public static void Configure()
        {
            CommandSystem.Register(
                "RecordWaypoints", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("RecordWaypoints start <name-prefix> | stop | status")]
        [Description("Auto-capture waypoints to draft as you walk.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null) return;

            if (e.Length < 1)
            {
                from.SendMessage(
                    "Usage: [RecordWaypoints start <prefix> | stop | status");
                return;
            }

            string sub = e.GetString(0).ToLowerInvariant();
            switch (sub)
            {
                case "start":  StartRecording(e); break;
                case "stop":   StopRecording(from); break;
                case "status": ShowStatus(from);  break;
                default:
                    from.SendMessage($"Unknown subcommand '{sub}'.");
                    from.SendMessage(
                        "Use: [RecordWaypoints start <prefix> | stop | status");
                    break;
            }
        }

        private static void StartRecording(CommandEventArgs e)
        {
            var from = e.Mobile;

            if (e.Length < 2)
            {
                from.SendMessage("Usage: [RecordWaypoints start <name-prefix>");
                from.SendMessage("Example: [RecordWaypoints start MinocRoad");
                return;
            }

            if (_active.ContainsKey(from.Serial))
            {
                from.SendMessage(
                    "You are already recording. [RecordWaypoints stop first.");
                return;
            }

            // Args 1..n become the prefix (allow multi-word prefixes).
            var sb = new StringBuilder();
            for (int i = 1; i < e.Length; i++)
            {
                if (i > 1) sb.Append(' ');
                sb.Append(e.GetString(i));
            }
            string prefix = sb.ToString().Trim();

            var rec = new Recording
            {
                From   = from,
                Prefix = prefix,
            };
            rec.Timer = Timer.DelayCall(
                SampleInterval, SampleInterval, () => Sample(rec));
            _active[from.Serial] = rec;

            from.SendMessage(0x35,
                $"Recording waypoints as '{prefix} N'. Walk the route.");
            from.SendMessage(0x3B2,
                "[RecordWaypoints stop when done.");

            // Capture the start position immediately.
            Sample(rec);
        }

        private static void StopRecording(Mobile from)
        {
            if (!_active.TryGetValue(from.Serial, out var rec))
            {
                from.SendMessage("You are not recording.");
                return;
            }
            rec.Timer?.Stop();
            _active.Remove(from.Serial);
            from.SendMessage(0x35,
                $"Recording stopped. Captured {rec.Counter} waypoint(s).");
            from.SendMessage(0x3B2,
                $"Snippets appended to {DraftPath}. " +
                "Paste them into waypoints.json and [ReloadWaypoints.");
        }

        private static void ShowStatus(Mobile from)
        {
            if (!_active.TryGetValue(from.Serial, out var rec))
            {
                from.SendMessage("Not recording.");
                return;
            }
            from.SendMessage(
                $"Recording '{rec.Prefix}'. Captured so far: {rec.Counter}.");
        }

        // -- Per-sample handler ----------------------------------------------
        private static void Sample(Recording rec)
        {
            var from = rec.From;
            if (from == null || from.Deleted ||
                from.Map == null || from.Map == Map.Internal)
            {
                rec.Timer?.Stop();
                if (from != null) _active.Remove(from.Serial);
                return;
            }

            var here = from.Location;

            // Skip if we haven't moved enough since the last capture.
            if (rec.HasLastSample)
            {
                int dx = here.X - rec.LastSample.X;
                int dy = here.Y - rec.LastSample.Y;
                if (dx * dx + dy * dy < MinStepTiles * MinStepTiles)
                {
                    return;
                }
            }

            rec.Counter++;
            string name = $"{rec.Prefix} {rec.Counter}";

            var connects = FindNeighbors(from.Map, here);
            if (rec.LastNodeName != null && !connects.Contains(rec.LastNodeName))
            {
                connects.Add(rec.LastNodeName);
            }

            AppendSnippet(name, here, connects);

            rec.LastSample    = here;
            rec.HasLastSample = true;
            rec.LastNodeName  = name;

            from.SendMessage(
                $"Recorded '{name}' at ({here.X}, {here.Y}, {here.Z}) " +
                $"with {connects.Count} connection(s).");
        }

        // -- Neighbor lookup -------------------------------------------------
        // Use the same approach MarkWayCommand uses: scan the live registry
        // for waypoints within NeighborRange.
        private static List<string> FindNeighbors(Map map, Point3D from)
        {
            var result = new List<string>();
            var graph  = WaypointRegistry.Graph;
            if (graph == null) return result;

            foreach (var name in graph.AllNames)
            {
                var node = graph.Get(name);
                if (node == null) continue;
                int dx = node.Location.X - from.X;
                int dy = node.Location.Y - from.Y;
                int dist2 = dx * dx + dy * dy;
                if (dist2 <= NeighborRange * NeighborRange)
                {
                    result.Add(name);
                }
            }
            return result;
        }

        // -- File append -----------------------------------------------------
        private static void AppendSnippet(
            string name, Point3D loc, List<string> connects)
        {
            var sb = new StringBuilder();
            sb.AppendLine("    {");
            sb.AppendLine($"      \"Name\": \"{Escape(name)}\",");
            sb.AppendLine($"      \"X\": {loc.X}, \"Y\": {loc.Y}, \"Z\": {loc.Z},");

            sb.Append("      \"Connects\": [");
            for (int i = 0; i < connects.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"\"{Escape(connects[i])}\"");
            }
            sb.AppendLine("]");
            sb.AppendLine("    },");

            try
            {
                File.AppendAllText(DraftPath, sb.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[RecordWaypoints] write to {DraftPath} failed: {ex.Message}");
            }
        }

        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
