// =========================================================================
// DungeonSpawnFix.cs — monsters in every T2A dungeon, and not in a pile.
//
// First Time Setup spawns the world from Spawners/uoclassic/UOClassic.map,
// a pre-T2A list. It has no spawners at all in the dungeons T2A added
// (Ice, Fire, Terathan Keep) or in the Orc Cave. And 222 of its dungeon
// spawners have a home range of 0, which makes the engine put every
// monster on the spawner's own tile. Small ranges do the same once their
// few tiles fill up: a spawn that finds no free tile falls back to the
// spawner's tile. Players saw both: an empty Ice dungeon, and monsters
// stacked on top of each other.
//
// Two passes, run on every boot and at the end of First Time Setup. Both
// leave the world alone once it is right, so running them again is safe:
//
//   1. A dungeon with no spawner in it gets the spawners from
//      Data/CustomSpawns/t2a_dungeon_spawns.json (ModernUO's own Ice,
//      Fire, Terathan Keep and Orc Cave sets, minus the orc bombers and
//      brutes, which came after T2A).
//   2. A Felucca dungeon spawner that makes monsters gets a home range of
//      at least MinHomeRange and 60 tries at finding a free tile, then
//      respawns so the pile it already made spreads out. Several count-1
//      spawners on one tile (Deceit has many) pile up too, so count does
//      not matter. Treasure chest spawners keep their exact spot.
//
//   [FixDungeonSpawns — run both passes now.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using Server.Commands;
using Server.Engines.Spawners;
using Server.Regions;

namespace Server.CustomBots
{
    public static class DungeonSpawnFix
    {
        // The dungeons pass 1 fills, by region name.
        private static readonly string[] MissingDungeons =
        {
            "Ice", "Fire", "Terathan Keep", "Orc Cave",
        };

        public const int MinHomeRange = 4;

        // A big spawner needs room for its whole count.
        public const int MinHomeRangeLarge = 6;
        private const int LargeCount = 8;

        // How far up and down from the spawner's floor a monster may land.
        // Enough for a ramp or a step, not enough to reach another floor.
        private const int FloorSlack = 10;

        private const int SpawnAttempts = 60;

        private static string DataFile =>
            Path.Combine(Core.BaseDirectory, "Data", "CustomSpawns", "t2a_dungeon_spawns.json");

        public static void Initialize()
        {
            CommandSystem.Register("FixDungeonSpawns", AccessLevel.Administrator, e =>
            {
                var (added, spread) = Run();
                e.Mobile.SendMessage($"Dungeon spawns: {added} spawner(s) added, {spread} spread out.");
            });

            Run();
        }

        public static (int added, int spread) Run()
        {
            var spawners = new List<BaseSpawner>();
            foreach (var item in World.Items.Values)
            {
                // Plain engine spawners only. PlayerBotSpawner derives from
                // Spawner, and the bot spawners are not ours to move.
                if (item is BaseSpawner s && s is not PlayerBotSpawner &&
                    !s.Deleted && s.Map == Map.Felucca)
                {
                    spawners.Add(s);
                }
            }

            int added = AddMissingDungeons(spawners);
            int spread = SpreadPiles(spawners);

            if (added > 0 || spread > 0)
            {
                Console.WriteLine($"[DungeonSpawns] {added} spawner(s) added, {spread} spread out");
            }

            return (added, spread);
        }

        private static string DungeonOf(Item item) =>
            Region.Find(item.Location, item.Map).GetRegion<DungeonRegion>()?.Name;

        private static int AddMissingDungeons(List<BaseSpawner> spawners)
        {
            var stocked = new HashSet<string>();
            foreach (var s in spawners)
            {
                var name = DungeonOf(s);
                if (name != null)
                {
                    stocked.Add(name);
                }
            }

            bool anyMissing = false;
            foreach (var name in MissingDungeons)
            {
                if (!stocked.Contains(name))
                {
                    anyMissing = true;
                }
            }

            // A world with no spawners at all has not had First Time Setup
            // yet. Setup runs this again once the world is spawned.
            if (!anyMissing || spawners.Count == 0 || !File.Exists(DataFile))
            {
                return 0;
            }

            var all = new Dictionary<Guid, ISpawner>();
            foreach (var s in spawners)
            {
                all[s.Guid] = s;
            }

            ImportSpawnersCommand.ImportFile(new FileInfo(DataFile), all);

            // The file holds all four dungeons. Any dungeon that already had
            // spawners of its own keeps only those.
            int added = 0;
            foreach (var spawner in all.Values)
            {
                if (spawner is not BaseSpawner bs || bs.Deleted || spawners.Contains(bs))
                {
                    continue;
                }

                var name = DungeonOf(bs);
                if (name != null && stocked.Contains(name))
                {
                    bs.Delete();
                    continue;
                }

                spawners.Add(bs);
                added++;
            }

            return added;
        }

        private static int SpreadPiles(List<BaseSpawner> spawners)
        {
            int spread = 0;
            foreach (var s in spawners)
            {
                if (s.Deleted || DungeonOf(s) == null || MakesOnlyItems(s))
                {
                    continue;
                }

                bool changed = false;

                int want = s.Count >= LargeCount ? MinHomeRangeLarge : MinHomeRange;
                if (s.HomeRange < want)
                {
                    var at = s.Location;
                    s.SpawnBounds = new Rectangle3D(
                        at.X - want,
                        at.Y - want,
                        at.Z - FloorSlack,
                        want * 2 + 1,
                        want * 2 + 1,
                        FloorSlack * 2 + 1
                    );
                    changed = true;
                }

                // After this many misses the engine drops the spawn on the
                // spawner's own tile. Its 10 is too few for a big area that
                // is mostly rock (Shame's slimes cover 201x201).
                if (s.MaxSpawnAttempts < SpawnAttempts)
                {
                    s.MaxSpawnAttempts = SpawnAttempts;
                    changed = true;
                }

                if (changed)
                {
                    s.Respawn();
                    spread++;
                }
            }

            return spread;
        }

        // Treasure chest spawners sit on purpose where the chest belongs.
        private static bool MakesOnlyItems(BaseSpawner s)
        {
            if (s.Entries == null || s.Entries.Count == 0)
            {
                return true;
            }

            foreach (var entry in s.Entries)
            {
                var type = AssemblyHandler.FindTypeByName(entry.SpawnedName);
                if (type == null || !typeof(Item).IsAssignableFrom(type))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
