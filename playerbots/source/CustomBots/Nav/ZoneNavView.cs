// =========================================================================
// ZoneNavView.cs — see the walk mesh in the game world.
//
//     [NavShow [radius]    zone outlines and links around you (default 24)
//     [NavHide             clear it
//     [NavPath <bot name>  the link chain that bot is walking right now
//
// Same construction as [showways: every marker is a WaypointMarkerItem,
// a ProjectedItem that pulses art to staff clients and blocks nothing.
// Border tiles only, hue by tag, links as runes, door links in red.
// No drawing commands here on purpose. Zones are drawn in the map editor.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class ZoneNavView
    {
        private const int BorderArt = 0xF9;    // the engine's own overlay line art
        private const int LinkArt   = 0x1F14;  // recall rune
        private const int PathArt   = 0x1F14;

        private const int HueRoad     = 0x3F;  // green
        private const int HuePlaza    = 0x481; // white
        private const int HueInterior = 0x35;  // orange
        private const int HueDock     = 0x59;  // blue
        private const int HueNoBots   = 0x21;  // red
        private const int HueDungeon  = 0x2C;  // dark red
        private const int HueArea     = 0x8A;  // purple
        private const int HuePortal   = 0x35;  // orange
        private const int HueLink     = 0x481; // white
        private const int HueDoorLink = 0x21;  // red
        private const int HuePathNext = 0x21;  // red: the link being crossed
        private const int HuePath     = 0x3F;  // green: the rest of the chain

        private const int DefaultRadius = 24;
        private const int MaxRadius = 80;
        private const int BorderSpacing = 2;
        private const int MaxItems = 1200;
        private const int RedrawAfterMoved = 8;
        private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan Pulse = TimeSpan.FromSeconds(2);

        private sealed class View
        {
            public readonly List<Item> Items = new();
            public Point3D DrawnFrom;
            public int Radius;
            public DateTime ExpiresAt;
            public string PathBot;   // set by [NavPath; redraws follow the bot
        }

        private static readonly Dictionary<Mobile, View> _views = new();
        private static Timer _timer;

        public static void Configure()
        {
            CommandSystem.Register("NavShow", AccessLevel.GameMaster, OnShow);
            CommandSystem.Register("NavHide", AccessLevel.GameMaster, OnHide);
            CommandSystem.Register("NavPath", AccessLevel.GameMaster, OnPath);
        }

        private static void OnShow(CommandEventArgs e)
        {
            int radius = DefaultRadius;
            if (e.Arguments.Length > 0 && int.TryParse(e.Arguments[0], out var r))
            {
                radius = Math.Clamp(r, 4, MaxRadius);
            }
            Draw(e.Mobile, radius, null, announce: true);
        }

        private static void OnHide(CommandEventArgs e)
        {
            e.Mobile.SendMessage(Clear(e.Mobile) ? "Nav view cleared." : "No nav view to clear.");
        }

        private static void OnPath(CommandEventArgs e)
        {
            string name = e.ArgString?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                e.Mobile.SendMessage("Usage: [NavPath <bot name>");
                return;
            }
            var bot = FindBot(name);
            if (bot == null)
            {
                e.Mobile.SendMessage($"No bot named '{name}'.");
                return;
            }
            Draw(e.Mobile, _views.TryGetValue(e.Mobile, out var v) ? v.Radius : DefaultRadius, bot.Name, announce: true);
        }

        private static PlayerBot FindBot(string name)
        {
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot b && !b.Deleted && b.Name.InsensitiveEquals(name))
                {
                    return b;
                }
            }
            return null;
        }

        public static int HueFor(PaintedZone z)
        {
            if (z.IsPortal) return HuePortal;
            if (z.IsArea) return HueArea;
            return (z.Tag ?? "").ToLowerInvariant() switch
            {
                "road"     => HueRoad,
                "plaza"    => HuePlaza,
                "interior" => HueInterior,
                "dock"     => HueDock,
                "no-bots"  => HueNoBots,
                "dungeon"  => HueDungeon,
                _          => HuePlaza,
            };
        }

        private static void Draw(Mobile from, int radius, string pathBot, bool announce)
        {
            if (from?.Map == null || from.Map == Map.Internal)
            {
                return;
            }
            Clear(from);
            var view = new View
            {
                DrawnFrom = from.Location,
                Radius = radius,
                ExpiresAt = Core.Now + Expiry,
                PathBot = pathBot,
            };
            _views[from] = view;
            var map = from.Map;
            ZoneRegistry.EnsureMesh();

            int zones = 0, links = 0;
            foreach (var z in ZoneRegistry.All)
            {
                if (z.MaxX < from.X - radius || z.MinX > from.X + radius ||
                    z.MaxY < from.Y - radius || z.MinY > from.Y + radius)
                {
                    continue;
                }
                zones++;
                int hue = HueFor(z);
                int refZ = z.ZKnown ? (z.ZMin + z.ZMax) / 2 : from.Z;
                foreach (var (x, y) in BorderTiles(z))
                {
                    if (Math.Abs(x - from.X) > radius || Math.Abs(y - from.Y) > radius)
                    {
                        continue;
                    }
                    Place(view, map, new Point3D(x, y, SurfaceZ(map, x, y, refZ)), BorderArt, hue, z.Name);
                }
            }

            foreach (var l in ZoneRegistry.Links)
            {
                if (Math.Abs(l.MidX - from.X) > radius || Math.Abs(l.MidY - from.Y) > radius)
                {
                    continue;
                }
                links++;
                int strikes = ZoneNav.StrikesOn(l);
                Place(view, map, l.Mid, LinkArt, l.IsDoor || strikes > 0 ? HueDoorLink : HueLink,
                      $"{l.A.Name} <-> {l.B.Name}{(l.IsDoor ? " (door)" : "")}{(strikes > 0 ? $" strikes {strikes}" : "")}");
            }

            string pathNote = "";
            if (pathBot != null)
            {
                var bot = FindBot(pathBot);
                if (bot?.Behavior is TravelerBehavior tb && tb.ActiveZoneFollower is { } zf && zf.OnMesh && zf.Route != null)
                {
                    var route = zf.Route;
                    for (int i = zf.TargetIndex; i < route.Targets.Count; i++)
                    {
                        var t = route.Targets[i];
                        Place(view, map, t, PathArt, i == zf.TargetIndex ? HuePathNext : HuePath,
                              i < route.Links.Count ? route.Links[i].ToString() : "goal");
                    }
                    pathNote = $" {bot.Name}: {route.Targets.Count - zf.TargetIndex} target(s) left, " +
                               $"in '{route.Zones[Math.Min(zf.TargetIndex, route.Zones.Count - 1)].Name}'.";
                }
                else if (bot?.Behavior is TravelerBehavior tb2 && tb2.ActiveZoneFollower is { } zf2)
                {
                    pathNote = $" {bot.Name} fell back to the engine walker ({zf2.FallbackReason}).";
                }
                else
                {
                    pathNote = $" {pathBot} is not on a zone route.";
                }
            }

            if (announce)
            {
                from.SendMessage(0x3B2, $"Nav view: {zones} zone(s), {links} link(s) within {radius} tiles, " +
                                        $"{view.Items.Count} marker(s). Zone walking is {ZoneNav.ModeName}.{pathNote}");
            }
            StartTimer();
        }

        private static int SurfaceZ(Map map, int x, int y, int refZ) =>
            Walkable.TryFindSeedZ(map, x, y, refZ, out int z) ? z : map.GetAverageZ(x, y);

        // Every BorderSpacing-th tile along the polygon outline.
        private static IEnumerable<(int x, int y)> BorderTiles(PaintedZone z)
        {
            int n = z.Points.Count;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                var (x0, y0) = z.Points[i];
                var (x1, y1) = z.Points[(i + 1) % n];
                int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
                int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
                int err = dx + dy;
                int x = x0, y = y0;
                while (true)
                {
                    if (count++ % BorderSpacing == 0)
                    {
                        yield return (x, y);
                    }
                    if (x == x1 && y == y1)
                    {
                        break;
                    }
                    int e2 = 2 * err;
                    if (e2 >= dy) { err += dy; x += sx; }
                    if (e2 <= dx) { err += dx; y += sy; }
                }
            }
        }

        private static void Place(View view, Map map, Point3D loc, int art, int hue, string name)
        {
            if (view.Items.Count >= MaxItems)
            {
                return;
            }
            var item = new WaypointMarkerItem(art) { Hue = hue, Name = name };
            item.MoveToWorld(loc, map);
            view.Items.Add(item);
        }

        public static bool Clear(Mobile from)
        {
            if (from == null || !_views.Remove(from, out var view))
            {
                return false;
            }
            foreach (var item in view.Items)
            {
                item?.Delete();
            }
            if (_views.Count == 0)
            {
                _timer?.Stop();
                _timer = null;
            }
            return true;
        }

        // Redraw every view — for [ReloadZones, so what you see is what
        // the bots just picked up.
        public static void RefreshAll()
        {
            if (_views.Count == 0)
            {
                return;
            }
            foreach (var m in new List<Mobile>(_views.Keys))
            {
                if (!_views.TryGetValue(m, out var v))
                {
                    continue;
                }
                if (m.Deleted || m.NetState == null)
                {
                    Clear(m);
                    continue;
                }
                Draw(m, v.Radius, v.PathBot, announce: false);
            }
        }

        private static void StartTimer()
        {
            _timer ??= Timer.DelayCall(Pulse, Pulse, OnTick);
        }

        private static void OnTick()
        {
            if (_views.Count == 0)
            {
                _timer?.Stop();
                _timer = null;
                return;
            }
            foreach (var m in new List<Mobile>(_views.Keys))
            {
                if (!_views.TryGetValue(m, out var view))
                {
                    continue;
                }
                if (m.Deleted || m.NetState == null || m.Map == null || m.Map == Map.Internal)
                {
                    Clear(m);
                    continue;
                }
                if (Core.Now >= view.ExpiresAt)
                {
                    Clear(m);
                    m.SendMessage(0x3B2, "Nav view expired. [NavShow to draw it again.");
                    continue;
                }
                int moved = Math.Max(Math.Abs(m.X - view.DrawnFrom.X), Math.Abs(m.Y - view.DrawnFrom.Y));
                if (moved >= RedrawAfterMoved || view.PathBot != null)
                {
                    // A path view follows the bot: redraw every pulse.
                    Draw(m, view.Radius, view.PathBot, announce: false);
                }
            }
        }
    }
}
