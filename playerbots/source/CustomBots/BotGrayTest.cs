// =========================================================================
// BotGrayTest.cs — does flagging gray actually get you jumped?
//
// A throwaway REAL player (the case the feature was asked for) picks a
// live bot out in the countryside, hits it once, and stands there wearing
// the criminal flag. Everything after that is the shard's own doing:
// BotGrayWatch spots the flag, the bots that care draw, and their own
// behaviors chase. Watch the console for "[gray] <name> drew on Gray
// Test", and for the fight that follows.
//
// Out in the countryside on purpose. Committing the crime inside a town
// brings the stock guards down on the rig in about a second, which proves
// the guard system works and nothing at all about the bots.
//
//   [TestGray [linger]     — run it, results to the caller + console.
//   gray_request.txt       — headless: "token [linger]" -> gray_ack.json.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Commands;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotGrayTest
    {
        // Long enough for several BotGrayWatch sweeps (3s each), and for
        // whoever draws to walk over and land a hit.
        public const int DefaultLinger = 90;

        // A spot is only worth using if there is a crowd to react.
        private const int CrowdRange = 10;
        private const int CrowdWanted = 3;

        public static void Configure()
        {
            CommandSystem.Register("TestGray", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var linger = e.Length > 0 ? Math.Clamp(e.GetInt32(0), 0, 600) : DefaultLinger;
            foreach (var line in Run(linger))
            {
                e.Mobile.SendMessage(line.StartsWith("FAIL") ? 0x22 : 0x3F, line);
            }
        }

        public static List<string> Run(int lingerSeconds = DefaultLinger)
        {
            var findings = new List<string>();

            var victim = FindCountrysideBot();
            if (victim == null)
            {
                findings.Add("FAIL no bot standing outside a guarded region with a crowd around it");
                return findings;
            }

            var rig = new PlayerMobile
            {
                Name = "Gray Test",
                Body = 0x190,
                Hue = 0x83EA,
                Player = true,
                RawStr = 100,
                RawDex = 100,
                RawInt = 100
            };

            rig.MoveToWorld(victim.Location, victim.Map);
            rig.Hits = rig.HitsMax;

            // One swing at an innocent is the whole crime. DoHarmful runs
            // CriminalAction for us, exactly as a real weapon swing would.
            rig.DoHarmful(victim);
            victim.Damage(1, rig);

            findings.Add(
                $"{(rig.Criminal ? "OK  " : "FAIL")} flagged gray by hitting {victim.Name} " +
                $"at {BotEventJournal.PlaceName(rig.Location, rig.Map)}: " +
                $"criminal={rig.Criminal} crowd={CountCrowd(victim)}");

            if (!rig.Criminal || lingerSeconds <= 0)
            {
                Cleanup(rig);
                return Report(findings);
            }

            findings.Add(
                $"WATCH gray rig standing for {lingerSeconds}s — expect \"[gray] ... drew on Gray Test\"");

            Timer.DelayCall(TimeSpan.FromSeconds(lingerSeconds), Cleanup, rig);

            return Report(findings);
        }

        private static List<string> Report(List<string> findings)
        {
            foreach (var line in findings)
            {
                Console.WriteLine($"[TestGray] {line}");
            }

            return findings;
        }

        // A bot outside every guarded region, with company. The crowd is
        // what makes the test mean anything: one bot alone might roll
        // unwilling and prove nothing.
        private static PlayerBot FindCountrysideBot()
        {
            PlayerBot fallback = null;

            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted || !bot.Alive ||
                    bot.Map == null || bot.Map == Map.Internal ||
                    bot.Murderer || bot.Criminal)
                {
                    continue;
                }

                if (bot.Behavior is PKBehavior or GhostBehavior)
                {
                    continue;
                }

                if (bot.Region?.GetRegion<Server.Regions.GuardedRegion>() != null)
                {
                    continue;
                }

                fallback ??= bot;

                if (CountCrowd(bot) >= CrowdWanted)
                {
                    return bot;
                }
            }

            return fallback;
        }

        private static int CountCrowd(PlayerBot bot)
        {
            var n = 0;

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, CrowdRange))
            {
                if (m is PlayerBot other && other != bot && other.Alive && !other.Deleted)
                {
                    n++;
                }
            }

            return n;
        }

        private static void Cleanup(PlayerMobile rig)
        {
            if (rig.Corpse is { Deleted: false } corpse)
            {
                corpse.Delete();
            }

            if (!rig.Deleted)
            {
                rig.Delete();
            }
        }
    }
}
