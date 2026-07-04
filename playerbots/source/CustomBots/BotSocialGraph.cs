// =========================================================================
// BotSocialGraph.cs — bots that know each other (IDEAS 1.4, light).
//
// A tiny per-pair memory: bots that hunted together in a party become
// FRIENDS. Friends (and guildmates) greet each other by first name on
// sight — "yo Corwin" — the cheapest, deepest "these are people" signal.
//
// Transient by design: friendships rebuild through play after a restart,
// exactly like the bots themselves. Keyed on serial pairs.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public static class BotSocialGraph
    {
        // Pairs that hunted together. Value = when the bond formed.
        private static readonly Dictionary<ulong, DateTime> _friends = new();

        // Last time a pair exchanged greetings — so two friends at the
        // same bank don't "yo" each other every 30 seconds.
        private static readonly Dictionary<ulong, DateTime> _lastGreeted = new();

        private static readonly TimeSpan GreetCooldown = TimeSpan.FromMinutes(30);

        // Runaway brake. Bots churn (sessions log them out), so the graph
        // is pruned wholesale if it ever balloons; friendships are cheap
        // to re-earn.
        private const int MaxEdges = 4000;

        private static ulong Key(Mobile a, Mobile b)
        {
            uint x = (uint)a.Serial.Value;
            uint y = (uint)b.Serial.Value;
            return x < y ? ((ulong)x << 32) | y : ((ulong)y << 32) | x;
        }

        public static void MakeFriends(PlayerBot a, PlayerBot b)
        {
            if (a == null || b == null || a == b)
            {
                return;
            }
            if (_friends.Count >= MaxEdges)
            {
                _friends.Clear();
                _lastGreeted.Clear();
            }
            _friends[Key(a, b)] = Core.Now;
        }

        public static bool AreFriends(PlayerBot a, PlayerBot b) =>
            a != null && b != null && a != b && _friends.ContainsKey(Key(a, b));

        public static bool CanGreet(PlayerBot a, PlayerBot b) =>
            !_lastGreeted.TryGetValue(Key(a, b), out var last) ||
            Core.Now - last >= GreetCooldown;

        public static void MarkGreeted(PlayerBot a, PlayerBot b)
        {
            _lastGreeted[Key(a, b)] = Core.Now;
        }
    }
}
