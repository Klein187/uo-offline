// =========================================================================
// BotStuckTelemetry.cs — the fleet-wide stuck ledger, chokepoint memory,
// and shared last-resort escapes.
//
// Every stuck round so far was diagnosed by hand-parsing console soak
// logs for FROZEN/STALLED/GIVING UP lines and bucketing them by spot
// with ad-hoc scripts. This file makes that loop first-class:
//
//   StuckTelemetry — every watchdog/rescue firing across all behaviors
//     calls Record(kind). Once a minute the rolling window is aggregated
//     into Data/Live/stuck_report.json (for tools) and a section of the
//     shard status page (for eyeballs): counts by kind, the current
//     hotspot list (events bucketed by spot, named via PlaceName + the
//     nearest waypoint), and the currently-penalized edges. A soak now
//     reports on itself.
//
//   NavEdgeHealth — when a Traveler gives up a leg after the full stuck
//     ladder, the graph EDGE it was walking takes a strike. FindPath
//     scales edge costs by live strikes (decaying after ~45 min), so the
//     whole fleet routes around a jamming edge where a detour exists —
//     one bot's failure becomes everyone's detour — instead of every bot
//     rediscovering the same chokepoint. Where no detour exists the
//     penalized edge still wins Dijkstra and bots keep trying it. The
//     penalized-edge list in the report doubles as the data-fix backlog:
//     an edge that keeps re-earning strikes needs a geometry pass.
//
//   BotStuckEscape — the physical last resorts shared by Traveler and
//     Adventurer/Crawler: a single sidestep in any open direction, and
//     the wedge extraction (teleport to the nearest engine-valid tile)
//     for a bot embedded in rock/decor that no amount of goal-picking
//     can ever free.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Server;

namespace Server.CustomBots
{
    public static class StuckTelemetry
    {
        // One recorded firing. Bot is stored by NAME (never the mobile —
        // entries outlive deletes). Loc is where the bot was when the
        // watchdog fired, i.e. the jam spot, not where a rescue sent it.
        private struct Entry
        {
            public DateTime At;
            public string Kind;
            public string Bot;
            public string Detail;
            public Point3D Loc;
            public Map Map;
        }

        private const int RingCapacity = 4096;
        private static readonly Entry[] _ring = new Entry[RingCapacity];
        private static int _ringNext;
        private static int _ringCount;

        // All-time (since boot) counts per kind — the ring only holds the
        // recent tail, but totals should survive a busy hour.
        private static readonly Dictionary<string, int> _totals =
            new(StringComparer.Ordinal);

        private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan DumpInterval = TimeSpan.FromSeconds(60);

        // Events are bucketed into 16x16-tile spots for the hotspot list.
        private const int SpotShift = 4;
        private const int TopSpots = 10;

        private static DateTime _bootAt;

        private static string ReportPath =>
            Path.Combine(Core.BaseDirectory, "Data", "Live", "stuck_report.json");

        public static void Configure()
        {
            _bootAt = Core.Now;
            NavEdgeHealth.Install();
            Timer.DelayCall(DumpInterval, DumpInterval, WriteReport);
        }

        // Cheap by design: one ring slot + one counter bump. All
        // aggregation happens on the cold dump timer.
        public static void Record(PlayerBot bot, string kind, string detail = null)
        {
            if (bot == null || string.IsNullOrEmpty(kind))
            {
                return;
            }

            _ring[_ringNext] = new Entry
            {
                At = Core.Now,
                Kind = kind,
                Bot = bot.Name,
                Detail = detail,
                Loc = bot.Location,
                Map = bot.Map,
            };
            _ringNext = (_ringNext + 1) % RingCapacity;
            if (_ringCount < RingCapacity)
            {
                _ringCount++;
            }

            _totals.TryGetValue(kind, out var n);
            _totals[kind] = n + 1;
        }

        // ---- Aggregation (cold path) ----

        private class SpotAgg
        {
            public Map Map;
            public int Bx, By;          // bucket coords (tile >> SpotShift)
            public int Count;
            public Dictionary<string, int> Kinds = new(StringComparer.Ordinal);
            public Entry Last;
        }

