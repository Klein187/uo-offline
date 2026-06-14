// =========================================================================
// DistanceField.cs — Precomputed "downhill toward this destination" field
// for the FINAL APPROACH. Bounded Dijkstra flood from the destination tile
// over walkable tiles, carrying a resolved Z per tile so it threads building
// interiors / floors correctly.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public sealed class DistanceField
    {
        private readonly Dictionary<long, int> _cost = new();
        private readonly Dictionary<long, int> _z    = new();  // resolved Z per tile
        private readonly Point3D _goal;
        private readonly int _radius;

        public Point3D Goal => _goal;
        public int Radius => _radius;
        public int CoveredTiles => _cost.Count;

        private DistanceField(Point3D goal, int radius)
        {
            _goal = goal;
            _radius = radius;
        }

        private static long Key(int x, int y) => ((long)(uint)x << 32) | (uint)y;

        public bool Covers(int x, int y) => _cost.ContainsKey(Key(x, y));

        // Resolved standing Z at a covered tile (for facing/teleport use).
        public bool TryZ(int x, int y, out int z) => _z.TryGetValue(Key(x, y), out z);

        public bool TryStep(int x, int y, out int nx, out int ny)
        {
            nx = x; ny = y;
            if (!_cost.TryGetValue(Key(x, y), out int here) || here == 0)
                return false;

            int best = here;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                if (_cost.TryGetValue(Key(x + dx, y + dy), out int c) && c < best)
                {
                    best = c; nx = x + dx; ny = y + dy;
                }
            }
            return best < here;
        }

        // ---- Build: bounded Dijkstra flood from the goal, carrying Z. -----
        // ---- binary cache serialization (see DestinationFieldCache) ------
        internal void WriteTo(System.IO.BinaryWriter w)
        {
            w.Write(_goal.X); w.Write(_goal.Y); w.Write(_goal.Z);
            w.Write(_radius);
            w.Write(_cost.Count);
            foreach (var kv in _cost) { w.Write(kv.Key); w.Write(kv.Value); }
            w.Write(_z.Count);
            foreach (var kv in _z) { w.Write(kv.Key); w.Write(kv.Value); }
        }

        internal static DistanceField ReadFrom(System.IO.BinaryReader r)
        {
            var goal = new Point3D(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            int radius = r.ReadInt32();
            var f = new DistanceField(goal, radius);
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            { long k = r.ReadInt64(); int c = r.ReadInt32(); f._cost[k] = c; }
            int m = r.ReadInt32();
            for (int i = 0; i < m; i++)
            { long k = r.ReadInt64(); int z = r.ReadInt32(); f._z[k] = z; }
            return f;
        }

        // Walk cost from the goal to (x,y), or -1 if not covered.
        internal int CostAt(int x, int y) =>
            _cost.TryGetValue(Key(x, y), out var c) ? c : -1;

        public static DistanceField Build(Map map, Point3D goal, int radius)
        {
            var field = new DistanceField(goal, radius);
            if (map == null || map == Map.Internal) return field;

            int sx = goal.X, sy = goal.Y;

            // Resolve a real standing Z at the goal tile to seed the flood.
            // If the stored Z (often 0) doesn't fit, probe for one.
            if (!Walkable.TryFindSeedZ(map, sx, sy, goal.Z, out int seedZ))
            {
                // Goal itself isn't standable at any candidate Z — leave the
                // field as just the goal tile so [fieldinfo flags it.
                field._cost[Key(sx, sy)] = 0;
                field._z[Key(sx, sy)] = goal.Z;
                return field;
            }

            var pq = new SortedSet<(int cost, int x, int y)>();
            field._cost[Key(sx, sy)] = 0;
            field._z[Key(sx, sy)] = seedZ;
            pq.Add((0, sx, sy));

            while (pq.Count > 0)
            {
                var cur = pq.Min;
                pq.Remove(cur);
                int cx = cur.x, cy = cur.y, cc = cur.cost;

                if (field._cost.TryGetValue(Key(cx, cy), out int known) && known < cc)
                    continue;

                int cz = field._z[Key(cx, cy)];

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = cx + dx, ny = cy + dy;

                    if (Math.Abs(nx - sx) > radius || Math.Abs(ny - sy) > radius)
                        continue;

                    if (!Walkable.CanStep(map, cx, cy, cz, nx, ny, out int nz))
                        continue;

                    int step = (dx != 0 && dy != 0) ? 14 : 10;
                    int ncost = cc + step;

                    long k = Key(nx, ny);
                    if (!field._cost.TryGetValue(k, out int old) || ncost < old)
                    {
                        field._cost[k] = ncost;
                        field._z[k] = nz;
                        pq.Add((ncost, nx, ny));
                    }
                }
            }
            return field;
        }
    }
}
