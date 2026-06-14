// =========================================================================
// SetBotPopulationCommand.cs — [SetBotPopulation <n>
//
// Changes the world's target bot count live, then regenerates the
// spawners to match — no server rebuild needed. Lets you dial the
// population in and find the number the server runs smoothly at.
//
//   [SetBotPopulation 1000   — set target to 1000 and regenerate
//   [SetBotPopulation        — show the current target
// =========================================================================

using System;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class SetBotPopulationCommand
    {
        // Sane bounds — a typo shouldn't spawn a million bots or zero.
        private const int MinPopulation = 0;
        private const int MaxPopulation = 5000;

        public static void Configure()
        {
            CommandSystem.Register(
                "SetBotPopulation", AccessLevel.Administrator, OnCommand);
        }

        [Usage("SetBotPopulation [count]")]
        [Description("Set the world bot population target and regenerate spawners.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null) return;

            if (e.Length < 1)
            {
                from.SendMessage(
                    $"Current bot population target: {BotPopulation.TargetCount}");
                from.SendMessage("Usage: [SetBotPopulation <count>");
                return;
            }

            int n = e.GetInt32(0);
            if (n < MinPopulation || n > MaxPopulation)
            {
                from.SendMessage(
                    $"Population must be between {MinPopulation} and {MaxPopulation}.");
                return;
            }

            int old = BotPopulation.TargetCount;
            BotPopulation.TargetCount = n;

            from.SendMessage(0x35,
                $"Bot population target: {old} -> {n}. Regenerating spawners...");

            // Regenerate the spawner set to the new target. GenerateBots
            // clears the old spawners first, so this fully replaces them.
            int placed = GenerateBotsCommand.RegenerateForPopulation();

            from.SendMessage(0x35,
                $"Placed {placed} spawners for ~{n} bots. " +
                $"They populate over the next few minutes.");
            Console.WriteLine(
                $"[SetBotPopulation] {from.Name}: {old} -> {n} " +
                $"({placed} spawners).");
        }
    }
}
