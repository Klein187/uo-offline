// =========================================================================
// DestinationFieldCache.cs — One local DistanceField per BotDestination.
//
// Built from DestinationCatalog at world load. Keyed by destination name.
// The waypoint graph still routes a bot across the continent to the
// destination's NearestWaypoint; once the bot is within a field's radius,
// the Traveler uses FieldApproach.Step to walk the final stretch straight
// to the exact destination tile — through doors and into interiors the
// flood reached. This replaces the Traveler's StartDrift/TickDrift block.
//
// Cost control: only LOCAL fields (small radius), so total memory is
// bounded. With ~N destinations and radius R, storage ~= N * (2R+1)^2 worst
// case, far less in practice (walls/water shrink coverage). Radius 60 over
// open ground is ~14k tiles; most destinations far less.
//
// Rebuild any time with [rebuildfields (see AnchorCommands.cs).
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public static class DestinationFieldCache
    {
        // Local-approach radius. Big enough that a bot arriving at the
        // destination's NearestWaypoint is already inside the field (the
        // waypoint should be within this many tiles of the destination),
        // small enough to stay cheap. Tune via [rebuildfields if needed.
        public const int DefaultRadius = 60;

        private static readonly Dictionary<string, DistanceField> _fields =
            new(StringComparer.OrdinalIgnoreCase);

        public static int Count => _fields.Count;

        // Build (or rebuild) a field for every destination in the catalog.
        // Returns (built, totalTiles, ms).
        public static (int built, long tiles, double ms) BuildAll(int radius = DefaultRadius, bool force = false)
        {
            var start = DateTime.UtcNow;
            ulong fp = Fingerprint(radius);

            if (!force && TryLoadFromDisk(fp, out long loadedTiles))
            {
                var lms = (DateTime.UtcNow - start).TotalMilliseconds;
                Console.WriteLine($"[fields] {_fields.Count} field(s) loaded from cache in {lms:0} ms.");
                return (_fields.Count, loadedTiles, lms);
            }

            _fields.Clear();
            long tiles = 0;

            foreach (var dest in DestinationCatalog.All)
            {
                var map = Map.Felucca;
                if (map == null) continue;

                var field = DistanceField.Build(map, dest.Location, radius);
                _fields[dest.Name] = field;
                tiles += field.CoveredTiles;
            }

            SaveToDisk(fp);
            var ms = (DateTime.UtcNow - start).TotalMilliseconds;
            return (_fields.Count, tiles, ms);
        }

        // ---- disk cache: skip the ~60s flood when nothing changed ----------
        private const int CacheVersion = 1;
        private static string CachePath => System.IO.Path.Combine(
            Core.BaseDirectory, "Data", "Navigation", "fields_cache.bin");

        // Deterministic FNV-1a over version + radius + every destination's
        // name/coords, so editing destinations.json invalidates the cache.
        private static ulong Fingerprint(int radius)
        {
            ulong h = 14695981039346656037UL;
            void Mix(long v)
            { for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= 1099511628211UL; } }
            Mix(CacheVersion); Mix(radius);
            foreach (var d in DestinationCatalog.All)
            {
                foreach (char c in d.Name) Mix(c);
                Mix(d.Location.X); Mix(d.Location.Y); Mix(d.Location.Z);
            }
            return h;
        }

        private static void SaveToDisk(ulong fp)
        {
            try
            {
                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(CachePath));
                using var w = new System.IO.BinaryWriter(
                    System.IO.File.Create(CachePath));
                w.Write(0x554F4643); // 'UOFC'
                w.Write(fp);
                w.Write(_fields.Count);
                foreach (var kv in _fields)
                {
                    w.Write(kv.Key);
                    kv.Value.WriteTo(w);
                }
                Console.WriteLine($"[fields] cached {_fields.Count} field(s) to disk.");
            }
            catch (Exception ex)
            { Console.WriteLine($"[fields] cache save failed: {ex.Message}"); }
        }

        private static bool TryLoadFromDisk(ulong fp, out long tiles)
        {
            tiles = 0;
            try
            {
                if (!System.IO.File.Exists(CachePath)) return false;
                using var r = new System.IO.BinaryReader(
                    System.IO.File.OpenRead(CachePath));
                if (r.ReadInt32() != 0x554F4643) return false;
                if (r.ReadUInt64() != fp) return false;   // stale -> rebuild
                int n = r.ReadInt32();
                _fields.Clear();
                for (int i = 0; i < n; i++)
                {
                    string name = r.ReadString();
                    var f = DistanceField.ReadFrom(r);
                    _fields[name] = f;
                    tiles += f.CoveredTiles;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[fields] cache load failed: {ex.Message}");
                _fields.Clear();
                return false;
            }
        }

        public static DistanceField Get(string destinationName)
        {
            if (string.IsNullOrEmpty(destinationName)) return null;
            _fields.TryGetValue(destinationName, out var f);
            return f;
        }
    }

    // -----------------------------------------------------------------------
    // FieldApproach — the helper the Traveler calls during final approach.
    //
    // Replaces StartDrift/TickDrift/EndDrift. Given the bot and the field
    // for its destination, takes one step toward the exact destination tile
    // each call. Returns an enum the Traveler reacts to.
    //
    // Usage inside TravelerBehavior, AFTER the bot has reached the final
    // waypoint and _hasArrived is set (arrival is already recorded — this is
    // the cosmetic-but-now-reliable final walk-in):
    //
    //     var field = DestinationFieldCache.Get(DestinationName);
    //     switch (FieldApproach.Step(bot, field, _finalCoord, DriftArriveRange))
    //     {
    //         case ApproachResult.Arrived:  StopStepTimer(); break;
    //         case ApproachResult.NoField:  // fall back to old drift, or just stop
    //         case ApproachResult.Blocked:  // count toward a give-up
    //     }
    // -----------------------------------------------------------------------
    public enum ApproachResult
    {
        Stepped,    // moved one tile closer along the field
        Arrived,    // within arrival range of the destination tile
        Blocked,    // field said stop but we're not there (edge of coverage)
        NoField,    // no field for this destination — caller should fall back
    }

    public static class FieldApproach
    {
        public static ApproachResult Step(PlayerBot bot, DistanceField field,
                                          Point3D goal, int arriveRange)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
                return ApproachResult.Blocked;

            if (field == null)
                return ApproachResult.NoField;

            int x = bot.X, y = bot.Y;

            // Arrived?
            if (Math.Max(Math.Abs(x - goal.X), Math.Abs(y - goal.Y)) <= arriveRange)
                return ApproachResult.Arrived;

            // Not yet inside the field's coverage (bot is beyond radius). The
            // waypoint should have delivered the bot into coverage; if not,
            // tell the caller so it can fall back.
            if (!field.Covers(x, y))
                return ApproachResult.NoField;

            if (!field.TryStep(x, y, out int nx, out int ny))
            {
                // At a local minimum that isn't the goal — only happens at
                // the very edge of coverage. Treat as blocked.
                return ApproachResult.Blocked;
            }

            Direction d = DirTo(x, y, nx, ny);
            if (bot.Direction != d) bot.Direction = d;

            return bot.Move(d) ? ApproachResult.Stepped : ApproachResult.Blocked;
        }

        private static Direction DirTo(int x, int y, int nx, int ny)
        {
            int dx = Math.Sign(nx - x), dy = Math.Sign(ny - y);
            return (dx, dy) switch
            {
                ( 0, -1) => Direction.North,
                ( 1, -1) => Direction.Right,
                ( 1,  0) => Direction.East,
                ( 1,  1) => Direction.Down,
                ( 0,  1) => Direction.South,
                (-1,  1) => Direction.Left,
                (-1,  0) => Direction.West,
                (-1, -1) => Direction.Up,
                _        => Direction.North,
            };
        }
    }
}
