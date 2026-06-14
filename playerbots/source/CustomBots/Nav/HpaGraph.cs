// =========================================================================
// HpaGraph.cs — Hierarchical Pathfinding A* graph, auto-built from the map.
//
// Replaces the hand-recorded WaypointGraph as the long-range router. Exposes
// the SAME surface the Traveler already uses, so wiring it in is minimal:
//   - Get(name)            -> HpaNode with .Location
//   - FindPath(from, to)   -> List<string> of node names
//   - Nearest(point)       -> nearest node name to a coord
//   - PickRandomName()
//
// How it's built (no hand recording):
//   1. Divide the map into ClusterSize tiles square clusters.
//   2. On each border between adjacent clusters, find contiguous runs of
//      mutually-walkable tile pairs ("transitions"). Each run yields ONE
//      pair of entrance nodes (one on each side), placed at the run midpoint.
//      Narrow gaps (bridges, gates) survive because they ARE runs.
//   3. Intra-cluster edges: bounded A* between every pair of entrances in the
//      same cluster, edge weighted by path length.
//   4. Inter-cluster edges: the paired border nodes connect at cost 1.
//
// Node names are coord-derived and stable: "hpa_<x>_<y>".
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Server;

namespace Server.CustomBots
{
    public sealed class HpaNode
    {
        public string Name;
        public Point3D Location;
        // API compatibility with WaypointNode: the Traveler reads .ArrivalRange
        // off leg nodes. HPA entrances use the default leg tolerance; there are
        // no tight door-waypoints in the auto graph.
        public int ArrivalRange = 0;
        // adjacency: neighborName -> cost
        public readonly Dictionary<string, double> Edges = new();
    }

    public static class HpaGraph
    {
        // Cluster size. Bigger = fewer clusters/borders/entrances = much
        // faster build (intra-cluster pairing is ~quadratic in entrances per
        // cluster), at the cost of slightly coarser routing. 96 keeps legs
        // short enough for greedy walking between entrances while keeping the
        // build tractable on the Deck.
        public const int ClusterSize = 96;

        private static readonly Dictionary<string, HpaNode> _nodes =
            new(StringComparer.OrdinalIgnoreCase);
        private static Map _map = Map.Felucca;

        public static int NodeCount => _nodes.Count;
        public static int EdgeCount => _nodes.Values.Sum(n => n.Edges.Count) / 2;
        public static IEnumerable<string> AllNames => _nodes.Keys;

        public static HpaNode Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            _nodes.TryGetValue(name, out var n);
            return n;
        }

        private static string NameAt(int x, int y) => $"hpa_{x}_{y}";

        private static HpaNode GetOrAdd(int x, int y)
        {
            string nm = NameAt(x, y);
            if (!_nodes.TryGetValue(nm, out var n))
            {
                int z = _map.GetAverageZ(x, y);
                Walkable.TryFindSeedZ(_map, x, y, z, out z);
                n = new HpaNode { Name = nm, Location = new Point3D(x, y, z) };
                _nodes[nm] = n;
            }
            return n;
        }

        private static void Link(HpaNode a, HpaNode b, double cost)
        {
            if (a == b) return;
            if (!a.Edges.TryGetValue(b.Name, out var old) || cost < old)
                a.Edges[b.Name] = cost;
            if (!b.Edges.TryGetValue(a.Name, out var old2) || cost < old2)
                b.Edges[a.Name] = cost;
        }

        // ---- Disk cache -----------------------------------------------------
        // The build is expensive (minute+), so we serialize the finished
        // graph and reload it on subsequent boots in well under a second.
        // Cache is invalidated by a version tag + the map dimensions; bump
        // CacheVersion whenever the build logic or node format changes.
        private const int CacheVersion = 5;
        private static string CachePath =>
            Path.Combine(Core.BaseDirectory, "Data", "Navigation", "hpa_cache.bin");

