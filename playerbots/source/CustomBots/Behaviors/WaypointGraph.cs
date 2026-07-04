// =========================================================================
// WaypointGraph.cs — A graph of named locations connected by short
// walkable edges (≤38 tiles each). The graph is the data Travelers use
// to navigate across long distances: each leg is short enough for
// ModernUO's A* (PathFollower) to handle.
//
// Pure data + algorithms — no Mobile awareness, easy to unit-test mentally.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public class WaypointNode
    {
        public string Name { get; set; }
        public Point3D Location { get; set; }

        // Names of the nodes this one connects to. Bidirectional — when
        // the graph is loaded, edges are added in both directions.
        public List<string> Connects { get; set; } = new();

        // Optional per-waypoint arrival tolerance. When a bot's PathFollower
        // approaches this node, the bot is considered "arrived" when within
        // ArrivalRange tiles. Default 3 (set by TravelerBehavior). Override
        // to 1 for door/entrance waypoints at unusual Z where the bot must
        // actually step onto the exact tile (e.g. Z=27 raised entrances).
        // 0 means "use behavior default."
        public int ArrivalRange { get; set; } = 0;
    }

    public class WaypointGraph
    {
        // ---- Constants ----

        // A* in ModernUO's BitmapAStarAlgorithm has a 38-tile search area.
        // Any edge longer than this means PathFollower can't path the leg.
        // We warn (don't reject) so the user can adjust.
        public const int MaxLegDistance = 38;

        // ---- Data ----

        private readonly Dictionary<string, WaypointNode> _nodes =
            new(StringComparer.OrdinalIgnoreCase);

        public int NodeCount => _nodes.Count;
        public IEnumerable<string> AllNames => _nodes.Keys;
        public IEnumerable<WaypointNode> AllNodes => _nodes.Values;

        public void AddNode(WaypointNode n)
        {
            if (n == null || string.IsNullOrEmpty(n.Name)) return;
            _nodes[n.Name] = n;
            _componentIds = null; // topology changed — relabel lazily
        }

        public WaypointNode Get(string name) =>
            name != null && _nodes.TryGetValue(name, out var n) ? n : null;

        // ---- Validation ----

        // Walks every edge, returns a list of warnings (long edges, missing
        // neighbor refs). Called once after load. Doesn't reject the graph.
        public List<string> Validate()
        {
            var warnings = new List<string>();

            foreach (var node in _nodes.Values)
            {
                foreach (var neighborName in node.Connects)
                {
                    var neighbor = Get(neighborName);
                    if (neighbor == null)
                    {
                        warnings.Add($"'{node.Name}' references unknown neighbor '{neighborName}'");
                        continue;
                    }

                    int dx = node.Location.X - neighbor.Location.X;
                    int dy = node.Location.Y - neighbor.Location.Y;
                    int distSq = dx * dx + dy * dy;
                    int max = MaxLegDistance * MaxLegDistance;
                    if (distSq > max)
                    {
                        double d = Math.Sqrt(distSq);
                        warnings.Add(
                            $"Edge '{node.Name}' -> '{neighborName}' is {d:F0} tiles " +
                            $"(>{MaxLegDistance}; PathFollower can't path this leg)");
                    }
                }
            }
            return warnings;
        }

        // ---- Shortest path search ----

        // Standard Dijkstra over the graph. Returns the sequence of node
        // names from `fromName` to `toName`, inclusive. Empty if no path.
        public List<string> FindPath(string fromName, string toName)
        {
            var result = new List<string>();
            if (fromName == toName)
            {
                result.Add(fromName);
                return result;
            }

            var fromNode = Get(fromName);
            var toNode = Get(toName);
            if (fromNode == null || toNode == null)
                return result;

            // Dijkstra with priority queue keyed on cumulative distance.
            var dist = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var prev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queue = new PriorityQueue<string, double>();

            foreach (var name in _nodes.Keys)
            {
                dist[name] = double.PositiveInfinity;
            }
            dist[fromName] = 0;
            queue.Enqueue(fromName, 0);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current.Equals(toName, StringComparison.OrdinalIgnoreCase))
                    break;

                var currentNode = Get(current);
                if (currentNode == null) continue;

                double currentDist = dist[current];

                foreach (var neighborName in currentNode.Connects)
                {
                    var neighbor = Get(neighborName);
                    if (neighbor == null) continue;

                    int dx = currentNode.Location.X - neighbor.Location.X;
                    int dy = currentNode.Location.Y - neighbor.Location.Y;
                    double edgeCost = Math.Sqrt(dx * dx + dy * dy);

                    double alt = currentDist + edgeCost;
                    if (alt < dist[neighborName])
                    {
                        dist[neighborName] = alt;
                        prev[neighborName] = current;
                        queue.Enqueue(neighborName, alt);
                    }
                }
            }

            // No path?
            if (!prev.ContainsKey(toName) && !toName.Equals(fromName, StringComparison.OrdinalIgnoreCase))
                return result;

            // Reconstruct path back to front.
            var reverse = new List<string>();
            string cursor = toName;
            while (cursor != null)
            {
                reverse.Add(cursor);
                if (!prev.TryGetValue(cursor, out var p))
                    break;
                cursor = p;
            }
            reverse.Reverse();
            return reverse;
        }

        // ---- Connected component ----

        // All node names walk-reachable from `fromName` — the connected
        // component containing it. Teleporters are not walk edges, so a
        // dungeon floor is its own component; this doubles as a "same
        // floor" test for dungeon crawlers.
        public HashSet<string> ReachableFrom(string fromName)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var start = Get(fromName);
            if (start == null)
            {
                return seen;
            }

            var stack = new Stack<WaypointNode>();
            seen.Add(start.Name);
            stack.Push(start);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                foreach (var neighborName in node.Connects)
                {
                    if (seen.Contains(neighborName))
                    {
                        continue;
                    }
                    var neighbor = Get(neighborName);
                    if (neighbor == null)
                    {
                        continue;
                    }
                    seen.Add(neighbor.Name);
                    stack.Push(neighbor);
                }
            }
            return seen;
        }

        // ---- Connected-component labels ----
        //
        // One O(V+E) pass labels every node with a component id, making
        // "same island / same floor" checks O(1) — ReachableFrom's BFS per
        // call is only needed when the actual member SET matters. Labels
        // are computed lazily and invalidated by AddNode ([MarkWay etc.).
        // Edges are treated as undirected regardless of how the data was
        // authored (the registry symmetrizes at load; this stays correct
        // even if a hand edit leaves a one-way edge).

        private Dictionary<string, int> _componentIds;
        private int _componentCount;

        private void EnsureComponents()
        {
            if (_componentIds != null) return;

            // Undirected adjacency (forward + reverse of every edge).
            var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in _nodes.Values)
            {
                foreach (var neighborName in node.Connects)
                {
                    if (Get(neighborName) == null)
                    {
                        continue;
                    }
                    if (!adj.TryGetValue(node.Name, out var f))
                    {
                        adj[node.Name] = f = new List<string>();
                    }
                    f.Add(neighborName);
                    if (!adj.TryGetValue(neighborName, out var r))
                    {
                        adj[neighborName] = r = new List<string>();
                    }
                    r.Add(node.Name);
                }
            }

            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            var stack = new Stack<string>();

            foreach (var name in _nodes.Keys)
            {
                if (ids.ContainsKey(name))
                {
                    continue;
                }
                int id = count++;
                ids[name] = id;
                stack.Push(name);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    if (!adj.TryGetValue(current, out var neighbors))
                    {
                        continue;
                    }
                    foreach (var neighborName in neighbors)
                    {
                        if (ids.ContainsKey(neighborName))
                        {
                            continue;
                        }
                        ids[neighborName] = id;
                        stack.Push(neighborName);
                    }
                }
            }

            _componentIds = ids;
            _componentCount = count;
        }

        // Component id of a node, or -1 if the node isn't in the graph.
        public int ComponentOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            EnsureComponents();
            return _componentIds.TryGetValue(name, out var id) ? id : -1;
        }

        public bool SameComponent(string a, string b)
        {
            int ca = ComponentOf(a);
            return ca >= 0 && ca == ComponentOf(b);
        }

        public int ComponentCount
        {
            get { EnsureComponents(); return _componentCount; }
        }

        // ---- Nearest-node lookup ----

        // Find the waypoint closest to a world location. Used to plug the
        // bot into the graph wherever they happen to be standing.
        public WaypointNode FindNearestNode(Point3D loc)
        {
            WaypointNode best = null;
            int bestDistSq = int.MaxValue;
            foreach (var n in _nodes.Values)
            {
                int dx = n.Location.X - loc.X;
                int dy = n.Location.Y - loc.Y;
                int distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = n;
                }
            }
            return best;
        }

        // The K nearest nodes by straight-line distance, closest first.
        // Small K + cold path, so a simple insertion scan beats building
        // and sorting the whole node list.
        public void FindNearestNodes(Point3D loc, int k, List<WaypointNode> results)
        {
            results.Clear();
            if (k <= 0) return;

            foreach (var n in _nodes.Values)
            {
                int dx = n.Location.X - loc.X;
                int dy = n.Location.Y - loc.Y;
                int distSq = dx * dx + dy * dy;

                int at = results.Count;
                while (at > 0)
                {
                    var p = results[at - 1].Location;
                    int pdx = p.X - loc.X;
                    int pdy = p.Y - loc.Y;
                    if (pdx * pdx + pdy * pdy <= distSq)
                    {
                        break;
                    }
                    at--;
                }
                if (at < k)
                {
                    results.Insert(at, n);
                    if (results.Count > k)
                    {
                        results.RemoveAt(results.Count - 1);
                    }
                }
            }
        }

        public string PickRandomName()
        {
            if (_nodes.Count == 0) return null;
            var arr = new string[_nodes.Count];
            int i = 0;
            foreach (var k in _nodes.Keys) arr[i++] = k;
            return arr[Utility.Random(arr.Length)];
        }
    }
}
