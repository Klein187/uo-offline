// =========================================================================
// PlayerBot.cs — Fake player with swappable behavior, speech color, outfit.
//
// What's new in v3:
//   - Random outfit archetypes via EquipmentTable (peasant, mage, warrior,
//     adventurer, merchant, wanderer)
//
// v2 additions still here:
//   - Names from NamePool (hundreds curated + algorithmic fallback)
//   - Per-bot speech color via Mobile.SpeechHue
//
// To use:
//   [SpawnTestBot          - drop a bot at your feet
//   [SetBehavior wander    - target a bot, switch it to wander
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Commands;
using Server.Items;
using Server.Mobiles;
using Server.Network;
using Server.Targeting;

namespace Server.CustomBots
{
    public class PlayerBot : PlayerMobile
    {
        public bool IsBot { get; set; } = true;

        // -------------------------------------------------------------------
        // Note on speech color:
        // Mobile.SpeechHue (inherited) is what overhead chat actually uses.
        // We set it in the constructor and let Mobile's own serialization
        // persist it. No SpeechHue field of our own; redeclaring would
        // shadow the inherited one and break Say()'s color lookup.
        // -------------------------------------------------------------------

        // -------------------------------------------------------------------
        // Behavior — the current "brain". Always non-null; falls back to
        // IdleBehavior. Swap via the setter; OnAttached/OnDetached fire.
        // -------------------------------------------------------------------
        private PlayerBotBehavior _behavior;

        public PlayerBotBehavior Behavior
        {
            get => _behavior;
            set
            {
                var newBehavior = value ?? new IdleBehavior();
                if (ReferenceEquals(_behavior, newBehavior))
                {
                    return;
                }

                _behavior?.OnDetached(this);
                _behavior = newBehavior;
                _behavior.OnAttached(this);

                // Reset phase timer whenever the brain changes. The
                // BotLifecycleManager uses this to decide when to transition
                // again. Skip on bots that haven't yet been seen by the
                // lifecycle (no personality assigned) — that case is
                // handled when personality is first assigned.
                if (Personality.IsAssigned)
                {
                    PhaseStartedAt = Core.Now;
                }
            }
        }

        // ---- Unreachable-foe memory ----
        //
        // Monsters this bot has proven it cannot physically reach — the
        // classic case is a critter (a giant rat, a bird) sealed inside a
        // building that attacks the bot THROUGH the wall. The bot can see it
        // and is flagged as its target, but there's no walkable path, so a
        // naive bot wedges against the wall trying to close.
        //
        // This memory lives on the BOT, not on a behavior, so it survives the
        // Traveler<->defender swap. Without it, each time a defender gives up
        // and reverts to a Traveler, the through-wall attack instantly spawns
        // a fresh defender with no memory and the bot pounds the wall again.
        // Behaviors consult IsUnreachable() before engaging and call
        // MarkUnreachable() when they give up. Transient; not serialized.
        private Dictionary<Mobile, DateTime> _unreachableFoes;

        // Remember that this foe can't be reached, for the given window.
        public void MarkUnreachable(Mobile foe, TimeSpan duration)
        {
            if (foe == null)
            {
                return;
            }
            _unreachableFoes ??= new Dictionary<Mobile, DateTime>();
            _unreachableFoes[foe] = Core.Now + duration;
        }

        // Is this foe currently flagged unreachable? Prunes expired/dead
        // entries as it checks so the map stays small.
        public bool IsUnreachable(Mobile foe)
        {
            if (foe == null || _unreachableFoes == null)
            {
                return false;
            }
            if (_unreachableFoes.TryGetValue(foe, out var until))
            {
                if (!foe.Deleted && Core.Now < until)
                {
                    return true;
                }
                _unreachableFoes.Remove(foe);
            }
            return false;
        }

        // ---- Lifecycle state ----

        // Personality drives behavior selection in the lifecycle manager.
        // Assigned lazily on first lifecycle tick if not present.
        public BotPersonality Personality;

        // When the current phase began. Lifecycle compares (now - this) to
        // Personality.AveragePhaseDuration to decide on transitions.
        public DateTime PhaseStartedAt;

        // When true, BotLifecycleManager never transitions this bot — it stays
        // locked in its spawned behavior forever. Set at spawn time for bots
        // placed by a FixedRoleBotSpawner (the spawn editor's "fixed role"
        // kind). Not serialized: bots are transient and re-derive this from
        // their spawner each time they're respawned.
        public bool LifecycleExempt;

        // ---- Identity (class + skill tier) ----

        // What kind of character this bot is. Rolled at creation, immutable.
        // Drives equipment selection and paperdoll title. Independent of
        // behavior — a Grandmaster Mage might be a BankSitter today.
        public BotClass Class;

        // How experienced this bot is. Bell-curved distribution: most are
        // mid-tier, few are Grandmasters. Drives equipment quality.
        public BotSkillTier SkillTier;

        // Craft specialization. Only meaningful when Class == Crafter;
        // rolled at creation. Drives the crafter's station (forge / tailor
        // vendor / bowyer / bank), its "making" animation, and its title.
        public CrafterType CrafterSpec;

        // Guild membership — index into BotGuilds.All, or -1 for the
        // unguilded majority. Rolled at creation; shown as "Name [TAG]"
        // via ApplyNameSuffix.
        public int BotGuildIndex = -1;

