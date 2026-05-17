// =========================================================================
// BehaviorRegistry.cs — Maps SerializableName -> behavior class.
//
// On world load, each bot has a saved string like "Idle" or "Wander".
// This registry knows how to construct a fresh PlayerBotBehavior given
// that string. New behaviors register themselves at startup via Configure().
// =========================================================================

using System;
using System.Collections.Generic;

namespace Server.CustomBots
{
    public static class BehaviorRegistry
    {
        // SerializableName -> factory function. Case-insensitive lookup.
        private static readonly Dictionary<string, Func<PlayerBotBehavior>> _factories =
            new(StringComparer.OrdinalIgnoreCase);

        public static void Configure()
        {
            // Built-in behaviors. New behaviors should add a line here in
            // their own Configure() method, OR register from this list if
            // we'd rather keep registration centralized.
            Register("Idle",       () => new IdleBehavior());
            Register("Wander",     () => new WanderBehavior());
            Register("BankSitter", () => new BankSitterBehavior());
            Register("Adventurer", () => new AdventurerBehavior());
            Register("Traveler",   () => new TravelerBehavior());
        }

        public static void Register(string name, Func<PlayerBotBehavior> factory)
        {
            _factories[name] = factory;
        }

        public static PlayerBotBehavior Create(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return new IdleBehavior();
            }

            if (_factories.TryGetValue(name, out var factory))
            {
                return factory();
            }

            // Unknown behavior name (e.g. removed in a later version).
            // Fall back to Idle rather than crashing the world load.
            Console.WriteLine($"BehaviorRegistry: Unknown behavior '{name}', falling back to Idle.");
            return new IdleBehavior();
        }

        public static IEnumerable<string> KnownNames => _factories.Keys;
    }
}
