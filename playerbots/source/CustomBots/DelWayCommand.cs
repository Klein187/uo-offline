// =========================================================================
// DelWayCommand.cs — delete a waypoint from in-game, safely.
//
//   [delway            target the nearest waypoint within 15 tiles
//   [delway <name>     target a waypoint by (partial) name
//   [delway yes        confirm and delete the announced target
//
// Deletion removes the node from waypoints.json and scrubs it from every
// other node's Connects list (file gets a .bak first). Destinations that
// referenced it need no fixup — the dynamic route-target resolver
// recomputes nearest nodes at plan time. Run [ReloadWaypoints after.
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
    public static class DelWayCommand
    {
        private static readonly Dictionary<Serial, string> _pending = new();

        private static string JsonPath => Path.Combine(
            Core.BaseDirectory, "Data", "Waypoints", "waypoints.json");

        public static void Configure()
        {
            CommandSystem.Register("delway", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            string arg = e.ArgString?.Trim() ?? "";

            if (arg.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                if (!_pending.TryGetValue(from.Serial, out var target))
                { from.SendMessage("Nothing pending. [delway first to pick a target."); return; }
                _pending.Remove(from.Serial);
                try
                {
                    int scrubbed = Delete(target);
                    from.SendMessage($"Deleted waypoint '{target}' (scrubbed from {scrubbed} " +
                                     $"Connects list(s)). Run [ReloadWaypoints to apply.");
                }
                catch (Exception ex)
                { from.SendMessage($"DELETE FAILED: {ex.Message} — nothing changed."); }
                return;
            }

            // pick a target: by name fragment, or nearest within 15 tiles
            var (root, key, list) = Load();
            JsonNode best = null;

            if (arg.Length > 0)
            {
                var matches = list.Where(n => ((string)n["Name"])
                    .Contains(arg, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                { from.SendMessage($"No waypoint matching '{arg}'."); return; }
                if (matches.Count > 1)
                {
                    from.SendMessage($"{matches.Count} matches — be more specific:");
                    foreach (var m in matches.Take(5))
                        from.SendMessage($"  {(string)m["Name"]}");
                    return;
                }
                best = matches[0];
            }
            else
            {
                int bd = 15 + 1;
                foreach (var n in list)
                {
                    int d = Math.Max(Math.Abs((int)n["X"] - from.X),
                                     Math.Abs((int)n["Y"] - from.Y));
                    if (d < bd) { bd = d; best = n; }
                }
                if (best == null)
                { from.SendMessage("No waypoint within 15 tiles. Stand closer or use [delway <name>."); return; }
            }

            string name = (string)best["Name"];
            var conns = (best["Connects"] as JsonArray)?.Select(v => (string)v).ToList() ?? new();
            _pending[from.Serial] = name;
            from.SendMessage($"Target: '{name}' at ({(int)best["X"]},{(int)best["Y"]}), " +
                             $"connects to: {string.Join(", ", conns)}");
            from.SendMessage("Run [delway yes to delete it.");
        }

        private static (JsonNode root, string key, List<JsonNode> list) Load()
        {
            var root = JsonNode.Parse(File.ReadAllText(JsonPath));
            string key = null;
            foreach (var kv in root.AsObject())
                if (kv.Value is JsonArray arr && arr.Count > 0 && arr[0]?["Connects"] != null)
                { key = kv.Key; break; }
            if (key == null) throw new Exception("waypoint list not found");
            return (root, key, ((JsonArray)root[key]).ToList());
        }

        private static int Delete(string name)
        {
            var (root, key, _) = Load();
            var arr = (JsonArray)root[key];
            JsonNode victim = arr.FirstOrDefault(n =>
                string.Equals((string)n["Name"], name, StringComparison.OrdinalIgnoreCase));
            if (victim == null) throw new Exception($"'{name}' no longer in file");
            arr.Remove(victim);

            int scrubbed = 0;
            foreach (var n in arr)
            {
                if (n["Connects"] is not JsonArray c) continue;
                var keep = c.Where(v => !string.Equals((string)v, name,
                    StringComparison.OrdinalIgnoreCase)).Select(v => (string)v).ToList();
                if (keep.Count != c.Count)
                {
                    n["Connects"] = new JsonArray(keep.Select(s => (JsonNode)s).ToArray());
                    scrubbed++;
                }
            }
            File.Copy(JsonPath, JsonPath + ".bak-delway", overwrite: true);
            File.WriteAllText(JsonPath, root.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return scrubbed;
        }
    }
}
