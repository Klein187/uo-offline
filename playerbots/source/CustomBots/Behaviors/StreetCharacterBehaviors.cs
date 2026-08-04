// =========================================================================
// StreetCharacterBehaviors.cs — the recognizable people of 1999 (IDEAS
// 1.5): the beggar and the newbie.
//
// Both are thin skins over the same base: stand around the bank, run
// their own chatter, and — the signature move — occasionally latch onto
// a REAL PLAYER and follow them for a bit. Nothing says "this is a live
// server" like a beggar trailing you across the plaza going "gold plz".
//
// Rolled as rare outcomes of the bank arrival handoff, so every bank
// develops its own street life organically. Both are timed visits: the
// character eventually wanders off and someone else drifts in.
// =========================================================================

using System;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public abstract class StreetCharacterBehavior : PlayerBotBehavior
    {
        // How long a player gets followed before the character gives up.
        private static readonly TimeSpan FollowDuration = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan FollowCooldown = TimeSpan.FromMinutes(4);
        private const int NoticeRange = 8;
        private const int FollowNear  = 2;

        private Mobile _followTarget;
        private DateTime _followUntil;
        private DateTime _nextFollowAllowed;
        private DateTime _nextIdleTurn;

        // The line said when latching onto a player, and when giving up.
        protected abstract string LatchCategory { get; }
        protected virtual string GiveUpEmote => "bah";

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            VisitExpiresAt ??= Core.Now +
                TimeSpan.FromMinutes(Utility.RandomMinMax(10, 25));
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal || bot.Deleted || !bot.Alive)
            {
                return;
            }

            if (CheckVisitExpired(bot))
            {
                return; // wandered off — behavior already swapped
            }

            TrySpeak(bot);

            // Following someone?
            if (_followTarget != null)
            {
                bool done = _followTarget.Deleted ||
                            _followTarget.Map != bot.Map ||
                            !bot.InRange(_followTarget.Location, NoticeRange + 8) ||
                            Core.Now >= _followUntil;
                if (done)
                {
                    BotScene.Deliver(bot, GiveUpEmote);
                    _followTarget = null;
                    _nextFollowAllowed = Core.Now + FollowCooldown;
                }
                else if (!bot.InRange(_followTarget.Location, FollowNear))
                {
                    var dir = bot.GetDirectionTo(_followTarget.Location);
                    bot.Direction = dir;
                    bot.Move(dir);
                    if (!bot.InRange(_followTarget.Location, FollowNear))
                    {
                        bot.Move(dir); // shuffle two steps a tick to keep pace
                    }
                }
                return;
            }

            // Latch onto a passing player?
            if (Core.Now >= _nextFollowAllowed && Utility.RandomDouble() < 0.15)
            {
                foreach (var m in bot.Map.GetMobilesInRange(bot.Location, NoticeRange))
                {
                    if (m is PlayerMobile && m is not PlayerBot &&
                        m.Alive && m.AccessLevel == AccessLevel.Player)
                    {
                        _followTarget = m;
                        _followUntil = Core.Now + FollowDuration;
                        var line = ChatLibrary.PickRandom(LatchCategory);
                        if (!string.IsNullOrEmpty(line))
                        {
                            bot.Say(line);
                        }
                        return;
                    }
                }
            }

            // Idle: face a random way now and then.
            if (Core.Now >= _nextIdleTurn)
            {
                _nextIdleTurn = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(8, 25));
                bot.Direction = (Direction)Utility.Random(8);
            }
        }
    }

    // The karma farmer at every bank. "gold plz."
    public class BeggarBehavior : StreetCharacterBehavior
    {
        public override string SerializableName => "Beggar";
        protected override string LatchCategory => "beg";

        public BeggarBehavior()
        {
            ChatCategories  = new[] { "beg" };
            ChatChance      = 0.20;
            MinChatCooldown = TimeSpan.FromSeconds(20);
            MaxChatCooldown = TimeSpan.FromSeconds(60);
        }
    }

    // Day-one citizen with day-one questions. "how do i get to minoc??"
    public class NewbieBehavior : StreetCharacterBehavior
    {
        public override string SerializableName => "Newbie";
        protected override string LatchCategory => "newbie";
        protected override string GiveUpEmote => "farewell";

        public NewbieBehavior()
        {
            ChatCategories  = new[] { "newbie" };
            ChatChance      = 0.18;
            MinChatCooldown = TimeSpan.FromSeconds(25);
            MaxChatCooldown = TimeSpan.FromSeconds(70);
        }
    }
}