        // ---- Session state (BotSessionManager) ----
        //
        // When this bot's play session ends — it says goodbye and logs
        // out. MinValue = not yet stamped (the manager stamps it on first
        // sight). Transient: bots don't persist, sessions restart with
        // the server.
        public DateTime SessionEndsAt;

        // Set while the goodbye-then-delete sequence is running so nothing
        // schedules it twice.
        public bool LoggingOut;

        // ---- Death state (BotDeathManager) ----
        //
        // True from resurrection until the corpse is reclaimed (or given
        // up on). TravelerBehavior checks it to break off toward the
        // corpse; the session manager won't log a bot out mid-run.
        public bool CorpseRunPending;

        // What the bot was doing when it died, so a red comes back a red
        // and everyone else resumes a sensible life. Transient.
        public string PreDeathBehaviorName;

        // Death-spiral brake: consecutive recent deaths (decays after an
        // hour without one). A bot that dies twice in the same dungeon
        // takes its stuff and goes home instead of feeding the same
        // scorpion room forever.
        public int RecentDeaths;
        public DateTime LastDeathAt;

        // ---- Home city (IDEAS 1.3) ----
        //
        // Rolled at creation; destination picks weigh this city's spots
        // extra, so the same faces keep turning up at the same bank —
        // regulars emerge for free. Transient (bots are transient).
        public string HomeCity;

        // ---- Gatherer haul state ----
        //
        // Set when a gatherer's shift ends: the next destination rolls
        // point at town (bank / buying crafter), where the delivery scene
        // plays and clears it.
        public bool HaulPending;

        // The pack llama/horse a gatherer's shift spawned — the yield
        // rides in ITS pack. Runtime-only (not serialized): restarts sweep
        // stray beasts at load and the next shift spawns a fresh one.
        // Cleared by delivery (BotEconomy), OnAfterDelete, and the beast's
        // own ownerless reaper. See BotPackAnimal.cs.
        public Server.Mobiles.BaseCreature PackAnimal;

        // The tamer's FIGHTING pet (nightmare, white wyrm...) — spawned
        // when a Tamer-class bot heads out to hunt, driven centrally by
        // BotCombatPets' upkeep. Runtime-only, same lifecycle doctrine as
        // PackAnimal: restarts sweep strays, deletion releases it.
        public Server.Mobiles.BaseCreature CombatPet;

        // Supply-errand cooldown (see BotSupplies): a failed shopping trip
        // or an empty purse must not loop the bot at the counter.
        // Runtime-only — bots are transient.
        public DateTime NextSupplyErrandAt;

        // ---- Gear progression (IDEAS 4.3) ----
        //
        // Dungeon runs survived since the last upgrade. At the bank, three
        // of them buy a visible step up: tier promotion + fresh kit.
        public int DungeonRunsSurvived;

        // ---- Constructors ----

        // [Constructible] makes this constructor visible to:
        //   - The [add command (`[add playerbot`)
        //   - ModernUO's spawner system (BaseSpawner only calls constructors
        //     that pass IsConstructible — without this attribute, spawners
        //     log "There is no constructor for ... that matches the given
        //     predicate" and silently produce nothing).
        [Constructible(AccessLevel.GameMaster)]
        public PlayerBot()
            : this(BotClassHelper.RollRandom(), BotSkillTierHelper.RollRandom())
        {
        }

        // Construct a bot with a SPECIFIC class and skill tier instead of
        // random rolls. Used by the [SpawnBot command so an admin can drop
        // a bot of a chosen class (e.g. a Mage) for testing.
        public PlayerBot(BotClass cls, BotSkillTier tier) : base()
        {
            // Mark this mobile as a player from the system's perspective.
            // PlayerMobile's default constructor doesn't set m_Player = true
            // (account creation normally does that), so we have to. Without
            // this, dungeon entrance teleporters refuse to fire on bots
            // because Teleporter.CanTeleport returns false when m.Player is
            // false and Creatures isn't enabled.
            Player = true;

            // Bots never eat or drink. Vanilla ModernUO doesn't auto-decay
            // hunger or thirst — these properties exist but no built-in
            // timer ticks them down. Setting both to max here is defense
            // in depth: if any custom content ever drains them, bots still
            // read as fully fed.
            Hunger = 20;
            Thirst = 20;

            Female = Utility.RandomBool();
            Body   = Female ? 0x191 : 0x190;

            Hue = Race.RandomSkinHue();
            Utility.AssignRandomHair(this);

            if (!Female)
            {
                Utility.AssignRandomFacialHair(this, randomHue: false);
                FacialHairHue = HairHue;
            }

            // Unique across all live bots — PickUnique claims the name in
            // NamePool's registry; OnAfterDelete releases it.
            Name = NamePool.PickUnique(Female);
            SpeechHue = SpeechHues.PickRandom();

            // The bot's "identity" — class (what they are) and skill tier
            // (how experienced). Passed in (random or explicit). Drives
            // skills, stats, equipment.
            Class     = cls;
            SkillTier = tier;

            // Crafter specialization — rolled for every bot but only used
            // when Class is Crafter. Cheap to always roll; keeps the field
            // valid regardless of class.
            CrafterSpec = CrafterTypeHelper.RollRandom();

            // Guild membership — ~40% of the population, weighted so big
            // and small guilds emerge.
            BotGuildIndex = BotGuilds.RollMembership();

            // Home city — where this bot "lives"; its destination rolls
            // favor home, so regulars emerge at every bank and forge.
            HomeCity = BotHomeCities.RollHome();

            // Apply real UO skills based on class template. The paperdoll
            // title comes from the highest skill, so a GM Warrior naturally
            // shows "the Grandmaster Swordsman" — no manual Title needed.
            ApplyClassSkills();

            // Apply class-flavored stats (Warriors are Str-heavy, etc.) and
            // recompute Hits/Stam/Mana from the new stat values.
            ApplyClassStats();

            // Every bot needs a backpack. PlayerMobile's constructor does
            // NOT create one (that normally happens during character
            // creation), so without this a bot has no container — nothing
            // to hold loot, nothing for a thief to snoop or steal. Create
            // it before rolling equipment so EquipmentTable can stock it.
            if (Backpack == null)
            {
                var pack = new Backpack { Movable = false };
                AddItem(pack);
            }

            // Roll equipment from the class+tier specific pool.
            EquipmentTable.RollOutfit(this, Class, SkillTier);

            // Tank mages carry the famous weapon — a halberd for the
            // Swords roll ("Hally Mage"), war mace / spear for the
            // others. The mage look itself never rolls weapons, so this
            // rides on top. Casting pockets it (pre-AOS ClearHands);
            // AdventurerBehavior re-arms it between casts.
            if (Class == BotClass.Mage)
            {
                EquipmentTable.EquipTankMageWeapon(this, SkillTier);
            }

            // Faction guild members carry the era shield — the visible
            // allegiance marker (and what opposing bots fight on sight
            // over). Replaces any rolled shield; bots with two-handed
            // weapons (bows etc) go shieldless but are still in the war.
            EquipFactionShield();

            Behavior = new IdleBehavior();
        }

