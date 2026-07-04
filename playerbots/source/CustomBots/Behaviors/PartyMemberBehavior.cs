// =========================================================================
// PartyMemberBehavior.cs — a bot following its hunting-party leader
// (IDEAS 2.2).
//
// Subclasses AdventurerBehavior for the same reason DungeonCrawler does:
// ALL the combat, flee, and stuck-recovery machinery comes free. The only
// override is goal selection — instead of patrolling wilderness or
// dungeon rooms, the member's "patrol goal" is always a spot beside the
// party leader. The result is the pure MMO image: a line of players
// walking down a road together, fanning out when a fight starts (combat
// preempts patrol in the base class), and re-forming after.
//
// During the party's ENTERING phase a member on the surface aims at the
// dungeon entrance pad instead — it walks up and steps in after the
// leader (BotPartyManager ports in any straggler the pad doesn't catch).
//
// Membership itself lives in BotPartyManager, not here. A member whose
// party is gone (disband edge, server load — parties are transient)
// self-heals to a Traveler on its next tick.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public class PartyMemberBehavior : AdventurerBehavior
    {
        public override string SerializableName => "PartyMember";

        // Close enough to the leader — stand instead of crowding him.
        private const int FollowNear = 3;

        public PartyMemberBehavior()
        {
            ChatCategories  = new[] { "traveling", "small_talk" };
            ChatChance      = 0.08;
            MinChatCooldown = TimeSpan.FromSeconds(40);
            MaxChatCooldown = TimeSpan.FromSeconds(120);
        }

        public override void Tick(PlayerBot bot)
        {
            var party = BotPartyManager.PartyOf(bot);
            if (party == null)
            {
                // Orphaned (disband raced a tick, or a stale save carried
                // the name through a restart) — back to ordinary life.
                bot.Behavior = BehaviorRegistry.Create("Traveler");
                return;
            }

            base.Tick(bot);
        }

        protected override Point3D? SelectPatrolGoal(PlayerBot bot)
        {
            var party = BotPartyManager.PartyOf(bot);
            if (party == null)
            {
                return bot.Location; // Tick self-heals next pass
            }

            // Entering: the leader is inside — walk onto the entrance pad.
            if (party.State == BotPartyState.Entering &&
                !DungeonRegistry.IsInDungeon(bot))
            {
                return party.EntranceTile;
            }

            var leader = party.Leader;
            if (leader == null || leader.Deleted || leader.Map != bot.Map)
            {
                return bot.Location; // manager handles ports / disband
            }

            if (bot.InRange(leader.Location, FollowNear))
            {
                return bot.Location; // in formation — stand easy
            }

            // Aim beside the leader, not on top of him, so a 4-bot party
            // arrives as a loose knot instead of a single-tile pile.
            int ox = Utility.RandomMinMax(-2, 2);
            int oy = Utility.RandomMinMax(-2, 2);
            return new Point3D(leader.X + ox, leader.Y + oy, leader.Z);
        }
    }
}
