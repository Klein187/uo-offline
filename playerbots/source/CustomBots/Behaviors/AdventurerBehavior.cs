// =========================================================================
// AdventurerBehavior.cs — Bots that explore the wilderness/dungeons,
// engaging monsters they encounter. Uses PathFollower (A*) for real
// pathfinding so they navigate around obstacles, into dungeons, etc.
//
// Architecture:
//   Decision tick (2s):
//     - In combat? Engage / chase / retreat
//     - Look for enemies in sight; engage
//     - Otherwise pick a patrol point and pathfind to it
//     - Random pauses, stuck recovery
//
//   Step timer (400ms walk / 200ms run):
//     - Calls PathFollower.Follow — A* handles everything
//
// Patrol point selection:
//   Random point within WanderRadius of Home. PathFollower routes around
//   any obstacles. If unreachable (stuck > timeout), pick another.
//
// Permadeath: PlayerBot's death drops corpse; spawner replaces the bot.
// =========================================================================

using System;
using Server;
using Server.Mobiles;
using MoveDelays = Server.Movement.Movement;

namespace Server.CustomBots
{
    public class AdventurerBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Adventurer";

        // ---- Tunables ----
        public int WanderRadius { get; set; } = 60;
        public int PatrolRange  { get; set; } = 25;
        public int SightRange   { get; set; } = 10;
        public double RetreatHpFraction { get; set; } = 0.30;
        public int ArrivalRange { get; set; } = 2;

        public Point3D Home { get; private set; }
        public Map     HomeMap { get; private set; }

        // ---- State ----
        private Point3D? _goal;
        private PathFollower _follower;
        private bool _running;

        private DateTime _pauseUntil = DateTime.MinValue;

        // Stuck detection.
        private Point3D _lastLoc;
        private DateTime _lastProgressAt;
        private static readonly TimeSpan StuckTimeout = TimeSpan.FromSeconds(10);

        private Timer _stepTimer;

        private static readonly string[] AmbientChat = { "small_talk", "lfg" };
        private static readonly string[] CombatChat  = { "combat_actions" };

        public AdventurerBehavior()
        {
            ChatCategories  = AmbientChat;
            ChatChance      = 0.12;
            MinChatCooldown = TimeSpan.FromSeconds(20);
            MaxChatCooldown = TimeSpan.FromSeconds(60);
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            Home    = bot.Location;
            HomeMap = bot.Map;
            _lastLoc        = bot.Location;
            _lastProgressAt = Core.Now;
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopStepTimer();
            base.OnDetached(bot);
        }