        // -------------------------------------------------------------------
        // Apply this bot's skill template based on Class and SkillTier.
        // Primary skill gets PrimarySkillTarget(tier) + jitter. Secondaries
        // get SecondarySkillTarget(tier) + jitter (the -4 offset keeps the
        // primary on top, so the paperdoll title reflects the class).
        // -------------------------------------------------------------------
        private void ApplyClassSkills()
        {
            ApplySkillTemplate(BotSkillTemplates.RollTemplate(Class));
        }

        private void ApplySkillTemplate(SkillTemplate template)
        {
            double primaryTarget   = BotSkillTemplates.PrimarySkillTarget(SkillTier);
            double secondaryTarget = BotSkillTemplates.SecondarySkillTarget(SkillTier);
            double utilityTarget   = BotSkillTemplates.UtilitySkillTarget(SkillTier);

            // Primary
            double primaryVal = Math.Clamp(
                primaryTarget + BotSkillTemplates.RollJitter(),
                0, 100);
            this.Skills[template.Primary].Base = primaryVal;

            // Secondaries — each independently jittered
            foreach (var skill in template.Secondary)
            {
                double val = Math.Clamp(
                    secondaryTarget + BotSkillTemplates.RollJitter(),
                    0, 100);
                this.Skills[skill].Base = val;
            }

            // Utility skills — half scale (a dexxer's recall Magery).
            foreach (var skill in template.Utility)
            {
                double val = Math.Clamp(
                    utilityTarget + BotSkillTemplates.RollJitter(),
                    0, 100);
                this.Skills[skill].Base = val;
            }
        }

        // -------------------------------------------------------------------
        // Apply class-flavored stats. Warriors get high Str, Mages high Int,
        // etc. Scales by tier — Novices are weaker than Grandmasters.
        // -------------------------------------------------------------------
        private void ApplyClassStats()
        {
            var (str, dex, intel) = BotSkillTemplates.StatTargets(Class, SkillTier);

            RawStr = str;
            RawDex = dex;
            RawInt = intel;

            // Refill HP/Stam/Mana from the new maxes.
            Hits = HitsMax;
            Stam = StamMax;
            Mana = ManaMax;
        }

        // -------------------------------------------------------------------
        // Equip the Order/Chaos shield for faction guild members. Shields
        // live on the TwoHanded layer: an existing rolled shield is
        // replaced; a two-handed weapon wins (an archer of DOOM carries no
        // shield but still fights the war).
        // -------------------------------------------------------------------
        public void EquipFactionShield()
        {
            var faction = BotGuilds.Get(BotGuildIndex)?.Faction ?? BotFaction.None;
            if (faction == BotFaction.None)
            {
                return;
            }

            if (FindItemOnLayer(Layer.TwoHanded) is Items.BaseShield rolled)
            {
                rolled.Delete();
            }
            if (FindItemOnLayer(Layer.TwoHanded) != null)
            {
                return; // two-handed weapon — no room for a shield
            }

            Item shield = faction == BotFaction.Order
                ? new BotOrderShield()
                : new BotChaosShield();
            if (!EquipItem(shield))
            {
                shield.Delete();
            }
        }

