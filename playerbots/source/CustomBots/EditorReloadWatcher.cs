// =========================================================================
// EditorReloadWatcher.cs — lets the map editor's buttons act on the running
// game without typing commands in the client.
//
// Two file-token bridges (same one-way idiom as the LiveMap snapshot):
//
//   "Reload in game"  -> Data/Live/reload_request.txt
//        Reloads waypoints, destinations (with arrival points), and zones
//        (areas). Cheap; data only. Writes Data/Live/reload_ack.json.
//
//   "Regenerate bots" -> Data/Live/genbots_request.txt
//        Clears and re-lays the whole bot population (= [GenerateBots), so
//        bank/shop crowds move onto newly-placed arrival points. Heavier.
//        Writes Data/Live/genbots_ack.json.
//
// serve_map.py bumps a token; this watcher polls every couple seconds and
// acts when a token changes, then writes an ack the editor reads back.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using Server;

namespace Server.CustomBots
{
    public static class EditorReloadWatcher
    {
        private static string Live(string file) =>
            Path.Combine(Core.BaseDirectory, "Data", "Live", file);

        private static readonly string ReloadReq = Live("reload_request.txt");
        private static readonly string ReloadAck = Live("reload_ack.json");
        private static readonly string GenReq    = Live("genbots_request.txt");
        private static readonly string GenAck    = Live("genbots_ack.json");
        private static readonly string AuditReq  = Live("audit_request.txt");
        private static readonly string AuditAck  = Live("audit_report.json");
        private static readonly string WalkReq   = Live("walkmap_request.txt");
        private static readonly string WalkAck   = Live("walkmap.pgm");
        private static readonly string PartyReq  = Live("party_request.txt");
        private static readonly string PartyAck  = Live("party_ack.json");
        private static readonly string DeathReq  = Live("death_request.txt");
        private static readonly string DeathAck  = Live("death_ack.json");
        private static readonly string FactionReq = Live("faction_request.txt");
        private static readonly string FactionAck = Live("faction_ack.json");
        private static readonly string LiveMapReq = Live("livemap_request.txt");
        private static readonly string LiveMapAck = Live("livemap_ack.json");
        private static readonly string PKsReq = Live("pks_request.txt");
        private static readonly string PKsAck = Live("pks_ack.json");

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
        private static long _lastReload = -1;
        private static long _lastGen = -1;
        private static long _lastAudit = -1;
        private static long _lastWalk = -1;
        private static long _lastParty = -1;
        private static long _lastDeath = -1;
        private static long _lastFaction = -1;
        private static long _lastLiveMap = -1;
        private static long _lastPKs = -1;
        private static Timer _timer;

        // ModernUO calls Initialize() after the world loads — registries and
        // spawners exist by then, so reload/regen are safe.
        public static void Initialize()
        {
            // Seed from existing tokens so stale files at boot don't trigger.
            _lastReload = ReadToken(ReloadReq) ?? 0;
            _lastGen    = ReadToken(GenReq) ?? 0;
            _lastAudit  = ReadToken(AuditReq) ?? 0;
            _lastWalk   = ReadWalkRequest(out _, out _, out _, out _) ?? 0;
            _lastParty  = ReadToken(PartyReq) ?? 0;
            _lastDeath  = ReadToken(DeathReq) ?? 0;
            _lastFaction = ReadToken(FactionReq) ?? 0;
            _lastLiveMap = ReadLiveMapRequest(out _) ?? 0;
            _lastPKs = ReadToken(PKsReq) ?? 0;
            _timer = Timer.DelayCall(Interval, Interval, Poll);
        }

        private static long? ReadToken(string path)
        {
            try
            {
                if (File.Exists(path) &&
                    long.TryParse(File.ReadAllText(path).Trim(), out var t))
                {
                    return t;
                }
            }
            catch
            {
                // file may be mid-write; retry next tick
            }
            return null;
        }