        private static void Aggregate(
            out Dictionary<string, int> windowKinds, out List<SpotAgg> topSpots)
        {
            windowKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var spots = new Dictionary<string, SpotAgg>(StringComparer.Ordinal);
            var cutoff = Core.Now - Window;

            for (int i = 0; i < _ringCount; i++)
            {
                ref var e = ref _ring[i];
                if (e.At < cutoff || e.Map == null)
                {
                    continue;
                }

                windowKinds.TryGetValue(e.Kind, out var kn);
                windowKinds[e.Kind] = kn + 1;

                int bx = e.Loc.X >> SpotShift;
                int by = e.Loc.Y >> SpotShift;
                var key = $"{e.Map.Name}:{bx}:{by}";
                if (!spots.TryGetValue(key, out var agg))
                {
                    spots[key] = agg = new SpotAgg { Map = e.Map, Bx = bx, By = by };
                }
                agg.Count++;
                agg.Kinds.TryGetValue(e.Kind, out var sk);
                agg.Kinds[e.Kind] = sk + 1;
                if (e.At >= agg.Last.At)
                {
                    agg.Last = e;
                }
            }

            topSpots = new List<SpotAgg>(spots.Values);
            topSpots.Sort((a, b) => b.Count.CompareTo(a.Count));
            if (topSpots.Count > TopSpots)
            {
                topSpots.RemoveRange(TopSpots, topSpots.Count - TopSpots);
            }
        }

        private static string SpotLabel(SpotAgg s)
        {
            int cx = (s.Bx << SpotShift) + (1 << (SpotShift - 1));
            int cy = (s.By << SpotShift) + (1 << (SpotShift - 1));
            var center = new Point3D(cx, cy, 0);
            var place = BotEventJournal.PlaceName(center, s.Map);
            var node = WaypointRegistry.Graph.FindNearestNode(center);
            return node != null
                ? $"{place} ({cx},{cy}) near {node.Name}"
                : $"{place} ({cx},{cy})";
        }

        private static string KindSummary(Dictionary<string, int> kinds)
        {
            var pairs = new List<KeyValuePair<string, int>>(kinds);
            pairs.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder(64);
            for (int i = 0; i < pairs.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(pairs[i].Key).Append(':').Append(pairs[i].Value);
            }
            return sb.ToString();
        }

        // ---- Outputs ----

