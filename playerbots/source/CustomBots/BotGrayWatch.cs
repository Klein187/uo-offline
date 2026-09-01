// =========================================================================
// BotGrayWatch.cs — somebody is fair game, somebody goes after them.
//
// A criminal is fair game and always was. Killing one is not a crime, it
// costs no karma and it earns no murder count, which is exactly why the
// bank steps used to empty every time a thief got caught. The bots knew
// none of that: every enemy scan in the shard begins "is this a
// BaseCreature with negative karma", so a gray could stand in the middle
// of the crowd and nobody so much as turned around.
//
// This watches for the gray flag on anything player-shaped — a bot or you
// — and hands the nearest few bots a reason to draw.
//
// REDS use the same machinery, for the same reason: killing a murderer is
// no crime either. It used to hand them straight to BotPKWatch, which does
// not fight — it yells for the guards. Where the guards turn out that is
// the whole of the answer and always was. Where they do not, it was the
// whole of the answer to nothing: a red could stand in a dungeon among a
// dozen blues and not one of them so much as faced him, because the only
// system that draws on a player-shaped target refused reds outright and
// the only system that handles reds was calling a watch that wasn't there.
// So a red counts as fair game wherever no guard will come, which is the
// same rule the players used — you jumped reds underground, you left them
// to the watch in town, and you left them alone in Buccaneer's Den.
//
// Nothing here sweeps the world looking for anybody. The gray flag
// announces itself three ways, all of them cheap: the engine's
// AggressiveAction event fires on every harmful act and says whether it
// was criminal; PlayerBot.CriminalAction reports a bot that flagged some
// other way; and the handful of actually-connected clients are checked
// outright, which catches you picking a pocket rather than a fight. The
// reds come in from the one sweep that already existed, BotPKWatch's,
// which walks the world every fifteen seconds regardless and now says so
// when it finds one with somebody standing next to it. Everything after
// that is a spatial query around a known target.
//
//   WHO      Fighting classes only. A smith at his forge, a merchant, a
//            tailor: not their business. Willingness is rolled off the
//            bot's serial so the same bot is the same kind of person
//            every time, nudged by Brave and Cautious.
//   HOW MANY Three at once. A gray gets jumped, not mobbed by a town.
//   WHERE    Bots whose behavior already knows how to fight get handed
//            the target and chase it down — their own tick swaps them to
//            a defender, the way being attacked mid-trip does. The
//            standing-around roles (bank crowd, shopkeepers) only take a
//            swing at arm's length, because a smith who abandons his
//            forge to chase somebody across Britain never comes back.
//   UNTIL    The flag lapses. Two minutes after the crime the target is
//            blue again, and a bot still swinging then becomes the
//            criminal — and would be jumped by this very system in turn.
//            So it is guarded from both ends: nobody starts on a flag
//            that is already running out, and every fight is called off
//            within a second of the flag dropping. Which is also what the
//            players who knew what they were doing did.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Mobiles;
using Server.Network;
using Server.Regions;

namespace Server.CustomBots
{
    public static class BotGrayWatch
    {
        public static bool Enabled = true;

        // ---- Knobs ----

        // How far off a bot notices the flag.
        private const int SpotRange = 10;

        // A standing role only engages what is already on top of it.
        private const int SwingRange = 2;

        // How many bots pile onto one gray.
        private const int MaxAttackers = 3;

        // Base willingness, before personality.
        private const int WillingPercent  = 35;
        private const int BraveWilling    = 60;
        private const int CautiousWilling = 15;

        // Odds the first bot in says something.
        private const double ShoutChance = 0.45;

        // The engine clears the flag two minutes after the crime
        // (Mobile.ExpireCriminalDelay). Nobody starts a fight it can't
        // finish inside that, so a fight can't outlive its reason.
        private static readonly TimeSpan StaleFlag = TimeSpan.FromSeconds(90);

        // Nothing about a red expires the way a gray's flag does — the
        // counts stand for days. What this measures is how long it has been
        // since anybody was there to see him, and it exists to keep the cost
        // down: there are getting on for eighty reds alive at once, and
        // scanning around every one of them every second forever, most of
        // them alone in a corridor with nobody to draw on them, is a lot of
        // work to do for nothing. BotPKWatch re-notes a red whenever a
        // civilian is in sight of him, so the ones with company stay in and
        // the rest fall out three sweeps after the hall empties.
        private static readonly TimeSpan RedStale = TimeSpan.FromSeconds(45);

