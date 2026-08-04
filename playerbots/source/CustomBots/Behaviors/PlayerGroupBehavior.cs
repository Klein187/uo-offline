// =========================================================================
// PlayerGroupBehavior.cs — a bot adventuring in a REAL PLAYER's party.
//
// The player forms the group with the normal party gump (or context
// menu): target a bot, it accepts the invite (BotPlayerParty), and this
// behavior takes over. It subclasses AdventurerBehavior so ALL the
// working combat comes free — the group layer only adds:
//
//   - FOLLOW: the patrol goal is a slot beside the party leader,
//     re-picked every pass while the leader moves; the bot runs to
//     catch up when it falls behind, and mills around naturally when
//     the group is standing at the bank deciding where to go.
//   - ASSIST: the leader's combatant becomes the bot's, and a monster
//     attacking any party member counts as "attacking a friend" so the
//     whole group turns on it.
//   - LEAVING: removed from the party (or the party dissolves, or the
//     leader logs out) → say goodbye and go back to normal life. Left
//     hopelessly behind for a while → "im lost, heading back", leave,
//     and travel home. No teleports — a lost group member in 1999
//     walked home too.
//
// Death is the era's death: a member that dies ghosts off to a healer
// through the normal death system and does NOT auto-rejoin — the
// player re-invites after the res, like everyone did.
// =========================================================================

using System;
using Server;
using Server.Engines.PartySystem;
using Server.Mobiles;

namespace Server.CustomBots
{
    public class PlayerGroupBehavior : AdventurerBehavior
    {
        public override string SerializableName => "PlayerGroup";

        // Follow tuning.
        private const int FollowSlotRadius = 2;   // stand this loose ring
        private const int CloseEnough = 3;        // inside this: relax
        private const int RunBeyond = 6;          // farther: run to catch up
        private const int LostBeyond = 60;        // farther: the lost clock runs
        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(90);

        private DateTime _lostSince = DateTime.MinValue;

        public override string GetStatusLine(PlayerBot bot)
        {
            var leader = LeaderOf(bot);
            if (bot.Combatant is Mobile foe && !foe.Deleted && foe.Alive)
            {
                return $"fighting {foe.Name} for {leader?.Name ?? "the party"}";
            }
            return leader != null
                ? $"adventuring with {leader.Name}"
                : "adventuring with a party";
        }

        // The real-player leader of the bot's party, or null when the
        // party is gone / not player-led.
        private static Mobile LeaderOf(PlayerBot bot)
        {
            if (bot.Party is not Party p)
            {
                return null;
            }
            var leader = p.Leader;
            if (leader == null || leader.Deleted || leader == bot ||
                !leader.Player || leader is PlayerBot)
            {
                return null;
            }
            return leader;
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            var leader = LeaderOf(bot);

            // Party over: removed, disbanded, or the leader left the world
            // (a logged-out leader lingers as a body for a bit — followers
            // stand by it, era-style, and give up when it fades).
            if (leader == null || leader.Map == null || leader.Map == Map.Internal)
            {
                LeaveGroup(bot, sayGoodbye: true);
                return;
            }

            // Hopelessly behind (other map counts immediately as lost).
            int dist = bot.Map == leader.Map
                ? Dist(bot.Location, leader.Location)
                : int.MaxValue;
            if (dist > LostBeyond)
            {
                if (_lostSince == DateTime.MinValue)
                {
                    _lostSince = Core.Now;
                }
                else if (Core.Now - _lostSince > GiveUpAfter)
                {
                    bot.Say("im lost, heading back");
                    LeaveGroup(bot, sayGoodbye: false);
                    return;
                }
            }
            else
            {
                _lostSince = DateTime.MinValue;
            }

            // Assist: pick up the leader's fight the moment it starts.
            if (bot.Alive && bot.Combatant == null &&
                leader.Combatant is BaseCreature bc &&
                !bc.Deleted && bc.Alive && bc.Map == bot.Map &&
                bot.InRange(bc.Location, 12))
            {
                bot.Combatant = bc;
            }

            base.Tick(bot);
        }

        // A monster fighting ANY member of my party is attacking a friend.
        protected override bool IsPartyFriend(PlayerBot bot, Mobile m) =>
            m != null && Party.Get(bot) is Party mine && Party.Get(m) == mine;

        // Follow slot: a stable per-bot spot on a small ring around the
        // leader, so a full party fans out instead of stacking on one tile.
        protected override Point3D? SelectPatrolGoal(PlayerBot bot)
        {
            var leader = LeaderOf(bot);
            if (leader == null || leader.Map != bot.Map)
            {
                return null;
            }

            int dist = Dist(bot.Location, leader.Location);
            if (dist <= CloseEnough)
            {
                return null; // near enough — idle mill beside the group
            }

            int seed = bot.Serial.ToInt32();
            int ox = seed % (FollowSlotRadius * 2 + 1) - FollowSlotRadius;
            int oy = seed / 7 % (FollowSlotRadius * 2 + 1) - FollowSlotRadius;
            return new Point3D(leader.X + ox, leader.Y + oy, leader.Z);
        }

        // The leader moves — a follow goal more than a couple of tiles
        // from the leader is stale; re-pick every pass.
        protected override bool PatrolGoalStale(PlayerBot bot, Point3D goal)
        {
            var leader = LeaderOf(bot);
            return leader != null && bot.Map == leader.Map &&
                   Dist(goal, leader.Location) > FollowSlotRadius + 2;
        }

        // Catch up at a run — a 1999 party member held the run key like
        // everyone else.
        protected override bool PatrolRuns => true;

        private void LeaveGroup(PlayerBot bot, bool sayGoodbye)
        {
            if (bot.Party is Party p)
            {
                p.Remove(bot);
            }
            bot.Party = null;

            if (sayGoodbye && Utility.RandomDouble() < 0.6)
            {
                var line = ChatLibrary.PickRandom("party_disband");
                if (!string.IsNullOrEmpty(line))
                {
                    bot.Say(line);
                }
            }

            bot.Behavior = new TravelerBehavior();
        }

        private static int Dist(Point3D a, Point3D b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);
            return dx > dy ? dx : dy;
        }
    }
}
