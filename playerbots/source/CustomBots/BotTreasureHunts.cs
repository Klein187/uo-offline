// =========================================================================
// BotTreasureHunts.cs — treasure-hunting theater (IDEAS 4.4).
//
// The T2A staple: a bot announces it "got a map", walks OUT of town to a
// wilderness dig site, digs (shovel swings + sounds), the guardians rise
// out of the ground and attack, the bot fights them down, pries open the
// unearthed chest, pockets the gold, and heads back to town with a story
// the gossip system spreads.
//
// Moving parts:
//   - TreasureSites: a few dozen synthetic "Dig Site N" destinations
//     generated from rural waypoint nodes at boot (same trick as
//     GatherSpots). Weight 0 — nobody ever rolls one; trips are assigned.
//   - BotTreasureHunts (manager): every so often picks an idle fighter in
//     town, plays the announcement beat, and sends it to a random dig
//     site as an ordinary Traveler destination. The arrival handoff in
//     TravelerBehavior attaches TreasureHunterBehavior.
//   - TreasureHunterBehavior: dig -> guardians -> fight (inherited
//     AdventurerBehavior brain) -> chest & payout -> back to town.
//
// Test hooks: [BotThunt [force]  +  headless thunt_request.txt token.
// Journal type "thunt" (Gossip/thunt.txt templates).
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;
using Server.Items;
using Server.Mobiles;

namespace Server.CustomBots
{
    // ---------------------------------------------------------------------
    // Synthetic dig sites.
    // ---------------------------------------------------------------------
    public static class TreasureSites
    {
        private const int MinCityDistance = 55;
        private const int MinSiteSpacing  = 110;
        private const int MaxSites        = 24;
        private const int DungeonSpaceX   = 5000;

        private static List<BotDestination> _generated;

        public static void Initialize() => EnsureRegistered();

        public static void EnsureRegistered()
        {
            if (_generated != null)
            {
                DestinationCatalog.RegisterSynthetic(_generated);
                return;
            }

            var graph = WaypointRegistry.Graph;
            if (graph.NodeCount == 0 || DestinationCatalog.Count == 0)
            {
                return;
            }

            var cityPoints = new List<Point3D>();
            foreach (var d in DestinationCatalog.All)
            {
                if (!string.IsNullOrEmpty(d.City))
                {
                    cityPoints.Add(d.Location);
                }
            }

            var picked = new List<WaypointNode>();
            foreach (var node in graph.AllNodes)
            {
                var loc = node.Location;
                if (loc.X >= DungeonSpaceX)
                {
                    continue;
                }

                bool nearCity = false;
                foreach (var c in cityPoints)
                {
                    if (Math.Max(Math.Abs(c.X - loc.X), Math.Abs(c.Y - loc.Y)) < MinCityDistance)
                    {
                        nearCity = true;
                        break;
                    }
                }
                if (nearCity)
                {
                    continue;
                }

                bool crowded = false;
                foreach (var p in picked)
                {
                    if (Math.Max(Math.Abs(p.Location.X - loc.X),
                                 Math.Abs(p.Location.Y - loc.Y)) < MinSiteSpacing)
                    {
                        crowded = true;
                        break;
                    }
                }
                if (crowded)
                {
                    continue;
                }

                picked.Add(node);
                if (picked.Count >= MaxSites)
                {
                    break;
                }
            }

            _generated = new List<BotDestination>();
            int i = 1;
            foreach (var node in picked)
            {
                _generated.Add(new BotDestination
                {
                    Name            = $"Dig Site {i++}",
                    Location        = node.Location,
                    Type            = DestinationType.TreasureSite,
                    City            = "",
                    NearestWaypoint = node.Name,
                });
            }

            DestinationCatalog.RegisterSynthetic(_generated);
            Console.WriteLine($"[TreasureSites] registered {_generated.Count} dig site(s).");
        }

        public static void OnCatalogReloaded()
        {
            if (_generated != null)
            {
                DestinationCatalog.RegisterSynthetic(_generated);
            }
        }

        public static BotDestination PickRandom()
        {
            if (_generated == null || _generated.Count == 0)
            {
                return null;
            }
            return _generated[Utility.Random(_generated.Count)];
        }
    }

    // ---------------------------------------------------------------------
    // Manager — starts hunts on a slow cadence.
    // ---------------------------------------------------------------------
    public static class BotTreasureHunts
    {
        public static bool Enabled { get; set; } = true;

        private static readonly TimeSpan AttemptMin   = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan AttemptMax   = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
        private const int MaxActiveHunts = 2;

        private static DateTime _nextAttempt = DateTime.MinValue;
        private static readonly List<(Serial bot, DateTime started)> _active = new();

