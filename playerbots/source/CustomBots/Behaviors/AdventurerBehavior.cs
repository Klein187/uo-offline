// =========================================================================
// AdventurerBehavior.cs — Bots that explore the wilderness/dungeons,
// engaging monsters they encounter. Uses PathFollower (A*) for real
// pathfinding so they navigate around obstacles, into dungeons, etc.
//
// Architecture:
//   Decision tick (2s):
//     - In combat? Engage / chase / retreat
//     - Look for enemies in sight; engage
//     - Otherwise pick a patrol point and pathfind to it
//     - Random pauses, stuck recovery
//
//   Step timer (400ms walk / 200ms run):
//     - Calls PathFollower.Follow — A* handles everything
//
// Patrol point selection:
//   Random point within WanderRadius of Home. PathFollower routes around
//   any obstacles. If unreachable (stuck > timeout), pick another.
//
// Permadeath: PlayerBot's death drops corpse; spawner replaces the bot.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;
using MoveDelays = Server.Movement.Movement;

namespace Server.CustomBots
{
    public class AdventurerBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Adventurer";

        // ---- Tunables ----
        public int WanderRadius { get; set; } = 60;
        public int PatrolRange  { get; set; } = 25;
        public int SightRange   { get; set; } = 10;
        // HP fraction at which a bot breaks off and flees. Set fairly high
        // — a bot that waits until it's nearly dead gets finished by a few
        // hits before it can escape. Better to bail early and survive.
        public double RetreatHpFraction { get; set; } = 0.55;
        public int ArrivalRange { get; set; } = 2;

        // ---- Defender mode ----
        //
        // When a Traveler gets attacked on the road it swaps to an
        // Adventurer in DEFENDER mode rather than passively eating hits.
        // A defender:
        //   - Fights with the full Adventurer combat (surround, chase).
        //   - Does NOT go hunting — it only deals with what attacked it.
        //     When there's no combatant and no enemy in sight, the fight
        //     is over and it returns to traveling.
        //   - Retreats SOONER than a hunting Adventurer — it didn't want
        //     this fight and just wants to get back on the road.
        //
        // Set DefenderMode=true (and optionally a retreat fraction) before
        // attaching the behavior. When the fight ends the behavior swaps
        // the bot back to a Traveler automatically.
        public bool DefenderMode { get; set; }
        public double DefenderRetreatHpFraction { get; set; } = 0.65;

        // ---- Ranged combat ----
        //
        // Archer/Ranger (and, later, Mage) bots fight at a distance rather
        // than closing to melee. When RangedCombat is true the bot:
        //   - Holds a STANDOFF band from the foe (StandoffMin..StandoffMax
        //     tiles) instead of stepping adjacent.
        //   - Kites: if the foe closes inside StandoffMin, the bot steps
        //     AWAY to reopen the gap.
        //   - If the foe is beyond StandoffMax, the bot closes until it's
        //     back in band.
        //   - Within band: hold position. ModernUO's combat system auto-
        //     fires the equipped bow at bot.Combatant each weapon tick.
        //
        // Set by OnAttached based on bot.Class (Archer / Ranger), or by a
        // caller before attach.
        public bool RangedCombat { get; set; }
        public int StandoffMin { get; set; } = 4;
        public int StandoffMax { get; set; } = 8;

        // ---- Spellcaster combat (real ModernUO casting) ----
        //
        // Mage bots fight at range, casting real ModernUO spells. The cast
        // lifecycle is OWNED BY THE FAST LOOP (StepRangedCombat) — one
        // place starts the cast, tracks it, fulfils its target cursor, and
        // sets the cooldown only when it actually resolves. There is no
        // separate polling chain racing it.
        //
        // The lifecycle, all driven from the fast loop (~300ms ticks):
        //   1. Idle  — no cast. If in-band + LOS + cooldown ready, START a
        //      cast: build the Spell, call Cast(), set _castInProgress.
        //   2. Casting — _castInProgress true. Each tick: if bot.Target is
        //      up (the cast delay elapsed), Invoke it on the foe → cast
        //      done, set cooldown. If bot.Spell went null without us
        //      invoking, the cast was disturbed → failed, short cooldown.
        //      If it's run past a timeout, give up.
        //
        // SpellcasterMode is set in OnAttached for Mage-class bots.
        public bool SpellcasterMode { get; set; }

        // ---- T2A tank mage ----
        // A Mage-class bot whose template rolled a real weapon skill —
        // the classic "hally mage". Tank mages hold their ground at melee
        // range instead of kiting: every cast pockets the weapon (the
        // pre-AOS ClearHands rule), so the loop re-arms it between casts
        // and the engine lands the swing while the next spell winds up —
        // the famous e-bolt + halberd rhythm.
        private bool      _tankMage;
        private SkillName _tankSkill;

        // Cooldown gate — earliest time the next cast may START. Set only
        // when a cast RESOLVES (lands or fails), never at launch, so a
        // disturbed cast doesn't burn a full cooldown.
        private DateTime _nextCastAllowed = DateTime.MinValue;

        // Cast-in-progress tracking, all owned by the fast loop.
        private bool     _castInProgress;     // a Cast() is underway
        private DateTime _castStartedAt;      // when — for timeout safety
        private double   _castCooldown = 2.0; // post-cast cooldown seconds
        // True when the current cast targets the BOT ITSELF (a self-heal
        // or self-cure) rather than the foe. The fast loop invokes the
        // spell's target cursor on the bot, not the enemy.
        private bool     _castSelfTarget;
        // True when the current cast was started DELIBERATELY at point-
        // blank range (the mage was cornered and chose to cast anyway).
        // Such a cast must NOT be self-disturbed by the "foe too close"
        // logic — being cornered is exactly why it cast. It commits.
        private bool     _castPointBlank;
        // A cast that hasn't produced a target cursor within this long is
        // assumed dead (fizzled, disturbed, interrupted) and abandoned.
        private static readonly TimeSpan CastTimeout = TimeSpan.FromSeconds(5);

        // When a Traveler hands off to a defender, it stashes the trip's
        // destination here. When the fight ends the defender builds the
        // resumed Traveler with this destination preset, so the bot
        // continues to where it was originally headed instead of picking
        // somewhere brand new. Null = let the new Traveler pick fresh.
        public string ResumeDestination { get; set; }

        public override string GetStatusLine(PlayerBot bot)
        {
            if (bot.Combatant is Mobile foe && !foe.Deleted && foe.Alive)
            {
                return DefenderMode
                    ? $"defending itself vs {foe.Name}"
                    : $"fighting {foe.Name}";
            }
            if (Core.Now < _restingUntil)
            {
                return "resting after a fight";
            }
            if (DefenderMode)
            {
                return string.IsNullOrEmpty(ResumeDestination)
                    ? "fight over — returning to the road"
                    : $"fight over — resuming trip to {ResumeDestination}";
            }
            return "hunting for trouble";
        }

        public Point3D Home { get; private set; }
        public Map     HomeMap { get; private set; }

        // ---- State ----
        private Point3D? _goal;
        private ILegFollower _follower;
        private bool _running;
        // Last known mount state — combined with _running to decide if the
        // step timer needs to restart at a different rate.
        private bool _wasMounted;

        private DateTime _pauseUntil = DateTime.MinValue;

        // Stuck detection. The streak counts consecutive windows without a
        // single step — goal repicks fix a bad goal, but a bot that stays
        // rooted through repeated repicks is blocked in PLACE and needs
        // the physical escalation (sidestep, then wedge extraction).
        private Point3D _lastLoc;
        private DateTime _lastProgressAt;
        private int _patrolStuckStreak;
        private static readonly TimeSpan StuckTimeout = TimeSpan.FromSeconds(10);

        private Timer _stepTimer;

        // When set, the step timer does GREEDY chase steps toward this foe
        // instead of PathFollower steps. Used for close-range pursuit so a
        // bot keeps pace with a fleeing monster — the step timer fires
        // every 200-400ms, fast enough to catch a runner, whereas the 2s
        // decision tick is far too slow. Cleared when the bot disengages.
        private Mobile _chaseFoe;

        // RANGED COMBAT — the unified fast-timer foe for mage/archer bots.
        //
        // When set, the step timer (firing every 200-400ms) runs the
        // ENTIRE ranged-combat loop each fire: measure distance, kite if
        // too close, close if too far, and cast/shoot if in band. This
        // exists because the 2-second decision tick is far too slow to
        // react to a monster closing the gap — by the time the slow tick
        // noticed, the monster was already in melee. Running positioning
        // on the fast timer lets the bot react in ~300ms.
        //
        // The decision tick just acquires the foe and hands it here; all
        // ranged positioning + attacking happens in the StepOnce loop.
        private Mobile _rangedFoe;
        // Why the last cast was chosen, for the [CombatDebug line. Tuning
        // the footwork is guesswork without it: "band" and a distance means
        // the kiting worked, "pinned" or "outpaced" means it did not.
        private string _castReason = "?";

        // When the bot first found itself inside melee while trying to hold
        // its band. MinValue means it is not currently trying to break away.
        private DateTime _kiteBreakSince = DateTime.MinValue;

        // How long it keeps walking before it accepts that it cannot shake
        // this foe and goes back to fighting at arm's length. Long enough
        // for four or five steps, short enough that a mage cornered by
        // something as fast as it is does not spend the fight running.
        private static readonly TimeSpan KiteBreakGrace = TimeSpan.FromSeconds(2.5);

        // What a tank mage will break off a swing for: sixth-circle mana,
        // and only about half the times the cooldown is up.
        private const int    TankMageRoomMana   = 20;
        private const double TankMageRoomChance = 0.5;

        // FLEE MODE — when set, the bot is running for its life. The fast
        // loop drops everything else and sprints directly away from this
        // threat with double-steps. Triggered when HP falls below the
        // retreat threshold. _fleeUntil bounds it so a bot that has gotten
        // clear eventually stops and resumes normal behavior.
        private Mobile   _fleeFrom;
        private DateTime _fleeUntil;

        // Flee mode itself. A bot can be running from a ROOM rather than
        // from one monster (see the withdraw check on the decision tick),
        // so the mode can't be inferred from _fleeFrom being set.
        private bool     _fleeing;

        // FIGHTING RETREAT. Set by StartFlee after looking the pack over:
        // true when nothing in it is beyond the bot on its own, so the
        // right play is to run until they string out and then turn on the
        // one in front. False means a true flee: something in there would
        // beat the bot one on one, and the place is remembered so the bot
        // does not walk back into it (see _avoidCenter).
        private bool     _fightingRetreat;
        private bool     _pickingOff;      // stopped running, on the straggler
        private Mobile   _pickOffFoe;
        private int      _pickOffTurns;    // turns taken without a kill
        private DateTime _pickOffSince = DateTime.MinValue;

        // Where the last pack the bot could not handle was standing, and
        // for how long fresh fights near it are declined. Without this a
        // bot that got clear turned straight round, took the first monster
        // that had followed it, was outnumbered again in a second, and ran
        // back to the waypoint it had just left. Back and forth for
        // minutes, never a kill on either side.
        private Point3D  _avoidCenter;
        private DateTime _avoidUntil = DateTime.MinValue;

        // HUNTED. A flee that ends "clear" at fourteen tiles is no use
        // against something that follows: a lich walked one mage between
        // the same two waypoints seven times in a row, ten tiles apart,
        // each flee starting at a few hit points less than the last. The
        // second flee from the same thing inside a minute means it is not
        // giving up, so the bot stops treating fourteen tiles as safe and
        // runs until it really is out of reach, and stays away longer.
        private string   _lastFleeName;
        private DateTime _lastFleeAt = DateTime.MinValue;
        private DateTime _fleeStreakStart = DateTime.MinValue;
        private int      _fleeStreak;
        private bool     _hunted;
        private bool     _withdrawFlee;   // nobody attacking, the room was just too much

        // UNREACHABLE-FOE detection. A bot can lock onto a monster it can SEE
        // but cannot physically reach — the classic case is a large rat (or
        // other critter) inside a sealed building: visible through a wall/
        // window, so FindNearbyEnemy's LOS preference picks it, but there's no
        // walkable path to it. PathFollower then falls back to stepping
        // straight at it, the bot jams against the building wall, and nothing
        // ever gives up (the patrol stuck-check sits past combat's early
        // return). Bots pile on the wall forever.
        //
        // We track the closest tile-distance the bot has achieved toward its
        // current foe. While the bot is NOT yet in attack position and that
        // distance stops improving, the foe is declared unreachable: the bot
        // abandons it and ignores it for a short while so it doesn't instantly
        // re-lock the same wall-blocked monster.
        private Mobile   _progressFoe;
        private int      _bestFoeDist;
        private DateTime _foeProgressAt;
        private static readonly TimeSpan UnreachableTimeout = TimeSpan.FromSeconds(7);

        // How long an abandoned foe stays ignored. The ignore list itself
        // lives on the BOT (PlayerBot.MarkUnreachable / IsUnreachable) so it
        // survives the Traveler<->defender swap — a monster hitting the bot
        // through a wall would otherwise respawn a fresh, memory-less defender
        // every time this one gives up. Bounded so the bot re-tries later
        // (a door may have opened, or it has wandered to a new spot).
        private static readonly TimeSpan UnreachableIgnore = TimeSpan.FromSeconds(30);

        // ---- Threat assessment (who a bot dares to pick a fight with) ----
        //
        // A bot sizes a monster up before engaging. Foe toughness is proxied
        // by HitsMax (mongbat ~10, orc ~60, lich ~120, dragon ~800) and each
        // skill tier has a "dare" ceiling: a Novice picks fights with rats
        // and skeletons; only a Grandmaster starts one with a dragon.
        //
        // Bravery in numbers: every friendly bot ALREADY fighting the foe
        // raises the effective ceiling by 60% — a monster no single bot
        // would touch gets swarmed once somebody starts the fight. This is
        // what lets a crowd of mid-tier bots bring down an event boss.
        //
        // A foe that is attacking the bot is never filtered out: the bot
        // fights back up to ~1.5x its ceiling, and beyond that it turns and
        // RUNS instead of trading hits it can't afford.
        private static readonly int[] TierDare =
        {
            45,   // Novice      — rats, mongbats, skeletons
            70,   // Apprentice  — zombies, orcs
            100,  // Journeyman  — ettins, earth elementals
            150,  // Adept       — trolls, liches
            240,  // Expert      — ogre lords, gazers
            400,  // Master      — daemons
            700,  // Grandmaster — dragons (and with friends, anything)
        };

        // A monster this far away registers only because it's in a fight
        // with the bot or a friendly bot (assist awareness) — plain
        // hostiles are still only noticed within SightRange. Kept modest so
        // bots don't aggro across half a screen.
        public int AssistRange { get; set; } = 14;

        // How close another monster has to be to a foe to count as being
        // WITH it — i.e. as something that joins in when the bot engages.
        public int PackRadius { get; set; } = 5;

        // A kill only makes the event journal (and thus bank gossip) when
        // the foe was at least this beefy — an ettin or lich is news, a
        // giant rat is not.
        protected const int NotableFoeHits = 90;

        // Target-switch hysteresis — don't re-pick mid-fight more than once
        // per window, or two attackers make the bot ping-pong between them.
        private DateTime _nextSwitchAllowed = DateTime.MinValue;

        // GANG PRESSURE — how many hostiles were actively targeting the bot
        // at the last decision tick. Every attacker past the first raises
        // the retreat threshold (see CheckRetreat): a player holds a 1v1 at
        // 60% health, but the same player with three monsters on them
        // leaves — incoming damage scales with attackers while outgoing
        // doesn't. Pile-ons are how bots actually die.
        private int _packAttackers;

        // How often a hunter pauses to consider whether it's out of
        // supplies and should break off to go shopping (see the patrol
        // section + BotSupplies).
        private DateTime _nextSupplyThink = DateTime.MinValue;

        // A self-bandage in flight finishes ~9-13s out (pre-AOS timing,
        // dex-scaled). BandageContext.BeginHeal CANCELS a running context,
        // so restarting on the old 2s cadence reset the timer forever and
        // never landed a single heal. No bandage touches while this is up.
        private DateTime _bandageBusyUntil = DateTime.MinValue;

        private static readonly string[] AmbientChat = { "small_talk", "lfg" };
        private static readonly string[] CombatChat  = { "combat_actions" };

        // Overridable so subclasses can flavor the idle chatter — a
        // DungeonCrawler whispers in the dark instead of asking for
        // groups it's already in.
        protected virtual string[] AmbientChatCategories => AmbientChat;

        // Post-fight breather: while set, the bot stands and bandages
        // (fighters) or meditates (casters) instead of striding off at
        // 40% health / 10 mana.
        private DateTime _restingUntil = DateTime.MinValue;

        // True while the current rest is a MEDITATION (mana came back low)
        // — restores mana as well as health during the rest ticks.
        private bool _meditating;

        // Mana thresholds: rest after a fight below 40%; sit down even
        // mid-patrol below 30% (a caster on fumes doesn't wander into the
        // next fight with an empty pool).
        private const double PostFightManaFraction = 0.40;
        private const double PatrolManaFraction    = 0.30;

        // Classes that fight from the mana pool. SpellcasterMode covers
        // Mages; Healers and Tamers carry real Magery too.
        private bool IsCaster(PlayerBot bot) =>
            SpellcasterMode ||
            bot.Class is BotClass.Mage or BotClass.Healer or BotClass.Tamer
                      or BotClass.TreasureHunter;

        // Begin a rest window. Meditation runs longer than a bandage stop
        // and announces itself with the era emote + the meditation hum.
        private void StartRest(PlayerBot bot, bool meditate)
        {
            _meditating = meditate;
            _restingUntil = Core.Now + TimeSpan.FromSeconds(
                meditate ? Utility.RandomMinMax(15, 30)
                         : Utility.RandomMinMax(10, 20));
            // Resting is silent — the meditation hum is the era's only tell.
            if (meditate)
            {
                bot.PlaySound(0xF9);
            }
            if (CombatDebug)
            {
                Console.WriteLine($"[Bot {bot.Name}] resting " +
                    $"({(meditate ? $"meditating, {bot.Mana}/{bot.ManaMax} mana" : $"bandaging, {bot.Hits}/{bot.HitsMax} hp")})");
            }
        }

