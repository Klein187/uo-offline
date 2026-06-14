// =========================================================================
// SpawnBotCommand.cs — [SpawnBot admin command.
//
// Drops a SINGLE PlayerBot of a chosen class at your feet. Unlike
// [BotSpawnerHere (which places a spawner that produces random-class
// bots over time), this is for testing a specific class right now —
// e.g. spawning a Mage to watch its combat.
//
// Use:
//   [SpawnBot mage              - a Mage, random skill tier
//   [SpawnBot mage grandmaster  - a Grandmaster Mage
//   [SpawnBot archer adept      - an Adept Archer
//   [SpawnBot                   - lists the valid class / tier names
//
// Classes: Warrior, Mage, Fencer, Archer, Tamer, Crafter, Healer,
//          Thief, Bard, Ranger
// Tiers:   Novice, Apprentice, Journeyman, Adept, Expert, Master,
//          Grandmaster   (omitted = random)
//
// The spawned bot starts in IdleBehavior; the lifecycle manager will
// pick it up and transition it normally. To make it fight immediately,
// attack it or set its behavior with the existing behavior commands.
// =========================================================================

using System;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class SpawnBotCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("SpawnBot", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("SpawnBot [class] [tier]")]
        [Description(
            "Spawns one PlayerBot of a chosen class at your location. " +
            "Class is required; tier is optional (random if omitted).")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null || from.Map == null || from.Map == Map.Internal)
            {
                return;
            }

            // No class given — show usage.
            if (e.Arguments.Length < 1)
            {
                from.SendMessage("Usage: [SpawnBot <class> [tier]");
                from.SendMessage("Classes: " + string.Join(", ",
                    Enum.GetNames(typeof(BotClass))));
                from.SendMessage("Tiers: " + string.Join(", ",
                    Enum.GetNames(typeof(BotSkillTier))) + " (optional)");
                return;
            }

            // Parse class (case-insensitive).
            if (!Enum.TryParse<BotClass>(e.Arguments[0], true, out var cls))
            {
                from.SendMessage($"Unknown class '{e.Arguments[0]}'. Valid: " +
                    string.Join(", ", Enum.GetNames(typeof(BotClass))));
                return;
            }

            // Parse tier if given, otherwise random.
            BotSkillTier tier;
            if (e.Arguments.Length >= 2)
            {
                if (!Enum.TryParse<BotSkillTier>(e.Arguments[1], true, out tier))
                {
                    from.SendMessage($"Unknown tier '{e.Arguments[1]}'. Valid: " +
                        string.Join(", ", Enum.GetNames(typeof(BotSkillTier))));
                    return;
                }
            }
            else
            {
                tier = BotSkillTierHelper.RollRandom();
            }

            // Build and place the bot.
            var bot = new PlayerBot(cls, tier);
            bot.MoveToWorld(from.Location, from.Map);

            from.SendMessage(
                $"Spawned {tier} {cls} '{bot.Name}' at " +
                $"({from.X},{from.Y},{from.Z}).");
        }
    }
}
