// =========================================================================
// BotSessionManager.cs — bots have play sessions, not eternal existence
// (IDEAS 1.1).
//
// Real players log in, play for a few hours, say "gtg dinner", and
// vanish. This manager wraps every bot's life in that session shape:
//
//   - Every bot gets a SessionEndsAt when first seen (1–4h out). When it
//     expires the bot says a goodbye line and logs out (vanishes after a
//     beat). Its spawner refills the slot minutes later — someone ELSE
//     logging in.
//   - A 24-hour POPULATION CURVE (local server clock = the player's own
//     clock) scales the target population: dead at 5am, packed in the
//     evening. Surplus bots get logged out gradually; spawner refills are
//     gated through AllowSpawn() so the population climbs back only as
//     the curve allows.
//   - Fresh spawns during play are "logins": journaled, and sometimes the
//     bot greets the room ("hey all", "anyone on?").
//
// Fixed-role bots (spawn editor) are exempt — the AFK macroer at the
// anvil has been there since 1997 and will be there forever, which is
// exactly right. Partied bots defer logout until the hunt disbands.
//
//   [BotSessions          — status (live / target / curve hour)
//   [BotSessions on|off   — toggle the whole layer
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class BotSessionManager
    {
        // ---- Knobs ----

        public static bool Enabled = true;

        // Session length rolled per login. IDEAS 1.1: "plays for 1–4 hours".
        public static int SessionMinMinutes = 60;
        public static int SessionMaxMinutes = 240;

        // Max logouts started per tick — keeps hour-edge population drops
        // gradual (a trickle of "gtg" instead of a mass exodus).
        private const int MaxLogoutsPerTick = 4;

        // Fraction of TargetCount that should be online at each local hour.
        // Evening peak, deep-night trough — the classic shard daily curve.
        private static readonly double[] HourCurve =
        {
            0.55, 0.40, 0.30, 0.22, 0.18, 0.15,   // 00-05  night collapse
            0.20, 0.30, 0.40, 0.50, 0.55, 0.60,   // 06-11  morning climb
            0.65, 0.65, 0.65, 0.70, 0.75, 0.85,   // 12-17  afternoon
            0.95, 1.00, 1.00, 0.95, 0.85, 0.70,   // 18-23  evening peak
        };

        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

        // ---- Internals ----

        private static Timer _timer;
        private static bool _initialStampDone;
        private static readonly List<PlayerBot> _scratch = new();

        public static double CurveNow => HourCurve[DateTime.Now.Hour];

        public static int TargetNow =>
            Math.Max(1, (int)(BotPopulation.TargetCount * CurveNow));

        public static void Configure()
        {
            _timer = Timer.DelayCall(TickInterval, TickInterval, OnTick);
            CommandSystem.Register("BotSessions", AccessLevel.GameMaster, Status_OnCommand);
        }

        // -------------------------------------------------------------------
        // AllowSpawn — consulted by PlayerBotSpawner before each spawn.
        // Refills are allowed only while the live population sits below the
        // current curve target, which is what gives the shard its daily
        // rhythm on the way UP as well as down.
        //
        // NamePool's claim registry is an exact O(1) live-bot count (every
        // bot claims a name at creation and releases it on delete), so this
        // stays accurate even inside the startup respawn loop where 400+
        // spawns land in a single call stack — a time-based count cache
        // would go stale there and let the whole population overshoot the
        // curve.
        // -------------------------------------------------------------------
        public static bool AllowSpawn()
        {
            if (!Enabled)
            {
                return true;
            }
            return NamePool.InUseCount < TargetNow;
        }

        private static int CountLive()
        {
            int n = 0;
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted && bot.Map != Map.Internal)
                {
                    n++;
                }
            }
            return n;
        }

        private static void OnTick()
        {
            if (!Enabled)
            {
                return;
            }

            _scratch.Clear();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted && bot.Map != Map.Internal)
                {
                    _scratch.Add(bot);
                }
            }

            // First tick after startup: the whole standing population came
            // up at once, but they didn't all just log in. Stamp them with
            // PARTIAL sessions (a random fraction already "played") so
            // logouts spread naturally instead of arriving as one wave
            // hours from now. No hello lines, no login journal spam.
            if (!_initialStampDone)
            {
                foreach (var bot in _scratch)
                {
                    if (!bot.LifecycleExempt && bot.SessionEndsAt == DateTime.MinValue)
                    {
                        var full = RollSessionSeconds();
                        var remaining = full * (0.10 + Utility.RandomDouble() * 0.90);
                        bot.SessionEndsAt = Core.Now + TimeSpan.FromSeconds(remaining);
                    }
                }
                _initialStampDone = true;
                _scratch.Clear();
                return;
            }

            int live = _scratch.Count;
            int target = TargetNow;
            int surplus = live - target;
            int logouts = 0;

            foreach (var bot in _scratch)
            {
                if (bot.Deleted || bot.LifecycleExempt)
                {
                    continue;
                }

                // Fresh spawn since the last tick — a login.
                if (bot.SessionEndsAt == DateTime.MinValue)
                {
                    bot.SessionEndsAt = Core.Now + TimeSpan.FromSeconds(RollSessionSeconds());
                    BotEventJournal.Record("login", bot);
                    MaybeSayHello(bot);
                    continue;
                }

                if (logouts >= MaxLogoutsPerTick)
                {
                    continue;
                }

                bool due = Core.Now >= bot.SessionEndsAt;

                // Over the curve target: nudge the soonest-to-leave out
                // early. 20 minutes of "leaving soon anyway" is close
                // enough that pulling it forward reads natural.
                bool curvePressure = surplus > 0 &&
                    bot.SessionEndsAt - Core.Now < TimeSpan.FromMinutes(20);

                if ((due || curvePressure) && CanLogoutNow(bot))
                {
                    BeginLogout(bot);
                    logouts++;
                    if (curvePressure && !due)
                    {
                        surplus--;
                    }
                }
            }

            _scratch.Clear();
        }

        private static double RollSessionSeconds() =>
            Utility.RandomMinMax(SessionMinMinutes * 60, SessionMaxMinutes * 60);

        // A bot mid-fight or mid-hunt finishes first; the next tick will
        // catch it once things calm down. Ghosts don't say "gtg", and a
        // bot mid corpse-run finishes the story before logging.
        private static bool CanLogoutNow(PlayerBot bot) =>
            bot.Alive &&
            !bot.LoggingOut &&
            !bot.CorpseRunPending &&
            bot.Behavior is not GhostBehavior and not CorpseReclaimBehavior &&
            bot.Combatant == null &&
            !BotPartyManager.IsInParty(bot);

        private static void BeginLogout(PlayerBot bot)
        {
            bot.LoggingOut = true;

            var line = ChatLibrary.PickRandom("session_goodbye");
            if (!string.IsNullOrEmpty(line))
            {
                bot.Say(line);
            }

            BotEventJournal.Record("logout", bot);

            // A beat between "gtg" and vanishing, like a real logout timer.
            Timer.DelayCall(TimeSpan.FromSeconds(Utility.RandomMinMax(3, 6)), () =>
            {
                if (!bot.Deleted)
                {
                    bot.Delete();
                }
            });
        }

        // A genuine mid-play login sometimes greets the room — but only if
        // someone (the player) is around to see it, a few seconds after
        // appearing, like a client finishing its load.
        private static void MaybeSayHello(PlayerBot bot)
        {
            if (Utility.RandomDouble() > 0.45)
            {
                return;
            }

            Timer.DelayCall(TimeSpan.FromSeconds(Utility.RandomMinMax(4, 15)), () =>
            {
                if (bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
                {
                    return;
                }
                foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 18))
                {
                    if (m is Mobiles.PlayerMobile && m is not PlayerBot)
                    {
                        var line = ChatLibrary.PickRandom("session_hello");
                        if (!string.IsNullOrEmpty(line))
                        {
                            bot.Say(line);
                        }
                        return;
                    }
                }
            });
        }

        [Usage("BotSessions [on|off]")]
        [Description("Shows session-layer status, or toggles it.")]
        private static void Status_OnCommand(CommandEventArgs e)
        {
            if (e.Arguments.Length > 0)
            {
                switch (e.Arguments[0].ToLowerInvariant())
                {
                    case "on":
                        Enabled = true;
                        e.Mobile.SendMessage("Bot sessions: ON.");
                        return;
                    case "off":
                        Enabled = false;
                        e.Mobile.SendMessage("Bot sessions: OFF (population pinned).");
                        return;
                }
            }

            int hour = DateTime.Now.Hour;
            e.Mobile.SendMessage(
                $"Bot sessions: {(Enabled ? "ON" : "OFF")}. Live {CountLive()}, " +
                $"target {TargetNow} (curve {CurveNow:P0} at {hour:00}:00, " +
                $"cap {BotPopulation.TargetCount}).");
        }
    }
}
