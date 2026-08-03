// =========================================================================
// AuditNavCommand.cs — navigation-data integrity audit.
//
// Every multi-session navigation bug so far has been silently bad DATA:
// stale destination→waypoint links (Jhelom MAROONED storm), dungeon
// walk-target coords a couple tiles off the real teleporter pads (bots
// standing beside the pad until timeout), islands whose waypoint
// component contains no moongate (Buccaneer's Den gate ping-pong). All of
// it is mechanically detectable, so detect it:
//
//   [AuditNav          — run the audit, results to the caller + console.
//   world start        — the same audit runs once automatically and
//                        prints warnings to the console, so a bad data
//                        edit announces itself on the next boot.
//
// Checks:
//   1. STALE LINK    destination's NearestWaypoint is missing from the
//                    graph or sits > StaleLinkTiles from the destination
//                    (suggests the actual nearest node).
//   2. OFF-PAD       a DungeonEntrance/Descend/Ascend arrival spot is not
//                    on a real teleporter tile (validated against
//                    Data/teleporters.json: src tiles ∪ back=true dst
//                    tiles — the back-pads have no explicit src record).
//   3. NO GATE       a destination sits in a waypoint component that no
//                    moongate lands in — unreachable on foot AND by gate
//                    from anywhere else (dungeon-scoped points are exempt;
//                    crawlers own those).
//   4. LONG EDGE     graph edges over the A* search range (delegates to
//                    WaypointGraph.Validate).
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class AuditNavCommand
    {
        // A hand-link routing AROUND a wall is legitimately farther than
        // the crow flies — but the A* box is 38 tiles, so anything past
        // this is unwalkable from the node regardless of intent.
        private const int StaleLinkTiles = 40;

        public static void Configure()
        {
            CommandSystem.Register("AuditNav", AccessLevel.GameMaster, OnCommand);
        }

        // Runs after the world (and all the CustomBots registries) load.
        public static void Initialize()
        {
            var warnings = Run();
            if (warnings.Count == 0)
            {
                Console.WriteLine("[AuditNav] nav data clean");
                return;
            }
            foreach (var w in warnings)
            {
                Console.WriteLine($"[AuditNav] {w}");
            }
            Console.WriteLine($"[AuditNav] {warnings.Count} warning(s) — see above");
        }

        [Usage("AuditNav")]
        [Description("Audit destinations/waypoints/teleporter data for stale links, off-pad dungeon points, unreachable islands, and long edges.")]
        public static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            var warnings = Run();

            foreach (var w in warnings)
            {
                Console.WriteLine($"[AuditNav] {w}");
            }

            if (from != null)
            {
                if (warnings.Count == 0)
                {
                    from.SendMessage(0x40, "AuditNav: nav data clean.");
                    return;
                }
                // Chat gets a capped view; the console has the full list.
                const int cap = 15;
                for (int i = 0; i < warnings.Count && i < cap; i++)
                {
                    from.SendMessage(0x22, warnings[i]);
                }
                if (warnings.Count > cap)
                {
                    from.SendMessage(0x22,
                        $"...and {warnings.Count - cap} more (full list on the console).");
                }
            }
        }

        // -------------------------------------------------------------------
        // The audit proper. Pure data checks — safe to run any time after
        // the registries load.
        // -------------------------------------------------------------------
        public static List<string> Run()
        {
            var warnings = new List<string>();
            var graph = WaypointRegistry.Graph;

            if (graph == null || graph.NodeCount == 0)
            {
                warnings.Add("waypoint graph is empty — no checks possible");
                return warnings;
            }

            var pads = LoadTeleporterPads(warnings);

            // Component id → does any moongate's route node live there?
            // (Ferries removed — gates are the only walk-in portals now, so
            // an island without a gate IS a gateless island. That's the
            // T2A truth: you sailed your own boat or you recalled.)
            var gatedComponents = new HashSet<int>();
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.Moongate)
                {
                    continue;
                }
                int comp = graph.ComponentOf(RouteNode(graph, d));
                if (comp >= 0)
                {
                    gatedComponents.Add(comp);
                }
            }

            // Gateless-island offenders grouped per component so one bad
            // island is one warning, not twenty.
            var gateless = new Dictionary<int, List<string>>();

            foreach (var d in DestinationCatalog.All)
            {
                bool isDungeonPoint =
                    d.Type == DestinationType.DungeonEntrance ||
                    d.Type == DestinationType.DungeonDescend ||
                    d.Type == DestinationType.DungeonAscend ||
                    d.Type == DestinationType.DungeonRoom;

                // ---- 1. stale link ----
                if (!string.IsNullOrEmpty(d.NearestWaypoint))
                {
                    var node = graph.Get(d.NearestWaypoint);
                    if (node == null)
                    {
                        var best = graph.FindNearestNode(d.Location);
                        warnings.Add(
                            $"STALE LINK: '{d.Name}' → waypoint '{d.NearestWaypoint}' " +
                            $"is not in the graph" +
                            (best != null ? $" (nearest is '{best.Name}')" : ""));
                    }
                    else
                    {
                        int dist = Cheb(d.Location, node.Location);
                        if (dist > StaleLinkTiles)
                        {
                            var best = graph.FindNearestNode(d.Location);
                            warnings.Add(
                                $"STALE LINK: '{d.Name}' → '{d.NearestWaypoint}' is " +
                                $"{dist} tiles away" +
                                (best != null && best != node
                                    ? $" (nearest is '{best.Name}' at {Cheb(d.Location, best.Location)})"
                                    : ""));
                        }
                    }
                }

                // ---- 2. dungeon walk-targets on real pads ----
                if (pads != null &&
                    (d.Type == DestinationType.DungeonEntrance ||
                     d.Type == DestinationType.DungeonDescend ||
                     d.Type == DestinationType.DungeonAscend))
                {
                    if (d.Arrivals != null && d.Arrivals.Count > 0)
                    {
                        foreach (var spot in d.Arrivals)
                        {
                            if (!pads.Contains((spot.Point.X, spot.Point.Y)))
                            {
                                warnings.Add(
                                    $"OFF-PAD: '{d.Name}' arrival spot " +
                                    $"({spot.Point.X},{spot.Point.Y}) is not a teleporter tile");
                            }
                        }
                    }
                    else
                    {
                        var walk = d.ArrivalPoint ?? d.Location;
                        if (!pads.Contains((walk.X, walk.Y)))
                        {
                            warnings.Add(
                                $"OFF-PAD: '{d.Name}' walk target ({walk.X},{walk.Y}) " +
                                $"is not a teleporter tile");
                        }
                    }
                }

                // ---- 3. gateless island ----
                if (!isDungeonPoint && d.Type != DestinationType.Moongate)
                {
                    int comp = graph.ComponentOf(RouteNode(graph, d));
                    if (comp >= 0 && !gatedComponents.Contains(comp))
                    {
                        if (!gateless.TryGetValue(comp, out var names))
                        {
                            gateless[comp] = names = new List<string>();
                        }
                        names.Add(d.Name);
                    }
                }
            }

            foreach (var (comp, names) in gateless)
            {
                warnings.Add(
                    $"NO GATE: waypoint component #{comp} has {names.Count} " +
                    $"destination(s) but no moongate lands there — unreachable " +
                    $"from elsewhere: {string.Join(", ", names)}");
            }

            // ---- 4. long edges / dangling neighbors ----
            foreach (var w in graph.Validate())
            {
                warnings.Add($"EDGE: {w}");
            }

            return warnings;
        }

        // The node routing actually uses for a destination: the authored
        // link when it exists in the graph, else the computed nearest.
        private static string RouteNode(WaypointGraph graph, BotDestination d)
        {
            if (!string.IsNullOrEmpty(d.NearestWaypoint) &&
                graph.Get(d.NearestWaypoint) != null)
            {
                return d.NearestWaypoint;
            }
            return graph.FindNearestNode(d.Location)?.Name;
        }

        // Every tile a teleporter fires from: src tiles, plus dst tiles of
        // back=true records (those pads exist in-game with no src entry of
        // their own — searching only src misses them; that gap hid the
        // Despise entrance-coord bug for days).
        private static HashSet<(int x, int y)> LoadTeleporterPads(List<string> warnings)
        {
            var path = Path.Combine(Core.BaseDirectory, "Data", "teleporters.json");
            if (!File.Exists(path))
            {
                warnings.Add($"teleporters.json not found at {path} — pad checks skipped");
                return null;
            }

            try
            {
                var pads = new HashSet<(int, int)>();
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    bool back = el.TryGetProperty("back", out var bv) && bv.GetBoolean();

                    if (el.TryGetProperty("src", out var src) &&
                        src.TryGetProperty("loc", out var sloc) &&
                        sloc.GetArrayLength() >= 2)
                    {
                        pads.Add((sloc[0].GetInt32(), sloc[1].GetInt32()));
                    }
                    if (back &&
                        el.TryGetProperty("dst", out var dst) &&
                        dst.TryGetProperty("loc", out var dloc) &&
                        dloc.GetArrayLength() >= 2)
                    {
                        pads.Add((dloc[0].GetInt32(), dloc[1].GetInt32()));
                    }
                }
                return pads;
            }
            catch (Exception ex)
            {
                warnings.Add($"teleporters.json parse failed ({ex.Message}) — pad checks skipped");
                return null;
            }
        }

        private static int Cheb(Point3D a, Point3D b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