        public static void Configure()
        {
            Timer.DelayCall(TickInterval, TickInterval, OnTick);
            CommandSystem.Register("BotThunt", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            if (e.Length > 0 && e.GetString(0).ToLowerInvariant() == "force")
            {
                e.Mobile.SendMessage(TryStartHunt(force: true)
                    ? "Treasure hunt started."
                    : "No eligible bot for a treasure hunt.");
                return;
            }
            e.Mobile.SendMessage(
                $"Treasure hunts: {_active.Count} active. ([BotThunt force)");
        }

        private static void OnTick()
        {
            if (!Enabled)
            {
                return;
            }

            Prune();

            if (Core.Now < _nextAttempt || _active.Count >= MaxActiveHunts)
            {
                return;
            }
            _nextAttempt = Core.Now + TimeSpan.FromSeconds(
                Utility.RandomMinMax((int)AttemptMin.TotalSeconds,
                                     (int)AttemptMax.TotalSeconds));

            TryStartHunt(force: false);
        }

        private static void Prune()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var (serial, started) = _active[i];
                var bot = World.FindEntity<Mobile>(serial) as PlayerBot;
                bool over =
                    bot == null || bot.Deleted || !bot.Alive ||
                    Core.Now - started > TimeSpan.FromMinutes(45) ||
                    (Core.Now - started > TimeSpan.FromMinutes(2) &&
                     bot.Behavior is not TravelerBehavior
                                 and not TreasureHunterBehavior);
                if (over)
                {
                    _active.RemoveAt(i);
                }
            }
        }

