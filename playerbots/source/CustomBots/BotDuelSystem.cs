// =========================================================================
// BotDuelSystem.cs — duels outside the bank (IDEAS 2.3).
//
// Era-perfect theater: two bots emote a challenge at the bank, walk ten
// tiles clear of the crowd, fight to low health (NOT to the death),
// winner bows and gloats, loser takes it gracefully, everyone drifts
// back. Uses the existing combat engine (DuelistBehavior is a thin
// Adventurer skin); the manager referees — it starts the fight when
// both reach their marks and stops it the moment someone's health dips.
//
// Duels are LEGAL (PlayerBot.IsHarmfulCriminal exempts a registered
// pair) so no guards crash the show. If a freak crit actually kills
// someone, the death system takes over — "died in a duel at the bank"
// is a fine story for the gossip mill.
//
//   [BotDuel — force a duel near you (or anywhere) for testing
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public enum BotDuelState
    {
        WalkingOut,
        Fighting,
    }

    public sealed class BotDuel
    {
        public PlayerBot A;
        public PlayerBot B;
        public Point3D SpotA;
        public Point3D SpotB;
        public int PreHitsA;
        public int PreHitsB;
        public BotDuelState State;
        public DateTime StartedAt;
        public DateTime StateSince;
    }

    public class DuelistBehavior : AdventurerBehavior
    {
        public override string SerializableName => "Duelist";

        // Where the referee sent this duelist to stand.
        public Point3D Mark;

        public DuelistBehavior()
        {
            ChatChance = 0.0; // all speech comes from the scene beats
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            // Honor duel: no fleeing at half health — the referee stops
            // the fight, not the nerves. (Death backstop: the death flow.)
            RetreatHpFraction = 0.05;
        }

        public override void Tick(PlayerBot bot)
        {
            // Orphaned (server hiccup mid-duel) — self-heal.
            if (BotDuelManager.DuelOf(bot) == null)
            {
                bot.Behavior = BehaviorRegistry.Create("BankSitter");
                return;
            }
            base.Tick(bot);
        }

        protected override Point3D? SelectPatrolGoal(PlayerBot bot)
        {
            // Walk to the mark, then hold it until the referee starts the
            // fight (combat preempts patrol from then on).
            return bot.InRange(Mark, 1) ? bot.Location : Mark;
        }
    }

    public static class BotDuelManager
    {
        public static bool Enabled = true;

        // A duel kicks off at most this often.
        private static readonly TimeSpan AttemptMin = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan AttemptMax = TimeSpan.FromMinutes(20);

        private static readonly TimeSpan WalkOutTimeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan FightTimeout   = TimeSpan.FromMinutes(3);

        // Stop the fight when either duelist drops this low.
        private const double StopHpFraction = 0.40;

        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(4);

        private static Timer _timer;
        private static DateTime _nextAttempt = DateTime.MinValue;
        private static BotDuel _duel; // one at a time — it's a spectacle
        private static readonly Dictionary<Serial, DateTime> _cooldowns = new();
        private static readonly TimeSpan BotCooldown = TimeSpan.FromMinutes(30);

        public static void Configure()
        {
            _timer = Timer.DelayCall(TickInterval, TickInterval, OnTick);
            CommandSystem.Register("BotDuel", AccessLevel.GameMaster, Force_OnCommand);
        }

        public static BotDuel DuelOf(PlayerBot bot) =>
            _duel != null && (_duel.A == bot || _duel.B == bot) ? _duel : null;

        public static bool AreDueling(PlayerBot a, PlayerBot b) =>
            _duel != null &&
            ((_duel.A == a && _duel.B == b) || (_duel.A == b && _duel.B == a));

        private static void OnTick()
        {
            if (!Enabled)
            {
                return;
            }

            if (_duel != null)
            {
                Referee();
                return;
            }

            if (Core.Now < _nextAttempt)
            {
                return;
            }
            _nextAttempt = Core.Now + TimeSpan.FromSeconds(
                Utility.RandomMinMax((int)AttemptMin.TotalSeconds,
                                     (int)AttemptMax.TotalSeconds));
            TryStartDuel(null);
        }

        // -------------------------------------------------------------------
        private static bool IsEligible(PlayerBot bot) =>
            bot != null && !bot.Deleted && bot.Alive &&
            !bot.LifecycleExempt && !bot.LoggingOut &&
            !bot.CorpseRunPending &&
            bot.Combatant == null &&
            bot.Behavior is BankSitterBehavior &&
            !BotPartyManager.IsInParty(bot) &&
            !(_cooldowns.TryGetValue(bot.Serial, out var until) && Core.Now < until);

        private static bool IsFighterClass(BotClass c) =>
            c is BotClass.Warrior or BotClass.Fencer or BotClass.Archer
              or BotClass.Ranger or BotClass.Mage;

        public static bool TryStartDuel(Point3D? anchor)
        {
            if (_duel != null)
            {
                return false;
            }

            var candidates = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && IsEligible(bot) && IsFighterClass(bot.Class))
                {
                    if (anchor.HasValue &&
                        Math.Max(Math.Abs(bot.X - anchor.Value.X),
                                 Math.Abs(bot.Y - anchor.Value.Y)) > 30)
                    {
                        continue;
                    }
                    candidates.Add(bot);
                }
            }

            foreach (var a in candidates)
            {
                foreach (var b in candidates)
                {
                    if (a == b ||
                        a.Map != b.Map ||
                        !a.InRange(b.Location, 10) ||
                        Math.Abs((int)a.SkillTier - (int)b.SkillTier) > 2 ||
                        BotFactionWar.AreEnemies(a, b)) // those fight for real
                    {
                        continue;
                    }
                    Begin(a, b);
                    return true;
                }
            }
            return false;
        }

        private static void Begin(PlayerBot a, PlayerBot b)
        {
            if (_cooldowns.Count > 2000)
            {
                _cooldowns.Clear();
            }
            _cooldowns[a.Serial] = Core.Now + BotCooldown;
            _cooldowns[b.Serial] = Core.Now + BotCooldown;

            // Marks: ~11 tiles from the midpoint, facing each other 6 apart.
            int mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;
            double ang = Utility.RandomDouble() * Math.PI * 2;
            int dx = (int)(Math.Cos(ang) * 11), dy = (int)(Math.Sin(ang) * 11);
            var map = a.Map;
            var spotA = Fit(map, mx + dx - (dx == 0 ? 3 : Math.Sign(dx) * 3), my + dy - (dy == 0 ? 3 : Math.Sign(dy) * 3));
            var spotB = Fit(map, mx + dx + (dx == 0 ? 3 : Math.Sign(dx) * 3), my + dy + (dy == 0 ? 3 : Math.Sign(dy) * 3));

            _duel = new BotDuel
            {
                A = a, B = b,
                SpotA = spotA, SpotB = spotB,
                PreHitsA = a.Hits, PreHitsB = b.Hits,
                State = BotDuelState.WalkingOut,
                StartedAt = Core.Now,
                StateSince = Core.Now,
            };

            Console.WriteLine($"[duel] {a.Name} vs {b.Name} at ({mx},{my})");

            BotScene.Play(
                (0.0, a, BotScene.Pick("duel_challenge")),
                (2.0, a, "*bows*"),
                (2.0, b, BotScene.Pick("duel_accept")),
                (1.5, b, "*bows*"));

            // Give the scene a beat, then send them to their marks.
            Timer.DelayCall(TimeSpan.FromSeconds(6), () =>
            {
                if (_duel == null || a.Deleted || b.Deleted)
                {
                    return;
                }
                a.Behavior = new DuelistBehavior { Mark = spotA };
                b.Behavior = new DuelistBehavior { Mark = spotB };
            });
        }

        private static Point3D Fit(Map map, int x, int y)
        {
            int z = map.GetAverageZ(x, y);
            if (map.CanFit(x, y, z, 16, false, false))
            {
                return new Point3D(x, y, z);
            }
            return new Point3D(x, y, z); // PathFollower copes; mark is advisory
        }

        // -------------------------------------------------------------------
        private static void Referee()
        {
            var d = _duel;
            var a = d.A;
            var b = d.B;

            bool broken =
                a == null || a.Deleted || !a.Alive ||
                b == null || b.Deleted || !b.Alive;

            if (broken)
            {
                // Someone actually died (or vanished) — the death flow has
                // the body; just clear the ring.
                Cleanup(revert: true);
                return;
            }

            switch (d.State)
            {
                case BotDuelState.WalkingOut:
                    bool ready = a.InRange(d.SpotA, 4) && b.InRange(d.SpotB, 4);
                    if (ready)
                    {
                        d.State = BotDuelState.Fighting;
                        d.StateSince = Core.Now;
                        d.PreHitsA = a.Hits;
                        d.PreHitsB = b.Hits;
                        a.Combatant = b;
                        b.Combatant = a;
                    }
                    else if (Core.Now - d.StateSince > WalkOutTimeout)
                    {
                        // Couldn't stage it (blocked marks) — call it off.
                        Console.WriteLine(
                            $"[duel] {a.Name} vs {b.Name} called off (marks unreachable)");
                        Cleanup(revert: true);
                    }
                    break;

                case BotDuelState.Fighting:
                    bool aLow = a.Hits <= a.HitsMax * StopHpFraction;
                    bool bLow = b.Hits <= b.HitsMax * StopHpFraction;
                    if (aLow || bLow || Core.Now - d.StateSince > FightTimeout)
                    {
                        var winner = aLow ? b : bLow ? a
                            : a.Hits >= b.Hits ? a : b; // timeout: judge's decision
                        var loser = winner == a ? b : a;
                        EndFight(winner, loser);
                    }
                    break;
            }
        }

        private static void EndFight(PlayerBot winner, PlayerBot loser)
        {
            var d = _duel;

            winner.Combatant = null;
            loser.Combatant = null;

            // Friendly bout: nobody limps home. Restore pre-fight blood.
            d.A.Hits = Math.Max(d.A.Hits, d.PreHitsA);
            d.B.Hits = Math.Max(d.B.Hits, d.PreHitsB);

            Console.WriteLine($"[duel] {winner.Name} defeats {loser.Name}");
            BotEventJournal.Record("duel", winner, loser.Name);

            BotScene.Play(
                (0.5, winner, "*bows*"),
                (2.0, winner, BotScene.Pick("duel_win")),
                (2.0, loser,  BotScene.Pick("duel_loss")),
                (1.5, loser,  "*bows*"));

            // Revert after the bows; the pair also become friends — a good
            // duel is how half of 1999's friendships started.
            BotSocialGraph.MakeFriends(winner, loser);
            var duel = d;
            Timer.DelayCall(TimeSpan.FromSeconds(7), () =>
            {
                foreach (var bot in new[] { duel.A, duel.B })
                {
                    if (bot != null && !bot.Deleted && bot.Alive &&
                        bot.Behavior is DuelistBehavior)
                    {
                        bot.Behavior = BehaviorRegistry.Create("BankSitter");
                    }
                }
            });

            _duel = null;
        }

        private static void Cleanup(bool revert)
        {
            var d = _duel;
            _duel = null;
            if (d == null)
            {
                return;
            }
            if (revert)
            {
                foreach (var bot in new[] { d.A, d.B })
                {
                    if (bot != null && !bot.Deleted && bot.Alive &&
                        bot.Behavior is DuelistBehavior)
                    {
                        bot.Behavior = BehaviorRegistry.Create("BankSitter");
                    }
                }
            }
        }

        [Usage("BotDuel")]
        [Description("Forces a duel between two bank-sitting fighters (near you if possible).")]
        private static void Force_OnCommand(CommandEventArgs e)
        {
            bool started = TryStartDuel(e.Mobile.Location) || TryStartDuel(null);
            e.Mobile.SendMessage(started
                ? "Duel starting — watch the bank."
                : "No two eligible bank-sitting fighters found.");
        }
    }
}
