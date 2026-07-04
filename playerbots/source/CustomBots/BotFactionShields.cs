// =========================================================================
// BotFactionShields.cs — the visible allegiance markers (IDEAS 2.1 ph 3).
//
// Vanilla Order/Chaos shields validate the wearer's REAL guild (pre-AOS)
// and self-destruct otherwise — the era rule. Bots have no Server.Guilds
// guild (their guilds are the lightweight BotGuilds catalog), so these
// subclasses accept a PlayerBot wearer and defer to the vanilla rule for
// everyone else. Which means: a player who loots one off a bot corpse
// and tries to wear it without the right guild watches it crumble —
// exactly what happened in 1999.
// =========================================================================

using ModernUO.Serialization;
using Server;
using Server.Items;

namespace Server.CustomBots
{
    [SerializationGenerator(0)]
    public partial class BotOrderShield : OrderShield
    {
        [Constructible]
        public BotOrderShield()
        {
        }

        public override bool Validate(Mobile m) => m is PlayerBot || base.Validate(m);
    }

    [SerializationGenerator(0)]
    public partial class BotChaosShield : ChaosShield
    {
        [Constructible]
        public BotChaosShield()
        {
        }

        public override bool Validate(Mobile m) => m is PlayerBot || base.Validate(m);
    }
}