        // Build if no valid cache exists, otherwise load. This is what the
        // startup hook should call. Returns a status string for logging.
        public static string EnsureBuilt(Map map = null)
        {
            _map = map ?? Map.Felucca;

            if (TryLoadFromDisk())
                return $"HPA* loaded from cache: {NodeCount:n0} nodes, {EdgeCount:n0} edges.";

            var (nodes, edges, ms) = Build(_map);
            string saved = SaveToDisk() ? "saved to cache" : "CACHE SAVE FAILED";
            return $"HPA* built: {nodes:n0} nodes, {edges:n0} edges, in {ms:0} ms ({saved}).";
        }

        public static bool SaveToDisk()
        {
            try
            {
                var dir = Path.GetDirectoryName(CachePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using var fs = File.Create(CachePath);
                using var w = new BinaryWriter(fs);

                w.Write(CacheVersion);
                w.Write(_map.Width);
                w.Write(_map.Height);
                w.Write(ClusterSize);
                w.Write(_nodes.Count);

                // Stable node ordering by name so edge indices are consistent.
                var ordered = _nodes.Values.OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
                var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < ordered.Count; i++) indexOf[ordered[i].Name] = i;

                // Nodes: x, y, z
                foreach (var n in ordered)
                {
                    w.Write(n.Location.X);
                    w.Write(n.Location.Y);
                    w.Write(n.Location.Z);
                }
                // Edges: for each node, count then (neighborIndex, cost) pairs.
                // Write each undirected edge from both sides (simpler load).
                foreach (var n in ordered)
                {
                    w.Write(n.Edges.Count);
                    foreach (var (nb, cost) in n.Edges)
                    {
                        w.Write(indexOf[nb]);
                        w.Write(cost);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[hpa] SaveToDisk failed: {ex.Message}");
                return false;
            }
        }

        private static bool TryLoadFromDisk()
        {
            try
            {
                if (!File.Exists(CachePath)) return false;

                using var fs = File.OpenRead(CachePath);
                using var r = new BinaryReader(fs);

                if (r.ReadInt32() != CacheVersion) return false;
                if (r.ReadInt32() != _map.Width)  return false;
                if (r.ReadInt32() != _map.Height) return false;
                if (r.ReadInt32() != ClusterSize) return false;

                int count = r.ReadInt32();
                _nodes.Clear();
                var ordered = new HpaNode[count];

                for (int i = 0; i < count; i++)
                {
                    int x = r.ReadInt32(), y = r.ReadInt32(), z = r.ReadInt32();
                    var n = new HpaNode { Name = NameAt(x, y), Location = new Point3D(x, y, z) };
                    ordered[i] = n;
                    _nodes[n.Name] = n;
                }
                for (int i = 0; i < count; i++)
                {
                    int ec = r.ReadInt32();
                    for (int j = 0; j < ec; j++)
                    {
                        int nbIdx = r.ReadInt32();
                        double cost = r.ReadDouble();
                        ordered[i].Edges[ordered[nbIdx].Name] = cost;
                    }
                }
                if (_nodes.Count > 0) RebuildSpatialIndex(); ComputeComponents();
                return _nodes.Count > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[hpa] cache load failed ({ex.Message}); will rebuild.");
                _nodes.Clear();
                return false;
            }
        }

        // ---- Build ----------------------------------------------------------
        public static (int nodes, int edges, double ms) Build(Map map = null)
        {
            var start = DateTime.UtcNow;
            _map = map ?? Map.Felucca;
            _nodes.Clear();

            int W = _map.Width, H = _map.Height;
            int ncx = W / ClusterSize, ncy = H / ClusterSize;

            void Phase(string label, DateTime since)
                => Console.WriteLine($"[hpa]   phase '{label}': {(DateTime.UtcNow - since).TotalMilliseconds:0} ms, nodes so far {_nodes.Count}");

            var tBorders = DateTime.UtcNow;
            Console.WriteLine($"[hpa] START border detection: {ncx}x{ncy} clusters");
            // --- vertical borders (between cluster cx and cx+1) ---
            for (int cx = 0; cx < ncx - 1; cx++)
            {
                int bx = (cx + 1) * ClusterSize; // right side; border bx-1 | bx
                for (int cy = 0; cy < ncy; cy++)
                {
                    int y0 = cy * ClusterSize, y1 = y0 + ClusterSize;
                    EmitVerticalRuns(bx, y0, y1);
                }
            }
            // --- horizontal borders ---
            for (int cy = 0; cy < ncy - 1; cy++)
            {
                int by = (cy + 1) * ClusterSize;
                for (int cx = 0; cx < ncx; cx++)
                {
                    int x0 = cx * ClusterSize, x1 = x0 + ClusterSize;
                    EmitHorizontalRuns(by, x0, x1);
                }
            }
            Phase("border entrance detection", tBorders);

            // --- intra-cluster edges ---
            var tGroup = DateTime.UtcNow;
            var byCluster = new Dictionary<(int, int), List<HpaNode>>();
            foreach (var n in _nodes.Values)
            {
                var key = (n.Location.X / ClusterSize, n.Location.Y / ClusterSize);
                if (!byCluster.TryGetValue(key, out var list))
                    byCluster[key] = list = new List<HpaNode>();
                list.Add(n);
            }
            Phase("grouping", tGroup);

            var tPair = DateTime.UtcNow;
            Console.WriteLine($"[hpa] START intra-cluster linking: {byCluster.Count} non-empty clusters");
            int biggest = 0, done = 0;
            foreach (var kv in byCluster)
            {
                done++;
                if (done % 200 == 0)
                    Console.WriteLine($"[hpa]   ...linking {done}/{byCluster.Count} " +
                        $"({(DateTime.UtcNow - tPair).TotalMilliseconds:0} ms)");
                var list = kv.Value;
                if (list.Count > biggest) biggest = list.Count;
                int cx0 = kv.Key.Item1 * ClusterSize, cy0 = kv.Key.Item2 * ClusterSize;

                // Per-ENTRANCE flood (one bounded Dijkstra from each entrance,
                // NOT per pair). Only links entrances that are actually
                // connected by walkable tiles WITHIN the cluster. This is what
                // makes coastal clusters correct: two entrances separated by a
                // water channel never get an edge, so abstract A* can't route
                // a bot across the ocean. Cost is O(entrances * clusterArea)
                // per cluster, not O(entrances^2 * area) — affordable.
                LinkClusterByFlood(list, cx0, cy0, ClusterSize);
            }
            Console.WriteLine($"[hpa]   biggest cluster entrance count: {biggest}");
            Phase("intra-cluster linking", tPair);

            RebuildSpatialIndex(); ComputeComponents();
            var ms = (DateTime.UtcNow - start).TotalMilliseconds;
            return (_nodes.Count, EdgeCount, ms);
        }

        // Emit entrance node pairs along a vertical border at column bx,
        // scanning rows [y0,y1). bx-1 is left cluster, bx is right cluster.
        private static void EmitVerticalRuns(int bx, int y0, int y1)
        {
            var run = new List<int>();
            void flush()
            {
                if (run.Count == 0) return;
                // Emit crossings at several points along the run (endpoints
                // plus every ~16 tiles), not just the midpoint. One crossing
                // per border run was too sparse — a single obstacle could
                // sever two clusters and shatter the graph into components.
                // Multiple bridges per border make connectivity robust.
                int step = 16;
                var emitted = new HashSet<int>();
                void emitAt(int idx)
                {
                    if (idx < 0 || idx >= run.Count) return;
                    if (!emitted.Add(idx)) return;
                    int yy = run[idx];
                    var na = GetOrAdd(bx - 1, yy);
                    var nb = GetOrAdd(bx, yy);
                    Link(na, nb, 1.0);
                }
                emitAt(0);
                emitAt(run.Count - 1);
                for (int k = step; k < run.Count - 1; k += step) emitAt(k);
                run.Clear();
            }
            for (int y = y0; y < y1; y++)
            {
                if (Walkable.CanStand(_map, bx - 1, y) &&
                    Walkable.CanStand(_map, bx, y))
                    run.Add(y);
                else
                    flush();
            }
            flush();
        }

        private static void EmitHorizontalRuns(int by, int x0, int x1)
        {
            var run = new List<int>();
            void flush()
            {
                if (run.Count == 0) return;
                int step = 16;
                var emitted = new HashSet<int>();
                void emitAt(int idx)
                {
                    if (idx < 0 || idx >= run.Count) return;
                    if (!emitted.Add(idx)) return;
                    int xx = run[idx];
                    var na = GetOrAdd(xx, by - 1);
                    var nb = GetOrAdd(xx, by);
                    Link(na, nb, 1.0);
                }
                emitAt(0);
                emitAt(run.Count - 1);
                for (int k = step; k < run.Count - 1; k += step) emitAt(k);
                run.Clear();
            }
            for (int x = x0; x < x1; x++)
            {
                if (Walkable.CanStand(_map, x, by - 1) &&
                    Walkable.CanStand(_map, x, by))
                    run.Add(x);
                else
                    flush();
            }
            flush();
        }

        // Link entrances within one cluster. Precomputes a walkability
        // bitmap for the cluster ONCE (one CanStand per tile), then floods
        // from each entrance over the cheap array. Without the bitmap, a
        // flood in a water-split cluster re-runs the expensive CanStand
        // (CanFit + Z scan) over the whole cluster for every entrance —
        // ~13M CanFit ops per coastal cluster, which froze the build. With
        // it, ~120k. Entrances unreachable by walkable tiles get no edge,
        // so coastal clusters correctly refuse to connect across water.
        private static void LinkClusterByFlood(List<HpaNode> ents, int bx0, int by0, int size)
        {
            if (ents.Count < 2) return;

            // Precompute walkability for the cluster box, once.
            var walk = new bool[size, size];
            for (int lx = 0; lx < size; lx++)
            for (int ly = 0; ly < size; ly++)
                walk[lx, ly] = Walkable.CanStand(_map, bx0 + lx, by0 + ly);

            // Entrance tile -> node, in local coords.
            var entAt = new Dictionary<(int, int), HpaNode>();
            foreach (var n in ents)
                entAt[(n.Location.X - bx0, n.Location.Y - by0)] = n;

            foreach (var src in ents)
            {
                int slx = src.Location.X - bx0, sly = src.Location.Y - by0;
                if (slx < 0 || slx >= size || sly < 0 || sly >= size) continue;
                if (!walk[slx, sly]) continue;

                var dist = new Dictionary<(int, int), double>();
                var pq = new SortedSet<(double d, int x, int y)>();
                dist[(slx, sly)] = 0;
                pq.Add((0, slx, sly));
                int found = 0, need = ents.Count - 1;

                while (pq.Count > 0 && found < need)
                {
                    var cur = pq.Min; pq.Remove(cur);
                    if (dist.TryGetValue((cur.x, cur.y), out var known) && known < cur.d)
                        continue;

                    if (entAt.TryGetValue((cur.x, cur.y), out var hit) && hit != src)
                    {
                        Link(src, hit, cur.d);
                        found++;
                    }

                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cur.x + dx, ny = cur.y + dy;
                        if (nx < 0 || nx >= size || ny < 0 || ny >= size) continue;
                        if (!walk[nx, ny]) continue;

                        double step = (dx != 0 && dy != 0) ? 1.414 : 1.0;
                        double nd = cur.d + step;
                        var k = (nx, ny);
                        if (!dist.TryGetValue(k, out var old) || nd < old)
                        {
                            dist[k] = nd;
                            pq.Add((nd, nx, ny));
                        }
                    }
                }
            }
        }

        // ---- Spatial index for Nearest() ------------------------------------
        // 60k+ nodes makes a linear scan per Nearest() call a main-thread
        // killer (every bot plan does 1-2 lookups, re-rolls multiply it).
        // Bucket nodes into a coarse grid; lookups check expanding rings of
        // buckets. Rebuilt whenever the node set changes (build/load).
        private const int BucketSize = 128;
        private static Dictionary<(int, int), List<HpaNode>> _buckets;

        // Component id per node, computed once per build/load. Lets FindPath
        // reject unreachable pairs INSTANTLY instead of A*-exhausting the
        // whole ~50k-node mainland component before concluding NO PATH (that
        // full-component sweep, multiplied by island re-rolls across hundreds
        // of boot-time bots, was freezing startup).
        private static Dictionary<string, int> _component;
        public static int ComponentCount { get; private set; }

        private static void ComputeComponents()
        {
            _component = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int id = 0;
            foreach (var start in _nodes.Keys)
            {
                if (_component.ContainsKey(start)) continue;
                id++;
                var stack = new Stack<string>();
                stack.Push(start); _component[start] = id;
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    if (!_nodes.TryGetValue(c, out var node)) continue;
                    foreach (var nb in node.Edges.Keys)
                        if (!_component.ContainsKey(nb))
                        { _component[nb] = id; stack.Push(nb); }
                }
            }
            ComponentCount = id;
        }

