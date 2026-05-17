// =========================================================================
// BotDiagnosticCommands.cs
//
// [BotGoals — Lists every PlayerBot with their behavior and (for Travelers)
// current destination + leg progress. Use this to verify travelers are
// actually using the waypoint graph correctly.
//
// [SetBotVerbose true|false — Toggle TravelerBehavior console logging.
// =========================================================================

using System;
using System.Reflection;
using System.Text;
using Server;
using Server.Commands;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class BotDiagnosticCommands
    {
        public static void Configure()
        {
            CommandSystem.Register("BotGoals",       AccessLevel.GameMaster, BotGoals_OnCommand);
            CommandSystem.Register("SetBotVerbose",  AccessLevel.GameMaster, SetBotVerbose_OnCommand);
        }

        [Usage("BotGoals")]
        [Description("Lists every PlayerBot's current behavior and (for Travelers) destination + leg progress.")]
        public static void BotGoals_OnCommand(CommandEventArgs e)
        {
            int count = 0;
            int travelerCount = 0;
            var sb = new StringBuilder();
            sb.AppendLine("--- PlayerBot Goals ---");

            foreach (var mobile in World.Mobiles.Values)
            {
                if (mobile is not PlayerBot bot || bot.Deleted || bot.Map == Map.Internal)
                    continue;

                count++;
                var behavior = bot.Behavior;
                var behaviorName = behavior?.SerializableName ?? "<none>";

                string detail = "";

                if (behavior is TravelerBehavior traveler)
                {
                    travelerCount++;
                    detail = FormatTravelerInfo(traveler);
                }

                sb.AppendLine(
                    $"{bot.Name,-15} @ ({bot.X,5},{bot.Y,5})  " +
                    $"{behaviorName,-10}  {detail}");
            }

            sb.AppendLine($"--- Total: {count} bots ({travelerCount} Travelers) ---");

            // Print to both: server console (so it's also in the log) AND
            // to the calling Mobile as a chat message.
            string text = sb.ToString();
            Console.WriteLine(text);
            foreach (var line in text.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    e.Mobile.SendMessage(line.TrimEnd());
            }
        }

        // Reach into TravelerBehavior's private state via reflection.
        // Cheap and harmless for a diagnostic command — far less invasive
        // than exposing every field as public.
        private static string FormatTravelerInfo(TravelerBehavior tb)
        {
            try
            {
                string dest = tb.DestinationName ?? "<none>";

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var t = typeof(TravelerBehavior);

                var pathField = t.GetField("_plannedPath", flags);
                var legField  = t.GetField("_legIndex",    flags);
                var arrivedF  = t.GetField("_hasArrived",  flags);

                var path = pathField?.GetValue(tb) as System.Collections.Generic.List<string>;
                int leg  = legField  != null ? (int)legField.GetValue(tb)  : -1;
                bool arr = arrivedF  != null && (bool)arrivedF.GetValue(tb);

                if (arr)
                    return $"-> {dest}  ARRIVED ({tb.Arrival})";

                if (path == null || path.Count == 0)
                    return $"-> {dest}  NO PATH";

                string currentLeg = leg >= 0 && leg < path.Count ? path[leg] : "?";
                return $"-> {dest}  leg {leg + 1}/{path.Count} ({currentLeg})";
            }
            catch (Exception ex)
            {
                return $"<reflection failed: {ex.Message}>";
            }
        }

        [Usage("SetBotVerbose true|false")]
        [Description("Toggle TravelerBehavior console logging.")]
        public static void SetBotVerbose_OnCommand(CommandEventArgs e)
        {
            if (e.Length < 1)
            {
                e.Mobile.SendMessage($"Current TravelerBehavior.Verbose = {TravelerBehavior.Verbose}");
                e.Mobile.SendMessage("Usage: [SetBotVerbose true   or   [SetBotVerbose false");
                return;
            }

            string arg = e.GetString(0).ToLowerInvariant();
            bool? v = arg switch
            {
                "true" or "1" or "on"  or "yes" => true,
                "false" or "0" or "off" or "no" => false,
                _ => null,
            };

            if (v == null)
            {
                e.Mobile.SendMessage($"Couldn't parse '{arg}'; use true/false.");
                return;
            }

            TravelerBehavior.Verbose = v.Value;
            e.Mobile.SendMessage($"TravelerBehavior.Verbose = {TravelerBehavior.Verbose}");
        }
    }
}
