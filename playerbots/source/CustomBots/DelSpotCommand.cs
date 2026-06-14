// =========================================================================
// DelSpotCommand.cs — delete a destination from in-game, safely.
//
//   [delspot           target the nearest destination within 15 tiles
//   [delspot <name>    target by (partial) name — lists matches if several
//   [delspot yes       confirm and delete the announced target
//
// Removes the entry from destinations.json (with a .bak first). The field
// cache fingerprint sees the change, so approach fields rebuild on the
// next boot automatically (or [rebuildfields to do it now).
// Run [ReloadDestinations after deleting.
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
    public static class DelSpotCommand
    {
        private static readonly Dictionary<Serial, string> _pending = new();

        private static string JsonPath => Path.Combine(
            Core.BaseDirectory, "Data", "Destinations", "destinations.json");

        public static void Configure()
        {
            CommandSystem.Register("delspot", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            string arg = e.ArgString?.Trim() ?? "";

            if (arg.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                if (!_pending.TryGetValue(from.Serial, out var target))
                { from.SendMessage("Nothing pending. [delspot first to pick a target."); return; }
                _pending.Remove(from.Serial);
                try
                {
                    Delete(target);
                    from.SendMessage($"Deleted destination '{target}'. " +
                                     "Run [ReloadDestinations to apply.");
                }
                catch (Exception ex)
                { from.SendMessage($"DELETE FAILED: {ex.Message} — nothing changed."); }
                return;
            }

            var (root, arr) = Load();
            JsonNode best = null;

            if (arg.Length > 0)
            {
                var matches = arr.Where(d => ((string)d["Name"])
                    .Contains(arg, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                { from.SendMessage($"No destination matching '{arg}'."); return; }
                if (matches.Count > 1)
                {
                    from.SendMessage($"{matches.Count} matches — be more specific:");
                    foreach (var m in matches.Take(6))
                        from.SendMessage($"  {(string)m["Name"]}  ({(int)m["X"]},{(int)m["Y"]})");
                    return;
                }
                best = matches[0];
            }
            else
            {
                int bd = 15 + 1;
                foreach (var d in arr)
                {
                    int dist = Math.Max(Math.Abs((int)d["X"] - from.X),
                                        Math.Abs((int)d["Y"] - from.Y));
                    if (dist < bd) { bd = dist; best = d; }
                }
                if (best == null)
                { from.SendMessage("No destination within 15 tiles. Stand closer or use [delspot <name>."); return; }
            }

            string name = (string)best["Name"];
            _pending[from.Serial] = name;
            from.SendMessage($"Target: '{name}'  ({(int)best["X"]},{(int)best["Y"]},{(int)best["Z"]})  " +
                             $"Type: {(string)best["Type"]}  City: {(string)best["City"]}");
            from.SendMessage("Run [delspot yes to delete it.");
        }

        private static (JsonNode root, List<JsonNode> arr) Load()
        {
            var root = JsonNode.Parse(File.ReadAllText(JsonPath));
            var arr = (JsonArray)root["Destinations"]
                      ?? throw new Exception("Destinations array not found");
            return (root, arr.ToList());
        }

        private static void Delete(string name)
        {
            var root = JsonNode.Parse(File.ReadAllText(JsonPath));
            var arr = (JsonArray)root["Destinations"]
                      ?? throw new Exception("Destinations array not found");
            var victim = arr.FirstOrDefault(d => string.Equals(
                (string)d["Name"], name, StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception($"'{name}' no longer in file");
            arr.Remove(victim);
            File.Copy(JsonPath, JsonPath + ".bak-delspot", overwrite: true);
            File.WriteAllText(JsonPath, root.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
