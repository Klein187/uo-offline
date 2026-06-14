// =========================================================================
// BotWhereCommand.cs — find PlayerBots in the world.
//
// [BotWhere            — list every bot, grouped by region, with coords,
//                        behavior, and distance from you.
// [BotWhere <text>     — only bots whose name contains <text>.
// [BotGoto             — target a bot (or run [BotGoto <name>) to teleport
//                        yourself next to it.
//
// This answers "where are my bots?" — [BotGoals lists behavior/goals but
// without region context or a way to jump to a bot.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Text;
using Server;
using Server.Commands;
using Server.Targeting;

namespace Server.CustomBots
{
    public static class BotWhereCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("BotWhere", AccessLevel.GameMaster, OnWhere);
            CommandSystem.Register("BotGoto",  AccessLevel.GameMaster, OnGoto);
        }

        [Usage("BotWhere [name filter]")]
        [Description("List PlayerBots grouped by region, with coords and distance.")]
        public static void OnWhere(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null) return;

            string filter = e.ArgString?.Trim() ?? "";
            bool hasFilter = filter.Length > 0;

            // Bucket bots by region name.
            var byRegion = new SortedDictionary<string, List<PlayerBot>>(
                StringComparer.OrdinalIgnoreCase);
            int total = 0;

            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted || bot.Map == Map.Internal)
                    continue;
                if (hasFilter &&
                    bot.Name?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                total++;
                string region = bot.Region?.Name;
                if (string.IsNullOrEmpty(region)) region = "Wilderness";
                if (!byRegion.TryGetValue(region, out var list))
                {
                    list = new List<PlayerBot>();
                    byRegion[region] = list;
                }
                list.Add(bot);
            }

            if (total == 0)
            {
                from.SendMessage(hasFilter
                    ? $"No bots found matching '{filter}'."
                    : "No bots are currently in the world.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(hasFilter
                ? $"--- Bots matching '{filter}': {total} ---"
                : $"--- PlayerBots in the world: {total} ---");

            foreach (var kv in byRegion)
            {
                sb.AppendLine($"[{kv.Key}] — {kv.Value.Count}");
                foreach (var bot in kv.Value)
                {
                    // Distance from the GM, if on the same map.
                    string dist = "";
                    if (bot.Map == from.Map)
                    {
                        int dx = bot.X - from.X;
                        int dy = bot.Y - from.Y;
                        dist = $"  {(int)Math.Sqrt(dx * dx + dy * dy)}t away";
                    }
                    else if (bot.Map != null)
                    {
                        dist = $"  on {bot.Map}";
                    }

                    var behaviorName =
                        bot.Behavior?.SerializableName ?? "<none>";

                    sb.AppendLine(
                        $"  {bot.Name,-16} ({bot.X,5},{bot.Y,5},{bot.Z,3})  " +
                        $"{behaviorName,-11}{dist}");
                }
            }

            // Send to the GM line by line, and echo to console.
            string text = sb.ToString();
            Console.WriteLine(text);
            foreach (var line in text.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    from.SendMessage(line.TrimEnd());
            }

            from.SendMessage(0x3B2,
                "Tip: [BotGoto <name> teleports you to a bot.");
        }

        [Usage("BotGoto [name]")]
        [Description("Teleport to a PlayerBot — by name, or target one.")]
        public static void OnGoto(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null) return;

            string name = e.ArgString?.Trim() ?? "";

            if (name.Length == 0)
            {
                from.SendMessage("Target the bot to teleport to.");
                from.Target = new GotoTarget();
                return;
            }

            // Name given — find the closest match.
            PlayerBot match = null;
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted || bot.Map == Map.Internal)
                    continue;
                if (bot.Name == null) continue;
                if (bot.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    match = bot;
                    break;  // exact match wins
                }
                if (match == null &&
                    bot.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    match = bot;  // partial — keep looking for an exact
                }
            }

            if (match == null)
            {
                from.SendMessage($"No bot found matching '{name}'.");
                return;
            }

            GoTo(from, match);
        }

        private static void GoTo(Mobile from, PlayerBot bot)
        {
            if (bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                from.SendMessage("That bot is not in the world.");
                return;
            }
            from.MoveToWorld(bot.Location, bot.Map);
            from.SendMessage(0x35,
                $"Teleported to {bot.Name} at ({bot.X},{bot.Y},{bot.Z}).");
        }

        private class GotoTarget : Target
        {
            public GotoTarget() : base(-1, false, TargetFlags.None) { }

            protected override void OnTarget(Mobile from, object targeted)
            {
                if (targeted is PlayerBot bot)
                {
                    GoTo(from, bot);
                }
                else
                {
                    from.SendMessage("That isn't a PlayerBot.");
                }
            }
        }
    }
}
