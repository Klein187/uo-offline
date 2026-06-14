// =========================================================================
// RecordWayCommand.cs — record waypoints by walking.
//
//   [recordway start [prefix]   begin recording (default prefix "Rec")
//   [recordway stop             finish, save, report
//   [recordway status           how many nodes so far
//
// While recording, a node is dropped automatically every ~25 tiles you
// walk, each connected to the previous one (bidirectionally). The chain's
// first and last nodes also connect to the nearest existing waypoint
// within 38 tiles (the A* leg limit), splicing the new path into the
// graph. Writes Data/Waypoints/waypoints.json on stop — then run
// [ReloadWaypoints and the new route is live, no restart.
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
    public static class RecordWayCommand
    {
        private const int SpacingTiles = 25;   // drop a node every N tiles
        private const int SpliceRange  = 38;   // connect ends to graph within

        private class Session
        {
            public string Prefix;
            public Point3D LastNode;
            public List<(string name, int x, int y, int z)> Nodes = new();
            public Timer Timer;
        }

        private static readonly Dictionary<Serial, Session> _sessions = new();

        private static string JsonPath => Path.Combine(
            Core.BaseDirectory, "Data", "Waypoints", "waypoints.json");

        public static void Configure()
        {
            CommandSystem.Register("recordway", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            var sub = e.Arguments.Length > 0 ? e.Arguments[0].ToLower() : "";

            switch (sub)
            {
                case "start":
                {
                    if (_sessions.ContainsKey(from.Serial))
                    { from.SendMessage("Already recording. [recordway stop first."); return; }

                    var s = new Session
                    {
                        Prefix = e.Arguments.Length > 1 ? e.Arguments[1] : "Rec",
                        LastNode = from.Location,
                    };
                    AddNode(s, from);   // first node where you stand
                    s.Timer = Timer.DelayCall(TimeSpan.FromMilliseconds(500),
                                              TimeSpan.FromMilliseconds(500),
                                              () => Tick(from));
                    _sessions[from.Serial] = s;
                    from.SendMessage($"Recording waypoints (prefix '{s.Prefix}', " +
                                     $"every {SpacingTiles} tiles). Walk the route, " +
                                     $"then [recordway stop.");
                    return;
                }
                case "stop":
                {
                    if (!_sessions.TryGetValue(from.Serial, out var s))
                    { from.SendMessage("Not recording."); return; }
                    s.Timer?.Stop();
                    _sessions.Remove(from.Serial);

                    // ensure the spot you stopped on is a node too
                    if (Dist(from.Location, s.LastNode) >= 5)
                        AddNode(s, from);

                    try
                    {
                        int spliced = SaveToJson(s);
                        from.SendMessage($"Saved {s.Nodes.Count} node(s); spliced " +
                                         $"{spliced} end(s) into the existing graph.");
                        from.SendMessage("Run [ReloadWaypoints to make the route live.");
                    }
                    catch (Exception ex)
                    {
                        from.SendMessage($"SAVE FAILED: {ex.Message} — nothing written.");
                    }
                    return;
                }
                case "status":
                {
                    from.SendMessage(_sessions.TryGetValue(from.Serial, out var st)
                        ? $"Recording: {st.Nodes.Count} node(s) so far (prefix '{st.Prefix}')."
                        : "Not recording.");
                    return;
                }
                default:
                    from.SendMessage("Usage: [recordway start [prefix] | stop | status");
                    return;
            }
        }

        private static void Tick(Mobile from)
        {
            if (!_sessions.TryGetValue(from.Serial, out var s)) return;
            if (from.Deleted || from.NetState == null)
            {   // player gone — abandon quietly, write nothing
                s.Timer?.Stop();
                _sessions.Remove(from.Serial);
                return;
            }
            if (Dist(from.Location, s.LastNode) >= SpacingTiles)
                AddNode(s, from);
        }

        private static void AddNode(Session s, Mobile from)
        {
            string name = $"{s.Prefix} {s.Nodes.Count + 1}";
            s.Nodes.Add((name, from.X, from.Y, from.Z));
            s.LastNode = from.Location;
            from.SendMessage($"+ waypoint '{name}' ({from.X},{from.Y},{from.Z})");
        }

        private static int Dist(Point3D a, Point3D b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        // ---- JSON splice ----------------------------------------------------
        private static int SaveToJson(Session s)
        {
            var root = JsonNode.Parse(File.ReadAllText(JsonPath));
            // find the node-array property
            string key = null;
            foreach (var kv in root.AsObject())
                if (kv.Value is JsonArray arr && arr.Count > 0 &&
                    arr[0]?["Connects"] != null) { key = kv.Key; break; }
            if (key == null) throw new Exception("waypoint list not found in JSON");
            var list = (JsonArray)root[key];

            // uniquify names against existing
            var existing = new HashSet<string>(
                list.Select(n => (string)n["Name"]), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < s.Nodes.Count; i++)
            {
                var n = s.Nodes[i];
                string nm = n.name; int suffix = 1;
                while (existing.Contains(nm)) nm = $"{n.name}.{suffix++}";
                existing.Add(nm);
                s.Nodes[i] = (nm, n.x, n.y, n.z);
            }

            // chain connections
            var connects = new List<string>[s.Nodes.Count];
            for (int i = 0; i < s.Nodes.Count; i++) connects[i] = new List<string>();
            for (int i = 0; i + 1 < s.Nodes.Count; i++)
            {
                connects[i].Add(s.Nodes[i + 1].name);
                connects[i + 1].Add(s.Nodes[i].name);
            }

            // splice both ends into nearest existing node within range
            int spliced = 0;
            foreach (int endIdx in (s.Nodes.Count == 1 ? new[] { 0 } : new[] { 0, s.Nodes.Count - 1 }))
            {
                var (nm, x, y, _) = s.Nodes[endIdx];
                JsonNode best = null; int bd = int.MaxValue;
                foreach (var n in list)
                {
                    int d = Math.Max(Math.Abs((int)n["X"] - x), Math.Abs((int)n["Y"] - y));
                    if (d < bd) { bd = d; best = n; }
                }
                if (best != null && bd <= SpliceRange)
                {
                    string bn = (string)best["Name"];
                    if (!connects[endIdx].Contains(bn)) connects[endIdx].Add(bn);
                    var bc = (JsonArray)best["Connects"];
                    if (!bc.Select(v => (string)v).Contains(nm)) bc.Add(nm);
                    spliced++;
                }
            }

            // append the new nodes
            for (int i = 0; i < s.Nodes.Count; i++)
            {
                var (nm, x, y, z) = s.Nodes[i];
                var obj = new JsonObject
                {
                    ["Name"] = nm, ["X"] = x, ["Y"] = y, ["Z"] = z,
                    ["Connects"] = new JsonArray(connects[i].Select(c => (JsonNode)c).ToArray()),
                };
                list.Add(obj);
            }

            File.Copy(JsonPath, JsonPath + ".bak-recordway", overwrite: true);
            File.WriteAllText(JsonPath, root.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return spliced;
        }
    }
}
