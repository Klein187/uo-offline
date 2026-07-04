// =========================================================================
// GathererBehavior.cs — a lumberjack/miner working a wilderness spot
// (IDEAS 1.5 "lumberjack in the middle of nowhere" + the supply side of
// the 4.1 economy loop).
//
// Attached by the Traveler handoff when a gatherer class arrives at a
// GatherSpot. The bot works: swings its tool at the treeline/rock face
// (real animation + the chop/dig sound), accumulates REAL logs/ore in
// its pack, and mutters work chatter. When the shift ends (visit timer)
// it shoulders the load — HaulPending — and travels to town, where
// TravelerBehavior's delivery hook plays the handoff scene at a crafter
// or the bank.
//
// If something attacks mid-shift, the tool is a real axe: swap to a
// defender and fight (the classic UO lumberjack). The shift is lost;
// the defender revert sends them traveling and the destination roll
// usually points at another spot.
// =========================================================================

using System;
using Server;

namespace Server.CustomBots
{
    public class GathererBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Gatherer";

        // Swing cadence and yield.
        private static readonly TimeSpan SwingInterval   = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan HarvestInterval = TimeSpan.FromSeconds(35);
        private const int MaxCarried = 60; // stop stuffing the pack past this

        private DateTime _nextSwing;
        private DateTime _nextHarvest;
        private Point3D _anchor;

        public GathererBehavior()
        {
            ChatCategories  = new[] { "gather_talk", "small_talk" };
            ChatChance      = 0.10;
            MinChatCooldown = TimeSpan.FromSeconds(45);
            MaxChatCooldown = TimeSpan.FromSeconds(120);
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            _anchor = bot.Location;
            _nextSwing = Core.Now;
            _nextHarvest = Core.Now + HarvestInterval;

            // Organic arrivals get a visit window from the handoff; a
            // directly-attached gatherer (admin, load) stamps its own.
            VisitExpiresAt ??= Core.Now + TimeSpan.FromMinutes(Utility.RandomMinMax(4, 8));
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal || bot.Deleted || !bot.Alive)
            {
                return;
            }

            // Attacked mid-shift: drop the work, raise the axe. The
            // defender's revert-to-Traveler resumes ordinary life.
            if (bot.Combatant is Mobile threat && threat.Alive && !threat.Deleted)
            {
                bot.Behavior = new AdventurerBehavior
                {
                    DefenderMode = true,
                    DefenderRetreatHpFraction = 0.45,
                };
                return;
            }

            // Shift over — shoulder the load and head to town. The
            // destination roll sees HaulPending and points at the bank /
            // the crafter who buys this material.
            if (VisitExpiresAt != null && Core.Now >= VisitExpiresAt.Value)
            {
                bot.HaulPending = true;
                var line = ChatLibrary.PickRandom("gather_haul");
                if (!string.IsNullOrEmpty(line))
                {
                    bot.Say(line);
                }
                bot.Behavior = BehaviorRegistry.Create("Traveler");
                return;
            }

            TrySpeak(bot);

            // Work theater: face the "work face", swing, thunk.
            if (Core.Now >= _nextSwing)
            {
                _nextSwing = Core.Now + SwingInterval +
                    TimeSpan.FromMilliseconds(Utility.Random(1500));

                if (Utility.RandomDouble() < 0.15)
                {
                    // Shuffle a tile — working along the treeline.
                    var dir = (Direction)Utility.Random(8);
                    if (bot.InRange(_anchor, 4))
                    {
                        bot.Direction = dir;
                        bot.Move(dir);
                    }
                    else
                    {
                        var back = bot.GetDirectionTo(_anchor);
                        bot.Direction = back;
                        bot.Move(back);
                    }
                }

                // Swing animation (one-hand chop) + the trade sound.
                bot.Animate(11, 5, 1, true, false, 0);
                bot.PlaySound(bot.Class == BotClass.Miner ? 0x125 : 0x13E);
            }

            // The yield: real stackables into the pack.
            if (Core.Now >= _nextHarvest)
            {
                _nextHarvest = Core.Now + HarvestInterval +
                    TimeSpan.FromSeconds(Utility.Random(20));
                AddYield(bot);
            }
        }

        private static void AddYield(PlayerBot bot)
        {
            if (bot.Backpack == null)
            {
                return;
            }

            int carried = 0;
            foreach (var item in bot.Backpack.Items)
            {
                if (item is Server.Items.Log or Server.Items.IronOre)
                {
                    carried += item.Amount;
                }
            }
            if (carried >= MaxCarried)
            {
                return;
            }

            Item yield = bot.Class == BotClass.Miner
                ? new Server.Items.IronOre(Utility.RandomMinMax(2, 6))
                : new Server.Items.Log(Utility.RandomMinMax(3, 8));
            if (!bot.Backpack.TryDropItem(bot, yield, sendFullMessage: false))
            {
                yield.Delete();
            }
        }
    }
}