        // -------------------------------------------------------------------
        // Re-derive this bot as a specific class. Used when a spawner pins an
        // artisan to a station (forge -> Smith, dock -> Fisherman, tailor shop
        // -> Tailor): the constructor already rolled a RANDOM class/outfit, so
        // we override the identity and rebuild skills, stats, and gear to
        // match. SkillTier is kept as rolled, for a natural spread of
        // novice-to-grandmaster artisans.
        // -------------------------------------------------------------------
        public void ReinitializeAsClass(BotClass cls)
        {
            Class = cls;

            StripGearAndPack();
            ResetSkills();

            ApplyClassSkills();
            ApplyClassStats();
            EquipmentTable.RollOutfit(this, Class, SkillTier);
        }

        // Zero every skill before a re-derive — without this, the old
        // class's template bleeds through (a re-derived smith keeping GM
        // Provocation from its constructor roll).
        private void ResetSkills()
        {
            for (int i = 0; i < Skills.Length; i++)
            {
                Skills[i].Base = 0;
            }
        }

        // Remove constructor-rolled equipment and backpack contents (they were
        // rolled for the random class) so a re-derive doesn't stack gear or
        // leave mismatched loot. The backpack container itself is kept.
        private void StripGearAndPack()
        {
            var equipped = new List<Item>();
            foreach (var it in Items)
            {
                if (it != Backpack && it.Layer != Layer.Bank)
                {
                    equipped.Add(it);
                }
            }
            foreach (var it in equipped)
            {
                it.Delete();
            }

            if (Backpack != null)
            {
                foreach (var it in new List<Item>(Backpack.Items))
                {
                    it.Delete();
                }
            }
        }

        public PlayerBot(Serial serial) : base(serial)
        {
            // State restored in Deserialize.
        }

        public override bool ShouldCheckStatTimers => false;

        // -------------------------------------------------------------------
        // Bots phase through crowds. The engine's CheckShove requires FULL
        // stamina to step onto an occupied tile — so every road-weary bot
        // bounced off the permanent bank-plaza crowds forever (the Britain
        // stuck cluster: 500+ pacing events per soak at the bank streets).
        // Bots ignore bodies when THEY move; a real player shoving a bot
        // still pays the normal rules.
        // -------------------------------------------------------------------
        public override bool CheckShove(Mobile shoved) => true;

        // -------------------------------------------------------------------
        // Doors open when a bot walks into them — the same thing the client's
        // "auto-open doors" checkbox has always done for players.
        //
        // Every behaviour steps through Mobile.Move, so overriding it here is
        // the one place that covers all of them: dungeon rooms, the Britain
        // bank, shops, houses. Before this, doors were only opened by
        // Traveler stuck-recovery and by Shoppers on the way in, which left
        // bots shut inside rooms until a rescue fired.
        //
        // Mobile.Move only returns false for a genuinely blocked STEP — a
        // turn always succeeds — so by the time we get here the bot is
        // already facing d, which is the tile DoorHelper looks at.
        // -------------------------------------------------------------------
        public override bool Move(Direction d)
        {
            if (base.Move(d))
            {
                return true;
            }

            // Blocked. If it was a closed door, open it and take the step.
            return DoorHelper.TryOpenAhead(this, d) && base.Move(d);
        }

        // -------------------------------------------------------------------
        // Order vs Chaos is LEGAL combat (IDEAS 2.1 phase 3): harming an
        // opposing faction bot is not a criminal act, so no gray flag and —
        // crucially — no guard whack. This is what lets shield wars rage in
        // the middle of Britain while the guards watch, exactly as T2A did.
        // -------------------------------------------------------------------
        public override bool IsHarmfulCriminal(Mobile target)
        {
            if (target is PlayerBot other &&
                (BotFactionWar.AreEnemies(this, other) ||
                 BotDuelManager.AreDueling(this, other)))
            {
                return false;
            }
            return base.IsHarmfulCriminal(target);
        }

        // -------------------------------------------------------------------
        // Show the guild tag exactly where real guilds show theirs: appended
        // to the name-line suffix ("Corwin the Grandmaster Swordsman [UDL]").
        // Composes with PlayerMobile's own suffixes (ethics etc) via base.
        // -------------------------------------------------------------------
        public override string ApplyNameSuffix(string suffix)
        {
            var guild = BotGuilds.Get(BotGuildIndex);
            if (guild != null)
            {
                suffix = string.IsNullOrWhiteSpace(suffix)
                    ? $"[{guild.Tag}]"
                    : $"{suffix} [{guild.Tag}]";
            }
            return base.ApplyNameSuffix(suffix);
        }

