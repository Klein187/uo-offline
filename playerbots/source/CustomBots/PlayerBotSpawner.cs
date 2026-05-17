// =========================================================================
// PlayerBotSpawner.cs — Spawner subclass that creates PlayerBots and
// configures their behavior.
//
// Inherits from Spawner (not BaseSpawner directly) because Spawner is the
// concrete class that implements the abstract SpawnBounds property. Behavior
// assignment happens via PlayerBot.OnAfterSpawn() reading this.BehaviorName.
//
// Serialization uses ModernUO's source generator. The "partial" keyword is
// required for the generator to emit Serialize/Deserialize methods.
// =========================================================================

using System;
using ModernUO.Serialization;
using Server;
using Server.Engines.Spawners;

namespace Server.CustomBots
{
    [SerializationGenerator(0)]
    public partial class PlayerBotSpawner : Spawner
    {
        // Which behavior the bots this spawner creates should have.
        // Stored as a string (looked up via BehaviorRegistry at spawn
        // time) so renaming a behavior class doesn't break old saves.
        [SerializableField(0)]
        [SerializedCommandProperty(AccessLevel.GameMaster)]
        private string _behaviorName = "Idle";

        // ---------------- Constructors ----------------

        // Default constructor required by the source generator and the
        // [add command. Sets PlayerBot as the spawned type.
        [Constructible(AccessLevel.GameMaster)]
        public PlayerBotSpawner() : base("PlayerBot")
        {
            Name = "PlayerBot Spawner";
        }

        // Convenience constructor used by [GenerateBots.
        [Constructible(AccessLevel.GameMaster)]
        public PlayerBotSpawner(
            string behaviorName,
            int amount,
            TimeSpan minDelay,
            TimeSpan maxDelay
        ) : base(
            amount,
            minDelay,
            maxDelay,
            team: 0,
            spawnBounds: default,
            spawnedNames: "PlayerBot"
        )
        {
            _behaviorName = behaviorName ?? "Idle";
            Name = $"PlayerBot Spawner ({_behaviorName})";
        }
    }
}