        private static void Poll()
        {
            var reload = ReadToken(ReloadReq);
            if (reload != null && reload.Value != _lastReload)
            {
                _lastReload = reload.Value;
                DoReload(reload.Value);
            }

            var gen = ReadToken(GenReq);
            if (gen != null && gen.Value != _lastGen)
            {
                _lastGen = gen.Value;
                DoRegen(gen.Value);
            }

            var audit = ReadToken(AuditReq);
            if (audit != null && audit.Value != _lastAudit)
            {
                _lastAudit = audit.Value;
                DoAudit(audit.Value);
            }

            var walk = ReadWalkRequest(out int wx0, out int wy0, out int wx1, out int wy1);
            if (walk != null && walk.Value != _lastWalk)
            {
                _lastWalk = walk.Value;
                DoWalkmap(walk.Value, wx0, wy0, wx1, wy1);
            }

            var partyTok = ReadToken(PartyReq);
            if (partyTok != null && partyTok.Value != _lastParty)
            {
                _lastParty = partyTok.Value;
                DoFormParty(partyTok.Value);
            }

            var deathTok = ReadToken(DeathReq);
            if (deathTok != null && deathTok.Value != _lastDeath)
            {
                _lastDeath = deathTok.Value;
                DoTestDeath(deathTok.Value);
            }

            var factionTok = ReadToken(FactionReq);
            if (factionTok != null && factionTok.Value != _lastFaction)
            {
                _lastFaction = factionTok.Value;
                DoTestFactionFight(factionTok.Value);
            }

            var liveTok = ReadLiveMapRequest(out double liveSecs);
            if (liveTok != null && liveTok.Value != _lastLiveMap)
            {
                _lastLiveMap = liveTok.Value;
                DoLiveMap(liveTok.Value, liveSecs);
            }

            var pksTok = ReadToken(PKsReq);
            if (pksTok != null && pksTok.Value != _lastPKs)
            {
                _lastPKs = pksTok.Value;
                DoGenPKs(pksTok.Value);
            }
        }

        // pks_request.txt: place the default road-PK spawner set (born-red
        // hunters) — the headless [GeneratePKs. Clears any existing PK
        // spawners first so it never stacks.
        private static void DoGenPKs(long token)
        {
            int placed = 0, pks = 0;
            try
            {
                GeneratePKsCommand.ClearPKSpawners();
                (placed, pks) = GeneratePKsCommand.PlaceDefault();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] genpks: {ex.Message}");
            }

            Console.WriteLine(
                $"[EditorReload] PK spawners placed: {placed} for ~{pks} red hunters (token {token}).");
            WriteAck(PKsAck,
                $"{{\"token\":{token},\"spawners\":{placed},\"pks\":{pks}}}");
        }

        // livemap_request.txt: "token seconds" — seconds >= 1 starts the
        // [LiveMap snapshot timer at that cadence, seconds <= 0 stops it.
        // Lets the map editor's Live checkbox drive snapshots directly, no
        // client needed.
        private static long? ReadLiveMapRequest(out double seconds)
        {
            seconds = 0;
            try
            {
                if (!File.Exists(LiveMapReq)) return null;
                var parts = File.ReadAllText(LiveMapReq).Split(
                    new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1 || !long.TryParse(parts[0], out var t)) return null;
                if (parts.Length > 1)
                {
                    double.TryParse(parts[1], out seconds);
                }
                return t;
            }
            catch
            {
                return null; // mid-write; retry next tick
            }
        }

        private static void DoLiveMap(long token, double seconds)
        {
            bool on = seconds >= 1;
            int n = 0;
            try
            {
                if (on)
                {
                    LiveMapSnapshot.StartFromEditor(seconds);
                    n = LiveMapSnapshot.WriteSnapshot();
                }
                else
                {
                    LiveMapSnapshot.StopFromEditor();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] livemap: {ex.Message}");
            }

            Console.WriteLine(on
                ? $"[EditorReload] LiveMap ON every {seconds:0}s ({n} entities, token {token})."
                : $"[EditorReload] LiveMap OFF (token {token}).");
            WriteAck(LiveMapAck,
                $"{{\"token\":{token},\"on\":{(on ? "true" : "false")}," +
                $"\"seconds\":{seconds:0.#},\"entities\":{n}}}");
        }

