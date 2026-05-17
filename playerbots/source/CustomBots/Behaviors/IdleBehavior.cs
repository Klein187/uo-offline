// =========================================================================
// IdleBehavior.cs — Stands still, occasionally mutters small talk.
//
// The default behavior for new bots. Doesn't move, doesn't fight, but
// does occasionally say something so they don't feel like statues.
// =========================================================================

namespace Server.CustomBots
{
    public class IdleBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Idle";

        public IdleBehavior()
        {
            // Idle bots only do small talk — they're not selling anything
            // or asking for groups; they're just there.
            ChatCategories = new[] { "small_talk" };
            ChatChance     = 0.10;
        }

        public override void Tick(PlayerBot bot)
        {
            TrySpeak(bot);
        }
    }
}