        public static int ComponentOf(string name) =>
            _component != null && name != null && _component.TryGetValue(name, out var c) ? c : -1;

        // Nearest node to `from` that belongs to `component`, within
        // `maxRadius` tiles. Used so a bot routes FROM a node that can
        // actually reach its destination, instead of from a stranded pocket
        // node that happens to be a few tiles closer. Radius-capped so we
        // never hand back a node across the sea (the LOST-rescue teleport
        // would yank the bot to it).
        // Nearest node satisfying an arbitrary tile predicate (e.g. "the
        // destination's distance field covers this node"), within maxRadius.
        // Used to pick route targets the FINAL APPROACH can provably finish
        // from — plain Nearest can return a node across a water channel
        // (Skara/Jhelom shores), stranding the bot in an arrive/approach loop.
        public static string NearestWhere(Point3D from, Func<int, int, bool> accept, int maxRadius)
        {
            if (_nodes.Count == 0 || accept == null) return null;
            if (_buckets == null) { RebuildSpatialIndex(); ComputeComponents(); }

            int bx = from.X / BucketSize, by = from.Y / BucketSize;
            int maxRing = maxRadius / BucketSize + 2;
            HpaNode best = null; double bestD = double.MaxValue;
            double maxD = (double)maxRadius * maxRadius;

            for (int r = 0; r <= maxRing; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (r > 0 && Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    if (!_buckets.TryGetValue((bx + dx, by + dy), out var list)) continue;
                    foreach (var n in list)
                    {
                        if (!accept(n.Location.X, n.Location.Y)) continue;
                        double ddx = n.Location.X - from.X, ddy = n.Location.Y - from.Y;
                        double d = ddx * ddx + ddy * ddy;
                        if (d <= maxD && d < bestD) { bestD = d; best = n; }
                    }
                }
            }
            return best?.Name;
        }