        // How long a bot is left alone about a target it broke off from.
        //
        // Without this the sweep simply handed the fight back. The bot fled
        // at low HP, StartFlee cleared its Combatant, and a second later the
        // sweep found a free bot standing next to a red and drew it again.
        // It could never actually get away. Observed: Marwina drew on
        // Farara in Destard, fled at 28hp, was re-drawn, fled at 35, 35, 35,
        // 12, twelve times in one fight, and died to it. Nothing hid this
        // for grays only because a gray's flag lapses in 90 seconds; a red's
        // reason to be hunted never lapses at all.
        private static readonly TimeSpan ReengageDelay = TimeSpan.FromSeconds(90);

        // (bot, target) -> when that bot may be drawn on that target again.
        // Per PAIR, not per bot: one that ran from a mage it could not beat
        // should still defend itself elsewhere, and three bots should still
        // be able to gang the next red.
        private static readonly Dictionary<(Mobile, Mobile), DateTime> _brokeOff = new();

        // Short: the flag can drop at any moment, and the tick is cheap
        // because it only ever walks the criminals it was told about.
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

        private static Timer _timer;

        // Who is wearing the flag, and since when — so a fight is never
        // started on one that is about to go blue.
        private static readonly Dictionary<Mobile, DateTime> _flaggedSince = new();
        private static readonly List<Mobile> _lapsed = new();

        // Only the fights this system started. Anything else — a duel, a
        // faction war, a monster — is somebody else's to end.
        private static readonly Dictionary<PlayerBot, Mobile> _engaged = new();

        // Collected during the sweep, acted on after it closes: assigning
        // Behavior runs OnAttached, which can move a bot out of the sector
        // being enumerated. BotPKWatch documents the crash this avoids.
        private static readonly List<(PlayerBot bot, Mobile gray, bool first)> _pending = new();
        private static readonly List<PlayerBot> _released = new();

        public static void Configure()
        {
            EventSink.AggressiveAction += OnAggressiveAction;
            _timer = Timer.DelayCall(TickInterval, TickInterval, OnTick);
        }

        // Every harmful act in the game passes through here, and the engine
        // has already worked out whether it was a crime.
        private static void OnAggressiveAction(AggressiveActionEventArgs e)
        {
            if (e.Criminal)
            {
                Note(e.Aggressor);
            }
        }

        // Somebody just flagged. Called by the event above, by
        // PlayerBot.CriminalAction, and by the connected-client check.
        public static void Note(Mobile m)
        {
            if (!Enabled || m == null || !IsFairGame(m))
            {
                return;
            }

            // A gray's timestamp is the moment of the crime and must not
            // move: it is what stops a bot starting a fight it cannot
            // finish before the flag lapses. A red's is the last time he was
            // seen, so it is refreshed every sweep he is seen on.
            if (m.Murderer)
            {
                _flaggedSince[m] = Core.Now;
                return;
            }

            _flaggedSince.TryAdd(m, Core.Now);
        }

        private static void OnTick()
        {
            if (!Enabled)
            {
                return;
            }

            // A real player can flag without ever swinging — picking a
            // pocket does it. There is a handful of clients at most, so
            // just look.
            foreach (var ns in NetState.Instances)
            {
                if (ns.Mobile is PlayerMobile pm && pm.Criminal)
                {
                    Note(pm);
                }
            }

            ReleaseFinished();
            LookForGrays();
        }

        // -------------------------------------------------------------------
        // Call off every fight whose reason has gone away.
        // -------------------------------------------------------------------
        private static void ReleaseFinished()
        {
            foreach (var (bot, gray) in _engaged)
            {
                // The bot is no longer on the target this system gave it.
                // It fled, or its own behaviour dropped or swapped the
                // fight. Either way it let go, and letting go has to END the
                // engagement — this was the whole bug. A fleeing bot keeps
                // running but stays well inside the range test below, so it
                // never counted as released, and it never counted as busy
                // either because StartFlee nulls Combatant. The sweep found
                // a free bot standing next to a red every single second and
                // handed the same fight back until it died.
                var letGo = !bot.Deleted && bot.Alive && bot.Combatant != gray;

                if (letGo || bot.Deleted || !bot.Alive || !IsFairGame(gray) ||
                    gray.Map != bot.Map ||
                    !bot.InRange(gray.Location, SpotRange + 8))
                {
                    _released.Add(bot);
                }
            }

            for (var i = 0; i < _released.Count; i++)
            {
                var bot = _released[i];
                _engaged.Remove(bot, out var gray);

                // The fight ended with the target still standing and still
                // worth hitting, so it ended because the BOT stopped: it
                // fled, it could not reach, it died. That is its decision
                // and it stands for a while. If the target is dead or blue
                // or gone there is nothing to stand off from.
                if (gray?.Deleted == false && gray.Alive && IsFairGame(gray))
                {
                    if (_brokeOff.Count > 1000)
                    {
                        _brokeOff.Clear();
                    }
                    _brokeOff[(bot, gray)] = Core.Now + ReengageDelay;
                }

                // Only drop the target if it is still the one we set — the
                // bot may have picked a fight of its own since.
                if (!bot.Deleted && bot.Combatant == gray)
                {
                    bot.Combatant = null;
                }
            }

            _released.Clear();
        }