        // -------------------------------------------------------------------
        // OnAfterSpawn — called by Mobile after a spawner places this bot in
        // the world. By now this.Spawner is set.
        //
        // We do three things here:
        //   1. Copy our behavior from the spawner (if it's a PlayerBotSpawner)
        //   2. For BankSitters, 80% try to relocate next to a wall (their
        //      back to the wall, face the crowd) — the classic AFK macroer
        //      look. The other 20% stand in the open.
        //   3. Pick a camera-facing direction so we don't all stare at the
        //      back wall.
        //
        // Manually-spawned bots (via [SpawnTestBot or [add) have no Spawner;
        // they get the default Idle behavior and a random camera-facing
        // direction. No wall-hug attempt.
        // -------------------------------------------------------------------
        public override void OnAfterSpawn()
        {
            base.OnAfterSpawn();

            string behaviorName = null;
            if (Spawner is PlayerBotSpawner pbs)
            {
                behaviorName = pbs.BehaviorName;

                // An artisan spawner pins a specific class by encoding it in
                // the behavior name as "Crafter:<Class>" (e.g. "Crafter:Smith"),
                // so a forge gets Smiths, a dock Fishermen, a tailor shop
                // Tailors. Split the class off so the registry sees a plain
                // behavior name.
                BotClass? forcedClass = null;
                int colon = behaviorName?.IndexOf(':') ?? -1;
                if (colon > 0)
                {
                    if (Enum.TryParse<BotClass>(behaviorName[(colon + 1)..], true, out var c))
                    {
                        forcedClass = c;
                    }
                    behaviorName = behaviorName[..colon];
                }

                // Pinned artisan: the constructor rolled a random class, so
                // re-derive this bot as the pinned class — correct skills,
                // tool, and starter goods for its station. Do this BEFORE
                // attaching the behavior so the behavior's OnAttached sees the
                // final class (a fisherman, for instance, only relocates to
                // the water's edge when its class is already Fisherman).
                if (forcedClass.HasValue)
                {
                    ReinitializeAsClass(forcedClass.Value);
                }

                Behavior = BehaviorRegistry.Create(behaviorName);
                // Fixed-role bots (placed via the spawn editor) never enter
                // the lifecycle — they stay locked in this behavior. They
                // also don't count toward the session curve (fixtures are
                // furniture, not sessions) — keep the O(1) tally exact.
                LifecycleExempt = Spawner is FixedRoleBotSpawner;
                if (LifecycleExempt)
                {
                    BotSessionManager.FixedRoleCount++;
                }

                // Shoppers pinned at vendor spots spawn straight into the
                // Shopper brain with no visit timer (unlike organic arrivals,
                // which the Traveler handoff stamps). Give them a staggered
                // timed visit so they too break off and travel on, rather than
                // standing at the counter forever. Fixed-role shoppers are
                // deliberately permanent, so skip them. Derived at spawn time
                // like LifecycleExempt — not serialized; refills/restarts
                // re-stamp it on the next OnAfterSpawn.
                if (!LifecycleExempt && Behavior is ShopperBehavior shopper)
                {
                    int minSecs = (int)ShopperBehavior.SpawnVisitMin.TotalSeconds;
                    int maxSecs = (int)ShopperBehavior.SpawnVisitMax.TotalSeconds;
                    shopper.VisitExpiresAt = Core.Now +
                        TimeSpan.FromSeconds(Utility.RandomMinMax(minSecs, maxSecs));
                }
            }

            // PK setup. PKs are strong (mostly Master/Grandmaster), run
            // the era's KILLER templates, and always work as a crew — the
            // whole spawner is one gang keyed to the spawner's serial.
            if (Behavior is PKBehavior pk)
            {
                // The era's PK class mix: the Red Mage (tank mage) was THE
                // classic; field dexxers fill out the gank squad.
                int classRoll = Utility.Random(100);
                Class = classRoll < 40 ? BotClass.Mage
                      : classRoll < 65 ? BotClass.Warrior
                      : classRoll < 85 ? BotClass.Fencer
                      : BotClass.Archer;

                SkillTier = Utility.RandomDouble() < 0.7
                    ? BotSkillTier.Grandmaster
                    : BotSkillTier.Master;

                // Full re-derive: the constructor rolled a random class's
                // gear and skills. Strip both, apply the PK template (Red
                // Mage / field PK with Tracking+Hiding), re-stat, re-outfit
                // as the real class — then the murderer's extras (explosion
                // pots, trapped pouches).
                StripGearAndPack();
                ResetSkills();
                ApplySkillTemplate(BotSkillTemplates.RollPKTemplate(Class));
                ApplyClassStats();
                EquipmentTable.RollOutfit(this, Class, SkillTier);
                if (Class == BotClass.Mage)
                {
                    EquipmentTable.EquipTankMageWeapon(this, SkillTier);
                }
                EquipmentTable.AddPKExtras(this, SkillTier);

                // Reds run in gangs, period — pack hunts need a crew.
                if (Spawner != null)
                {
                    pk.GangId = (int)Spawner.Serial.Value;
                }

                Console.WriteLine(
                    $"[pk] {Name} spawned: {SkillTier} " +
                    $"{BotClassHelper.DisplayName(Class)} (gang {pk.GangId})");
            }

            // A bot that spawns INSIDE a dungeon is a dungeon crawler, no
            // matter what the spawner asked for — a Wanderer/Idler pacing a
            // dungeon corridor forever reads as broken. Deliberate dungeon
            // residents are exempt: fixed-role bots (spawn editor) and PKs
            // (dungeon PKs hunt there on purpose). The crawler spawns
            // context-less and derives its dungeon+level from the nearest
            // interior point on its first tick (TryRecoverContext).
            if (!LifecycleExempt &&
                Behavior is not PKBehavior &&
                Behavior is not DungeonCrawlerBehavior &&
                DungeonRegistry.IsInDungeon(this))
            {
                Behavior = new DungeonCrawlerBehavior();
            }

            // BankSitters: most of them lean against the nearest wall.
            // Anyone else (Wanderers, Idle): just stand and face the camera.
            bool hugged = false;
            if (behaviorName == "BankSitter" && Utility.RandomDouble() < 0.80)
            {
                hugged = TryHugNearbyWall();
            }

            if (!hugged)
            {
                Direction = RandomCameraFacingDirection();
            }

            // Roll for a mount. Most bots are mounted (varied horses,
            // ostards, llamas). Mounts despawn at death + delete.
            // BankSitters who are wall-hugging skip the mount (they're
            // standing pressed against a wall, mount would clip weirdly).
            // Fishermen never mount — they work the dock edge on foot;
            // casting a line from horseback looks absurd.
            // Miners and Lumberjacks never mount either: their animal is
            // the PACK beast (which nobody can ride) — they walk out to
            // the site leading it, like every real gatherer did.
            if (!hugged && Class != BotClass.Fisherman &&
                !BotClassHelper.IsGatherer(Class) &&
                Utility.RandomDouble() < 0.70)
            {
                BotMountHelper.TryMountRandom(this);
            }
        }