        private static void WriteReport()
        {
            try
            {
                Aggregate(out var windowKinds, out var topSpots);
                var edges = NavEdgeHealth.Snapshot();

                var spotRows = new List<object>(topSpots.Count);
                foreach (var s in topSpots)
                {
                    spotRows.Add(new
                    {
                        place = SpotLabel(s),
                        map = s.Map.Name,
                        count = s.Count,
                        kinds = s.Kinds,
                        lastBot = s.Last.Bot,
                        lastDetail = s.Last.Detail,
                        lastAt = s.Last.At,
                    });
                }

                var edgeRows = new List<object>(edges.Count);
                foreach (var e in edges)
                {
                    edgeRows.Add(new
                    {
                        a = e.A,
                        b = e.B,
                        strikes = e.Strikes,
                        penalty = e.Penalty,
                        until = e.Until,
                    });
                }

                var report = new
                {
                    asOf = Core.Now,
                    bootedAt = _bootAt,
                    windowMinutes = (int)Window.TotalMinutes,
                    window = windowKinds,
                    total = _totals,
                    hotspots = spotRows,
                    edges = edgeRows,
                };

                File.WriteAllText(ReportPath, JsonSerializer.Serialize(
                    report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[stuck] report write failed: {ex.Message}");
            }
        }

        // Status-page section — appended by BotStatusPage right after the
        // news feed, so a glance at status.html shows whether the shard
        // is jamming anywhere and where.
        public static void AppendHtml(StringBuilder sb)
        {
            Aggregate(out var windowKinds, out var topSpots);
            var edges = NavEdgeHealth.Snapshot();

            sb.Append($"<h2>Stuck &amp; Rescues (last {(int)Window.TotalMinutes} min)</h2>");

            if (windowKinds.Count == 0 && edges.Count == 0)
            {
                sb.Append("<p class='dim'>none — all quiet</p>");
                return;
            }

            var kindPairs = new List<KeyValuePair<string, int>>(windowKinds);
            kindPairs.Sort((a, b) => b.Value.CompareTo(a.Value));
            sb.Append("<p>");
            for (int i = 0; i < kindPairs.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(" · ");
                }
                sb.Append($"{H(kindPairs[i].Key)} <b>{kindPairs[i].Value}</b>");
            }
            sb.Append("</p>");

            if (topSpots.Count > 0)
            {
                sb.Append("<table><tr><th>#</th><th>Where</th><th>Kinds</th>" +
                          "<th>Last</th></tr>");
                foreach (var s in topSpots)
                {
                    sb.Append($"<tr><td>{s.Count}</td>" +
                              $"<td>{H(SpotLabel(s))}</td>" +
                              $"<td class='dim'>{H(KindSummary(s.Kinds))}</td>" +
                              $"<td class='dim'>{H(s.Last.Bot)} {s.Last.At:HH:mm}" +
                              (string.IsNullOrEmpty(s.Last.Detail)
                                  ? ""
                                  : $" — {H(s.Last.Detail)}") +
                              "</td></tr>");
                }
                sb.Append("</table>");
            }

            if (edges.Count > 0)
            {
                sb.Append("<p><b>Penalized edges</b> (bots detouring around):</p><table>");
                sb.Append("<tr><th>Edge</th><th>Strikes</th><th>Cost</th><th>Until</th></tr>");
                foreach (var e in edges)
                {
                    sb.Append($"<tr><td>{H(e.A)} &harr; {H(e.B)}</td>" +
                              $"<td>{e.Strikes}</td><td>&times;{e.Penalty:0.#}</td>" +
                              $"<td class='dim'>{e.Until:HH:mm}</td></tr>");
                }
                sb.Append("</table>");
            }
        }

        private static string H(string s) =>
            string.IsNullOrEmpty(s)
                ? ""
                : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    // =====================================================================
    // NavEdgeHealth — decaying per-edge failure memory consulted by
    // WaypointGraph.FindPath. Strikes come from Traveler leg give-ups
    // (the bot ground through its whole nudge/repath ladder and still
    // couldn't cross the edge). Penalties are cost MULTIPLIERS, never
    // removals — connectivity is untouched, only path shape changes.
    // =====================================================================
    public static class NavEdgeHealth
    {
        private class EdgeState
        {
            public string A, B;
            public int Strikes;
            public DateTime Until;
        }

        private static readonly Dictionary<string, EdgeState> _edges =
            new(StringComparer.OrdinalIgnoreCase);

        // Fast pre-filter so the FindPath hot loop pays one HashSet probe
        // (no string concat) while no edges are penalized — the common
        // case — and only builds keys for nodes that appear in some
        // penalized edge.
        private static readonly HashSet<string> _touchedNodes =
            new(StringComparer.OrdinalIgnoreCase);

        private const int MaxStrikes = 6;
        private static readonly TimeSpan StrikeTtl = TimeSpan.FromMinutes(45);

        public static void Install()
        {
            WaypointGraph.EdgePenalty = PenaltyFor;
        }

        private static string KeyOf(string a, string b) =>
            string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

        // A Traveler ground through its whole stuck ladder on this edge
        // and gave up. Penalize it (both directions) for a while.
        public static void ReportFailure(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) ||
                string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var key = KeyOf(a, b);
            if (!_edges.TryGetValue(key, out var e))
            {
                _edges[key] = e = new EdgeState { A = a, B = b };
            }
            else if (Core.Now >= e.Until)
            {
                e.Strikes = 0; // old strikes fully decayed — start fresh
            }

            e.Strikes = Math.Min(MaxStrikes, e.Strikes + 1);
            e.Until = Core.Now + StrikeTtl;
            _touchedNodes.Add(a);
            _touchedNodes.Add(b);

            Console.WriteLine(
                $"[nav] edge '{a}' <-> '{b}' takes a strike ({e.Strikes}) — " +
                $"cost x{PenaltyFactor(e.Strikes):0.#} until {e.Until:HH:mm}");
        }

        private static double PenaltyFactor(int strikes) => 1.0 + 2.0 * strikes;

        // Called from inside Dijkstra's relax loop — must be cheap when
        // nothing is penalized.
        public static double PenaltyFor(string a, string b)
        {
            if (_edges.Count == 0 ||
                !_touchedNodes.Contains(a) || !_touchedNodes.Contains(b))
            {
                return 1.0;
            }

            if (!_edges.TryGetValue(KeyOf(a, b), out var e) || Core.Now >= e.Until)
            {
                return 1.0;
            }
            return PenaltyFactor(e.Strikes);
        }

        public readonly struct EdgeReport
        {
            public string A { get; init; }
            public string B { get; init; }
            public int Strikes { get; init; }
            public double Penalty { get; init; }
            public DateTime Until { get; init; }
        }

        // Live (unexpired) edges, worst first. Prunes expired entries as
        // a side effect — the dump timer is the natural janitor.
        public static List<EdgeReport> Snapshot()
        {
            var result = new List<EdgeReport>();
            if (_edges.Count == 0)
            {
                return result;
            }

            List<string> dead = null;
            foreach (var (key, e) in _edges)
            {
                if (Core.Now >= e.Until)
                {
                    (dead ??= new List<string>()).Add(key);
                    continue;
                }
                result.Add(new EdgeReport
                {
                    A = e.A,
                    B = e.B,
                    Strikes = e.Strikes,
                    Penalty = PenaltyFactor(e.Strikes),
                    Until = e.Until,
                });
            }

            if (dead != null)
            {
                foreach (var key in dead)
                {
                    _edges.Remove(key);
                }
                _touchedNodes.Clear();
                foreach (var e in _edges.Values)
                {
                    _touchedNodes.Add(e.A);
                    _touchedNodes.Add(e.B);
                }
            }

            result.Sort((x, y) => y.Strikes.CompareTo(x.Strikes));
            return result;
        }
    }

