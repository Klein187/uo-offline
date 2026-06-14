// =========================================================================
// FieldCommands.cs — Admin tooling for the destination distance-field cache.
//
//   [rebuildfields        Build/rebuild a local approach field for every
//                         destination in DestinationCatalog.
//   [fieldinfo            Report coverage; flag tiny fields (bad coords).
//   [testapproach <name>  Walk the nearest bot's final approach into the
//                         named destination via its field. Diagnostic only.
//
// Note: this codebase's Mobile.SendMessage has no params/format overload —
// it's SendMessage(string) or SendMessage(int hue, string). All messages
// below use interpolated single-string form. Args are read via e.Arguments
// / e.ArgString to match the other commands in this project.
// =========================================================================

using System;
using System.Linq;
using Server;
using Server.Commands;
using Server.Mobiles;

namespace Server.CustomBots
{
    public static class FieldCommands
    {
        public static void Configure()
        {
            CommandSystem.Register("rebuildfields", AccessLevel.GameMaster, RebuildFields_OnCommand);
            CommandSystem.Register("fieldinfo",     AccessLevel.GameMaster, FieldInfo_OnCommand);
            CommandSystem.Register("testapproach",  AccessLevel.GameMaster, TestApproach_OnCommand);
        }

        [Usage("rebuildfields")]
        [Description("Build/rebuild a local approach field for every destination.")]
        private static void RebuildFields_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;

            if (DestinationCatalog.Count == 0)
            {
                from.SendMessage("DestinationCatalog is empty — load destinations.json first.");
                return;
            }

            from.SendMessage($"Building approach fields for {DestinationCatalog.Count} destination(s)...");

            var (built, tiles, ms) = DestinationFieldCache.BuildAll(force: true);

            from.SendMessage($"Built {built} field(s), {tiles:n0} total tiles, in {ms:0} ms.");
            from.SendMessage("Use [fieldinfo for the per-destination breakdown.");
        }

        [Usage("fieldinfo")]
        [Description("List destinations with their approach-field coverage.")]
        private static void FieldInfo_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (DestinationFieldCache.Count == 0)
            {
                from.SendMessage("No fields built. Run [rebuildfields first.");
                return;
            }

            int tiny = 0;
            foreach (var dest in DestinationCatalog.All)
            {
                var f = DestinationFieldCache.Get(dest.Name);
                int covered = f?.CoveredTiles ?? 0;
                if (f == null)
                {
                    from.SendMessage($"{dest.Name} [{dest.Type}] — NO FIELD");
                }
                else if (covered < 30)
                {
                    tiny++;
                    from.SendMessage($"{dest.Name} [{dest.Type}] @ {dest.Location} — {covered} tiles !! TINY (bad coord?)");
                }
            }

            long total = DestinationCatalog.All
                .Select(d => DestinationFieldCache.Get(d.Name))
                .Where(f => f != null)
                .Sum(f => (long)f.CoveredTiles);

            from.SendMessage($"{DestinationFieldCache.Count} field(s), {total:n0} total tiles. {tiny} flagged tiny.");
        }

        [Usage("testapproach <destination name>")]
        [Description("Route the nearest PlayerBot's final approach via the named destination's field.")]
        private static void TestApproach_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            string name = e.ArgString?.Trim() ?? "";
            if (name.Length == 0)
            {
                from.SendMessage("Usage: [testapproach <destination name>");
                return;
            }

            var dest = DestinationCatalog.GetByName(name);
            if (dest == null)
            {
                from.SendMessage($"No destination named '{name}'.");
                return;
            }

            var field = DestinationFieldCache.Get(dest.Name);
            if (field == null)
            {
                from.SendMessage($"No field for '{dest.Name}'. Run [rebuildfields.");
                return;
            }

            PlayerBot nearest = null;
            int best = int.MaxValue;
            foreach (var m in from.GetMobilesInRange(30))
            {
                if (m is PlayerBot pb)
                {
                    int d = (int)from.GetDistanceToSqrt(m);
                    if (d < best) { best = d; nearest = pb; }
                }
            }
            if (nearest == null)
            {
                from.SendMessage("No PlayerBot within 30 tiles to test with.");
                return;
            }

            from.SendMessage($"Walking {nearest.Name} into '{dest.Name}' @ {dest.Location} via field ({field.CoveredTiles} tiles)...");
            ApproachTestTimer.Start(nearest, dest, field, from);
        }
    }

    // Minimal driver so [testapproach works standalone. The real integration
    // lives in TravelerBehavior; this is a diagnostic.
    public sealed class ApproachTestTimer : Timer
    {
        private readonly PlayerBot _bot;
        private readonly BotDestination _dest;
        private readonly DistanceField _field;
        private readonly Mobile _report;
        private int _ticks;
        private const int MaxTicks = 600;

        private ApproachTestTimer(PlayerBot bot, BotDestination dest,
                                  DistanceField field, Mobile report)
            : base(TimeSpan.FromSeconds(0.3), TimeSpan.FromSeconds(0.3))
        {
            _bot = bot; _dest = dest; _field = field; _report = report;
        }

        public static void Start(PlayerBot bot, BotDestination dest,
                                 DistanceField field, Mobile report)
            => new ApproachTestTimer(bot, dest, field, report).Start();

        protected override void OnTick()
        {
            if (_bot == null || _bot.Deleted) { Stop(); return; }
            if (++_ticks > MaxTicks)
            {
                _report?.SendMessage($"Approach gave up after {MaxTicks} ticks (bot at {_bot.Location}).");
                Stop();
                return;
            }

            switch (FieldApproach.Step(_bot, _field, _dest.Location, 1))
            {
                case ApproachResult.Arrived:
                    _report?.SendMessage($"{_bot.Name} reached '{_dest.Name}' in {_ticks} ticks.");
                    Stop();
                    break;

                case ApproachResult.NoField:
                    _report?.SendMessage(
                        $"{_bot.Name} is outside the field for '{_dest.Name}' — too far for the " +
                        $"local approach (the waypoint graph would deliver it into range first).");
                    Stop();
                    break;

                case ApproachResult.Blocked:
                    if (_ticks > 30) { _report?.SendMessage("Blocked at coverage edge."); Stop(); }
                    break;
            }
        }
    }
}
