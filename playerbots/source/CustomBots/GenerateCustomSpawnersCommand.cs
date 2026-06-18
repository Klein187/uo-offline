// =========================================================================
// GenerateCustomSpawnersCommand.cs — [GenerateCustomSpawners
//
// Reads the spawn-editor's Data/CustomSpawns/spawns.json and materializes
// each record into a live spawner, by Kind:
//
//   Monster / NPC / Vendor   -> a stock ModernUO Spawner (creature types,
//                               home range, respawn window).
//   PlayerBotFixed           -> a FixedRoleBotSpawner: PlayerBots locked to
//                               one behavior; BotLifecycleManager never
//                               transitions them.
//   PlayerBotLifecycle       -> a normal PlayerBotSpawner: the bots it makes
//                               are seeded with an initial behavior and then
//                               roam/transition via the lifecycle system.
//
// Idempotent: every spawner this command creates is Named with a
// "CustomSpawn:" prefix, and a re-run deletes those first before rebuilding.
// It only touches its own spawners (that prefix) — the [GenerateBots
// population spawners are left alone.
//
// NOTE on coexistence: [GenerateBots clears ALL PlayerBotSpawners (it owns
// the ambient population), which would also remove PlayerBot-kind custom
// spawners. Run [GenerateCustomSpawners after [GenerateBots if you use both.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;
using Server.Commands;
using Server.Engines.Spawners;

namespace Server.CustomBots
{
    public static class GenerateCustomSpawnersCommand
    {
        // Name prefix marking spawners this command owns (for idempotent clear).
        private const string NamePrefix = "CustomSpawn:";

        private static readonly string JsonPath =
            Path.Combine(Core.BaseDirectory, "Data", "CustomSpawns", "spawns.json");

        public static void Configure()
        {
            CommandSystem.Register(
                "GenerateCustomSpawners", AccessLevel.Administrator, OnCommand);
        }

        // ---- JSON shape (matches the spawn editor's serve_map.py output) ----
        private sealed class Wrapper
        {
            public List<Rec> Spawns { get; set; }
        }

        private sealed class Rec
        {
            public int Id { get; set; }
            public string Kind { get; set; }
            public string Map { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
            public List<string> What { get; set; }
            public int Count { get; set; }
            public int Range { get; set; }
            public double MinDelay { get; set; }
            public double MaxDelay { get; set; }
            public string Source { get; set; }
        }

        [Usage("GenerateCustomSpawners")]
        [Description(
            "Reads Data/CustomSpawns/spawns.json (authored in the map editor) " +
            "and creates the spawners it describes. Removes previously-" +
            "generated custom spawners first (idempotent).")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null)
            {
                return;
            }

            if (!File.Exists(JsonPath))
            {
                from.SendMessage($"GenerateCustomSpawners: no file at {JsonPath}.");
                return;
            }

            int cleared = ClearExisting();
            if (cleared > 0)
            {
                from.SendMessage($"GenerateCustomSpawners: removed {cleared} previous custom spawner(s).");
            }

            Wrapper data;
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                data = JsonSerializer.Deserialize<Wrapper>(File.ReadAllText(JsonPath), opts);
            }
            catch (Exception ex)
            {
                from.SendMessage($"GenerateCustomSpawners: JSON parse failed: {ex.Message}");
                return;
            }

            if (data?.Spawns == null || data.Spawns.Count == 0)
            {
                from.SendMessage("GenerateCustomSpawners: file has no spawns.");
                return;
            }

            int made = 0;
            int failed = 0;
            foreach (var rec in data.Spawns)
            {
                try
                {
                    if (Generate(rec, from))
                    {
                        made++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    from.SendMessage($"  spawn #{rec.Id}: error {ex.Message}");
                }
            }

            from.SendMessage(
                $"GenerateCustomSpawners: created {made} spawner(s), {failed} failed.");
        }

