// =========================================================================
// ExportBotSpawnersCommand.cs — [ExportBotSpawners admin command.
//
// Walks the world, finds every PlayerBotSpawner, writes their definitions
// to Distribution/Data/PlayerBotSpawners.json. This file can then be
// committed to the GitHub repo alongside the source code — when other
// users deploy and run [GenerateBots, they get exactly the placements
// the file describes.
//
// JSON format:
//   {
//     "spawners": [
//       { "map": "Felucca", "x": 1434, "y": 1697, "z": 0,
//         "behaviorName": "BankSitter", "amount": 15, "boundsRadius": 10 }
//     ]
//   }
//
// boundsRadius is derived from the spawner's actual SpawnBounds rectangle.
// Square bounds are assumed (which is what BotSpawnerHere creates); for
// non-square bounds we use the larger half-width.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class ExportBotSpawnersCommand
    {
        private static readonly string OutputPath =
            Path.Combine(Core.BaseDirectory, "Data", "PlayerBotSpawners.json");

        public static void Configure()
        {
            CommandSystem.Register("ExportBotSpawners", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("ExportBotSpawners")]
        [Description("Writes every PlayerBotSpawner in the world to Data/PlayerBotSpawners.json.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null)
            {
                return;
            }

            var list = new List<SpawnerRecord>();

            foreach (var item in World.Items.Values)
            {
                if (item is not PlayerBotSpawner pbs || pbs.Deleted || pbs.Map == null || pbs.Map == Map.Internal)
                {
                    continue;
                }

                // Derive a square "radius" from the bounds rectangle. Bounds
                // are centered on the spawner tile by convention; compute
                // distance from spawner to bounds edge.
                var bounds = pbs.SpawnBounds;
                int radius = 0;
                if (bounds != default)
                {
                    int halfWidth  = (bounds.End.X - bounds.Start.X) / 2;
                    int halfHeight = (bounds.End.Y - bounds.Start.Y) / 2;
                    radius = Math.Max(halfWidth, halfHeight);
                }

                // Total target population. BaseSpawner.Count is the
                // spawner-wide max, summed across all entries. Our spawners
                // only ever have one entry ("PlayerBot") so this is exactly
                // what we want without iterating internal collections.
                int amount = pbs.Count;

                list.Add(new SpawnerRecord
                {
                    map          = pbs.Map.Name,
                    x            = pbs.X,
                    y            = pbs.Y,
                    z            = pbs.Z,
                    behaviorName = pbs.BehaviorName,
                    amount       = amount > 0 ? amount : 1,
                    boundsRadius = radius
                });
            }

            // Pretty-print so the file is human-editable.
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json    = JsonSerializer.Serialize(new Wrapper { spawners = list }, options);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(OutputPath) ?? Core.BaseDirectory);
                File.WriteAllText(OutputPath, json);
                from.SendMessage($"Exported {list.Count} PlayerBotSpawner(s) to {OutputPath}");
            }
            catch (Exception ex)
            {
                from.SendMessage($"Export failed: {ex.Message}");
            }
        }

        // --- JSON DTO ---
        // Public members (camelCase here, matching the JSON convention).

        public class Wrapper
        {
            public List<SpawnerRecord> spawners { get; set; }
        }

        public class SpawnerRecord
        {
            public string map          { get; set; }
            public int    x            { get; set; }
            public int    y            { get; set; }
            public int    z            { get; set; }
            public string behaviorName { get; set; }
            public int    amount       { get; set; }
            public int    boundsRadius { get; set; }
        }
    }
}
