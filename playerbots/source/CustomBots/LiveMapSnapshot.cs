// =========================================================================
// LiveMapSnapshot.cs — [LiveMap : the spawn editor's Phase-2 "live view".
//
// The map editor normally reads static JSON (waypoints/destinations/spawns).
// To show the ACTUAL living world — where bots and creatures are RIGHT NOW —
// something has to bridge the running game to the browser. This does it the
// project's usual way: a timer periodically snapshots the relevant mobiles on
// a map and writes a compact JSON to Data/Live/entities.json. serve_map.py
// serves that file and the map polls it.
//
//   [LiveMap on [seconds]   start snapshotting (default every 3s)
//   [LiveMap off            stop
//   [LiveMap once           write a single snapshot now
//   [LiveMap                report status
//
// OFF by default — it only walks the mobile table while someone is actually
// watching the live map, so there's zero idle cost.
//
// Entity kinds: Bot (PlayerBot, tagged with behavior/class/fixed), Vendor
// (BaseVendor), Pet (controlled/summoned creature), Monster (Karma < 0
// creature), NPC (everything else BaseCreature).
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Server;
using Server.Commands;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class LiveMapSnapshot
    {
        // Which map to snapshot. The world map editor renders Felucca.
        private static Map _map = Map.Felucca;

        // Hard cap so a huge world can't produce a runaway file.
        private const int MaxEntities = 12000;

        private static readonly string OutPath =
            Path.Combine(Core.BaseDirectory, "Data", "Live", "entities.json");

        private static Timer _timer;
        private static TimeSpan _interval = TimeSpan.FromSeconds(3);

        public static void Configure()
        {
            CommandSystem.Register("LiveMap", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("LiveMap [on|off|once] [seconds]")]
        [Description(
            "Live world view for the map editor. 'on' writes Data/Live/" +
            "entities.json every few seconds (default 3); 'off' stops; " +
            "'once' writes a single snapshot.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            string arg = e.Arguments.Length > 0 ? e.Arguments[0].ToLowerInvariant() : "";

            switch (arg)
            {
                case "on":
                {
                    if (e.Arguments.Length > 1 &&
                        double.TryParse(e.Arguments[1], out var secs) && secs >= 1)
                    {
                        _interval = TimeSpan.FromSeconds(secs);
                    }
                    Start();
                    int n = WriteSnapshot();
                    from?.SendMessage(
                        $"LiveMap ON — snapshotting {_map} every {_interval.TotalSeconds:0}s " +
                        $"({n} entities). Tick 'Live' in the map editor.");
                    break;
                }
                case "off":
                {
                    Stop();
                    from?.SendMessage("LiveMap OFF.");
                    break;
                }
                case "once":
                {
                    int n = WriteSnapshot();
                    from?.SendMessage($"LiveMap: wrote one snapshot ({n} entities) to {OutPath}.");
                    break;
                }
                default:
                {
                    from?.SendMessage(
                        _timer != null
                            ? $"LiveMap is ON ({_interval.TotalSeconds:0}s, {_map})."
                            : "LiveMap is OFF. Use [LiveMap on [seconds].");
                    break;
                }
            }
        }

        private static void Start()
        {
            _timer?.Stop();
            _timer = Timer.DelayCall(_interval, _interval, () => WriteSnapshot());
        }

        private static void Stop()
        {
            _timer?.Stop();
            _timer = null;
        }

        // Editor bridge (EditorReloadWatcher's livemap_request.txt): the map
        // editor's Live checkbox starts/stops snapshotting without a client.
        public static bool Running => _timer != null;

        public static void StartFromEditor(double seconds)
        {
            if (seconds >= 1)
            {
                _interval = TimeSpan.FromSeconds(seconds);
            }
            Start();
        }

        public static void StopFromEditor() => Stop();

        // ---- snapshot ----

        private sealed class Ent
        {
            public string k { get; set; }   // kind
            public int x { get; set; }
            public int y { get; set; }
            public string b { get; set; }    // bot behavior
            public string c { get; set; }    // bot class
            public bool? fx { get; set; }     // bot fixed-role (lifecycle exempt)
            public string t { get; set; }     // creature type name
            public string n { get; set; }     // name (bots only, keeps file small)
            public string s { get; set; }     // bot status line ("what am I doing")
            public int? hp { get; set; }       // bot HP percent (0-100)
            public bool? red { get; set; }     // bot is a murderer (Kills >= 5)
            public List<int> r { get; set; }   // traveling bot route, flat [x0,y0,x1,y1,...]
            public int? li { get; set; }        // current leg index into r (in node units)
        }

        private sealed class Snap
        {
            public string map { get; set; }
            public int count { get; set; }
            public List<Ent> entities { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Returns the number of entities written.
        public static int WriteSnapshot()
        {
            var ents = new List<Ent>();
            try
            {
                foreach (var m in World.Mobiles.Values)
                {
                    if (m == null || m.Deleted || m.Map != _map)
                    {
                        continue;
                    }

                    Ent ent = Classify(m);
                    if (ent == null)
                    {
                        continue;
                    }

                    ent.x = m.X;
                    ent.y = m.Y;
                    ents.Add(ent);
                    if (ents.Count >= MaxEntities)
                    {
                        break;
                    }
                }

                var snap = new Snap { map = _map.Name, count = ents.Count, entities = ents };
                Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
                // Write to a temp file then move into place so the editor never
                // reads a half-written file.
                var tmp = OutPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snap, JsonOpts));
                File.Move(tmp, OutPath, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LiveMap] snapshot error: {ex.Message}");
            }
            return ents.Count;
        }

        private static Ent Classify(Mobile m)
        {
            if (m is PlayerBot bot)
            {
                var ent = new Ent
                {
                    k = "Bot",
                    b = bot.Behavior?.SerializableName ?? "Idle",
                    c = bot.Class.ToString(),
                    fx = bot.LifecycleExempt,
                    n = bot.Name,
                    hp = bot.HitsMax > 0 ? (int)(bot.Hits * 100L / bot.HitsMax) : null,
                    red = bot.Kills >= 5 ? true : null
                };

                // "What am I doing" one-liner for the map's bot inspector.
                // Defensive: a status line must never be able to kill the
                // whole snapshot.
                try
                {
                    ent.s = bot.Behavior?.GetStatusLine(bot);
                }
                catch
                {
                    // leave ent.s null
                }

                // Travelers carry a planned route — resolve its node names to
                // coords so the live view can draw it when the bot is selected.
                if (bot.Behavior is TravelerBehavior tb &&
                    tb.PlannedPath != null && tb.PlannedPath.Count > 0)
                {
                    var coords = new List<int>(tb.PlannedPath.Count * 2);
                    foreach (var name in tb.PlannedPath)
                    {
                        var node = WaypointRegistry.Graph?.Get(name);
                        if (node != null)
                        {
                            coords.Add(node.Location.X);
                            coords.Add(node.Location.Y);
                        }
                        if (coords.Count >= 1000) // sanity cap (~500 legs; real routes are far shorter)
                        {
                            break;
                        }
                    }
                    if (coords.Count >= 4)
                    {
                        ent.r = coords;
                        ent.li = tb.LegIndex;
                    }
                }
                return ent;
            }

            if (m is BaseVendor)
            {
                return new Ent { k = "Vendor", t = m.GetType().Name };
            }

            if (m is BaseCreature bc)
            {
                string kind = (bc.Controlled || bc.Summoned)
                    ? "Pet"
                    : (bc.Karma < 0 ? "Monster" : "NPC");
                return new Ent { k = kind, t = m.GetType().Name };
            }

            // Real players and staff are intentionally not snapshotted.
            return null;
        }
    }
}
