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
using Server.Mobiles;

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
                ["red"]      = 0.8, // a red was SPOTTED. Sightings outnumber
                                    // murders three to one even deduplicated,
                                    // so at 2.0 they were most of the talk
                ["duel"]     = 2.0,
                ["theft"]    = 2.0, // a pocket picked at the bank
                ["kill"]     = 1.5,
                ["party"]    = 1.5,
                ["warband"]  = 1.2, // a patrol marched out
                ["convoy"]   = 0.6, // guild crew on the road — mild news
                ["house"]    = 1.2, // somebody finally placed a house
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
            var place = PlaceName(loc, map);

            // A red walks past a bank and six witnesses each file the same
            // sighting. Half the journal was "red" that way — 199 of 400
            // rows — and half the gossip on the shard was the same warning
            // about the same red with a different name in front of it. One
            // sighting per red per place per ten minutes is the news.
            if (type == "red")
            {
                for (int i = _ring.Count - 1; i >= 0; i--)
                {
                    var prior = _ring[i];
                    if (Core.Now - prior.At > TimeSpan.FromMinutes(10))
                    {
                        break;
                    }
                    if (prior.Type == "red" && prior.Place == place &&
                        string.Equals(prior.Other, other, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }

            var ev = new BotEvent
            {
                At    = Core.Now,
                Type  = type,
                Actor = actor ?? "",
                Other = other ?? "",
                Place = place,
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
        // Nobody died "at Vesper Provisioner 9". They died in Vesper. A
        // destination's NAME is an authoring handle — numbered, typed,
        // unique — and it read as one every time gossip repeated it. A
        // shop, forge or bank is its town; a landmark keeps its name with
        // the number filed off; the synthetic spots get the words a
        // person would use.
        private static string Humanize(BotDestination d)
        {
            if (d == null)
            {
                return "the wilderness";
            }

            switch (d.Type)
            {
                case DestinationType.Bank:
                case DestinationType.Tavern:
                case DestinationType.Inn:
                case DestinationType.Forge:
                case DestinationType.Healer:
                case DestinationType.Library:
                case DestinationType.Stables:
                case DestinationType.CityCenter:
                case DestinationType.VendorSmith:
                case DestinationType.VendorMage:
                case DestinationType.VendorTailor:
                case DestinationType.VendorCarpenter:
                case DestinationType.VendorBowyer:
                case DestinationType.VendorAlchemist:
                case DestinationType.VendorWeaponer:
                case DestinationType.VendorProvisioner:
                    if (!string.IsNullOrEmpty(d.City))
                    {
                        return d.City;
                    }
                    break;
                case DestinationType.TreasureSite:
                    return "a dig site";
                case DestinationType.Dock:
                    return !string.IsNullOrEmpty(d.City) ? $"the {d.City} docks" : "the docks";
                case DestinationType.MiningSpot:
                    return "the mines";
                case DestinationType.LumberSpot:
                    return "the woods";
                case DestinationType.GatherSpot:
                    return "the wilds";
                case DestinationType.Graveyard:
                    return !string.IsNullOrEmpty(d.City) ? $"the {d.City} graveyard" : "the graveyard";
                case DestinationType.Crossroads:
                case DestinationType.Bridge:
                    if (!string.IsNullOrEmpty(d.City))
                    {
                        return $"outside {d.City}";
                    }
                    break;
            }

            // "Honor Shrine" stays; "Moongate 134" and "WP 55" are handles.
            var name = d.Name ?? "";
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+\d+[a-z]?$", "");
            if (name.Length == 0 || name.StartsWith("WP", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(d.City) ? $"outside {d.City}" : "the road";
            }
            if (name.StartsWith("Moongate", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(d.City) ? $"the {d.City} moongate" : "a moongate";
            }
            return name;
        }

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
                    return Humanize(best);
                }
                if (bestDist <= 80 && !string.IsNullOrEmpty(best.City))
                {
                    return best.City;
                }
                if (bestDist <= 80)
                {
                    return $"near {Humanize(best)}";
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
        // News you can HEAR ABOUT. A thing that happened this close and this
        // recently is a thing you are standing in — the event lines cover
        // that live; gossip is what reaches you from somewhere else. This
        // is also what stopped a bot mid-fight with a red from turning to
        // the player to mention there were reds about.
        private const int HereRadius = 40;
        private static readonly TimeSpan HereAge = TimeSpan.FromMinutes(20);

        // How far and how old before the details go soft. Close and fresh,
        // you have the names. Further out it is "someone got killed at
        // Shame". Rumour losing its edges with distance is what makes it
        // rumour instead of a news feed.
        private const int VividRadius = 400;
        private const int VagueRadius = 1200;
        private static readonly TimeSpan VividAge = TimeSpan.FromMinutes(45);

        // A killer named this many times in the ring is a known name, and
        // the line changes: not "X killed Y" but "X again".
        private const int RepeatKillerAt = 3;

        // What each speaker has told lately. Weight decay spreads a story
        // across the SHARD; it does nothing to stop one bot telling the
        // same one three times running when there is little else to say.
        private static readonly Dictionary<string, List<(BotEvent ev, DateTime at)>> _told =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RetellAfter = TimeSpan.FromMinutes(15);

        private static bool ToldRecently(string speaker, BotEvent ev, DateTime now)
        {
            if (!_told.TryGetValue(speaker, out var list))
            {
                return false;
            }
            list.RemoveAll(t => now - t.at > RetellAfter);
            foreach (var t in list)
            {
                if (ReferenceEquals(t.ev, ev))
                {
                    return true;
                }
            }
            return false;
        }

        private static void NoteTold(string speaker, BotEvent ev, DateTime now)
        {
            if (!_told.TryGetValue(speaker, out var list))
            {
                _told[speaker] = list = new List<(BotEvent, DateTime)>();
            }
            list.Add((ev, now));
            if (list.Count > 6)
            {
                list.RemoveAt(0);
            }
            if (_told.Count > 4000)
            {
                _told.Clear(); // a bounded scratchpad, not a record
            }
        }

        public static string ComposeGossip(string speakerName, Point3D speakerLoc) =>
            ComposeGossip(speakerName, speakerLoc, null);

        public static string ComposeGossip(string speakerName, Point3D speakerLoc, PlayerBot speaker)
        {
            if (_templates.Count == 0 || _ring.Count == 0)
            {
                return null;
            }

            var now = Core.Now;
            List<(BotEvent ev, string key, double w, int dist)> pool = null;
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

                bool own = string.Equals(ev.Actor, speakerName,
                    StringComparison.OrdinalIgnoreCase);
                var key = own ? ev.Type + "_self" : ev.Type;
                if (!_templates.ContainsKey(key))
                {
                    continue;
                }
                if (ToldRecently(speakerName, ev, now))
                {
                    continue;
                }

                int dist = 0;
                if (!own && speakerLoc != Point3D.Zero)
                {
                    dist = Math.Max(Math.Abs(ev.X - speakerLoc.X),
                                    Math.Abs(ev.Y - speakerLoc.Y));

                    // Word hasn't traveled this far yet.
                    if (age < TimeSpan.FromSeconds(60 + dist * NewsSecondsPerTile))
                    {
                        continue;
                    }

                    // Not news here. You are looking at it.
                    if (dist <= HereRadius && age < HereAge)
                    {
                        continue;
                    }
                }

                double w = baseW / (1.0 + ev.TellCount);
                if (own)
                {
                    w *= 2.5; // people lead with their own stories
                }
                pool ??= new List<(BotEvent, string, double, int)>();
                pool.Add((ev, key, w, dist));
                totalW += w;
            }

            if (pool == null)
            {
                return null;
            }

            double r = Utility.RandomDouble() * totalW;
            var (picked, pickedKey, _, pickedDist) = pool[^1];
            foreach (var (ev, key, w, d) in pool)
            {
                r -= w;
                if (r <= 0)
                {
                    picked = ev;
                    pickedKey = key;
                    pickedDist = d;
                    break;
                }
            }

            bool isOwn = pickedKey.EndsWith("_self", StringComparison.Ordinal);
            var pickedAge = now - picked.At;

            // A name that keeps coming up gets talked about as a name.
            int repeat = 0;
            if (!isOwn && picked.Type == "pk" && picked.Other.Length > 0)
            {
                repeat = CountByKiller(picked.Other, now);
                if (repeat >= RepeatKillerAt && _templates.ContainsKey("pk_repeat"))
                {
                    pickedKey = "pk_repeat";
                }
            }

            // How much of it the speaker actually knows.
            int detail = isOwn ? 2
                       : pickedDist <= VividRadius && pickedAge < VividAge ? 2
                       : pickedDist <= VagueRadius || pickedAge < VividAge ? 1
                       : 0;

            string actor = picked.Actor;
            string other = picked.Other;

            // For a party, a war band or a convoy, {other} is WHERE they
            // went, and it arrived here as the destination's handle —
            // "Brit GY'" was said out loud. Same words a person would use.
            if (picked.Type is "party" or "warband" or "convoy" or "party_self")
            {
                var dest = DestinationCatalog.GetByName(other);
                if (dest != null)
                {
                    other = !string.IsNullOrEmpty(dest.Dungeon) ? dest.Dungeon : Humanize(dest);
                }
            }
            if (detail < 2 && !isOwn && actor.Length > 0 && Utility.RandomDouble() < 0.6)
            {
                actor = Utility.RandomDouble() < 0.5 ? "someone" : "some poor sod";
            }
            if (detail == 0 && (picked.Type == "pk" || picked.Type == "red") && other.Length > 0)
            {
                other = Utility.RandomDouble() < 0.5 ? "a red" : "some red";
            }

            var templates = _templates[pickedKey];
            List<string> usable = null;
            foreach (var t in templates)
            {
                if (other.Length == 0 && t.Contains("{other}", StringComparison.Ordinal))
                {
                    continue;
                }
                if (actor.Length == 0 && t.Contains("{actor}", StringComparison.Ordinal))
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
            NoteTold(speakerName, picked, now);
            var line = Fill(usable[Utility.Random(usable.Count)],
                            actor, other, picked.Place, pickedAge, repeat);

            if (speaker != null)
            {
                // Said TO someone, some of the time. An announcement to the
                // air is what made it read like a ticker.
                var listener = NearbyListener(speaker, actor);
                if (listener != null && Utility.RandomDouble() < 0.35)
                {
                    line = $"{FirstName(listener)}, {line}";
                }

                ScheduleReaction(speaker, picked, actor, other, listener);
            }

            return line;
        }

        private static string Fill(string template, string actor, string other, string place,
                                   TimeSpan age, int count)
        {
            var line = template
                .Replace("{actor}", actor, StringComparison.Ordinal)
                .Replace("{other}", other, StringComparison.Ordinal)
                .Replace("{place}", place, StringComparison.Ordinal)
                .Replace("{count}", count.ToString(), StringComparison.Ordinal)
                .Replace("{when}", WhenPhrase(age), StringComparison.Ordinal);

            // {when} is usually nothing; tidy the gap it leaves.
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\s{2,}", " ");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\s+([,.?])", "$1");

            // A place name can carry its own preposition ("near a dig
            // site") or be a kind of ground rather than a spot ("the
            // wilderness"), and the template already said "at". "at near a
            // dig site" and "red near the wilderness" both went out.
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\b(at|in|to|by) near\b", "near");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\b(at|near|by) the wilderness\b", "out in the wilds");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\bto the wilderness\b", "out into the wilds");
            line = line.Replace("the wilderness", "the wilds", StringComparison.Ordinal);
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\b(at|near|by) outside\b", "outside");

            // Things happen IN a town, AT a shrine.
            line = System.Text.RegularExpressions.Regex.Replace(line,
                @"\bat (Britain|Vesper|Trinsic|Minoc|Yew|Moonglow|Magincia|Jhelom|Skara Brae|Nujel'm|Occlo|Serpent's Hold|Cove|Buccaneer's Den|Wind|Papua|Delucia)\b",
                "in $1");
            return line.Trim();
        }

        // A timestamp on every line is what a ticker does. People mostly
        // leave it out, and say it loosely when they do.
        private static string WhenPhrase(TimeSpan age)
        {
            double roll = Utility.RandomDouble();
            return age switch
            {
                { TotalMinutes: < 10 } => roll < 0.5 ? "" : "just now",
                { TotalMinutes: < 45 } => roll < 0.6 ? "" : roll < 0.8 ? "not long ago" : "a bit ago",
                { TotalHours: < 2 }    => roll < 0.5 ? "" : "earlier",
                _                      => roll < 0.4 ? "" : roll < 0.7 ? "earlier today" : "a while back",
            };
        }

        private static int CountByKiller(string killer, DateTime now)
        {
            int n = 0;
            for (int i = _ring.Count - 1; i >= 0; i--)
            {
                var ev = _ring[i];
                if (now - ev.At > GossipMaxAge)
                {
                    break;
                }
                if (ev.Type == "pk" &&
                    string.Equals(ev.Other, killer, StringComparison.OrdinalIgnoreCase))
                {
                    n++;
                }
            }
            return n;
        }

        private static string FirstName(Mobile m)
        {
            var name = m?.Name ?? "";
            int sp = name.IndexOf(' ');
            return (sp > 0 ? name[..sp] : name).ToLowerInvariant();
        }

        // Someone within talking distance who is not the speaker and not
        // the person the story is about. Players count — the bots are
        // talking to whoever is at the bank.
        private static Mobile NearbyListener(PlayerBot speaker, string actorName)
        {
            if (speaker?.Map == null)
            {
                return null;
            }
            Mobile best = null;
            int bestDist = int.MaxValue;
            foreach (var m in speaker.Map.GetMobilesInRange(speaker.Location, 6))
            {
                if (m == speaker || m.Deleted || !m.Alive || m is not PlayerMobile)
                {
                    continue;
                }
                if (m is PlayerBot pb && pb.Combatant != null)
                {
                    continue;
                }
                if (string.Equals(m.Name, actorName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int d = Math.Max(Math.Abs(m.X - speaker.X), Math.Abs(m.Y - speaker.Y));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = m;
                }
            }
            return best;
        }

        // Gossip is a two-way thing. A bystander answers a beat later —
        // "again?", "which way did he go" — from react_<type>.txt, or the
        // plain react.txt when the type has none. Nobody answers while
        // fighting, and nobody answers their own story.
        private static void ScheduleReaction(PlayerBot speaker, BotEvent ev,
                                             string actor, string other, Mobile listener)
        {
            if (speaker?.Map == null || Utility.RandomDouble() > 0.55)
            {
                return;
            }

            var responder = listener as PlayerBot;
            if (responder == null)
            {
                foreach (var m in speaker.Map.GetMobilesInRange(speaker.Location, 8))
                {
                    if (m is PlayerBot pb && pb != speaker && pb.Alive && pb.Combatant == null &&
                        !string.Equals(pb.Name, actor, StringComparison.OrdinalIgnoreCase))
                    {
                        responder = pb;
                        break;
                    }
                }
            }
            if (responder == null)
            {
                return;
            }

            var key = "react_" + ev.Type;
            if (!_templates.TryGetValue(key, out var lines) &&
                !_templates.TryGetValue("react", out lines))
            {
                return;
            }

            List<string> usable = null;
            foreach (var t in lines)
            {
                if (other.Length == 0 && t.Contains("{other}", StringComparison.Ordinal)) continue;
                if (actor.Length == 0 && t.Contains("{actor}", StringComparison.Ordinal)) continue;
                (usable ??= new List<string>()).Add(t);
            }
            if (usable == null)
            {
                return;
            }

            var reply = Fill(usable[Utility.Random(usable.Count)],
                             actor, other, ev.Place, Core.Now - ev.At, 0);
            var delay = TimeSpan.FromSeconds(2.5 + Utility.RandomDouble() * 3.0);

            Timer.DelayCall(delay, () =>
            {
                if (responder.Deleted || !responder.Alive || responder.Combatant != null ||
                    responder.Map != speaker.Map ||
                    !responder.InRange(speaker.Location, 12))
                {
                    return;
                }
                responder.Say(reply);
            });
        }

    }
}
