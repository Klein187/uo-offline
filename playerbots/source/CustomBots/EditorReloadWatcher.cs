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

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
        private static long _lastReload = -1;
        private static long _lastGen = -1;
        private static Timer _timer;

        // ModernUO calls Initialize() after the world loads — registries and
        // spawners exist by then, so reload/regen are safe.
        public static void Initialize()
        {
            // Seed from existing tokens so stale files at boot don't trigger.
            _lastReload = ReadToken(ReloadReq) ?? 0;
            _lastGen    = ReadToken(GenReq) ?? 0;
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
