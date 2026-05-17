// =========================================================================
// WaypointRegistry.cs — Loads the WaypointGraph from JSON.
//
// File format (Data/Waypoints/waypoints.json):
//
//   {
//     "Waypoints": [
//       {
//         "Name": "Britain Bank",
//         "X": 1434, "Y": 1697, "Z": 0,
//         "Connects": ["Britain South Gate", "Britain North Gate"]
//       },
//       ...
//     ]
//   }
//
// All edges are made bidirectional automatically — if A connects to B but
// B doesn't list A, B gets A added on load. This makes editing easier.
//
// Reload after editing: [ReloadWaypoints
// =========================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Server;

namespace Server.CustomBots
{
    public static class WaypointRegistry
    {
        public const string DataDirRelative = "Data/Waypoints";
        public const string DataFile        = "waypoints.json";

        private static WaypointGraph _graph = new();
        private static bool _loaded;

        public static WaypointGraph Graph
        {
            get { EnsureLoaded(); return _graph; }
        }

        public static int Load()
        {
            _graph = new WaypointGraph();
            _loaded = true;

            string path = Path.Combine(DataDirRelative, DataFile);
            if (!File.Exists(path))
            {
                Console.WriteLine($"WaypointRegistry: no file at {path}");
                return 0;
            }

            try
            {
                using var stream = File.OpenRead(path);
                var doc = JsonDocument.Parse(stream);

                if (!doc.RootElement.TryGetProperty("Waypoints", out var arr))
                {
                    Console.WriteLine("WaypointRegistry: missing 'Waypoints' array");
                    return 0;
                }

                foreach (var el in arr.EnumerateArray())
                {
                    var node = ParseNode(el);
                    if (node != null) _graph.AddNode(node);
                }

                // Make edges bidirectional. If A says it connects to B but
                // B doesn't list A, fix B.
                foreach (var name in _graph.AllNames)
                {
                    var n = _graph.Get(name);
                    if (n == null) continue;
                    foreach (var neighborName in new System.Collections.Generic.List<string>(n.Connects))
                    {
                        var neighbor = _graph.Get(neighborName);
                        if (neighbor == null) continue;
                        if (!neighbor.Connects.Contains(n.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            neighbor.Connects.Add(n.Name);
                        }
                    }
                }

                // Validate and warn about issues.
                var warnings = _graph.Validate();
                foreach (var w in warnings)
                {
                    Console.WriteLine($"WaypointRegistry warning: {w}");
                }

                Console.WriteLine(
                    $"WaypointRegistry: loaded {_graph.NodeCount} node(s) " +
                    $"with {warnings.Count} warning(s)");
                return _graph.NodeCount;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WaypointRegistry: error loading {path}: {ex.Message}");
                return 0;
            }
        }

        private static WaypointNode ParseNode(JsonElement el)
        {
            if (!el.TryGetProperty("Name", out var nameEl)) return null;
            var name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) return null;

            int x = el.TryGetProperty("X", out var xe) ? xe.GetInt32() : 0;
            int y = el.TryGetProperty("Y", out var ye) ? ye.GetInt32() : 0;
            int z = el.TryGetProperty("Z", out var ze) ? ze.GetInt32() : 0;

            var node = new WaypointNode
            {
                Name = name,
                Location = new Point3D(x, y, z),
            };

            if (el.TryGetProperty("Connects", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                foreach (var cn in c.EnumerateArray())
                {
                    var s = cn.GetString();
                    if (!string.IsNullOrEmpty(s)) node.Connects.Add(s);
                }
            }

            return node;
        }

        private static void EnsureLoaded()
        {
            if (!_loaded) Load();
        }
    }
}