        public static HpaNode NearestInComponent(Point3D from, int component, int maxRadius)
        {
            if (_nodes.Count == 0 || component <= 0) return null;
            if (_buckets == null) { RebuildSpatialIndex(); ComputeComponents(); }

            int bx = from.X / BucketSize, by = from.Y / BucketSize;
            int maxRing = maxRadius / BucketSize + 2;
            HpaNode best = null; double bestD = double.MaxValue;
            double maxD = (double)maxRadius * maxRadius;

            for (int r = 0; r <= maxRing; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (r > 0 && Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    if (!_buckets.TryGetValue((bx + dx, by + dy), out var list)) continue;
                    foreach (var n in list)
                    {
                        if (ComponentOf(n.Name) != component) continue;
                        double ddx = n.Location.X - from.X, ddy = n.Location.Y - from.Y;
                        double d = ddx * ddx + ddy * ddy;
                        if (d <= maxD && d < bestD) { bestD = d; best = n; }
                    }
                }
            }
            return best;
        }

        private static void RebuildSpatialIndex()
        {
            _buckets = new Dictionary<(int, int), List<HpaNode>>();
            foreach (var n in _nodes.Values)
            {
                var k = (n.Location.X / BucketSize, n.Location.Y / BucketSize);
                if (!_buckets.TryGetValue(k, out var list))
                    _buckets[k] = list = new List<HpaNode>();
                list.Add(n);
            }
        }

