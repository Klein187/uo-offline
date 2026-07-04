// =========================================================================
// BotStatusPage.cs — the old-school shard status page (IDEAS 6.3).
//
// Every minute, writes Data/Live/status.html: who's "online" (name,
// guild tag, class/tier, what they're doing, where), the population vs
// the daily curve, live hunts and fights, and the latest journal events
// — exactly the page every 1999 shard had, except this one's telling
// the truth about 400 bots. Auto-refreshes in the browser.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Server;

namespace Server.CustomBots
{
    public static class BotStatusPage
    {
        public static bool Enabled = true;

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
        private static Timer _timer;

        private static string PagePath =>
            Path.Combine(Core.BaseDirectory, "Data", "Live", "status.html");

        public static void Configure()
        {
            _timer = Timer.DelayCall(Interval, Interval, Write);
        }

        private static void Write()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                var sb = new StringBuilder(64 * 1024);
                BuildPage(sb);
                Directory.CreateDirectory(Path.GetDirectoryName(PagePath));
                File.WriteAllText(PagePath, sb.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[status] page write failed: {ex.Message}");
            }
        }

        private static void BuildPage(StringBuilder sb)
        {
            var bots = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted && bot.Map != Map.Internal)
                {
                    bots.Add(bot);
                }
            }
            bots.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.Append("<meta http-equiv='refresh' content='60'>");
            sb.Append("<title>Shard Status</title><style>");
            sb.Append("body{background:#1a1408;color:#d8c58a;font-family:Verdana,sans-serif;font-size:12px;margin:20px}");
            sb.Append("h1,h2{color:#f0dca0;border-bottom:1px solid #6b5a2a}");
            sb.Append("table{border-collapse:collapse}td,th{padding:2px 10px;text-align:left;border-bottom:1px solid #33290f}");
            sb.Append("th{color:#f0dca0}.dim{color:#8a7a4a}.red{color:#d06050}.grn{color:#88b060}");
            sb.Append("</style></head><body>");

            sb.Append("<h1>Shard Status</h1>");
            sb.Append($"<p class='dim'>as of {DateTime.Now:HH:mm:ss} — refreshes every minute</p>");

            sb.Append($"<p><b class='grn'>{bots.Count}</b> online — curve target " +
                      $"{BotSessionManager.TargetNow} ({BotSessionManager.CurveNow:P0} " +
                      $"of {BotPopulation.TargetCount} at {DateTime.Now.Hour:00}:00). " +
                      $"Hunting parties: {BotPartyManager.Parties.Count}.</p>");

            // ---- Recent events ----
            sb.Append("<h2>Latest News</h2><table>");
            foreach (var ev in BotEventJournal.Recent(15))
            {
                string what = ev.Type switch
                {
                    "pk"      => $"<span class='red'>{H(ev.Other)} murdered {H(ev.Actor)}</span>",
                    "faction" => $"<span class='red'>{H(ev.Other)} slew {H(ev.Actor)} in the shield war</span>",
                    "death"   => $"{H(ev.Actor)} was killed" +
                                 (ev.Other.Length > 0 ? $" by {H(ev.Other)}" : ""),
                    "red"     => $"<span class='red'>{H(ev.Actor)} spotted the murderer {H(ev.Other)}</span>",
                    "duel"    => $"{H(ev.Actor)} won a duel against {H(ev.Other)}",
                    "kill"    => $"{H(ev.Actor)} slew {H(ev.Other)}",
                    "party"   => $"{H(ev.Actor)} led a hunt to {H(ev.Other)}",
                    "login"   => $"<span class='dim'>{H(ev.Actor)} logged in</span>",
                    "logout"  => $"<span class='dim'>{H(ev.Actor)} logged out</span>",
                    _         => $"{H(ev.Actor)} {H(ev.Type)}",
                };
                sb.Append($"<tr><td class='dim'>{ev.At:HH:mm}</td><td>{what}</td>" +
                          $"<td class='dim'>{H(ev.Place)}</td></tr>");
            }
            sb.Append("</table>");

            // ---- Who's online ----
            sb.Append($"<h2>Who's Online ({bots.Count})</h2><table>");
            sb.Append("<tr><th>Name</th><th>Guild</th><th>Class</th><th>Doing</th><th>Where</th></tr>");
            foreach (var bot in bots)
            {
                var guild = BotGuilds.Get(bot.BotGuildIndex);
                string doing = bot.Alive
                    ? bot.Behavior?.SerializableName ?? "?"
                    : "GHOST";
                sb.Append("<tr>");
                sb.Append($"<td>{H(bot.Name)}</td>");
                sb.Append($"<td class='dim'>{(guild != null ? H(guild.Tag) : "")}</td>");
                sb.Append($"<td class='dim'>{bot.SkillTier} {BotClassHelper.DisplayName(bot.Class)}</td>");
                sb.Append($"<td>{doing}</td>");
                sb.Append($"<td class='dim'>{H(BotEventJournal.PlaceName(bot.Location, bot.Map))}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table></body></html>");
        }

        private static string H(string s) =>
            string.IsNullOrEmpty(s)
                ? ""
                : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
