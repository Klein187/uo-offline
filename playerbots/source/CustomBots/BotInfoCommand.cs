// =========================================================================
// BotInfoCommand.cs — [BotInfo
//
// Target a PlayerBot and dump their class, tier, stats, and all skills
// with non-zero values to the chat window. Useful for verifying that
// the skill template system is working as expected.
//
// Sample output:
//   --- Halric ---
//   Class: Warrior   Tier: Master
//   Str/Dex/Int: 94 / 71 / 47
//   HP: 94/94   Stam: 71/71   Mana: 47/47
//   Skills:
//     Swords:       89.7
//     Tactics:      87.2
//     Anatomy:      86.5
//     Healing:      83.1
//     Parrying:     84.0
//     Magic Resist: 85.4
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;
using Server.Targeting;

namespace Server.CustomBots
{
    public static class BotInfoCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("BotInfo", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("BotInfo")]
        [Description("Target a PlayerBot to see their class, tier, stats, and skills.")]
        public static void OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage("Target a PlayerBot to view their stats and skills.");
            e.Mobile.Target = new BotInfoTarget();
        }

        private class BotInfoTarget : Target
        {
            public BotInfoTarget() : base(12, false, TargetFlags.None) { }

            protected override void OnTarget(Mobile from, object targeted)
            {
                if (targeted is not PlayerBot bot)
                {
                    from.SendMessage("That's not a PlayerBot.");
                    return;
                }

                // Header
                from.SendMessage(0x35, $"--- {bot.Name} ---");
                from.SendMessage(0x3B2,
                    $"Class: {BotClassHelper.DisplayName(bot.Class)}   " +
                    $"Tier: {BotSkillTierHelper.DisplayName(bot.SkillTier)}");

                // Stats
                from.SendMessage(0x3B2,
                    $"Str/Dex/Int: {bot.RawStr} / {bot.RawDex} / {bot.RawInt}");
                from.SendMessage(0x3B2,
                    $"HP: {bot.Hits}/{bot.HitsMax}   " +
                    $"Stam: {bot.Stam}/{bot.StamMax}   " +
                    $"Mana: {bot.Mana}/{bot.ManaMax}");

                // Behavior
                from.SendMessage(0x3B2,
                    $"Behavior: {bot.Behavior?.SerializableName ?? "Idle"}");

                // Travel state — only meaningful for Travelers (other
                // behaviors are post-arrival or stationary).
                if (bot.Behavior is TravelerBehavior tv)
                {
                    string dest = string.IsNullOrEmpty(tv.DestinationName)
                        ? "—" : tv.DestinationName;
                    string leg = tv.CurrentLegWaypoint ?? "—";
                    from.SendMessage(0x3B2,
                        $"Destination: {dest}");
                    from.SendMessage(0x3B2,
                        $"Current waypoint: {leg}   Leg: {tv.LegProgress}");
                }

                // Notoriety / guard-relevant flags. These are what guards
                // check when deciding whether to attack. If Criminal or
                // Murderer is true on a freshly-spawned bot, that's the
                // bug we're chasing.
                from.SendMessage(0x35, "Notoriety:");
                from.SendMessage(0x3B2,
                    $"  Criminal: {bot.Criminal}   Murderer: {bot.Murderer}   Kills: {bot.Kills}");
                from.SendMessage(0x3B2,
                    $"  Karma: {bot.Karma}   Fame: {bot.Fame}");
                from.SendMessage(0x3B2,
                    $"  AccessLevel: {bot.AccessLevel}   Player: {bot.Player}");

                // Skills with non-zero values, sorted highest first.
                var nonzero = new List<(string name, double val)>();
                foreach (var skill in bot.Skills)
                {
                    if (skill.Base > 0.0)
                        nonzero.Add((skill.SkillName.ToString(), skill.Base));
                }

                if (nonzero.Count == 0)
                {
                    from.SendMessage(0x3B2, "Skills: (none above 0)");
                    return;
                }

                nonzero.Sort((a, b) => b.val.CompareTo(a.val));

                from.SendMessage(0x35, "Skills:");
                foreach (var (name, val) in nonzero)
                {
                    // Pad name to ~14 chars for column alignment.
                    string padded = (name + ":").PadRight(16);
                    from.SendMessage(0x3B2, $"  {padded}{val:F1}");
                }
            }
        }
    }
}