        // -------------------------------------------------------------------
        // OnBeforeDeath — called just before the bot dies. Use this to
        // dismount + delete the mount so it doesn't become an orphan.
        // -------------------------------------------------------------------
        // -------------------------------------------------------------------
        // A real player answers when spoken to. Same per-listener pipeline
        // vendors use; all the who/when/what rules live in the responder.
        // HandlesOnSpeech is the gate that makes the engine deliver speech
        // here at all (default false) — kept narrow: only real players'
        // words are worth processing, never other bots' barks.
        // -------------------------------------------------------------------
        public override bool HandlesOnSpeech(Mobile from) =>
            from != null && from != this && from.Player && from is not PlayerBot;

        public override void OnSpeech(SpeechEventArgs e)
        {
            base.OnSpeech(e);
            BotSpeechResponder.Handle(this, e);
        }

        public override bool OnBeforeDeath()
        {
            BotMountHelper.DismountAndDelete(this);
            return base.OnBeforeDeath();
        }

        // -------------------------------------------------------------------
        // OpenTrade — a bot can be the other half of a real trade window.
        //
        // Stock bails here when either side has no NetState, which for a
        // bot is always. A hawker holding stock takes the drag instead and
        // runs the sale; everything else falls through to stock behaviour
        // (which declines, and the dropped item bounces back).
        // -------------------------------------------------------------------
        public override bool OpenTrade(Mobile from, Item offer = null) =>
            BotTradeWindow.TryOpen(this, from, offer) || base.OpenTrade(from, offer);

        // -------------------------------------------------------------------
        // OnDeath — record the death in the shard's event journal so bank
        // gossip can retell it. A murder by a red is bigger news than a
        // dungeon death, so it gets its own event type.
        // -------------------------------------------------------------------
        public override void OnDeath(Container c)
        {
            var killer = LastKiller;

            // A reflected spell (Magic Reflection bounces the bot's own
            // cast back) or a backfire can make a bot its own killer —
            // "Wulfgar was killed by Wulfgar" reads like a bug in the feed
            // and in gossip. A self-kill is an unattributed death.
            bool selfKill = killer == this;

            string type = !selfKill && killer is PlayerBot pk
                ? pk.Behavior is PKBehavior ? "pk"
                : BotFactionWar.AreEnemies(this, pk) ? "faction"
                : "pk"
                : "death";
            BotEventJournal.Record(type, this,
                selfKill ? "" : killer?.Name ?? "");

            base.OnDeath(c);

            // The corpse exists now — start the ghost/res/corpse-run story.
            BotDeathManager.OnBotDeath(this, killer);
        }

        // -------------------------------------------------------------------
        // OnAfterDelete — called when the bot is fully removed from the
        // world. Belt + suspenders: if OnBeforeDeath didn't fire (e.g. the
        // bot was [removed by an admin while alive), still clean up the
        // mount here.
        // -------------------------------------------------------------------
        public override void OnAfterDelete()
        {
            BotMountHelper.DismountAndDelete(this);
            BotPackAnimals.Release(this);
            BotCombatPets.Release(this);
            if (LifecycleExempt)
            {
                BotSessionManager.FixedRoleCount--;
            }
            NamePool.Release(Name);
            base.OnAfterDelete();
        }

