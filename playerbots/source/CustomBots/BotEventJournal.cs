// =========================================================================
// BotEventJournal.cs — the shard's memory of itself (IDEAS 6.3 + 1.4).
//
// One append-only journal of notable bot events: kills, deaths, PK
// murders, hunting parties setting out, logins/logouts. One writer, many
// consumers:
//
//   - In-memory ring (last 300 events) feeds GOSSIP: bots at banks retell
//     recent events with real names and places ("Aldreth got killed at
//     Despise earlier!!"). A line is only ever spoken if the event
//     actually happened — the server talks about itself truthfully.
//   - Disk JSONL (Data/Live/event-journal.jsonl) for dashboards / the
//     future shard status page.
//
// Gossip templates live in Data/PlayerBotChat/Gossip/<type>.txt — one
// file per event type (pk.txt, death.txt, kill.txt, party.txt), lines
// with {actor} {other} {place} {when} tokens. They're in a SUBDIRECTORY
// so ChatLibrary's flat *.txt scan never serves a raw template as
// ambient chatter.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;

namespace Server.CustomBots
{
    public sealed class BotEvent
    {
        public DateTime At    { get; init; }
        public string   Type  { get; init; }  // "kill" | "death" | "pk" | "party" | "login" | "logout"
        public string   Actor { get; init; }  // the bot the event happened to / who did it
        public string   Other { get; init; }  // counterparty (foe, killer, dungeon...) or ""
        public string   Place { get; init; }  // friendly place name
    }

    public static class BotEventJournal
    {
        // How long an event stays gossip-worthy.
        private static readonly TimeSpan GossipMaxAge = TimeSpan.FromHours(3);

        private const int RingCapacity = 300;
        private static readonly List<BotEvent> _ring = new();

        // type -> gossip template lines ({actor}/{other}/{place}/{when}).
        private static readonly Dictionary<string, List<string>> _templates =
            new(StringComparer.OrdinalIgnoreCase);

        // Drama weighting for which event gets retold: murders are the
        // talk of the town; an ordinary monster kill barely registers.
        private static readonly Dictionary<string, double> _gossipWeight =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["pk"]      = 4.0,
                ["faction"] = 3.5, // shield war street kills
                ["death"]   = 3.0,
                ["red"]     = 2.0, // a red was SPOTTED — warn people
                ["duel"]    = 2.0,
                ["kill"]    = 1.5,
                ["party"]   = 1.5,
            };

        private static string GossipDir =>
            Path.Combine(Core.BaseDirectory, "Data", "PlayerBotChat", "Gossip");

        private static string JournalPath =>
            Path.Combine(Core.BaseDirectory, "Data", "Live", "event-journal.jsonl");

        public static void Configure()
        {
            LoadTemplates();
        }