        // -------------------------------------------------------------------
        // Decision tick.
        // -------------------------------------------------------------------
        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal || bot.Deleted)
            {
                StopStepTimer();
                return;
            }

            ChatCategories = bot.Combatant != null ? CombatChat : AmbientChat;
            TrySpeak(bot);

            // -- 1. Combat --
            var combatant = bot.Combatant;
            if (combatant is Mobile foe)
            {
                if (foe.Deleted || !foe.Alive ||
                    foe.Map != bot.Map ||
                    !bot.InRange(foe.Location, SightRange + 4))
                {
                    bot.Combatant = null;
                    _goal = null;
                    _follower = null;
                    StopStepTimer();
                    return;
                }

                if (bot.Hits < bot.HitsMax * RetreatHpFraction)
                {
                    bot.Combatant = null;
                    SetGoal(bot, Home, running: true);
                    return;
                }

                if (!bot.InRange(foe.Location, 1))
                {
                    SetGoal(bot, foe.Location, running: true);
                }
                else
                {
                    // Adjacent — let combat tick swing.
                    StopStepTimer();
                }
                return;
            }

            // -- 2. Look for an enemy --
            var target = FindNearbyEnemy(bot);
            if (target != null)
            {
                bot.Combatant = target;
                SetGoal(bot, target.Location, running: true);
                return;
            }

            // -- 3. Stuck check --
            if (bot.Location != _lastLoc)
            {
                _lastLoc = bot.Location;
                _lastProgressAt = Core.Now;
            }
            else if (Core.Now - _lastProgressAt > StuckTimeout)
            {
                // Try a different patrol point.
                _goal = null;
                _follower = null;
                _lastProgressAt = Core.Now;
            }

            // -- 4. Pause --
            if (Core.Now < _pauseUntil)
            {
                StopStepTimer();
                return;
            }
            if (Utility.RandomDouble() < 0.05)
            {
                _pauseUntil = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(2, 5));
                StopStepTimer();
                return;
            }

            // -- 5. Patrol --
            EnsurePatrolGoal(bot);
            if (_goal != null)
            {
                EnsureStepTimer(bot, running: false);
            }
        }

        // -------------------------------------------------------------------
        // EnsurePatrolGoal — picks or refreshes the current patrol goal.
        // -------------------------------------------------------------------
        private void EnsurePatrolGoal(PlayerBot bot)
        {
            // If we've reached the current goal (or have none), pick new.
            if (_goal == null || bot.InRange(_goal.Value, ArrivalRange))
            {
                // If we're far from home, head back. Otherwise pick a random
                // point within PatrolRange.
                int homeDx = bot.X - Home.X;
                int homeDy = bot.Y - Home.Y;
                int homeDistSq = homeDx * homeDx + homeDy * homeDy;
                int wanderRadSq = WanderRadius * WanderRadius;

                if (homeDistSq > wanderRadSq)
                {
                    _goal = Home;
                }
                else
                {
                    double angle = Utility.RandomDouble() * Math.PI * 2.0;
                    int dist = Utility.RandomMinMax(8, PatrolRange);
                    int tx = bot.X + (int)(Math.Cos(angle) * dist);
                    int ty = bot.Y + (int)(Math.Sin(angle) * dist);
                    _goal = new Point3D(tx, ty, bot.Z);
                }
                _follower = new PathFollower(bot, _goal.Value);
            }
            else if (_follower == null)
            {
                _follower = new PathFollower(bot, _goal.Value);
            }
        }

        private void SetGoal(PlayerBot bot, Point3D goal, bool running)
        {
            if (_goal != goal || _follower == null)
            {
                _goal = goal;
                _follower = new PathFollower(bot, goal);
            }
            EnsureStepTimer(bot, running);
        }

        // -------------------------------------------------------------------
        // FindNearbyEnemy
        // -------------------------------------------------------------------
        private Mobile FindNearbyEnemy(PlayerBot bot)
        {
            Mobile best = null;
            int bestDistSq = int.MaxValue;

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, SightRange))
            {
                if (m == bot || m.Deleted || !m.Alive) continue;
                if (m is not BaseCreature bc) continue;
                if (bc.ControlMaster != null || bc.Summoned) continue;
                if (!bc.AlwaysAttackable && bc.FightMode == FightMode.None) continue;

                int dx = bc.X - bot.X;
                int dy = bc.Y - bot.Y;
                int distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = bc;
                }
            }
            return best;
        }

        // -------------------------------------------------------------------
        // Step timer
        // -------------------------------------------------------------------
        private void EnsureStepTimer(PlayerBot bot, bool running)
        {
            if (_stepTimer != null && _running == running)
                return;

            StopStepTimer();
            _running = running;
            int delayMs = running ? MoveDelays.RunFootDelay : MoveDelays.WalkFootDelay;
            var interval = TimeSpan.FromMilliseconds(delayMs);
            _stepTimer = Timer.DelayCall(interval, interval, () => StepOnce(bot));
        }

        private void StopStepTimer()
        {
            if (_stepTimer != null)
            {
                _stepTimer.Stop();
                _stepTimer = null;
            }
        }

        private void StepOnce(PlayerBot bot)
        {
            if (bot.Deleted || !bot.Alive || bot.Map == null || bot.Map == Map.Internal)
            {
                StopStepTimer();
                return;
            }

            if (_follower == null)
            {
                StopStepTimer();
                return;
            }

            bool arrived = _follower.Follow(_running, ArrivalRange);
            if (arrived)
            {
                // Reached current goal — decision tick picks the next one.
                StopStepTimer();
                _goal = null;
                _follower = null;
            }
        }
    }
}
