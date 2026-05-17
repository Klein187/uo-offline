// =========================================================================
// GenerateBotsCommand.cs — [GenerateBots admin command.
//
// Places PlayerBotSpawners across the world. Two modes:
//
//   1. JSON-driven (preferred). Reads Distribution/Data/PlayerBotSpawners.json
//      and creates a PlayerBotSpawner for each entry. This is how a user
//      who downloads from GitHub gets the canonical placements.
//
//   2. Fallback hardcoded list. If the JSON file isn't present, falls back
//      to a small built-in list of 9 city banks. Lets the command produce
//      *something* on fresh installs even before the JSON has been authored.
//
// Idempotent: deletes existing PlayerBotSpawners first, then re-creates.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class GenerateBotsCommand
    {
        private static readonly string JsonPath =
            Path.Combine(Core.BaseDirectory, "Data", "PlayerBotSpawners.json");

        // Hardcoded fallback list — only used when no JSON file exists.
        // These coords are educated guesses for ModernUO's T2A maps; expect
        // some to be inside walls. Better to author the JSON via the
        // [BotSpawnerHere → [ExportBotSpawners workflow.
        private sealed record FallbackSpot(
            string MapName, int X, int Y, int Z,
            string Behavior, int Amount, int BoundsRadius);

        private static readonly FallbackSpot[] FallbackList =
        {
            new("Felucca", 1434, 1697, 0, "BankSitter", 15, 10),  // Britain
            new("Felucca", 2891,  678, 0, "BankSitter",  8, 10),  // Vesper
            new("Felucca", 1832, 2839, 0, "BankSitter",  8, 10),  // Trinsic
            new("Felucca",  643,  858, 0, "BankSitter",  6, 10),  // Yew
            new("Felucca", 2511,  564, 0, "BankSitter",  6, 10),  // Minoc
            new("Felucca", 3680, 2155, 0, "BankSitter",  8, 10),  // Magincia
            new("Felucca", 1417, 3821, 0, "BankSitter",  5, 10),  // Jhelom
            new("Felucca",  591, 2147, 0, "BankSitter",  6, 10),  // Skara Brae
            new("Felucca", 4471, 1175, 0, "BankSitter",  6, 10),  // Moonglow
        };

        public static void Configure()
        {
            CommandSystem.Register("GenerateBots", AccessLevel.Administrator, OnCommand);
        }

        [Usage("GenerateBots")]
        [Description(
            "Places PlayerBotSpawners across the world. " +
            "Reads Data/PlayerBotSpawners.json if present, else uses a built-in fallback list. " +
            "Removes existing PlayerBotSpawners first."
        )]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null)
            {
                return;
            }

            ClearExistingSpawners(from);

            int placed = 0;

            if (File.Exists(JsonPath))
            {
                from.SendMessage($"GenerateBots: loading {JsonPath}...");
                placed = LoadFromJson(from);
            }
            else
            {
                from.SendMessage("GenerateBots: no JSON file found; using built-in fallback list.");
                placed = LoadFromFallback(from);
            }

            from.SendMessage($"GenerateBots: placed {placed} spawner(s).");
            from.SendMessage("Walk to any bank to see the crowd. Bots spawn over the next few seconds.");
        }

        private static void ClearExistingSpawners(Mobile from)
        {
            var existing = new List<PlayerBotSpawner>();
            foreach (var item in World.Items.Values)
            {
                if (item is PlayerBotSpawner pbs && !pbs.Deleted)
                {
                    existing.Add(pbs);
                }
            }
            foreach (var pbs in existing)
            {
                pbs.Delete();
            }
            if (existing.Count > 0)
            {
                from.SendMessage($"GenerateBots: removed {existing.Count} existing spawner(s).");
            }
        }

        private static int LoadFromJson(Mobile from)
        {
            int placed = 0;
            try
            {
                var json = File.ReadAllText(JsonPath);
                var data = JsonSerializer.Deserialize<ExportBotSpawnersCommand.Wrapper>(json);
                if (data?.spawners == null)
                {
                    from.SendMessage("GenerateBots: JSON parsed but contains no spawners.");
                    return 0;
                }

                foreach (var rec in data.spawners)
                {
                    if (TryPlace(rec.map, rec.x, rec.y, rec.z, rec.behaviorName, rec.amount, rec.boundsRadius, from))
                    {
                        placed++;
                    }
                }
            }
            catch (Exception ex)
            {
                from.SendMessage($"GenerateBots: failed to parse JSON ({ex.Message}). No spawners placed.");
            }
            return placed;
        }

        private static int LoadFromFallback(Mobile from)
        {
            int placed = 0;
            foreach (var s in FallbackList)
            {
                if (TryPlace(s.MapName, s.X, s.Y, s.Z, s.Behavior, s.Amount, s.BoundsRadius, from))
                {
                    placed++;
                }
            }
            return placed;
        }

        // Common path used by both JSON and fallback. Returns true on
        // successful placement (false only on unknown map name).
        private static bool TryPlace(
            string mapName, int x, int y, int z,
            string behaviorName, int amount, int boundsRadius,
            Mobile from)
        {
            var map = Map.Parse(mapName);
            if (map == null || map == Map.Internal)
            {
                from.SendMessage($"GenerateBots: unknown map '{mapName}', skipping.");
                return false;
            }

            if (boundsRadius < 1) boundsRadius = 10;
            if (amount       < 1) amount       = 1;

            var loc = new Point3D(x, y, z);
            var bounds = new Rectangle3D(
                new Point3D(x - boundsRadius, y - boundsRadius, z - 5),
                new Point3D(x + boundsRadius, y + boundsRadius, z + 20)
            );

            var spawner = new PlayerBotSpawner(
                behaviorName: behaviorName,
                amount:       amount,
                minDelay:     TimeSpan.FromMinutes(5),
                maxDelay:     TimeSpan.FromMinutes(15)
            );
            spawner.SpawnBounds   = bounds;
            spawner.UseSpiralScan = true;
            spawner.MoveToWorld(loc, map);
            spawner.Respawn();
            return true;
        }
    }
}
