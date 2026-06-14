// =========================================================================
// MarkSpotCommand.cs — [MarkSpot <Type> <name...>  (LIVE version)
//
// Appends the GM's current position as a destination DIRECTLY to
// Data/Destinations/destinations.json (with a .bak first):
//   - Type from the first arg (DestinationType, case-insensitive)
//   - City auto-detected (nearest city center)
//   - NearestWaypoint auto-filled from the waypoint graph, with a GAP
//     warning if it's beyond the 38-tile leg limit
//
//   [MarkSpot Tavern The Salty Dog        mark here as a Tavern
//   [MarkSpotShow                         list spots marked this session
//   [MarkSpotClear                        UNDO the most recent mark
//
// After marking: [ReloadDestinations makes it pickable. Its approach
// field doesn't exist until [rebuildfields (or next boot's auto-rebuild —
// the cache fingerprint sees the change); until then bots arrive without
// the field-guided final approach. (Draft-file workflow retired.)
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
    public static class MarkSpotCommand
    {
        private const int GapWarn = 38;

        private static readonly List<string> _sessionMarks = new();

        private static string JsonPath => Path.Combine(
            Core.BaseDirectory, "Data", "Destinations", "destinations.json");

        private static readonly (string city, int x, int y)[] CityCenters =
        {
            ("Britain", 1434, 1690), ("Vesper", 2899, 676), ("Minoc", 2466, 437),
            ("Trinsic", 1900, 2780), ("Yew", 632, 858), ("Skara Brae", 596, 2138),
            ("Moonglow", 4442, 1172), ("Jhelom", 1383, 3815), ("Nujel'm", 3732, 1279),
            ("Magincia", 3714, 2220), ("Cove", 2230, 1200), ("Buccaneer's Den", 2706, 2150),
        };

        public static void Configure()
        {
            CommandSystem.Register("MarkSpot",      AccessLevel.GameMaster, OnCommand);
            CommandSystem.Register("MarkSpotClear", AccessLevel.GameMaster, OnClearCommand);
            CommandSystem.Register("MarkSpotShow",  AccessLevel.GameMaster, OnShowCommand);
        }

        [Usage("MarkSpot <Type> <name words...>")]
        [Description("Add a destination at the current position directly to destinations.json.")]
        public static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null) return;

            if (e.Length < 2)
            {
                from.SendMessage("Usage: [MarkSpot <Type> <name words...>");
                from.SendMessage("Types: " + string.Join(", ",
                    Enum.GetNames(typeof(DestinationType))));
                return;
            }

            if (!Enum.TryParse<DestinationType>(e.GetString(0), true, out var type))
            {
                from.SendMessage($"Unknown type '{e.GetString(0)}'. Valid: " +
                    string.Join(", ", Enum.GetNames(typeof(DestinationType))));
                return;
            }

            var sb = new StringBuilder();
            for (int i = 1; i < e.Length; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(e.GetString(i));
            }
            string name = sb.ToString().Trim();
            if (name.Length == 0) { from.SendMessage("Name cannot be empty."); return; }

            string city = NearestCity(from.X, from.Y);

            // nearest waypoint for the routing hint (the dynamic resolver
            // re-derives this at plan time anyway, but a good hint is nice)
            string nearestWp = "";
            int wpDist = int.MaxValue;
            var graph = WaypointRegistry.Graph;
            if (graph != null)
            {
                var node = graph.FindNearestNode(from.Location);
                if (node != null)
                {
                    nearestWp = node.Name;
                    wpDist = Math.Max(Math.Abs(node.Location.X - from.X),
                                      Math.Abs(node.Location.Y - from.Y));
                }
            }

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(JsonPath));
                var arr = (JsonArray)root["Destinations"];
                if (arr == null) { from.SendMessage("Destinations array not found."); return; }

                if (arr.Any(d => string.Equals((string)d["Name"], name,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    from.SendMessage($"A destination named '{name}' already exists. Pick another name.");
                    return;
                }

                arr.Add(new JsonObject
                {
                    ["Name"] = name,
                    ["X"] = from.X, ["Y"] = from.Y, ["Z"] = from.Z,
                    ["Type"] = type.ToString(),
                    ["City"] = city,
                    ["NearestWaypoint"] = nearestWp,
                });

                File.Copy(JsonPath, JsonPath + ".bak-markspot", overwrite: true);
                File.WriteAllText(JsonPath, root.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                _sessionMarks.Add(name);
            }
            catch (Exception ex)
            {
                from.SendMessage($"Write failed: {ex.Message} — nothing changed.");
                return;
            }

            from.SendMessage(0x35, $"Marked destination: {name}  (LIVE in destinations.json)");
            from.SendMessage(0x3B2, $"  ({from.X}, {from.Y}, {from.Z})  Type: {type}  City: {city}");
            if (nearestWp.Length == 0)
                from.SendMessage(0x22, "  WARNING: no waypoint graph loaded — routing hint empty.");
            else if (wpDist > GapWarn)
                from.SendMessage(0x22, $"  GAP: nearest waypoint '{nearestWp}' is {wpDist} tiles away " +
                                        "— [MarkWay something closer or bots won't truly arrive.");
            else
                from.SendMessage(0x3B2, $"  Nearest waypoint: {nearestWp} ({wpDist}t)");
            from.SendMessage(0x3B2, "  [ReloadDestinations to make it pickable; " +
                                    "[rebuildfields for its approach field.");
        }

        [Usage("MarkSpotClear")]
        [Description("UNDO the most recent [MarkSpot from this session.")]
        public static void OnClearCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (_sessionMarks.Count == 0)
            { from.SendMessage("No spots this session to undo."); return; }

            string name = _sessionMarks[^1];
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(JsonPath));
                var arr = (JsonArray)root["Destinations"];
                var victim = arr?.FirstOrDefault(d => string.Equals(
                    (string)d["Name"], name, StringComparison.OrdinalIgnoreCase));
                if (victim != null)
                {
                    arr.Remove(victim);
                    File.Copy(JsonPath, JsonPath + ".bak-markspot", overwrite: true);
                    File.WriteAllText(JsonPath, root.ToJsonString(
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                _sessionMarks.RemoveAt(_sessionMarks.Count - 1);
                from.SendMessage($"Undid destination '{name}'. [ReloadDestinations to apply.");
            }
            catch (Exception ex)
            { from.SendMessage($"Undo failed: {ex.Message}"); }
        }

        [Usage("MarkSpotShow")]
        [Description("List destinations marked this session.")]
        public static void OnShowCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (_sessionMarks.Count == 0)
            { from.SendMessage("No spots this session."); return; }
            from.SendMessage(0x35, $"Marked this session ({_sessionMarks.Count}):");
            foreach (var n in _sessionMarks.TakeLast(20))
                from.SendMessage(0x3B2, $"  {n}");
        }

        private static string NearestCity(int x, int y)
        {
            string best = "Britain"; double bd = double.MaxValue;
            foreach (var (c, cx, cy) in CityCenters)
            {
                double d = (double)(cx - x) * (cx - x) + (double)(cy - y) * (cy - y);
                if (d < bd) { bd = d; best = c; }
            }
            return best;
        }
    }
}
