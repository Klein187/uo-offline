// =========================================================================
// BotBuildMap.cs — where a house can go, for the map editor.
//
// Writes Data/Live/build_map.pgm: one pixel per tile, 255 = a small house
// can stand with its center here, by the same rules the bots place by
// (HousePlacement.Check, off the waypoint trails, and close enough to a
// waypoint or a drawn zone for the owner to walk between them). A house may
// stand inside a drawn zone: zones are only where bots can walk.
// build_map_small.pgm is the same
// map at 1/4 size, for when the editor is zoomed out. EDIT house spots in the
// editor shows them in green.
//
// A pass over the whole mainland takes a while, so it runs a few milliseconds
// at a time and the world keeps moving. Most tiles are ruled out first by a
// cheap look for trees, rocks and water under the house; only what is left
// goes through the real check.
//
// Runs on boot when the map is missing or the number of houses in the world
// has changed since it was made, after houses are placed in the spots, when
// the editor reloads waypoints and zones in game, and on request:
// homes_request.txt "token buildmap".
// =========================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Server.Logging;
using Server.Mobiles;
using Server.Multis;

namespace Server.CustomBots
{
    public static class BotBuildMap
    {
        private static readonly ILogger logger = LogFactory.GetLogger(typeof(BotBuildMap));

        // ---- Knobs ----

        // Past this the map is dungeons and the Lost Lands: no houses.
        private const int LandWidth = 5120;

        // The house the map is drawn for: the small stone and plaster house.
        private const int SmallHouse = 0x64;

        // How long each slice runs, and how often.
        private const int SliceMillis = 12;
        private static readonly TimeSpan SliceEvery = TimeSpan.FromMilliseconds(50);

        // The small map's pixel covers this many tiles a side.
        private const int SmallScale = 4;

        // How far off a trail a house in a spot must stand, as BotVendorHouses.
        private const int MinTrail = 6;

        // A house's front must be in reach of the path finder from the road
        // (38 tiles, BitmapAStarAlgorithm.AreaSize). The bots' own test walks
        // it; that is too slow for every tile, so the map goes by distance,
        // with room for the front being a few tiles from the center.
        private const int WaypointReach = 32;
        private const int ZoneSampleStep = 8;

        // ---- State ----

        private static Timer _timer;
        private static byte[] _inReach;
        private static bool _again;
        private static byte[] _blocked;
        private static byte[] _good;
        private static (int x, int y)[] _cells;
        private static int _row;
        private static bool _checking;
        private static int _found;
        private static long _started;

        private static string Live(string name) => Path.Combine(Core.BaseDirectory, "Data", "Live", name);

        public static bool Running => _timer != null;

        // On boot: only if the map is missing or out of date.
        public static void StartIfStale()
        {
            if (!File.Exists(Live("build_map.pgm")) || !File.Exists(Live("build_map_small.pgm")) ||
                SavedHouseCount() != BaseHouse.AllHouses.Count)
            {
                Start();
            }
        }

        public static void Start()
        {
            // Asked again part way through: the houses changed, so go again
            // when this pass is done.
            if (Running)
            {
                _again = true;
                return;
            }
            var map = Map.Felucca;
            int height = map.Height;
            _blocked = new byte[LandWidth * height];
            _inReach = MarkReach(height);
            _good = new byte[map.Width * height];
            _cells = OccupiedCells();
            _row = 0;
            _checking = false;
            _found = 0;
            _started = Stopwatch.GetTimestamp();
            WriteStatus(0);
            logger.Information("[buildmap] making the buildable ground map");
            _timer = Timer.DelayCall(SliceEvery, SliceEvery, Slice);
        }

        // Every tile close enough to a waypoint or a drawn zone.
        private static byte[] MarkReach(int height)
        {
            var reach = new byte[LandWidth * height];

            void Around(int px, int py)
            {
                int x0 = Math.Max(0, px - WaypointReach);
                int x1 = Math.Min(LandWidth - 1, px + WaypointReach);
                int y0 = Math.Max(0, py - WaypointReach);
                int y1 = Math.Min(height - 1, py + WaypointReach);
                if (x0 > x1)
                {
                    return;
                }
                for (int y = y0; y <= y1; y++)
                {
                    Array.Fill(reach, (byte)1, y * LandWidth + x0, x1 - x0 + 1);
                }
            }

            var graph = WaypointRegistry.Graph;
            if (graph != null)
            {
                foreach (var node in graph.AllNodes)
                {
                    Around(node.Location.X, node.Location.Y);
                }
            }

            // A zone's corners and a tile every so often inside it; the reach
            // squares overlap far more than that spacing.
            foreach (var zone in ZoneRegistry.All)
            {
                if (!BotHousing.IsSurfaceNav(zone))
                {
                    continue;
                }
                foreach (var (px, py) in zone.Points)
                {
                    Around(px, py);
                }
                for (int x = zone.MinX; x <= zone.MaxX; x += ZoneSampleStep)
                {
                    for (int y = zone.MinY; y <= zone.MaxY; y += ZoneSampleStep)
                    {
                        if (zone.Contains(x, y))
                        {
                            Around(x, y);
                        }
                    }
                }
            }
            return reach;
        }

