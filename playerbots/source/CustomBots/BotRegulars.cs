// =========================================================================
// BotRegulars.cs — some bots come back.
//
// A plain bot plays one session and is gone for good: its spawner refills
// the slot with somebody new. That is fine for a crowd, but nothing about
// a bot can last longer than a few hours — not a bank balance, not a
// house. Regulars fix that. A regular is a bot that logs out the way a
// player does and logs back in later with the same name, skills, gear and
// bank box.
//
//   - A bot becomes a regular at the end of a session, by chance, up to a
//     cap. From then on it belongs to no spawner (the slot refills).
//   - Logging out parks it on the internal map, which is what the engine
//     does with a player who logs out. It stays in the world save.
//   - While it is away it keeps LoggingOut set and an idle brain. Every
//     party, duel, hunt and shop picker already skips a bot that is
//     logging out, so nothing drafts a bot that is not there.
//   - When the population has room, the session manager brings a regular
//     back before the spawners add a stranger. It logs in where it logged
//     out (or at home, once regulars own houses).
//   - A restart parks every player on the internal map, regulars included.
//     They come back over the next minutes the same way.
//
//   [Regulars            — roster: online, offline, cap
//   regulars_request.txt — headless test: "token make N" turns N live bots
//                          into regulars and logs them out now; "token
//                          login N" brings N back now. Ack:
//                          regulars_ack.json.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using Server.Commands;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotRegulars
    {
        // ---- Knobs ----

        public static bool Enabled = true;

        // Share of ordinary logouts that turn the bot into a regular.
        private const double BecomeRegularChance = 0.25;

        // Regulars at most, online and offline together. A world with too
        // many would see the same faces all day.
        public static int MaxRegulars => Math.Max(50, BotPopulation.TargetCount / 8);

        // A player who just said "gtg" is not back two minutes later.
        private static readonly TimeSpan MinOffline = TimeSpan.FromMinutes(20);

        private const int MaxLoginsPerTick = 3;

        // ---- State ----

        private static readonly HashSet<PlayerBot> _offline = new();
        private static int _total;

        public static int OfflineCount => _offline.Count;
        public static int TotalCount => _total;

        public static void Initialize()
        {
            CommandSystem.Register("Regulars", AccessLevel.GameMaster, e =>
            {
                e.Mobile.SendMessage($"Regulars: {_total - _offline.Count} online, " +
                    $"{_offline.Count} offline, cap {MaxRegulars}.");
            });

            _lastToken = ReadToken(out _, out _) ?? 0;
            Timer.DelayCall(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), PollRequest);
        }

        // -------------------------------------------------------------------
        // Boot: the engine has already parked every player on the internal
        // map. A regular stays there until the session manager logs it in.
        // Called by BotStartupManager's purge, which would otherwise delete
        // it as a stale bot.
        // -------------------------------------------------------------------
        public static void AdoptAtBoot(PlayerBot bot)
        {
            _total++;
            ParkOffline(bot);
        }

        // A bot made in code to be a regular (a seeded homeowner). It starts
        // logged out at loc, as if it had logged out there a while ago.
        public static void AdoptNew(PlayerBot bot, Point3D loc, Map map)
        {
            bot.Regular = true;
            _total++;
            bot.LogoutLocation = loc;
            bot.LogoutMap = map;
            if (bot.Map != Map.Internal)
            {
                bot.Internalize();
            }
            bot.OfflineSince = Core.Now - TimeSpan.FromMinutes(Utility.RandomMinMax(0, 20));
            ParkOffline(bot);
        }

        // -------------------------------------------------------------------
        // End of a session. True = the bot stays (as a regular, now offline)
        // and must not be deleted.
        // -------------------------------------------------------------------
        public static bool TryKeep(PlayerBot bot)
        {
            if (!Enabled || bot.Deleted)
            {
                return false;
            }

            if (!bot.Regular)
            {
                if (!CanBecomeRegular(bot) || _total >= MaxRegulars ||
                    Utility.RandomDouble() >= BecomeRegularChance)
                {
                    return false;
                }

                MakeRegular(bot);
            }

            GoOffline(bot);
            return true;
        }

        private static bool CanBecomeRegular(PlayerBot bot) =>
            bot.Alive && !bot.IsPermanent && !bot.LifecycleExempt &&
            bot.Behavior is not PKBehavior &&
            bot.Map != null && bot.Map != Map.Internal;

        private static void MakeRegular(PlayerBot bot)
        {
            bot.Regular = true;
            _total++;

            // Out of the spawner's books: the spawner refills the slot with
            // somebody new, and this one comes and goes on its own.
            if (bot.Spawner != null)
            {
                try { bot.Spawner.Remove(bot); } catch { }
                bot.Spawner = null;
            }
        }

        private static void GoOffline(PlayerBot bot)
        {
            // Followers do not wait on the internal map: a pack beast or a
            // war pet is stabled, as a player's would be.
            BotPackAnimals.Release(bot);
            BotCombatPets.Release(bot);

            bot.Combatant = null;
            bot.Warmode = false;
            bot.LogoutLocation = bot.Location;
            bot.LogoutMap = bot.Map;
            bot.OfflineSince = Core.Now;
            bot.Internalize();
            ParkOffline(bot);
        }

        private static void ParkOffline(PlayerBot bot)
        {
            bot.LoggingOut = true;
            bot.Behavior = BehaviorRegistry.Create("Idle");
            if (bot.OfflineSince == DateTime.MinValue)
            {
                bot.OfflineSince = Core.Now;
            }
            _offline.Add(bot);
        }

        // -------------------------------------------------------------------
        // Login. The session manager calls this each tick with how many
        // more bots the curve wants online; regulars get first claim on
        // those places. Returns how many came back.
        // -------------------------------------------------------------------
        public static int LogInSome(int wanted, bool ignoreMinOffline = false)
        {
            if (!Enabled || wanted <= 0 || _offline.Count == 0)
            {
                return 0;
            }

            if (!ignoreMinOffline)
            {
                wanted = Math.Min(wanted, MaxLoginsPerTick);
            }

            var ready = new List<PlayerBot>();
            foreach (var bot in _offline)
            {
                if (bot.Deleted)
                {
                    continue;
                }
                if (ignoreMinOffline || Core.Now - bot.OfflineSince >= MinOffline)
                {
                    ready.Add(bot);
                }
            }

            int n = 0;
            while (n < wanted && ready.Count > 0)
            {
                int i = Utility.Random(ready.Count);
                var bot = ready[i];
                ready.RemoveAt(i);
                if (LogIn(bot))
                {
                    n++;
                }
            }
            return n;
        }

        public static bool LogInNow(PlayerBot bot) =>
            _offline.Contains(bot) && LogIn(bot);

        private static bool LogIn(PlayerBot bot)
        {
            _offline.Remove(bot);

            var map = bot.LogoutMap;
            var loc = bot.LogoutLocation;
            if (map == null || map == Map.Internal || loc == Point3D.Zero)
            {
                map = Map.Felucca;
                loc = new Point3D(1434, 1699, map.GetAverageZ(1434, 1699));
            }

            if (!bot.Alive)
            {
                bot.Resurrect();
            }
            bot.Hits = bot.HitsMax;
            bot.Stam = bot.StamMax;
            bot.Mana = bot.ManaMax;

            bot.LoggingOut = false;
            bot.OfflineSince = DateTime.MinValue;

            // The session manager stamps a fresh session on its next tick
            // and journals it as a login, like any other arrival.
            bot.SessionEndsAt = DateTime.MinValue;

            bot.MoveToWorld(loc, map);

            // Saved before home towns were saved: the town it logs in near.
            if (string.IsNullOrEmpty(bot.HomeCity))
            {
                bot.HomeCity = BotHangouts.TownNear(loc) ?? BotHomeCities.RollHome();
            }

            // Logged in at home: start the day in the house, and leave by
            // the front door like an owner.
            var home = BotHomes.HomeOf(bot);
            bot.Behavior = home != null && home.IsInside(bot)
                ? new HomeVisitBehavior(home)
                : BehaviorRegistry.Create("Traveler");
            Console.WriteLine($"[regulars] {bot.Name} logged back in at {loc}, bank {Banker.GetBalance(bot)}");
            return true;
        }

        // Regulars who have been away long enough to come back. The
        // spawners hold off while there are any, so a familiar face gets the
        // free place on the next session tick instead of a stranger.
        public static bool AnyReady()
        {
            if (!Enabled)
            {
                return false;
            }
            foreach (var bot in _offline)
            {
                if (!bot.Deleted && Core.Now - bot.OfflineSince >= MinOffline)
                {
                    return true;
                }
            }
            return false;
        }

        // Called from PlayerBot.OnAfterDelete.
        public static void Forget(PlayerBot bot)
        {
            if (bot.Regular)
            {
                _total = Math.Max(0, _total - 1);
            }
            _offline.Remove(bot);
        }

        // -------------------------------------------------------------------
        // Headless test hook.
        // -------------------------------------------------------------------
        private static long _lastToken;
        private static Dictionary<PlayerBot, int> _wealth = new();
        private static DateTime _wealthAt = DateTime.MinValue;

        private static string RequestFile =>
            Path.Combine(Core.BaseDirectory, "Data", "Live", "regulars_request.txt");

        private static string AckFile =>
            Path.Combine(Core.BaseDirectory, "Data", "Live", "regulars_ack.json");

        private static long? ReadToken(out string verb, out int count)
        {
            verb = null;
            count = 0;
            try
            {
                if (!File.Exists(RequestFile))
                {
                    return null;
                }
                var parts = File.ReadAllText(RequestFile).Trim().Split(' ',
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !long.TryParse(parts[0], out var token))
                {
                    return null;
                }
                verb = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;
                count = parts.Length > 2 && int.TryParse(parts[2], out var c) ? c : 1;
                return token;
            }
            catch
            {
                return null;
            }
        }

        private static void PollRequest()
        {
            var token = ReadToken(out var verb, out var count);
            if (token == null || token.Value == _lastToken)
            {
                return;
            }
            _lastToken = token.Value;

            var names = new List<string>();
            if (verb == "make")
            {
                var picks = new List<PlayerBot>();
                foreach (var m in World.Mobiles.Values)
                {
                    if (m is PlayerBot b && !b.Deleted && !b.Regular && !b.LoggingOut &&
                        CanBecomeRegular(b) && b.Combatant == null)
                    {
                        picks.Add(b);
                    }
                }
                for (int i = 0; i < count && picks.Count > 0; i++)
                {
                    int j = Utility.Random(picks.Count);
                    var b = picks[j];
                    picks.RemoveAt(j);
                    MakeRegular(b);
                    GoOffline(b);
                    names.Add(b.Name);
                    Console.WriteLine($"[regulars] TEST {b.Name} made a regular, gold in bank " +
                        $"{Banker.GetBalance(b)}, offline");
                }
            }
            else if (verb == "wealth")
            {
                // Pack gold + bank for every online bot. The second call
                // reports what the bots seen both times gained, by class:
                // what bots actually earn per hour.
                var now = new Dictionary<PlayerBot, int>();
                foreach (var m in World.Mobiles.Values)
                {
                    if (m is PlayerBot b && !b.Deleted && b.Map != Map.Internal && !b.LifecycleExempt)
                    {
                        now[b] = BotBanking.Wealth(b);
                    }
                }

                if (_wealthAt == DateTime.MinValue)
                {
                    names.Add($"snapshot of {now.Count} bots");
                }
                else
                {
                    double hours = Math.Max(0.01, (Core.Now - _wealthAt).TotalHours);
                    var sum = new SortedDictionary<string, (int n, long gain, int best)>();
                    foreach (var (b, g) in now)
                    {
                        if (!_wealth.TryGetValue(b, out var g0))
                        {
                            continue;
                        }
                        var k = b.Class.ToString();
                        var cur = sum.GetValueOrDefault(k);
                        sum[k] = (cur.n + 1, cur.gain + (g - g0), Math.Max(cur.best, g - g0));
                    }
                    foreach (var (k, v) in sum)
                    {
                        Console.WriteLine($"[regulars] EARN {k}: {v.n} bots, avg {v.gain / Math.Max(1, v.n) / hours:0}/h, " +
                            $"best {v.best / hours:0}/h");
                    }
                    names.Add($"{hours * 60:0} min");
                }
                _wealth = now;
                _wealthAt = Core.Now;
            }
            else if (verb == "login")
            {
                int before = _offline.Count;
                LogInSome(count, ignoreMinOffline: true);
                names.Add($"{before - _offline.Count} logged in");
            }

            var line = $"{{\"token\":{token.Value},\"verb\":\"{verb}\",\"online\":{_total - _offline.Count}," +
                $"\"offline\":{_offline.Count},\"names\":\"{string.Join(", ", names).Replace("\"", "'")}\"}}";
            try { File.WriteAllText(AckFile, line); } catch { }
            Console.WriteLine($"[regulars] RESULT {line}");
        }
    }
}
