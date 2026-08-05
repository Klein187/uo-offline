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
        private PathFollower _follower;
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

        // FLEE MODE — when set, the bot is running for its life. The fast
        // loop drops everything else and sprints directly away from this
        // threat with double-steps. Triggered when HP falls below the
        // retreat threshold. _fleeUntil bounds it so a bot that has gotten
        // clear eventually stops and resumes normal behavior.
        private Mobile   _fleeFrom;
        private DateTime _fleeUntil;

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

            // A bard heading into the field packs its lute.
            EnsureInstrument(bot);
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopStepTimer();
            _chaseFoe = null;
            _rangedFoe = null;
            _fleeFrom = null;
            _progressFoe = null;
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
            if (_fleeFrom != null)
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
                    // Refresh the gang-pressure count while fighting — the
                    // fast loop's CheckRetreat reads it every ~300ms but a
                    // 2s-stale count is fine (monsters don't teleport in).
                    _packAttackers = CountAttackers(bot);

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

            bool reached = _goal != null && bot.InRange(_goal.Value, ArrivalRange);

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
                _follower = new PathFollower(bot, _goal.Value);
            }
            else if (_follower == null)
            {
                _follower = new PathFollower(bot, _goal.Value);
            }
        }

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

        // When false, the bot stops STARTING fights: FindNearbyEnemy only
        // returns foes already attacking it or a friend. A DungeonCrawler
        // in exit mode overrides this — on the way out you defend
        // yourself, you don't clear rooms.
        protected virtual bool WantsFreshFights => true;

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
            if (dist <= 4)
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
            Point3D slot = PickAttackSlot(bot, foe);
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
        private static readonly
            (string type, double minMagery, int mana, double cd, int weight)[] AttackSpellBook =
        {
            ("Server.Spells.First.MagicArrowSpell",     0.0,  4, 2.0,  2),
            ("Server.Spells.Second.HarmSpell",         25.0,  6, 2.0,  2),
            ("Server.Spells.Third.FireballSpell",      40.0,  9, 2.25, 3),
            ("Server.Spells.Fourth.LightningSpell",    55.0, 11, 2.5,  3),
            ("Server.Spells.Fifth.MindBlastSpell",     70.0, 14, 2.5,  2),
            ("Server.Spells.Sixth.EnergyBoltSpell",    85.0, 20, 3.0,  3),
            ("Server.Spells.Sixth.ExplosionSpell",     90.0, 20, 3.0,  3),
            ("Server.Spells.Seventh.FlameStrikeSpell", 95.0, 40, 3.5,  2),
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

            // Build the eligible pool: skilled enough AND can afford it now.
            Span<int> eligible = stackalloc int[AttackSpellBook.Length];
            int count = 0;
            for (int i = 0; i < AttackSpellBook.Length; i++)
            {
                if (magery >= AttackSpellBook[i].minMagery &&
                    bot.Mana >= AttackSpellBook[i].mana)
                {
                    eligible[count++] = i;
                }
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
                Console.WriteLine($"[Bot {bot.Name}] casting {shortName} at '{foe.Name}'");
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
        private bool TrySelfCare(PlayerBot bot)
        {
            if (!SpellcasterMode) return false;
            if (Core.Now < _nextCastAllowed) return false;
            if (bot.Spell != null || _castInProgress) return false;

            // CURE — poison ticks HP down and is worth clearing first.
            // Mana-gated (Cure is 2nd circle, 6 mana): an OOM mage burns
            // no cast attempt and reaches for a cure potion instead.
            if (bot.Poisoned)
            {
                if (bot.Mana >= 6)
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

            if (hpFraction < 0.40 && magery >= 65.0 && bot.Mana >= 11)
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
        private static int TileDist(Mobile m, Point3D p)
        {
            int dx = m.X - p.X; if (dx < 0) dx = -dx;
            int dy = m.Y - p.Y; if (dy < 0) dy = -dy;
            return dx > dy ? dx : dy;
        }

        private static Point3D PickAttackSlot(PlayerBot bot, Mobile foe)
        {
            // 8 tiles around the foe.
            int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
            int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

            Point3D best = foe.Location;
            int bestDistSq = int.MaxValue;
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
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
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
                _follower = new PathFollower(bot, goal);
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

                // Threat gate: don't START a fight above the dare ceiling.
                // A foe already on us stays targetable regardless.
                int dare   = EffectiveDare(bot, bc);
                int danger = bc.HitsMax;
                if (danger > dare && !attackingMe)
                {
                    continue;
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
                    // Fighting back is right up to ~1.5x the ceiling;
                    // beyond that the right move is to run.
                    bestOverwhelming = attackingMe && danger > dare * 3 / 2;
                }
            }

            overwhelming = bestOverwhelming;
            return best;
        }

        // Effective dare ceiling for this bot against this foe: the tier's
        // base ceiling, raised 60% for every friendly bot already fighting
        // the foe (bravery in numbers — crowds swarm what no one bot would
        // touch alone).
        private static int EffectiveDare(PlayerBot bot, BaseCreature foe)
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

            Mobile better = null;
            int betterDist = int.MaxValue;

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, SightRange))
            {
                if (m == bot || m == current || m.Deleted || !m.Alive) continue;
                if (m is not BaseCreature bc) continue;
                if (bc.Combatant != bot) continue;   // only live attackers matter
                if (bot.IsUnreachable(bc)) continue;

                int dist = TileDist(bot, bc.Location);

                bool worthIt = (!currentAttackingMe && dist <= currentDist) ||
                               dist + 3 <= currentDist;
                if (worthIt && dist < betterDist)
                {
                    better = bc;
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
            if (_fleeFrom != null)
            {
                var threat = _fleeFrom;
                bool threatGone = threat.Deleted || !threat.Alive ||
                                  threat.Map != bot.Map;
                int fdist = threatGone ? 999 : TileDist(bot, threat.Location);

                // Clear of danger, or the flee window expired → stop
                // fleeing. The decision tick decides what to do next
                // (defenders resume travel, hunters head home).
                if (threatGone || fdist >= SightRange + 4 ||
                    Core.Now >= _fleeUntil)
                {
                    _fleeFrom = null;
                    StopStepTimer();
                    return;
                }

                // While running, keep healing. A melee bot bandages /
                // drinks potions; a mage casts heal/cure. This is what
                // actually keeps a fleeing bot alive.
                if (SpellcasterMode)
                {
                    TrySelfCare(bot);
                }
                else
                {
                    TryMeleeSelfHeal(bot);
                }

                // Sprint away — double-step so the bot genuinely outpaces
                // the pursuer instead of being chased down at equal speed.
                if (bot.Frozen) bot.Frozen = false;
                var faceThreat = bot.GetDirectionTo(threat);
                StepAway(bot, faceThreat, threat);
                var faceThreat2 = bot.GetDirectionTo(threat);
                StepAway(bot, faceThreat2, threat);
                return;
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
                    // target. Treat as a failed cast: short cooldown.
                    if (!stillCasting && cursor == null)
                    {
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

                // TOO CLOSE — kite away to reopen the gap. A same-speed
                // kite can't open distance one-step-at-a-time, so when the
                // foe is adjacent we DOUBLE-STEP: two retreat tiles this
                // fire. That actually creates separation instead of just
                // shuffling alongside the monster.
                if (rdist < StandoffMin)
                {
                    if (bot.Frozen) bot.Frozen = false;

                    // TANK MAGE at melee range: no kiting. Re-arm the
                    // weapon (the last cast pocketed it) and stand in —
                    // the engine swings it while the next spell comes off
                    // cooldown. The T2A hally-mage rhythm.
                    if (_tankMage && rdist <= 2)
                    {
                        RearmTankWeapon(bot);
                        if (castReady && haveLOS)
                        {
                            if (!TrySelfCare(bot))
                            {
                                BeginCast(bot, rf, pointBlank: true);
                            }
                        }
                        return;
                    }

                    StepAway(bot, rface, rf);
                    // Adjacent / nearly so → take a second retreat step to
                    // genuinely outpace the monster.
                    if (rdist <= 2)
                    {
                        var rf2 = bot.GetDirectionTo(rf);
                        StepAway(bot, rf2, rf);
                    }

                    // CORNERED: self-care takes priority — a cornered mage
                    // that's poisoned or badly hurt should cure/heal rather
                    // than trade point-blank Fireballs. If self-care didn't
                    // fire, cast an attack spell point-blank.
                    if (castReady && haveLOS && rdist <= 2)
                    {
                        if (!TrySelfCare(bot))
                        {
                            BeginCast(bot, rf, pointBlank: true);
                        }
                    }
                    return;
                }

                // TOO FAR — close the gap.
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
                if (castReady)
                {
                    if (!TrySelfCare(bot))
                    {
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
                $"[Bot {bot.Name}] fleeing from '{threat?.Name}' " +
                $"(hp {bot.Hits}/{bot.HitsMax})");

            bot.Combatant = null;
            _chaseFoe  = null;
            _rangedFoe = null;
            _goal      = null;
            _follower  = null;
            ClearCast();
            _fleeFrom  = threat;
            _fleeUntil = Core.Now + TimeSpan.FromSeconds(8.0);
            EnsureStepTimer(bot, running: true);
        }

        // If the bot's HP has fallen below its retreat threshold, start
        // fleeing from the given threat. Returns true if flee was started.
        // Called from the fast loop so the decision happens within ~300ms
        // — the 2s decision tick is too slow; a bot can die in that gap.
        private bool CheckRetreat(PlayerBot bot, Mobile threat)
        {
            if (threat == null || threat.Deleted || !threat.Alive) return false;
            double retreatAt = DefenderMode
                ? DefenderRetreatHpFraction
                : RetreatHpFraction;

            // Gang pressure: surviving a swarm means leaving EARLIER —
            // incoming damage scales with attackers, the escape run
            // doesn't. Each attacker past the first raises the bail
            // threshold a notch (capped so a mob doesn't make bots
            // flee at a scratch).
            int extra = _packAttackers - 1;
            if (extra > 0)
            {
                retreatAt = Math.Min(0.90, retreatAt + 0.08 * Math.Min(3, extra));
            }

            if (bot.HitsMax > 0 && bot.Hits < bot.HitsMax * retreatAt)
            {
                StartFlee(bot, threat);
                return true;
            }
            return false;
        }

        // Hostiles actively targeting the bot right now (the current foe
        // included). Feeds the gang-pressure retreat scaling above.
        private static int CountAttackers(PlayerBot bot)
        {
            int n = 0;
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 6))
            {
                if (m is BaseCreature bc && !bc.Deleted && bc.Alive &&
                    bc.Combatant == bot)
                {
                    n++;
                }
            }
            return n;
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
                return false;
            }

            // Already able to attack it — success, not stuck. Keep the clock
            // fresh so a foe that LATER runs out of reach still gets a full
            // grace window before being abandoned.
            if (InAttackPosition(bot, foe))
            {
                _bestFoeDist   = TileDist(bot, foe.Location);
                _foeProgressAt = Core.Now;
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

            // No closer than before AND not in attack position. If that's gone
            // on too long the foe is effectively unreachable — give up.
            if (Core.Now - _foeProgressAt > UnreachableTimeout)
            {
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
