// =========================================================================
// ZoneNavStats.cs — is the drawn walk mesh actually walking anybody?
//
// Counts every leg a walker is made for: walked on the mesh, or handed to
// the engine walker and why (zones off for this bot, the start or the end
// off the drawn ground). Zone legs that give up mid-way count by reason
// too. The off-mesh leg ends are tallied by 16-tile square, so the worst
// gaps in the drawing show up by place.
//
// Test hook: Data/Live/zonenav_request.txt = "<token> <command>", where the
// command is on | off | half | verbose | quiet | reset | stats. Every
// command answers with the counts in Data/Live/zonenav_ack.json.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Server.CustomBots
{
    public static class ZoneNavStats
    {
        private static string Req => Path.Combine(Core.BaseDirectory, "Data", "Live", "zonenav_request.txt");
        private static string Ack => Path.Combine(Core.BaseDirectory, "Data", "Live", "zonenav_ack.json");

        private static DateTime _since = DateTime.UtcNow;
        private static int _zoneLegs, _notWanted, _startOff, _goalOff, _bothOff, _zoneArrived, _fallbackArrived;
        private static readonly Dictionary<string, int> _fallbacks = new();
        private static readonly Dictionary<(int, int), int> _offCells = new();

        public static void Initialize()
        {
            Timer.DelayCall(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), Poll);
        }

        public static void LegNotWanted() => _notWanted++;
        public static void LegOnMesh() => _zoneLegs++;

        public static void LegOffMesh(Point3D from, bool startOn, Point3D to, bool goalOn)
        {
            if (!startOn && !goalOn)
            {
                _bothOff++;
            }
            else if (!startOn)
            {
                _startOff++;
            }
            else
            {
                _goalOff++;
            }
            if (!startOn)
            {
                Cell(from);
            }
            if (!goalOn)
            {
                Cell(to);
            }
        }

        private static void Cell(Point3D p)
        {
            var k = (p.X / 16, p.Y / 16);
            _offCells[k] = _offCells.TryGetValue(k, out var n) ? n + 1 : 1;
        }

        public static void Fallback(string why)
        {
            _fallbacks[why] = _fallbacks.TryGetValue(why, out var n) ? n + 1 : 1;
        }

        public static void Arrived(bool onMesh)
        {
            if (onMesh)
            {
                _zoneArrived++;
            }
            else
            {
                _fallbackArrived++;
            }
        }

        private static void Reset()
        {
            _since = DateTime.UtcNow;
            _zoneLegs = _notWanted = _startOff = _goalOff = _bothOff = _zoneArrived = _fallbackArrived = 0;
            _fallbacks.Clear();
            _offCells.Clear();
        }

        private static void Poll()
        {
            if (!File.Exists(Req))
            {
                return;
            }
            string text;
            try
            {
                text = File.ReadAllText(Req).Trim();
                File.Delete(Req);
            }
            catch
            {
                return;
            }
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string token = parts.Length > 0 ? parts[0] : "0";
            string cmd = parts.Length > 1 ? parts[1].ToLowerInvariant() : "stats";
            switch (cmd)
            {
                case "on":      ZoneNav.Mode = ZoneNavMode.On; break;
                case "off":     ZoneNav.Mode = ZoneNavMode.Off; break;
                case "half":    ZoneNav.Mode = ZoneNavMode.Half; break;
                case "verbose": ZoneNav.Verbose = true; break;
                case "quiet":   ZoneNav.Verbose = false; break;
                case "reset":   Reset(); break;
            }
            Console.WriteLine($"[zonenav] {cmd}: mode {ZoneNav.ModeName}, verbose {ZoneNav.Verbose}");

            var cells = _offCells.OrderByDescending(kv => kv.Value).Take(20)
                .Select(kv => new { x = kv.Key.Item1 * 16 + 8, y = kv.Key.Item2 * 16 + 8, legs = kv.Value })
                .ToList();
            var doc = new
            {
                token,
                mode = ZoneNav.ModeName,
                verbose = ZoneNav.Verbose,
                minutes = Math.Round((DateTime.UtcNow - _since).TotalMinutes, 1),
                zoneLegs = _zoneLegs,
                notWanted = _notWanted,
                startOffMesh = _startOff,
                goalOffMesh = _goalOff,
                bothOffMesh = _bothOff,
                zoneArrived = _zoneArrived,
                fallbackArrived = _fallbackArrived,
                fallbacks = _fallbacks,
                zones = ZoneRegistry.All.Count,
                links = ZoneRegistry.Links.Count,
                offMeshCells = cells,
            };
            try
            {
                File.WriteAllText(Ack, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // the next request answers again
            }
        }
    }
}
