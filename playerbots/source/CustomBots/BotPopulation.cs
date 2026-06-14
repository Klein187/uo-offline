// =========================================================================
// BotPopulation.cs — central configuration for the world bot population.
//
// ONE place to set how many bots populate the world. [GenerateBots and the
// startup manager both read TargetCount from here, so changing the world
// population is a single-number edit (or use [SetBotPopulation in-game to
// change it live without a rebuild).
// =========================================================================

namespace Server.CustomBots
{
    public static class BotPopulation
    {
        // The world's target live bot count. [GenerateBots distributes this
        // many bots across all city spawners. Default 1000.
        //
        // Runtime-adjustable via [SetBotPopulation <n>.
        public static int TargetCount { get; set; } = 400;

        // Safety ceiling for the startup respawn. Sits ABOVE TargetCount so
        // it never caps a legitimate population — it only catches a genuine
        // runaway (e.g. a corrupted pile of spawners). Recomputed from
        // TargetCount so it always stays comfortably above it.
        public static int StartupCap => TargetCount + 300;

        // Per-spawner bot count. A city's allotment is split into spawners
        // of roughly this size — many small spawners with room to place
        // beat one giant spawner that can't fit its bots (which triggers
        // ModernUO's "no valid spawn positions" abandonment).
        public const int BotsPerSpawner = 12;

        // Bounds radius for each spawner — how far from the spawner tile
        // bots may be placed. Wider bounds = more valid tiles = fewer
        // failed placements when the population is dense.
        public const int SpawnerBoundsRadius = 18;
    }
}