        // ---- Query: nearest node to a coord --------------------------------
        public static string Nearest(Point3D from)
        {
            if (_nodes.Count == 0) return null;
            if (_buckets == null) { RebuildSpatialIndex(); ComputeComponents(); }

            int bx = from.X / BucketSize, by = from.Y / BucketSize;
            HpaNode best = null; double bestD = double.MaxValue;

            // Expand ring by ring until we've found a candidate AND searched
            // one ring beyond it (a nearer node can't be further out than
            // that, given bucket geometry).
            for (int r = 0; r < 64; r++)
            {
                bool any = false;
                for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (r > 0 && Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    if (!_buckets.TryGetValue((bx + dx, by + dy), out var list)) continue;
                    any = true;
                    foreach (var n in list)
                    {
                        double ddx = n.Location.X - from.X, ddy = n.Location.Y - from.Y;
                        double d = ddx * ddx + ddy * ddy;
                        if (d < bestD) { bestD = d; best = n; }
                    }
                }
                // Found something and current ring is beyond the best hit's
                // possible improvement range — stop.
                if (best != null && (double)((r - 1) * BucketSize) * ((r - 1) * BucketSize) > bestD && r > 1)
                    break;
                if (!any && best != null && r > 2) break;
            }
            return best?.Name;
        }