        public static void LoadTemplates()
        {
            _templates.Clear();
            if (!Directory.Exists(GossipDir))
            {
                Console.WriteLine($"[journal] gossip template dir not found at {GossipDir}; gossip disabled.");
                return;
            }

            int lines = 0;
            foreach (var path in Directory.EnumerateFiles(GossipDir, "*.txt"))
            {
                var type = Path.GetFileNameWithoutExtension(path);
                var list = new List<string>();
                try
                {
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith('#'))
                        {
                            continue;
                        }
                        list.Add(line);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[journal] failed to read {path}: {ex.Message}");
                }
                if (list.Count > 0)
                {
                    _templates[type] = list;
                    lines += list.Count;
                }
            }
            Console.WriteLine($"[journal] loaded {lines} gossip template(s) across {_templates.Count} event type(s).");
        }

        // -------------------------------------------------------------------
        // Record — the one write path. Everything notable funnels here.
        // -------------------------------------------------------------------
        public static void Record(string type, string actor, string other, Point3D loc, Map map)
        {
            var ev = new BotEvent
            {
                At    = Core.Now,
                Type  = type,
                Actor = actor ?? "",
                Other = other ?? "",
                Place = PlaceName(loc, map),
            };

            _ring.Add(ev);
            if (_ring.Count > RingCapacity)
            {
                _ring.RemoveAt(0);
            }

            // Violence heats the danger map (IDEAS 3.3) — the population
            // starts avoiding places where people keep dying.
            switch (type)
            {
                case "pk":
                    BotDangerMap.AddHeat(ev.Place, 3.0);
                    break;
                case "faction":
                    BotDangerMap.AddHeat(ev.Place, 1.0);
                    break;
                case "death":
                    BotDangerMap.AddHeat(ev.Place, 0.8);
                    break;
            }

            AppendToDisk(ev);
        }

        // Most-recent events, newest first — the shard status page's feed.
        public static IReadOnlyList<BotEvent> Recent(int count)
        {
            var list = new List<BotEvent>(Math.Min(count, _ring.Count));
            for (int i = _ring.Count - 1; i >= 0 && list.Count < count; i--)
            {
                list.Add(_ring[i]);
            }
            return list;
        }

        public static void Record(string type, PlayerBot actor, string other = "")
        {
            if (actor == null)
            {
                return;
            }
            Record(type, actor.Name, other, actor.Location, actor.Map);
        }

        private static void AppendToDisk(BotEvent ev)
        {
            try
            {
                var dir = Path.GetDirectoryName(JournalPath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var line = JsonSerializer.Serialize(new
                {
                    at    = ev.At.ToString("yyyy-MM-dd HH:mm:ss"),
                    type  = ev.Type,
                    actor = ev.Actor,
                    other = ev.Other,
                    place = ev.Place,
                });
                File.AppendAllText(JournalPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // Disk trouble must never break the game loop.
                Console.WriteLine($"[journal] append failed: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------
        // PlaceName — turn a coordinate into something a player would say.
        // Inside a dungeon → the dungeon's name. Near a known destination →
        // its name (or its city for wider hits). Else the region, else
        // "the wilderness".
        // -------------------------------------------------------------------
        public static string PlaceName(Point3D loc, Map map)
        {
            // Inside a dungeon the REGION carries the canonical name
            // ("Despise") — interior destination tags can carry authoring
            // suffixes ("Despise lvl1 ratmen") that read wrong in gossip.
            try
            {
                var dungeonRegion = Region.Find(loc, map);
                if (dungeonRegion?.IsPartOf<Server.Regions.DungeonRegion>() == true &&
                    !string.IsNullOrEmpty(dungeonRegion.Name))
                {
                    return dungeonRegion.Name;
                }
            }
            catch
            {
                // fall through to destination-based naming
            }

            BotDestination best = null;
            int bestDist = int.MaxValue;
            foreach (var d in DestinationCatalog.All)
            {
                int dist = Math.Max(Math.Abs(d.Location.X - loc.X),
                                    Math.Abs(d.Location.Y - loc.Y));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }

            if (best != null)
            {
                // Interior dungeon points carry the dungeon tag — that's
                // the name players actually use ("at despise").
                if (!string.IsNullOrEmpty(best.Dungeon))
                {
                    return best.Dungeon;
                }
                if (bestDist <= 20)
                {
                    return best.Name;
                }
                if (bestDist <= 80 && !string.IsNullOrEmpty(best.City))
                {
                    return best.City;
                }
                if (bestDist <= 80)
                {
                    return $"near {best.Name}";
                }
            }

            var region = Region.Find(loc, map);
            if (region?.Name is { Length: > 0 } regionName)
            {
                return regionName;
            }
            return "the wilderness";
        }

        // -------------------------------------------------------------------
        // Gossip — compose a line about a recent event. Null when there's
        // nothing fresh to talk about (or no templates). The speaker never
        // gossips about themself, and never about an event they couldn't
        // plausibly have heard of yet (< 60s old).
        // -------------------------------------------------------------------
        public static string ComposeGossip(string speakerName)
        {
            if (_templates.Count == 0 || _ring.Count == 0)
            {
                return null;
            }

            var now = Core.Now;
            List<BotEvent> pool = null;
            double totalW = 0;
            for (int i = _ring.Count - 1; i >= 0; i--)
            {
                var ev = _ring[i];
                var age = now - ev.At;
                if (age > GossipMaxAge)
                {
                    break; // ring is chronological; everything older follows
                }
                if (age < TimeSpan.FromSeconds(60))
                {
                    continue; // news needs a moment to travel
                }
                if (!_gossipWeight.TryGetValue(ev.Type, out _))
                {
                    continue; // logins/logouts etc aren't gossip
                }
                if (string.Equals(ev.Actor, speakerName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!_templates.ContainsKey(ev.Type))
                {
                    continue;
                }
                pool ??= new List<BotEvent>();
                pool.Add(ev);
                totalW += _gossipWeight[ev.Type];
            }

            if (pool == null)
            {
                return null;
            }

            // Weighted pick by drama.
            double r = Utility.RandomDouble() * totalW;
            BotEvent picked = pool[^1];
            foreach (var ev in pool)
            {
                r -= _gossipWeight[ev.Type];
                if (r <= 0)
                {
                    picked = ev;
                    break;
                }
            }

            var templates = _templates[picked.Type];
            var line = templates[Utility.Random(templates.Count)];

            return line
                .Replace("{actor}", picked.Actor, StringComparison.Ordinal)
                .Replace("{other}", picked.Other, StringComparison.Ordinal)
                .Replace("{place}", picked.Place, StringComparison.Ordinal)
                .Replace("{when}", WhenPhrase(now - picked.At), StringComparison.Ordinal);
        }

        private static string WhenPhrase(TimeSpan age) => age switch
        {
            { TotalMinutes: < 10 } => "just now",
            { TotalMinutes: < 45 } => "a few min ago",
            { TotalHours: < 2 }    => "earlier",
            _                      => "a while back",
        };
    }
}
