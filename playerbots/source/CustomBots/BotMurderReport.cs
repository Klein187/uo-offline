// =========================================================================
// BotMurderReport.cs — bots report their murderers.
//
// In T2A a murder count is not awarded by the kill itself. The victim has
// to answer the "Would you like to report <name> for murder?" gump that
// pops four seconds after death, and only then does the killer take a
// count. That gump is sent to a NetState, and a bot has no NetState, so
// every bot a player cut down died without ever reporting it: a player
// could murder the whole of Britain and stay blue forever.
//
// So the bot answers the gump itself. On death it walks its own aggressor
// list and reports anyone the engine already flagged as a criminal
// aggressor — the same list, the same flags, the same PlayerMurderSystem
// call the gump's "Yes" button makes.
//
// What that flag means is doing all the real work here, and it is why
// this cannot simply count kills:
//
//   * Only a HARMFUL CRIMINAL act sets it (Mobile.DoHarmful), i.e. the
//     attacker was innocent-to-victim at the time. Killing a red, a gray
//     or a guild-war enemy is not murder and never has been.
//   * PlayerBot.IsHarmfulCriminal already exempts Order/Chaos war and
//     bot duels, so faction fights and duels stay countless.
//   * Healing back to full HP clears it (Mobile.Hits), so a fight the
//     victim actually recovered from is not a murder either.
//
// Run BEFORE base.OnDeath: the stock handler on PlayerDeathEvent consumes
// these same flags to build its gump, so reporting first leaves it with
// nothing to ask about and no gump is queued into the void. Its other job
// — handing out fame for the kill — still runs.
// =========================================================================

using System.Collections.Generic;
using Server.Engines.PlayerMurderSystem;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotMurderReport
    {
        // ---- Knobs ----

        public static bool Enabled = true;

        // Bots report each other too, so a PK earns its reputation the way
        // a player does instead of only wearing the counts it spawned with.
        // Turn this off to leave counts to real players' kills only.
        public static bool ReportBotKillers = true;

        // -------------------------------------------------------------------
        // Called from PlayerBot.OnDeath, before base.OnDeath.
        // -------------------------------------------------------------------
        public static void OnBotDeath(PlayerBot victim)
        {
            if (!Enabled || victim == null)
            {
                return;
            }

            // Guards won't take reports of the death of a thief.
            if (victim.NpcGuild == NpcGuild.ThievesGuild)
            {
                return;
            }

            List<Mobile> killers = null;

            foreach (var ai in victim.Aggressors)
            {
                if (ai.Attacker is not PlayerMobile attacker || attacker.Deleted)
                {
                    continue;
                }

                if (!ai.CanReportMurder || ai.Reported)
                {
                    continue;
                }

                if (!ReportBotKillers && attacker is PlayerBot)
                {
                    continue;
                }

                // Claim the flags now so the stock gump handler skips them.
                ai.Reported = true;
                ai.CanReportMurder = false;

                if (PlayerMurderSystem.IsRecentlyReported(victim, attacker))
                {
                    continue;
                }

                killers ??= new List<Mobile>();
                killers.Add(attacker);
            }

            if (killers == null)
            {
                return;
            }

            // Reporting outside the loop: awarding a count touches karma and
            // notoriety on the killer, and nothing that walks an aggressor
            // list should be doing it mid-iteration.
            for (var i = 0; i < killers.Count; i++)
            {
                PlayerMurderSystem.ReportMurder(victim, killers[i]);
            }
        }
    }
}