        // -------------------------------------------------------------------
        // Wall-hugger: scan the 5-tile-radius around us for tiles that are
        // (a) walkable and (b) have at least one impassable neighbor (i.e.
        // a wall). If found, teleport there and face AWAY from the wall —
        // back to the wall, head toward the room. Returns true on success.
        //
        // Uses Map.CanFit as the universal "walkable" probe rather than
        // poking TileFlags directly — captures static walls, land
        // impassables, and furniture all in one check.
        // -------------------------------------------------------------------
        private bool TryHugNearbyWall()
        {
            if (Map == null || Map == Map.Internal)
            {
                return false;
            }

            // Range we'll scan for a suitable wall-adjacent tile.
            const int scanRange = 5;
            // Height needed for a person to stand here. 16 matches what
            // ModernUO uses for the standard "can a mobile stand here" check.
            const int height = 16;

            // Build a list of (candidate, wallDirection) pairs.
            var candidates = new System.Collections.Generic.List<(Point3D loc, Direction wallDir)>();

            for (int dx = -scanRange; dx <= scanRange; dx++)
            {
                for (int dy = -scanRange; dy <= scanRange; dy++)
                {
                    if (dx == 0 && dy == 0) continue;

                    int x = Location.X + dx;
                    int y = Location.Y + dy;
                    int z = Location.Z;

                    // The tile itself must be standable.
                    if (!Map.CanFit(x, y, z, height, checkBlocksFit: false, checkMobiles: true))
                    {
                        continue;
                    }

                    // Check the 4 cardinal neighbors. If any is impassable,
                    // this tile is wall-adjacent. We track which side the
                    // wall is on so we can face away from it.
                    Direction? wallSide = null;
                    if (!Map.CanFit(x,     y - 1, z, height, false, false)) wallSide = Direction.North;
                    else if (!Map.CanFit(x + 1, y,     z, height, false, false)) wallSide = Direction.East;
                    else if (!Map.CanFit(x,     y + 1, z, height, false, false)) wallSide = Direction.South;
                    else if (!Map.CanFit(x - 1, y,     z, height, false, false)) wallSide = Direction.West;

                    if (wallSide.HasValue)
                    {
                        candidates.Add((new Point3D(x, y, z), wallSide.Value));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            var pick = candidates[Utility.Random(candidates.Count)];
            MoveToWorld(pick.loc, Map);

            // Face AWAY from the wall — back to the wall, face the room.
            // Direction enum: 0=North, 1=Right, 2=East... add 4 mod 8 for opposite.
            Direction = (Direction)(((int)pick.wallDir + 4) & 7);
            return true;
        }

        // South-facing for the offline single-player viewing angle.
        // ClassicUO's default camera looks down at South/SE/SW. Bots that
        // face one of these directions appear to look "at the player".
        private static readonly Direction[] CameraFacing =
        {
            Direction.South, Direction.South,    // weighted toward straight south
            Direction.Right,                     // SE
            Direction.Down                       // SW
        };

        private static Direction RandomCameraFacingDirection()
        {
            return CameraFacing[Utility.Random(CameraFacing.Length)];
        }

        // ---- Serialization ----
        //
        // Version history:
        //   0 — IsBot only
        //   1 — IsBot, behavior name
        //   2 — IsBot, behavior name, personality, phase started at
        //   3 — IsBot, behavior name, personality, phase started at, Class, SkillTier
        //
        // SpeechHue is handled by Mobile.Serialize / Mobile.Deserialize
        // automatically; we don't touch it here.
        //
        // For bots saved at v0 that load under this code, behavior defaults
        // to "Idle" (the safe fallback). Personality is default (unassigned);
        // the lifecycle manager will roll a fresh one when it first sees them.
        // Class defaults to Warrior, SkillTier defaults to Apprentice (mid-low)
        // for bots loaded from v0-v2 saves — they don't get a re-roll because
        // their equipment is already on them from the earlier construction.

        public override void Serialize(IGenericWriter writer)
        {
            base.Serialize(writer);

            writer.Write(6);                                       // version
            writer.Write(IsBot);
            writer.Write(_behavior?.SerializableName ?? "Idle");
            Personality.Write(writer);
            writer.Write(PhaseStartedAt);
            writer.Write((byte)Class);
            writer.Write((byte)SkillTier);
            writer.Write((byte)CrafterSpec);                       // v5 layout (3 subtypes)
            writer.Write(BotGuildIndex);                           // v6
        }

        public override void Deserialize(IGenericReader reader)
        {
            base.Deserialize(reader);

            int version = reader.ReadInt();

            string behaviorName = "Idle";

            if (version >= 0)
            {
                IsBot = reader.ReadBool();
            }
            if (version >= 1)
            {
                behaviorName = reader.ReadString();
            }
            if (version >= 2)
            {
                Personality = BotPersonality.Read(reader);
                PhaseStartedAt = reader.ReadDateTime();
            }
            if (version >= 3)
            {
                Class     = (BotClass)reader.ReadByte();
                SkillTier = (BotSkillTier)reader.ReadByte();
            }
            else
            {
                // Pre-v3 bots: assign reasonable defaults. Their equipment
                // is whatever was saved with them, and the skills they had
                // (if any) are preserved by Mobile.Deserialize. We don't
                // re-roll skills here — that'd change loaded bots in a way
                // that surprises the user.
                Class     = BotClass.Warrior;
                SkillTier = BotSkillTier.Apprentice;
            }
            if (version >= 5)
            {
                // Current three-subtype layout — read directly.
                CrafterSpec = (CrafterType)reader.ReadByte();
            }
            else if (version == 4)
            {
                // Pre-rebuild seven-subtype layout — remap retired subtypes
                // (Tinker/Inscriptionist/Carpenter/Bowcrafter) onto a valid
                // current one so no bot loads with a meaningless spec.
                CrafterSpec = CrafterTypeHelper.MigrateLegacyByte(reader.ReadByte());
            }
            else
            {
                // Pre-v4 bots: no spec stored — roll one so the field is valid.
                CrafterSpec = CrafterTypeHelper.RollRandom();
            }
            if (version >= 6)
            {
                BotGuildIndex = reader.ReadInt();
            }
            else
            {
                // Pre-guild bots roll membership on load so an old save
                // still produces a guilded population.
                BotGuildIndex = BotGuilds.RollMembership();
            }

            // Migrate legacy Crafter-class bots (saved before the split into
            // separate Smith/Tailor/Fisherman classes) onto their concrete
            // class, using the old subtype. After this, BotClass.Crafter is
            // never live — it exists only as this migration's source value.
            if (Class == BotClass.Crafter)
            {
                Class = CrafterSpec switch
                {
                    CrafterType.Tailor    => BotClass.Tailor,
                    CrafterType.Fisherman => BotClass.Fisherman,
                    _                     => BotClass.Smith,
                };
            }

            // Heal pre-v20c bots whose m_Player flag was never set. Without
            // this, dungeon entrance teleporters refuse to fire on them.
            // Setting Player on an already-Player bot is a no-op, so this
            // is safe to apply to every load.
            Player = true;

            // Same idea for hunger/thirst — pre-v20d bots may have nonzero
            // hunger/thirst from earlier runs. Reset to max each load.
            Hunger = 20;
            Thirst = 20;

            // Register the loaded name so fresh spawns can't duplicate it.
            // (Loaded bots are normally purged at startup anyway, but the
            // claim keeps the registry honest for however long they live.)
            NamePool.Claim(Name);

            // Note: no manual Title set. The paperdoll renders the title
            // from the bot's highest skill above 50 (UO's normal behavior).
            // A GM Swordsman naturally shows "the Grandmaster Swordsman".
            // Pre-v3 bots may have no real skills set, so they'll show as
            // just their name — that's fine, they'll get fresh skills on
            // any future re-spawn.

            Behavior = BehaviorRegistry.Create(behaviorName);
        }
    }

    // -----------------------------------------------------------------------
    // [SpawnTestBot — admin command that drops a PlayerBot at your feet.
    // -----------------------------------------------------------------------
    public static class SpawnTestBotCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("SpawnTestBot", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("SpawnTestBot")]
        [Description("Spawns a single PlayerBot at your location with Idle behavior.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null || from.Map == null)
            {
                return;
            }

            var bot = new PlayerBot();
            bot.MoveToWorld(from.Location, from.Map);

            from.SendMessage($"Spawned {bot.Name} ({(bot.Female ? "F" : "M")}, hue {bot.SpeechHue}). Use [SetBehavior to give it a job.");
        }
    }

    // -----------------------------------------------------------------------
    // [SetBehavior — admin command that swaps a PlayerBot's behavior.
    //   Usage:  [SetBehavior <name>      → target a PlayerBot
    //   Names:  idle, wander
    // -----------------------------------------------------------------------
    public static class SetBehaviorCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("SetBehavior", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("SetBehavior <name>")]
        [Description("Targets a PlayerBot and swaps its behavior. Known names: idle, wander.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null)
            {
                return;
            }

            if (e.Arguments.Length == 0)
            {
                from.SendMessage("Usage: [SetBehavior <name>");
                from.SendMessage("Known behaviors: " + string.Join(", ", BehaviorRegistry.KnownNames));
                return;
            }

            var name = e.Arguments[0];
            from.SendMessage($"Target a PlayerBot to assign behavior '{name}'.");
            from.Target = new SetBehaviorTarget(name);
        }

        private class SetBehaviorTarget : Target
        {
            private readonly string _behaviorName;

            public SetBehaviorTarget(string behaviorName) : base(12, false, TargetFlags.None)
            {
                _behaviorName = behaviorName;
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                if (targeted is not PlayerBot bot)
                {
                    from.SendMessage("That's not a PlayerBot.");
                    return;
                }

                var behavior = BehaviorRegistry.Create(_behaviorName);
                bot.Behavior = behavior;
                from.SendMessage($"{bot.Name} now has behavior: {behavior.SerializableName}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // [ClearBots — admin command that deletes PlayerBots.
    //   [ClearBots          - deletes all PlayerBots within 30 tiles
    //   [ClearBots <range>  - within <range> tiles
    //   [ClearBots all      - deletes ALL PlayerBots in the world
    //
    // Useful for dev: spawn 20 test bots, iterate behavior, wipe with one
    // command. Doesn't touch monsters, NPCs, or your own character — only
    // bots that are instances of PlayerBot.
    // -----------------------------------------------------------------------
    public static class ClearBotsCommand
    {
        public static void Configure()
        {
            CommandSystem.Register("ClearBots", AccessLevel.GameMaster, OnCommand);
        }

        [Usage("ClearBots [range | 'all']")]
        [Description("Deletes PlayerBots. Default range 30 tiles; 'all' wipes them globally.")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null || from.Map == null)
            {
                return;
            }

            // Parse the argument
            bool worldwide = false;
            int  range     = 30;

            if (e.Arguments.Length > 0)
            {
                var arg = e.Arguments[0];
                if (string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase))
                {
                    worldwide = true;
                }
                else if (int.TryParse(arg, out var n) && n > 0)
                {
                    range = n;
                }
                else
                {
                    from.SendMessage("Usage: [ClearBots [range | 'all']");
                    return;
                }
            }

            // Snapshot mobiles into a list — deletion during iteration is
            // unsafe in some collection types.
            var victims = new System.Collections.Generic.List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted)
                {
                    continue;
                }
                if (!worldwide)
                {
                    if (bot.Map != from.Map) continue;
                    if (!bot.InRange(from.Location, range)) continue;
                }
                victims.Add(bot);
            }

            foreach (var bot in victims)
            {
                bot.Delete();
            }

            if (worldwide)
            {
                from.SendMessage($"Cleared {victims.Count} PlayerBots from the world.");
            }
            else
            {
                from.SendMessage($"Cleared {victims.Count} PlayerBots within {range} tiles.");
            }
        }
    }
}
