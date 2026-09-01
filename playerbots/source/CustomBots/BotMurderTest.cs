// =========================================================================
// BotMurderTest.cs — proof that killing a bot earns a murder count.
//
// The whole point of BotMurderReport is a number that used to stay at
// zero, so it needs a test that reads the number. This one drives the
// real path end to end: a throwaway REAL PlayerMobile (not a bot — the
// case the bug was reported for) walks onto a live innocent bot, swings
// at it through Mobile.DoHarmful so the engine sets its own criminal
// flags, and kills it with real damage. Then it reads Kills.
//
// Six kills in a row is the interesting length: the fifth is where the
// killer is supposed to turn red, and a shard where counts land but the
// notoriety never flips is still broken for the player who asked.
//
// The negative control matters as much: the same rig kills a murderer
// too. A red is not a murder in any era of UO, and if that one also
// counts then the flags aren't being read, kills are.
//
// Two of the kills are real fights rather than one-shots, because the
// engine rewrites the criminal flag on every harmful act after the first
// and the interesting question is whether the original one survives:
//
//   BRAWL   the victim fights back, then takes another swing. It has to
//           still count — the whole of PvP looks like this.
//   HEALED  the victim is healed to full mid-fight, which clears every
//           report flag on it (Mobile.Hits), and is then attacked again.
//           The next criminal swing has to re-arm the flag.
//
// Then the rig stands in the Britain bank as a red for a while, because
// the count is only worth anything if the street reacts to it. Watch the
// console for a bot yelling for the guards, and the guards arriving.
//
//   [TestMurders [n] [linger]   — run it, results to the caller + console.
//   murder_request.txt          — headless: "token [n] [linger]".
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Commands;
using Server.Engines.PlayerMurderSystem;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotMurderTest
    {
        public static void Configure()
        {
            CommandSystem.Register("TestMurders", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var count = e.Length > 0 ? Math.Clamp(e.GetInt32(0), 1, 20) : 6;
            var linger = e.Length > 1 ? Math.Clamp(e.GetInt32(1), 0, 600) : DefaultLinger;
            foreach (var line in Run(count, linger))
            {
                e.Mobile.SendMessage(line.StartsWith("FAIL") ? 0x22 : 0x3F, line);
            }
        }

        // Long enough for several BotPKWatch ticks (15s each) to land.
        public const int DefaultLinger = 100;

        // How far a town NPC has to be for the stock guard system NOT to
        // whack the red the instant it arrives: GuardedRegion spawns a
        // guard immediately if a human townsperson is within 8 tiles of a
        // guard candidate, which is why standing the rig on the Britain
        // bank steps proved nothing — the banker had it killed before the
        // bots' 15-second sweep ever ran.
        private const int NpcClearance = 9;

        public static List<string> Run(int kills = 6, int lingerSeconds = DefaultLinger)
        {
            var findings = new List<string>();

            var innocents = new List<PlayerBot>();
            PlayerBot red = null;

            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted || !bot.Alive ||
                    bot.Map == null || bot.Map == Map.Internal)
                {
                    continue;
                }

                if (bot.Murderer)
                {
                    red ??= bot;
                    continue;
                }

                if (bot.Criminal || bot.Blessed || bot.AccessLevel > AccessLevel.Player)
                {
                    continue;
                }

                if (innocents.Count < kills)
                {
                    innocents.Add(bot);
                }

                if (innocents.Count >= kills && red != null)
                {
                    break;
                }
            }

            if (innocents.Count == 0)
            {
                findings.Add("FAIL no innocent bots alive to test against");
                return findings;
            }

            var rig = new PlayerMobile
            {
                Name = "Murder Test",
                Body = 0x190,
                Hue = 0x83EA,
                // Login stamps this; without it the rig is an NPC and none
                // of the player-only branches (murder counts included) run.
                Player = true,
                RawStr = 100,
                RawDex = 100,
                RawInt = 100
            };
            rig.Hits = rig.HitsMax;

            try
            {
                for (var i = 0; i < innocents.Count; i++)
                {
                    // The last two innocents get fought instead of one-shot.
                    var style = i == innocents.Count - 2 ? KillStyle.Brawl
                        : i == innocents.Count - 1 ? KillStyle.Healed
                        : KillStyle.OneShot;

                    findings.Add(KillAndReport(rig, innocents[i], true, style));
                }

                if (red != null)
                {
                    findings.Add(KillAndReport(rig, red, false, KillStyle.OneShot));
                }
                else
                {
                    findings.Add("SKIP no murderer alive for the negative control");
                }
            }
            catch
            {
                Cleanup(rig);
                throw;
            }

            var wasRed = rig.Murderer;
            Cleanup(rig);

            if (lingerSeconds > 0 && wasRed)
            {
                // A FRESH red for the standing-around half. The one that did
                // the killing is in no state to be watched: six victims all
                // fight back, and a rig with no armour and no weapon loses
                // that. It reached the watch spot as a corpse, which reads
                // in the log exactly like a broken feature.
                var redRig = new PlayerMobile
                {
                    Name = "Red Test",
                    Body = 0x190,
                    Hue = 0x83EA,
                    Player = true,
                    RawStr = 100,
                    RawDex = 100,
                    RawInt = 100,
                    Kills = 5 // Murderer is Kills >= 5, and no fight to show for it
                };

                var spot = FindWatchSpot(redRig, out var inTown);
                if (spot == null)
                {
                    findings.Add("SKIP no bot standing anywhere a red could be watched");
                    redRig.Delete();
                }
                else
                {
                    redRig.MoveToWorld(spot.Location, spot.Map);
                    redRig.Hits = redRig.HitsMax;

                    findings.Add(
                        $"WATCH fresh red at {spot.Name}'s tile for {lingerSeconds}s — expect " +
                        (inTown ? "a bot to yell for the guards" : "a bot to raise the alarm"));

                    Probe(redRig, lingerSeconds);
                    Timer.DelayCall(TimeSpan.FromSeconds(lingerSeconds), Cleanup, redRig);
                }
            }

            foreach (var line in findings)
            {
                Console.WriteLine($"[TestMurders] {line}");
            }

            return findings;
        }

        // While the red stands there, say out loud what BotPKWatch would
        // see if it looked right now. A silent linger otherwise leaves you
        // guessing which of half a dozen gates was the one that shut.
        private static void Probe(PlayerMobile rig, int lingerSeconds)
        {
            var left = lingerSeconds;

            Timer.DelayCall(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(15),
                lingerSeconds / 15,
                () =>
                {
                    left -= 15;

                    if (rig.Deleted)
                    {
                        Console.WriteLine("[TestMurders] probe: rig deleted");
                        return;
                    }

                    var region = rig.Region?.GetRegion<Server.Regions.GuardedRegion>();
                    var bots = 0;
                    var witnesses = 0;

                    if (rig.Map != null && rig.Map != Map.Internal)
                    {
                        foreach (var m in rig.Map.GetMobilesInRange(rig.Location, 12))
                        {
                            if (m is not PlayerBot civ || civ == rig)
                            {
                                continue;
                            }

                            bots++;

                            if (civ.Alive && !civ.LoggingOut &&
                                civ.Behavior is not PKBehavior and not GhostBehavior &&
                                civ.CanSee(rig))
                            {
                                witnesses++;
                            }
                        }
                    }

                    Console.WriteLine(
                        $"[TestMurders] probe {left}s left: alive={rig.Alive} red={rig.Murderer} " +
                        $"player={rig.Player} access={rig.AccessLevel} " +
                        $"region={region?.Name ?? "none"} " +
                        $"candidate={region?.IsGuardCandidate(rig) ?? false} " +
                        $"botsInRange={bots} witnesses={witnesses}");
                });
        }

        // Somewhere the reaction can actually be observed: standing on a
        // live bot, so there is a witness in range by construction. A spot
        // inside a guarded town is worth more (it exercises the guard call
        // as well as the alarm), but only if the stock guard system won't
        // get there first — hence the clearance from town NPCs.
        private static PlayerBot FindWatchSpot(Mobile rig, out bool inTown)
        {
            PlayerBot fallback = null;
            inTown = false;

            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted || !bot.Alive || bot == rig ||
                    bot.Map == null || bot.Map == Map.Internal || bot.Murderer)
                {
                    continue;
                }

                if (bot.Behavior is PKBehavior or GhostBehavior)
                {
                    continue;
                }

                var guarded = bot.Region?.GetRegion<Server.Regions.GuardedRegion>();
                if (guarded == null || guarded.IsDisabled())
                {
                    fallback ??= bot;
                    continue;
                }

                if (!HasTownNpcNear(bot))
                {
                    inTown = true;
                    return bot;
                }
            }

            return fallback;
        }

        private static bool HasTownNpcNear(PlayerBot bot)
        {
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, NpcClearance))
            {
                if (!m.Player && m.Body.IsHuman && m.Alive && !m.Deleted)
                {
                    return true;
                }
            }

            return false;
        }

        // The rig is a real mobile in a live world: it leaves a corpse if
        // the guards get it, and that corpse outlives the mobile.
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

        private enum KillStyle
        {
            OneShot,
            Brawl,  // victim fights back, killer swings again
            Healed  // victim is healed to full, killer swings again
        }

        // shouldCount: whether this kill is supposed to award a count.
        private static string KillAndReport(
            PlayerMobile rig, PlayerBot victim, bool shouldCount, KillStyle style
        )
        {
            var name = victim.Name;
            rig.MoveToWorld(victim.Location, victim.Map);

            var noto = Notoriety.Compute(rig, victim);
            var criminal = rig.IsHarmfulCriminal(victim);

            rig.DoHarmful(victim);
            victim.Damage(1, rig);

            if (style == KillStyle.Brawl)
            {
                victim.DoHarmful(rig);
                rig.DoHarmful(victim);
            }
            else if (style == KillStyle.Healed)
            {
                victim.Hits = victim.HitsMax;
                rig.DoHarmful(victim);
            }

            var flagged = CanReport(victim, rig);
            var fightsBack = victim.Combatant == rig;
            var before = rig.Kills;

            victim.Damage(victim.Hits + 10, rig);

            var after = rig.Kills;
            var gained = after - before;
            var ok = shouldCount ? gained == 1 : gained == 0;

            return $"{(ok ? "OK  " : "FAIL")} {style} {(shouldCount ? "innocent" : "murderer")} " +
                   $"{name}: noto={noto} criminal={criminal} canReport={flagged} " +
                   $"fightsBack={fightsBack} dead={!victim.Alive} " +
                   $"kills {before}->{after} red={rig.Murderer}";
        }

        private static bool CanReport(Mobile victim, Mobile killer)
        {
            foreach (var ai in victim.Aggressors)
            {
                if (ai.Attacker == killer)
                {
                    return ai.CanReportMurder;
                }
            }

            return false;
        }
    }
}