        // The tiles under the house itself, from its center.
        private static (int x, int y)[] OccupiedCells()
        {
            var mcl = MultiData.GetComponents(SmallHouse);
            var cells = new List<(int, int)>();
            for (int x = 0; x < mcl.Width; x++)
            {
                for (int y = 0; y < mcl.Height; y++)
                {
                    if (mcl.Tiles[x][y].Length > 0)
                    {
                        cells.Add((x + mcl.Min.X, y + mcl.Min.Y));
                    }
                }
            }
            return cells.ToArray();
        }

        private static void Slice()
        {
            var map = Map.Felucca;
            long start = Stopwatch.GetTimestamp();
            int lastPercent = Percent(map);

            // The real check needs a mobile on the map. It lives only for this
            // slice, so a world save never sees it.
            Mobile probe = _checking ? new Rat { Controlled = true, Blessed = true, Hidden = true } : null;
            probe?.MoveToWorld(new Point3D(1, 1, 0), map);
            try
            {
                while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < SliceMillis)
                {
                    if (_row >= map.Height)
                    {
                        if (_checking)
                        {
                            Finish(map);
                            return;
                        }
                        _checking = true;
                        _row = 0;
                        return;
                    }
                    if (_checking)
                    {
                        CheckRow(map, probe, _row);
                    }
                    else
                    {
                        MarkRow(map, _row);
                    }
                    _row++;
                }
            }
            finally
            {
                probe?.Delete();
            }

            int percent = Percent(map);
            if (percent / 5 != lastPercent / 5)
            {
                WriteStatus(percent);
            }
        }

        private static int Percent(Map map) => (_checking ? 50 : 0) + _row * 50 / map.Height;

        // Water and anything impassable on the ground: no house over it.
        private static void MarkRow(Map map, int y)
        {
            for (int x = 0; x < LandWidth; x++)
            {
                var land = map.Tiles.GetLandTile(x, y);
                bool blocked = (TileData.LandTable[land.ID & TileData.MaxLandValue].Flags & TileFlag.Impassable) != 0;
                if (!blocked)
                {
                    foreach (var st in map.Tiles.GetStaticTiles(x, y))
                    {
                        if (TileData.ItemTable[st.ID & TileData.MaxItemValue].Impassable)
                        {
                            blocked = true;
                            break;
                        }
                    }
                }
                if (blocked)
                {
                    _blocked[y * LandWidth + x] = 1;
                }
            }
        }

        private static bool ClearUnderHouse(int x, int y, int height)
        {
            foreach (var (dx, dy) in _cells)
            {
                int cx = x + dx, cy = y + dy;
                if (cx < 0 || cy < 0 || cx >= LandWidth || cy >= height || _blocked[cy * LandWidth + cx] != 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static void CheckRow(Map map, Mobile probe, int y)
        {
            int width = map.Width;
            var graph = WaypointRegistry.Graph;
            for (int x = 0; x < LandWidth; x++)
            {
                if (_inReach[y * LandWidth + x] == 0 || !ClearUnderHouse(x, y, map.Height))
                {
                    continue;
                }
                var c = new Point3D(x, y, map.GetAverageZ(x, y));
                if (HousePlacement.Check(probe, SmallHouse, c, out _) == HousePlacementResult.Valid &&
                    (graph == null || !BotHousing.TooCloseToTrail(graph, x, y, MinTrail)))
                {
                    _good[y * width + x] = 255;
                    _found++;
                }
            }
        }

        private static void Finish(Map map)
        {
            _timer.Stop();
            _timer = null;
            int width = map.Width, height = map.Height;

            int smallW = width / SmallScale, smallH = height / SmallScale;
            var small = new byte[smallW * smallH];
            for (int y = 0; y < smallH * SmallScale; y++)
            {
                for (int x = 0; x < smallW * SmallScale; x++)
                {
                    if (_good[y * width + x] != 0)
                    {
                        small[y / SmallScale * smallW + x / SmallScale] = 255;
                    }
                }
            }

            try
            {
                WritePgm("build_map.pgm", _good, width, height);
                WritePgm("build_map_small.pgm", small, smallW, smallH);
                WriteStatus(100);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[buildmap] could not write the map");
            }

            logger.Information("[buildmap] {Found} buildable tiles, took {Seconds:F0} s",
                _found, Stopwatch.GetElapsedTime(_started).TotalSeconds);

            _blocked = null;
            _inReach = null;
            _good = null;
            _cells = null;

            if (_again)
            {
                _again = false;
                Start();
            }
        }

        // Written beside the final name and moved in, so the editor never
        // reads half a file.
        private static void WritePgm(string name, byte[] pixels, int width, int height)
        {
            var tmp = Live(name + ".tmp");
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                var header = Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
                f.Write(header, 0, header.Length);
                f.Write(pixels, 0, pixels.Length);
            }
            File.Move(tmp, Live(name), true);
        }

        private static void WriteStatus(int percent)
        {
            try
            {
                File.WriteAllText(Live("build_map.json"), JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["percent"] = percent,
                    ["houses"] = BaseHouse.AllHouses.Count,
                    ["tiles"] = _found,
                }));
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[buildmap] could not write the status");
            }
        }

        // The number of houses the finished map was made with, -1 if none.
        private static int SavedHouseCount()
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Live("build_map.json")));
                return doc.RootElement.GetProperty("percent").GetInt32() == 100
                    ? doc.RootElement.GetProperty("houses").GetInt32()
                    : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