        private static bool Generate(Rec rec, Mobile from)
        {
            var map = Map.Parse(rec.Map ?? "Felucca");
            if (map == null || map == Map.Internal)
            {
                from.SendMessage($"  spawn #{rec.Id}: unknown map '{rec.Map}'.");
                return false;
            }

            var loc = new Point3D(rec.X, rec.Y, rec.Z);
            int count = Math.Max(1, rec.Count);
            int range = Math.Max(0, rec.Range);
            var minD = TimeSpan.FromMinutes(rec.MinDelay <= 0 ? 5 : rec.MinDelay);
            var maxD = TimeSpan.FromMinutes(rec.MaxDelay <= 0 ? 15 : rec.MaxDelay);
            if (maxD < minD)
            {
                maxD = minD;
            }
            var what = rec.What ?? new List<string>();

            switch (rec.Kind)
            {
                case "Monster":
                case "NPC":
                case "Vendor":
                    return MakeCreatureSpawner(rec, map, loc, count, range, minD, maxD, what, from);
                case "PlayerBotFixed":
                    return MakeBotSpawner(rec, map, loc, count, range, minD, maxD, what, true, from);
                case "PlayerBotLifecycle":
                    return MakeBotSpawner(rec, map, loc, count, range, minD, maxD, what, false, from);
                default:
                    from.SendMessage($"  spawn #{rec.Id}: unknown kind '{rec.Kind}'.");
                    return false;
            }
        }

        // Monster / NPC / Vendor -> a stock ModernUO Spawner. Mirrors the
        // construction the Nerun importer uses (count, delays, team, names).
        private static bool MakeCreatureSpawner(
            Rec rec, Map map, Point3D loc, int count, int range,
            TimeSpan minD, TimeSpan maxD, List<string> what, Mobile from)
        {
            var valid = new List<string>();
            foreach (var w in what)
            {
                var t = AssemblyHandler.FindTypeByName(w);
                if (t != null)
                {
                    valid.Add(t.Name);
                }
                else
                {
                    from.SendMessage($"  spawn #{rec.Id}: unknown type '{w}' (skipped).");
                }
            }

            if (valid.Count == 0)
            {
                from.SendMessage($"  spawn #{rec.Id}: no valid types — nothing placed.");
                return false;
            }

            // NOTE: the 5th positional ctor param is spawnBounds, so the
            // type names go through the named `spawnedNames:` argument (same
            // as the Nerun importer). HomeRange is set AFTER MoveToWorld so it
            // builds bounds centered on the real location, not (0,0).
            var spawner = new Spawner(count, minD, maxD, 0, spawnedNames: valid.ToArray())
            {
                Name = $"{NamePrefix} {rec.Kind} #{rec.Id}"
            };
            spawner.MoveToWorld(loc, map);
            if (spawner.Map == Map.Internal)
            {
                spawner.Delete();
                from.SendMessage($"  spawn #{rec.Id}: landed on Internal map.");
                return false;
            }
            spawner.HomeRange = range;
            spawner.Respawn();
            return true;
        }

        // PlayerBot kinds -> a PlayerBotSpawner (lifecycle) or
        // FixedRoleBotSpawner (locked role). Behavior comes from What[0];
        // a lifecycle seed may omit it (defaults to Traveler).
        private static bool MakeBotSpawner(
            Rec rec, Map map, Point3D loc, int count, int range,
            TimeSpan minD, TimeSpan maxD, List<string> what, bool fixedRole, Mobile from)
        {
            string behavior =
                (what.Count > 0 && !string.IsNullOrWhiteSpace(what[0]))
                    ? what[0].Trim()
                    : (fixedRole ? "Idle" : "Traveler");

            if (range < 1)
            {
                range = 10;
            }

            PlayerBotSpawner spawner = fixedRole
                ? new FixedRoleBotSpawner(behavior, count, minD, maxD)
                : new PlayerBotSpawner(behavior, count, minD, maxD);

            spawner.SpawnBounds = new Rectangle3D(
                new Point3D(loc.X - range, loc.Y - range, loc.Z - 5),
                new Point3D(loc.X + range, loc.Y + range, loc.Z + 20));
            spawner.UseSpiralScan = true;
            spawner.Name = $"{NamePrefix} {rec.Kind} ({behavior}) #{rec.Id}";
            spawner.MoveToWorld(loc, map);
            if (spawner.Map == Map.Internal)
            {
                spawner.Delete();
                from.SendMessage($"  spawn #{rec.Id}: landed on Internal map.");
                return false;
            }
            spawner.Respawn();
            return true;
        }

        // Delete every spawner this command previously created (matched by the
        // "CustomSpawn:" Name prefix). Leaves [GenerateBots spawners untouched.
        private static int ClearExisting()
        {
            var doomed = new List<BaseSpawner>();
            foreach (var item in World.Items.Values)
            {
                if (item is BaseSpawner sp && !sp.Deleted &&
                    sp.Name != null &&
                    sp.Name.StartsWith(NamePrefix, StringComparison.Ordinal))
                {
                    doomed.Add(sp);
                }
            }
            foreach (var sp in doomed)
            {
                try { sp.Delete(); } catch { }
            }
            return doomed.Count;
        }
    }
}
