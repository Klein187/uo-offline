// =========================================================================
// ClearSpawnersCommand.cs — [ClearSpawners
//
// Deletes EVERY PlayerBotSpawner in the world (and the bots they've
// spawned). Use this to clean up an accumulated pile of spawners — e.g.
// when the panel "spawn" button or [BotSpawnerHere has been run many
// times and stacked dozens of overlapping spawners.
//
// After clearing, place a fresh, controlled set with [GenerateBots (which
// clears old spawners first) or a few deliberate [BotSpawnerHere calls.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class ClearSpawnersCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("ClearSpawners", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("ClearSpawners")]
        [Description("Deletes every PlayerBotSpawner (and its bots) in the world.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null) return;

            // Collect spawners first (don't mutate the collection mid-loop).
            var spawners = new List<PlayerBotSpawner>();
            foreach (var item in World.Items.Values)
            {
                if (item is PlayerBotSpawner sp && !sp.Deleted)
                    spawners.Add(sp);
            }

            // Collect bots too — delete them so the world clears immediately
            // rather than leaving orphans until the next purge.
            var bots = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted)
                    bots.Add(bot);
            }

            foreach (var sp in spawners)
            {
                try { sp.Delete(); } catch { }
            }
            foreach (var bot in bots)
            {
                try { bot.Delete(); } catch { }
            }

            from.SendMessage(0x35,
                $"Cleared {spawners.Count} spawner(s) and {bots.Count} bot(s).");
            from.SendMessage(0x3B2,
                "Use [GenerateBots to place a fresh, controlled set.");
            Console.WriteLine(
                $"[ClearSpawners] {from.Name}: removed {spawners.Count} spawners, " +
                $"{bots.Count} bots.");
        }
    }
}
