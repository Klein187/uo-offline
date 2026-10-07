// =========================================================================
// GhostBehavior.cs — a freshly dead bot haunting its corpse (IDEAS 3.1).
//
// The first act of the death story: the ghost drifts around the death
// spot for a minute or two, moaning OoOoOo at anyone nearby (the client
// garbles ghost speech for the living — exactly right). Then it goes
// looking for a way back, and every way back is now a real thing standing
// in the world:
//
//   - Someone living with the skill — a mage with Resurrection, a healer
//     or a grandmaster dexxer with bandages. The ghost floats over and
//     asks (BotResurrectAid).
//   - A real ANKH, or a real HEALER NPC. Town healers, the shrine ankhs,
//     and the wandering healers that roam Felucca all count. The ghost
//     walks the last tiles to one and stands up there.
//   - Failing both, it walks — really walks — to the nearest healer or
//     shrine as a Traveler, and does the above on arrival.
//   - Died underground? It climbs out first (GhostExitBehavior). Dungeons
//     have no healers and, Destard aside, no ankhs; the way back to life
//     is the way out.
//
// What is GONE is the old ending: "found by a wandering healer" fired on
// a timer with nothing anywhere near the bot. Seventeen of nineteen
// resurrections in one soak came out of that branch, which is why deaths
// looked like bots randomly standing back up. The only survivor of it is
// BotDeathManager's twenty-minute stranding net, and that one carries the
// ghost to a res point before raising it.
//
// Ghosts don't fight, don't mount, don't shop. They drift, and they ask.
// =========================================================================

