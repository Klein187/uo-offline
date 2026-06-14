// =========================================================================
// ResyncWaypointsCommand.cs — [resyncwaypoints
//
// Rewrites every destination's stored NearestWaypoint to the node that is
// actually nearest in the CURRENT graph. After heavy graph editing the
// stored value can be stale (pointing at a removed/relocated node, or a
// now-suboptimal one); this realigns the whole catalog in one pass.
//
//   [resyncwaypoints          report what WOULD change (dry run)
//   [resyncwaypoints apply    write the changes to destinations.json
//
// Writes a .bak first. Run [ReloadDestinations after applying.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class ResyncWaypointsCommand
    {
        private static string JsonPath => Path.Combine(
            Core.BaseDirectory, "Data", "Destinations", "destinations.json");

        public static void Configure()
        {
            CommandSystem.Register("resyncwaypoints", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            bool apply = e.Arguments.Length > 0 &&
                         e.Arguments[0].Equals("apply", StringComparison.OrdinalIgnoreCase);

            var graph = WaypointRegistry.Graph;
            if (graph == null || graph.NodeCount == 0)
            { from.SendMessage("No waypoint graph loaded."); return; }

            JsonNode root;
            JsonArray arr;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(JsonPath));
                arr = (JsonArray)root["Destinations"];
                if (arr == null) { from.SendMessage("Destinations array not found."); return; }
            }
            catch (Exception ex)
            { from.SendMessage($"Read failed: {ex.Message}"); return; }

            int changed = 0, examined = 0;
            var changes = new List<string>();

            foreach (var d in arr)
            {
                examined++;
                int x = (int)d["X"], y = (int)d["Y"];
                var node = graph.FindNearestNode(new Point3D(x, y, d["Z"] != null ? (int)d["Z"] : 0));
                if (node == null) continue;

                string stored = (string)d["NearestWaypoint"] ?? "";
                if (!string.Equals(stored, node.Name, StringComparison.Ordinal))
                {
                    int dist = Math.Max(Math.Abs(node.Location.X - x),
                                        Math.Abs(node.Location.Y - y));
                    changes.Add($"{(string)d["Name"]}: '{stored}' -> '{node.Name}' ({dist}t)");
                    if (apply) d["NearestWaypoint"] = node.Name;
                    changed++;
                }
            }

            from.SendMessage(0x35, $"Examined {examined} destinations; " +
                                   $"{changed} differ from current-nearest.");
            foreach (var c in changes.Take(25))
                from.SendMessage(0x3B2, "  " + c);
            if (changes.Count > 25)
                from.SendMessage(0x3B2, $"  ... and {changes.Count - 25} more.");

            if (!apply)
            {
                if (changed > 0)
                    from.SendMessage("Dry run. Run [resyncwaypoints apply to write these.");
                return;
            }

            if (changed == 0) { from.SendMessage("Nothing to change."); return; }

            try
            {
                File.Copy(JsonPath, JsonPath + ".bak-resync", overwrite: true);
                File.WriteAllText(JsonPath, root.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                from.SendMessage(0x35, $"Wrote {changed} change(s). Run [ReloadDestinations.");
            }
            catch (Exception ex)
            { from.SendMessage($"Write failed: {ex.Message} — nothing changed."); }
        }
    }
}
