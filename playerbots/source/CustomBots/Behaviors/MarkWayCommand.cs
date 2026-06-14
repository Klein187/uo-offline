// =========================================================================
// MarkWayCommand.cs — [MarkWay <name...>  (LIVE version)
//
// Appends the GM's current position as a waypoint DIRECTLY to
// Data/Waypoints/waypoints.json (with a .bak first). Auto-connects to
// every existing waypoint within 38 tiles (PathFollower's A* range) —
// bidirectionality is handled by the WaypointRegistry loader, so only
// the new node's Connects list is needed. Run [ReloadWaypoints and the
// node is routable; hit Refresh on the map and it's visible.
//
//   [MarkWay Britain North Road A    mark here with that name
//   [MarkWayShow                     list waypoints marked this session
//   [MarkWayClear                    UNDO the most recent mark (this session)
//
// (The old draft-file workflow is retired; waypoints-draft.txt is unused.)
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class MarkWayCommand
    {
        private const int NeighborRange = 38;

        // Names added this session, newest last — powers Show and Clear(undo).
        private static readonly List<string> _sessionMarks = new();

        private static string JsonPath => Path.Combine(
            Core.BaseDirectory, "Data", "Waypoints", "waypoints.json");

        public static void Configure()
        {
            CommandSystem.Register("MarkWay",      AccessLevel.GameMaster, OnCommand);
            CommandSystem.Register("MarkWayClear", AccessLevel.GameMaster, OnClearCommand);
            CommandSystem.Register("MarkWayShow",  AccessLevel.GameMaster, OnShowCommand);
        }

        [Usage("MarkWay <name words...>")]
        [Description("Add a waypoint at the current position directly to waypoints.json.")]
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

            var sb = new StringBuilder();
            for (int i = 0; i < e.Length; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(e.GetString(i));
            }
            string name = sb.ToString().Trim();
            if (name.Length == 0) { from.SendMessage("Name cannot be empty."); return; }

            // Neighbors within range, nearest first (in-memory graph is fine
            // for this — anything marked-but-not-reloaded is also checked
            // against the FILE below, so chains within one session connect).
            var neighbors = new List<(string n, int d)>();
            try
            {
                var (root, key, arr) = Load();

                if (arr.Any(n => string.Equals((string)n["Name"], name,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    from.SendMessage($"A waypoint named '{name}' already exists. Pick another name.");
                    return;
                }

                // Walkability gate: flood from where the GM stands (same
                // movement rules as bots) and only connect neighbors the
                // flood reaches — never across rivers or through walls.
                var walkField = DistanceField.Build(from.Map, from.Location, NeighborRange + 4);
                var skipped = new List<string>();
                foreach (var n in arr)
                {
                    int nx = (int)n["X"], ny = (int)n["Y"];
                    int dx = from.X - nx, dy = from.Y - ny;
                    int dist = (int)Math.Sqrt(dx * dx + dy * dy);
                    if (dist > NeighborRange) continue;
                    if (walkField != null && !walkField.Covers(nx, ny))
                    {
                        skipped.Add((string)n["Name"]);
                        continue;
                    }
                    neighbors.Add(((string)n["Name"], dist));
                }
                neighbors.Sort((a, b) => a.d.CompareTo(b.d));
                if (skipped.Count > 0)
                    from.SendMessage(0x22, "  Skipped (no walkable path): " +
                        string.Join(", ", skipped));

                var node = new JsonObject
                {
                    ["Name"] = name,
                    ["X"] = from.X, ["Y"] = from.Y, ["Z"] = from.Z,
                    ["Connects"] = new JsonArray(
                        neighbors.Select(p => (JsonNode)p.n).ToArray()),
                };
                arrAppend(root, key, node);

                File.Copy(JsonPath, JsonPath + ".bak-markway", overwrite: true);
                File.WriteAllText(JsonPath, root.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                _sessionMarks.Add(name);
            }
            catch (Exception ex)
            {
                from.SendMessage($"Write failed: {ex.Message} — nothing changed.");
                return;
            }

            from.SendMessage(0x35, $"Marked waypoint: {name}  (LIVE in waypoints.json)");
            from.SendMessage(0x3B2, $"  ({from.X}, {from.Y}, {from.Z})");
            if (neighbors.Count == 0)
            {
                from.SendMessage(0x22,
                    "  WARNING: no existing waypoint within 38 tiles — this node is ISOLATED " +
                    "and routes nowhere until something connects to it.");
            }
            else
            {
                from.SendMessage(0x3B2, "  Auto-connects to: " +
                    string.Join(", ", neighbors.Select(p => $"{p.n} ({p.d}t)")));
            }
            from.SendMessage(0x3B2, "  Run [ReloadWaypoints to make it routable.");
        }

        [Usage("MarkWayClear")]
        [Description("UNDO the most recent [MarkWay from this session.")]
        public static void OnClearCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (_sessionMarks.Count == 0)
            { from.SendMessage("No marks this session to undo. (Use [delway for older nodes.)"); return; }

            string name = _sessionMarks[^1];
            try
            {
                var (root, key, arr) = Load();
                var victim = arr.FirstOrDefault(n => string.Equals(
                    (string)n["Name"], name, StringComparison.OrdinalIgnoreCase));
                if (victim != null)
                {
                    ((JsonArray)root[key]).Remove(victim);
                    File.Copy(JsonPath, JsonPath + ".bak-markway", overwrite: true);
                    File.WriteAllText(JsonPath, root.ToJsonString(
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                _sessionMarks.RemoveAt(_sessionMarks.Count - 1);
                from.SendMessage($"Undid mark '{name}'. Run [ReloadWaypoints.");
            }
            catch (Exception ex)
            { from.SendMessage($"Undo failed: {ex.Message}"); }
        }

        [Usage("MarkWayShow")]
        [Description("List waypoints marked this session.")]
        public static void OnShowCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (_sessionMarks.Count == 0)
            { from.SendMessage("No marks this session."); return; }
            from.SendMessage(0x35, $"Marked this session ({_sessionMarks.Count}):");
            foreach (var n in _sessionMarks.TakeLast(20))
                from.SendMessage(0x3B2, $"  {n}");
        }

        // ---- json helpers ---------------------------------------------------
        private static (JsonNode root, string key, List<JsonNode> arr) Load()
        {
            var root = JsonNode.Parse(File.ReadAllText(JsonPath));
            string key = null;
            foreach (var kv in root.AsObject())
                if (kv.Value is JsonArray a && a.Count > 0 && a[0]?["Connects"] != null)
                { key = kv.Key; break; }
            if (key == null) throw new Exception("waypoint list not found in JSON");
            return (root, key, ((JsonArray)root[key]).ToList());
        }

        private static void arrAppend(JsonNode root, string key, JsonObject node)
            => ((JsonArray)root[key]).Add(node);
    }
}
