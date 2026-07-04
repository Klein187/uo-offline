// =========================================================================
// BotPartySystem.cs — hunting parties (IDEAS 2.2).
//
// The lfg.txt chatter already asks "LFG despise anyone?" — this makes it
// real. Life of a party:
//
//   MUSTERING  A bank-sitting fighter rolls "form group": broadcasts an
//              LFG line naming a real dungeon, and 1-3 compatible bots
//              nearby answer ("me", "inv pls") and converge on him.
//   MARCHING   The leader becomes a Traveler aimed at that dungeon's
//              entrance; members run PartyMemberBehavior (follow the
//              leader, fight what shows up). Partied bots never Recall,
//              Gate, or take moongate shortcuts — the target dungeon is
//              chosen on the leader's own landmass, so the march is a
//              real walk down real roads.
//   ENTERING   The leader steps onto the entrance teleporter and becomes
//              a DungeonCrawler (the normal entry flow). Members walk
//              onto the pad after him; any the pad doesn't catch are
//              ported to the landing spot a beat later, staggered, like
//              a group zoning in one by one.
//   CRAWLING   Members keep following the leader as he crawls. Floor
//              changes read as "everyone took the teleporter" via the
//              same straggler port. When the leader's run timer brings
//              him back to the surface, the party breaks up with
//              goodbyes — and everyone who hunted together becomes
//              FRIENDS in the social graph (they'll greet each other by
//              name at banks for the rest of their lives).
//
// Parties are transient (like bots). The lifecycle manager and session
// manager both leave partied bots alone until the hunt ends.
//
//   [BotParties        — live party status
//   [BotParties form   — force-form a party near you (testing)
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public enum BotPartyState
    {
        Mustering,
        Marching,
        Entering,
        Crawling,
    }

    public sealed class BotParty
    {
        public PlayerBot Leader;
        public readonly List<PlayerBot> Members = new(); // excludes leader
        public BotPartyState State;
        public BotDestination Target;      // the DungeonEntrance destination
        public Point3D EntranceTile;       // surface teleporter pad
        public Point3D LandingSpot;        // leader's first inside position
        public DateTime FormedAt;
        public DateTime StateSince;

        public void SetState(BotPartyState s)
        {
            State = s;
            StateSince = Core.Now;
        }

        public IEnumerable<PlayerBot> Everyone()
        {
            if (Leader != null)
            {
                yield return Leader;
            }
            foreach (var m in Members)
            {
                yield return m;
            }
        }
    }

    public static class BotPartyManager
    {
        // ---- Knobs ----

        public static bool Enabled = true;

        public const int MaxParties = 3;

        // Formation attempts happen this often (each attempt may fail —
        // no eligible leader, nobody answered the LFG).
        private static readonly TimeSpan FormAttemptMin = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan FormAttemptMax = TimeSpan.FromMinutes(8);

        // How far the LFG broadcast recruits from.
        private const int RecruitRange = 20;

        // Straight-line cap on the march — keeps parties watchable and on
        // the leader's own landmass.
        private const int MaxDungeonDistance = 600;

        private static readonly TimeSpan MusterTimeout   = TimeSpan.FromSeconds(75);
        private static readonly TimeSpan MarchTimeout    = TimeSpan.FromMinutes(35);
        private static readonly TimeSpan EnterTimeout    = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PartyMaxLife    = TimeSpan.FromMinutes(75);

        // A member this far from the leader (and not fighting) gets ported
        // to his side — covers teleporter floor-changes and door mishaps.
        private const int StragglerDistance = 30;

        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(8);

        // ---- State ----

        private static Timer _timer;
        private static DateTime _nextFormAttempt = DateTime.MinValue;
        private static readonly List<BotParty> _parties = new();

        public static IReadOnlyList<BotParty> Parties => _parties;

        public static void Configure()
        {
            _timer = Timer.DelayCall(TickInterval, TickInterval, OnTick);
            CommandSystem.Register("BotParties", AccessLevel.GameMaster, Status_OnCommand);
        }

        public static BotParty PartyOf(PlayerBot bot)
        {
            if (bot == null)
            {
                return null;
            }
            for (int i = 0; i < _parties.Count; i++)
            {
                var p = _parties[i];
                if (p.Leader == bot)
                {
                    return p;
                }
                for (int j = 0; j < p.Members.Count; j++)
                {
                    if (p.Members[j] == bot)
                    {
                        return p;
                    }
                }
            }
            return null;
        }

        public static bool IsInParty(PlayerBot bot) => PartyOf(bot) != null;

        // -------------------------------------------------------------------
        // Tick — advance every party, then maybe try to form a new one.
        // -------------------------------------------------------------------
        private static void OnTick()
        {
            if (!Enabled)
            {
                return;
            }

            for (int i = _parties.Count - 1; i >= 0; i--)
            {
                try
                {
                    AdvanceParty(_parties[i]);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[party] advance error: {ex.Message}");
                    _parties.RemoveAt(i);
                }
            }

            if (Core.Now >= _nextFormAttempt)
            {
                _nextFormAttempt = Core.Now + TimeSpan.FromSeconds(
                    Utility.RandomMinMax((int)FormAttemptMin.TotalSeconds,
                                         (int)FormAttemptMax.TotalSeconds));
                if (_parties.Count < MaxParties)
                {
                    TryFormParty(null);
                }
            }
        }

        // -------------------------------------------------------------------
        // Party advancement.
        // -------------------------------------------------------------------
        private static void AdvanceParty(BotParty party)
        {
            // Drop deleted/dead members quietly (they died or logged off —
            // it happens to every group).
            party.Members.RemoveAll(m => m == null || m.Deleted || !m.Alive);

            var leader = party.Leader;
            bool leaderGone = leader == null || leader.Deleted || !leader.Alive;

            if (leaderGone || party.Members.Count == 0 ||
                Core.Now - party.FormedAt > PartyMaxLife)
            {
                Disband(party, sayGoodbyes: !leaderGone);
                return;
            }

            switch (party.State)
            {
                case BotPartyState.Mustering:
                    TickMustering(party);
                    break;
                case BotPartyState.Marching:
                    TickMarching(party);
                    break;
                case BotPartyState.Entering:
                    TickEntering(party);
                    break;
                case BotPartyState.Crawling:
                    TickCrawling(party);
                    break;
            }
        }

        private static void TickMustering(BotParty party)
        {
            var leader = party.Leader;

            bool allClose = true;
            foreach (var m in party.Members)
            {
                if (m.Map != leader.Map || !m.InRange(leader.Location, 6))
                {
                    allClose = false;
                    break;
                }
            }

            if (!allClose && Core.Now - party.StateSince < MusterTimeout)
            {
                return;
            }

            // Set out. The leader becomes a Traveler aimed at the dungeon
            // entrance; TravelerBehavior's normal entry flow (walk onto the
            // real Teleporter) takes it from there. Party membership makes
            // the Traveler skip Recall/Gate/moongate shortcuts.
            SayScene(leader, "party_depart", party);
            BotEventJournal.Record("party", leader, party.Target.Dungeon);

            leader.Behavior = new TravelerBehavior
            {
                DestinationName = party.Target.Name,
            };
            party.SetState(BotPartyState.Marching);
        }

        private static void TickMarching(BotParty party)
        {
            var leader = party.Leader;

            if (DungeonRegistry.IsInDungeon(leader))
            {
                party.LandingSpot = leader.Location;
                party.SetState(BotPartyState.Entering);
                return;
            }

            PortStragglers(party);

            // Leader wandered off-plan? (entrance pad dead → Traveler picked
            // a new destination; MAROONED rescue re-aimed the trip; etc.)
            // A hunt whose leader is now walking to a tailor shop is over.
            if (Core.Now - party.StateSince > TimeSpan.FromSeconds(60) &&
                leader.Behavior is TravelerBehavior t &&
                !string.Equals(t.DestinationName, party.Target.Name, StringComparison.OrdinalIgnoreCase))
            {
                Disband(party, sayGoodbyes: true);
                return;
            }

            if (Core.Now - party.StateSince > MarchTimeout)
            {
                Disband(party, sayGoodbyes: true);
            }
        }

        private static void TickEntering(BotParty party)
        {
            var leader = party.Leader;

            // Leader popped straight back out (unmapped landing reverts to
            // Traveler) — call it off.
            if (!DungeonRegistry.IsInDungeon(leader))
            {
                Disband(party, sayGoodbyes: true);
                return;
            }

            bool allInside = true;
            int stagger = 0;
            foreach (var m in party.Members)
            {
                if (DungeonRegistry.IsInDungeon(m))
                {
                    continue;
                }
                allInside = false;

                // Near the pad (its own walk-on may still fire) or timed
                // out — port them in, staggered, like a group zoning.
                bool nearPad = m.InRange(party.EntranceTile, 4);
                bool overdue = Core.Now - party.StateSince > EnterTimeout;
                if ((nearPad || overdue) && m.Combatant == null)
                {
                    var member = m;
                    var spot = Jitter(party.LandingSpot, 2);
                    var map = leader.Map;
                    Timer.DelayCall(TimeSpan.FromSeconds(0.8 + stagger * 1.3), () =>
                    {
                        if (!member.Deleted && member.Alive &&
                            !DungeonRegistry.IsInDungeon(member))
                        {
                            member.MoveToWorld(spot, map);
                        }
                    });
                    stagger++;
                }
            }

            if (allInside)
            {
                party.SetState(BotPartyState.Crawling);
            }
        }

        private static void TickCrawling(BotParty party)
        {
            var leader = party.Leader;

            // Leader surfaced — run over, timer expired, climb-out done.
            // The hunt is over; break up on the surface.
            if (!DungeonRegistry.IsInDungeon(leader))
            {
                Disband(party, sayGoodbyes: true);
                return;
            }

            PortStragglers(party);
        }

        // Members too far behind (and not mid-fight) appear at the leader's
        // side. Overland this covers doors/rivers; in dungeons it's how the
        // whole party "takes the teleporter" the leader just used.
        private static void PortStragglers(BotParty party)
        {
            var leader = party.Leader;
            int stagger = 0;
            foreach (var m in party.Members)
            {
                if (m.Combatant != null)
                {
                    continue;
                }
                bool far = m.Map != leader.Map ||
                           !m.InRange(leader.Location, StragglerDistance);
                if (!far)
                {
                    continue;
                }

                var member = m;
                var spot = Jitter(leader.Location, 2);
                var map = leader.Map;
                Timer.DelayCall(TimeSpan.FromSeconds(0.5 + stagger * 1.1), () =>
                {
                    if (member.Deleted || !member.Alive || member.Combatant != null)
                    {
                        return;
                    }
                    var p = PartyOf(member);
                    if (p == party) // still in this party
                    {
                        member.MoveToWorld(spot, map);
                    }
                });
                stagger++;
            }
        }

        private static Point3D Jitter(Point3D p, int radius) =>
            new(p.X + Utility.RandomMinMax(-radius, radius),
                p.Y + Utility.RandomMinMax(-radius, radius),
                p.Z);

        // -------------------------------------------------------------------
        // Formation.
        // -------------------------------------------------------------------

        // Classes that can lead a hunt / get recruited into one.
        private static bool IsFighter(BotClass c) =>
            c is BotClass.Warrior or BotClass.Mage or BotClass.Fencer
              or BotClass.Archer or BotClass.Ranger;

        private static bool IsRecruitable(BotClass c) =>
            IsFighter(c) || c is BotClass.Healer or BotClass.Bard or BotClass.Tamer;

        private static bool IsEligible(PlayerBot bot) =>
            bot != null && !bot.Deleted && bot.Alive &&
            !bot.LifecycleExempt && !bot.LoggingOut &&
            bot.Combatant == null &&
            !DungeonRegistry.IsInDungeon(bot) &&
            !IsInParty(bot);

        // Try to form one party. anchor = null picks a random bank-sitting
        // leader anywhere; a non-null anchor (the [BotParties form command)
        // prefers leaders near that spot.
        public static BotParty TryFormParty(Point3D? anchor)
        {
            // 1. Leader: a bank-sitting fighter with some experience.
            var candidates = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot &&
                    IsEligible(bot) &&
                    bot.Behavior is BankSitterBehavior &&
                    IsFighter(bot.Class) &&
                    bot.SkillTier >= BotSkillTier.Apprentice)
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
            if (candidates.Count == 0)
            {
                return null;
            }
            var leader = candidates[Utility.Random(candidates.Count)];

            // 2. Target dungeon: an entrance on the leader's own landmass,
            //    close enough to march to.
            var target = PickDungeonFor(leader);
            if (target == null)
            {
                return null;
            }

            // 3. Recruits: compatible bots in LFG earshot, friends first.
            var recruits = new List<PlayerBot>();
            foreach (var m in leader.Map.GetMobilesInRange(leader.Location, RecruitRange))
            {
                if (m is PlayerBot bot && bot != leader &&
                    IsEligible(bot) &&
                    IsRecruitable(bot.Class) &&
                    Math.Abs((int)bot.SkillTier - (int)leader.SkillTier) <= 2 &&
                    bot.Behavior is BankSitterBehavior or IdleBehavior or WanderBehavior)
                {
                    recruits.Add(bot);
                }
            }
            if (recruits.Count == 0)
            {
                return null; // nobody answered the LFG — no party today
            }

            // Friends first, then guildmates (IDEAS 2.1 phase 2: guilds
            // prefer their own for groups), then everyone else.
            int Affinity(PlayerBot r) =>
                BotSocialGraph.AreFriends(leader, r) ? 2
                : leader.BotGuildIndex >= 0 && r.BotGuildIndex == leader.BotGuildIndex ? 1
                : 0;
            recruits.Sort((a, b) => Affinity(b).CompareTo(Affinity(a)));

            int take = Math.Min(recruits.Count, Utility.RandomMinMax(1, 3));

            var party = new BotParty
            {
                Leader       = leader,
                Target       = target,
                EntranceTile = target.Location,
                FormedAt     = Core.Now,
            };
            party.SetState(BotPartyState.Mustering);

            // 4. Theater: the LFG broadcast, then answers as each joins.
            SayScene(leader, "party_recruit", party);

            for (int i = 0; i < take; i++)
            {
                var member = recruits[i];
                party.Members.Add(member);

                var mm = member;
                Timer.DelayCall(TimeSpan.FromSeconds(1.5 + i * 1.8), () =>
                {
                    if (!mm.Deleted && PartyOf(mm) == party)
                    {
                        SayScene(mm, "party_join", party);
                    }
                });
                member.Behavior = new PartyMemberBehavior();
            }

            _parties.Add(party);
            Console.WriteLine(
                $"[party] {leader.Name} formed a party of {take + 1} " +
                $"for {target.Dungeon} ({target.Name})");
            return party;
        }

        // Nearest few dungeon entrances on the leader's landmass; random
        // among them so Despise doesn't get every single party.
        private static BotDestination PickDungeonFor(PlayerBot leader)
        {
            var graph = WaypointRegistry.Graph;
            var leaderNode = graph.FindNearestNode(leader.Location);
            int leaderComp = leaderNode != null ? graph.ComponentOf(leaderNode.Name) : -1;

            var options = new List<(BotDestination d, int dist)>();
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.DungeonEntrance ||
                    string.IsNullOrEmpty(d.Dungeon))
                {
                    continue;
                }

                int dist = Math.Max(Math.Abs(d.Location.X - leader.X),
                                    Math.Abs(d.Location.Y - leader.Y));
                if (dist > MaxDungeonDistance)
                {
                    continue;
                }

                // Same walkable component when both sides are known — a
                // party never marches at an entrance across the ocean.
                if (leaderComp >= 0)
                {
                    var wp = d.NearestWaypoint;
                    if (string.IsNullOrEmpty(wp) || graph.Get(wp) == null)
                    {
                        wp = graph.FindNearestNode(d.Location)?.Name;
                    }
                    if (wp != null && graph.ComponentOf(wp) != leaderComp)
                    {
                        continue;
                    }
                }

                options.Add((d, dist));
            }

            if (options.Count == 0)
            {
                return null;
            }

            options.Sort((a, b) => a.dist.CompareTo(b.dist));
            int pool = Math.Min(options.Count, 3);
            return options[Utility.Random(pool)].d;
        }

        // -------------------------------------------------------------------
        // Disband — goodbyes, friendships, and back to ordinary life.
        // -------------------------------------------------------------------
        private static void Disband(BotParty party, bool sayGoodbyes)
        {
            _parties.Remove(party);

            // Everyone who hunted together is friends now — they'll greet
            // each other by name at banks from here on.
            var everyone = new List<PlayerBot>();
            foreach (var b in party.Everyone())
            {
                if (b != null && !b.Deleted)
                {
                    everyone.Add(b);
                }
            }
            for (int i = 0; i < everyone.Count; i++)
            {
                for (int j = i + 1; j < everyone.Count; j++)
                {
                    BotSocialGraph.MakeFriends(everyone[i], everyone[j]);
                }
            }

            int stagger = 0;
            foreach (var bot in everyone)
            {
                if (sayGoodbyes && bot.Alive)
                {
                    var b = bot;
                    Timer.DelayCall(TimeSpan.FromSeconds(0.5 + stagger * 1.4), () =>
                    {
                        if (!b.Deleted && b.Alive)
                        {
                            var line = ChatLibrary.PickRandom("party_disband");
                            if (!string.IsNullOrEmpty(line))
                            {
                                b.Say(line);
                            }
                        }
                    });
                    stagger++;
                }

                // Members go back to ordinary life. Inside a dungeon that
                // means becoming a crawler (finish the dive, climb out on
                // the normal exit reflex); outside, a fresh Traveler. The
                // leader keeps whatever behavior it's already running.
                if (bot != party.Leader && bot.Behavior is PartyMemberBehavior)
                {
                    bot.Behavior = DungeonRegistry.IsInDungeon(bot)
                        ? new DungeonCrawlerBehavior()
                        : BehaviorRegistry.Create("Traveler");
                }
            }

            Console.WriteLine($"[party] disbanded ({party.Target?.Dungeon}, " +
                              $"{everyone.Count} bots, state {party.State})");
        }

        // Speak a party-scene line, substituting the dungeon name. These
        // are theater beats, so they speak regardless of player presence
        // (unlike ambient chatter) — the cost of an unseen Say is nil.
        private static void SayScene(PlayerBot bot, string category, BotParty party)
        {
            var line = ChatLibrary.PickRandom(category);
            if (string.IsNullOrEmpty(line))
            {
                return;
            }
            var dungeon = party.Target?.Dungeon ?? "the dungeon";
            bot.Say(line.Replace("{dungeon}", dungeon.ToLowerInvariant(), StringComparison.Ordinal));
        }

        // -------------------------------------------------------------------
        [Usage("BotParties [form]")]
        [Description("Lists live hunting parties, or force-forms one near you.")]
        private static void Status_OnCommand(CommandEventArgs e)
        {
            if (e.Arguments.Length > 0 &&
                string.Equals(e.Arguments[0], "form", StringComparison.OrdinalIgnoreCase))
            {
                var formed = TryFormParty(e.Mobile.Location) ?? TryFormParty(null);
                e.Mobile.SendMessage(formed != null
                    ? $"Formed: {formed.Leader.Name} + {formed.Members.Count} " +
                      $"→ {formed.Target.Dungeon}"
                    : "No eligible leader/recruits right now (need bank-sitting fighters).");
                return;
            }

            if (_parties.Count == 0)
            {
                e.Mobile.SendMessage("No live parties.");
                return;
            }
            foreach (var p in _parties)
            {
                e.Mobile.SendMessage(
                    $"{p.Leader?.Name ?? "?"} +{p.Members.Count} → {p.Target?.Dungeon} " +
                    $"[{p.State}] age {(int)(Core.Now - p.FormedAt).TotalMinutes}m " +
                    $"at ({p.Leader?.X},{p.Leader?.Y})");
            }
        }
    }
}
