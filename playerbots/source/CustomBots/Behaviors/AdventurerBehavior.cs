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

        // Stuck detection.
        private Point3D _lastLoc;
        private DateTime _lastProgressAt;
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

        private static readonly string[] AmbientChat = { "small_talk", "lfg" };
        private static readonly string[] CombatChat  = { "combat_actions" };

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

            // Archer/Ranger bots fight at range. Unless a caller has
            // already set RangedCombat explicitly, infer it from class.
            if (!RangedCombat &&
                (bot.Class == BotClass.Archer || bot.Class == BotClass.Ranger))
            {
                RangedCombat = true;
            }

            // Mage bots also fight at range, and additionally throw spells.
            if (bot.Class == BotClass.Mage)
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
            }
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopStepTimer();
            _chaseFoe = null;
            _rangedFoe = null;
            _fleeFrom = null;
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

            // If this is a timed destination visit (e.g. a graveyard
            // stop), return to traveling once the window is up — but only
            // when NOT in combat. A bot mid-fight finishes the fight first;
            // it'll re-check next tick once the combatant clears.
            if (bot.Combatant == null && CheckVisitExpired(bot)) return;

            ChatCategories = bot.Combatant != null ? CombatChat : AmbientChat;
            TrySpeak(bot);

            // -- 1. Combat --
            var combatant = bot.Combatant;
            if (combatant is Mobile foe)
            {
                bool foeGone = foe.Deleted || !foe.Alive ||
                               foe.Map != bot.Map ||
                               !bot.InRange(foe.Location, SightRange + 4);

                if (foeGone)
                {
                    // This foe is down or out of range.
                    bot.Combatant = null;
                    _goal = null;
                    _follower = null;
                    _chaseFoe = null;
                    _rangedFoe = null;
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
                        // Hunting adventurer: foe gone, go idle/patrol.
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

                    ChaseFoe(bot, foe);
                    return;
                }
            }

            // -- 2. Look for an enemy --
            var target = FindNearbyEnemy(bot);
            if (target != null)
            {
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

            // -- 3. Stuck check --
            if (bot.Location != _lastLoc)
            {
                _lastLoc = bot.Location;
                _lastProgressAt = Core.Now;
            }
            else if (Core.Now - _lastProgressAt > StuckTimeout)
            {
                // Try a different patrol point.
                _goal = null;
                _follower = null;
                _lastProgressAt = Core.Now;
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
                EnsureStepTimer(bot, running: false);
            }
        }

        // -------------------------------------------------------------------
        // EnsurePatrolGoal — picks or refreshes the current patrol goal.
        // -------------------------------------------------------------------
        private void EnsurePatrolGoal(PlayerBot bot)
        {
            // If we've reached the current goal (or have none), pick new.
            if (_goal == null || bot.InRange(_goal.Value, ArrivalRange))
            {
                // If we're far from home, head back. Otherwise pick a random
                // point within PatrolRange.
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
                _follower = new PathFollower(bot, _goal.Value);
            }
            else if (_follower == null)
            {
                _follower = new PathFollower(bot, _goal.Value);
            }
        }

        // Defender helper: if no enemy remains in sight, the fight is over
        // — swap the bot back to a Traveler so it resumes its trip.
        // Returns true if it swapped (caller must return immediately).
        // If an enemy IS still around, returns false and the normal
        // enemy-search will re-engage it next.
        private bool ReturnToTravelIfSafe(PlayerBot bot)
        {
            var stillHostile = FindNearbyEnemy(bot);
            if (stillHostile != null)
                return false;  // more to fight — stay a defender

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

            // Already adjacent? Stand and let the combat tick swing. Keep
            // FACING the foe so we look engaged and turn with it.
            if (bot.InRange(foe.Location, 1))
            {
                _chaseFoe = null;
                StopStepTimer();
                _goal = null;
                _follower = null;
                var face = bot.GetDirectionTo(foe);
                if (bot.Direction != face) bot.Direction = face;
                return;
            }

            int dist = TileDist(bot, foe.Location);

            // CLOSE range: hand the foe to the step timer for GREEDY chase.
            // The step timer fires every 200-400ms — fast enough to keep
            // pace with a fleeing monster. The 2s decision tick alone is
            // far too slow to catch a runner.
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
        // Spell choice scales with Magery skill (Base):
        //   < 35  -> Magic Arrow   (Circle 1)
        //   35-64 -> Fireball      (Circle 3)
        //   65-89 -> Lightning     (Circle 4)
        //   >= 90 -> Energy Bolt   (Circle 6)
        //
        // Spells are built by reflection so an unknown class name fails
        // gracefully (step down the ladder). Requires LOS and reagents.
        // -------------------------------------------------------------------
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

            // The spell ladder, weakest first. A mage casts the strongest
            // rung its Magery allows; on a build mismatch it steps DOWN
            // the ladder rather than collapsing to Magic Arrow.
            (string type, double minMagery, double cd)[] ladder =
            {
                ("Server.Spells.First.MagicArrowSpell",   0.0, 2.0),
                ("Server.Spells.Third.FireballSpell",    35.0, 2.5),
                ("Server.Spells.Fourth.LightningSpell",  65.0, 2.5),
                ("Server.Spells.Sixth.EnergyBoltSpell",  90.0, 3.0),
            };

            int rung = 0;
            for (int i = 0; i < ladder.Length; i++)
                if (magery >= ladder[i].minMagery) rung = i;

            Server.Spells.Spell spell = null;
            double cooldownSeconds = 2.0;
            for (int i = rung; i >= 0 && spell == null; i--)
            {
                spell = CreateSpell(ladder[i].type, bot);
                if (spell != null) cooldownSeconds = ladder[i].cd;
            }
            if (spell == null)
            {
                // Nothing resolved — skip, no crash.
                _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(3.0);
                return;
            }

            var face = bot.GetDirectionTo(foe);
            if (bot.Direction != face) bot.Direction = face;

            try
            {
                spell.Cast();
            }
            catch
            {
                _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(3.0);
                return;
            }

            // Cast launched — record tracking. The fast loop takes over:
            // it will invoke the target cursor when it appears and set the
            // cooldown on resolution.
            _castInProgress = true;
            _castStartedAt  = Core.Now;
            _castCooldown   = cooldownSeconds;
            _castPointBlank = pointBlank;
            _castSelfTarget = false;
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
                spell.Cast();
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
            if (bot.Poisoned)
            {
                BeginSelfCast(bot, "Server.Spells.Second.CureSpell", 2.0);
                return _castInProgress;
            }

            // HEAL — scale the spell to the wound and the bot's skill.
            double hpFraction = bot.HitsMax > 0
                ? (double)bot.Hits / bot.HitsMax
                : 1.0;
            double magery = bot.Skills[SkillName.Magery].Base;

            if (hpFraction < 0.40 && magery >= 65.0)
            {
                // Badly hurt and skilled enough — Greater Heal.
                BeginSelfCast(bot, "Server.Spells.Fourth.GreaterHealSpell", 2.5);
                return _castInProgress;
            }
            if (hpFraction < 0.65)
            {
                // Hurt — a basic Heal. If Greater Heal was wanted but the
                // bot lacks the skill, this is the fallback.
                BeginSelfCast(bot, "Server.Spells.First.HealSpell", 2.0);
                return _castInProgress;
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
        //   HP < 45%  -> drink a Heal Potion (instant) AND start a bandage
        //   HP < 70%  -> start a bandage (bandages are the steady heal)
        // -------------------------------------------------------------------
        private void TryMeleeSelfHeal(PlayerBot bot)
        {
            if (SpellcasterMode) return;                 // mages use spells
            if (Core.Now < _nextCastAllowed) return;     // shared heal gate
            if (bot.Backpack == null) return;

            double hpFraction = bot.HitsMax > 0
                ? (double)bot.Hits / bot.HitsMax
                : 1.0;
            if (hpFraction >= 0.70) return;              // not hurt enough

            bool didSomething = false;

            // Badly hurt — drink a health potion for an instant top-up.
            if (hpFraction < 0.45)
            {
                if (DrinkHealPotion(bot)) didSomething = true;
            }

            // Start a bandage — the bread-and-butter heal-over-time.
            if (StartBandageSelf(bot)) didSomething = true;

            if (didSomething)
            {
                // Brief gate so the bot doesn't spam every fast-loop tick.
                _nextCastAllowed = Core.Now + TimeSpan.FromSeconds(2.0);
            }
        }

        // Find a HealPotion (or GreaterHealPotion) in the pack and drink
        // it. Returns true if one was drunk. Reflection-based: calls the
        // potion's Drink(Mobile) method without a hard compile dependency.
        private static bool DrinkHealPotion(PlayerBot bot)
        {
            string[] potionTypes =
            {
                "Server.Items.GreaterHealPotion",
                "Server.Items.HealPotion",
            };
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
        private static bool StartBandageSelf(PlayerBot bot)
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

        private static Server.Spells.Spell CreateSpell(string typeName, PlayerBot caster)
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

                // Is this tile occupied by another mobile? If so, skip —
                // unless it's us (we might already be standing in a slot).
                bool occupied = false;
                foreach (var m in foe.Map.GetMobilesInRange(
                             new Point3D(tx, ty, foe.Z), 0))
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
                    best = new Point3D(tx, ty, foe.Z);
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
        // Correct filter:
        //   - Skip controlled pets and summons.
        //   - Skip anything with Karma >= 0. Monsters in UO have very
        //     negative karma (-1000 to -10000); wildlife is 0 or slightly
        //     positive; guards/NPCs are positive.
        //   - Require FightMode != None as a final sanity check.
        //
        // Note: bots WILL engage monsters that invade guarded zones. The
        // Karma filter handles this — zombies/orcs/etc are Karma < 0, so
        // an Adventurer who finds themselves in a city under attack will
        // defend it. This is intentional for "town gets invaded" events.
        // -------------------------------------------------------------------
        private Mobile FindNearbyEnemy(PlayerBot bot)
        {
            Mobile best = null;
            int bestDistSq = int.MaxValue;
            bool bestVisible = false;

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, SightRange))
            {
                if (m == bot || m.Deleted || !m.Alive) continue;
                if (m is not BaseCreature bc) continue;

                // Skip players' pets and summoned creatures.
                if (bc.ControlMaster != null || bc.Summoned) continue;

                // Skip anything that isn't actually hostile.
                if (bc.FightMode == FightMode.None) continue;

                // Karma test: real monsters are deeply negative. NPCs
                // (guards, vendors, town criers, healers) are neutral or
                // positive.
                if (bc.Karma >= 0) continue;

                int dx = bc.X - bot.X;
                int dy = bc.Y - bot.Y;
                int distSq = dx * dx + dy * dy;

                // A foe the bot can SEE always beats one it can't —
                // engaging a target behind a wall means the bot walks up
                // and casts at it, and the spell fizzles its own LOS
                // check. Within the same visibility tier, nearest wins.
                bool visible = HasLOS(bot, bc);

                bool better;
                if (visible != bestVisible)
                    better = visible;            // visible always wins
                else
                    better = distSq < bestDistSq; // same tier: nearest

                if (best == null || better)
                {
                    best = bc;
                    bestDistSq = distSq;
                    bestVisible = visible;
                }
            }
            return best;
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
                // Adjacent? Stop stepping, let the combat tick swing.
                if (bot.InRange(f.Location, 1))
                {
                    var fd = bot.GetDirectionTo(f);
                    if (bot.Direction != fd) bot.Direction = fd;
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
                    // cast) or a self-heal/cure (abandoning it leaves the
                    // mage hurt or poisoned — worse than taking one hit).
                    if (rdist < 2 && !_castPointBlank && !_castSelfTarget)
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
                // Reached current goal — decision tick picks the next one.
                StopStepTimer();
                _goal = null;
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
            if (bot.HitsMax > 0 && bot.Hits < bot.HitsMax * retreatAt)
            {
                StartFlee(bot, threat);
                return true;
            }
            return false;
        }
    }
}
