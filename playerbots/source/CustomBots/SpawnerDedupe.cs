// =========================================================================
// SpawnerDedupe.cs — one spawner per spot, not one per setup run.
//
// ModernUO's json spawner import replaces a spawner already standing at the
// same place, but its .map import (Nerun's format, which First Time Setup
// uses for Spawners/uoclassic/UOClassic.map) does not: every run adds a
// whole second set. Running setup again (the panel says it is safe to)
// stacked every town NPC, animal and monster spawner, so a tavern had six
// tavern keepers on top of each other and every spawn came in multiples.
//
// This keeps the oldest of any group of plain engine spawners that match
// exactly (map, location, how many it spawns, and what) and deletes the
// rest, with what they spawned. It runs on every boot (a clean world costs
// one pass and changes nothing) and at the end of First Time Setup. Bot
// spawners are never touched.
//
//   [DedupeSpawners — run it now.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Text;
using Server.Commands;
using Server.Engines.Spawners;

namespace Server.CustomBots
{
    public static class SpawnerDedupe
    {
        public static void Initialize()
        {
            CommandSystem.Register("DedupeSpawners", AccessLevel.Administrator, e =>
            {
                var (removed, groups) = Run();
                e.Mobile.SendMessage($"Removed {removed} duplicate spawner(s) from {groups} stacked spot(s).");
            });

            Run();
        }

        public static (int removed, int groups) Run()
        {
            var byKey = new Dictionary<string, List<BaseSpawner>>();
            foreach (var item in World.Items.Values)
            {
                if (item is not BaseSpawner s || s.Deleted || s.GetType() != typeof(Spawner) ||
                    s.Map == null || s.Map == Map.Internal)
                {
                    continue;
                }
                var key = KeyOf(s);
                if (!byKey.TryGetValue(key, out var list))
                {
                    byKey[key] = list = new List<BaseSpawner>();
                }
                list.Add(s);
            }

            int removed = 0, groups = 0;
            var spawnsRemoved = 0;
            var what = new Dictionary<string, int>();
            foreach (var (_, list) in byKey)
            {
                if (list.Count < 2)
                {
                    continue;
                }
                groups++;
                list.Sort((a, b) => a.Serial.Value.CompareTo(b.Serial.Value));
                for (int i = 1; i < list.Count; i++)
                {
                    var dup = list[i];
                    spawnsRemoved += dup.Spawned?.Count ?? 0;
                    var first = dup.Entries.Count > 0 ? dup.Entries[0].SpawnedName : "?";
                    what[first] = what.GetValueOrDefault(first) + 1;
                    dup.Delete();
                    removed++;
                }
            }

            if (removed > 0)
            {
                var top = new List<KeyValuePair<string, int>>(what);
                top.Sort((a, b) => b.Value.CompareTo(a.Value));
                var sb = new StringBuilder();
                for (int i = 0; i < Math.Min(8, top.Count); i++)
                {
                    sb.Append(i == 0 ? "" : ", ").Append(top[i].Key).Append(' ').Append(top[i].Value);
                }
                Console.WriteLine($"[SpawnerDedupe] removed {removed} duplicate spawner(s) at {groups} stacked " +
                    $"spot(s), taking {spawnsRemoved} extra spawn(s) with them. Most: {sb}");
            }
            return (removed, groups);
        }

        private static string KeyOf(BaseSpawner s)
        {
            var parts = new List<string>();
            foreach (var e in s.Entries)
            {
                parts.Add($"{e.SpawnedName?.ToLowerInvariant()}:{e.SpawnedMaxCount}");
            }
            parts.Sort(StringComparer.Ordinal);
            return $"{s.Map.MapID}|{s.X}|{s.Y}|{s.Z}|{s.Count}|{string.Join(",", parts)}";
        }
    }
}
