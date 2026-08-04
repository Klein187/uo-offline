// =========================================================================
// BotSeaEvents.cs — the fishing SOS (IDEAS 4.4, sea half).
//
// Every so often a fisherman working a dock reels in a barnacled bottle:
// a small scene plays on the pier (the catch, the excitement), the event
// lands in the journal (Gossip/sos.txt spreads it), and — when an
// adventurer happens to be standing around the docks — the map changes
// hands and that bot sets out on a real treasure hunt (the same dig-site
// trip BotTreasureHunts runs, announced with sos flavor instead).
//
// The fisherman stays at work afterward: fishermen fish; adventurers
// chase maps. If nobody's around to buy it, the story still circulates —
// "Marta fished up a sealed bottle at the Vesper dock" is good gossip
// even when nothing comes of it.
//
// Test hooks: [BotSos [force]  +  headless sos_request.txt token.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class BotSeaEvents
    {
        public static bool Enabled { get; set; } = true;

        private static readonly TimeSpan AttemptMin   = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan AttemptMax   = TimeSpan.FromMinutes(35);
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

        // How far from the fisherman a dockside bystander can be and still
        // "buy the map".
        private const int BuyerRange = 30;

        private static DateTime _nextAttempt = DateTime.MinValue;

        public static void Configure()
        {
            Timer.DelayCall(TickInterval, TickInterval, OnTick);
            CommandSystem.Register("BotSos", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            if (e.Length > 0 && e.GetString(0).ToLowerInvariant() == "force")
            {
                e.Mobile.SendMessage(TryFishUpBottle()
                    ? "SOS scene started."
                    : "No working fisherman found.");
                return;
            }
            e.Mobile.SendMessage("Fishing SOS events. ([BotSos force)");
        }

        private static void OnTick()
        {
            if (!Enabled || Core.Now < _nextAttempt)
            {
                return;
            }
            _nextAttempt = Core.Now + TimeSpan.FromSeconds(
                Utility.RandomMinMax((int)AttemptMin.TotalSeconds,
                                     (int)AttemptMax.TotalSeconds));

            TryFishUpBottle();
        }

        public static bool TryFishUpBottle()
        {
            // A fisherman actually working a pier.
            var fishers = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted && bot.Alive &&
                    bot.Class == BotClass.Fisherman &&
                    bot.Behavior is CrafterBehavior)
                {
                    fishers.Add(bot);
                }
            }
            if (fishers.Count == 0)
            {
                return false;
            }

            var fisher = fishers[Utility.Random(fishers.Count)];

            // The catch, played as beats on the pier.
            BotScene.Play(
                (0.0, fisher, ChatLibrary.PickRandom("sos_catch") ?? "a bottle! theres a map inside!!"),
                (6.0, fisher, ChatLibrary.PickRandom("sos_offer") ?? "who wants it? genuine treasure map, cheap"));

            BotEventJournal.Record("sos", fisher);
            Console.WriteLine($"[sos] {fisher.Name} fished up a bottle at ({fisher.X},{fisher.Y})");

            // A dockside adventurer takes the map and makes a real trip of
            // it, a few beats after the offer.
            Timer.DelayCall(TimeSpan.FromSeconds(9), () =>
            {
                if (fisher.Deleted)
                {
                    return;
                }

                PlayerBot buyer = null;
                foreach (var m in fisher.GetMobilesInRange(BuyerRange))
                {
                    if (m is PlayerBot bot && !bot.Deleted && bot.Alive &&
                        bot != fisher &&
                        bot.Combatant == null &&
                        !bot.LifecycleExempt && !bot.LoggingOut &&
                        !bot.CorpseRunPending &&
                        !BotClassHelper.IsArtisan(bot.Class) &&
                        !BotClassHelper.IsGatherer(bot.Class) &&
                        bot.Class != BotClass.Crafter &&
                        !BotPartyManager.IsInParty(bot) &&
                        (bot.Behavior is TravelerBehavior or IdleBehavior
                                      or WanderBehavior or BankSitterBehavior))
                    {
                        buyer = bot;
                        break;
                    }
                }

                if (buyer == null)
                {
                    return; // no takers — the gossip alone carries it
                }

                var site = TreasureSites.PickRandom();
                if (site == null)
                {
                    return;
                }

                BotScene.Play(
                    (0.0, buyer, ChatLibrary.PickRandom("sos_buy") ?? "ill take that map off ye"),
                    (2.0, fisher, "here"));

                Timer.DelayCall(TimeSpan.FromSeconds(4), () =>
                {
                    if (buyer.Deleted || !buyer.Alive)
                    {
                        return;
                    }
                    BotTreasureHunts.StartTrip(buyer, site, "sos_setout");
                });
            });

            return true;
        }
    }
}