        // death_request.txt: kill a random eligible surface bot so headless
        // soaks can exercise the full death story (ghost → healer walk →
        // res → corpse run) on demand. Watch the [death] console lines.
        private static void DoTestDeath(long token)
        {
            var candidates = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted && bot.Alive &&
                    !bot.LifecycleExempt && !bot.LoggingOut &&
                    !BotPartyManager.IsInParty(bot) &&
                    !DungeonRegistry.IsInDungeon(bot))
                {
                    candidates.Add(bot);
                }
            }

            if (candidates.Count == 0)
            {
                WriteAck(DeathAck, $"{{\"token\":{token},\"killed\":false}}");
                return;
            }

            var victim = candidates[Utility.Random(candidates.Count)];
            Console.WriteLine($"[EditorReload] test death: killing {victim.Name} (token {token}).");
            victim.Kill();
            WriteAck(DeathAck,
                $"{{\"token\":{token},\"killed\":true," +
                $"\"name\":\"{victim.Name.Replace("\"", "\\\"")}\"," +
                $"\"x\":{victim.X},\"y\":{victim.Y}}}");
        }

        // faction_request.txt: force an Order-vs-Chaos fight (teleporting
        // one fighter to the other if none are colocated) — headless
        // equivalent of [BotFactions fight.
        private static void DoTestFactionFight(long token)
        {
            bool started = false;
            try
            {
                started = BotFactionWar.TryStartFight(teleport: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] faction fight: {ex.Message}");
            }
            WriteAck(FactionAck, $"{{\"token\":{token},\"started\":{(started ? "true" : "false")}}}");
        }

        // party_request.txt: force-form a hunting party (= [BotParties form
        // without a client) so headless soaks can exercise the full party
        // pipeline on demand. Ack reports who formed and where they're headed.
        private static void DoFormParty(long token)
        {
            BotParty party = null;
            try { party = BotPartyManager.TryFormParty(null); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] party: {ex.Message}"); }

            if (party == null)
            {
                Console.WriteLine($"[EditorReload] party request: no eligible leader/recruits (token {token}).");
                WriteAck(PartyAck, $"{{\"token\":{token},\"formed\":false}}");
                return;
            }

            var members = new List<string>();
            foreach (var m in party.Members)
            {
                members.Add("\"" + m.Name.Replace("\"", "\\\"") + "\"");
            }
            WriteAck(PartyAck,
                $"{{\"token\":{token},\"formed\":true," +
                $"\"leader\":\"{party.Leader.Name.Replace("\"", "\\\"")}\"," +
                $"\"dungeon\":\"{party.Target.Dungeon}\"," +
                $"\"members\":[{string.Join(",", members)}]}}");
        }

        // walkmap_request.txt: "token x0 y0 x1 y1" — dump per-tile
        // walkability of the rect to Data/Live/walkmap.pgm (P5, 255 =
        // standable). Lets offline tools plan waypoints against the
        // server's REAL movement rules instead of guessing from map art.
        private static long? ReadWalkRequest(out int x0, out int y0, out int x1, out int y1)
        {
            x0 = y0 = x1 = y1 = 0;
            try
            {
                if (!File.Exists(WalkReq)) return null;
                var parts = File.ReadAllText(WalkReq).Split(
                    new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !long.TryParse(parts[0], out var t)) return null;
                x0 = int.Parse(parts[1]); y0 = int.Parse(parts[2]);
                x1 = int.Parse(parts[3]); y1 = int.Parse(parts[4]);
                return t;
            }
            catch
            {
                return null; // mid-write; retry next tick
            }
        }

        private static void DoWalkmap(long token, int x0, int y0, int x1, int y1)
        {
            const int MaxSide = 512; // bound the game-thread stall
            if (x1 < x0) (x0, x1) = (x1, x0);
            if (y1 < y0) (y0, y1) = (y1, y0);
            x1 = Math.Min(x1, x0 + MaxSide - 1);
            y1 = Math.Min(y1, y0 + MaxSide - 1);
            int w = x1 - x0 + 1, h = y1 - y0 + 1;

            var map = Map.Felucca;
            var bytes = new byte[w * h];
            var zbytes = new byte[w * h]; // resolved standing Z + 128 (0 = unwalkable)
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (Walkable.TryFindSeedZ(map, x0 + x, y0 + y, 0, out int z))
                    {
                        bytes[y * w + x] = 255;
                        zbytes[y * w + x] = (byte)Math.Clamp(z + 128, 1, 255);
                    }
                }
            }

            try
            {
                using var fs = new FileStream(WalkAck, FileMode.Create, FileAccess.Write);
                var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n");
                fs.Write(header, 0, header.Length);
                fs.Write(bytes, 0, bytes.Length);

                // Z sidecar: offline trail A* needs per-tile heights to apply
                // the climb/drop step rules — a flat mask over-connects
                // adjacent tiles split by a cliff seam.
                using var fz = new FileStream(
                    Live("walkmap_z.pgm"), FileMode.Create, FileAccess.Write);
                fz.Write(header, 0, header.Length);
                fz.Write(zbytes, 0, zbytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] walkmap write failed: {ex.Message}");
            }

            Console.WriteLine(
                $"[EditorReload] walkmap ({x0},{y0})-({x1},{y1}) {w}x{h} written (token {token}).");
        }

        // Full nav-data verification without a client: the [AuditNav data
        // checks plus the [auditedges walkability flood, results to
        // Data/Live/audit_report.json. Lets data authored outside the game
        // (map editor, scripts, Claude) be verified headlessly.
        private static void DoAudit(long token)
        {
            var lines = new List<string>();
            try { lines.AddRange(AuditNavCommand.Run()); }
            catch (Exception ex) { lines.Add($"AuditNav failed: {ex.Message}"); }
            try
            {
                foreach (var l in AuditEdgesCommand.Scan())
                {
                    lines.Add($"EDGEWALK: {l}");
                }
            }
            catch (Exception ex) { lines.Add($"auditedges scan failed: {ex.Message}"); }

            Console.WriteLine($"[EditorReload] audit ran: {lines.Count} finding(s) (token {token}).");

            for (int i = 0; i < lines.Count; i++)
            {
                lines[i] = "\"" + lines[i].Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            }
            WriteAck(AuditAck,
                $"{{\"token\":{token},\"findings\":[{string.Join(",", lines)}]}}");
        }

        private static void DoReload(long token)
        {
            int wps = 0, dests = 0, zones = 0;
            try { wps = WaypointRegistry.Load(); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] waypoints: {ex.Message}"); }
            try { dests = DestinationCatalog.Load(); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] destinations: {ex.Message}"); }
            try { zones = ZoneRegistry.Reload(); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] zones: {ex.Message}"); }

            Console.WriteLine(
                $"[EditorReload] reloaded {wps} waypoint(s), {dests} destination(s), " +
                $"{zones} zone(s) (token {token}).");

            WriteAck(ReloadAck,
                $"{{\"token\":{token},\"waypoints\":{wps}," +
                $"\"destinations\":{dests},\"zones\":{zones}}}");
        }

        private static void DoRegen(long token)
        {
            int spawners = 0;
            try { spawners = GenerateBotsCommand.RegenerateForPopulation(); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] regen: {ex.Message}"); }

            Console.WriteLine(
                $"[EditorReload] regenerated bot population: {spawners} spawner(s) (token {token}).");

            WriteAck(GenAck, $"{{\"token\":{token},\"spawners\":{spawners}}}");
        }

        private static void WriteAck(string path, string json)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] ack write failed: {ex.Message}");
            }
        }
    }
}
