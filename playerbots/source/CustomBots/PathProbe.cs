// =========================================================================
// PathProbe.cs — test hook: can a bot standing HERE walk to the waypoints
// around it?
//
// Write Data/Live/pathprobe_request.txt, one "x y z" per line. For each
// spot the probe takes the four nearest waypoints (what a trip plugs into
// the graph at) and runs the real A* from the spot to each, once at the
// waypoint's authored Z and once at the floor height found there.
// Results go to Data/Live/pathprobe_result.txt.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using Server.PathAlgorithms;

namespace Server.CustomBots
{
    public static class PathProbe
    {
        private static string Req => Path.Combine(Core.BaseDirectory, "Data", "Live", "pathprobe_request.txt");
        private static string Res => Path.Combine(Core.BaseDirectory, "Data", "Live", "pathprobe_result.txt");

        public static void Initialize()
        {
            Timer.DelayCall(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), Poll);
        }

        private static void Poll()
        {
            if (!File.Exists(Req))
            {
                return;
            }
            string[] lines;
            try
            {
                lines = File.ReadAllLines(Req);
                File.Delete(Req);
            }
            catch
            {
                return;
            }

            PlayerBot probe = null;
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot b && b.Alive && b.Map == Map.Felucca)
                {
                    probe = b;
                    break;
                }
            }
            var graph = WaypointRegistry.Graph;
            var outp = new List<string>();
            if (probe == null || graph == null)
            {
                outp.Add("no bot or no graph");
                File.WriteAllLines(Res, outp);
                return;
            }

            var map = Map.Felucca;
            var alg = BitmapAStarAlgorithm.Instance;
            int budget = alg.MaxSearchNodes;
            var near = new List<WaypointNode>(4);
            if (lines.Length > 0 && lines[0].Trim() == "audit")
            {
                Audit(probe, map, graph, alg, outp);
                File.WriteAllLines(Res, outp);
                return;
            }
            foreach (var line in lines)
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 3 || !int.TryParse(p[0], out var x) || !int.TryParse(p[1], out var y) ||
                    !int.TryParse(p[2], out var z))
                {
                    continue;
                }
                var from = new Point3D(x, y, z);
                if (p.Length >= 4 && p[3] == "exit")
                {
                    var way = ExitWalk.FindWayOut(probe, from, out var exitNode, out var sealedIn, out var flooded);
                    outp.Add($"exit {from}: {(way == null ? "none" : way.Count + " steps to " + exitNode.Name + " " + exitNode.Location)} sealed={sealedIn} flooded={flooded}");
                    if (way != null)
                    {
                        outp.Add("  " + string.Join(" ", way));
                    }
                    continue;
                }
                if (p.Length >= 7 && p[6] == "zroute" && int.TryParse(p[3], out var zx) &&
                    int.TryParse(p[4], out var zy) && int.TryParse(p[5], out var zz))
                {
                    var r = ZoneNav.FindRoute(map, from, new Point3D(zx, zy, zz));
                    if (r == null)
                    {
                        outp.Add($"zroute {from}: none");
                        continue;
                    }
                    outp.Add($"zroute {from} -> ({zx},{zy},{zz}): {r.Zones.Count} zones");
                    for (int i = 0; i < r.Zones.Count; i++)
                    {
                        var t = i < r.Targets.Count ? r.Targets[i].ToString() : "-";
                        outp.Add($"  {r.Zones[i].Name} -> target {t}");
                    }
                    continue;
                }
                if (p.Length >= 7 && p[6] == "path" && int.TryParse(p[3], out var x2) &&
                    int.TryParse(p[4], out var y2) && int.TryParse(p[5], out var z2))
                {
                    var to = new Point3D(x2, y2, z2);
                    var dirs = alg.Find(probe, map, from, to);
                    outp.Add($"path {from} -> {to}: astar={(dirs == null ? "fail" : dirs.Length + " steps")}");
                    continue;
                }
                near.Clear();
                graph.FindNearestNodes(from, 4, near);
                var house = Server.Multis.BaseHouse.FindHouseAt(from, map, 20);
                var region = Region.Find(from, map);
                var zone = ZoneRegistry.MeshZoneAt(from);
                int doors = 0, locked = 0;
                foreach (var it in map.GetItemsInRange<Server.Items.BaseDoor>(from, 12))
                {
                    doors++;
                    if (it.Locked)
                    {
                        locked++;
                    }
                }
                foreach (var it in map.GetItemsInRange<Server.Items.BaseDoor>(from, 10))
                {
                    outp.Add($"  door {it.GetType().Name} 0x{it.ItemID:X4} at {it.Location} open={it.Open} locked={it.Locked}");
                }
                outp.Add($"spot {from} house={(house == null ? "-" : house.GetType().Name + " owner=" + house.Owner?.Name)} " +
                         $"region={region?.Name ?? "-"} zone={zone?.Name ?? "-"} doors12={doors} locked={locked}");
                var opened = new List<Server.Items.BaseDoor>();
                for (int pass = 0; pass < (p.Length >= 4 && p[3] == "grid" ? 2 : 0); pass++)
                {
                    if (pass == 1)
                    {
                        foreach (var dr in map.GetItemsInRange<Server.Items.BaseDoor>(from, 10))
                        {
                            if (!dr.Open)
                            {
                                opened.Add(dr);
                            }
                        }
                        foreach (var dr in opened)
                        {
                            dr.Open = true;
                        }
                        outp.Add($"  -- with {opened.Count} closed doors opened and a 20000-node budget:");
                        alg.MaxSearchNodes = 20000;
                    }
                    // Which tiles in a 21x21 box can A* reach from the spot?
                    // @ spot, W waypoint, . reached, # not, digits = reached
                    // at another floor (tens of Z).
                    for (int gy = -10; gy <= 10; gy++)
                    {
                        var row = new System.Text.StringBuilder("    ");
                        for (int gx = -10; gx <= 10; gx++)
                        {
                            int tx = x + gx, ty = y + gy;
                            if (gx == 0 && gy == 0)
                            {
                                row.Append('@');
                                continue;
                            }
                            bool isWp = false;
                            foreach (var n in near)
                            {
                                isWp |= n.Location.X == tx && n.Location.Y == ty;
                            }
                            int tz = map.GetAverageZ(tx, ty);
                            bool standable = Walkable.TryFindSeedZ(map, tx, ty, z, out var fz) && map.CanFit(tx, ty, fz, 16, false, false);
                            bool reach = alg.Find(probe, map, from, new Point3D(tx, ty, fz)) != null;
                            bool isDoor = false;
                            foreach (var dr in map.GetItemsAt<Server.Items.BaseDoor>(new Point2D(tx, ty)))
                            {
                                isDoor = true;
                            }
                            if (isDoor)
                            {
                                row.Append(reach ? 'd' : 'D');
                                continue;
                            }
                            row.Append(isWp ? (reach ? 'W' : 'X') : reach ? (Math.Abs(fz - z) >= 10 ? (char)('0' + Math.Clamp(fz / 10, 0, 9)) : '.') : standable ? 'o' : '#');
                        }
                        outp.Add(row.ToString());
                    }
                }
                foreach (var dr in opened)
                {
                    dr.Open = false;
                }
                alg.MaxSearchNodes = budget;
                foreach (var n in near)
                {
                    var g = n.Location;
                    bool ok = alg.Find(probe, map, from, g) != null;
                    Walkable.TryFindSeedZ(map, g.X, g.Y, g.Z, out var seedZ);
                    bool okSeed = seedZ != g.Z && alg.Find(probe, map, from, new Point3D(g.X, g.Y, seedZ)) != null;
                    int d = Math.Max(Math.Abs(g.X - x), Math.Abs(g.Y - y));
                    outp.Add($"  {n.Name,-28} {g} d={d} astar={ok} floorZ={seedZ} astarAtFloor={okSeed}");
                }
            }
            File.WriteAllLines(Res, outp);
        }

        private static int Plane(int z) => (z + 128) / 20;

        // Every surface waypoint: its stored Z against the floor under it,
        // and every edge walked by the real A* both as stored and at the
        // floors. Dungeons (x >= 5120) are left out.
        private static void Audit(PlayerBot probe, Map map, WaypointGraph graph, BitmapAStarAlgorithm alg, List<string> outp)
        {
            var floor = new Dictionary<string, Point3D>();
            int nodes = 0, badZ = 0;
            foreach (var n in graph.AllNodes)
            {
                var l = n.Location;
                if (l.X >= 5120)
                {
                    continue;
                }
                nodes++;
                Walkable.TryFindSeedZ(map, l.X, l.Y, l.Z, out var fz);
                floor[n.Name] = new Point3D(l.X, l.Y, fz);
                if (fz != l.Z)
                {
                    badZ++;
                    outp.Add($"Z {n.Name}	{l.X}	{l.Y}	{l.Z}	{fz}");
                }
            }
            int edges = 0, failStored = 0, failFloor = 0, tooLong = 0;
            var seen = new HashSet<string>();
            foreach (var n in graph.AllNodes)
            {
                if (!floor.TryGetValue(n.Name, out var fa))
                {
                    continue;
                }
                foreach (var to in n.Connects)
                {
                    var m = graph.Get(to);
                    if (m == null || !floor.TryGetValue(to, out var fb))
                    {
                        continue;
                    }
                    var key = string.CompareOrdinal(n.Name, to) < 0 ? n.Name + "|" + to : to + "|" + n.Name;
                    if (!seen.Add(key))
                    {
                        continue;
                    }
                    edges++;
                    int d = Math.Max(Math.Abs(fa.X - fb.X), Math.Abs(fa.Y - fb.Y));
                    if (d > 37)
                    {
                        tooLong++;
                        outp.Add($"LONG {n.Name}	{to}	{d}");
                        continue;
                    }
                    bool okS = alg.Find(probe, map, n.Location, m.Location) != null;
                    bool okF = alg.Find(probe, map, fa, fb) != null;
                    bool okR = alg.Find(probe, map, fb, fa) != null;
                    if (!okS)
                    {
                        failStored++;
                    }
                    if (!okF || !okR)
                    {
                        failFloor++;
                    }
                    if (!okS || !okF || !okR)
                    {
                        outp.Add($"EDGE {n.Name}	{to}	{d}	stored={okS}	floor={okF}	reverse={okR}");
                    }
                }
            }
            // Destinations: can a bot standing on the spot (as stored, the
            // way a Recall landing reads it) walk to the destination's
            // waypoint?
            int dests = 0, destFail = 0, crowd = 0;
            PlayerBot ghost = null;
            foreach (var mm in World.Mobiles.Values)
            {
                if (mm is PlayerBot gb && !gb.Alive && gb.Map == map)
                {
                    ghost = gb;
                    break;
                }
            }
            foreach (var d in DestinationCatalog.All)
            {
                var at = d.ArrivalPoint ?? d.Location;
                if (at.X >= 5120 || !string.IsNullOrEmpty(d.Dungeon) || string.IsNullOrEmpty(d.NearestWaypoint) ||
                    !floor.TryGetValue(d.NearestWaypoint, out var wf))
                {
                    continue;
                }
                dests++;
                int dist = Math.Max(Math.Abs(at.X - wf.X), Math.Abs(at.Y - wf.Y));
                if (dist > 37)
                {
                    continue;
                }
                bool standable = Walkable.TryFindSeedZ(map, at.X, at.Y, at.Z, out var fz) &&
                    map.CanFit(at.X, at.Y, fz, 16, false, false);
                var start = new Point3D(at.X, at.Y, fz);
                bool ok = alg.Find(probe, map, start, wf) != null;
                if (!ok)
                {
                    destFail++;
                    // A ghost is not blocked by other mobiles: tells a room
                    // sealed by walls from one sealed by a crowd.
                    bool ghostOk = ghost != null && alg.Find(ghost, map, start, wf) != null;
                    if (ghostOk)
                    {
                        crowd++;
                    }
                    outp.Add($"DEST {d.Name}	{d.Type}	{at.X}	{at.Y}	{at.Z}	floor={fz}	standable={standable}	wp={d.NearestWaypoint}	d={dist}	ghostOk={ghostOk}");
                }
            }
            outp.Insert(0, $"dests {dests} destFail {destFail} crowdOnly {crowd} ghost={ghost?.Name ?? "none"}");
            outp.Insert(0, $"nodes {nodes} badZ {badZ} edges {edges} failStored {failStored} failFloor {failFloor} tooLong {tooLong}");
        }
    }
}
