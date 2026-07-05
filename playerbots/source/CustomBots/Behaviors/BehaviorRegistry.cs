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
            Register("DungeonCrawler", () => new DungeonCrawlerBehavior());
            Register("Traveler",   () => new TravelerBehavior());
            Register("Shopper",    () => new ShopperBehavior());
            Register("Crafter",    () => new CrafterBehavior());
            Register("PK",         () => new PKBehavior());
            // Parties are transient — a "PartyMember" loaded from a save has
            // no party anymore. The behavior self-heals to Traveler on its
            // first tick, so constructing it directly is safe.
            Register("PartyMember", () => new PartyMemberBehavior());
            // Death-flow behaviors. A Ghost handles both states (dead →
            // haunt; alive → corpse run). "CorpseReclaim" from a stale save
            // maps to Traveler — the corpse it knew is long gone.
            Register("Ghost",         () => new GhostBehavior());
            Register("CorpseReclaim", () => new TravelerBehavior());
            // Street characters + gatherers + duelists. A Duelist loaded
            // from a save has no duel (they're transient) — its Tick
            // self-heals to BankSitter.
            Register("Gatherer", () => new GathererBehavior());
            Register("Beggar",   () => new BeggarBehavior());
            Register("Newbie",   () => new NewbieBehavior());
            Register("Duelist",  () => new DuelistBehavior());
            // A TreasureHunter loaded from a save has no dig scene — its
            // OnAttached restarts the dig where it stands, which is fine.
            Register("TreasureHunter", () => new TreasureHunterBehavior());
            // A working Tamer self-heals to Traveler when its quarry is gone.
            Register("Tamer", () => new TamerBehavior());
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
