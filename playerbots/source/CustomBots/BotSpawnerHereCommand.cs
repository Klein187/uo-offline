// =========================================================================
// BotSpawnerHereCommand.cs — [BotSpawnerHere admin command.
//
// Drops a PlayerBotSpawner at your current location, configured with the
// behavior and count you specify. Bounds extend ±10 tiles around the
// spawner so bots disperse naturally instead of stacking.
//
// Use:
//   [BotSpawnerHere               - default: BankSitter, 8 bots
//   [BotSpawnerHere wander 12     - 12 wanderers
//   [BotSpawnerHere banksitter 15 - 15 bank sitters
//
// Workflow for building Britannia's bot population from scratch:
//   1. Walk to a bank's player-gathering spot
//   2. Run [BotSpawnerHere banksitter <count>
//   3. (optional) Add some travelers: [BotSpawnerHere wander 3
//   4. Walk to next city, repeat
//   5. When done, [ExportBotSpawners writes the placements to JSON
//      for distribution via GitHub.
// =========================================================================

using System;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class BotSpawnerHereCommand
    {
        // How far ± from the spawner tile bots can appear. ±10 gives a
        // 21x21 box, plenty of room for a 15-bot crowd to disperse without
        // stacking on top of each other.
        private const int DefaultBoundsRadius = 10;

        // Default population if the user doesn't specify a count.
        private const int DefaultCount = 8;

        public static void Configure()
        {
            CommandSystem.Register("BotSpawnerHere", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("BotSpawnerHere [behavior] [count]")]
        [Description(
            "Places a PlayerBotSpawner at your location. " +
            "Behavior defaults to 'BankSitter', count defaults to 8."
        )]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null || from.Map == null || from.Map == Map.Internal)
            {
                return;
            }

            string behaviorName = "BankSitter";
            int    count        = DefaultCount;

            if (e.Arguments.Length >= 1)
            {
                behaviorName = e.Arguments[0];
            }
            if (e.Arguments.Length >= 2)
            {
                if (!int.TryParse(e.Arguments[1], out count) || count < 1)
                {
                    from.SendMessage("Count must be a positive integer.");
                    return;
                }
            }

            // Verify the behavior is one we know about. Avoids silently
            // creating spawners that produce IdleBehavior bots because of
            // a typo.
            var probe = BehaviorRegistry.Create(behaviorName);
            if (!string.Equals(probe.SerializableName, behaviorName, StringComparison.OrdinalIgnoreCase))
            {
                from.SendMessage($"Unknown behavior '{behaviorName}'. Known: " +
                                  string.Join(", ", BehaviorRegistry.KnownNames));
                return;
            }

            // Spawn bounds rectangle centered on the admin's tile.
            var bounds = new Rectangle3D(
                new Point3D(from.X - DefaultBoundsRadius, from.Y - DefaultBoundsRadius, from.Z - 5),
                new Point3D(from.X + DefaultBoundsRadius, from.Y + DefaultBoundsRadius, from.Z + 20)
            );

            var spawner = new PlayerBotSpawner(
                behaviorName: probe.SerializableName,  // canonical casing
                amount:       count,
                minDelay:     TimeSpan.FromMinutes(5),
                maxDelay:     TimeSpan.FromMinutes(15)
            );
            spawner.SpawnBounds   = bounds;
            spawner.UseSpiralScan = true;
            spawner.MoveToWorld(from.Location, from.Map);
            spawner.Respawn();

            from.SendMessage(
                $"Placed PlayerBotSpawner: {probe.SerializableName} × {count} " +
                $"at ({from.X},{from.Y},{from.Z}) bounds ±{DefaultBoundsRadius}."
            );
        }
    }
}
