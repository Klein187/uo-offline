// =========================================================================
// FixedRoleBotSpawner.cs — a PlayerBotSpawner whose bots NEVER transition.
//
// Identical to PlayerBotSpawner except for its type: PlayerBot.OnAfterSpawn
// checks `Spawner is FixedRoleBotSpawner` and sets LifecycleExempt = true on
// the bots it places, so BotLifecycleManager skips them. Used by the spawn
// editor's "PlayerBot — fixed role" kind: a permanent BankSitter, a forever
// Crafter at this forge, etc.
//
// No new serialized fields, so it's a fresh [SerializationGenerator(0)] with
// no migration — the role-locking is purely a function of the spawner's TYPE,
// not stored state.
// =========================================================================

using System;
using ModernUO.Serialization;

namespace Server.CustomBots
{
    [SerializationGenerator(0)]
    public partial class FixedRoleBotSpawner : PlayerBotSpawner
    {
        // Default ctor required by the source generator and the [add command.
        [Constructible(AccessLevel.GameMaster)]
        public FixedRoleBotSpawner() : base()
        {
            Name = "FixedRole Bot Spawner";
        }

        // Convenience ctor used by [GenerateCustomSpawners.
        [Constructible(AccessLevel.GameMaster)]
        public FixedRoleBotSpawner(
            string behaviorName,
            int amount,
            TimeSpan minDelay,
            TimeSpan maxDelay
        ) : base(behaviorName, amount, minDelay, maxDelay)
        {
            Name = $"FixedRole Bot Spawner ({behaviorName})";
        }
    }
}
