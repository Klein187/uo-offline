// =========================================================================
// FieldDebugCommand.cs — [fielddebug <destination name>
//
// Diagnoses why a destination's field is tiny. Prints (to the GM AND the
// server console, so it's copyable from the terminal):
//   - the stored coord and the seed Z the scan resolved
//   - for each of the 8 neighbors: whether it's standable from the seed,
//     and at what Z (or why it failed)
//   - the land-average Z at the seed tile
//
// This tells us whether the problem is (a) the seed tile itself not being
// standable, (b) neighbors blocked by statics, or (c) a Z-window too tight
// to step off the seed. No more guessing.
// =========================================================================

using System;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class FieldDebugCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("fielddebug", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("fielddebug <destination name>")]
        [Description("Diagnose why a destination's approach field is tiny.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            string name = e.ArgString?.Trim() ?? "";
            if (name.Length == 0) { from.SendMessage("Usage: [fielddebug <name>"); return; }

            var dest = DestinationCatalog.GetByName(name);
            if (dest == null) { from.SendMessage($"No destination '{name}'."); return; }

            var map = Map.Felucca;
            int x = dest.Location.X, y = dest.Location.Y, storedZ = dest.Location.Z;
            int landZ = map.GetAverageZ(x, y);

            void Both(string s) { from.SendMessage(s); Console.WriteLine("[fielddebug] " + s); }

            Both($"--- {dest.Name} @ ({x},{y},{storedZ}) ---");
            Both($"land avg Z = {landZ}");

            bool seeded = Walkable.TryFindSeedZ(map, x, y, storedZ, out int seedZ);
            Both($"seed standable? {seeded}  seedZ = {seedZ}");

            if (!seeded)
            {
                Both("=> SEED TILE NOT STANDABLE. Coordinate likely points into " +
                     "a wall/void. Fix the coord in destinations.json.");
                return;
            }

            // Probe the 8 neighbors from the seed Z.
            int reachable = 0;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                bool ok = Walkable.CanStep(map, x, y, seedZ, x + dx, y + dy, out int nz);
                if (ok) reachable++;
                string dir = $"({dx,2},{dy,2})";
                Both($"  nbr {dir}: {(ok ? $"OK  z={nz}" : "blocked")}");
            }

            Both($"=> {reachable}/8 neighbors reachable from seed. " +
                 (reachable == 0
                    ? "Flood can't expand — Z window too tight, or seed is on " +
                      "an isolated 1-tile perch (e.g. a table/altar static)."
                    : "Flood should expand; if still tiny, neighbors may dead-end fast."));
        }
    }
}
