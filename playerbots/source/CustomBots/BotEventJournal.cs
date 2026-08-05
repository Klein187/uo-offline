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
        public int      X     { get; init; }  // where it happened (map editor's
        public int      Y     { get; init; }  // event ticker jumps here)

        // How many times gossip has retold this event. Each telling halves
        // its future weight, so one murder doesn't dominate every bank
        // conversation for three hours.
        public int TellCount;
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
                ["pk"]       = 4.0,
                ["faction"]  = 3.5, // shield war street kills
                ["warclash"] = 3.0, // war bands met — a street battle
                ["death"]    = 3.0,
                ["red"]      = 2.0, // a red was SPOTTED — warn people
                ["duel"]     = 2.0,
                ["kill"]     = 1.5,
                ["party"]    = 1.5,
                ["warband"]  = 1.2, // a patrol marched out
                ["convoy"]   = 0.6, // guild crew on the road — mild news
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
                X     = loc.X,
                Y     = loc.Y,
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
                    x     = ev.X,
                    y     = ev.Y,
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

        // News doesn't teleport: a rumor spreads outward from where it
        // happened at roughly this many seconds per tile (~30 tiles a
        // minute — word of mouth, travelers, recall runs). A murder in
        // Trinsic reaches Minoc's bank crowd in about an hour and a
        // quarter, not two minutes; the same murder is talk of Trinsic
        // itself almost immediately.
        private const double NewsSecondsPerTile = 2.0;

        // -------------------------------------------------------------------
        // Gossip — compose a line about a recent event. Null when there's
        // nothing fresh to talk about (or no templates). An event about
        // the SPEAKER uses the "<type>_self" first-person templates ("i
        // got murdered by X, watch yourself") — a bot never narrates its
        // own story in the third person. Nobody gossips about an event
        // they couldn't plausibly have heard of yet — a minimum 60s
        // everywhere, plus travel time proportional to how far the
        // speaker is from where it happened (own events are exempt: the
        // speaker was there). Retold events fade (TellCount halves the
        // weight per telling), and a template that needs a token the
        // event doesn't have (a self-kill has no {other}) is never picked.
        // -------------------------------------------------------------------
        public static string ComposeGossip(string speakerName, Point3D speakerLoc)
        {
            if (_templates.Count == 0 || _ring.Count == 0)
            {
                return null;
            }

            var now = Core.Now;
            List<(BotEvent ev, string key, double w)> pool = null;
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
                if (!_gossipWeight.TryGetValue(ev.Type, out var baseW))
                {
                    continue; // logins/logouts etc aren't gossip
                }

                // Own events go first-person; without _self templates for
                // the type, the speaker just doesn't bring it up.
                bool own = string.Equals(ev.Actor, speakerName,
                    StringComparison.OrdinalIgnoreCase);
                var key = own ? ev.Type + "_self" : ev.Type;
                if (!_templates.ContainsKey(key))
                {
                    continue;
                }

                // Distance gate: someone ELSE's news has to physically reach
                // the speaker. (Point3D.Zero = caller has no location —
                // legacy/test paths — which skips the gate.)
                if (!own && speakerLoc != Point3D.Zero)
                {
                    int dist = Math.Max(Math.Abs(ev.X - speakerLoc.X),
                                        Math.Abs(ev.Y - speakerLoc.Y));
                    if (age < TimeSpan.FromSeconds(60 + dist * NewsSecondsPerTile))
                    {
                        continue; // word hasn't traveled this far yet
                    }
                }

                double w = baseW / (1.0 + ev.TellCount);
                if (own)
                {
                    // People lead with their own stories — a bot that just
                    // got murdered or slew a lich talks about THAT before
                    // retelling someone else's news. (Still decays with
                    // TellCount, so nobody loops their war story forever.)
                    w *= 2.5;
                }
                pool ??= new List<(BotEvent, string, double)>();
                pool.Add((ev, key, w));
                totalW += w;
            }

            if (pool == null)
            {
                return null;
            }

            // Weighted pick by drama (freshness-decayed).
            double r = Utility.RandomDouble() * totalW;
            var (picked, pickedKey, _) = pool[^1];
            foreach (var (ev, key, w) in pool)
            {
                r -= w;
                if (r <= 0)
                {
                    picked = ev;
                    pickedKey = key;
                    break;
                }
            }

            // Only templates whose tokens this event can actually fill —
            // an unattributed death (empty {other}) must not produce
            // "got killed by  at the crossroads".
            var templates = _templates[pickedKey];
            List<string> usable = null;
            foreach (var t in templates)
            {
                if (picked.Other.Length == 0 &&
                    t.Contains("{other}", StringComparison.Ordinal))
                {
                    continue;
                }
                (usable ??= new List<string>()).Add(t);
            }
            if (usable == null)
            {
                return null;
            }

            picked.TellCount++;
            var line = usable[Utility.Random(usable.Count)];

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