        public AdventurerBehavior()
        {
            ChatCategories  = AmbientChat;
            ChatChance      = 0.12;
            MinChatCooldown = TimeSpan.FromSeconds(20);
            MaxChatCooldown = TimeSpan.FromSeconds(60);
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);
            Home    = bot.Location;
            HomeMap = bot.Map;
            _lastLoc        = bot.Location;
            _lastProgressAt = Core.Now;

            // Experience shows in WHEN you bail (IDEAS 3.1): veterans break
            // off with over half their health; novices misjudge fights and
            // hang on too long — which is how deaths (UO's most iconic
            // experience) actually happen now and then. Crawlers inherit
            // this, so dungeons are where the tail risk mostly lives.
            RetreatHpFraction = bot.SkillTier switch
            {
                BotSkillTier.Novice     => 0.32,
                BotSkillTier.Apprentice => 0.40,
                BotSkillTier.Journeyman => 0.47,
                BotSkillTier.Adept      => 0.52,
                _                       => 0.55,
            };

            // Archer/Ranger bots fight at range. Unless a caller has
            // already set RangedCombat explicitly, infer it from class.
            if (!RangedCombat &&
                (bot.Class == BotClass.Archer || bot.Class == BotClass.Ranger))
            {
                RangedCombat = true;
            }

            // Mage bots also fight at range, and additionally throw spells.
            // Treasure Hunters too — their template has no weapon line;
            // real Magery is how the era's diggers answered chest
            // guardians. (Tank-mage detection below naturally excludes
            // them: no weapon skill >= the threshold.)
            if (bot.Class is BotClass.Mage or BotClass.TreasureHunter)
            {
                RangedCombat    = true;
                SpellcasterMode = true;
                // Mages need MORE distance than archers. Getting hit while
                // casting disturbs the spell (Spell.OnCasterHurt -> Disturb
                // for Player casters, and bots are Players). A wider, more
                // distant band means a charging monster has to cover more
                // ground before it can interrupt a cast — and the bot
                // starts kiting sooner.
                StandoffMin = 6;
                StandoffMax = 10;

                // (Bard instrument check runs below for every class.)
                // Tank mage detection — did this mage's template roll a
                // weapon line? (BotSkillTemplate bakes it at creation.)
                double sw = bot.Skills[SkillName.Swords].Base;
                double mc = bot.Skills[SkillName.Macing].Base;
                double fn = bot.Skills[SkillName.Fencing].Base;
                double best = Math.Max(sw, Math.Max(mc, fn));
                if (best >= BotSkillTemplates.TankWeaponSkillMin)
                {
                    _tankMage  = true;
                    _tankSkill = best == sw ? SkillName.Swords
                               : best == mc ? SkillName.Macing
                               : SkillName.Fencing;
                }
            }

            RollNerve(bot);

