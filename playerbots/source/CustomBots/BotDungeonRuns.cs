// =========================================================================
// BotDungeonRuns.cs — how often a bot sets out for a dungeon.
//
// A Traveler picking its next trip first asks: is this one a dungeon run?
// The chance is set per class. Fighters go a good share of the time, the
// support classes now and then, and everyone else leaves it to the normal
// place roll (where a dungeon still comes up once in a long while).
//
// Before this, dungeons were just eleven more places in a roll of about
// two hundred, most of them shops, and the home-town weighting pushed them
// further down. About one trip in twenty was a dungeon, and on a live
// shard of 1,100 bots eleven were crawling.
//
// Which dungeon: one of the town's own two (see BotHangouts) most of the
// time, and now and then (AwayChance) any other dungeon at random, however
// far, by road, moongate or Recall. Either way, fewer trips go where
// murders have been reported lately (BotDangerMap).
//
// Hunting grounds drawn in the map editor (AdventureArea) are in the same
// roll, each counted like one dungeon. One near home (in town, or within a
// walk on the same landmass) counts as the town's own; the rest are away.
// =========================================================================

using System.Collections.Generic;

namespace Server.CustomBots
{
    public static class BotDungeonRuns
    {
        // Chance a new trip is a dungeon run, by class.
        public static double ChanceFor(BotClass c) => c switch
        {
            BotClass.Warrior or BotClass.Mage or BotClass.Fencer
                or BotClass.Archer or BotClass.Ranger => 0.30,
            BotClass.Tamer => 0.20,
            BotClass.Healer or BotClass.Thief => 0.10,
            BotClass.Bard => 0.05,
            _ => 0.0,
        };

        // Share of dungeon runs, and of hunting parties, that go to a
        // dungeon other than the town's own.
        public const double AwayChance = 0.20;

        public static bool WantsRun(PlayerBot bot) =>
            bot != null && bot.Alive && !bot.HaulPending &&
            Utility.RandomDouble() < ChanceFor(bot.Class);

        public static BotDestination PickEntrance(PlayerBot bot)
        {
            var hangout = BotHangouts.For(bot);
            var town = bot.HomeCity;
            bool away = Utility.RandomDouble() < AwayChance;
            var options = new List<(BotDestination d, double w)>();
            double total = 0;
            foreach (var d in DestinationCatalog.All)
            {
                bool ground = d.Type == DestinationType.AdventureArea;
                if (d.Type != DestinationType.DungeonEntrance && !ground || !RedTerritory.MayGoTo(bot, d))
                {
                    continue;
                }
                double w;
                if (hangout == null)
                {
                    // No home town: by distance, as before.
                    w = BotHangouts.Factor(hangout, d);
                }
                else if (ground)
                {
                    bool local = BotHangouts.Factor(hangout, d) >= 1.0;
                    if (local == away)
                    {
                        continue;
                    }
                    w = 1.0;
                }
                else if (BotHangouts.IsLocalDungeon(town, d) != away)
                {
                    // Home runs: the town's own. Away runs: every other
                    // dungeon the same, wherever it is. Split between a
                    // dungeon's entrances so Covetous is not twice as
                    // likely as the rest.
                    w = 1.0 / EntrancesOf(d.Dungeon);
                }
                else
                {
                    continue;
                }
                w *= BotDangerMap.Multiplier(d);
                if (w > 0)
                {
                    options.Add((d, w));
                    total += w;
                }
            }
            if (total <= 0)
            {
                return null;
            }
            double r = Utility.RandomDouble() * total;
            foreach (var (d, w) in options)
            {
                r -= w;
                if (r <= 0)
                {
                    return d;
                }
            }
            return options[^1].d;
        }

        public static int EntrancesOf(string dungeon)
        {
            int n = 0;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type == DestinationType.DungeonEntrance &&
                    string.Equals(d.Dungeon, dungeon, System.StringComparison.OrdinalIgnoreCase))
                {
                    n++;
                }
            }
            return System.Math.Max(1, n);
        }
    }
}
