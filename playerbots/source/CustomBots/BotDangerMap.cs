// =========================================================================
// BotDangerMap.cs — region danger reputation + PK ecology (IDEAS 3.2/3.3).
//
// Danger as INFORMATION that propagates through the population — exactly
// how it worked on real servers:
//
//   HEAT     Every journaled murder/death adds heat to its place name.
//            Heat decays (45-minute half-life), so "stay out of the
//            graveyard" fades unless the killing continues.
//   AVOID    Destination rolls consult the map: a hot place's weight
//            drops to a quarter — the population visibly drains away
//            from where the reds are working.
//   ALARM    A civilian that SPOTS a red broadcasts it — "RED AT BRIT
//            GY!!" — journals it (so the banks gossip about the
//            sighting), heats the place, and nearby travelers scatter
//            to freshly-rolled destinations.
//
//   [BotDanger — show current hot spots
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class BotDangerMap
    {
        public static bool Enabled = true;

        // Exponential decay half-life for heat.
        private static readonly TimeSpan HalfLife = TimeSpan.FromMinutes(45);

        // Heat thresholds → destination-weight multipliers.
        private const double HotHeat  = 3.0;  // multiple recent murders
        private const double WarmHeat = 1.5;

        // place name (journal PlaceName) -> (heat, last update)
        private static readonly Dictionary<string, (double heat, DateTime at)> _heat =
            new(StringComparer.OrdinalIgnoreCase);

        public static void Configure()
        {
            CommandSystem.Register("BotDanger", AccessLevel.GameMaster, Status_OnCommand);
        }

        public static void AddHeat(string place, double amount)
        {
            if (string.IsNullOrEmpty(place) || amount <= 0)
            {
                return;
            }
            double current = CurrentHeat(place);
            _heat[place] = (current + amount, Core.Now);

            if (_heat.Count > 500)
            {
                Prune();
            }
        }

        public static double CurrentHeat(string place)
        {
            if (string.IsNullOrEmpty(place) ||
                !_heat.TryGetValue(place, out var entry))
            {
                return 0;
            }
            double halves = (Core.Now - entry.at).TotalMinutes / HalfLife.TotalMinutes;
            return entry.heat * Math.Pow(0.5, halves);
        }

        private static void Prune()
        {
            var dead = new List<string>();
            foreach (var kv in _heat)
            {
                if (CurrentHeat(kv.Key) < 0.1)
                {
                    dead.Add(kv.Key);
                }
            }
            foreach (var k in dead)
            {
                _heat.Remove(k);
            }
        }

        // Destination-roll multiplier: hot places empty out. Checks the
        // destination's own name and its city (a murdered bank chills the
        // whole town a little less).
        public static double Multiplier(BotDestination dest)
        {
            if (!Enabled || dest == null)
            {
                return 1.0;
            }

            double h = CurrentHeat(dest.Name);
            if (!string.IsNullOrEmpty(dest.City))
            {
                h = Math.Max(h, CurrentHeat(dest.City) * 0.5);
            }
            if (!string.IsNullOrEmpty(dest.Dungeon))
            {
                h = Math.Max(h, CurrentHeat(dest.Dungeon));
            }

            return h >= HotHeat ? 0.25
                 : h >= WarmHeat ? 0.5
                 : 1.0;
        }

        [Usage("BotDanger")]
        [Description("Lists places with recent murder heat.")]
        private static void Status_OnCommand(CommandEventArgs e)
        {
            int shown = 0;
            foreach (var kv in _heat)
            {
                double h = CurrentHeat(kv.Key);
                if (h >= 0.5)
                {
                    e.Mobile.SendMessage($"  {kv.Key}: heat {h:0.0}");
                    shown++;
                }
            }
            e.Mobile.SendMessage(shown == 0
                ? "No dangerous places right now."
                : $"{shown} place(s) carry danger heat.");
        }
    }

    // ---------------------------------------------------------------------
    // BotPKWatch — civilians who SPOT a red raise the alarm.
    // ---------------------------------------------------------------------
    public static class BotPKWatch
    {
        public static bool Enabled = true;

        private const int SpotRange    = 12; // how close a red gets noticed
        private const int ScatterRange = 18; // travelers this close scatter

        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
        // One alarm per red per window — the same red isn't "spotted"
        // every fifteen seconds forever.
        private static readonly TimeSpan RedCooldown = TimeSpan.FromMinutes(6);

        private static Timer _timer;
        private static readonly Dictionary<Serial, DateTime> _alerted = new();

        public static void Configure()
        {
            _timer = Timer.DelayCall(TickInterval, TickInterval, OnTick);
        }

        private static void OnTick()
        {
            if (!Enabled)
            {
                return;
            }

            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot red || red.Deleted || !red.Alive ||
                    red.Behavior is not PKBehavior)
                {
                    continue;
                }
                if (_alerted.TryGetValue(red.Serial, out var until) && Core.Now < until)
                {
                    continue;
                }

                // A civilian in sight of the red?
                PlayerBot witness = null;
                foreach (var n in red.Map.GetMobilesInRange(red.Location, SpotRange))
                {
                    if (n is PlayerBot civ && civ != red && civ.Alive &&
                        civ.Behavior is not PKBehavior &&
                        civ.Behavior is not GhostBehavior &&
                        !civ.LoggingOut)
                    {
                        witness = civ;
                        break;
                    }
                }
                if (witness == null)
                {
                    continue;
                }

                if (_alerted.Count > 1000)
                {
                    _alerted.Clear();
                }
                _alerted[red.Serial] = Core.Now + RedCooldown;
                RaiseAlarm(witness, red);
            }
        }

        private static void RaiseAlarm(PlayerBot witness, PlayerBot red)
        {
            var place = BotEventJournal.PlaceName(witness.Location, witness.Map);

            var line = BotScene.Pick("red_alert", "{place}", place.ToUpperInvariant());
            if (!string.IsNullOrEmpty(line))
            {
                witness.Say(line);
            }

            // The sighting is news (gossip: "someone saw a red at X") and
            // the place heats up so destination rolls route around it.
            BotEventJournal.Record("red", witness.Name, red.Name,
                witness.Location, witness.Map);
            BotDangerMap.AddHeat(place, 2.0);

            Console.WriteLine($"[pk] {witness.Name} spotted red {red.Name} at {place}!");

            // Travelers in earshot scatter: fresh trips rolled under the
            // new danger weights naturally point somewhere else.
            foreach (var n in witness.Map.GetMobilesInRange(witness.Location, ScatterRange))
            {
                if (n is PlayerBot civ && civ != red && civ.Alive &&
                    civ.Behavior is TravelerBehavior &&
                    !BotPartyManager.IsInParty(civ) &&
                    Utility.RandomDouble() < 0.6)
                {
                    var flee = ChatLibrary.PickRandom("combat_flee");
                    if (!string.IsNullOrEmpty(flee) && Utility.RandomDouble() < 0.4)
                    {
                        civ.Say(flee);
                    }
                    civ.Behavior = new TravelerBehavior();
                }
            }
        }
    }
}