        public static void OnHuntComplete(PlayerBot bot)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (_active[i].bot == bot.Serial)
                {
                    _active.RemoveAt(i);
                }
            }
        }

        private static bool IsEligible(PlayerBot bot) =>
            bot != null && !bot.Deleted && bot.Alive &&
            !bot.LifecycleExempt && !bot.LoggingOut &&
            !bot.CorpseRunPending &&
            bot.Combatant == null &&
            !BotClassHelper.IsArtisan(bot.Class) &&
            !BotClassHelper.IsGatherer(bot.Class) &&
            bot.Class != BotClass.Crafter &&
            (bot.Behavior is BankSitterBehavior or IdleBehavior
                          or WanderBehavior or ShopperBehavior) &&
            !BotPartyManager.IsInParty(bot) &&
            !DungeonRegistry.IsInDungeon(bot);

        public static bool TryStartHunt(bool force)
        {
            var site = TreasureSites.PickRandom();
            if (site == null)
            {
                return false;
            }

            var candidates = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && IsEligible(bot))
                {
                    candidates.Add(bot);
                }
            }
            if (candidates.Count == 0)
            {
                return false;
            }

            var hunter = candidates[Utility.Random(candidates.Count)];
            StartTrip(hunter, site, "thunt_start");
            return true;
        }

        // Shared with the fishing-SOS flow, which starts the same trip with
        // different flavor lines.
        public static void StartTrip(PlayerBot hunter, BotDestination site,
            string announceCategory)
        {
            var line = ChatLibrary.PickRandom(announceCategory);
            if (!string.IsNullOrEmpty(line))
            {
                hunter.Say(line);
            }

            var traveler = new TravelerBehavior { DestinationName = site.Name };
            hunter.Behavior = traveler;
            _active.Add((hunter.Serial, Core.Now));

            Console.WriteLine(
                $"[thunt] {hunter.Name} sets out for {site.Name} " +
                $"({site.Location.X},{site.Location.Y})");
        }
    }

    // ---------------------------------------------------------------------
    // The on-site behavior: dig, fight the guardians, open the chest.
    // ---------------------------------------------------------------------
    public class TreasureHunterBehavior : AdventurerBehavior
    {
        public override string SerializableName => "TreasureHunter";

        private enum Phase { Digging, Fighting, Looting }

        private Phase _phase;
        private Point3D _anchor;
        private DateTime _digUntil;
        private DateTime _nextDigSwing;
        private DateTime _lootAt;
        private readonly List<BaseCreature> _guardians = new();
        private Item _chest;
        private bool _paidOut;

        public override string GetStatusLine(PlayerBot bot) => _phase switch
        {
            Phase.Digging => "digging for treasure",
            Phase.Fighting => "fighting chest guardians",
            _ => "looting a treasure chest",
        };

        public TreasureHunterBehavior()
        {
            ChatCategories  = new[] { "thunt_dig", "small_talk" };
            ChatChance      = 0.10;
            MinChatCooldown = TimeSpan.FromSeconds(30);
            MaxChatCooldown = TimeSpan.FromSeconds(90);
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            _phase = Phase.Digging;
            _anchor = bot.Location;
            _digUntil = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(18, 30));
            _nextDigSwing = Core.Now;

            // Safety net: whatever happens, the visit window ends the hunt
            // and the base class hands back to a Traveler.
            VisitExpiresAt = Core.Now + TimeSpan.FromMinutes(12);
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Deleted || !bot.Alive)
            {
                return; // death flow owns the bot; OnDetached cleans up
            }

            switch (_phase)
            {
                case Phase.Digging:
                    // Jumped mid-dig by a wandering mob: the base brain
                    // fights it; the dig timer keeps running.
                    if (bot.Combatant is Mobile threat && threat.Alive)
                    {
                        base.Tick(bot);
                        return;
                    }

                    if (Core.Now >= _digUntil)
                    {
                        SpawnGuardians(bot);
                        _phase = Phase.Fighting;
                        return;
                    }

                    if (Core.Now >= _nextDigSwing)
                    {
                        _nextDigSwing = Core.Now + TimeSpan.FromSeconds(3) +
                            TimeSpan.FromMilliseconds(Utility.Random(1200));
                        bot.Direction = (Direction)Utility.Random(8);
                        bot.Animate(11, 5, 1, true, false, 0);
                        bot.PlaySound(0x125); // shovel into dirt
                    }
                    TrySpeak(bot);
                    break;

                case Phase.Fighting:
                    PruneGuardians();
                    if (_guardians.Count == 0 &&
                        (bot.Combatant is not Mobile c || !c.Alive))
                    {
                        _phase = Phase.Looting;
                        _lootAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(4, 8));
                        PlaceChest(bot);
                        return;
                    }
                    base.Tick(bot); // the adventurer brain fights
                    break;

                case Phase.Looting:
                    if (Core.Now < _lootAt)
                    {
                        return;
                    }
                    PayOut(bot);
                    break;
            }
        }

        private void SpawnGuardians(PlayerBot bot)
        {
            var line = ChatLibrary.PickRandom("thunt_spawn");
            if (!string.IsNullOrEmpty(line))
            {
                bot.Say(line);
            }

            // Guardian pack scales with the hunter's tier — era-classic
            // undead for everyone, uglier company for the seasoned.
            int tier = (int)bot.SkillTier;
            int count = Utility.RandomMinMax(3, 4);
            for (int i = 0; i < count; i++)
            {
                BaseCreature mob;
                double roll = Utility.RandomDouble();
                if (tier >= 4 && roll < 0.25)
                {
                    mob = new Lich();
                }
                else if (tier >= 2 && roll < 0.45)
                {
                    mob = Utility.RandomBool()
                        ? new Ghoul()
                        : new Ettin();
                }
                else
                {
                    mob = Utility.RandomBool()
                        ? new Skeleton()
                        : new Zombie();
                }

                var loc = new Point3D(
                    bot.X + Utility.RandomMinMax(-3, 3),
                    bot.Y + Utility.RandomMinMax(-3, 3),
                    bot.Z);
                mob.MoveToWorld(loc, bot.Map);
                mob.PlaySound(mob.GetAngerSound());
                mob.Combatant = bot;
                _guardians.Add(mob);
            }

            Effects.PlaySound(bot.Location, bot.Map, 0x1FB); // ground rumble
        }

        private void PruneGuardians()
        {
            for (int i = _guardians.Count - 1; i >= 0; i--)
            {
                var g = _guardians[i];
                if (g == null || g.Deleted || !g.Alive)
                {
                    _guardians.RemoveAt(i);
                }
            }
        }

        private void PlaceChest(PlayerBot bot)
        {
            try
            {
                var chest = new MetalChest
                {
                    Movable = false,
                    Name = "a battered treasure chest",
                };
                chest.MoveToWorld(new Point3D(_anchor.X, _anchor.Y, _anchor.Z), bot.Map);
                _chest = chest;

                // The chest is scenery — it fades after the hunter leaves.
                Timer.DelayCall(TimeSpan.FromSeconds(150), () =>
                {
                    if (_chest != null && !_chest.Deleted)
                    {
                        _chest.Delete();
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[thunt] chest failed: {ex.Message}");
            }
        }

        private void PayOut(PlayerBot bot)
        {
            if (_paidOut)
            {
                return;
            }
            _paidOut = true;

            bot.Animate(32, 5, 1, true, false, 0); // bow/crouch at the chest
            bot.PlaySound(0x2E5); // chest opening creak

            var line = ChatLibrary.PickRandom("thunt_find");
            if (!string.IsNullOrEmpty(line))
            {
                bot.Say(line);
            }

            if (bot.Backpack != null)
            {
                var gold = new Gold(Utility.RandomMinMax(250, 900));
                if (!bot.Backpack.TryDropItem(bot, gold, sendFullMessage: false))
                {
                    gold.Delete();
                }
            }

            BotEventJournal.Record("thunt", bot);
            BotTreasureHunts.OnHuntComplete(bot);

            // Head home with the story. The destination roll takes it from
            // here (usually a bank — where the gossip lands best).
            bot.Behavior = BehaviorRegistry.Create("Traveler");
        }

        public override void OnDetached(PlayerBot bot)
        {
            base.OnDetached(bot);

            // Never leak the spawned pack: whatever ends this behavior
            // (payout, retreat, death, lifecycle), guardians that are not
            // mid-fight with someone ELSE sink back into the ground.
            foreach (var g in _guardians)
            {
                if (g != null && !g.Deleted && g.Alive &&
                    (g.Combatant == null || g.Combatant == bot))
                {
                    g.PlaySound(0x1FB);
                    g.Delete();
                }
            }
            _guardians.Clear();
        }
    }
}
