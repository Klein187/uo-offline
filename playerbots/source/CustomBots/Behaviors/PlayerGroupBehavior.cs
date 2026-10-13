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
using System.Collections.Generic;
using Server;
using Server.Engines.PartySystem;
using Server.Mobiles;
using MoveDelays = Server.Movement.Movement;

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

        // The leader's footsteps, oldest first. A follower on the trail
        // steps along it on its own timer at running pace instead of
        // aiming at the leader every 2-second decision tick: aiming made
        // it run to where the leader WAS, stop there, and wait, so a
        // running player lost it within a minute, and past A*'s reach it
        // stood still. The trail is always walkable (the leader walked
        // it), and a teleporter the leader stepped on is on it too.
        private readonly List<Point3D> _trail = new();
        private const int TrailMax = 800;
        private const int TeleportGap = 6;
        private DateTime _trailBlockedUntil = DateTime.MinValue;
        private Timer _followTimer;
        private bool _followMounted;
        private DateTime _onTrailUntil = DateTime.MinValue;

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

            // Leashed: the leader is off and not fighting. Drop whatever
            // this is and go; the base drops the fight (HoldFights).
            _leashed = bot.Alive && IsLeashed(bot, leader);

            // Assist: whatever is on ANY of us is on all of us. This used
            // to pick up only the leader's fight, and only against a
            // monster — a red on the player, or on another member, got
            // a bot standing there watching. Same rule the bot-led hunts
            // use now.
            if (bot.Alive && bot.Combatant == null && !_leashed)
            {
                var foe = PartyFoe(bot);
                if (foe != null)
                {
                    bot.Combatant = foe;
                    if (CombatDebug)
                    {
                        Console.WriteLine(
                            $"[party] {bot.Name} assists {leader.Name}'s party against '{foe.Name}'");
                    }
                }
            }

            EnsureFollowTimer(bot);
            base.Tick(bot);
        }

        // How far the leader can get before a follower drops its fight.
        private const int LeashRange = 10;
        private bool _leashed;

        protected override bool HoldFights(PlayerBot bot) => _leashed;

        // The leader is more than LeashRange off and is not standing in a
        // fight of its own. A player who runs is not asking the group to
        // stay behind and finish the zombie: underground every follower
        // used to stop for each monster on the way and was left 30 to 60
        // tiles back, re-engaging reds it had already given up on.
        private static bool IsLeashed(PlayerBot bot, Mobile leader)
        {
            if (Dist(bot.Location, leader.Location) <= LeashRange)
            {
                return false;
            }
            return leader.Combatant is not Mobile lc || lc.Deleted || !lc.Alive ||
                   Dist(leader.Location, lc.Location) > 12;
        }

        protected override bool HoldPatrol(PlayerBot bot) =>
            bot.Combatant == null && Core.Now < _onTrailUntil;

        private void EnsureFollowTimer(PlayerBot bot)
        {
            bool mounted = bot.Mounted;
            if (_followTimer != null && _followMounted == mounted)
            {
                return;
            }
            _followTimer?.Stop();
            _followMounted = mounted;
            var interval = TimeSpan.FromMilliseconds(
                mounted ? MoveDelays.RunMountDelay : MoveDelays.RunFootDelay);
            _followTimer = Timer.DelayCall(interval, interval, () => FollowStep(bot));
        }

        private void StopFollow()
        {
            _followTimer?.Stop();
            _followTimer = null;
            _onTrailUntil = DateTime.MinValue;
        }

        private void RecordLeader(Point3D at)
        {
            if (_trail.Count > 0 && _trail[^1] == at)
            {
                return;
            }
            _trail.Add(at);
            if (_trail.Count > TrailMax)
            {
                _trail.RemoveRange(0, _trail.Count - TrailMax);
            }
        }

        // Newest trail point the bot is standing on or next to, or -1.
        private int TrailIndexAt(PlayerBot bot)
        {
            for (int i = _trail.Count - 1; i >= 0; i--)
            {
                var c = _trail[i];
                if (Dist(c, bot.Location) <= 1 && Math.Abs(c.Z - bot.Z) <= 10)
                {
                    return i;
                }
            }
            return -1;
        }

        private void FollowStep(PlayerBot bot)
        {
            if (bot.Deleted || bot.Behavior != this)
            {
                StopFollow();
                return;
            }
            var leader = LeaderOf(bot);
            if (leader == null || leader.Map != bot.Map)
            {
                _onTrailUntil = DateTime.MinValue;
                return;
            }
            RecordLeader(leader.Location);

            if (!bot.Alive || (bot.Combatant != null && !_leashed) || bot.Frozen || bot.Paralyzed ||
                Dist(bot.Location, leader.Location) <= CloseEnough)
            {
                _onTrailUntil = DateTime.MinValue;
                return;
            }

            // A blocked step handed the bot to the patrol: let it path round
            // for a few seconds instead of halting it again next step.
            if (Core.Now < _trailBlockedUntil)
            {
                return;
            }

            int at = TrailIndexAt(bot);
            if (at < 0 || at + 1 >= _trail.Count)
            {
                _onTrailUntil = DateTime.MinValue;
                return;
            }

            // A big gap in the trail is a teleport: the leader stepped on
            // a pad here. Stand on the same tile and let it take the bot
            // too. A recall leaves the bot at the last footstep, and the
            // lost clock handles that. A gap of a tile or two is only the
            // leader stepping twice between samples.
            var next = _trail[at + 1];
            if (Dist(next, _trail[at]) > TeleportGap)
            {
                // The pad itself is never on the trail: the leader is moved
                // the moment it steps on, so the last footstep is the tile
                // BEFORE it. Step onto the teleporter beside that footstep.
                next = PadNear(bot.Map, _trail[at]) ?? _trail[at];
                if (bot.Location == next)
                {
                    _onTrailUntil = Core.Now + TimeSpan.FromSeconds(1);
                    return;
                }
            }
            // Cut a corner where the trail doubles back past the bot.
            else if (at + 2 < _trail.Count && Dist(_trail[at + 2], bot.Location) <= 1 &&
                     Dist(_trail[at + 2], _trail[at + 1]) <= 1)
            {
                next = _trail[at + 2];
            }

            if (Core.Now >= _onTrailUntil)
            {
                HaltMovement();
            }
            _onTrailUntil = Core.Now + TimeSpan.FromSeconds(1);

            if (next == bot.Location)
            {
                return;
            }
            if (TryStep(bot, bot.GetDirectionTo(next)))
            {
                return;
            }

            // The next footstep is taken, usually by another follower bunched
            // up behind the same leader. Try the one after it, then a tile
            // either side, before handing the bot to the patrol.
            if (at + 2 < _trail.Count && Dist(_trail[at + 2], bot.Location) <= 2 &&
                TryStep(bot, bot.GetDirectionTo(_trail[at + 2])))
            {
                return;
            }
            var ahead = bot.GetDirectionTo(next);
            if (TryStep(bot, (Direction)(((int)ahead + 1) & 0x7)) ||
                TryStep(bot, (Direction)(((int)ahead + 7) & 0x7)))
            {
                return;
            }

            // Truly blocked (a door, a wall the trail cut by a teleport):
            // hand back to the patrol for a few seconds; it paths round it.
            _onTrailUntil = DateTime.MinValue;
            _trailBlockedUntil = Core.Now + TimeSpan.FromSeconds(3);
        }

        // The nearest working teleporter within 2 tiles of a spot, if any.
        private static Point3D? PadNear(Map map, Point3D at)
        {
            Point3D? best = null;
            int bestDist = int.MaxValue;
            foreach (var t in map.GetItemsInRange<Server.Items.Teleporter>(at, 2))
            {
                if (!t.Active)
                {
                    continue;
                }
                int d = Dist(t.Location, at);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = t.Location;
                }
            }
            return best;
        }

        // Turn, then step (a Move in a new direction only turns), running.
        private static bool TryStep(PlayerBot bot, Direction dir)
        {
            var from = bot.Location;
            if ((bot.Direction & Direction.Mask) != (dir & Direction.Mask))
            {
                bot.Direction = dir;
            }
            return bot.Move(dir | Direction.Running) && bot.Location != from;
        }

        private const int AssistRange = 14;

        // Nearest thing fighting a member of the player's party. A monster
        // always counts. A person counts only when they are the aggressor
        // — attacking one of us, or a standing murderer — so the bot never
        // swings first at a blue the player happens to be duelling.
        private static Mobile PartyFoe(PlayerBot bot)
        {
            if (bot.Party is not Party p)
            {
                return null;
            }

            Mobile best = null;
            int bestDist = int.MaxValue;
            for (int i = 0; i < p.Members.Count; i++)
            {
                var mate = p.Members[i].Mobile;
                if (mate == null || mate == bot || mate.Deleted || !mate.Alive ||
                    mate.Map != bot.Map)
                {
                    continue;
                }

                // A foe fighting this mate, or one this mate is fighting.
                Mobile foe = mate.Combatant as Mobile;
                if (!IsHostileToParty(foe, p))
                {
                    foe = null;
                }
                if (foe == null)
                {
                    foreach (var m in mate.GetMobilesInRange(AssistRange))
                    {
                        if (m.Combatant == mate && IsHostileToParty(m, p))
                        {
                            foe = m;
                            break;
                        }
                    }
                }
                if (foe == null || foe.Map != bot.Map || bot.IsUnreachable(foe))
                {
                    continue;
                }

                int d = Math.Max(Math.Abs(foe.X - bot.X), Math.Abs(foe.Y - bot.Y));
                if (d <= AssistRange && d < bestDist)
                {
                    bestDist = d;
                    best = foe;
                }
            }
            return best;
        }

        private static bool IsHostileToParty(Mobile foe, Party p)
        {
            if (foe == null || foe.Deleted || !foe.Alive || Party.Get(foe) == p)
            {
                return false;
            }
            if (foe is BaseCreature bc)
            {
                return bc.ControlMaster == null || Party.Get(bc.ControlMaster) != p;
            }
            if (foe is PlayerMobile pm)
            {
                if (pm.Murderer)
                {
                    return true;
                }
                if (pm.Combatant is Mobile target && Party.Get(target) == p)
                {
                    return true;
                }
            }
            return false;
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
                // Near enough: mill a tile or so beside the leader. Null
                // here is NOT idling - it hands the bot the base wander,
                // a random spot 8-25 tiles off (or the spot it joined at,
                // once that is 60 tiles back), which walked it away from
                // the group it had just caught up with.
                // Never onto a teleporter: a dungeon's landing sits among
                // its exit pads, and a milling bot (or the stuck sidestep
                // after it) walked onto one and back up to the surface.
                // Near a pad, stand still.
                if (PadNear(bot.Map, leader.Location) != null || PadNear(bot.Map, bot.Location) != null)
                {
                    return bot.Location;
                }
                var spot = new Point3D(
                    leader.X + Utility.RandomMinMax(-FollowSlotRadius, FollowSlotRadius),
                    leader.Y + Utility.RandomMinMax(-FollowSlotRadius, FollowSlotRadius),
                    leader.Z);
                return PadNear(bot.Map, spot) is Point3D pad && Dist(pad, spot) <= 1
                    ? bot.Location
                    : spot;
            }

            if (dist > 30)
            {
                // The furthest-along footstep A* can still reach from here:
                // the nearest one is usually the tile the bot stands on.
                for (int i = _trail.Count - 1; i >= 0; i--)
                {
                    if (Dist(_trail[i], bot.Location) <= 25)
                    {
                        return _trail[i];
                    }
                }
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
            StopFollow();
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
