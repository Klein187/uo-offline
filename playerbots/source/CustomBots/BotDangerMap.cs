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
// The red being watched for can be YOU. Once a player can earn murder
// counts off bots (BotMurderReport), a red player walking into Britain
// has to draw the same street reaction a red bot does, or going red
// means nothing but the guards.
//
//   [BotDanger — show current hot spots
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Collections;
using Server.Commands;
using Server.Mobiles;
using Server.Regions;

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
        // Yelling for the guards is a separate, much shorter beat. The
        // shout above is news that travels; this is a thing you do RIGHT
        // NOW because there is a murderer standing in the street, and if
        // he lives through the first call you do it again.
        private static readonly TimeSpan GuardCallCooldown = TimeSpan.FromSeconds(45);

        private static Timer _timer;
        private static readonly Dictionary<Serial, DateTime> _alerted = new();
        private static readonly Dictionary<Serial, DateTime> _guardsCalled = new();

        // Collected during the sweep, acted on after it closes. Calling the
        // guards SPAWNS a guard, and spawning a mobile inside a walk of
        // World.Mobiles throws "Collection was modified" and takes the
        // whole server down with it — the same trap RaiseAlarm's scatter
        // list documents a few lines below, which this walked straight
        // into. Reused rather than reallocated; the tick is every 15s.
        private static readonly List<(Mobile red, PlayerBot witness, bool alarm, bool guards)>
            _pending = new();

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

            foreach (var red in World.Mobiles.Values)
            {
                // Cheapest filter first: this used to test "is a PlayerBot",
                // and it still has to throw out every monster and vendor in
                // the world before doing any real work.
                if (!red.Player || red.Deleted || !red.Alive ||
                    red.Map == null || red.Map == Map.Internal ||
                    red.AccessLevel > AccessLevel.Player)
                {
                    continue;
                }

                // A red is a red whether or not it is running the PK brain.
                // This used to test PKBehavior alone, so a murderer walking
                // through on an ordinary Traveler trip drew no reaction at
                // all — which is exactly the case that put reds in the
                // middle of town with nobody saying a word.
                //
                // And a red is a red whether or not it is a bot. A real
                // player with five counts is the murderer this whole system
                // was written about; it just never looked at one. Monsters
                // are somebody else's problem — they have their own
                // notoriety and their own reasons to be feared.
                var isRed = red switch
                {
                    PlayerBot pb => pb.Behavior is PKBehavior || pb.Murderer,
                    _            => red.Murderer
                };

                if (!isRed)
                {
                    continue;
                }

                bool alarmDue =
                    !_alerted.TryGetValue(red.Serial, out var until) || Core.Now >= until;
                bool guardsDue = red.Murderer &&
                    (!_guardsCalled.TryGetValue(red.Serial, out var quiet) || Core.Now >= quiet);

                // A civilian in sight of the red? This used to be skipped
                // once both cooldowns were running, because all it fed was
                // the shout. It feeds BotGrayWatch now as well, and that has
                // to keep hearing about a red for as long as somebody is
                // standing there — so the look happens every sweep and the
                // cooldowns are applied further down, to the shouting alone.
                PlayerBot witness = null;
                foreach (var n in red.Map.GetMobilesInRange(red.Location, SpotRange))
                {
                    if (n is PlayerBot civ && civ != red && civ.Alive &&
                        !RedTerritory.IsRed(civ) &&
                        civ.Behavior is not GhostBehavior &&
                        !civ.LoggingOut &&
                        // Nobody yells about a murderer they cannot see. The
                        // reds who hide are the ones this matters for, bot
                        // and player alike.
                        civ.CanSee(red))
                    {
                        witness = civ;
                        break;
                    }
                }
                if (witness == null)
                {
                    continue;
                }

                // Somebody is here to see him. Shouting and scattering is
                // the right answer on a road and no answer at all in a
                // dungeon, where there is no watch to call and nowhere to
                // scatter to — so the witness also goes to BotGrayWatch,
                // which works out for itself whether this is a place where
                // somebody else was going to handle it, and hands the red to
                // the bots who would actually fight one where nobody was.
                BotGrayWatch.Note(red);

                if (!alarmDue && !guardsDue)
                {
                    continue;
                }

                _pending.Add((red, witness, alarmDue, guardsDue));
            }

            // Out of the enumeration — now it is safe to make things happen.
            for (var i = 0; i < _pending.Count; i++)
            {
                var (red, witness, alarmDue, guardsDue) = _pending[i];

                if (red.Deleted || !red.Alive || witness.Deleted || !witness.Alive)
                {
                    continue; // the world moved while we were deciding
                }

                // Guards first. Shouting the news is what you do after
                // you've done something about it.
                if (guardsDue && TryCallGuards(witness, red))
                {
                    if (_guardsCalled.Count > 1000)
                    {
                        _guardsCalled.Clear();
                    }
                    _guardsCalled[red.Serial] = Core.Now + GuardCallCooldown;
                }

                if (alarmDue)
                {
                    if (_alerted.Count > 1000)
                    {
                        _alerted.Clear();
                    }
                    _alerted[red.Serial] = Core.Now + RedCooldown;
                    RaiseAlarm(witness, red);
                }
            }

            _pending.Clear();
        }

        // The oldest reflex in the game: a murderer walks into town and
        // somebody yells for the guards.
        //
        // The shout has to be backed by a direct CallGuards. The vanilla
        // "guards" keyword lives in GuardedRegion.OnSpeech, and keywords
        // are parsed out of the CLIENT's speech packet — a Mobile.Say from
        // server code carries none, so a bot can holler the word all day
        // and nothing happens. Hence: say the line for the people watching,
        // then call the region directly for the effect.
        private static bool TryCallGuards(PlayerBot witness, Mobile red)
        {
            var region = red.Region.GetRegion<GuardedRegion>();
            if (region == null || region.IsDisabled() || !region.IsGuardCandidate(red))
            {
                return false;
            }

            // The witness has to be under the same protection it is
            // invoking. Somebody safely outside the town line watching a
            // red stand inside it doesn't get to call the watch.
            if (witness.Region.GetRegion<GuardedRegion>() != region)
            {
                return false;
            }

            var line = ChatLibrary.PickRandom("guards_call");
            witness.Say(string.IsNullOrEmpty(line) ? "GUARDS!!" : line);
            region.CallGuards(red.Location);

            Console.WriteLine(
                $"[pk] {witness.Name} called the guards on {red.Name} in {region.Name}!");
            return true;
        }

        private static void RaiseAlarm(PlayerBot witness, Mobile red)
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
            //
            // Collect first, act after. Assigning Behavior runs OnAttached
            // immediately, and a fresh Traveler can move the bot out of the
            // sector we are still enumerating -- which threw
            // "Collection was modified after the enumerator was instantiated"
            // and took the whole server down. It only became a reliable crash
            // once the population doubled, but the bug was always there.
            using var scatter = PooledRefList<PlayerBot>.Create();
            foreach (var n in witness.Map.GetMobilesInRange(witness.Location, ScatterRange))
            {
                if (n is PlayerBot civ && civ != red && civ.Alive &&
                    civ.Behavior is TravelerBehavior &&
                    !BotPartyManager.IsInParty(civ) &&
                    Utility.RandomDouble() < 0.6)
                {
                    scatter.Add(civ);
                }
            }

            for (var i = 0; i < scatter.Count; i++)
            {
                var civ = scatter[i];
                if (civ.Deleted || !civ.Alive)
                {
                    continue;
                }

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
