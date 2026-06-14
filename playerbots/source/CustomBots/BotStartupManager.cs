// =========================================================================
// BotStartupManager.cs — rebuilds the bot population when the world loads.
//
// WHY THIS EXISTS
// ---------------
// PlayerBot extends PlayerMobile. ModernUO's world save persists player
// characters via their ACCOUNT, not as free-standing world mobiles — so
// accountless PlayerBots are NOT written to the world save and do NOT
// survive a restart. (This is why bots "vanished": the save genuinely
// never contained them.)
//
// The fix: treat bots as TRANSIENT. What persists is the PlayerBotSpawner
// items (those are Items, saved correctly in Items.bin). On every server
// startup, after the world has loaded, this manager's Initialize() hook:
//
//   1. PURGES any stale PlayerBots that somehow came through the load
//      (orphans on Map.Internal, half-states) so nothing accumulates.
//   2. RESPAWNS every PlayerBotSpawner so the live bot population is
//      rebuilt immediately — instead of waiting up to 15 minutes for each
//      spawner's natural timer tick.
//
// Net effect: bots are always present after a restart, the world feels
// populated, and there's no orphan accumulation in the save file.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public static class BotStartupManager
    {
        // ModernUO calls static Configure() methods BEFORE the world loads
        // and static Initialize() methods AFTER it has loaded. We need the
        // post-load hook — the spawners and any stale bots only exist once
        // the world is in memory — so the work goes in Initialize().
        //
        // (Configure() is intentionally empty/absent here; using it would
        // run before the world exists and find nothing.)
        private static readonly bool LegacyStartupEnabled = true;

        public static void Initialize()
        {
            int purged = PurgeStaleBots();

            // Stage Manager population has replaced the legacy physical-bot
            // world: no spawner respawn, no 43s field build, no HPA build.
            // Flip to true to bring the old system back.
            if (!LegacyStartupEnabled)
            {
                Console.WriteLine($"[BotStartupManager] purged {purged} stale bot(s); " +
                    "legacy startup skipped (Stage Manager active).");
                return;
            }

            // If the world has no spawners at all, lay down the default
            // set so it isn't empty. (No-op if spawners already exist.)
            GenerateBotsCommand.EnsurePopulation();

            // Force every spawner to fill its bot population now, rather
            // than waiting up to 15 minutes for its natural timer tick.
            int spawners = RespawnAllSpawners();

            // Build local approach fields for every destination, so the
            // Traveler can walk bots into destination tiles without the
            // waypoint-drift code. Reads DestinationCatalog (loaded in the
            // pre-load Configure() pass, before this Initialize()).
            var (fieldsBuilt, fieldTiles, fieldMs) = DestinationFieldCache.BuildAll();
            Console.WriteLine(
                $"[BotStartupManager] Built {fieldsBuilt} approach field(s), " +
                $"{fieldTiles:n0} tiles, in {fieldMs:0} ms.");
            // HPA load disabled — waypoint Traveler doesn't use it; loading the
            // 60k-node graph only cost RAM. Re-enable if HPA routing returns.
            // Console.WriteLine("[BotStartupManager] " + HpaGraph.EnsureBuilt(Map.Felucca));


        }

        // -------------------------------------------------------------------
        // Remove any PlayerBot still in the world at load time. Bots don't
        // persist by design; any that appear in a fresh load are stale
        // remnants and must be cleared so they don't accumulate.
        // -------------------------------------------------------------------
        private static int PurgeStaleBots()
        {
            var stale = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted)
                    stale.Add(bot);
            }
            foreach (var bot in stale)
            {
                try { bot.Delete(); } catch { }
            }
            return stale.Count;
        }

        // -------------------------------------------------------------------
        // Find every PlayerBotSpawner and force an immediate Respawn() so
        // the bot population is rebuilt now, not on the spawner's next
        // natural timer tick (which can be up to 15 minutes away).
        //
        // Stops once the live bot count reaches BotPopulation.StartupCap —
        // a runaway brake that sits ABOVE the configured target, so it
        // never limits a legitimate population, only catches a genuine
        // spawner-pile runaway.
        // -------------------------------------------------------------------
        private static int RespawnAllSpawners()
        {
            var spawners = new List<PlayerBotSpawner>();
            foreach (var item in World.Items.Values)
            {
                if (item is PlayerBotSpawner sp && !sp.Deleted)
                    spawners.Add(sp);
            }

            int cap = BotPopulation.StartupCap;
            int respawned = 0;
            foreach (var sp in spawners)
            {
                if (CountBots() >= cap)
                {
                    Console.WriteLine(
                        $"[BotStartupManager] runaway brake hit ({cap} bots) — " +
                        $"skipping {spawners.Count - respawned} remaining spawner(s). " +
                        $"You likely have stacked spawners; use [ClearSpawners then " +
                        $"[GenerateBots for a clean set.");
                    break;
                }
                try
                {
                    sp.Respawn();
                    respawned++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[BotStartupManager] respawn failed for spawner " +
                        $"{sp.Serial}: {ex.Message}");
                }
            }
            return respawned;
        }

        // Count live PlayerBots in the world.
        private static int CountBots()
        {
            int n = 0;
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted)
                    n++;
            }
            return n;
        }
    }
}
