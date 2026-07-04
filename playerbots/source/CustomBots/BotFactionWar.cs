// =========================================================================
// BotFactionWar.cs — Order vs Chaos (IDEAS 2.1 phase 3).
//
// The era-correct faction war. Six of the bot guilds carry shields —
// three Order (Knights of Yew, Order of the Silver Serpent, Guardians of
// Virtue), three Chaos (DOOM, The Undead Lords, Dread Lords of Nox) —
// and members of opposing shields fight ON SIGHT, in town, guards
// ignoring it (PlayerBot.IsHarmfulCriminal treats faction enemies as
// legal targets, exactly like the real Order/Chaos rules). Street
// fights outside Brit bank were THE spectacle of the era; this brings
// them back.
//
// The manager scans periodically, picks ONE eligible opposing pair in
// sight of each other, and starts the fight: battle cries, mutual
// Combatant, and defender-mode Adventurer brains that prosecute the
// fight and return both to their trips afterward. Retreat logic ends
// most fights with a runner; the ones that end in a kill feed the death
// system (ghost, corpse run) and the event journal ("faction" gossip —
// the whole bank talks about it).
//
// Cooldowns keep it a street fight now and then, not a permanent brawl.
//
//   [BotFactions         — status (faction counts, active fights)
//   [BotFactions fight   — force a fight for testing (teleports one
//                          fighter to the other if none are colocated)
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public enum BotFaction : byte
    {
        None,
        Order,
        Chaos,
    }

    public static class BotFactionWar
    {
        // ---- Knobs ----

        public static bool Enabled = true;

        // How far a faction bot notices an enemy shield.
        private const int SightRange = 12;

        // At most this many faction fights running at once.
        private const int MaxActiveFights = 3;

        // A new fight starts at most this often (randomized), so faction
        // violence stays an EVENT, not a constant. (First soak at 90s-5m
        // produced 4 fights in 10 minutes — constant brawling, not a
        // spectacle. ~5-8/hour peak feels like "the war is real" without
        // wallpapering the banks with it.)
        private static readonly TimeSpan FightAttemptMin = TimeSpan.FromMinutes(4);
        private static readonly TimeSpan FightAttemptMax = TimeSpan.FromMinutes(12);

        // Per-bot and per-pair rest between fights.
        private static readonly TimeSpan BotCooldown  = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PairCooldown = TimeSpan.FromMinutes(30);

        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

        // ---- State ----

        private static Timer _timer;
        private static DateTime _nextFightAttempt = DateTime.MinValue;
        private static readonly List<(PlayerBot a, PlayerBot b, DateTime started)> _fights = new();
        private static readonly Dictionary<Serial, DateTime> _botCooldowns = new();
        private static readonly Dictionary<ulong, DateTime> _pairCooldowns = new();

        public static void Configure()
        {
            _timer = Timer.DelayCall(TickInterval, TickInterval, OnTick);
            CommandSystem.Register("BotFactions", AccessLevel.GameMaster, Status_OnCommand);
        }

        public static BotFaction FactionOf(PlayerBot bot) =>
            BotGuilds.Get(bot?.BotGuildIndex ?? -1)?.Faction ?? BotFaction.None;

        public static bool AreEnemies(PlayerBot a, PlayerBot b)
        {
            var fa = FactionOf(a);
            if (fa == BotFaction.None)
            {
                return false;
            }
            var fb = FactionOf(b);
            return fb != BotFaction.None && fb != fa;
        }

        // -------------------------------------------------------------------
        private static void OnTick()
        {
            if (!Enabled)
            {
                return;
            }

            PruneFights();

            if (Core.Now < _nextFightAttempt || _fights.Count >= MaxActiveFights)
            {
                return;
            }
            _nextFightAttempt = Core.Now + TimeSpan.FromSeconds(
                Utility.RandomMinMax((int)FightAttemptMin.TotalSeconds,
                                     (int)FightAttemptMax.TotalSeconds));

            TryStartFight(teleport: false);
        }

        // A fight is over when either side is gone or neither is still
        // targeting the other (both fled / one died — the death flow owns
        // the loser from there).
        private static void PruneFights()
        {
            for (int i = _fights.Count - 1; i >= 0; i--)
            {
                var (a, b, started) = _fights[i];
                bool over =
                    a == null || a.Deleted || !a.Alive ||
                    b == null || b.Deleted || !b.Alive ||
                    (Core.Now - started > TimeSpan.FromSeconds(30) &&
                     a.Combatant != b && b.Combatant != a);
                if (over)
                {
                    _fights.RemoveAt(i);
                }
            }
        }

        private static bool OnFightCooldown(PlayerBot bot) =>
            _botCooldowns.TryGetValue(bot.Serial, out var until) && Core.Now < until;

        private static ulong PairKey(PlayerBot a, PlayerBot b)
        {
            uint x = (uint)a.Serial.Value, y = (uint)b.Serial.Value;
            return x < y ? ((ulong)x << 32) | y : ((ulong)y << 32) | x;
        }

        private static bool IsEligible(PlayerBot bot) =>
            bot != null && !bot.Deleted && bot.Alive &&
            !bot.LifecycleExempt && !bot.LoggingOut &&
            !bot.CorpseRunPending &&
            bot.Combatant == null &&
            bot.Behavior is not PKBehavior
                        and not GhostBehavior
                        and not CorpseReclaimBehavior &&
            !BotPartyManager.IsInParty(bot) &&
            !DungeonRegistry.IsInDungeon(bot) &&
            !OnFightCooldown(bot);

        // -------------------------------------------------------------------
        // TryStartFight — find one opposing pair in sight of each other and
        // light the fuse. With teleport=true (test command), any two
        // opposing bots anywhere will do; one is moved to the other.
        // -------------------------------------------------------------------
        public static bool TryStartFight(bool teleport)
        {
            // Collect eligible faction bots.
            var fighters = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot &&
                    FactionOf(bot) != BotFaction.None &&
                    IsEligible(bot))
                {
                    fighters.Add(bot);
                }
            }
            if (fighters.Count < 2)
            {
                return false;
            }

            Shuffle(fighters);

            foreach (var a in fighters)
            {
                foreach (var b in fighters)
                {
                    if (a == b || !AreEnemies(a, b))
                    {
                        continue;
                    }
                    if (_pairCooldowns.TryGetValue(PairKey(a, b), out var until) &&
                        Core.Now < until)
                    {
                        continue;
                    }

                    bool inSight = a.Map == b.Map && a.InRange(b.Location, SightRange);
                    if (!inSight && !teleport)
                    {
                        continue;
                    }
                    if (!inSight)
                    {
                        // Test path: stage the encounter.
                        b.MoveToWorld(new Point3D(
                            a.X + Utility.RandomMinMax(-4, 4),
                            a.Y + Utility.RandomMinMax(-4, 4),
                            a.Z), a.Map);
                    }

                    BeginFight(a, b);
                    return true;
                }
            }
            return false;
        }

        private static void Shuffle(List<PlayerBot> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Utility.Random(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static void BeginFight(PlayerBot a, PlayerBot b)
        {
            _fights.Add((a, b, Core.Now));
            _botCooldowns[a.Serial] = Core.Now + BotCooldown;
            _botCooldowns[b.Serial] = Core.Now + BotCooldown;
            _pairCooldowns[PairKey(a, b)] = Core.Now + PairCooldown;

            // Runaway brake on the cooldown dicts (bots churn with sessions).
            if (_botCooldowns.Count > 2000)
            {
                _botCooldowns.Clear();
            }
            if (_pairCooldowns.Count > 4000)
            {
                _pairCooldowns.Clear();
            }

            Console.WriteLine(
                $"[faction] {FactionOf(a)} {a.Name} vs {FactionOf(b)} {b.Name} " +
                $"at ({a.X},{a.Y})!");

            BattleCry(a);
            BattleCry(b);

            // Defender-mode brains prosecute the fight with the full combat
            // kit and hand each bot back to its trip when it's over. Stash
            // the current trip so the winner resumes where it was headed.
            var destA = (a.Behavior as TravelerBehavior)?.DestinationName;
            var destB = (b.Behavior as TravelerBehavior)?.DestinationName;

            a.Behavior = new AdventurerBehavior
            {
                DefenderMode = true,
                DefenderRetreatHpFraction = 0.30, // shield pride: fight longer
                ResumeDestination = destA,
            };
            b.Behavior = new AdventurerBehavior
            {
                DefenderMode = true,
                DefenderRetreatHpFraction = 0.30,
                ResumeDestination = destB,
            };

            a.Combatant = b;
            b.Combatant = a;
        }

        private static void BattleCry(PlayerBot bot)
        {
            var category = FactionOf(bot) == BotFaction.Order
                ? "order_battle"
                : "chaos_battle";
            var line = ChatLibrary.PickRandom(category);
            if (!string.IsNullOrEmpty(line))
            {
                bot.Say(line);
            }
        }

        // -------------------------------------------------------------------
        [Usage("BotFactions [fight]")]
        [Description("Shows faction war status, or forces a test fight.")]
        private static void Status_OnCommand(CommandEventArgs e)
        {
            if (e.Arguments.Length > 0 &&
                string.Equals(e.Arguments[0], "fight", StringComparison.OrdinalIgnoreCase))
            {
                e.Mobile.SendMessage(TryStartFight(teleport: true)
                    ? "Faction fight started — check the console for who/where."
                    : "No two eligible opposing faction bots found.");
                return;
            }

            int order = 0, chaos = 0;
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted)
                {
                    switch (FactionOf(bot))
                    {
                        case BotFaction.Order:
                            order++;
                            break;
                        case BotFaction.Chaos:
                            chaos++;
                            break;
                    }
                }
            }

            PruneFights();
            e.Mobile.SendMessage(
                $"Faction war: {(Enabled ? "ON" : "OFF")}. " +
                $"Order {order}, Chaos {chaos}, active fights {_fights.Count}.");
            foreach (var (a, b, started) in _fights)
            {
                e.Mobile.SendMessage(
                    $"  {a?.Name} vs {b?.Name}, {(int)(Core.Now - started).TotalSeconds}s, " +
                    $"at ({a?.X},{a?.Y})");
            }
        }
    }
}