    // =====================================================================
    // BotStuckEscape — physical last resorts shared by all behaviors.
    // =====================================================================
    public static class BotStuckEscape
    {
        // Single-step sidestep — try each compass direction in random
        // order, take the first that succeeds. Returns false when all 8
        // are blocked (the physically-wedged tell).
        public static bool SidestepAny(PlayerBot bot)
        {
            Direction[] dirs =
            {
                Direction.North, Direction.East, Direction.South, Direction.West,
                Direction.Up,    Direction.Down, Direction.Left,  Direction.Right,
            };
            for (int i = dirs.Length - 1; i > 0; i--)
            {
                int j = Utility.Random(i + 1);
                (dirs[i], dirs[j]) = (dirs[j], dirs[i]);
            }
            foreach (var d in dirs)
            {
                if (bot.Direction != d)
                {
                    bot.Direction = d;
                }
                if (bot.Move(d))
                {
                    return true;
                }
            }
            return false;
        }

        // Teleport a fully wedged bot to the nearest tile the engine will
        // accept a mobile on. Spiral outward so the extraction stays local
        // (a couple of tiles for a crowd box-in; a few more for a bot
        // embedded in a cliff by a bad teleport landing). Returns true if
        // the bot was moved.
        public static bool TryExtract(PlayerBot bot, string context)
        {
            var map = bot.Map;
            if (map == null || map == Map.Internal)
            {
                return false;
            }

            for (int r = 1; r <= 10; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue; // ring only
                        }
                        int x = bot.X + dx;
                        int y = bot.Y + dy;
                        int z = map.GetAverageZ(x, y);
                        if (map.CanSpawnMobile(x, y, z))
                        {
                            Console.WriteLine(
                                $"[stuck] {bot.Name}: WEDGED at ({bot.X},{bot.Y},{bot.Z}) " +
                                $"({context}) — extracted to ({x},{y},{z})");
                            // Record BEFORE the move so the hotspot is the
                            // wedge, not where the extraction dropped it.
                            StuckTelemetry.Record(bot, "wedge_extract",
                                $"{context} → ({x},{y},{z})");
                            bot.MoveToWorld(new Point3D(x, y, z), map);
                            return true;
                        }
                    }
                }
            }

            Console.WriteLine(
                $"[stuck] {bot.Name}: WEDGED at ({bot.X},{bot.Y},{bot.Z}) ({context}) " +
                $"and no valid tile within 10 — leaving for the lifecycle to recycle");
            StuckTelemetry.Record(bot, "wedge_hopeless", context);
            return false;
        }
    }
}
