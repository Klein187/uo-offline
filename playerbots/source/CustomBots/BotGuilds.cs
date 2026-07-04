// =========================================================================
// BotGuilds.cs — the shard's player guilds (IDEAS 2.1 phase 1: tags +
// rosters).
//
// A fixed catalog of era-flavored guilds. ~40% of bots roll membership at
// creation; the guild index is stored on the bot and serialized. The tag
// shows as "Name [TAG]" via PlayerBot.ApplyNameSuffix — no real
// Server.Guilds objects involved (those are account-backed, serialized
// entities; bots are transient, so a static catalog + an int per bot gives
// the same on-screen result with none of the persistence weight).
//
// Membership is rolled with per-guild weights so the population develops
// believable structure: a couple of big zerg guilds, several mid-size,
// a few small tight crews.
//
//   [BotGuilds  — roster summary (live member counts per guild)
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public sealed class BotGuildDef
    {
        public string Name   { get; init; }
        public string Tag    { get; init; }
        // Relative roster size. Higher = more members roll into it.
        public double Weight { get; init; } = 1.0;
        // Order/Chaos allegiance (IDEAS 2.1 phase 3). Faction guild
        // members carry the era shield and fight the other side ON SIGHT,
        // in town, guards ignoring it — exactly as T2A worked.
        public BotFaction Faction { get; init; } = BotFaction.None;
    }

    public static class BotGuilds
    {
        // Chance a fresh bot belongs to any guild at all.
        public const double MembershipChance = 0.40;

        // The catalog. Order matters: a bot stores its guild as an index
        // into this array, so APPEND new guilds — never reorder or remove.
        public static readonly BotGuildDef[] All =
        {
            new() { Name = "The Syndicate",               Tag = "TS",   Weight = 2.5 },
            new() { Name = "The Undead Lords",            Tag = "UDL",  Weight = 2.0, Faction = BotFaction.Chaos },
            new() { Name = "Knights of Yew",              Tag = "KoY",  Weight = 1.5, Faction = BotFaction.Order },
            new() { Name = "DOOM",                        Tag = "DOOM", Weight = 2.0, Faction = BotFaction.Chaos },
            new() { Name = "Order of the Silver Serpent", Tag = "OSS",  Weight = 1.5, Faction = BotFaction.Order },
            new() { Name = "The Black Hand",              Tag = "BH",   Weight = 1.0 },
            new() { Name = "Wolves of Vesper",            Tag = "WoV",  Weight = 1.0 },
            new() { Name = "Circle of Mages",             Tag = "CoM",  Weight = 1.0 },
            new() { Name = "The Crimson Brotherhood",     Tag = "CB",   Weight = 1.0 },
            new() { Name = "Guardians of Virtue",         Tag = "GoV",  Weight = 1.2, Faction = BotFaction.Order },
            new() { Name = "Trinsic Trading Company",     Tag = "TTC",  Weight = 0.8 },
            new() { Name = "The Merry Men",               Tag = "MM",   Weight = 0.6 },
            new() { Name = "Dread Lords of Nox",          Tag = "NOX",  Weight = 0.8, Faction = BotFaction.Chaos },
        };

        private static readonly double _totalWeight = SumWeights();

        private static double SumWeights()
        {
            double t = 0;
            foreach (var g in All)
            {
                t += g.Weight;
            }
            return t;
        }

        // Roll guild membership for a fresh bot. Returns the guild index,
        // or -1 for the unguilded majority.
        public static int RollMembership()
        {
            if (Utility.RandomDouble() >= MembershipChance)
            {
                return -1;
            }

            double r = Utility.RandomDouble() * _totalWeight;
            double acc = 0;
            for (int i = 0; i < All.Length; i++)
            {
                acc += All[i].Weight;
                if (r <= acc)
                {
                    return i;
                }
            }
            return All.Length - 1;
        }

        // Safe lookup — returns null for -1 / stale indices from old saves.
        public static BotGuildDef Get(int index) =>
            index >= 0 && index < All.Length ? All[index] : null;

        public static void Configure()
        {
            CommandSystem.Register("BotGuilds", AccessLevel.GameMaster, Roster_OnCommand);
        }

        [Usage("BotGuilds")]
        [Description("Lists bot guilds with live member counts.")]
        private static void Roster_OnCommand(CommandEventArgs e)
        {
            var counts = new int[All.Length];
            int unguilded = 0, total = 0;

            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted)
                {
                    continue;
                }
                total++;
                if (bot.BotGuildIndex >= 0 && bot.BotGuildIndex < counts.Length)
                {
                    counts[bot.BotGuildIndex]++;
                }
                else
                {
                    unguilded++;
                }
            }

            e.Mobile.SendMessage($"Bot guilds ({total} live bots, {unguilded} unguilded):");
            for (int i = 0; i < All.Length; i++)
            {
                e.Mobile.SendMessage($"  [{All[i].Tag}] {All[i].Name}: {counts[i]}");
            }
        }
    }
}
