// =========================================================================
// AuditEdgesCommand.cs — find (and optionally remove) impossible edges.
//
//   [auditedges        scan every graph edge for walkability; REPORT only
//   [auditedges fix    remove the BLOCKED edges from waypoints.json
//
// For each waypoint, floods a distance field (same movement rules bots
// use) and checks that every connected neighbor is actually reachable.
// Edges that cross rivers/walls with no path get flagged BLOCKED; edges
// longer than the 38-tile A* leg limit get flagged FAR.
//
// CAUTION: nodes at closed doors (interior links) can be false positives,
// since the flood stops at shut doors like bot route-walking does. Review
// the report before running fix; door-named nodes deserve a look first.
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
    public static class AuditEdgesCommand
    {
        private const int LegLimit = 38;

        private static string JsonPath => Path.Combine(
            Core.BaseDirectory, "Data", "Waypoints", "waypoints.json");

        public static void Configure()
        {
            CommandSystem.Register("auditedges", AccessLevel.GameMaster, OnCommand);
        }

        // -------------------------------------------------------------------
        // Headless scan — the same flood-fill edge check as [auditedges,
        // returning report lines instead of chat output. Used by the
        // editor/file-token bridge so data authored outside the client can
        // be verified without logging in. Never fixes; report only.
        // -------------------------------------------------------------------
        public static List<string> Scan()
        {
            var lines = new List<string>();

            JsonNode root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(JsonPath));
            }
            catch (Exception ex)
            {
                lines.Add($"waypoints.json parse failed: {ex.Message}");
                return lines;
            }

            string key = null;
            foreach (var kv in root.AsObject())
                if (kv.Value is JsonArray a && a.Count > 0 && a[0]?["Connects"] != null)
                { key = kv.Key; break; }
            if (key == null)
            {
                lines.Add("waypoint list not found");
                return lines;
            }
            var arr = (JsonArray)root[key];

            var pos = new Dictionary<string, (int x, int y, int z)>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in arr)
                pos[(string)n["Name"]] = ((int)n["X"], (int)n["Y"],
                                          n["Z"] != null ? (int)n["Z"] : 0);

            var checkedPairs = new HashSet<string>();
            foreach (var n in arr)
            {
                string an = (string)n["Name"];
                var (ax, ay, az) = pos[an];
                DistanceField field = null;

                foreach (var cNode in (n["Connects"] as JsonArray) ?? new JsonArray())
                {
                    string bn = (string)cNode;
                    if (!pos.TryGetValue(bn, out var b)) continue;
                    string pair = string.CompareOrdinal(an, bn) < 0 ? an + "|" + bn : bn + "|" + an;
                    if (!checkedPairs.Add(pair)) continue;

                    int dist = Math.Max(Math.Abs(ax - b.x), Math.Abs(ay - b.y));
                    if (dist > LegLimit)
                    {
                        lines.Add($"FAR ({dist}t): {an} <-> {bn}");
                        continue;
                    }

                    field ??= DistanceField.Build(Map.Felucca,
                        new Point3D(ax, ay, az), LegLimit + 6);
                    int cost = field?.CostAt(b.x, b.y) ?? -1;
                    if (cost < 0)
                        lines.Add($"BLOCKED: {an} <-> {bn}");
                    else if (cost > LegLimit * 14)
                        lines.Add($"WALKCOST ({cost}): {an} <-> {bn}");
                }
            }
            return lines;
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            bool fix = e.Arguments.Length > 0 &&
                       e.Arguments[0].Equals("fix", StringComparison.OrdinalIgnoreCase);

            var root = JsonNode.Parse(File.ReadAllText(JsonPath));
            string key = null;
            foreach (var kv in root.AsObject())
                if (kv.Value is JsonArray a && a.Count > 0 && a[0]?["Connects"] != null)
                { key = kv.Key; break; }
            if (key == null) { from.SendMessage("waypoint list not found."); return; }
            var arr = (JsonArray)root[key];

            var pos = new Dictionary<string, (int x, int y, int z)>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in arr)
                pos[(string)n["Name"]] = ((int)n["X"], (int)n["Y"],
                                          n["Z"] != null ? (int)n["Z"] : 0);

            from.SendMessage($"Auditing edges across {arr.Count} waypoints (flooding each)...");
            var blocked  = new List<(string a, string b)>();
            var walkcost = new List<(string a, string b, int c)>();
            var far      = new List<(string a, string b, int d)>();
            var checkedPairs = new HashSet<string>();

            foreach (var n in arr)
            {
                string an = (string)n["Name"];
                var (ax, ay, az) = pos[an];
                DistanceField field = null;

                foreach (var cNode in (n["Connects"] as JsonArray) ?? new JsonArray())
                {
                    string bn = (string)cNode;
                    if (!pos.TryGetValue(bn, out var b)) continue;
                    string pair = string.CompareOrdinal(an, bn) < 0 ? an + "|" + bn : bn + "|" + an;
                    if (!checkedPairs.Add(pair)) continue;

                    int dist = Math.Max(Math.Abs(ax - b.x), Math.Abs(ay - b.y));
                    if (dist > LegLimit) { far.Add((an, bn, dist)); continue; }

                    field ??= DistanceField.Build(Map.Felucca,
                        new Point3D(ax, ay, az), LegLimit + 6);
                    int cost = field?.CostAt(b.x, b.y) ?? -1;
                    if (cost < 0)
                        blocked.Add((an, bn));
                    else if (cost > LegLimit * 14)   // cost is weighted (~10/step, 14 diagonal), not tiles
                        walkcost.Add((an, bn, cost));
                }
            }

            from.SendMessage(0x35, $"Edges checked: {checkedPairs.Count}.  " +
                                   $"BLOCKED: {blocked.Count}   WALKCOST(>{LegLimit}): {walkcost.Count}   " +
                                   $"FAR(>{LegLimit}t): {far.Count}");
            foreach (var (a, b, c) in walkcost)
            {
                from.SendMessage(0x22, $"  WALKCOST ({c} steps): {a} <-> {b}");
                Console.WriteLine($"[auditedges] WALKCOST ({c}): {a} <-> {b}");
            }
            foreach (var (a, b) in blocked)
            {
                from.SendMessage(0x22, $"  BLOCKED: {a} <-> {b}");
                Console.WriteLine($"[auditedges] BLOCKED: {a} <-> {b}");
            }
            foreach (var (a, b, d) in far)
            {
                from.SendMessage(0x3B2, $"  FAR ({d}t): {a} <-> {b}");
                Console.WriteLine($"[auditedges] FAR ({d}t): {a} <-> {b}");
            }

            if (!fix)
            {
                if (blocked.Count > 0)
                    from.SendMessage("Run [auditedges fix to remove the BLOCKED edges " +
                                     "(review door/interior nodes first).");
                return;
            }

            if (blocked.Count == 0 && walkcost.Count == 0)
            { from.SendMessage("Nothing to fix."); return; }

            var bad = new HashSet<string>(
                blocked.SelectMany(p => new[] { p.a + "|" + p.b, p.b + "|" + p.a })
                .Concat(walkcost.SelectMany(p => new[] { p.a + "|" + p.b, p.b + "|" + p.a })),
                StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            foreach (var n in arr)
            {
                string an = (string)n["Name"];
                if (n["Connects"] is not JsonArray c) continue;
                var keep = c.Select(v => (string)v)
                            .Where(bn => !bad.Contains(an + "|" + bn)).ToList();
                if (keep.Count != c.Count)
                {
                    removed += c.Count - keep.Count;
                    n["Connects"] = new JsonArray(keep.Select(s => (JsonNode)s).ToArray());
                }
            }
            File.Copy(JsonPath, JsonPath + ".bak-auditedges", overwrite: true);
            File.WriteAllText(JsonPath, root.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            from.SendMessage(0x35, $"Removed {removed} edge reference(s) " +
                             $"({blocked.Count} blocked pair(s)). Run [ReloadWaypoints.");
        }
    }
}