            // A bard heading into the field packs its lute. (Tamers get no
            // pet here — pets come out of the STABLES: the travel leg that
            // brought the tamer detours to claim it, like a real player.
            // A tamer that arrives petless hunts petless.)
            EnsureInstrument(bot);
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopStepTimer();
            _chaseFoe = null;
            _rangedFoe = null;
            _fleeFrom = null;
            _fleeing = false;
            _fightingRetreat = false;
            _pickingOff = false;
            _pickOffFoe = null;
            _pickOffTurns = 0;
            _avoidUntil = DateTime.MinValue;
            _fleeStreak = 0;
            _hunted = false;
            _lastFleeName = null;
            _progressFoe = null;
            _pullUntil = DateTime.MinValue;
            _nextWithdrawAt = DateTime.MinValue;
            _gambling = false;
            ClearEscapeRoute();
            ClearCast();
            _nextCastAllowed = DateTime.MinValue;
            base.OnDetached(bot);
        }

        // -------------------------------------------------------------------
        // Decision tick.
        // -------------------------------------------------------------------
        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal || bot.Deleted)
            {
                StopStepTimer();
                return;
            }

            // Fleeing for its life — let the fast loop run the escape.
            // Don't acquire targets, don't make travel decisions; the bot
            // is busy surviving. The flee branch clears _fleeFrom and stops
            // the timer once the bot is clear, and the next tick proceeds
            // normally from there.
            if (_fleeing)
            {
                EnsureStepTimer(bot, running: true);
                return;
            }

            // Bard arts — provoke/peace fire on their own cooldown, in and
            // out of combat (getting a monster off you is exactly when you
            // play). Cheap no-op for everyone without the skills.
            if (bot.Alive)
            {
                TryBardArts(bot);
            }

            // If this is a timed destination visit (e.g. a graveyard
            // stop), return to traveling once the window is up — but only
            // when NOT in combat. A bot mid-fight finishes the fight first;
            // it'll re-check next tick once the combatant clears.
            if (bot.Combatant == null && CheckVisitExpired(bot)) return;

            ChatCategories = bot.Combatant != null ? CombatChat : AmbientChatCategories;
            TrySpeak(bot);

            // -- 1. Combat --
            var combatant = bot.Combatant;
            if (combatant is Mobile foe)
            {
                bool foeKilled = foe.Deleted || !foe.Alive;
                bool foeGone = foeKilled ||
                               foe.Map != bot.Map ||
                               !bot.InRange(foe.Location, SightRange + 4);

                if (foeGone)
                {
                    // Dropped it (as opposed to it wandering off) — gloat.
                    if (foeKilled)
                    {
                        TryEventLine(bot, 0.35, "combat_victory");

                        // One down. The fighting retreat is working; the
                        // turn count starts over for the next one.
                        if (_pickingOff && foe == _pickOffFoe)
                        {
                            Console.WriteLine(
                                $"[Bot {bot.Name}] picked off '{foe.Name}' " +
                                $"(hp {bot.Hits}/{bot.HitsMax})");
                            _pickOffTurns = 0;
                        }

                        // Notable kills go in the shard's event journal so
                        // bank gossip can retell them. Trash mobs (rats,
                        // birds) don't make the news, and bot-vs-bot kills
                        // are journaled by the VICTIM's OnDeath (as pk /
                        // faction) — don't double-report them here.
                        if (foe.HitsMax >= NotableFoeHits && foe is not PlayerBot)
                        {
                            BotEventJournal.Record("kill", bot, foe.Name ?? "a monster");
                        }
                    }

                    // This foe is down or out of range.
                    bot.Combatant = null;
                    _goal = null;
                    _follower = null;
                    _chaseFoe = null;
                    _rangedFoe = null;
                    _progressFoe = null;
                    _packAttackers = 0;
                    _pickingOff = false;
                    _pickOffFoe = null;
                    ClearCast();
                    StopStepTimer();

                    // Defender: if nothing else is hostile, the fight is
                    // over — go back to traveling. If another enemy is
                    // still around, fall through to section 2 to re-engage.
                    if (DefenderMode)
                    {
                        if (ReturnToTravelIfSafe(bot)) return;
                        // still hostiles nearby → fall through to section 2
                    }
                    else
                    {
                        // Hunting adventurer: foe gone. If the fight left a
                        // mark, sit a moment before moving on (IDEAS 6.2):
                        // casters meditate the mana pool back (an empty
                        // mage is no mage), fighters bandage up.
                        bool drained = IsCaster(bot) &&
                            bot.Mana < bot.ManaMax * PostFightManaFraction;
                        bool hurt = bot.Hits < bot.HitsMax * 0.70;
                        if (drained || hurt)
                        {
                            StartRest(bot, meditate: drained);
                        }
                        return;
                    }
                }
                else
                {
                    // Foe is alive and in range. The fast loop's CheckRetreat
                    // normally triggers the flee before we get here, but as
                    // a backstop the decision tick checks too — if HP is
                    // below the threshold, flee instead of fighting.
                    if (CheckRetreat(bot, foe)) return;

                    // Something ELSE beating on us while we chase a foe
                    // that isn't? Turn on the attacker instead.
                    var switched = ReconsiderTarget(bot, foe);
                    if (switched != foe)
                    {
                        foe = switched;
                        bot.Combatant = foe;
                    }

                    // Can't get to it? Give up before piling on a wall.
                    if (CheckUnreachable(bot, foe)) return;

                    ChaseFoe(bot, foe);
                    return;
                }
            }

            // -- 2. Look for an enemy --
            var target = FindNearbyEnemy(bot, out bool overwhelming);

            // Hurt below the line this bot would flee at? Then it has no
            // business starting anything. If the thing is already on it,
            // keep running; if it isn't, decline the fight and fall
            // through to the rest branch to bandage up first.
            if (target != null && TooHurtToStart(bot, Math.Max(1, _targetKnot.Count)))
            {
                var scene = NearThreat(bot);
                if (scene.Attackers > 0 || TileDist(bot, target.Location) <= 3)
                {
                    StartFlee(bot, target);
                    return;
                }
                target = null;
            }

            if (target != null)
            {
                // Way out of the bot's league AND coming for it — don't
                // trade hits it can't afford. Run, screaming.
                if (overwhelming)
                {
                    StartFlee(bot, target);
                    return;
                }

                // Rushing into a friend's fight gets an assist shout;
                // starting a fresh fight gets a battle cry.
                bool assisting = target is BaseCreature tbc &&
                                 tbc.Combatant is PlayerBot &&
                                 tbc.Combatant != bot;
                TryEventLine(bot, 0.4, assisting ? "combat_assist" : "combat_engage");
                Console.WriteLine(
                    $"[Bot {bot.Name}] {(assisting ? "assisting against" : "engaging")} " +
                    $"'{target.Name}'");

                bot.Combatant = target;

                // It came with company. Melee has to close to swing, so it
                // drags the target off its friends first rather than
                // walking into the middle of them. Ranged bots don't need
                // this — their standoff band below already widens when the
                // room is crowded, which is the same idea done by kiting.
                if (!RangedCombat && _targetKnot.Count > 1)
                {
                    _pullUntil = Core.Now + PullWindow;
                    _pullFrom  = _targetKnot.Center;
                    if (CombatDebug)
                    {
                        Console.WriteLine(
                            $"[Bot {bot.Name}] pulling '{target.Name}' off " +
                            $"{_targetKnot.Count - 1} other(s)");
                    }
                }
                else
                {
                    _pullUntil = DateTime.MinValue;
                }

                // Route by combat style. A ranged bot (mage/archer) must
                // NOT melee-walk to the foe's tile — hand it straight to
                // the ranged loop so it kites/casts from the first moment.
                if (RangedCombat)
                {
                    RangedPosition(bot, target);
                }
                else
                {
                    SetGoal(bot, target.Location, running: true);
                }
                return;
            }

            // Nothing here worth picking a fight with — but the room can
            // still be full of things that will happily pick one with US.
            // Standing in it is how bots died without ever choosing a
            // fight: the threat gate declined every target, and then the
            // bot carried on patrolling through the middle of the camp.
            //
            // This is a LAST RESORT, not a proximity alarm. Written the
            // obvious way (anything within sight, weight over budget) it
            // fired constantly underground, because a dungeon always has
            // two monsters within ten tiles: crawlers backed out of rooms
            // nothing was even attacking them in, re-triggered eight
            // seconds later, and never fought at all. So it wants all
            // three of: something genuinely ON TOP of the bot, a room
            // weighing more than DOUBLE what the bot can answer, and a
            // cooldown, so a bot that can't get clear gets on with its
            // life instead of shuffling in and out forever.
            var room = NearThreat(bot);
            if (Core.Now >= _nextWithdrawAt &&
                room.Count >= 2 &&
                room.NearestDist <= WithdrawRange &&
                room.Weight > RoomBudget(bot) * 2 * _nerve)
            {
                _nextWithdrawAt = Core.Now + WithdrawCooldown;
                if (CombatDebug)
                {
                    Console.WriteLine(
                        $"[Bot {bot.Name}] withdrawing — {room.Count} hostiles " +
                        $"weighing {room.Weight} vs budget {RoomBudget(bot)}, " +
                        $"nearest {room.NearestDist}");
                }
                OnWithdrew(bot, room.Center);
                StartFlee(bot, null);
                return;
            }

            // Defender with no combatant and no enemy in sight: the fight
            // is fully over. Return to traveling.
            if (DefenderMode)
            {
                ResumeTraveling(bot);
                return;
            }

            // -- 2.5 Resting — bandaging/meditating after a fight. Recover
            // in visible increments; any new combat above already
            // preempted this.
            if (Core.Now < _restingUntil)
            {
                StopStepTimer();
                if (bot.Hits < bot.HitsMax)
                {
                    bot.Hits = Math.Min(bot.HitsMax,
                        bot.Hits + Math.Max(2, bot.HitsMax / 16));
                }
                if (_meditating && bot.Mana < bot.ManaMax)
                {
                    bot.Mana = Math.Min(bot.ManaMax,
                        bot.Mana + Math.Max(3, bot.ManaMax / 10));
                }
                return;
            }

            // A caster on fumes (repeated skirmishes, foes that fled) sits
            // and meditates BEFORE wandering into the next fight, even
            // when no fight just ended.
            if (!DefenderMode && IsCaster(bot) && bot.ManaMax >= 20 &&
                bot.Mana < bot.ManaMax * PatrolManaFraction)
            {
                StartRest(bot, meditate: true);
                return;
            }

            // Out of arrows / reagents / bandages? A real hunter breaks
            // off and goes SHOPPING — there are no invisible refills.
            // Pure hunters only: defenders resume their trip anyway (the
            // Traveler side runs its own errand check), crawlers end
            // their run through exit mode, and party members finish the
            // hunt with the group.
            if (!DefenderMode && GetType() == typeof(AdventurerBehavior) &&
                Core.Now >= _nextSupplyThink)
            {
                _nextSupplyThink = Core.Now + TimeSpan.FromMinutes(2);
                if (BotSupplies.PickErrandDestination(bot) is string errand)
                {
                    Console.WriteLine(
                        $"[supplies] {bot.Name} breaks off the hunt to restock");
                    bot.Behavior = new TravelerBehavior { DestinationName = errand };
                    return;
                }
            }

            // -- 3. Stuck check --
            if (bot.Location != _lastLoc)
            {
                _lastLoc = bot.Location;
                _lastProgressAt = Core.Now;
                _patrolStuckStreak = 0;
            }
            else if (Core.Now - _lastProgressAt > StuckTimeout)
            {
                // Window 1: try a different patrol point (a bad goal is the
                // common case). Window 2+: the bot hasn't taken a SINGLE
                // step through a repick — it's blocked in place, so break
                // it loose physically. Window 4: even sidesteps couldn't
                // move it — wedged in rock/decor; extract it, since no
                // goal choice can ever free a bot the engine won't move.
                _patrolStuckStreak++;
                _goal = null;
                _follower = null;
                _lastProgressAt = Core.Now;

                if (_patrolStuckStreak >= 4)
                {
                    _patrolStuckStreak = 0;
                    BotStuckEscape.TryExtract(bot, SerializableName);
                }
                else if (_patrolStuckStreak >= 2)
                {
                    if (_patrolStuckStreak == 2)
                    {
                        StuckTelemetry.Record(bot, "patrol_stuck", SerializableName);
                    }
                    BotStuckEscape.SidestepAny(bot);
                }
            }

            // -- 4. Pause --
            if (Core.Now < _pauseUntil)
            {
                StopStepTimer();
                return;
            }
            if (Utility.RandomDouble() < 0.05)
            {
                _pauseUntil = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(2, 5));
                StopStepTimer();
                return;
            }

            // -- 5. Patrol --
            EnsurePatrolGoal(bot);
            if (_goal != null)
            {
                EnsureStepTimer(bot, running: PatrolRuns);
            }
        }

        // -------------------------------------------------------------------
        // EnsurePatrolGoal — picks or refreshes the current patrol goal.
        // -------------------------------------------------------------------
        private void EnsurePatrolGoal(PlayerBot bot)
        {
            // A subclass tracking a MOVING target (a player-party follower
            // shadowing its leader) can invalidate the current goal so it
            // re-picks every pass instead of walking to a stale spot.
            if (_goal != null && PatrolGoalStale(bot, _goal.Value))
            {
                _goal = null;
                _follower = null;
            }

            bool reached = _goal != null &&
                           (bot.InRange(_goal.Value, ArrivalRange) || PatrolGoalReachedEarly(bot, _goal.Value));

            // Reached the current goal — give a subclass first refusal. A
            // DungeonCrawler uses this to fire a level transition when it
            // steps onto a teleporter point. If the hook claims the arrival
            // (returns true), it has taken over (possibly teleporting or
            // swapping behavior) and we must not pick a new goal here.
            if (reached && OnPatrolGoalReached(bot))
            {
                _goal = null;
                _follower = null;
                return;
            }

            // If we've reached the current goal (or have none), pick new.
            if (_goal == null || reached)
            {
                // A subclass may supply the next goal (e.g. the crawler's
                // scoped dungeon points). Null means "use the default
                // wilderness patrol below".
                var custom = SelectPatrolGoal(bot);
                if (custom.HasValue)
                {
                    _goal = custom.Value;
                }
                else
                {
                    // If we're far from home, head back. Otherwise pick a
                    // random point within PatrolRange.
                    int homeDx = bot.X - Home.X;
                    int homeDy = bot.Y - Home.Y;
                    int homeDistSq = homeDx * homeDx + homeDy * homeDy;
                    int wanderRadSq = WanderRadius * WanderRadius;

                    if (homeDistSq > wanderRadSq)
                    {
                        _goal = Home;
                    }
                    else
                    {
                        double angle = Utility.RandomDouble() * Math.PI * 2.0;
                        int dist = Utility.RandomMinMax(8, PatrolRange);
                        int tx = bot.X + (int)(Math.Cos(angle) * dist);
                        int ty = bot.Y + (int)(Math.Sin(angle) * dist);
                        _goal = new Point3D(tx, ty, bot.Z);
                    }
                }
                _follower = LegFollowers.Create(bot, _goal.Value, ZoneWalkOverride(bot, _goal.Value));
            }
            else if (_follower == null)
            {
                _follower = LegFollowers.Create(bot, _goal.Value, ZoneWalkOverride(bot, _goal.Value));
            }
        }

        // Whether a patrol leg walks the painted mesh. Null follows the
        // fleet mode; a crawler answers true inside drawn dungeon floors.
        protected virtual bool? ZoneWalkOverride(PlayerBot bot, Point3D goal) => null;

        // A subclass can call a goal reached before the bot is within
        // ArrivalRange of it: a crawler inside a drawn room outline is at
        // the room, wherever the room point sits.
        protected virtual bool PatrolGoalReachedEarly(PlayerBot bot, Point3D goal) => false;

        // ---- Patrol extension hooks (used by DungeonCrawlerBehavior) ----
        //
        // Default Adventurer behavior is unchanged: SelectPatrolGoal returns
        // null (use the wilderness patrol) and OnPatrolGoalReached returns
        // false (just pick the next patrol point). A subclass overrides these
        // to drive goal selection without reimplementing combat/nav/flee.

        // Supply the next patrol goal. Return null to use the default
        // wilderness patrol.
        protected virtual Point3D? SelectPatrolGoal(PlayerBot bot) => null;

        // Called when the current patrol goal is reached. Return true to
        // claim the arrival (the base will NOT pick a new goal this pass).
        protected virtual bool OnPatrolGoalReached(PlayerBot bot) => false;

        // The bot just backed out of a room it could not stand in. Nothing
        // was chasing it, so nothing stops its own patrol walking it
        // straight back in thirty seconds later: one crawler withdrew from
        // the same eight ghouls four times in two minutes. A subclass whose
        // patrol has a notion of "this room" drops it here.
        protected virtual void OnWithdrew(PlayerBot bot, Point3D room) { }

        // When false, the bot stops STARTING fights: FindNearbyEnemy only
        // returns foes already attacking it or a friend. A DungeonCrawler
        // in exit mode overrides this — on the way out you defend
        // yourself, you don't clear rooms.
        protected virtual bool WantsFreshFights => true;

        // The same question from outside. BotGrayWatch asks it before
        // handing this bot a red: a crawler on its way out with empty packs
        // has already decided it is done fighting, and a murderer walking
        // past does not change that.
        public bool LooksForTrouble => WantsFreshFights;

        // Running for its life right now. StartFlee clears Combatant, which
        // to anything looking from outside is indistinguishable from a bot
        // standing idle and free to take a fight. BotGrayWatch has to know
        // the difference or it hands back the fight the bot just broke off.
        public bool IsFleeing => _fleeing && Core.Now < _fleeUntil;

        // Patrol legs walk by default; a follower catching up to its
        // party leader overrides this to run.
        protected virtual bool PatrolRuns => false;

        // True = the current patrol goal no longer points where it
        // should (the tracked target moved) — re-pick this pass.
        protected virtual bool PatrolGoalStale(PlayerBot bot, Point3D goal) => false;

        // Extra "that's my friend" test for FindNearbyEnemy: a monster
        // fighting this mobile counts as attacking a friend. Lets a
        // player-party follower defend its real-player leader.
        protected virtual bool IsPartyFriend(PlayerBot bot, Mobile m) => false;

        // Stop all movement and clear the current goal — for a subclass that
        // is about to teleport and must not keep stepping toward a stale goal.
        protected void HaltMovement()
        {
            _goal = null;
            _follower = null;
            StopStepTimer();
        }

        // Defender helper: if no enemy remains in sight, the fight is over
        // — swap the bot back to a Traveler so it resumes its trip.
        // Returns true if it handled the situation (swapped to Traveler, or
        // started a flee) — the caller must return immediately. Returns
        // false when an enemy IS still around and worth fighting; the
        // normal enemy-search will re-engage it next.
        private bool ReturnToTravelIfSafe(PlayerBot bot)
        {
            var stillHostile = FindNearbyEnemy(bot, out bool overwhelming);
            if (stillHostile != null)
            {
                // The remaining hostile is hopeless to fight — run instead
                // of "resuming travel" straight through its teeth.
                if (overwhelming)
                {
                    StartFlee(bot, stillHostile);
                    return true;
                }
                return false;  // more to fight — stay a defender
            }

            ResumeTraveling(bot);
            return true;
        }

        // Swap the bot back to a Traveler. If this defender was carrying a
        // ResumeDestination, the new Traveler heads back there — the bot
        // continues its interrupted trip rather than picking somewhere new.
        private void ResumeTraveling(PlayerBot bot)
        {
            var traveler = new TravelerBehavior();
            if (!string.IsNullOrEmpty(ResumeDestination))
                traveler.DestinationName = ResumeDestination;
            bot.Behavior = traveler;
        }

        // -------------------------------------------------------------------
        // ChaseFoe — combat movement.
        //
        // Solves two problems with naive "path to foe.Location":
        //
        //   1. BUNCHING. If every bot targets the foe's exact tile, they
        //      pile onto the same approach tile and fight single-file. Fix:
        //      each bot claims a DIFFERENT free tile adjacent to the foe
        //      (an "attack slot") — the closest open one to itself. Bots
        //      naturally fan out around the monster.
        //
        //   2. FLEEING MONSTERS. PathFollower A*s to a target coord, but a
        //      running monster has moved by the time the path completes —
        //      the bot perpetually chases a stale position. Fix: when
        //      close, abandon A* and GREEDY-STEP straight at the foe every
        //      tick. A direct step re-aimed each tick keeps pace with a
        //      moving target where stale-target A* never catches up.
        // -------------------------------------------------------------------
        private void ChaseFoe(PlayerBot bot, Mobile foe)
        {
            // Ranged fighters use standoff positioning, not melee closing.
            if (RangedCombat)
            {
                RangedPosition(bot, foe);
                return;
            }

            int dist = TileDist(bot, foe.Location);

            // CLOSE range (including adjacent): hand the foe to the step
            // timer for GREEDY chase. The step timer fires every 200-400ms
            // — fast enough to keep pace with a fleeing monster, and while
            // ADJACENT it stays alive as the melee combat pulse: facing,
            // retreat checks and bandages all need ~300ms latency exactly
            // where the damage is coming in. (This branch used to stop the
            // timer when adjacent, which parked retreat AND self-heal on
            // the 2s decision tick — a toe-to-toe fighter could lose its
            // whole retreat margin in that gap, and never bandaged at all.)
            // ...and only when the bot can SEE it. TileDist is straight
            // line, so a monster three tiles away through a dungeon wall
            // reads as "close" and greedy stepping then walks the bot into
            // the wall and shuffles it left and right along the wall for as
            // long as the fight lasts. The door into that room is off to
            // one side and a greedy step never turns for it. No line of
            // sight means something is in the way, which is the pathfinder's
            // job, not the greedy step's.
            if (dist <= 4 && HasLOS(bot, foe))
            {
                _goal = null;
                _follower = null;
                _chaseFoe = foe;
                EnsureStepTimer(bot, running: true);
                return;
            }

            // FAR range: pick a free attack slot around the foe and A* to
            // it. The slot (not the foe's exact tile) fans bots out so they
            // surround the monster instead of single-filing onto one tile.
            _chaseFoe = null;
            var knot = Survey(bot, foe.Location, PackRadius);
            Point3D slot = knot.Count > 1
                ? PickAttackSlot(bot, foe, knot.Center)
                : PickAttackSlot(bot, foe);
            SetGoal(bot, slot, running: true);
        }

        // -------------------------------------------------------------------
        // RangedPosition — called from the slow (2s) decision tick. It does
        // NOT do positioning itself; it just hands the foe to the fast
        // step-timer loop in StepOnce, which runs the actual ranged combat
        // every 200-400ms. The decision tick is too slow to react to a
        // closing monster, so all positioning lives on the fast timer.
        // -------------------------------------------------------------------
        private void RangedPosition(PlayerBot bot, Mobile foe)
        {
            _chaseFoe = null;
            _goal     = null;
            _follower = null;
            _rangedFoe = foe;
            _kiteBreakSince = DateTime.MinValue;
            EnsureStepTimer(bot, running: true);
        }

        // -------------------------------------------------------------------
        // BeginCast — START a real ModernUO spell cast at the foe.
        //
        // This ONLY launches the cast and records tracking state. It does
        // NOT poll for the target or set the cooldown — the fast loop
        // (StepRangedCombat, in StepOnce) owns the rest of the lifecycle:
        // it watches for the target cursor, invokes it, and sets the
        // cooldown when the cast actually resolves.
        //
        // Spell choice: the mage's POOL is every book entry it has the
        // Magery AND the mana for, trimmed to the strongest AttackPoolDepth
        // entries, then weighted-random picked — so a GM mixes Energy
        // Bolt / Explosion / Flamestrike / Mind Blast instead of spamming
        // one spell, while a novice still plinks Magic Arrows. A foe that
        // isn't poisoned yet occasionally eats a Poison instead (mages use
        // their whole book, not just damage).
        //
        // Spells are built by reflection so an unknown class name fails
        // gracefully (step down the book). Requires LOS and reagents.
        // -------------------------------------------------------------------

        // The attack spell book, weakest first:
        // (type, minMagery, mana, cooldown, pick weight).
        // minGap is the daylight the bot wants before it will commit to a
        // spell. Pre-AOS, one blow landed mid-chant kills the spell outright
        // and only a FIRST circle survives it, so a sixth circle thrown at
        // arm's length is mana poured on the floor. The longer the chant,
        // the more room it needs — which is what makes the footwork below
        // worth doing: back up two tiles and the whole book opens up.
        private static readonly
            (string type, double minMagery, int mana, double cd, int weight, int minGap)[]
            AttackSpellBook =
        {
            ("Server.Spells.First.MagicArrowSpell",     0.0,  4, 2.0,  2, 0),
            ("Server.Spells.Second.HarmSpell",         25.0,  6, 2.0,  2, 3),
            ("Server.Spells.Third.FireballSpell",      40.0,  9, 2.25, 3, 4),
            ("Server.Spells.Fourth.LightningSpell",    55.0, 11, 2.5,  3, 4),
            ("Server.Spells.Fifth.MindBlastSpell",     70.0, 14, 2.5,  2, 5),
            ("Server.Spells.Sixth.EnergyBoltSpell",    85.0, 20, 3.0,  3, 5),
            ("Server.Spells.Sixth.ExplosionSpell",     90.0, 20, 3.0,  3, 5),
            ("Server.Spells.Seventh.FlameStrikeSpell", 95.0, 40, 3.5,  2, 6),
        };

        // How many of the strongest castable entries stay in the pick pool.
        private const int AttackPoolDepth = 4;

        // Console diagnostics for the high-volume combat events (per-cast
        // spell picks). The rare events (engage, flee, target switch) are
        // always logged. Flip to false once the combat pass is verified.
        // Verbose per-cast spell logging. Runtime-toggleable via
        // [CombatDebug on|off (no rebuild needed).
        public static bool CombatDebug = true;

        private void BeginCast(PlayerBot bot, Mobile foe, bool pointBlank = false)
        {
            // Live foe required.
            if (foe == null || foe.Deleted || !foe.Alive || foe.Map != bot.Map)
                return;

            // Don't stack — engine already has a spell, or we think we do.
            if (bot.Spell != null || _castInProgress) return;

            // Line of sight required — a spell with no LOS just fizzles
            // its own CheckHSequence. Skip; the loop will reposition.
            if (!HasLOS(bot, foe))
            {
                _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(0.6);
                return;
            }

            double magery = bot.Skills[SkillName.Magery].Base;
            int foeDist = TileDist(bot, foe.Location);

            // INSIDE SWING RANGE: first circle only.
            //
            // Pre-AOS, ANY damage taken mid-cast kills the spell outright
            // (Spell.OnCasterHurt -> Disturb(Hurt)), and the hurt-fizzle
            // effect is AOS-only, so nothing shows for it: the mantra goes
            // out and then nothing happens at all. Toe to toe with anything
            // that swings, every Fireball and Energy Bolt died that way and
            // the mage looked like it was chanting to itself.
            //
            // The engine leaves ONE door open. Spell.Disturb returns early
            // for a FIRST CIRCLE spell on a hurt disturb when Core.AOS is
            // false, so Magic Arrow lands through the blows. That is the
            // T2A melee answer too: in a scrum you spam magic arrow, you
            // don't feed sixth circles to the interrupt.
            //
            // A held foe can't swing, so it doesn't force the downgrade.
            //
            // This fires ONLY for a bot that has given up on getting away —
            // pinned in a corner, or matched for speed (see the break-away
            // in StepRangedCombat). Merely being close is no longer enough:
            // the answer to a monster in your face is to walk out of its
            // face, and the minGap column above keeps the big spells honest
            // until that has happened.
            if (pointBlank && !foe.Paralyzed && !foe.Frozen)
            {
                if (TryBeginFoeCast(bot, foe,
                        "Server.Spells.First.MagicArrowSpell", 1.75, pointBlank))
                {
                    return;
                }

                // No mana even for that — kite while the pool refills.
                _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(2.0);
                return;
            }

            // Utility: a foe closing into the standoff band means
            // interrupted casts and point-blank trades. The classic mage
            // answer is Paralyze — freeze it, kite back out, resume the
            // barrage. 5th circle (14 mana), so skilled mages only, and
            // never wasted on a foe that's already held.
            if (magery >= 65.0 && bot.Mana >= 14 && !foe.Paralyzed &&
                foeDist <= StandoffMin + 2 &&
                Utility.RandomDouble() < 0.35 &&
                TryBeginFoeCast(bot, foe, "Server.Spells.Fifth.ParalyzeSpell", 2.5, pointBlank))
            {
                return;
            }

            // Utility: an unpoisoned foe occasionally gets a Poison instead
            // of another damage spell — variety AND damage-over-time.
            if (magery >= 40.0 && bot.Mana >= 9 && !foe.Poisoned &&
                Utility.RandomDouble() < 0.15 &&
                TryBeginFoeCast(bot, foe, "Server.Spells.Third.PoisonSpell", 2.25, pointBlank))
            {
                return;
            }

            // A foe that cannot move or swing interrupts nothing, so the
            // gap rule is suspended against it. That is the entire point of
            // opening with Paralyze.
            bool held = foe.Paralyzed || foe.Frozen;

            // Build the eligible pool: skilled enough, can afford it, AND
            // standing far enough back to finish the words.
            Span<int> eligible = stackalloc int[AttackSpellBook.Length];
            int count = 0;
            for (int i = 0; i < AttackSpellBook.Length; i++)
            {
                if (magery >= AttackSpellBook[i].minMagery &&
                    bot.Mana >= AttackSpellBook[i].mana &&
                    (held || foeDist >= AttackSpellBook[i].minGap))
                {
                    eligible[count++] = i;
                }
            }

            // Magic Arrow is the floor, not a choice. It survives a scrum
            // and it is all a novice has, but once anything better is
            // castable from here it leaves the pool — otherwise the
            // weighted roll lands on it often enough that a Grandmaster
            // spends half a fight plinking arrows at a daemon.
            if (count > 1 && eligible[0] == 0)
            {
                for (int i = 1; i < count; i++)
                {
                    eligible[i - 1] = eligible[i];
                }
                count--;
            }

            if (count == 0)
            {
                // Out of mana for anything — keep kiting while it regens.
                _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(2.0);
                return;
            }

            // Keep only the strongest AttackPoolDepth entries, then pick
            // by weight — variety among a mage's BEST spells, not the
            // whole book (a GM plinking Magic Arrows looks wrong).
            int start = count > AttackPoolDepth ? count - AttackPoolDepth : 0;

            int totalWeight = 0;
            for (int i = start; i < count; i++)
            {
                totalWeight += AttackSpellBook[eligible[i]].weight;
            }

            int roll = Utility.Random(totalWeight);
            int picked = eligible[count - 1];
            for (int i = start; i < count; i++)
            {
                roll -= AttackSpellBook[eligible[i]].weight;
                if (roll < 0)
                {
                    picked = eligible[i];
                    break;
                }
            }

            if (TryBeginFoeCast(bot, foe,
                    AttackSpellBook[picked].type, AttackSpellBook[picked].cd, pointBlank))
            {
                return;
            }

            // Build mismatch on the picked spell — step DOWN through the
            // rest of the eligible book rather than not casting at all.
            for (int i = count - 1; i >= 0; i--)
            {
                int idx = eligible[i];
                if (idx == picked) continue;
                if (TryBeginFoeCast(bot, foe,
                        AttackSpellBook[idx].type, AttackSpellBook[idx].cd, pointBlank))
                {
                    return;
                }
            }

            // Nothing resolved — skip, no crash.
            _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(3.0);
        }

        // Launch one foe-targeted cast and record tracking state. Returns
        // false (with nothing recorded) if the spell type can't be built
        // on this build or the engine refused the cast.
        private bool TryBeginFoeCast(
            PlayerBot bot, Mobile foe, string spellType, double cooldownSeconds, bool pointBlank)
        {
            var spell = CreateSpell(spellType, bot);
            if (spell == null) return false;

            var face = bot.GetDirectionTo(foe);
            if (bot.Direction != face) bot.Direction = face;

            try
            {
                if (!spell.Cast()) return false;
            }
            catch
            {
                return false;
            }

            if (CombatDebug)
            {
                int dot = spellType.LastIndexOf('.');
                var shortName = spellType.Substring(dot + 1);
                Console.WriteLine(
                    $"[Bot {bot.Name}] casting {shortName} at '{foe.Name}' " +
                    $"[why={_castReason} d={TileDist(bot, foe.Location)}]");
            }

            // Cast launched — record tracking. The fast loop takes over:
            // it will invoke the target cursor when it appears and set the
            // cooldown on resolution.
            _castInProgress = true;
            _castStartedAt  = Core.Now;
            _castCooldown   = cooldownSeconds;
            _castPointBlank = pointBlank;
            _castSelfTarget = false;
            return true;
        }

        // -------------------------------------------------------------------
        // Bard arts — Provocation and Peacemaking, ANYWHERE. The era's
        // bard: a bot with real Provocation redirects its own attacker
        // onto the nearest other monster (and slips out of the fight), or
        // sets an idle pair brawling for the loot when it's out hunting.
        // One with Peacemaking calms an attacker it can't redirect — the
        // defensive tune. Skill-checked with real instrument sounds; the
        // crawler subclass inherits all of it, so it works roadside, at
        // the graveyard, and five floors down alike.
        // -------------------------------------------------------------------
        protected const double BardSkillMin = 60.0;
        private DateTime _nextBardArtAt = DateTime.MinValue;

        // A working bard never leaves home without an instrument.
        protected static void EnsureInstrument(PlayerBot bot)
        {
            if ((bot.Skills[SkillName.Provocation].Base >= BardSkillMin ||
                 bot.Skills[SkillName.Peacemaking].Base >= BardSkillMin) &&
                bot.Backpack != null &&
                bot.Backpack.FindItemByType(typeof(BaseInstrument)) == null)
            {
                bot.Backpack.DropItem(new Lute());
            }
        }

        // Something worth pointing a tune at: a genuine AGGRESSIVE
        // monster — never a vendor, townsperson, grazing animal, pet or
        // summon (the first live test provoked a giant rat onto the
        // town weaver). Own attackers bypass this — whatever's already
        // biting the bard is fair game to redirect.
        private static bool ProvokeTarget(BaseCreature bc) =>
            bc.Alive && !bc.Deleted && !bc.Controlled && !bc.Summoned &&
            bc is not Server.Mobiles.BaseVendor &&
            bc.FightMode is FightMode.Closest or FightMode.Weakest
                         or FightMode.Strongest or FightMode.Evil &&
            bc.Combatant is not BaseCreature;

        protected void TryBardArts(PlayerBot bot)
        {
            double prov  = bot.Skills[SkillName.Provocation].Base;
            double peace = bot.Skills[SkillName.Peacemaking].Base;
            if ((prov < BardSkillMin && peace < BardSkillMin) ||
                Core.Now < _nextBardArtAt)
            {
                return;
            }

            var attacker = bot.Combatant as BaseCreature;
            if (attacker != null && (!attacker.Alive || attacker.Deleted ||
                                     attacker.Controlled))
            {
                attacker = null;
            }

            // Provocation first — turning a fight into loot beats merely
            // stopping it.
            if (prov >= BardSkillMin)
            {
                var a = attacker;
                if (a == null && WantsFreshFights)
                {
                    // Nothing on us — set an idle pair brawling (the
                    // farming trick). Only while out LOOKING for fights;
                    // a defender or a bot climbing out doesn't stir pots.
                    foreach (var m in bot.GetMobilesInRange(8))
                    {
                        if (m is BaseCreature bc && ProvokeTarget(bc))
                        {
                            a = bc;
                            break;
                        }
                    }
                }
                if (a != null)
                {
                    BaseCreature b = null;
                    foreach (var m in a.GetMobilesInRange(8))
                    {
                        if (m is BaseCreature bc && bc != a && ProvokeTarget(bc))
                        {
                            b = bc;
                            break;
                        }
                    }
                    if (b != null)
                    {
                        _nextBardArtAt = Core.Now +
                            TimeSpan.FromSeconds(Utility.RandomMinMax(8, 15));
                        var lute = bot.Backpack?.FindItemByType(
                            typeof(BaseInstrument)) as BaseInstrument;

                        // Skill check — a GM lands it nearly every time.
                        if (Utility.RandomDouble() * 100.0 > prov)
                        {
                            lute?.PlayInstrumentBadly(bot);
                            return; // sour note — the pair ignores it
                        }
                        lute?.PlayInstrumentWell(bot);
                        a.Combatant = b;
                        b.Combatant = a;
                        if (bot.Combatant == a)
                        {
                            bot.Combatant = null; // slip out while they brawl
                        }
                        Console.WriteLine(
                            $"[Bard] {bot.Name}: provoked {a.Name} onto {b.Name}");
                        return;
                    }
                }
            }

            // Peacemaking — nothing to redirect onto; calm the attacker
            // down instead. Its real era use: the defensive tune.
            if (peace >= BardSkillMin && attacker != null)
            {
                _nextBardArtAt = Core.Now +
                    TimeSpan.FromSeconds(Utility.RandomMinMax(8, 15));
                var lute = bot.Backpack?.FindItemByType(
                    typeof(BaseInstrument)) as BaseInstrument;

                if (Utility.RandomDouble() * 100.0 > peace)
                {
                    lute?.PlayInstrumentBadly(bot);
                    return;
                }
                lute?.PlayInstrumentWell(bot);
                double secs = 4.0 + peace / 10.0; // GM ≈ 14s of calm
                attacker.Pacify(bot, Core.Now + TimeSpan.FromSeconds(secs));
                if (bot.Combatant == attacker)
                {
                    bot.Combatant = null;
                }
                Console.WriteLine(
                    $"[Bard] {bot.Name}: peaced {attacker.Name} off " +
                    $"({(int)secs}s of calm)");
            }
        }

        // -------------------------------------------------------------------
        // RearmTankWeapon — put the tank mage's weapon back in hand.
        //
        // Every cast unequips the weapon into the backpack (pre-AOS
        // ClearHands), exactly as it did to real T2A players — who macroed
        // the re-equip. This is that macro. Never fires mid-cast: equipping
        // would disturb the bot's own spell.
        // -------------------------------------------------------------------
        private void RearmTankWeapon(PlayerBot bot)
        {
            if (_castInProgress || bot.Spell != null) return;

            if (bot.FindItemOnLayer(Layer.TwoHanded) is BaseWeapon ||
                bot.FindItemOnLayer(Layer.OneHanded) is BaseWeapon)
            {
                return; // already armed
            }

            var pack = bot.Backpack;
            if (pack == null) return;

            foreach (var item in pack.Items)
            {
                if (item is BaseWeapon w && w.Skill == _tankSkill)
                {
                    bot.EquipItem(w);
                    return;
                }
            }
        }

        // -------------------------------------------------------------------
        // BeginSelfCast — START a self-targeted support spell (heal/cure).
        //
        // Same launch-and-track model as BeginCast, but the spell targets
        // the BOT ITSELF. No LOS check (a caster always sees itself) and
        // no foe. The fast loop invokes the target cursor on the bot.
        //
        // spellType is a fully-qualified ModernUO spell class name; the
        // call fails gracefully (no crash, brief cooldown) if it can't be
        // resolved for this build.
        // -------------------------------------------------------------------
        private void BeginSelfCast(PlayerBot bot, string spellType, double cooldownSeconds)
        {
            // Don't stack — engine already has a spell, or we think we do.
            if (bot.Spell != null || _castInProgress) return;

            var spell = CreateSpell(spellType, bot);
            if (spell == null)
            {
                // Spell type not found for this build — skip, no crash.
                _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(2.0);
                return;
            }

            try
            {
                // Cast() returning false means the engine refused the
                // launch (no mana, can't concentrate...). Tracking a cast
                // that never started left _castInProgress pointing at
                // nothing until the fail path noticed a tick later.
                if (!spell.Cast())
                {
                    _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(1.0);
                    return;
                }
            }
            catch
            {
                _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(2.0);
                return;
            }

            // Cast launched — record tracking. _castSelfTarget tells the
            // fast loop to invoke the cursor on the bot, not the foe.
            _castInProgress = true;
            _castStartedAt  = Core.Now;
            _castCooldown   = cooldownSeconds;
            _castPointBlank = false;
            _castSelfTarget = true;
        }

        // -------------------------------------------------------------------
        // TrySelfCare — if the mage is poisoned or hurt, cast a support
        // spell on itself instead of attacking. Returns true if a self-cast
        // was started (caller should then skip its attack cast this tick).
        //
        // Priority: CURE first (poison drains HP continuously, and it also
        // makes healing less effective in many rulesets), then HEAL.
        //   - Poisoned                   -> Cure        (2nd circle)
        //   - HP below ~40% + Magery 65+ -> Greater Heal (4th circle)
        //   - HP below ~65%              -> Heal         (1st circle)
        // Greater Heal needs real skill; a low-skill mage just uses Heal.
        // -------------------------------------------------------------------
        // inSwingRange: something is close enough to hit the bot while it
        // casts. Pre-AOS that disturbs anything above first circle
        // silently, so Cure (2nd) and Greater Heal (4th) are dead casts
        // there — the bot reaches for the potion belt instead, and keeps
        // only first-circle Heal, which the engine's disturb rule spares.
        private bool TrySelfCare(PlayerBot bot, bool inSwingRange = false)
        {
            if (!SpellcasterMode) return false;
            if (Core.Now < _nextCastAllowed) return false;
            if (bot.Spell != null || _castInProgress) return false;

            // CURE — poison ticks HP down and is worth clearing first.
            // Mana-gated (Cure is 2nd circle, 6 mana): an OOM mage burns
            // no cast attempt and reaches for a cure potion instead.
            if (bot.Poisoned)
            {
                if (bot.Mana >= 6 && !inSwingRange)
                {
                    BeginSelfCast(bot, "Server.Spells.Second.CureSpell", 2.0);
                    if (_castInProgress) return true;
                }
                if (DrinkCurePotion(bot))
                {
                    _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(2.0);
                    return true;
                }
            }

            // HEAL — scale the spell to the wound, the bot's skill, and
            // what the mana pool can actually pay for.
            double hpFraction = bot.HitsMax > 0
                ? (double)bot.Hits / bot.HitsMax
                : 1.0;
            double magery = bot.Skills[SkillName.Magery].Base;

            if (hpFraction < 0.40 && magery >= 65.0 && bot.Mana >= 11 && !inSwingRange)
            {
                // Badly hurt and skilled enough — Greater Heal (4th, 11 mana).
                BeginSelfCast(bot, "Server.Spells.Fourth.GreaterHealSpell", 2.5);
                return _castInProgress;
            }
            if (hpFraction < 0.65 && bot.Mana >= 4)
            {
                // Hurt — a basic Heal (1st, 4 mana). Also the fallback when
                // Greater Heal was wanted but skill or mana fell short.
                BeginSelfCast(bot, "Server.Spells.First.HealSpell", 2.0);
                return _castInProgress;
            }

            // Badly hurt with an empty pool — potions don't need mana.
            if (hpFraction < 0.45 && DrinkHealPotion(bot))
            {
                _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(2.0);
                return true;
            }

            return false;
        }

        // -------------------------------------------------------------------
        // TryMeleeSelfHeal — a melee fighter uses bandages and health
        // potions on itself when hurt. Mages heal with spells (TrySelfCare);
        // this is the non-caster equivalent.
        //
        // Cooldown-gated via _nextCastAllowed (shared gate — a bot isn't
        // doing two heal actions at once anyway). Item use goes through
        // reflection so a build-specific API difference fails gracefully
        // (no heal) rather than breaking the build.
        //
        // Priority when hurt:
        //   Poisoned  -> drink a Cure Potion (poison keeps ticking, and a
        //                pre-AOS bandage on a poisoned patient slips into
        //                a cure attempt instead of healing)
        //   HP < 45%  -> drink a Heal Potion (instant) AND start a bandage
        //   HP < 70%  -> start a bandage (bandages are the steady heal) —
        //                but never while one is already running: BeginHeal
        //                cancels a running context, and the old restart-
        //                every-2s loop reset the ~10s timer forever
        // -------------------------------------------------------------------
        private void TryMeleeSelfHeal(PlayerBot bot)
        {
            if (SpellcasterMode) return;                 // mages use spells
            if (Core.Now < _nextCastAllowed) return;     // shared heal gate
            if (bot.Backpack == null) return;

            double hpFraction = bot.HitsMax > 0
                ? (double)bot.Hits / bot.HitsMax
                : 1.0;
            bool poisoned = bot.Poisoned;
            if (hpFraction >= 0.70 && !poisoned) return; // not hurt enough

            bool didSomething = false;

            // Poison first — it ticks HP down until cleared.
            if (poisoned && DrinkCurePotion(bot)) didSomething = true;

            // Badly hurt — drink a health potion for an instant top-up.
            if (hpFraction < 0.45)
            {
                if (DrinkHealPotion(bot)) didSomething = true;
            }

            // Start a bandage — the bread-and-butter heal-over-time.
            if (hpFraction < 0.70 && Core.Now >= _bandageBusyUntil &&
                StartBandageSelf(bot))
            {
                // Mirror the engine's pre-AOS self-heal timing (9.4s +
                // 0.6s per 10 dex under 120) with a half-second margin, so
                // the window covers slow low-dex bandagers and doesn't
                // idle fast ones.
                _bandageBusyUntil = Core.Now +
                    TimeSpan.FromSeconds(9.9 + 0.06 * (120 - bot.Dex));
                didSomething = true;
            }

            if (didSomething)
            {
                // Brief gate so the bot doesn't spam every fast-loop tick.
                _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(2.0);
            }
        }

        private static readonly string[] HealPotionTypes =
        {
            "Server.Items.GreaterHealPotion",
            "Server.Items.HealPotion",
            "Server.Items.LesserHealPotion",
        };

        private static readonly string[] CurePotionTypes =
        {
            "Server.Items.GreaterCurePotion",
            "Server.Items.CurePotion",
            "Server.Items.LesserCurePotion",
        };

        // Internal: PKBehavior shares these combat-care helpers.
        internal static bool DrinkHealPotion(PlayerBot bot) =>
            DrinkPotion(bot, HealPotionTypes);

        internal static bool DrinkCurePotion(PlayerBot bot) =>
            DrinkPotion(bot, CurePotionTypes);

        // Find the first potion of the given types in the pack and drink
        // it (strongest listed first). Returns true if one was drunk.
        // Reflection-based: calls the potion's Drink(Mobile) method without
        // a hard compile dependency.
        private static bool DrinkPotion(PlayerBot bot, string[] potionTypes)
        {
            if (bot.Backpack == null) return false;
            foreach (var typeName in potionTypes)
            {
                var t = FindType(typeName);
                if (t == null) continue;
                var potion = bot.Backpack.FindItemByType(t);
                if (potion == null) continue;

                try
                {
                    var drink = t.GetMethod("Drink",
                        new[] { typeof(Mobile) });
                    if (drink != null)
                    {
                        drink.Invoke(potion, new object[] { bot });
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        // Begin a bandage self-heal. Returns true if a bandage was applied.
        // ModernUO's Bandage uses a BandageContext.BeginHeal(healer,
        // patient) static. Reflection keeps this from being a hard build
        // dependency in case the API differs.
        internal static bool StartBandageSelf(PlayerBot bot)
        {
            var bandageType = FindType("Server.Items.Bandage");
            if (bandageType == null) return false;
            var bandage = bot.Backpack.FindItemByType(bandageType);
            if (bandage == null) return false;

            var ctxType = FindType("Server.Items.BandageContext");
            if (ctxType == null) return false;

            try
            {
                var begin = ctxType.GetMethod("BeginHeal",
                    new[] { typeof(Mobile), typeof(Mobile) });
                if (begin != null)
                {
                    var ctx = begin.Invoke(null, new object[] { bot, bot });
                    if (ctx != null)
                    {
                        // Consume one bandage from the stack.
                        bandage.Consume(1);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // Construct a Spell by fully-qualified type name via reflection.
        // Returns null if the type isn't found or has no (Mobile, Item)
        // constructor — caller handles the null gracefully.
        // Resolve a Type by fully-qualified name across all loaded
        // assemblies. Returns null if not found — every caller handles
        // null gracefully, so an unknown type name never crashes or
        // breaks the build.
        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        internal static Server.Spells.Spell CreateSpell(string typeName, PlayerBot caster)
        {
            try
            {
                Type t = FindType(typeName);
                if (t == null) return null;

                // Spell ctors are (Mobile caster, Item scroll).
                var ctor = t.GetConstructor(new[] { typeof(Mobile), typeof(Item) });
                if (ctor != null)
                    return ctor.Invoke(new object[] { caster, null }) as Server.Spells.Spell;

                return null;
            }
            catch
            {
                return null;
            }
        }

        // Pick a tile adjacent to the foe for this bot to attack from.
        // Prefers the open tile closest to the bot's current position;
        // skips tiles occupied by other mobiles so bots spread around the
        // monster instead of stacking. Falls back to the foe's own tile if
        // somehow nothing is free.
        // Tile distance between a bot and a point, using the standard UO
        // metric (Chebyshev — max of the X and Y deltas). This is the same
        // metric Mobile.InRange uses, so "dist <= N" here agrees with
        // "InRange(loc, N)". Computed directly because Mobile has no
        // GetDistanceToSqrt method (an earlier version called that — it
        // doesn't exist, which is why range checks were wrong).
        private static int TileDist(Mobile m, Point3D p) => TileDist(m.Location, p);

        private static int TileDist(Point3D a, Point3D b)
        {
            int dx = a.X - b.X; if (dx < 0) dx = -dx;
            int dy = a.Y - b.Y; if (dy < 0) dy = -dy;
            return dx > dy ? dx : dy;
        }

        // `avoid`, when given, is the centre of the pack the foe belongs
        // to. The slot is then chosen on the FAR side of the foe from its
        // friends, so a melee bot walks around a group to reach its edge
        // instead of taking the shortest line straight through the middle
        // of it. Nearest-to-the-bot stays the tiebreak.
        private static Point3D PickAttackSlot(PlayerBot bot, Mobile foe, Point3D? avoid = null)
        {
            // 8 tiles around the foe.
            int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
            int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

            Point3D best = foe.Location;
            int bestScore = int.MinValue;
            bool found = false;

            for (int i = 0; i < 8; i++)
            {
                int tx = foe.X + dx[i];
                int ty = foe.Y + dy[i];

                // A slot inside a wall/decor sends the bot A*-ing at a tile
                // it can never stand on — the stuck ladder then grinds and
                // repicks. Only offer tiles the engine would accept. Probe
                // at the FOE's Z first (adjacent tiles share its floor —
                // this is what works on statics dungeon floors, where the
                // land Z underneath is solid rock); land-average Z second
                // for sloped surface terrain.
                int tz = foe.Z;
                if (!foe.Map.CanSpawnMobile(tx, ty, tz))
                {
                    tz = foe.Map.GetAverageZ(tx, ty);
                    if (!foe.Map.CanSpawnMobile(tx, ty, tz))
                    {
                        continue;
                    }
                }

                // Is this tile occupied by another mobile? If so, skip —
                // unless it's us (we might already be standing in a slot).
                bool occupied = false;
                foreach (var m in foe.Map.GetMobilesInRange(
                             new Point3D(tx, ty, tz), 0))
                {
                    if (m != bot && !m.Deleted && m.Alive)
                    {
                        occupied = true;
                        break;
                    }
                }
                if (occupied) continue;

                int ddx = bot.X - tx;
                int ddy = bot.Y - ty;
                int distSq = ddx * ddx + ddy * ddy;

                // Score: further from the pack is worth much more than
                // being a step closer to walk to. Without `avoid` this is
                // exactly the old nearest-slot rule.
                int slotScore = -distSq;
                if (avoid.HasValue)
                {
                    slotScore += TileDist(new Point3D(tx, ty, tz), avoid.Value) * 64;
                }

                if (slotScore > bestScore)
                {
                    bestScore = slotScore;
                    best = new Point3D(tx, ty, tz);
                    found = true;
                }
            }

            return found ? best : foe.Location;
        }

        private void SetGoal(PlayerBot bot, Point3D goal, bool running)
        {
            _chaseFoe = null;  // PathFollower mode, not greedy chase
            _rangedFoe = null;
            if (_goal != goal || _follower == null)
            {
                _goal = goal;
                _follower = LegFollowers.Create(bot, goal, ZoneWalkOverride(bot, goal));
            }
            EnsureStepTimer(bot, running);
        }

        // Line-of-sight check. A ranged bot must not engage or cast at a
        // foe it can't actually see — spells fizzle their own LOS check at
        // resolution (CheckHSequence), which looks like "the mage randomly
        // doesn't attack", and a mage will otherwise try to cast through
        // walls. Wrapped so a build/API quirk fails OPEN (assume visible)
        // rather than freezing all combat.
        private static bool HasLOS(PlayerBot bot, Mobile target)
        {
            try { return bot.InLOS(target); }
            catch { return true; }
        }

        // -------------------------------------------------------------------
        // FindNearbyEnemy
        //
        // Only attack ACTUAL hostile monsters. The naive filter
        // (AlwaysAttackable OR FightMode != None) included town guards,
        // vendors, healers, etc — all of whom share those flags.
        //
        // Base filter:
        //   - Skip controlled pets and summons.
        //   - Skip anything with Karma >= 0. Monsters in UO have very
        //     negative karma (-1000 to -10000); wildlife is 0 or slightly
        //     positive; guards/NPCs are positive.
        //   - Require FightMode != None as a final sanity check.
        //
        // On top of that, candidates are SCORED rather than nearest-first:
        //   - A foe attacking ME dominates everything (self-defense).
        //   - A foe attacking a FRIENDLY BOT ranks next (assist) — and is
        //     noticed out to AssistRange, beyond normal sight, so bots
        //     rush into a nearby friend's fight.
        //   - Visible-with-LOS beats behind-a-wall; then nearer beats far.
        //
        // Threat gate: a foe tougher than the bot's dare ceiling (see
        // TierDare/EffectiveDare) is never PICKED as a fresh fight. If it's
        // already attacking the bot it stays a valid target — the bot
        // fights back up to ~1.5x its ceiling; past that `overwhelming`
        // comes back true and the caller should flee, not engage.
        //
        // Note: bots WILL engage monsters that invade guarded zones. The
        // Karma filter handles this — zombies/orcs/etc are Karma < 0, so
        // an Adventurer who finds themselves in a city under attack will
        // defend it. This is intentional for "town gets invaded" events.
        // -------------------------------------------------------------------
        private Mobile FindNearbyEnemy(PlayerBot bot, out bool overwhelming)
        {
            overwhelming = false;

            // A murderer or a gray with a blade in one of us outranks every
            // monster in the room. This scan is monsters only below, and
            // BotGrayWatch's sweep is the only thing that ever handed a bot
            // a person, so a red could open up on a crawler mid-fight and
            // the crawler would finish the skeleton first, or never look
            // up at all.
            var person = FindPersonAttacking(bot, out overwhelming);
            if (person != null)
            {
                _targetKnot = default;
                return person;
            }

            Mobile best = null;
            int bestScore = int.MinValue;
            bool bestOverwhelming = false;

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, AssistRange))
            {
                if (m == bot || m.Deleted || !m.Alive) continue;
                if (m is not BaseCreature bc) continue;

                // Skip players' pets and summoned creatures.
                if (bc.ControlMaster != null || bc.Summoned) continue;

                // Skip foes recently proven unreachable (e.g. a rat sealed
                // inside a building) so the bot doesn't relock the wall.
                if (bot.IsUnreachable(bc)) continue;

                // Skip anything that isn't actually hostile.
                if (bc.FightMode == FightMode.None) continue;

                // Karma test: real monsters are deeply negative. NPCs
                // (guards, vendors, town criers, healers) are neutral or
                // positive.
                if (bc.Karma >= 0) continue;

                int dist = TileDist(bot, bc.Location);
                bool attackingMe     = bc.Combatant == bot;
                bool attackingFriend = !attackingMe &&
                    (bc.Combatant is PlayerBot ||
                     (bc.Combatant is Mobile fcm && IsPartyFriend(bot, fcm)));

                // Beyond the bot's own sight a monster only registers when
                // it's in a fight with the bot or a friendly bot.
                if (dist > SightRange && !attackingMe && !attackingFriend)
                {
                    continue;
                }

                // The bot just ran from a pack standing here that it could
                // not have taken apart. Starting a fresh fight next to it
                // is how the run-flee-run loop began. A friend already in
                // there still gets the assist.
                if (!attackingMe && !attackingFriend &&
                    Core.Now < _avoidUntil &&
                    TileDist(bc.Location, _avoidCenter) <= PackRadius + 4)
                {
                    continue;
                }

                // Not looking for trouble (exit-mode crawler): only foes
                // already in a fight with us or a friend register.
                if (!WantsFreshFights && !attackingMe && !attackingFriend)
                {
                    continue;
                }

                // Defenders don't go looking for trouble — no rushing off
                // to distant friends' fights; they deal with what's here
                // and get back on the road.
                if (DefenderMode && attackingFriend && dist > SightRange)
                {
                    continue;
                }

                // Threat gate. A foe already on us stays targetable
                // regardless — you don't get to decline a fight that has
                // already started.
                //
                // For a FRESH fight the question is not "can I beat this
                // orc", it is "can I beat this orc AND whatever is standing
                // with it", because walking up to one of a group aggroes
                // the group. So the gate weighs the whole knot around the
                // candidate, not the candidate alone. This is the thing
                // that used to send bots into the middle of a camp.
                int dare = EffectiveDare(bot, bc);
                var knot = attackingMe
                    ? default
                    : Survey(bot, bc.Location, PackRadius);

                if (!attackingMe)
                {
                    // Too much monster in one place — for THIS bot. A bold
                    // one starts fights a careful one walks away from, and
                    // sometimes that is the last decision it makes.
                    if (knot.Weight > dare * _nerve)
                    {
                        continue;
                    }
                    // Taking on something a level-headed bot would have
                    // walked away from. Logged so a monster death can be
                    // traced back to the decision that caused it.
                    if (CombatDebug && knot.Weight > dare)
                    {
                        Console.WriteLine(
                            $"[Bot {bot.Name}] BOLD engage '{bc.Name}' — knot " +
                            $"{knot.Count} weighing {knot.Weight} vs dare {dare} " +
                            $"(nerve {_nerve:0.00})");
                    }
                    // Too MANY of them, whatever they weigh. What kills a
                    // bot is the number of things swinging at it.
                    if (knot.Count > EngagePackLimit(bot))
                    {
                        continue;
                    }
                }

                int score = 0;
                if (attackingMe)
                {
                    score += 1000;
                }
                if (attackingFriend)
                {
                    score += 400;
                }
                // Thin the herd: given the choice, take the straggler.
                // Every extra monster standing with this one is a reason to
                // pick a different one, which over a fight peels a group
                // apart from the edges instead of engaging its middle.
                if (!attackingMe && knot.Count > 1)
                {
                    score -= (knot.Count - 1) * 150;
                }
                // A foe the bot can SEE beats one it can't — engaging a
                // target behind a wall means walking up and casting at it,
                // and the spell fizzles its own LOS check.
                if (HasLOS(bot, bc))
                {
                    score += 200;
                }
                score += (AssistRange - dist) * 10;

                if (score > bestScore)
                {
                    best = bc;
                    bestScore = score;
                    _targetKnot = knot;
                    // Fighting back is right up to ~1.5x the ceiling;
                    // beyond that the right move is to run.
                    bestOverwhelming = attackingMe && bc.HitsMax > dare * 3 / 2;
                }
            }

            overwhelming = bestOverwhelming;
            return best;
        }

        // A player-shaped attacker who is fair game: a murderer, or anybody
        // who flagged gray by starting on us or a friend. Nearest first.
        // Only ones already IN the fight: choosing to start on a red who is
        // minding his own business is BotGrayWatch's call, with its
        // willingness roll and its cap on how many pile on.
        private Mobile FindPersonAttacking(PlayerBot bot, out bool overwhelming)
        {
            overwhelming = false;
            Mobile best = null;
            int bestDist = int.MaxValue;

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, AssistRange))
            {
                if (m == bot || !m.Player || m.Deleted || !m.Alive) continue;
                if (m.Combatant is not Mobile victim) continue;

                bool onMe     = victim == bot;
                bool onFriend = !onMe && (victim is PlayerBot || IsPartyFriend(bot, victim));
                if (!onMe && !onFriend) continue;

                // Not a crime to hit back at a red or a gray. Anybody else
                // hitting us is a duel, a faction fight, or a guild war,
                // and those have their own systems.
                if (!BotGrayWatch.FairGame(m)) continue;
                if (!bot.CanSee(m)) continue;

                int dist = TileDist(bot, m.Location);
                if (!onMe && dist > SightRange) continue;

                // Ours first, then the closest.
                int rank = (onMe ? 0 : 100) + dist;
                if (rank < bestDist)
                {
                    bestDist = rank;
                    best = m;
                }
            }

            if (best != null)
            {
                int dare = EffectiveDare(bot, best);
                overwhelming = best.HitsMax > dare * 3 / 2;
            }
            return best;
        }

        // The knot of monsters standing with the foe FindNearbyEnemy last
        // picked. The engage branch reads it to decide whether the fight
        // needs pulling first.
        private ThreatPicture _targetKnot;

        // ---- The pull ----
        //
        // Sometimes the only foe worth taking still has company. A player
        // doesn't walk into the middle of that — they get its attention and
        // back off, and the one that follows gets fought on its own while
        // the rest lose interest. That is the whole technique for taking a
        // group apart, and it's what these two fields do: while the window
        // is open the bot walks away from the knot instead of into it.
        //
        // Bounded, because a bot that pulls forever never fights. It ends
        // early the moment nothing but the target is still near.
        private static readonly TimeSpan PullWindow = TimeSpan.FromSeconds(5);
        private DateTime _pullUntil = DateTime.MinValue;
        private Point3D  _pullFrom;

        // Far enough off the knot to fight the one that followed. Ends the
        // pull early — without it a bot walks backwards for the whole
        // window even after it has already got what it wanted.
        private const int PullClearDistance = 8;

        // ---- NERVE ----
        //
        // How much trouble THIS bot takes on, and how long it stays in it.
        // Every safety gate below is scaled by it.
        //
        // A shard where every bot reads the odds perfectly is its own kind
        // of wrong: nothing ever dies to a monster, which is not what a
        // world full of people looks like. Some players are careful and
        // some are cocky, and the cocky ones get killed now and then —
        // that is where a shard's monster deaths come from, and why the
        // careful ones being alive means anything.
        //
        // Brave over-reaches and holds too long. Cautious leaves while it
        // still can. Everyone else gets a small roll of their own, so no
        // two bots draw the line in quite the same place.
        private double _nerve = 1.0;

        private void RollNerve(PlayerBot bot)
        {
            double nerve = 1.0;
            if (bot.Personality.HasTrait(PersonalityTrait.Brave))    nerve += 0.65;
            if (bot.Personality.HasTrait(PersonalityTrait.Cautious)) nerve -= 0.25;
            nerve += Utility.RandomDouble() * 0.40 - 0.20;
            _nerve = Math.Clamp(nerve, 0.60, 2.00);
        }

        // The pack size this bot will wade into. A bold one takes on one
        // more than its tier says it should.
        private int EngagePackLimit(PlayerBot bot) =>
            MaxEngagePack(bot) + (_nerve >= 1.25 ? 1 : 0);

        // Currently riding a fight out instead of retreating. Tracked only
        // so the decision is logged once rather than every fast tick.
        private bool _gambling;

        // Fit to START a fight?
        //
        // The retreat threshold is the line below which this bot LEAVES a
        // fight. Nothing checked it before picking a NEW one, and only the
        // in-progress fight ever consulted it — so a bot that escaped a
        // scorpion at 7 hit points turned straight round on the next
        // decision tick and engaged the same scorpion, because a foe
        // already attacking it skips the threat gate entirely. It did that
        // until it died, resurrected, and did it again. Every monster
        // death in the soak was this loop, not a bot misjudging a fight.
        // `foes` is how many will be swinging once the fight starts. The
        // line a bot RUNS at rises with every attacker past the first (see
        // CheckRetreat), and this used to test the bare line, so a bot at
        // 48 of 82 was fit to start on a pair of skeletons and was back
        // over the two-attacker line and running the moment it did. Six
        // times in a row, never a swing that mattered. Fit to start means
        // fit to start THAT fight, with some room to fight it.
        private bool TooHurtToStart(PlayerBot bot, int foes = 1)
        {
            if (bot.HitsMax <= 0) return false;
            double fitAt = (DefenderMode
                ? DefenderRetreatHpFraction
                : RetreatHpFraction) / _nerve;
            int extra = foes - 1;
            if (extra > 0)
            {
                fitAt = Math.Min(0.95, fitAt + 0.10 * Math.Min(4, extra));
            }
            // Starting exactly on the line means running at the first hit.
            fitAt = Math.Min(0.95, fitAt + 0.08);
            return bot.Hits < bot.HitsMax * fitAt;
        }

        // ---- Withdrawing from a bad room ----
        // Close enough to matter, and rare enough that a bot which cannot
        // get clear stops trying and goes back to being useful.
        private const int WithdrawRange = 5;
        private static readonly TimeSpan WithdrawCooldown = TimeSpan.FromSeconds(30);
        private DateTime _nextWithdrawAt = DateTime.MinValue;

        // Effective dare ceiling for this bot against this foe: the tier's
        // base ceiling, raised 60% for every friendly bot already fighting
        // the foe (bravery in numbers — crowds swarm what no one bot would
        // touch alone).
        private static int EffectiveDare(PlayerBot bot, Mobile foe)
        {
            int rank = BotSkillTierHelper.Rank(bot.SkillTier);
            if (rank < 0)
            {
                rank = 0;
            }
            else if (rank >= TierDare.Length)
            {
                rank = TierDare.Length - 1;
            }
            int dare = TierDare[rank];

            int allies = 0;
            foreach (var m in foe.Map.GetMobilesInRange(foe.Location, 8))
            {
                if (m != bot && m is PlayerBot friend && friend.Alive &&
                    friend.Combatant == foe)
                {
                    allies++;
                }
            }

            return dare + (int)(dare * 0.6 * allies);
        }

        // -------------------------------------------------------------------
        // AREA THREAT SURVEY
        //
        // A bot used to size up ONE monster and then walk to it. What
        // killed it was everything standing NEXT to that monster: the dare
        // gate passed on a lone orc's 60 hit points and the bot ran into
        // six of them.
        //
        // This reads the whole neighbourhood instead, the way a player
        // reads a room at a glance: how many hostiles, what they weigh
        // together, how many are already on me, how close the nearest one
        // is, and where the mass of them sits so I know which way is out.
        // -------------------------------------------------------------------
        private struct ThreatPicture
        {
            public int     Count;        // hostile monsters seen
            public int     Weight;       // summed HitsMax - the pack's heft
            public int     Attackers;    // how many are actually on the bot
            public int     CloseAttackers; // of those, the ones in swing range
            public int     CloseWeight;    // and what those close ones weigh
            public int     NearestDist;  // tiles to the closest one
            public Point3D Center;       // where the mass of them sits
            public bool    Any => Count > 0;
        }

        // What counts as a monster. Same test FindNearbyEnemy picks targets
        // by, pulled out so the survey and the target picker can never
        // disagree about what is and isn't a threat.
        private static bool IsHostileMonster(Mobile m) =>
            m is BaseCreature bc && !bc.Deleted && bc.Alive &&
            bc.ControlMaster == null && !bc.Summoned &&
            bc.FightMode != FightMode.None && bc.Karma < 0;

        // Survey the hostiles within `radius` of `at`. `at` is usually the
        // bot's own tile (what is on me) or a candidate foe's tile (what
        // comes WITH that foe if I start on it).
        private static ThreatPicture Survey(PlayerBot bot, Point3D at, int radius)
        {
            var p = new ThreatPicture { NearestDist = int.MaxValue, Center = at };
            if (bot.Map == null) return p;

            long sx = 0, sy = 0;
            foreach (var m in bot.Map.GetMobilesInRange(at, radius))
            {
                if (m == bot || !IsHostileMonster(m)) continue;
                var bc = (BaseCreature)m;

                p.Count++;
                p.Weight += bc.HitsMax;

                int d = TileDist(bot, bc.Location);
                if (d < p.NearestDist) p.NearestDist = d;

                // "On me" is anything that has picked the bot as its
                // target, out to the edge of sight. "Close" is the part of
                // that which can actually reach the bot this second. A
                // fighting retreat needs the second number: a pack that is
                // chasing counts entirely as attackers while it is still
                // eight tiles back.
                if (bc.Combatant == bot)
                {
                    p.Attackers++;
                    if (d <= 2)
                    {
                        p.CloseAttackers++;
                        p.CloseWeight += bc.HitsMax;
                    }
                }

                sx += bc.X;
                sy += bc.Y;
            }

            if (p.Count > 0)
            {
                p.Center = new Point3D((int)(sx / p.Count), (int)(sy / p.Count), bot.Z);
            }
            else
            {
                p.NearestDist = int.MaxValue;
            }
            return p;
        }

        // The survey around the BOT, cached for a second. CheckRetreat runs
        // on the ~300ms fast loop and a fresh sector scan every fire is
        // waste - monsters do not teleport in, and a second-old picture is
        // still a second fresher than the 2s decision tick ever was.
        private ThreatPicture _near;
        private DateTime      _nearAt = DateTime.MinValue;

        private ThreatPicture NearThreat(PlayerBot bot)
        {
            if (Core.Now >= _nearAt)
            {
                _near   = Survey(bot, bot.Location, SightRange);
                _nearAt = Core.Now + TimeSpan.FromSeconds(1.0);
            }
            return _near;
        }

        // The bot's own dare ceiling with no crowd bonus - what it can
        // answer on its own.
        private static int BaseDare(PlayerBot bot)
        {
            int rank = BotSkillTierHelper.Rank(bot.SkillTier);
            if (rank < 0) rank = 0;
            else if (rank >= TierDare.Length) rank = TierDare.Length - 1;
            return TierDare[rank];
        }

        // Friendly bots in the fight beside this one. Numbers change what a
        // room is worth standing in, both for engaging and for leaving.
        private static int FriendlyFighters(PlayerBot bot)
        {
            int n = 0;
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 8))
            {
                if (m != bot && m is PlayerBot f && f.Alive && !f.Deleted &&
                    f.Combatant != null)
                {
                    n++;
                }
            }
            return n;
        }

        // How much monster this bot can stand in the middle of before the
        // room itself is the problem, friends counted in.
        private static int RoomBudget(PlayerBot bot) =>
            BaseDare(bot) * (1 + FriendlyFighters(bot));

        // How many separate monsters a bot will take on at once, by tier.
        // Weight alone is not the whole story - eight mongbats do not weigh
        // much and will still kill an apprentice, because what hurts is the
        // number of things swinging, not the size of their hit point pools.
        private static readonly int[] TierPack =
        {
            1,   // Novice
            2,   // Apprentice
            2,   // Journeyman
            3,   // Adept
            3,   // Expert
            4,   // Master
            4,   // Grandmaster
        };

        private static int MaxEngagePack(PlayerBot bot)
        {
            int rank = BotSkillTierHelper.Rank(bot.SkillTier);
            if (rank < 0) rank = 0;
            else if (rank >= TierPack.Length) rank = TierPack.Length - 1;
            return TierPack[rank] + FriendlyFighters(bot);
        }

        // -------------------------------------------------------------------
        // ESCAPE ROUTING
        //
        // The old flee stepped one tile directly away from the single
        // monster it was running from, twice, every fast tick. Blind: it
        // backed into walls, shuffled along them, and ran straight through
        // the rest of the pack because it only ever looked at one of them.
        //
        // A fleeing bot now picks a real waypoint to run to - one further
        // from the mass of monsters than it is now, and close enough that
        // A* can reach it in a single leg - and PathFollower routes it
        // there around the walls. Arriving still in trouble chains to the
        // next node out, so the bot works its way along the road network
        // instead of into a corner. Blind sprinting stays as the fallback
        // for ground with no waypoints near it.
        // -------------------------------------------------------------------

        // How many nearby nodes to consider, and the furthest one A* will
        // reliably reach in a single leg. 30 was too greedy: a quarter of
        // the routes picked were nodes the pathfinder then could not walk
        // to, which costs the bot the two seconds it takes to notice. A
        // shorter hop it can actually make beats a longer one it can't —
        // and the chain-to-the-next-node-on-arrival covers the distance
        // anyway.
        private const int EscapeNodeSample = 10;
        private const int EscapeLegMax     = 20;

        private Point3D?     _escapeGoal;
        private PathFollower _escapeFollower;
        private int          _escapeStalls;
        private readonly List<WaypointNode> _escapeCandidates = new();

        // Escape goals the route follower could not actually reach from
        // where the bot was. Euclidean distance picks them — seventeen
        // tiles looks fine — and A* then fails across a wall or a Z change
        // and the bot stands still. One bot was handed the same failing
        // node four times in a row. A goal that stalled is skipped on the
        // next pick, so a different node gets its turn.
        private readonly HashSet<string> _stalledEscapeGoals =
            new(StringComparer.OrdinalIgnoreCase);
        private string _escapeGoalName;

        private Point3D? PickEscapeGoal(PlayerBot bot, Point3D away)
        {
            var graph = WaypointRegistry.Graph;
            if (graph == null || graph.NodeCount == 0) return null;

            _escapeCandidates.Clear();
            try { graph.FindNearestNodes(bot.Location, EscapeNodeSample, _escapeCandidates); }
            catch { return null; }
            if (_escapeCandidates.Count == 0) return null;

            int hereFromThreat = TileDist(bot.Location, away);

            WaypointNode best = null;
            int bestScore = int.MinValue;

            foreach (var n in _escapeCandidates)
            {
                int leg = TileDist(bot.Location, n.Location);
                // Too close to be worth routing to, or too far for one A*
                // leg (a failed path leaves the bot standing still, which
                // in a flee is fatal).
                if (leg < 4 || leg > EscapeLegMax) continue;
                if (_stalledEscapeGoals.Contains(n.Name)) continue;

                // It has to be genuinely further out than standing still is.
                int fromThreat = TileDist(n.Location, away);
                if (fromThreat <= hereFromThreat + 3) continue;

                // Distance from the pack is what matters; the walk there is
                // a mild tiebreak, and a junction beats a dead end because
                // the next hop out has somewhere to go.
                // Reachability is worth as much as distance here — the
                // walk being short is what makes the path succeed.
                int score = fromThreat * 3 - leg * 2 +
                            (n.Connects != null ? n.Connects.Count * 2 : 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = n;
                }
            }

            _escapeGoalName = best?.Name;
            return best?.Location;
        }

        private void SetEscapeRoute(PlayerBot bot, Point3D away)
        {
            _escapeGoal = PickEscapeGoal(bot, away);
            _escapeFollower = _escapeGoal.HasValue
                ? new PathFollower(bot, _escapeGoal.Value)
                : null;
            _escapeStalls = 0;
        }

        private void ClearEscapeRoute()
        {
            _escapeGoal = null;
            _escapeFollower = null;
            _escapeStalls = 0;
        }

        // -------------------------------------------------------------------
        // FIGHTING RETREAT
        //
        // What a player does with a pack on their heels is not "run until
        // clear, walk back, get chased again". They run until the pack
        // strings out, since monsters do not all move at one speed and do
        // not all give chase for the same distance, then turn on whichever
        // one is out in front, kill it, and run again if the rest arrive.
        // The pack gets smaller every pass.
        //
        // Whether that is on the table is decided ONCE, when the flee
        // starts: is there anything in this pack the bot could not beat by
        // itself? If there is, turning round just means dying to that one
        // with company, so the bot runs for real and remembers the spot.
        // If there is not, every flee tick looks for the moment the one in
        // front is alone.
        // -------------------------------------------------------------------

        // Turns without a kill before the bot admits the pack is not going
        // to string out, and the window over which they are counted.
        private const int PickOffMaxTurns = 4;
        private static readonly TimeSpan PickOffWindow = TimeSpan.FromSeconds(90);

        // How far ahead of the second monster the first must be. A melee
        // bot has to finish, or at least badly hurt, the leader before the
        // next one is in reach; a caster needs a cast's worth of room, and
        // a chaser a few tiles back covers that in one.
        private const int PickOffGapMelee  = 4;
        private const int PickOffGapRanged = 4;

        // How long fresh fights are declined around a pack the bot ran from.
        private static readonly TimeSpan AvoidWindow = TimeSpan.FromSeconds(45);

        // Two flees from the same thing inside this window is being hunted;
        // a hunted bot stays away from the spot for the longer window.
        private static readonly TimeSpan HuntedWindow      = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan HuntedAvoidWindow = TimeSpan.FromSeconds(120);

        // Could this bot take the pack apart one at a time? True when the
        // pack is nothing but monsters it would fight alone, and not so
        // many of them that the plan is silly. Judged against the bot's
        // OWN dare: a friend in the room may not still be there on the
        // next turn.
        private bool AssessPickOff(PlayerBot bot, in ThreatPicture near, Mobile threat)
        {
            // Something that is not a monster started this: a red, a
            // faction enemy. Turning on a straggling skeleton with a player
            // on your heels is how you die to the player.
            if (threat != null && !IsHostileMonster(threat) && threat.Alive &&
                threat.Map == bot.Map)
            {
                if (CombatDebug)
                {
                    Console.WriteLine(
                        $"[Bot {bot.Name}] true flee — '{threat.Name}' is not a monster");
                }
                return false;
            }

            if (!near.Any)
            {
                return threat == null || threat.HitsMax <= BaseDare(bot) * _nerve;
            }

            int    dare    = (int)(BaseDare(bot) * _nerve);
            int    biggest = 0;
            string worst   = null;

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, SightRange))
            {
                if (m == bot || !IsHostileMonster(m)) continue;
                if (m.HitsMax > biggest)
                {
                    biggest = m.HitsMax;
                    worst   = m.Name;
                }
            }

            // No cap worth the name on how many: a dozen rats picked off
            // one at a time is exactly the plan. Just not a whole spawn.
            bool can = biggest <= dare && near.Count <= 12;

            if (CombatDebug)
            {
                Console.WriteLine(can
                    ? $"[Bot {bot.Name}] fighting retreat — {near.Count} of them, " +
                      $"biggest '{worst}' {biggest} vs dare {dare}"
                    : $"[Bot {bot.Name}] true flee — " +
                      (biggest > dare
                          ? $"'{worst}' {biggest} is beyond dare {dare}"
                          : $"{near.Count} of them is too many"));
            }
            return can;
        }

        // Looks for the moment to turn: the nearest monster is inside
        // reach, the next one is well behind it, the bot is fit to fight
        // and the leader is something it would fight alone. Takes the turn
        // when it finds one and returns true; the fast loop then runs the
        // fight through its normal chase or ranged branch, and CheckRetreat
        // sends the bot back to running if the rest catch up.
        private bool TryTurnOnStraggler(PlayerBot bot, Mobile chaser)
        {
            if (!_fightingRetreat || _pickingOff) return false;
            if (_pickOffTurns >= PickOffMaxTurns) return false;
            if (TooHurtToStart(bot, 2)) return false;
            if (bot.Map == null) return false;

            // A non-monster chaser (a red) still close means no turning.
            if (chaser != null && !IsHostileMonster(chaser) && chaser.Alive &&
                chaser.Map == bot.Map &&
                TileDist(bot, chaser.Location) <= SightRange + 4)
            {
                return false;
            }

            Mobile lead = null;
            int d1 = int.MaxValue, d2 = int.MaxValue;
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, SightRange + 4))
            {
                if (m == bot || !IsHostileMonster(m)) continue;
                if (bot.IsUnreachable((BaseCreature)m)) continue;

                int d = TileDist(bot, m.Location);
                if (d < d1)
                {
                    d2   = d1;
                    d1   = d;
                    lead = m;
                }
                else if (d < d2)
                {
                    d2 = d;
                }
            }
            if (lead == null) return false;

            int gap = d2 == int.MaxValue ? 99 : d2 - d1;

            bool isolated;
            if (RangedCombat)
            {
                isolated = d1 >= 3 && d1 <= StandoffMax + 2 &&
                           gap >= PickOffGapRanged && HasLOS(bot, lead);
            }
            else
            {
                isolated = d1 <= 5 && gap >= PickOffGapMelee && HasLOS(bot, lead);
            }
            if (!isolated) return false;

            // Alone it has to be something the bot would take on. Its own
            // dare here, no crowd bonus: the crowd is what it is running
            // from.
            if (lead.HitsMax > BaseDare(bot) * _nerve) return false;

            if (_pickOffTurns == 0)
            {
                _pickOffSince = Core.Now;
            }
            _pickOffTurns++;

            Console.WriteLine(
                $"[Bot {bot.Name}] turns on '{lead.Name}' — {d1} tiles off, " +
                $"next one {(d2 == int.MaxValue ? "nowhere" : d2 + " tiles")} " +
                $"(hp {bot.Hits}/{bot.HitsMax}, turn {_pickOffTurns}/{PickOffMaxTurns})");

            _fleeing    = false;
            _fleeFrom   = null;
            ClearEscapeRoute();
            _pickingOff = true;
            _pickOffFoe = lead;
            _gambling   = false;

            bot.Combatant = lead;
            ChaseFoe(bot, lead);
            return true;
        }

        // -------------------------------------------------------------------
        // ReconsiderTarget — mid-fight target switching.
        //
        // Without this, a bot locked onto foe A ignores foe B chewing on it
        // from behind until A is dead. Rule: switch to a monster that is
        // actively attacking the bot when either (a) the current foe ISN'T
        // attacking it and the attacker is no farther, or (b) the attacker
        // is substantially closer than the current foe. Hysteresis-gated so
        // two attackers don't make the bot ping-pong between them.
        //
        // Returns the foe to fight (the current one if no switch is
        // warranted). Caller updates bot.Combatant when it changes.
        // -------------------------------------------------------------------
        private Mobile ReconsiderTarget(PlayerBot bot, Mobile current)
        {
            if (Core.Now < _nextSwitchAllowed)
            {
                return current;
            }

            bool currentAttackingMe = current.Combatant == bot;
            int currentDist = TileDist(bot, current.Location);

            // A person is a person: a red, or a gray this bot drew on. The
            // rule below used to hand that fight to the first rat that bit
            // the bot, since a red circling at range is "not attacking me"
            // and the rat is. Nobody who drew on a murderer turned round
            // for a rat.
            bool currentIsPerson = current.Player;

            Mobile better = null;
            int betterDist = int.MaxValue;

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, SightRange))
            {
                if (m == bot || m == current || m.Deleted || !m.Alive) continue;
                if (m.Combatant != bot) continue;   // only live attackers matter

                bool worthIt;
                int dist = TileDist(bot, m.Location);

                if (m is BaseCreature bc)
                {
                    if (currentIsPerson) continue;
                    if (bot.IsUnreachable(bc)) continue;
                    worthIt = (!currentAttackingMe && dist <= currentDist) ||
                              dist + 3 <= currentDist;
                }
                else if (m.Player && BotGrayWatch.FairGame(m) && bot.CanSee(m))
                {
                    // A murderer opening up on us while we fight a monster:
                    // the monster can wait. Between two people, the closer.
                    worthIt = !currentIsPerson || dist + 3 <= currentDist;
                }
                else
                {
                    continue;
                }
                if (worthIt && dist < betterDist)
                {
                    better = m;
                    betterDist = dist;
                }
            }

            if (better != null)
            {
                _nextSwitchAllowed = Core.Now + TimeSpan.FromSeconds(5);
                Console.WriteLine(
                    $"[Bot {bot.Name}] switching target: '{current.Name}' -> " +
                    $"'{better.Name}' ({betterDist} tiles, attacking me)");
                return better;
            }

            return current;
        }

        // -------------------------------------------------------------------
        // Step timer
        // -------------------------------------------------------------------
        private void EnsureStepTimer(PlayerBot bot, bool running)
        {
            bool mounted = bot.Mounted;
            if (_stepTimer != null && _running == running && _wasMounted == mounted)
                return;

            StopStepTimer();
            _running = running;
            _wasMounted = mounted;

            // Mounted bots use the faster mount delays.
            int delayMs;
            if (mounted)
            {
                delayMs = running ? MoveDelays.RunMountDelay : MoveDelays.WalkMountDelay;
            }
            else
            {
                delayMs = running ? MoveDelays.RunFootDelay : MoveDelays.WalkFootDelay;
            }

            var interval = TimeSpan.FromMilliseconds(delayMs);
            _stepTimer = Timer.DelayCall(interval, interval, () => StepOnce(bot));
        }

        private void StopStepTimer()
        {
            if (_stepTimer != null)
            {
                _stepTimer.Stop();
                _stepTimer = null;
            }
        }

        private void StepOnce(PlayerBot bot)
        {
            if (bot.Deleted || !bot.Alive || bot.Map == null || bot.Map == Map.Internal)
            {
                StopStepTimer();
                return;
            }

            // ===== FLEE MODE — running for its life =====
            // Overrides all combat. The bot sprints directly away from the
            // threat with double-steps and uses any healing it has. It
            // keeps fleeing until it's well clear or the flee timer runs
            // out, then resumes normal behavior.
            if (_fleeing)
            {
                // What the bot is running from is the whole neighbourhood,
                // not one monster — but the specific thing that started the
                // flee still counts on its own. The survey only sees
                // MONSTERS, so testing it alone made a bot running from a
                // red PK go clear on its first tick, stop, get hit, and
                // flee again: the same name over and over at the same hit
                // points. It is clear when BOTH are.
                var near = NearThreat(bot);

                bool fromGone = _fleeFrom == null || _fleeFrom.Deleted ||
                                !_fleeFrom.Alive || _fleeFrom.Map != bot.Map;
                int fromDist = fromGone
                    ? int.MaxValue
                    : TileDist(bot, _fleeFrom.Location);

                // Hunted means fourteen tiles is not clear, twenty-four is.
                int clearAt = SightRange + 4 + (_hunted ? 10 : 0);
                bool monstersClear = !near.Any || near.NearestDist >= clearAt;
                bool chaserClear   = fromGone || fromDist >= clearAt;

                if (monstersClear && chaserClear || Core.Now >= _fleeUntil)
                {
                    // A pack the bot could not have handled one at a time
                    // stays off limits for a while. Where it was is the
                    // best guess of where it still is.
                    // A withdrawal is its own reason: nothing was chasing,
                    // the room was too much, and the room is still there.
                    if (!_fightingRetreat || _withdrawFlee || _hunted ||
                        _pickOffTurns >= PickOffMaxTurns)
                    {
                        _avoidCenter = near.Any ? near.Center
                                     : !fromGone ? _fleeFrom.Location
                                     : bot.Location;
                        _avoidUntil  = Core.Now + (_hunted ? HuntedAvoidWindow : AvoidWindow);
                    }
                    _fleeing  = false;
                    _fleeFrom = null;
                    ClearEscapeRoute();
                    StopStepTimer();
                    return;
                }

                // While running, keep healing. A melee bot bandages /
                // drinks potions; a mage casts heal/cure. This is what
                // actually keeps a fleeing bot alive. In swing range that
                // means potions, not a 4th-circle cast nothing will let it
                // finish.
                int closest = Math.Min(near.NearestDist, fromDist);

                // A cast roots the caster for two seconds or more, and a
                // chaser five tiles back covers that in one. A fleeing mage
                // drinks unless it has real room; the cure-or-heal CAST is
                // for when the pursuit has fallen well behind.
                if (SpellcasterMode)
                {
                    TrySelfCare(bot, inSwingRange: closest <= 5);
                }
                else
                {
                    TryMeleeSelfHeal(bot);
                }

                if (bot.Frozen) bot.Frozen = false;

                // ROOTED, not stuck. A bot mid-cast (its own heal) or held
                // by a Paralyze cannot take a step, and the route below
                // counted every such tick as a stall: six of them — one
                // Greater Heal — and it threw the route away and sprinted
                // blind into whatever was there. Half of all surface escapes
                // ended that way. Standing still is the engine's doing; the
                // route is still good, so keep it and wait to be free.
                if (bot.Spell != null || bot.Paralyzed)
                {
                    return;
                }

                // THE TURN. Running has strung the pack out and the one in
                // front is alone: stop and take it.
                if (TryTurnOnStraggler(bot, fromGone ? null : _fleeFrom))
                {
                    return;
                }

                // Run from the mass of monsters when there is one, else
                // from whatever single thing is chasing.
                Point3D away = near.Any ? near.Center
                             : !fromGone ? _fleeFrom.Location
                             : bot.Location;

                // ROUTED ESCAPE — walk a real path to a waypoint out of
                // here. A* goes around the walls the blind sprint used to
                // shuffle along.
                if (_escapeFollower != null)
                {
                    var before = bot.Location;
                    bool reachedNode = false;
                    try { reachedNode = _escapeFollower.Follow(true, ArrivalRange); }
                    catch { reachedNode = false; }

                    if (reachedNode)
                    {
                        // Made it and still not clear — chain to the next
                        // node further out rather than stopping here.
                        SetEscapeRoute(bot, away);
                        return;
                    }

                    if (bot.Location != before)
                    {
                        _escapeStalls = 0;
                        return;   // moving along the route, nothing else to do
                    }

                    // The route isn't moving the bot — blocked path, a door
                    // it can't solve, a goal it can't stand on. Standing
                    // still while fleeing is fatal, so fall through to the
                    // blind sprint, and give up on the route entirely if it
                    // keeps failing.
                    if (++_escapeStalls >= 6)
                    {
                        if (CombatDebug)
                        {
                            Console.WriteLine(
                                $"[Bot {bot.Name}] escape route stalled — sprinting blind");
                        }
                        if (_escapeGoalName != null)
                        {
                            _stalledEscapeGoals.Add(_escapeGoalName);
                        }
                        ClearEscapeRoute();

                        // Try once more with that node off the table before
                        // giving up on routing altogether.
                        SetEscapeRoute(bot, away);
                        if (_escapeFollower != null)
                        {
                            return;
                        }
                    }
                }

                // BLIND SPRINT — the fallback, and the only option on
                // ground with no waypoints near it. Double-stepped so the
                // bot genuinely outpaces a same-speed pursuer, and aimed
                // away from the CENTRE OF THE PACK rather than away from
                // whichever single monster triggered the retreat.
                var out1 = bot.GetDirectionTo(away);
                StepAwayFrom(bot, out1);
                var out2 = bot.GetDirectionTo(away);
                StepAwayFrom(bot, out2);
                return;
            }

            // ===== THE PULL — drag one monster off the group =====
            // Runs ahead of the chase so a melee bot walks AWAY from the
            // knot for a moment instead of straight into it. The foe we
            // aggroed follows; its friends fall behind. Ends the instant
            // nothing but the foe is still close, or when the window runs
            // out and the bot just fights where it stands.
            if (Core.Now < _pullUntil)
            {
                var pullFoe = _chaseFoe ?? bot.Combatant as Mobile;
                if (pullFoe == null || pullFoe.Deleted || !pullFoe.Alive ||
                    pullFoe.Map != bot.Map)
                {
                    _pullUntil = DateTime.MinValue;
                }
                else if (CheckRetreat(bot, pullFoe))
                {
                    // Pulling is not worth dying for.
                    return;
                }
                else
                {
                    var pnear = NearThreat(bot);
                    if (pnear.Count <= 1 ||
                        TileDist(bot.Location, _pullFrom) >= PullClearDistance)
                    {
                        // Nothing but the target still near, or far enough
                        // off the knot that the rest won't join in. Turn
                        // and fight it.
                        _pullUntil = DateTime.MinValue;
                        if (CombatDebug)
                        {
                            Console.WriteLine(
                                $"[Bot {bot.Name}] pull SEPARATED '{pullFoe.Name}'");
                        }
                    }
                    else
                    {
                        if (bot.Frozen) bot.Frozen = false;
                        StepAwayFrom(bot, bot.GetDirectionTo(_pullFrom));
                        var pd = bot.GetDirectionTo(pullFoe);
                        if (bot.Direction != pd) bot.Direction = pd;
                        return;
                    }
                }
            }

            // Greedy chase mode — pursue a live foe with fast single steps.
            if (_chaseFoe != null)
            {
                var f = _chaseFoe;
                if (f.Deleted || !f.Alive || f.Map != bot.Map)
                {
                    _chaseFoe = null;
                    StopStepTimer();
                    return;
                }
                // Low on HP? Break off and flee — checked here on the fast
                // loop so it triggers within ~300ms, not on the slow tick.
                if (CheckRetreat(bot, f)) return;
                // Hurt but not yet fleeing — use bandages / potions.
                TryMeleeSelfHeal(bot);
                // Adjacent? Hold position and let the engine swing — but
                // KEEP the pulse running so retreat/heal/facing stay on
                // the fast clock while blows are being traded.
                if (bot.InRange(f.Location, 1))
                {
                    var fd = bot.GetDirectionTo(f);
                    if (bot.Direction != fd) bot.Direction = fd;
                    return;
                }
                // Lost sight of it: it went round a corner, through a door,
                // or behind a wall. Greedy stepping cannot solve any of
                // those — it just walks into the obstacle. Hand back to the
                // decision tick, which paths instead.
                if (!HasLOS(bot, f))
                {
                    _chaseFoe = null;
                    StopStepTimer();
                    return;
                }
                // Step toward the foe; flow around blockers.
                var d = bot.GetDirectionTo(f);
                if (bot.Direction != d) bot.Direction = d;
                if (!bot.Move(d))
                {
                    var left  = (Direction)(((int)d + 7) & 0x7);
                    var right = (Direction)(((int)d + 1) & 0x7);
                    if (!bot.Move(left)) bot.Move(right);
                }
                return;
            }

            // ===== RANGED COMBAT (fast-timer loop) =====
            // Mage/archer combat runs ENTIRELY here, every 200-400ms. This
            // loop OWNS the cast lifecycle: it starts casts, tracks the
            // in-progress cast, fulfils the spell's target cursor, and
            // sets the cooldown only when a cast resolves.
            if (_rangedFoe != null)
            {
                var rf = _rangedFoe;
                if (rf.Deleted || !rf.Alive || rf.Map != bot.Map ||
                    !bot.InRange(rf.Location, SightRange + 6))
                {
                    // Foe gone — drop out; decision tick handles disengage.
                    _rangedFoe = null;
                    ClearCast();
                    StopStepTimer();
                    return;
                }

                int rdist = TileDist(bot, rf.Location);
                var rface = bot.GetDirectionTo(rf);
                if (bot.Direction != rface) bot.Direction = rface;

                // Low on HP? Break off and flee. Checked before any
                // positioning/casting so a hurt mage runs instead of
                // standing to trade another spell it won't survive.
                if (CheckRetreat(bot, rf)) return;

                // ---- A cast is in progress: drive it to resolution ----
                if (_castInProgress)
                {
                    // The spell object — null once the cast resolves OR is
                    // disturbed. bot.Target appears when the cast delay
                    // elapses (the spell's OnCast raised the cursor).
                    var spellNow = bot.Spell as Server.Spells.Spell;
                    bool stillCasting = spellNow != null && spellNow.IsCasting;
                    var cursor = bot.Target;

                    // DANGER: foe is right on top of us — abandon the cast
                    // and kite. EXCEPT when this cast must COMMIT: a
                    // deliberate point-blank attack (cornered, chose to
                    // cast), a self-heal/cure (abandoning it leaves the
                    // mage hurt or poisoned — worse than taking one hit),
                    // a foe that's PARALYZED (it can't swing — finishing
                    // the cast is free), or a TANK MAGE (built to trade
                    // hits at melee range; it never abandons a cast).
                    if (rdist < 2 && !_castPointBlank && !_castSelfTarget &&
                        !rf.Paralyzed && !_tankMage)
                    {
                        if (stillCasting)
                        {
                            try { spellNow.Disturb(
                                Server.Spells.DisturbType.EquipRequest); }
                            catch { }
                        }
                        ClearCast();
                        _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(1.0);
                        if (bot.Frozen) bot.Frozen = false;
                        StepAway(bot, rface, rf);
                        return;
                    }

                    // Target cursor is up — the cast finished its delay.
                    // Fulfil it: a self-cast (heal/cure) targets the bot,
                    // an attack spell targets the foe. This fires the spell.
                    if (cursor != null)
                    {
                        Mobile castTarget = _castSelfTarget ? (Mobile)bot : rf;
                        try { cursor.Invoke(bot, castTarget); } catch { }
                        // Cast resolved. Cooldown from here.
                        double cd = _castCooldown;
                        ClearCast();
                        _nextCastAllowed = Core.Now +
                            TimeSpan.FromSeconds(cd + Utility.RandomMinMax(0, 2));
                        return;
                    }

                    // No cursor yet and the spell object is gone — the
                    // cast was disturbed/fizzled before producing a
                    // target. Pre-AOS that is a SILENT kill: a single hit
                    // landed mid-cast, Disturb ran, and no fizzle effect
                    // or message goes out for it. Treat as a failed cast:
                    // short cooldown.
                    if (!stillCasting && cursor == null)
                    {
                        if (CombatDebug)
                        {
                            Console.WriteLine(
                                $"[Bot {bot.Name}] cast DISTURBED by '{rf.Name}' " +
                                $"(dist {rdist})");
                        }
                        ClearCast();
                        _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(1.5);
                        return;
                    }

                    // Timeout safety — a cast that never resolved.
                    if (Core.Now - _castStartedAt > CastTimeout)
                    {
                        if (stillCasting)
                        {
                            try { spellNow.Disturb(
                                Server.Spells.DisturbType.EquipRequest); }
                            catch { }
                        }
                        ClearCast();
                        _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(1.5);
                        return;
                    }

                    // Still casting, foe not dangerously close — hold
                    // position and let the cast finish. Don't move.
                    return;
                }

                // ---- No cast in progress: position, then maybe cast ----

                bool haveLOS    = HasLOS(bot, rf);
                bool castReady  = SpellcasterMode && Core.Now >= _nextCastAllowed;

                // The band a ranged bot holds is measured against the ROOM,
                // not just against the one thing it is shooting. Kiting a
                // single orc perfectly while three of its friends walk up
                // behind is not kiting. In a crowd the band widens and the
                // bot backs off the CENTRE of the pack, which pulls its
                // target away from the rest — one monster gets shot, the
                // group thins.
                var rnear = NearThreat(bot);
                bool crowd = rnear.Count >= 2;
                int standoffMin = crowd
                    ? Math.Min(StandoffMax - 1, StandoffMin + 2)
                    : StandoffMin;

                // TOO CLOSE — kite away to reopen the gap. A same-speed
                // kite can't open distance one-step-at-a-time, so when the
                // foe is adjacent we DOUBLE-STEP: two retreat tiles this
                // fire. That actually creates separation instead of just
                // shuffling alongside the monster.
                if (rdist < standoffMin ||
                    (crowd && rnear.NearestDist < standoffMin))
                {
                    if (bot.Frozen) bot.Frozen = false;

                    // In a crowd, back off the mass of them; the target
                    // follows and the rest are left behind. Alone, back off
                    // the target itself.
                    var kiteFrom = crowd
                        ? bot.GetDirectionTo(rnear.Center)
                        : rface;

                    // TANK MAGE at melee range: the weapon is the damage.
                    // Re-arm it (the last cast pocketed it) and let the
                    // engine swing — the T2A hally-mage rhythm.
                    //
                    // What it must NOT do is plink a point-blank Magic
                    // Arrow, which is what it used to do every time the
                    // cooldown came up. Casting pockets the weapon again,
                    // so that arrow costs a halberd swing to deal about
                    // five damage: strictly worse than staying silent. It
                    // was also the single largest source of Magic Arrows in
                    // the whole shard.
                    //
                    // When it wants a real spell it does what every other
                    // mage does — makes room, throws it from the band, and
                    // walks back in.
                    if (_tankMage && rdist <= 2)
                    {
                        RearmTankWeapon(bot);

                        if (castReady && haveLOS &&
                            TrySelfCare(bot, inSwingRange: !rf.Paralyzed))
                        {
                            return;
                        }

                        // Worth breaking the rhythm for? Only for a spell
                        // that beats a halberd swing, and only sometimes —
                        // a hally mage that backed off before every cast
                        // would never land a blow.
                        if (castReady && bot.Mana >= TankMageRoomMana &&
                            Utility.RandomDouble() < TankMageRoomChance)
                        {
                            StepAwayFrom(bot, kiteFrom);
                            StepAwayFrom(bot, kiteFrom);
                        }
                        return;
                    }

                    bool moved = StepAwayFrom(bot, kiteFrom);
                    // Adjacent / nearly so → take a second retreat step to
                    // genuinely outpace the monster.
                    if (rdist <= 2 || (crowd && rnear.NearestDist <= 2))
                    {
                        moved |= StepAwayFrom(bot, crowd
                            ? bot.GetDirectionTo(NearThreat(bot).Center)
                            : bot.GetDirectionTo(rf));
                    }

                    // THE BREAK-AWAY, and the reason this whole branch
                    // exists. Casting roots the caster. A bot that answers
                    // every adjacent monster with an instant point-blank
                    // spell therefore never leaves melee at all: it steps
                    // twice, plants itself to chant, the monster walks back
                    // in, and the entire fight is Magic Arrows at arm's
                    // length — which is exactly what it looked like.
                    //
                    // So while the retreat is still working, these ticks are
                    // spent WALKING and nothing is cast. Two or three of
                    // them buys the gap the book needs, and the next tick
                    // through the band below throws a real spell.
                    if (_kiteBreakSince == DateTime.MinValue)
                    {
                        _kiteBreakSince = Core.Now;
                    }

                    // It gives up only for a reason: pinned, so the steps
                    // did nothing, or a foe that simply matches its speed.
                    // Then point-blank Magic Arrow is right again — it is
                    // the one spell that lands through the blows.
                    bool pinned   = !moved;
                    bool outpaced = Core.Now - _kiteBreakSince >= KiteBreakGrace;

                    // Self-care is never held back by the footwork: a
                    // poisoned or badly hurt mage cures and heals whatever
                    // its feet are doing.
                    if (castReady && haveLOS && rdist <= 2)
                    {
                        if (!TrySelfCare(bot, inSwingRange: !rf.Paralyzed) &&
                            (pinned || outpaced))
                        {
                            _castReason = pinned ? "pinned" : "outpaced";
                            BeginCast(bot, rf, pointBlank: true);
                        }
                    }
                    return;
                }

                // TOO FAR — close the gap. (StandoffMax is unchanged by a
                // crowd; only the near edge of the band moves out, so a
                // widened band can never invert.)
                if (rdist > StandoffMax)
                {
                    StepToward(bot, rface);
                    return;
                }

                // IN BAND but no LOS — step in to clear the obstruction.
                if (!haveLOS)
                {
                    StepToward(bot, rface);
                    return;
                }

                // IN BAND, LOS clear — attack, or self-care first.
                // Archer: just stand; ModernUO's combat fires the bow.
                // Mage: cure/heal if needed, otherwise cast at the foe.
                //
                // Standing here at all means the retreat worked, so the
                // grace is re-armed for the next time something closes.
                _kiteBreakSince = DateTime.MinValue;

                if (castReady)
                {
                    if (!TrySelfCare(bot))
                    {
                        _castReason = "band";
                        BeginCast(bot, rf);
                    }
                }
                return;
            }

            if (_follower == null)
            {
                StopStepTimer();
                return;
            }

            bool arrived = _follower.Follow(_running, ArrivalRange);
            // Inside a drawn room outline counts as there, checked per step:
            // a 2-second decision tick is too slow to catch the outline
            // before the bot is on top of the point anyway.
            if (!arrived && _goal != null && PatrolGoalReachedEarly(bot, _goal.Value))
            {
                arrived = true;
            }
            if (arrived)
            {
                // Reached current goal. KEEP _goal set — the decision tick's
                // reached-check needs to observe it to fire OnPatrolGoalReached
                // (route-hop advance, room lingers, teleporter pad-walks all
                // hang off that hook). Clearing it here made every arrival
                // invisible to the slow tick: a crawler starting within
                // ArrivalRange of its first route hop re-selected that hop
                // forever and stood frozen at the dungeon landing.
                StopStepTimer();
                _follower = null;
            }
        }

        // Step one tile directly away from whatever `toward` points at;
        // flow around blockers. Same as StepAway but takes a direction, so
        // it works for running from a POINT (the centre of a pack) as well
        // as from a mobile.
        // Returns whether a tile was actually given up. False means pinned
        // — every way back is blocked — which is the signal the break-away
        // uses to stop trying and fight where it stands.
        private static bool StepAwayFrom(PlayerBot bot, Direction toward)
        {
            var away = (Direction)(((int)toward + 4) & 0x7);
            if (bot.Move(away))
            {
                return true;
            }
            var l = (Direction)(((int)away + 7) & 0x7);
            var r = (Direction)(((int)away + 1) & 0x7);
            return bot.Move(l) || bot.Move(r);
        }

        // Step one tile directly away from the foe; flow around blockers.
        private static void StepAway(PlayerBot bot, Direction faceFoe, Mobile foe)
        {
            var away = (Direction)(((int)faceFoe + 4) & 0x7);
            if (!bot.Move(away))
            {
                var l = (Direction)(((int)away + 7) & 0x7);
                var r = (Direction)(((int)away + 1) & 0x7);
                if (!bot.Move(l)) bot.Move(r);
            }
        }

        // Step one tile toward the foe; flow around blockers.
        private static void StepToward(PlayerBot bot, Direction faceFoe)
        {
            if (!bot.Move(faceFoe))
            {
                var l = (Direction)(((int)faceFoe + 7) & 0x7);
                var r = (Direction)(((int)faceFoe + 1) & 0x7);
                if (!bot.Move(l)) bot.Move(r);
            }
        }

        // Clear all cast-in-progress tracking. Does NOT touch the cooldown
        // — callers set _nextCastAllowed appropriately for their case.
        private void ClearCast()
        {
            _castInProgress = false;
            _castPointBlank = false;
            _castSelfTarget = false;
        }

        // Begin fleeing from a threat. Drops the fight (combatant, foe
        // tracking, any cast) and hands the bot to the fast loop's flee
        // branch, which sprints it away. The flee lasts a bounded window.
        private void StartFlee(PlayerBot bot, Mobile threat)
        {
            // Every flee funnels through here — HP retreat, overwhelming
            // foe, all of it. Scream on the way out. (The HP in the log
            // line tells the two apart: near-full HP = overwhelming dare,
            // low HP = retreat threshold.)
            TryEventLine(bot, 0.5, "combat_flee");
            Console.WriteLine(
                $"[Bot {bot.Name}] fleeing from '{threat?.Name ?? "the area"}' " +
                $"(hp {bot.Hits}/{bot.HitsMax})");

            bot.Combatant = null;
            _chaseFoe  = null;
            _rangedFoe = null;
            _goal      = null;
            _follower  = null;
            _pullUntil = DateTime.MinValue;
            ClearCast();
            _fleeFrom  = threat;
            _fleeing   = true;
            _stalledEscapeGoals.Clear();

            // Run from the MASS of them, not from the one that happened to
            // trigger the retreat. Running from a single monster inside a
            // pack is how a bot ends up sprinting through the rest of it.
            var near = NearThreat(bot);

            // A fresh flee, as opposed to the rest of the pack catching up
            // mid pick-off, starts the turn count over. The count is what
            // stops a bot that keeps turning and never finishing anything.
            if (!_pickingOff &&
                Core.Now - _pickOffSince > PickOffWindow)
            {
                _pickOffTurns = 0;
            }
            _pickingOff = false;
            _pickOffFoe = null;
            _fightingRetreat = AssessPickOff(bot, near, threat);

            // Same thing again, soon after? Count it.
            string fleeName = threat?.Name ?? "the area";
            if (fleeName == _lastFleeName && Core.Now - _lastFleeAt < HuntedWindow)
            {
                _fleeStreak++;
            }
            else
            {
                _fleeStreak = 1;
                _fleeStreakStart = Core.Now;
            }
            _lastFleeName = fleeName;
            _lastFleeAt   = Core.Now;
            _withdrawFlee = threat == null;

            // Hunted is the same thing chasing the bot off again and again.
            // A pack the bot COULD take apart still counts if it never
            // strings out: a lich and two knights that keep station on a
            // mage walked her between the same two waypoints eight times
            // while every flee was judged a fighting retreat. The one thing
            // that clears it is the plan working, a turn taken since the
            // streak began.
            bool turning = _pickOffTurns > 0 && _pickOffSince >= _fleeStreakStart;
            _hunted = _fleeStreak >= 2 && !turning;
            if (_hunted && CombatDebug)
            {
                Console.WriteLine(
                    $"[Bot {bot.Name}] HUNTED by '{fleeName}' — flee {_fleeStreak} in a row, " +
                    $"running further");
            }
            Point3D away = near.Any ? near.Center
                         : threat != null ? threat.Location
                         : bot.Location;

            SetEscapeRoute(bot, away);

            if (CombatDebug)
            {
                Console.WriteLine(_escapeGoal.HasValue
                    ? $"[Bot {bot.Name}] escape route to {_escapeGoal.Value} " +
                      $"({TileDist(bot.Location, _escapeGoal.Value)} tiles)"
                    : $"[Bot {bot.Name}] no escape route — sprinting blind");
            }

            // A routed escape is a real walk to somewhere, so it gets long
            // enough to finish. Blind sprinting keeps the old short window
            // — with nowhere to aim, more time just means more shuffling.
            _fleeUntil = Core.Now +
                TimeSpan.FromSeconds(_escapeFollower != null ? (_hunted ? 40.0 : 20.0) : 8.0);

            EnsureStepTimer(bot, running: true);
        }

        // If the bot's HP has fallen below its retreat threshold, start
        // fleeing from the given threat. Returns true if flee was started.
        // Called from the fast loop so the decision happens within ~300ms
        // — the 2s decision tick is too slow; a bot can die in that gap.
        private bool CheckRetreat(PlayerBot bot, Mobile threat)
        {
            if (threat == null || threat.Deleted || !threat.Alive) return false;

            var near = NearThreat(bot);
            _packAttackers = near.Attackers;

            // HOPELESS — leave on the numbers, before the hit points.
            //
            // The HP threshold below only ever fires once the damage is
            // already done, and against a pack the run out is longer than
            // the health left to pay for it. A player surrounded by more
            // than they can answer leaves at FULL health; they don't stand
            // there to see how it goes. Two or more already swinging and a
            // room weighing twice what this bot can handle is that moment.
            if (_pickingOff)
            {
                // Mid pick-off the whole pack is "on me" by definition; it
                // is chasing. What matters is who has actually arrived. Two
                // in swing range weighing more than the bot answers alone
                // means the rest caught up: back to running, and the turn
                // count stands so this cannot go on forever.
                if (near.CloseAttackers >= 2 &&
                    near.CloseWeight > RoomBudget(bot) * _nerve)
                {
                    if (CombatDebug)
                    {
                        Console.WriteLine(
                            $"[Bot {bot.Name}] pack caught up — " +
                            $"{near.CloseAttackers} in reach weighing " +
                            $"{near.CloseWeight} (hp {bot.Hits}/{bot.HitsMax}, " +
                            $"turn {_pickOffTurns}/{PickOffMaxTurns})");
                    }
                    StartFlee(bot, threat);
                    return true;
                }
            }
            else if (near.Attackers >= 2 && near.Weight > RoomBudget(bot) * 2 * _nerve)
            {
                if (CombatDebug)
                {
                    Console.WriteLine(
                        $"[Bot {bot.Name}] OUTNUMBERED — {near.Attackers} on me, " +
                        $"room weighs {near.Weight} vs budget {RoomBudget(bot)} " +
                        $"(hp {bot.Hits}/{bot.HitsMax})");
                }
                StartFlee(bot, threat);
                return true;
            }

            // THE GAMBLE — "I can take it."
            //
            // Widening nerve alone was not enough to put monster deaths on
            // the board: a bold bot would start a fight over its head, get
            // hurt, and then escape cleanly every single time, because the
            // retreat and the routed run-away work. Bots were dying at
            // roughly four an hour, which reads as a world where nothing
            // is dangerous.
            //
            // What is missing is the decision players actually die to. A
            // bold one that is losing, against something that is losing
            // HARDER, does not run — it swings for the kill. Most of the
            // time it wins the race and it looks like nerve. Sometimes the
            // monster wins it, and that is a death that came from a call
            // somebody made, not from walking into a camp unlooked-at.
            //
            // Deliberately narrow: bold bots only, one-on-one only, and
            // only while the foe is clearly worse off. A swarm still drags
            // the bot out through the OUTNUMBERED check above, which runs
            // first for exactly that reason.
            if (_nerve >= 1.3 && near.Attackers <= 1 && threat.HitsMax > 0 &&
                bot.HitsMax > 0)
            {
                double mine   = (double)bot.Hits / bot.HitsMax;
                double theirs = (double)threat.Hits / threat.HitsMax;

                if (theirs < mine * 0.9)
                {
                    if (CombatDebug && !_gambling)
                    {
                        Console.WriteLine(
                            $"[Bot {bot.Name}] GAMBLING on '{threat.Name}' — " +
                            $"me {bot.Hits}/{bot.HitsMax}, it {threat.Hits}/" +
                            $"{threat.HitsMax} (nerve {_nerve:0.00})");
                    }
                    _gambling = true;
                    return false;   // no retreat: swing for the kill
                }
            }
            _gambling = false;

            // Nerve moves the bail line too: the bold hold on into the red,
            // the careful are gone at two thirds.
            double retreatAt = (DefenderMode
                ? DefenderRetreatHpFraction
                : RetreatHpFraction) / _nerve;

            // Gang pressure: surviving a swarm means leaving EARLIER —
            // incoming damage scales with attackers, the escape run
            // doesn't. Each attacker past the first raises the bail
            // threshold a notch.
            int extra = near.Attackers - 1;
            if (extra > 0)
            {
                retreatAt = Math.Min(0.95, retreatAt + 0.10 * Math.Min(4, extra));
            }

            if (bot.HitsMax > 0 && bot.Hits < bot.HitsMax * retreatAt)
            {
                StartFlee(bot, threat);
                return true;
            }
            return false;
        }

        // (The old invisible combat-supply refill is GONE — un-T2A. Kits
        // now carry era-sized stocks, and BotSupplies turns "running low"
        // into a real shopping errand; see the patrol-section check.)

        // -------------------------------------------------------------------
        // Unreachable-foe detection — see the field comments above.
        // -------------------------------------------------------------------

        // True when the bot is positioned to actually attack the foe: adjacent
        // for melee, or within firing band WITH line of sight for ranged.
        // While in attack position the bot is fighting, not stuck — progress
        // tracking treats that as success.
        private bool InAttackPosition(PlayerBot bot, Mobile foe)
        {
            int dist = TileDist(bot, foe.Location);
            if (RangedCombat)
            {
                return dist <= StandoffMax && HasLOS(bot, foe);
            }
            return dist <= 1;
        }

        // Track progress toward the current foe; abandon it if the bot can't
        // get into attack position within UnreachableTimeout. Returns true if
        // the foe was given up (caller must return — combat is over for it).
        private bool CheckUnreachable(PlayerBot bot, Mobile foe)
        {
            // Already proven unreachable — possibly re-set as our combatant by
            // a foe attacking through a wall. Disengage at once; don't restart
            // the approach (which is what makes the bot pound the wall again).
            if (bot.IsUnreachable(foe))
            {
                AbandonFoe(bot);
                if (DefenderMode)
                {
                    ResumeTraveling(bot);
                }
                return true;
            }

            // New foe (or none tracked) — start the clock fresh; never give up
            // on the first tick we see it.
            if (foe != _progressFoe)
            {
                _progressFoe   = foe;
                _bestFoeDist   = TileDist(bot, foe.Location);
                _foeProgressAt = Core.Now;
                _doorTriedFor  = null;
                _stallAnchor   = bot.Location;
                _stallSince    = Core.Now;
                return false;
            }

            // Already able to attack it — success, not stuck. Keep the clock
            // fresh so a foe that LATER runs out of reach still gets a full
            // grace window before being abandoned.
            if (InAttackPosition(bot, foe))
            {
                _bestFoeDist   = TileDist(bot, foe.Location);
                _foeProgressAt = Core.Now;
                _stallAnchor   = bot.Location;
                _stallSince    = Core.Now;
                return false;
            }

            int dist = TileDist(bot, foe.Location);
            if (dist < _bestFoeDist)
            {
                // Got closer than ever before — real progress.
                _bestFoeDist   = dist;
                _foeProgressAt = Core.Now;
                return false;
            }

            // Has the bot's own body moved? This deliberately does NOT ask
            // whether it got nearer the foe. A bot taking the long way round
            // — out of the corridor, through the door, into the room — spends
            // most of that trip getting FURTHER from the monster in a
            // straight line, and judging it on distance would cut off the
            // exact behaviour the door work exists to allow. Feet, not gap.
            bool moved = Math.Max(Math.Abs(bot.X - _stallAnchor.X),
                                  Math.Abs(bot.Y - _stallAnchor.Y)) > StallMoveTiles;
            if (moved)
            {
                _stallAnchor = bot.Location;
                _stallSince  = Core.Now;
            }

            // Standing still, out of attack position, and cannot even see the
            // thing: that is a wall. No point spending the full grace window
            // on it. With sight, keep the long fuse — the bot may be circling
            // an obstacle, or the foe may simply be running away.
            bool pinned = !moved && !HasLOS(bot, foe) &&
                          Core.Now - _stallSince > BlindStallTimeout;

            // No closer than before AND not in attack position. If that's gone
            // on too long the foe is effectively unreachable — give up.
            if (pinned || Core.Now - _foeProgressAt > UnreachableTimeout)
            {
                // Unless what is "in the way" is a door. A closed door is not
                // a walkable tile, so A* finds no route at all, and
                // PathFollower answers a failed path by stepping straight at
                // the goal (PathFollower.Follow: `if (!(Enabled &&
                // m_Path.Success)) d = GetDirectionTo(goal)`). That walks the
                // bot into the wall BESIDE the door, where it shuffles until
                // this timeout fires and it gives up on a monster that was
                // one doorway away. PlayerBot.Move already opens a door it
                // steps into, the way a player's auto-open does — the bot
                // just never had a reason to step at the door rather than at
                // the monster. So give it one, once, before writing the foe
                // off. Once per foe: a door that will not open (locked, or
                // not the thing in the way after all) must not restart this
                // clock forever.
                if (_doorTriedFor != foe && TryDoorOnTheWay(bot, foe))
                {
                    _doorTriedFor  = foe;
                    _foeProgressAt = Core.Now;
                    return false;
                }

                Console.WriteLine(
                    $"[Bot {bot.Name}] gave up on '{foe.Name}' as unreachable");
                bot.MarkUnreachable(foe, UnreachableIgnore);
                AbandonFoe(bot);
                // A defender that can't reach what it was fighting returns to
                // its trip; a hunter falls through to patrol next tick.
                if (DefenderMode)
                {
                    ResumeTraveling(bot);
                }
                return true;
            }

            return false;
        }

        // A bot pinned against a wall gives up sooner than one that is
        // merely slow. Measured: about 15% of bot paths fail, and raising
        // both the A* node budget and the search window recovered almost
        // none of them, so those goals really are unreachable. Seven seconds
        // of walking on the spot in front of a wall is what reads as a
        // broken bot; three is a bot that tried and moved on.
        private static readonly TimeSpan BlindStallTimeout = TimeSpan.FromSeconds(3);

        // More than this many tiles from the anchor counts as having moved.
        private const int StallMoveTiles = 1;

        // Where the bot was when it last made ground, and when.
        private Point3D _stallAnchor;
        private DateTime _stallSince;

        // How far to look for a door standing between the bot and its foe.
        // Wide enough for a dungeon room's entrance from inside the corridor,
        // narrow enough that it never picks a door in a different part of the
        // level.
        private const int DoorSearchRange = 12;

        // The foe a door detour has already been spent on.
        private Mobile _doorTriedFor;

        // Is there a shut door between the bot and the thing it cannot reach?
        // If so, head for the door instead of the monster (or just open it,
        // when already close enough to touch). Returns true when the bot was
        // given something new to do.
        private bool TryDoorOnTheWay(PlayerBot bot, Mobile foe)
        {
            var map = bot.Map;
            if (map == null || map == Map.Internal)
            {
                return false;
            }

            int foeDist = TileDist(bot, foe.Location);

            BaseDoor best = null;
            int bestCost = int.MaxValue;

            foreach (var item in map.GetItemsInRange(bot.Location, DoorSearchRange))
            {
                if (item is not BaseDoor door || door.Open || door.Locked)
                {
                    continue;
                }

                // Same floor. A door one level up is not what is in the way.
                if (Math.Abs(door.Z - bot.Z) > 15)
                {
                    continue;
                }

                // On the way, not behind us: standing closer to the foe than
                // the bot does.
                int toFoe = TileDist(foe, door.Location);
                if (toFoe >= foeDist)
                {
                    continue;
                }

                // Cheapest total detour wins.
                int cost = TileDist(bot, door.Location) + toFoe;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = door;
                }
            }

            if (best == null)
            {
                return false;
            }

            // Close enough to reach past — just open it. The bot stops
            // ArrivalRange short of anything it walks to, so this has to
            // cover more than the adjacent tile.
            if (bot.InRange(best.Location, ArrivalRange + 1) &&
                DoorHelper.TryOpenNear(bot, ArrivalRange + 1))
            {
                Console.WriteLine(
                    $"[Bot {bot.Name}] opened a door to get at '{foe.Name}'");
                _goal = null;
                _follower = null;
                _chaseFoe = null;
                return true;
            }

            // Otherwise walk to it. The door tile itself is not walkable, so
            // the route ends beside it and the branch above opens it on a
            // later tick.
            Console.WriteLine(
                $"[Bot {bot.Name}] can't reach '{foe.Name}' — going round " +
                $"via the door at ({best.X},{best.Y})");
            SetGoal(bot, best.Location, running: true);
            return true;
        }

        // Drop the current fight entirely (combatant + all pursuit state) so
        // the bot can move on. Used when a foe is abandoned as unreachable.
        private void AbandonFoe(PlayerBot bot)
        {
            bot.Combatant = null;
            _goal        = null;
            _follower    = null;
            _chaseFoe    = null;
            _rangedFoe   = null;
            _progressFoe = null;
            ClearCast();
            StopStepTimer();
        }
    }
}
