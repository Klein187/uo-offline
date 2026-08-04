// =========================================================================
// BankFixtures.cs — the permanent bank crowd, at EVERY bank.
//
// A FixedRoleBotSpawner's bots are LifecycleExempt: the lifecycle manager
// never reassigns them, the session curve never logs them out, parties and
// faction fights never draft them — they stand at the bank forever, being
// the bank crowd. Individuals still die and get replaced by the spawner,
// but the CROWD never changes what it's doing.
//
// This ensure pass makes that true for every bank, not just the ones a
// spawner was hand-placed at: on startup, every Bank-type destination
// without a BankSitter fixture spawner nearby gets one, spawned full
// immediately. The spawner ITEM persists in the world save (bots are
// transient and rebuilt each boot), so this runs as a no-op forever after
// — and any bank authored later gets its crowd on the next boot for free.
// Hand-placed fixture spawners are respected (the nearby check finds
// them) and never duplicated.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public static class BankFixtures
    {
        public static bool Enabled = true;

        // Permanent sitters per bank. Three read as "regulars", but with
        // the role variety (AFK statues, hidden stealth macroers) a crowd
        // of three can roll all-silent — five keeps every bank audibly
        // alive while the macroers do their thing in the corners. The
        // mostly-stationary extras don't wall off the counters.
        public const int SittersPerBank = 5;

        // A fixture spawner within this range of the bank's spot counts as
        // "this bank already has its crowd" (covers hand-placed ones that
        // aren't exactly on the destination tile).
        private const int ExistingSearchRange = 20;

        // How far the spawner scatters its sitters. BankSitter's own
        // scattered-home pick spreads them further across the building.
        private const int SpawnSpread = 5;

        // Replacement cadence when a fixture dies (guard-zone deaths are
        // rare — this mostly never fires).
        private static readonly TimeSpan RespawnMin = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan RespawnMax = TimeSpan.FromMinutes(5);

        public static void Initialize()
        {
            // Post-world-load, and late enough that the destination
            // catalog and the startup respawn have both settled.
            Timer.DelayCall(TimeSpan.FromSeconds(12), EnsureAll);
        }

        private static void EnsureAll()
        {
            if (!Enabled)
            {
                return;
            }

            var map = Map.Felucca;
            int placed = 0;

            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.Bank)
                {
                    continue;
                }

                var spot = d.ArrivalPoint ?? d.Location;

                bool exists = false;
                foreach (var s in map.GetItemsInRange<FixedRoleBotSpawner>(spot, ExistingSearchRange))
                {
                    if (!s.Deleted &&
                        string.Equals(s.BehaviorName, "BankSitter",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        // The spawner ITEM persists across boots with its
                        // old count baked in — top it up when the target
                        // grows so every bank gets the full varied crowd.
                        if (s.Count < SittersPerBank)
                        {
                            s.Count = SittersPerBank;
                            s.Respawn();
                            Console.WriteLine(
                                $"[fixtures] bank crowd at '{d.Name}' topped up to {SittersPerBank}");
                        }
                        break;
                    }
                }
                if (exists)
                {
                    continue;
                }

                var spawner = new FixedRoleBotSpawner(
                    "BankSitter", SittersPerBank, RespawnMin, RespawnMax)
                {
                    HomeRange = SpawnSpread,
                };
                spawner.MoveToWorld(spot, map);
                spawner.Respawn();
                placed++;
                Console.WriteLine(
                    $"[fixtures] permanent bank crowd placed at '{d.Name}' " +
                    $"({spot.X},{spot.Y}) — {SittersPerBank} sitters");
            }

            if (placed > 0)
            {
                Console.WriteLine(
                    $"[fixtures] {placed} bank(s) received a permanent crowd " +
                    $"({placed * SittersPerBank} fixture sitters).");
            }
        }
    }
}