using System;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public class GhostBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Ghost";

        public override string GetStatusLine(PlayerBot bot) =>
            SeekSite.HasValue
                ? "dead — drifting the last few tiles to an ankh"
                : _aider != null
                    ? $"dead — begging {_aider.Name} for a res"
                    : "dead — a ghost seeking resurrection";

        // Set by BotDeathManager.OnTravelerArrival: the ghost finished a
        // res walk and the actual ankh/healer is a few tiles further on.
        // With this set the behavior does nothing but close that gap.
        public Point3D? SeekSite { get; set; }

        // A ghost handed back from the dungeon climb-out has already done
        // its haunting five floors down. It gets on with finding an ankh.
        public bool SkipHaunt { get; set; }

        private const int HauntMinSeconds = 45;
        private const int HauntMaxSeconds = 120;

        // How far the ghost drifts from the corpse while haunting.
        private const int DriftRadius = 6;

        // Res-in-place waits for hostiles to clear the area, up to this
        // much extra haunting past the normal window.
        private static readonly TimeSpan HauntHostileGrace = TimeSpan.FromMinutes(3);

        private const int HostileCheckRange = 8;

        // Nothing to walk to right now (no route, nobody around). Drift a
        // while longer and ask again rather than giving up on the world.
        private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

        private DateTime _hauntUntil = DateTime.MinValue;
        private Point3D _anchor;

        // A site we walked all the way to and were NOT raised at (the ankh
        // was deleted, the tile won't hold a body). Without this the ghost
        // would pick the same one again next tick, forever.
        private DateTime _skipSiteUntil = DateTime.MinValue;
        private bool _warnedNowhereToGo;
        private static readonly TimeSpan SiteGiveUpFor = TimeSpan.FromMinutes(5);

        // ---- Begging a living bot for a res ----
        // The ghost does the walking. Nothing here reaches into the other
        // bot's behavior to march it over — the aider just happens to be
        // standing there when a ghost turns up at its elbow.
        private PlayerBot _aider;
        private DateTime _aidAttemptedAt = DateTime.MinValue;
        private DateTime _nextAidSearch = DateTime.MinValue;
        private int _aidAttempts;

        // A cast can fizzle and a bandage can fail; give the pair a few
        // goes before the ghost writes them off and walks.
        private const int MaxAidAttempts = 3;
        private static readonly TimeSpan AidRetryDelay   = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan AidSearchPeriod = TimeSpan.FromSeconds(8);
        // After someone has failed three times, stop hovering at their
        // elbow and get on with the walk.
        private static readonly TimeSpan AidGiveUpFor    = TimeSpan.FromMinutes(2);
        // Long enough to close 20 tiles at a drift; past it the aider has
        // clearly wandered off.
        private static readonly TimeSpan AidApproachLimit = TimeSpan.FromSeconds(45);
        private DateTime _aidStartedAt = DateTime.MinValue;

        // ---- Ghost walking ----
        // A drifting step timer, shared with GhostExitBehavior. The slow
        // two-second behavior tick is far too coarse to walk a ghost
        // anywhere; this is the same trick the crawler's pad walk uses.
        private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(400);

        private PathFollower _follower;
        private Timer _stepTimer;
        private Point3D _walkGoal;
        private int _walkRange;
        private bool _walkArrived;

        // Walk-stall watchdog. Plans lie, distance doesn't: a ghost that
        // hasn't gotten closer to its goal in this long isn't going to.
        // Found on the first live run — a ghost stood six tiles from the
        // Chaos Shrine ankh for twenty minutes because the last steps go
        // up onto a platform it couldn't path onto.
        private static readonly TimeSpan WalkStallLimit = TimeSpan.FromSeconds(45);
        private int _walkBestDist = int.MaxValue;
        private DateTime _walkBestAt = DateTime.MinValue;
        private bool _walkStalled;

        // Standing this close to a real ankh with the last steps blocked
        // counts as touching it. The bot IS at the shrine; the difference
        // is a step in the masonry, not a story.
        // Ten, not six: shrine ankhs stand on platforms, and the
        // Spirituality shrine's walk-up spot is eight tiles out; at six every
        // ghost there gave up, picked the same shrine again and waited out
        // the twenty-minute net.
        private const int StalledReachSlack = 10;

        // A healer or an ankh this far off is still worth walking to when no
        // healer or shrine destination can be reached: the islands with a
        // dungeon mouth (Fire Isle, the Deceit isle) have a healer standing
        // half a screen away that is nobody's destination.
        private const int FarResSiteRange = 150;

        // Truly nowhere to go: carried to a res point after this, not after
        // the full twenty minutes.
        private static readonly TimeSpan NowhereRescueAfter = TimeSpan.FromMinutes(5);

        public GhostBehavior()
        {
            ChatCategories  = new[] { "ghost" };
            ChatChance      = 0.30;
            MinChatCooldown = TimeSpan.FromSeconds(15);
            MaxChatCooldown = TimeSpan.FromSeconds(45);
        }

        // For the fleet-wide nav watchdog. A ghost only counts as going
        // somewhere while its walk timer is running — drifting around the
        // corpse during the haunt is standing still on purpose.
        public override Point3D? NavGoal(PlayerBot bot) =>
            _stepTimer != null ? _walkGoal : null;

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            _anchor = bot.Location;
            _hauntUntil = SkipHaunt
                ? Core.Now
                : Core.Now + TimeSpan.FromSeconds(
                    Utility.RandomMinMax(HauntMinSeconds, HauntMaxSeconds));
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopWalk();
            base.OnDetached(bot);
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot == null || bot.Deleted ||
                bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            if (CheckRevived(bot)) return;
            if (CheckResSite(bot)) return;
            if (CheckStranded(bot)) return;

            // Walking the last tiles to an ankh or a healer we already
            // picked out. CheckResSite above is what ends this — the walk
            // itself has no arrival ceremony.
            if (SeekSite.HasValue)
            {
                TrySpeak(bot);
                RunSeekWalk(bot);
                return;
            }

            if (CheckAid(bot)) return;

            TrySpeak(bot);

            if (Core.Now >= _hauntUntil)
            {
                BeginResJourney(bot);
                return;
            }

            Drift(bot);
        }

        // -------------------------------------------------------------------
        // Shared ghost checks. GhostExitBehavior runs the same three at the
        // top of its own tick, so a ghost climbing out of a dungeon is just
        // as open to a passing mage or a Destard ankh as one on the surface.
        // Each returns true when it handled the bot (behavior swapped or
        // the tick is spoken for).
        // -------------------------------------------------------------------

        // Somehow alive again (a player's res spell, an admin, another bot
        // that beat us to it) — skip straight to the corpse run.
        protected bool CheckRevived(PlayerBot bot)
        {
            if (!bot.Alive)
            {
                return false;
            }
            StopWalk();
            bot.CorpseRunPending = true;
            bot.Behavior = new CorpseReclaimBehavior();
            return true;
        }

        // Standing at an ankh, or beside a healer who is willing. This is
        // the ONLY ordinary way a ghost comes back now.
        protected bool CheckResSite(PlayerBot bot)
        {
            var how = BotDeathManager.ResurrectorInReach(bot);
            if (how == null)
            {
                return false;
            }
            StopWalk();
            BotDeathManager.ResurrectBot(bot, how);
            return true;
        }

        // The twenty-minute net. Never reached by a ghost whose journey
        // works; when it fires, something is wedged and worth reading in
        // the log.
        protected bool CheckStranded(PlayerBot bot)
        {
            if (!BotDeathManager.CheckGhostRescue(bot))
            {
                return false;
            }
            StopWalk();
            return true;
        }

        // -------------------------------------------------------------------
        // Ask the living. Finds the nearest bot that could raise us, floats
        // to it, and asks. Returns true when the tick was spent on this.
        // -------------------------------------------------------------------
        protected bool CheckAid(PlayerBot bot)
        {
            // Still waiting on a cast or a bandage that's already underway.
            if (_aidAttemptedAt != DateTime.MinValue &&
                Core.Now - _aidAttemptedAt < AidRetryDelay)
            {
                return true;
            }

            // Three fizzles is enough — this one can't do it. Let go of
            // them and get walking; somebody else may turn up later.
            if (_aidAttempts >= MaxAidAttempts)
            {
                if (_aider != null)
                {
                    Console.WriteLine(
                        $"[death] {_aider.Name} could not raise {bot.Name} — the " +
                        $"ghost gives up on them");
                    _aider = null;
                    StopWalk();
                }
                _aidAttempts = 0;
                _nextAidSearch = Core.Now + AidGiveUpFor;
                return false;
            }

            // Drop an aider that died, wandered off, ran out of what it
            // needed, or that the ghost simply can't get to.
            if (_aider != null &&
                (!BotResurrectAid.Willing(_aider, bot) ||
                 !BotResurrectAid.CanAid(_aider) ||
                 WalkStalled ||
                 Core.Now - _aidStartedAt > AidApproachLimit))
            {
                _aider = null;
                StopWalk();
            }

            if (_aider == null)
            {
                if (Core.Now < _nextAidSearch)
                {
                    return false;
                }
                _nextAidSearch = Core.Now + AidSearchPeriod;

                _aider = BotResurrectAid.FindAider(bot);
                if (_aider == null)
                {
                    return false;
                }

                _aidStartedAt = Core.Now;
                _aidAttempts = 0;

                var plea = ChatLibrary.PickRandom("ghost_plea");
                if (!string.IsNullOrEmpty(plea))
                {
                    bot.Say(plea);
                }
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost drifts to {_aider.Name} for a res");
            }

            // In reach — ask.
            if (BotResurrectAid.TryAid(_aider, bot))
            {
                StopWalk();
                _aidAttempts++;
                _aidAttemptedAt = Core.Now;
                return true;
            }

            // Not in reach yet: float over. Range 1 so the Resurrection
            // cast (TargetRange 1) can land.
            BeginWalk(bot, _aider.Location, BotResurrectAid.CastRange);
            return true;
        }

        // -------------------------------------------------------------------
        // The haunt is over. Decide how this ghost gets back to the living.
        // -------------------------------------------------------------------
        protected void BeginResJourney(PlayerBot bot)
        {
            // Underground: the way back to life is the way OUT.
            if (DungeonRegistry.IsInDungeon(bot))
            {
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost starts climbing out of the dungeon");
                bot.Behavior = new GhostExitBehavior();
                return;
            }

            // An ankh or a healer already within sight — walk to it rather
            // than crossing the map to a destination that has one.
            var site = Core.Now >= _skipSiteUntil
                ? BotDeathManager.FindResSite(bot)
                : null;
            if (site.HasValue)
            {
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost makes for the ankh/healer at " +
                    $"{site.Value}");
                SeekSite = site;
                return;
            }

            // The long walk. TravelerBehavior carries a dead bot now; on
            // arrival BotDeathManager hands it back here in SeekSite mode
            // to close the last few tiles to the actual ankh.
            var destName = BotDeathManager.PickResDestination(bot);
            if (destName != null)
            {
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost sets off for '{destName}'");
                bot.Behavior = new TravelerBehavior { DestinationName = destName };
                return;
            }

            // No destination to walk to. A real healer or ankh further off
            // on this same island still counts (the walk fails if it is
            // across water, and the ghost ends up back here).
            var far = Core.Now >= _skipSiteUntil
                ? BotDeathManager.FindResSite(bot, FarResSiteRange)
                : null;
            if (far.HasValue)
            {
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost has no road to a healer, but sees one " +
                    $"at {far.Value} — walking over");
                SeekSite = far;
                return;
            }

            // Truly nowhere: an island with no healer and no ankh. A player
            // would wait for a passing res; a bot waits a few minutes for
            // one, then the stranding net carries it.
            if (bot.LastDeathAt != DateTime.MinValue && Core.Now - bot.LastDeathAt >= NowhereRescueAfter)
            {
                StopWalk();
                BotDeathManager.CarryToResPoint(bot, "had nowhere to go");
                return;
            }

            // Keep haunting and ask again in a minute; a wandering healer
            // may walk past. Said once per ghost, because a silent version
            // of this is how you end up with bots standing in a field for
            // twenty minutes and no idea why.
            if (!_warnedNowhereToGo)
            {
                _warnedNowhereToGo = true;
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost has no reachable healer or shrine " +
                    $"from ({bot.X},{bot.Y}) — waiting for someone to pass");
            }

            if (HostileNearby(bot) && Core.Now < _hauntUntil + HauntHostileGrace)
            {
                return;
            }
            _hauntUntil = Core.Now + RetryAfter;
        }

        // -------------------------------------------------------------------
        // SeekSite: close the gap to a specific ankh or healer. CheckResSite
        // ends it the moment we're in range, so this only has to keep the
        // ghost moving and give up if the site turns out unreachable.
        // -------------------------------------------------------------------
        private void RunSeekWalk(PlayerBot bot)
        {
            var goal = SeekSite.Value;

            // Can't get the last few steps in. Shrines sit on platforms and
            // healers stand inside shops; the ghost is AT the place even
            // when the pathfinder can't put it on the exact tile. Close
            // enough to a real ankh counts as touching it.
            if (WalkStalled)
            {
                int gap = Dist(bot.Location, goal);
                if (gap <= StalledReachSlack &&
                    BotDeathManager.FindAnkh(bot, StalledReachSlack) != null)
                {
                    StopWalk();
                    SeekSite = null;
                    BotDeathManager.ResurrectBot(bot, "reached the ankh");
                    return;
                }

                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost cannot reach {goal} ({gap} tiles " +
                    $"out) — looking elsewhere");
                SeekSite = null;
                StopWalk();
                _skipSiteUntil = Core.Now + SiteGiveUpFor;
                _hauntUntil = Core.Now; // re-decide next tick
                return;
            }

            if (_walkArrived)
            {
                // Standing on it and CheckResSite still said no — the ankh
                // is gone, or the healer refused us (a murderer). Fall
                // back to the long walk.
                Console.WriteLine(
                    $"[death] {bot.Name}'s ghost reached {goal} and was not raised " +
                    $"— looking elsewhere");
                SeekSite = null;
                StopWalk();
                _skipSiteUntil = Core.Now + SiteGiveUpFor;
                _hauntUntil = Core.Now; // re-decide next tick
                return;
            }

            // A healer NPC wanders; re-aim at where it is now.
            var fresh = BotDeathManager.FindResSite(bot);
            if (fresh.HasValue && fresh.Value != goal)
            {
                SeekSite = fresh;
                goal = fresh.Value;
            }

            BeginWalk(bot, goal, BotDeathManager.AnkhResRange);
        }

        // Same "actual monster" filter the combat code uses: deeply
        // negative karma + a fight mode. Wildlife and townsfolk don't
        // delay a res.
        protected static bool HostileNearby(PlayerBot bot)
        {
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, HostileCheckRange))
            {
                if (m is Server.Mobiles.BaseCreature bc &&
                    bc.Alive && !bc.Deleted &&
                    bc.Karma < 0 &&
                    bc.FightMode != Server.Mobiles.FightMode.None &&
                    !bc.Controlled && !bc.Summoned)
                {
                    return true;
                }
            }
            return false;
        }

        private void Drift(PlayerBot bot)
        {
            // Drift: aimless one-tile floats around the corpse.
            if (Utility.RandomDouble() < 0.5)
            {
                Direction dir;
                if (Math.Max(Math.Abs(bot.X - _anchor.X), Math.Abs(bot.Y - _anchor.Y))
                    > DriftRadius)
                {
                    dir = bot.GetDirectionTo(_anchor);
                }
                else
                {
                    dir = (Direction)Utility.Random(8);
                }
                bot.Direction = dir;
                bot.Move(dir);
            }
        }

        // -------------------------------------------------------------------
        // Ghost walking. A short-range drift toward one tile, driven by its
        // own fast timer because the behavior tick is two seconds wide.
        // Re-targeting to the same goal is a no-op, so callers can just say
        // "keep going there" every tick.
        // -------------------------------------------------------------------
        protected void BeginWalk(PlayerBot bot, Point3D goal, int range)
        {
            if (_stepTimer != null && _walkGoal == goal && _walkRange == range)
            {
                return; // already on it
            }

            StopWalk();

            _walkGoal     = goal;
            _walkRange    = range;
            _walkArrived  = false;
            _walkStalled  = false;
            _walkBestDist = Dist(bot.Location, goal);
            _walkBestAt   = Core.Now;
            _follower     = new PathFollower(bot, goal);
            _stepTimer    = Timer.DelayCall(StepInterval, StepInterval, () => WalkStep(bot));
        }

        protected void StopWalk()
        {
            if (_stepTimer != null)
            {
                _stepTimer.Stop();
                _stepTimer = null;
            }
            _follower = null;
            // Callers read the flag before stopping; leaving it set would
            // make the NEXT walk abort on the last one's bad news.
            _walkStalled = false;
        }

        protected bool WalkArrived => _walkArrived;
        protected bool WalkStalled => _walkStalled;
        protected bool Walking => _stepTimer != null;
        protected Point3D WalkGoal => _walkGoal;

        private void WalkStep(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Alive ||
                bot.Map == null || bot.Map == Map.Internal ||
                _follower == null)
            {
                StopWalk();
                return;
            }

            if (_follower.Follow(false, _walkRange))
            {
                _walkArrived = true;
                StopWalk();
                return;
            }

            int dist = Dist(bot.Location, _walkGoal);
            if (dist < _walkBestDist)
            {
                _walkBestDist = dist;
                _walkBestAt = Core.Now;
                return;
            }

            if (Core.Now - _walkBestAt > WalkStallLimit)
            {
                // Stop first, then raise the flag: StopWalk clears it (so a
                // new walk starts clean), and setting it before the stop
                // meant nobody ever saw a stall. Ghosts five tiles from a
                // shrine ankh on its platform walked at it, stalled,
                // forgot, and started again until the twenty-minute net.
                StopWalk();
                _walkStalled = true;
            }
        }

        protected static int Dist(Point3D a, Point3D b)
        {
            int dx = a.X - b.X; if (dx < 0) dx = -dx;
            int dy = a.Y - b.Y; if (dy < 0) dy = -dy;
            return dx > dy ? dx : dy;
        }
    }
}