        // -------------------------------------------------------------------
        // Find the flagged, then find who cares.
        // -------------------------------------------------------------------
        private static void LookForGrays()
        {
            foreach (var (gray, since) in _flaggedSince)
            {
                // Blue again, dead, or gone — and going stale counts as
                // gone. For a gray that is the flag running out: whoever
                // wanted this one has had their chance, and starting now
                // only ends with the attacker gray instead. For a red it is
                // nobody having laid eyes on him in a while, which is not a
                // reason to spare him, only a reason to stop looking.
                if (!IsFairGame(gray) ||
                    Core.Now - since >= (gray.Murderer ? RedStale : StaleFlag))
                {
                    _lapsed.Add(gray);
                    continue;
                }

                var attackers = CountAttackers(gray);

                foreach (var m in gray.Map.GetMobilesInRange(gray.Location, SpotRange))
                {
                    if (attackers >= MaxAttackers)
                    {
                        break;
                    }

                    if (m is not PlayerBot bot || !WouldDraw(bot, gray))
                    {
                        continue;
                    }

                    _pending.Add((bot, gray, attackers == 0));
                    attackers++;
                }
            }

            for (var i = 0; i < _pending.Count; i++)
            {
                var (bot, gray, first) = _pending[i];

                if (bot.Deleted || !bot.Alive || !IsFairGame(gray) ||
                    bot.Combatant != null)
                {
                    continue; // the world moved while we were deciding
                }

                // Setting the combatant IS the engagement. A behavior that
                // fights reads it on its next tick and swaps itself to a
                // defender; one that doesn't just swings at what is already
                // in front of it.
                bot.Combatant = gray;
                _engaged[bot] = gray;

                if (first)
                {
                    var line = ChatLibrary.PickRandom(
                        gray.Murderer ? "red_call" : "gray_call");
                    if (!string.IsNullOrEmpty(line) && Utility.RandomDouble() < ShoutChance)
                    {
                        bot.Say(line);
                    }
                }

                Console.WriteLine(
                    $"[{(gray.Murderer ? "red" : "gray")}] {bot.Name} drew on " +
                    $"{gray.Name} at " +
                    $"{BotEventJournal.PlaceName(bot.Location, bot.Map)}");
            }

            _pending.Clear();

            for (var i = 0; i < _lapsed.Count; i++)
            {
                _flaggedSince.Remove(_lapsed[i]);
            }

            _lapsed.Clear();
        }

        // -------------------------------------------------------------------
        // Is this a criminal worth drawing on?
        // -------------------------------------------------------------------
        private static bool IsFairGame(Mobile m)
        {
            if (m.Deleted || !m.Alive || !m.Player ||
                m.AccessLevel > AccessLevel.Player || m.Blessed)
            {
                return false;
            }

            if (m.Map == null || m.Map == Map.Internal)
            {
                return false;
            }

            // A murderer is fair game to anybody, but only where nobody else
            // is going to deal with him. In a guarded town the watch does it
            // and a brawl in the street only gets in their way; in
            // Buccaneer's Den nothing does it, because the place is theirs
            // and a blue who starts something there gets what he asked for.
            if (m.Murderer)
            {
                return !RedTerritory.Contains(m.Location) && !GuardsWillCome(m);
            }

            return m.Criminal;
        }

        // Would the watch turn out for this one where he is standing? Asked
        // the way BotPKWatch asks it, and for the same reason — a disabled
        // guard region (Buccaneer's Den) is not a guarded place, and
        // TownRegion derives from GuardedRegion, so the type alone lies.
        private static bool GuardsWillCome(Mobile m)
        {
            var region = m.Region?.GetRegion<GuardedRegion>();
            return region != null && !region.IsDisabled() &&
                   region.IsGuardCandidate(m);
        }