        // API-compatible with WaypointGraph.FindNearestNode: returns the node
        // object nearest to the coord (or null if the graph is empty).
        public static HpaNode FindNearestNode(Point3D from)
        {
            var name = Nearest(from);
            return name == null ? null : Get(name);
        }

        public static string PickRandomName()
        {
            if (_nodes.Count == 0) return null;
            int idx = Utility.Random(_nodes.Count);
            foreach (var k in _nodes.Keys)
                if (idx-- == 0) return k;
            return null;
        }

        // ---- Query: abstract A* over the node graph ------------------------
        public static List<string> FindPath(string fromName, string toName)
        {
            var result = new List<string>();
            if (!_nodes.ContainsKey(fromName) || !_nodes.ContainsKey(toName))
                return result;
            if (string.Equals(fromName, toName, StringComparison.OrdinalIgnoreCase))
            { result.Add(fromName); return result; }

            // Different connected components -> unreachable. Return empty
            // immediately instead of exhausting the whole component via A*.
            if (_component != null &&
                _component.TryGetValue(fromName, out var ca) &&
                _component.TryGetValue(toName, out var cb) && ca != cb)
                return result;

            var goal = _nodes[toName].Location;
            var open = new SortedSet<(double f, string name)>(
                Comparer<(double f, string name)>.Create((a, b) =>
                    a.f != b.f ? a.f.CompareTo(b.f)
                               : string.CompareOrdinal(a.name, b.name)));
            var gScore = new Dictionary<string, double> { [fromName] = 0 };
            var came = new Dictionary<string, string>();
            open.Add((0, fromName));

            while (open.Count > 0)
            {
                var cur = open.Min; open.Remove(cur);
                if (cur.name == toName) break;

                var node = _nodes[cur.name];
                double cg = gScore[cur.name];
                foreach (var (nbName, cost) in node.Edges)
                {
                    double ng = cg + cost;
                    if (!gScore.TryGetValue(nbName, out var old) || ng < old)
                    {
                        gScore[nbName] = ng;
                        came[nbName] = cur.name;
                        var nb = _nodes[nbName].Location;
                        double h = Math.Sqrt(
                            Math.Pow(nb.X - goal.X, 2) + Math.Pow(nb.Y - goal.Y, 2));
                        open.Add((ng + h, nbName));
                    }
                }
            }

            if (!came.ContainsKey(toName) && fromName != toName)
                return result; // no path

            // reconstruct
            var path = new List<string>();
            string c = toName;
            path.Add(c);
            while (came.TryGetValue(c, out var prev))
            {
                path.Add(prev); c = prev;
            }
            path.Reverse();
            return path;
        }
    }
}
