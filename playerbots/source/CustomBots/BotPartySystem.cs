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
// BEYOND HUNTS — two lighter party kinds reuse the same muster/march/
// straggler/disband machinery (IDEAS 2.1: guilds and factions act like
// groups, not name tags):
//
//   CONVOY   A guilded bot already on the road musters 1-3 guildmates
//            who walk the trip WITH it — the visible "guild crew on the
//            road" image. At the destination the group dissolves into
//            the place: every member finishes the last stretch on its
//            own feet and does its own arrival thing.
//   WARBAND  An Order/Chaos fighter musters 1-3 faction-mates and
//            patrols to a faction-flavored spot (Order: shrines, banks,
//            city squares; Chaos: graveyards, crossroads, bridges).
//            When an enemy war band is live, new bands usually aim at
//            ITS patrol target — collision courses that end in the
//            group battles BotFactionWar stages (war bands dissolve
//            into the fight when it starts).
//
//   [BotParties           — live party status (all kinds)
//   [BotParties form      — force-form a hunting party (testing)
//   [BotParties convoy    — force-form a guild convoy (testing)
//   [BotParties warband   — force-form a faction war band (testing)
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

    public enum BotPartyKind
    {
        Hunt,      // the classic dungeon dive (muster→march→enter→crawl)
        Convoy,    // guildmates walking a road trip together
        Warband,   // an Order/Chaos squad on patrol
    }

    public sealed class BotParty
    {
        public BotPartyKind Kind;
        public PlayerBot Leader;
        public readonly List<PlayerBot> Members = new(); // excludes leader
        public BotPartyState State;
        public BotDestination Target;      // hunt: the DungeonEntrance; convoy/warband: the trip target
        public BotFaction Faction;         // warbands only
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

        public const int MaxParties = 3;   // hunts
        public const int MaxConvoys = 3;
        public const int MaxWarbands = 2;

        // Formation attempts happen this often (each attempt may fail —
        // no eligible leader, nobody answered the LFG).
        private static readonly TimeSpan FormAttemptMin = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan FormAttemptMax = TimeSpan.FromMinutes(8);

        // Convoys form a bit more readily (they're cheap, short outings);
        // war bands are rarer — a patrol should feel like an event.
        private static readonly TimeSpan ConvoyAttemptMin = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan ConvoyAttemptMax = TimeSpan.FromMinutes(7);
        private static readonly TimeSpan WarbandAttemptMin = TimeSpan.FromMinutes(6);
        private static readonly TimeSpan WarbandAttemptMax = TimeSpan.FromMinutes(15);

        // How far the LFG broadcast recruits from.
        private const int RecruitRange = 20;

        // Guildmates answer a hunt LFG from farther across the plaza —
        // the guild grapevine carries better than a shout.
        private const int GuildRecruitRange = 40;

        // Convoy/warband muster radius.
        private const int RoamRecruitRange = 30;

        // A convoy needs a real march ahead of it — no two-street escorts —
        // but not a cross-continent trek either: a group walk should ARRIVE
        // inside the march window, not dissolve on a timeout mid-road.
        private const int ConvoyMinTripDistance = 80;
        private const int ConvoyMaxTripDistance = 600;

        // Warband patrol legs: long enough to be seen on the roads, short
        // enough to resolve within the band's lifetime.
        private const int WarbandMinPatrol = 60;
        private const int WarbandMaxPatrol = 500;

        // Chance a fresh warband aims at a live ENEMY band's patrol target
        // instead of rolling its own — collision courses make clashes.
        private const double WarbandInterceptChance = 0.65;

        // Straight-line cap on the march — keeps parties watchable and on
        // the leader's own landmass.
        private const int MaxDungeonDistance = 600;

        private static readonly TimeSpan MusterTimeout   = TimeSpan.FromSeconds(75);
        private static readonly TimeSpan MarchTimeout    = TimeSpan.FromMinutes(35);
        private static readonly TimeSpan EnterTimeout    = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PartyMaxLife    = TimeSpan.FromMinutes(75);

        // Roam kinds are lighter commitments — the walk IS the event.
        private static readonly TimeSpan RoamMarchTimeout = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan ConvoyMaxLife    = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan WarbandMaxLife   = TimeSpan.FromMinutes(25);

        // Close enough to the roam target to call the outing arrived.
        private const int RoamArriveRange = 12;

        // A member this far from the leader (and not fighting) gets ported
        // to his side — covers teleporter floor-changes and door mishaps.
        private const int StragglerDistance = 30;

        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(8);

        // ---- State ----

        private static Timer _timer;
        private static DateTime _nextFormAttempt = DateTime.MinValue;
        private static DateTime _nextConvoyAttempt = DateTime.MinValue;
        private static DateTime _nextWarbandAttempt = DateTime.MinValue;
        private static readonly List<BotParty> _parties = new();

        public static IReadOnlyList<BotParty> Parties => _parties;

        public static int CountKind(BotPartyKind kind)
        {
            int n = 0;
            for (int i = 0; i < _parties.Count; i++)
            {
                if (_parties[i].Kind == kind)
                {
                    n++;
                }
            }
            return n;
        }

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
                if (CountKind(BotPartyKind.Hunt) < MaxParties)
                {
                    TryFormParty(null);
                }
            }

            if (Core.Now >= _nextConvoyAttempt)
            {
                _nextConvoyAttempt = Core.Now + TimeSpan.FromSeconds(
                    Utility.RandomMinMax((int)ConvoyAttemptMin.TotalSeconds,
                                         (int)ConvoyAttemptMax.TotalSeconds));
                if (CountKind(BotPartyKind.Convoy) < MaxConvoys)
                {
                    TryFormConvoy();
                }
            }

            if (Core.Now >= _nextWarbandAttempt)
            {
                _nextWarbandAttempt = Core.Now + TimeSpan.FromSeconds(
                    Utility.RandomMinMax((int)WarbandAttemptMin.TotalSeconds,
                                         (int)WarbandAttemptMax.TotalSeconds));
                if (CountKind(BotPartyKind.Warband) < MaxWarbands)
                {
                    TryFormWarband();
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

            var maxLife = party.Kind switch
            {
                BotPartyKind.Convoy  => ConvoyMaxLife,
                BotPartyKind.Warband => WarbandMaxLife,
                _                    => PartyMaxLife,
            };
            if (leaderGone || party.Members.Count == 0 ||
                Core.Now - party.FormedAt > maxLife)
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

            // Set out. The leader becomes a Traveler aimed at the target;
            // TravelerBehavior's normal flow takes it from there (for a
            // hunt that ends in the walk-onto-the-real-Teleporter entry).
            // Party membership makes the Traveler skip Recall/Gate/
            // moongate shortcuts, so the march is a real walk.
            switch (party.Kind)
            {
                case BotPartyKind.Convoy:
                    SayScene(leader, "guild_convoy_depart", party);
                    BotEventJournal.Record("convoy", leader, party.Target.Name);
                    break;
                case BotPartyKind.Warband:
                    SayScene(leader, "warband_depart", party);
                    BotEventJournal.Record("warband", leader, party.Target.Name);
                    break;
                default:
                    SayScene(leader, "party_depart", party);
                    BotEventJournal.Record("party", leader, party.Target.Dungeon);
                    break;
            }

            leader.Behavior = new TravelerBehavior
            {
                DestinationName = party.Target.Name,
            };
            party.SetState(BotPartyState.Marching);
        }

        private static void TickMarching(BotParty party)
        {
            if (party.Kind != BotPartyKind.Hunt)
            {
                TickRoamMarching(party);
                return;
            }

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

        // Convoy/warband march: walk together until the target is reached,
        // then dissolve into the destination. Combat interludes are
        // TOLERATED — a leader mid-fight is a defender that resumes the
        // same trip, and members fight beside it (Adventurer combat
        // preempts the follow) — that's the point of traveling in a group.
        private static void TickRoamMarching(BotParty party)
        {
            var leader = party.Leader;

            PortStragglers(party);

            // Arrived — close enough that the group has visibly walked in
            // together. Everyone disperses on their own feet from here.
            if (party.Target != null && leader.Map != null &&
                leader.InRange(party.Target.Location, RoamArriveRange))
            {
                Disband(party, sayGoodbyes: true);
                return;
            }

            // Leader handed off to a visit behavior (BankSitter, Shopper,
            // Visitor...) — it has arrived even if the record's coord sits
            // deeper inside the building. Defender swaps (roadside fights)
            // are AdventurerBehavior and are tolerated: the fight resumes
            // the same trip when it ends.
            if (leader.Behavior is not TravelerBehavior and not AdventurerBehavior)
            {
                Disband(party, sayGoodbyes: true);
                return;
            }

            // The leader's trip is no longer OUR trip (gave up, got
            // rescued, rerolled) — the outing dissolves.
            if (Core.Now - party.StateSince > TimeSpan.FromSeconds(60) &&
                leader.Behavior is TravelerBehavior t &&
                !string.Equals(t.DestinationName, party.Target?.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                Disband(party, sayGoodbyes: true);
                return;
            }

            if (Core.Now - party.StateSince > RoamMarchTimeout)
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

            // Guildmates hear the LFG from farther across the plaza — the
            // guild grapevine carries better than a shout. This (plus the
            // affinity sort below) is what makes guild crews hunt together
            // rather than with whoever happened to stand closest.
            if (leader.BotGuildIndex >= 0)
            {
                foreach (var m in leader.Map.GetMobilesInRange(leader.Location, GuildRecruitRange))
                {
                    if (m is PlayerBot bot && bot != leader &&
                        bot.BotGuildIndex == leader.BotGuildIndex &&
                        !recruits.Contains(bot) &&
                        IsEligible(bot) &&
                        IsRecruitable(bot.Class) &&
                        Math.Abs((int)bot.SkillTier - (int)leader.SkillTier) <= 2 &&
                        bot.Behavior is BankSitterBehavior or IdleBehavior or WanderBehavior)
                    {
                        recruits.Add(bot);
                    }
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
        // Convoy formation — guildmates walking a road trip together.
        // -------------------------------------------------------------------

        // Destination types a convoy/warband never marches at: portals
        // teleport the leader away mid-march, and dungeon entrances turn
        // it into a crawler (dungeon groups are the HUNT system's job).
        private static bool IsRoamableTarget(BotDestination d) =>
            d != null &&
            d.Type != DestinationType.Moongate &&
            d.Type != DestinationType.Dock &&
            d.Type != DestinationType.Dungeon &&
            d.Type != DestinationType.DungeonEntrance;

        public static BotParty TryFormConvoy()
        {
            // Leader: a guilded bot already on the road with a real march
            // still ahead of it — and not mid-Recall (the pending teleport
            // would yank it away from the muster).
            var candidates = new List<(PlayerBot bot, BotDestination dest)>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || !IsEligible(bot) ||
                    bot.BotGuildIndex < 0 || bot.CorpseRunPending ||
                    bot.Behavior is not TravelerBehavior t ||
                    t.MagicTravelPending ||
                    string.IsNullOrEmpty(t.DestinationName))
                {
                    continue;
                }
                var dest = DestinationCatalog.GetByName(t.DestinationName);
                if (!IsRoamableTarget(dest))
                {
                    continue;
                }
                int dist = Math.Max(Math.Abs(bot.X - dest.Location.X),
                                    Math.Abs(bot.Y - dest.Location.Y));
                if (dist < ConvoyMinTripDistance || dist > ConvoyMaxTripDistance)
                {
                    continue;
                }
                candidates.Add((bot, dest));
            }
            if (candidates.Count == 0)
            {
                return null;
            }

            // Probe several would-be leaders — a single random pick almost
            // always stands alone on some road; the convoys that CAN form
            // are the ones passing through a plaza where guildmates are.
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = Utility.Random(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
            int probes = Math.Min(candidates.Count, 15);
            PlayerBot leader = null;
            BotDestination target = null;
            List<PlayerBot> mates = null;
            for (int c = 0; c < probes; c++)
            {
                var found = FindConvoyMates(candidates[c].bot);
                if (found.Count > 0)
                {
                    (leader, target) = candidates[c];
                    mates = found;
                    break;
                }
            }
            if (mates == null)
            {
                return null;
            }

            int take = Math.Min(mates.Count, Utility.RandomMinMax(1, 3));
            var party = new BotParty
            {
                Kind     = BotPartyKind.Convoy,
                Leader   = leader,
                Target   = target,
                FormedAt = Core.Now,
            };
            party.SetState(BotPartyState.Mustering);

            SayScene(leader, "guild_convoy_recruit", party);
            // The leader waits for the crew — park the trip until depart
            // (which hands it a fresh Traveler at the same destination).
            leader.Behavior = new IdleBehavior();

            for (int i = 0; i < take; i++)
            {
                var member = mates[i];
                party.Members.Add(member);
                var mm = member;
                Timer.DelayCall(TimeSpan.FromSeconds(1.5 + i * 1.8), () =>
                {
                    if (!mm.Deleted && PartyOf(mm) == party)
                    {
                        SayScene(mm, "guild_convoy_join", party);
                    }
                });
                member.Behavior = new PartyMemberBehavior();
            }

            _parties.Add(party);
            var guild = BotGuilds.Get(leader.BotGuildIndex);
            Console.WriteLine(
                $"[party] {leader.Name} set out with {take} [{guild?.Tag}] " +
                $"guildmates for {target.Name}");
            return party;
        }

        // Free guildmates in muster range of a would-be convoy leader.
        // Fighters and support classes only — a smith tagging along would
        // try to box an ettin (PartyMember fights); smiths keep their own
        // roads and get escorted when THEY lead. A traveler mid-Recall is
        // excluded: the pending teleport would rip it out of the group a
        // beat after joining.
        private static List<PlayerBot> FindConvoyMates(PlayerBot leader)
        {
            var mates = new List<PlayerBot>();
            foreach (var m in leader.Map.GetMobilesInRange(leader.Location, RoamRecruitRange))
            {
                if (m is PlayerBot bot && bot != leader &&
                    bot.BotGuildIndex == leader.BotGuildIndex &&
                    IsEligible(bot) && !bot.CorpseRunPending &&
                    IsRecruitable(bot.Class) &&
                    bot.Behavior is BankSitterBehavior or IdleBehavior
                                 or WanderBehavior or TravelerBehavior &&
                    (bot.Behavior is not TravelerBehavior mt || !mt.MagicTravelPending))
                {
                    mates.Add(bot);
                }
            }
            return mates;
        }

        // -------------------------------------------------------------------
        // Warband formation — an Order/Chaos squad on patrol. New bands
        // usually intercept a live enemy band's target, so two squads end
        // up on a collision course and BotFactionWar's group fight fires
        // when they sight each other.
        // -------------------------------------------------------------------
        public static BotParty TryFormWarband()
        {
            var candidates = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && IsEligible(bot) &&
                    !bot.CorpseRunPending &&
                    BotFactionWar.FactionOf(bot) != BotFaction.None &&
                    IsFighter(bot.Class) &&
                    bot.SkillTier >= BotSkillTier.Apprentice &&
                    bot.Behavior is BankSitterBehavior or IdleBehavior
                                 or WanderBehavior or TravelerBehavior &&
                    (bot.Behavior is not TravelerBehavior tb || !tb.MagicTravelPending))
                {
                    candidates.Add(bot);
                }
            }
            if (candidates.Count == 0)
            {
                return null;
            }

            // Probe several would-be leaders (same reasoning as convoys —
            // the bands that CAN muster are wherever faction-mates stand
            // near each other, usually a bank plaza).
            Shuffle(candidates);
            int probes = Math.Min(candidates.Count, 15);
            PlayerBot leader = null;
            BotFaction faction = BotFaction.None;
            BotDestination target = null;
            BotParty enemyBand = null;
            List<PlayerBot> mates = null;
            for (int c = 0; c < probes; c++)
            {
                var cand = candidates[c];
                var candFaction = BotFactionWar.FactionOf(cand);
                var found = FindWarbandMates(cand, candFaction);
                if (found.Count == 0)
                {
                    continue;
                }

                // Target: intercept the enemy's patrol when one is out,
                // else a faction-flavored spot on this leader's landmass.
                BotDestination candTarget = null;
                enemyBand = FindEnemyWarband(candFaction);
                if (enemyBand?.Target != null &&
                    Utility.RandomDouble() < WarbandInterceptChance &&
                    OnSameLandmass(cand, enemyBand.Target))
                {
                    candTarget = enemyBand.Target;
                }
                candTarget ??= PickPatrolTarget(cand, candFaction);
                if (candTarget == null)
                {
                    continue;
                }

                leader = cand;
                faction = candFaction;
                target = candTarget;
                mates = found;
                break;
            }
            if (mates == null)
            {
                return null;
            }

            int take = Math.Min(mates.Count, Utility.RandomMinMax(1, 3));
            var party = new BotParty
            {
                Kind     = BotPartyKind.Warband,
                Leader   = leader,
                Faction  = faction,
                Target   = target,
                FormedAt = Core.Now,
            };
            party.SetState(BotPartyState.Mustering);

            SayScene(leader, "warband_recruit", party);
            leader.Behavior = new IdleBehavior();

            for (int i = 0; i < take; i++)
            {
                var member = mates[i];
                party.Members.Add(member);
                var mm = member;
                Timer.DelayCall(TimeSpan.FromSeconds(1.5 + i * 1.8), () =>
                {
                    if (!mm.Deleted && PartyOf(mm) == party)
                    {
                        SayScene(mm, "warband_join", party);
                    }
                });
                member.Behavior = new PartyMemberBehavior();
            }

            _parties.Add(party);
            Console.WriteLine(
                $"[party] {faction} war band: {leader.Name} +{take} " +
                $"patrolling to {target.Name}" +
                (enemyBand != null && target == enemyBand.Target ? " (INTERCEPT)" : ""));
            return party;
        }

        private static void Shuffle(List<PlayerBot> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Utility.Random(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // Free faction-mates (any of the faction's guilds — allied shields
        // march together) in muster range of a would-be warband leader.
        private static List<PlayerBot> FindWarbandMates(PlayerBot leader, BotFaction faction)
        {
            var mates = new List<PlayerBot>();
            foreach (var m in leader.Map.GetMobilesInRange(leader.Location, RoamRecruitRange))
            {
                if (m is PlayerBot bot && bot != leader &&
                    BotFactionWar.FactionOf(bot) == faction &&
                    IsEligible(bot) && !bot.CorpseRunPending &&
                    IsRecruitable(bot.Class) &&
                    bot.Behavior is BankSitterBehavior or IdleBehavior
                                 or WanderBehavior or TravelerBehavior &&
                    (bot.Behavior is not TravelerBehavior mt || !mt.MagicTravelPending))
                {
                    mates.Add(bot);
                }
            }
            return mates;
        }

        private static BotParty FindEnemyWarband(BotFaction myFaction)
        {
            for (int i = 0; i < _parties.Count; i++)
            {
                var p = _parties[i];
                if (p.Kind == BotPartyKind.Warband &&
                    p.Faction != BotFaction.None && p.Faction != myFaction)
                {
                    return p;
                }
            }
            return null;
        }

        private static int LandmassOf(PlayerBot bot)
        {
            var graph = WaypointRegistry.Graph;
            var node = graph.FindNearestNode(bot.Location);
            return node != null ? graph.ComponentOf(node.Name) : -1;
        }

        private static bool OnComponent(int comp, BotDestination dest)
        {
            if (comp < 0)
            {
                return false;
            }
            var graph = WaypointRegistry.Graph;
            var wp = dest.NearestWaypoint;
            if (string.IsNullOrEmpty(wp) || graph.Get(wp) == null)
            {
                wp = graph.FindNearestNode(dest.Location)?.Name;
            }
            return wp != null && graph.ComponentOf(wp) == comp;
        }

        private static bool OnSameLandmass(PlayerBot leader, BotDestination dest) =>
            OnComponent(LandmassOf(leader), dest);

        // Order patrols civilization (shrines, banks, squares); Chaos
        // haunts the dark spots (graveyards, crossroads, bridges). Both
        // stay on the leader's landmass and inside patrol range.
        private static BotDestination PickPatrolTarget(PlayerBot leader, BotFaction faction)
        {
            int comp = LandmassOf(leader);
            var options = new List<BotDestination>();
            foreach (var d in DestinationCatalog.All)
            {
                bool flavored = faction == BotFaction.Order
                    ? d.Type is DestinationType.Shrine or DestinationType.Bank
                             or DestinationType.CityCenter or DestinationType.Crossroads
                    : d.Type is DestinationType.Graveyard or DestinationType.Crossroads
                             or DestinationType.Bridge or DestinationType.Shrine;
                if (!flavored)
                {
                    continue;
                }

                int dist = Math.Max(Math.Abs(d.Location.X - leader.X),
                                    Math.Abs(d.Location.Y - leader.Y));
                if (dist < WarbandMinPatrol || dist > WarbandMaxPatrol)
                {
                    continue;
                }
                if (!OnComponent(comp, d))
                {
                    continue;
                }
                options.Add(d);
            }
            return options.Count > 0 ? options[Utility.Random(options.Count)] : null;
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

                // Members go back to ordinary life. Hunt: inside a dungeon
                // that means becoming a crawler (finish the dive, climb
                // out on the normal exit reflex); outside, a fresh
                // Traveler. Convoy: finish the walk to the SAME
                // destination on their own feet and do their own arrival
                // thing — the group dissolves INTO the place it walked
                // to. Warband: patrol's over, disperse.
                if (bot != party.Leader && bot.Behavior is PartyMemberBehavior)
                {
                    bot.Behavior = party.Kind switch
                    {
                        BotPartyKind.Convoy => new TravelerBehavior
                        {
                            DestinationName = party.Target?.Name,
                        },
                        BotPartyKind.Warband => BehaviorRegistry.Create("Traveler"),
                        _ => DungeonRegistry.IsInDungeon(bot)
                            ? new DungeonCrawlerBehavior()
                            : BehaviorRegistry.Create("Traveler"),
                    };
                }
            }

            // A roam leader parked in Idle for the muster (disband raced
            // the depart — members died, timeout) must not idle forever:
            // send it back on its way.
            if (party.Kind != BotPartyKind.Hunt &&
                party.Leader is { Deleted: false, Alive: true } roamLeader &&
                roamLeader.Behavior is IdleBehavior)
            {
                roamLeader.Behavior = new TravelerBehavior
                {
                    DestinationName = party.Target?.Name,
                };
            }

            Console.WriteLine($"[party] {party.Kind} disbanded " +
                              $"({party.Target?.Name}, {everyone.Count} bots, " +
                              $"state {party.State})");
        }

        // A fight (or anything else) just consumed this bot's group — the
        // party dissolves around it. Participants already swapped OFF
        // PartyMemberBehavior (e.g. to faction-fight defenders) keep their
        // new brains; only untouched followers get reattached to normal
        // life. No goodbyes — the occasion speaks for itself.
        public static void DisbandInvolving(PlayerBot bot)
        {
            var party = PartyOf(bot);
            if (party != null)
            {
                Disband(party, sayGoodbyes: false);
            }
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
            var dungeon = party.Target?.Dungeon;
            if (string.IsNullOrEmpty(dungeon))
            {
                dungeon = "the dungeon";
            }
            var dest = party.Target?.Name ?? "the road";
            bot.Say(line
                .Replace("{dungeon}", dungeon.ToLowerInvariant(), StringComparison.Ordinal)
                .Replace("{dest}", dest.ToLowerInvariant(), StringComparison.Ordinal));
        }

        // -------------------------------------------------------------------
        [Usage("BotParties [form | convoy | warband]")]
        [Description("Lists live parties (all kinds), or force-forms one for testing.")]
        private static void Status_OnCommand(CommandEventArgs e)
        {
            if (e.Arguments.Length > 0)
            {
                var arg = e.Arguments[0];
                if (string.Equals(arg, "form", StringComparison.OrdinalIgnoreCase))
                {
                    var formed = TryFormParty(e.Mobile.Location) ?? TryFormParty(null);
                    e.Mobile.SendMessage(formed != null
                        ? $"Formed: {formed.Leader.Name} + {formed.Members.Count} " +
                          $"→ {formed.Target.Dungeon}"
                        : "No eligible leader/recruits right now (need bank-sitting fighters).");
                    return;
                }
                if (string.Equals(arg, "convoy", StringComparison.OrdinalIgnoreCase))
                {
                    var convoy = TryFormConvoy();
                    e.Mobile.SendMessage(convoy != null
                        ? $"Convoy: {convoy.Leader.Name} + {convoy.Members.Count} " +
                          $"→ {convoy.Target.Name}"
                        : "No eligible guilded traveler with free guildmates nearby.");
                    return;
                }
                if (string.Equals(arg, "warband", StringComparison.OrdinalIgnoreCase))
                {
                    var band = TryFormWarband();
                    e.Mobile.SendMessage(band != null
                        ? $"War band: {band.Faction} {band.Leader.Name} + {band.Members.Count} " +
                          $"→ {band.Target.Name}"
                        : "No eligible faction fighter with free faction-mates nearby.");
                    return;
                }
            }

            if (_parties.Count == 0)
            {
                e.Mobile.SendMessage("No live parties.");
                return;
            }
            foreach (var p in _parties)
            {
                e.Mobile.SendMessage(
                    $"{p.Kind}: {p.Leader?.Name ?? "?"} +{p.Members.Count} " +
                    $"→ {p.Target?.Name} [{p.State}] " +
                    $"age {(int)(Core.Now - p.FormedAt).TotalMinutes}m " +
                    $"at ({p.Leader?.X},{p.Leader?.Y})");
            }
        }
    }
}