        // -------------------------------------------------------------------
        // Would THIS bot draw on THIS gray?
        // -------------------------------------------------------------------
        private static bool WouldDraw(PlayerBot bot, Mobile gray)
        {
            if (bot == gray || bot.Deleted || !bot.Alive || bot.LoggingOut ||
                bot.Combatant != null || bot.Map != gray.Map)
            {
                return false;
            }

            // A ghost has no quarrels, and a red has its own reasons.
            if (bot.Behavior is GhostBehavior or PKBehavior || bot.Murderer)
            {
                return false;
            }

            if (!IsFightingClass(bot.Class) || !bot.CanSee(gray))
            {
                return false;
            }

            // Already tried this one and broke off. Leave it be.
            if (_brokeOff.TryGetValue((bot, gray), out var until) && Core.Now < until)
            {
                return false;
            }

            // Running for its life. A fleeing bot has no Combatant, so the
            // check above cannot see it is busy — and handing it a fight
            // while it runs is how it dies instead of getting away.
            if (bot.Behavior is AdventurerBehavior { IsFleeing: true })
            {
                return false;
            }

            // A gray is a free kill anyone will take. A murderer is a real
            // fight, and the bots who take real fights on purpose are the
            // adventurers and the dungeon crews — excluding one already
            // walking out with its supplies gone, which is not looking for
            // trouble and should not be handed any. Everyone else goes on
            // doing what they already do about reds, which is leave: that is
            // BotPKWatch's scatter, and it was never the broken part.
            if (gray.Murderer &&
                (bot.Behavior is not AdventurerBehavior adv || !adv.LooksForTrouble))
            {
                return false;
            }

            // Never your own side. A guildmate or a party member who flags
            // gray is somebody you cover for, not somebody you kill.
            if (IsOwnSide(bot, gray))
            {
                return false;
            }

            // A standing role stays standing unless the gray is close enough
            // to hit from where it is.
            if (!ChasesOnItsOwn(bot.Behavior) && !bot.InRange(gray.Location, SwingRange))
            {
                return false;
            }

            return IsWilling(bot);
        }

        // Behaviors that pick their target up off Combatant and go after it.
        // AdventurerBehavior covers the dungeon crawler and both party
        // behaviors; Traveler, Visitor and Gatherer each swap themselves to
        // a defender when they find a combatant waiting.
        private static bool ChasesOnItsOwn(PlayerBotBehavior b) =>
            b is AdventurerBehavior or TravelerBehavior or VisitorBehavior or GathererBehavior;

        // Tradespeople don't draw. The gatherers do — the axe is a real
        // weapon and they already fight what finds them in the woods.
        private static bool IsFightingClass(BotClass cls) =>
            !BotClassHelper.IsArtisan(cls) &&
            cls is not BotClass.Crafter and not BotClass.Merchant;

        private static bool IsOwnSide(PlayerBot bot, Mobile gray)
        {
            if (gray is PlayerBot other)
            {
                if (bot.BotGuildIndex >= 0 && bot.BotGuildIndex == other.BotGuildIndex)
                {
                    return true;
                }

                var party = BotPartyManager.PartyOf(bot);
                if (party != null && party == BotPartyManager.PartyOf(other))
                {
                    return true;
                }
            }

            var botParty = Engines.PartySystem.Party.Get(bot);
            return botParty != null && botParty == Engines.PartySystem.Party.Get(gray);
        }

        // Stable per bot: the same bot is the same kind of person every
        // time, rather than rerolling its nerve every three seconds.
        private static bool IsWilling(PlayerBot bot)
        {
            var threshold = WillingPercent;

            if (bot.Personality.HasTrait(PersonalityTrait.Brave))
            {
                threshold = BraveWilling;
            }
            else if (bot.Personality.HasTrait(PersonalityTrait.Cautious))
            {
                threshold = CautiousWilling;
            }

            return bot.Serial.Value % 100 < (uint)threshold;
        }

        private static int CountAttackers(Mobile gray)
        {
            var n = 0;

            foreach (var (bot, target) in _engaged)
            {
                if (target == gray && !bot.Deleted && bot.Alive)
                {
                    n++;
                }
            }

            return n;
        }
    }
}
