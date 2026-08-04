// =========================================================================
// BotPlayerParty.cs — bots accept a real player's party invite.
//
// The player uses the NORMAL party UI: open the party gump (or the
// context menu on the bot), add member, target the bot. The engine's
// Party.Invite marks the bot as a candidate (bot.Party = the inviting
// Mobile) and starts the 30s decline timer; since a bot has no client
// to click "accept" with, the behavior tick spots the pending invite
// and answers like a person would:
//
//   ACCEPT (after a short "reading the popup" delay) when the bot is a
//   combat class doing something interruptible — it says a party_join
//   line ("im in") and becomes a PlayerGroupBehavior follower.
//
//   DECLINE when it's busy being something else: crafters and gatherers
//   are working, dungeon crawlers are mid-run, outlaws don't do parties,
//   and the bank fixtures hold their post ("cant, waiting on someone").
//
// Only REAL players' invites are answered here — bot-to-bot parties
// run on their own system (BotPartyManager) and never touch this path.
// =========================================================================

using System;
using Server;
using Server.Engines.PartySystem;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotPlayerParty
    {
        // True when the bot is a MEMBER of a real player's party — used
        // to shield it from the session curve and lifecycle reassignment
        // for as long as the adventure runs.
        public static bool InPlayerParty(PlayerBot bot) =>
            bot?.Party is Party p && p.Leader != null &&
            p.Leader.Player && p.Leader is not PlayerBot;

        // Called every behavior tick, before the behavior runs. Cheap:
        // pending invites park a Mobile (not a Party) in bot.Party.
        public static void CheckInvite(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Party is not Mobile inviter)
            {
                return;
            }

            // Only a real player's invite gets an answer. Anything else
            // pending (shouldn't happen) is declined so it can't linger.
            if (!inviter.Player || inviter is PlayerBot || inviter.Deleted)
            {
                Decline(bot, inviter, say: false);
                return;
            }

            if (!WouldJoin(bot, out var declineLine))
            {
                Timer.DelayCall(TimeSpan.FromSeconds(Utility.RandomMinMax(1, 2)), () =>
                {
                    if (!bot.Deleted && bot.Party == inviter)
                    {
                        if (!string.IsNullOrEmpty(declineLine))
                        {
                            bot.Say(declineLine);
                        }
                        Decline(bot, inviter, say: false);
                    }
                });
                return;
            }

            // A person takes a beat to read the popup and click.
            Timer.DelayCall(
                TimeSpan.FromSeconds(1.0 + Utility.RandomDouble() * 1.5), () =>
                {
                    if (bot.Deleted || !bot.Alive || bot.Party != inviter)
                    {
                        return; // invite expired / withdrawn meanwhile
                    }
                    if (inviter.Party is not Party p || !p.Candidates.Contains(bot))
                    {
                        bot.Party = null;
                        return;
                    }

                    p.OnAccept(bot);

                    var line = ChatLibrary.PickRandom("party_join");
                    if (!string.IsNullOrEmpty(line))
                    {
                        bot.Say(line);
                    }
                    Console.WriteLine(
                        $"[party] {bot.Name} joined {inviter.Name}'s party");

                    bot.Behavior = new PlayerGroupBehavior();
                });
        }

        // Is this bot in a joinable state, and if not, what does it say?
        private static bool WouldJoin(PlayerBot bot, out string declineLine)
        {
            declineLine = null;

            if (!bot.Alive)
            {
                return false; // a ghost can't click accept
            }

            // The permanent fixtures (bank crowd, station crafters) hold
            // their post — that's the whole point of them.
            if (bot.LifecycleExempt)
            {
                declineLine = "cant, waiting on someone here";
                return false;
            }

            // Working classes are working.
            if (BotClassHelper.IsArtisan(bot.Class) ||
                BotClassHelper.IsGatherer(bot.Class) ||
                bot.Class == BotClass.Crafter)
            {
                declineLine = "cant, im working";
                return false;
            }

            // Busy behaviors decline; the interruptible crowd accepts.
            switch (bot.Behavior)
            {
                case PlayerGroupBehavior:            // already in one
                case PartyMemberBehavior:            // already in a bot party
                case PKBehavior:                     // outlaws don't do parties
                case DungeonCrawlerBehavior:         // mid-run, underground
                case GathererBehavior:
                case CrafterBehavior:
                case CorpseReclaimBehavior:
                case GhostBehavior:
                    declineLine = "cant right now";
                    return false;
            }

            if (BotPartyManager.IsInParty(bot))
            {
                declineLine = "with a group already";
                return false;
            }

            return true;
        }

        private static void Decline(PlayerBot bot, Mobile inviter, bool say)
        {
            if (inviter?.Party is Party p)
            {
                p.OnDecline(bot, inviter);
            }
            if (bot.Party == inviter)
            {
                bot.Party = null;
            }
        }
    }
}
