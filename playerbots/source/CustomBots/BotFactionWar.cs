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

        // Party note: HUNT parties are off-limits (don't break up a dungeon
        // dive over shield politics), but convoy/warband members are fair
        // game — meeting the enemy is a war band's whole purpose, and a
        // guild convoy of faction guildmates that crosses the other side
        // fights like anyone else. Their party dissolves into the battle.
        private static bool IsEligible(PlayerBot bot) =>
            bot != null && !bot.Deleted && bot.Alive &&
            !bot.LifecycleExempt && !bot.LoggingOut &&
            !bot.CorpseRunPending &&
            bot.Combatant == null &&
            bot.Behavior is not PKBehavior
                        and not GhostBehavior
                        and not CorpseReclaimBehavior &&
            BotPartyManager.PartyOf(bot) is not { Kind: BotPartyKind.Hunt } &&
            !DungeonRegistry.IsInDungeon(bot) &&
            !OnFightCooldown(bot);

        // Ally-draft eligibility: same as above minus the fight cooldown —
        // a bot standing next to a faction brawl is in it, rested or not.
        private static bool IsDraftable(PlayerBot bot, BotFaction faction) =>
            bot != null && !bot.Deleted && bot.Alive &&
            FactionOf(bot) == faction &&
            !bot.LifecycleExempt && !bot.LoggingOut &&
            !bot.CorpseRunPending &&
            bot.Combatant == null &&
            bot.Behavior is not PKBehavior
                        and not GhostBehavior
                        and not CorpseReclaimBehavior &&
            BotPartyManager.PartyOf(bot) is not { Kind: BotPartyKind.Hunt } &&
            !DungeonRegistry.IsInDungeon(bot);

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

        // How far around each principal we look for faction-mates to pull
        // into the fight, and the biggest side a brawl can grow to.
        private const int AllyDraftRange = 10;
        private const int MaxSideSize = 4;

        private static void BeginFight(PlayerBot a, PlayerBot b)
        {
            // GROUP FIGHT: everyone's faction-mates standing nearby pile in
            // — a lone enemy walking into a war band gets ganged exactly
            // like 1999, and two bands meeting becomes a street battle.
            var sideA = new List<PlayerBot> { a };
            var sideB = new List<PlayerBot> { b };
            CollectAllies(a, sideA, sideB);
            CollectAllies(b, sideB, sideA);

            _fights.Add((a, b, Core.Now));
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
                $"[faction] {FactionOf(a)} vs {FactionOf(b)} — " +
                $"{sideA.Count}v{sideB.Count} at ({a.X},{a.Y})! " +
                $"({a.Name} / {b.Name})");
            if (sideA.Count + sideB.Count > 2)
            {
                BotEventJournal.Record("warclash", a, $"{sideA.Count}v{sideB.Count}");
            }

            // Convert every participant to a fight-ready defender FIRST,
            // then dissolve any convoy/warband they marched in — Disband
            // only reattaches bots still running PartyMemberBehavior, so
            // drafted fighters keep their defender brains and only
            // undrafted stragglers go back to ordinary life.
            int cry = 0;
            foreach (var bot in sideA)
            {
                Draft(bot, cry++);
            }
            foreach (var bot in sideB)
            {
                Draft(bot, cry++);
            }
            foreach (var bot in sideA)
            {
                BotPartyManager.DisbandInvolving(bot);
            }
            foreach (var bot in sideB)
            {
                BotPartyManager.DisbandInvolving(bot);
            }

            // Pair everyone off cyclically so each fighter opens on an
            // enemy (uneven sides double up on the outnumbered).
            int n = Math.Max(sideA.Count, sideB.Count);
            for (int i = 0; i < n; i++)
            {
                var xa = sideA[i % sideA.Count];
                var xb = sideB[i % sideB.Count];
                xa.Combatant = xb;
                xb.Combatant = xa;
            }
        }

        // Pull nearby same-faction bots into `side` (center is already in).
        private static void CollectAllies(
            PlayerBot center, List<PlayerBot> side, List<PlayerBot> otherSide)
        {
            var faction = FactionOf(center);
            foreach (var m in center.Map.GetMobilesInRange(center.Location, AllyDraftRange))
            {
                if (side.Count >= MaxSideSize)
                {
                    break;
                }
                if (m is not PlayerBot bot || bot == center ||
                    side.Contains(bot) || otherSide.Contains(bot) ||
                    !IsDraftable(bot, faction))
                {
                    continue;
                }
                side.Add(bot);
            }
        }

        // Swap one participant to a fight-ready defender: cooldown stamp,
        // staggered battle cry, and a defender brain that resumes the
        // bot's trip when the fight ends.
        private static void Draft(PlayerBot bot, int cryStagger)
        {
            _botCooldowns[bot.Serial] = Core.Now + BotCooldown;

            if (cryStagger <= 1)
            {
                BattleCry(bot);
            }
            else
            {
                var crier = bot;
                Timer.DelayCall(TimeSpan.FromSeconds(0.4 * cryStagger), () =>
                {
                    if (!crier.Deleted && crier.Alive)
                    {
                        BattleCry(crier);
                    }
                });
            }

            var dest = (bot.Behavior as TravelerBehavior)?.DestinationName;
            bot.Behavior = new AdventurerBehavior
            {
                DefenderMode = true,
                DefenderRetreatHpFraction = 0.30, // shield pride: fight longer
                ResumeDestination = dest,
            };
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
