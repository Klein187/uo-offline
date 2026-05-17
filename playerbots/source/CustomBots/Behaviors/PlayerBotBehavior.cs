// =========================================================================
// PlayerBotBehavior.cs — Abstract base class for bot behaviors.
//
// v2 changes:
//   - Random first-letter capitalization for organic feel
//   - (Per-bot color is handled automatically: Mobile.SpeechHue is set on
//     the bot at construction and Say() picks it up by default.)
// =========================================================================

using System;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public abstract class PlayerBotBehavior
    {
        public abstract string SerializableName { get; }

        // Chat config — override in subclasses.
        public virtual string[] ChatCategories { get; protected set; } = Array.Empty<string>();
        public virtual double ChatChance        { get; protected set; } = 0.15;
        public virtual TimeSpan MinChatCooldown { get; protected set; } = TimeSpan.FromSeconds(30);
        public virtual TimeSpan MaxChatCooldown { get; protected set; } = TimeSpan.FromSeconds(90);
        public virtual int ChatHearRange        { get; protected set; } = 22;

        // Probability that a spoken line gets its first letter capitalized.
        // Mimics how some real players naturally capitalize sentences while
        // others don't. ~35% feels right — most lowercase (1998 UO style),
        // but enough capitals to vary the feed.
        protected virtual double CapitalizeChance => 0.35;

        private DateTime _nextChatAllowed = DateTime.MinValue;

        // ---- Lifecycle ----

        public virtual void Tick(PlayerBot bot) { }

        public virtual void OnAttached(PlayerBot bot)
        {
            _nextChatAllowed = Core.Now + RandomCooldown();
        }

        public virtual void OnDetached(PlayerBot bot) { }

        // -------------------------------------------------------------------
        // TrySpeak — gate-check, then potentially Say a random line.
        // -------------------------------------------------------------------
        protected bool TrySpeak(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return false;
            }

            if (Core.Now < _nextChatAllowed)
            {
                return false;
            }

            if (Utility.RandomDouble() > ChatChance)
            {
                return false;
            }

            if (!IsPlayerNearby(bot))
            {
                return false;
            }

            var cats = ChatCategories;
            if (cats == null || cats.Length == 0)
            {
                return false;
            }

            var line = ChatLibrary.PickRandom(cats);
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }

            // Probabilistic capitalization — sometimes "hail", sometimes "Hail".
            // Don't touch lines that start with non-letters (e.g. "*sigh*",
            // "WTS GM hally"). Those should keep their case as written.
            if (Utility.RandomDouble() < CapitalizeChance
                && line.Length > 0
                && char.IsLower(line[0]))
            {
                line = char.ToUpper(line[0]) + line.Substring(1);
            }

            // Mobile.Say uses bot.SpeechHue automatically. The hue was set
            // on the bot when it was constructed and persists across saves.
            bot.Say(line);

            _nextChatAllowed = Core.Now + RandomCooldown();
            return true;
        }

        private bool IsPlayerNearby(PlayerBot bot)
        {
            var range = ChatHearRange;
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, range))
            {
                if (m is PlayerMobile && m is not PlayerBot)
                {
                    return true;
                }
            }
            return false;
        }

        private TimeSpan RandomCooldown()
        {
            var minMs = MinChatCooldown.TotalMilliseconds;
            var maxMs = MaxChatCooldown.TotalMilliseconds;
            var pick  = minMs + Utility.RandomDouble() * (maxMs - minMs);
            return TimeSpan.FromMilliseconds(pick);
        }
    }
}
