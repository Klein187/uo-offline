# Design Plan — Systems To Build

Designed, not yet built. Two major systems captured here: **Dungeons** and the
**Crafter rebuild**. Both lean heavily on systems that already work (arrival
points, zones, moongate-style teleport, class/skill-tier weighting), so each is
far less code than it sounds.

---

# 1. Dungeons

## Authoring (two existing map tools, distinguished by type, tied by dungeon name)
- **Arrival-point creator gains create-on-empty-ground** (mirroring the area
  absorb/create): drop an arrival point on a teleporter tile, pick a type from
  a dropdown — "Dungeon Entrance" / "Dungeon Exit" (and level-transition
  Descend/Ascend) each prompt for a **dungeon name**. Mints a destination on
  that tile. (Clicking near an existing dot still just attaches, as now.)
- **Area painter** handles dungeon combat areas (regions), tagged with the
  dungeon name — reuses absorb/create.
- **Dungeon tag** (like City) scopes everything: a crawler only sees/rolls its
  own dungeon's points and areas.

## Entry
- Surface entrance = a rollable destination (class-weighted, like graveyards)
  with an arrival point **on the surface teleporter tile**. Combat-bots roll
  it, route there, step on it.
- Stock UO teleporter moves the bot inside (no bot-side teleport code needed).
- Bot lands in the dungeon's **entry area** (painted, dungeon-tagged) →
  "entered area → become DungeonCrawler" handoff fires. Crawler wakes up
  already inside, in the correct coordinate pocket.
- **Timing matters:** become the crawler AFTER the teleport (on the dungeon
  side), never before — UO dungeons sit at wildly different coords, so a
  pre-teleport crawler would try to route across the void. The
  entered-an-area-tagged-for-this-dungeon trigger handles this for free.

## The crawl (dumb + local; depth EMERGES from weights)
- Crawler repeatedly picks a next point among **its dungeon's points**
  (weighted, **skill-aware**) and roams/fights toward it.
- **Level-transition teleporters are just points in that pool.** Rolling a
  down-teleporter and stepping on it moves the bot to the next level; it then
  rolls from that level's reachable points (re-scoped on arrival via the
  level's landing area, same entered-area trick).
- **Skill-weighting on deeper-level transition points** keeps low-skill bots
  shallow, high-skill bots deep. Population thinning with depth is EMERGENT —
  not assigned. No target-depth planning, no global pathing. The bot is dumb
  and local; the interesting global behavior falls out of the weights.
- **Combat is placeholder** — build the crawl loop on working nav + crude
  fighting, improve combat later without touching navigation. (Adventurer
  combat "isn't the best"; rework it separately.)

## Exit (per-level "go up one" reflex; full climb-out EMERGES)
- Run-timer expires → crawler enters **exit-mode**.
- Exit-mode goal: seek **THIS level's** up/exit teleporter, step on it. Lands
  one level up, still in exit-mode, repeats. Climbs out one floor at a time
  with no whole-journey awareness — mirror image of descent.
- On the final surface-side teleporter → now on the surface → revert to
  **Traveler**, resume life.

## Loop-breaker (the key invariant)
- Crawler-conversion only happens to **non-crawlers**; de-conversion only
  happens **on the surface**. A crawler stays a crawler the entire time inside
  (including the climb out), so walking back through any landing/entry area
  never re-grabs it. This is what prevents the "exit through the entry area →
  re-converted → infinite loop" failure.

## Death (interim)
- Respawn at **born-location**. Resurrection shrines someday (big project,
  deferred).

## Teleport mechanic — fork MoongateTravel.BeginTrip
The working moongate code is the proven template (freeze Traveler → effect →
DelayCall → MoveToWorld with spread → swap behavior on far side). Dungeon
version: **fixed target instead of random**, and **behavior-swap chosen per
transition** (entrance → crawler, level→level → stay crawler/re-scope, final
exit → Traveler). Could nearly fork MoongateTravel.cs into DungeonTravel.cs and
change just those two things.

## Why it's less code than it sounds
Reuses: weighted-destination roll, arrival-point teleporters, dungeon-tag
scoping, area-entry handoff, moongate teleport pattern. New parts: the
DungeonCrawler behavior (roam + crude fight), a skill term in point-weighting,
and exit-mode's "seek this level's exit" reflex. Scaffolding files already in
the tree: DungeonEntry.cs, BotSkillTemplate.cs, BotSkillTier.cs,
LifecycleTransitions.cs.

---

# 2. Crafter rebuild

**Status:** current CrafterBehavior is a placeholder — rebuild from scratch,
don't extend it.

## Core idea: illusion, not simulation
Crafters don't craft. No ore/ingots/cloth/fishing mechanics. They stand in the
right place, loop a work animation, and *occasionally* an item appears in their
pack via a skill-gated roll. To a passerby it's indistinguishable from real
crafting — all they ever see is the animation + pack contents.

## The four pieces (cleanly separable)
1. **Anchor** — route to a subtype-appropriate destination (class-weighted),
   arrive at an **arrival point** that positions the bot correctly, stay for a
   long working session. Shopper-shaped core (reuse that shell).
   - Smith → at the anvil/forge tile, facing it
   - Tailor → in the tailor shop
   - Fisherman → on a **water-edge dock tile, facing water** (arrival point is
     the key enabler — the dock needs an arrival point on a castable tile)
2. **Loop** — on a cadence, play the work animation (+ optional sound), hold
   pose. Fisherman casts (equipped pole visible), smith hammers, tailor sews.
   Pure visual. (Crafter = Shopper with animation instead of shopping chatter.)
3. **Production roll (the entire "crafting system") — TWO separate rolls:**
   - **Roll 1 — did anything get made this cycle?** Frequency scales with skill
     tier (novice rarely, GM fairly regularly). NOT every cycle.
   - **Roll 2 — what is it?** Heavily weighted toward the COMMON item
     (fish / plain weapon) for EVERYONE. Rare items (treasure map, SOS bottle)
     are only on the table at all at high tiers, and even then a tiny slice.
   - **Critical calibration:** a GM fisherman STILL mostly gets fish. Rough
     feel ~90% fish / 8% minor / 2% map+SOS. Skill shows in the *occasional*
     rare, never a constant stream. Same for smith (mostly regular pieces;
     notable ones stay uncommon).
4. **Starter props** — seed a few thematic items at spawn so a freshly-seen
   crafter already looks the part, plus the **equipped tool** (pole / hammer)
   for the animation.

## Locked decisions
1. **Skill → loot via the existing tier system** (Novice…GM). Each tier has a
   loot table; higher tiers add rare items ON TOP of common ones, but common
   stays dominant even at GM.
2. **Pack management = random sell-off.** Periodically items just vanish ("sold
   to the shop") so packs never fill. Simple, believable, bounded.
3. **Three subtypes now:** Smith (weapons/armor), Tailor (leather/cloth),
   Fisherman (fish + rare sea finds).
4. **Maker's mark = use UO's real one.** GM output gets Exceptional quality +
   the actual "crafted by [name]" property (authentic tooltips). Smith/tailor
   goods only — fisherman output (fish/maps) is NOT marked.

## Subtype = data plugged into one engine
Each subtype is just a table of: (destination type sought, work animation,
tiered loot tables with common-dominant weighting, starter props, equipped
tool, whether output is maker's-markable). Same engine, different data.

## Build order (when ready)
1. **Confirm animations** are triggerable on a PlayerBot — the one real
   unknown. Need fishing-cast and anvil-hammer action IDs via ModernUO's
   Animate/action API; fall back to closest "working" action if unavailable.
   Load-bearing — do this first.
2. **Write the engine** — anchor + loop + two-roll production + sell-off +
   props, parameterized by the per-subtype table.
3. **Define the three subtype tables.**
4. **Place arrival points** (map/data work) — anvil tiles, water-edge dock
   tiles facing water. Behavior is correct without them but bots won't position
   right until painted.

## Why the current one isn't working (diagnosis)
Likely: crafter subtypes not weighting toward their OWN destination type
(fishermen may not prefer docks), and/or docks lacking water-edge arrival
points so even on arrival a bot can't position to fish. Rebuild + arrival
points address both. Fisherman-at-docks was the specific symptom.

## Tuning knobs (editable table, dial in-game later)
- Production frequency per tier (novice rare → GM regular)
- Loot-table weights per tier (keep common dominant; rare slice tiny even at GM)
- Sell-off frequency / pack cap

---

# 3. Spawn Editor (map editor expansion)

Turn the map editor into the visual spawn-authoring tool for the whole world:
place spawns of every kind, filter the view by kind, and generate real
in-game spawners from the placed data. Eventually ingest Nerun's existing
~1700 spawns to see/edit them too.

## Phasing (build in this order; each is useful alone)
- **Phase 1 — ✅ DONE (v26):** place your OWN spawns + filters + generate
  in-game. `spawns.json` (kind/what/count/range/timer) at `Data/CustomSpawns/`,
  serve_map `spawn_*` endpoints, colored filterable layers, and
  `[GenerateCustomSpawners` (Monster/NPC/Vendor → ModernUO Spawner;
  PlayerBotFixed → `FixedRoleBotSpawner`; PlayerBotLifecycle → PlayerBotSpawner).
  Type dropdown from `spawn_types.json` (generated by `gen_spawn_types.py`).
- **Phase 2 — ✅ DONE (v26):** live population view. `[LiveMap` snapshots
  entities to `Data/Live/entities.json`; serve_map `/live.json`; the editor's
  "Live entities" layer polls it, colors bots by behavior, filters by kind,
  has a density heatmap, click-to-inspect, and draws a selected bot's route.
  (Live-query bridge = the game writes a snapshot file the editor polls.)
  Bonus tools shipped alongside: a waypoint-coverage heatmap, and "Reload in
  game" / "Regenerate bots in game" buttons (file-token bridge via
  `EditorReloadWatcher`).
- **Phase 3 (later, NOT DONE):** ingest Nerun's UOClassic.map so all ~1700
  existing spawns appear in the editor and can be moved/edited/deleted. Biggest
  piece: parse Nerun's .map format, round-trip edits back out, decide whether
  editing Nerun's file or adopting its spawns into our own system.

## Phase 1 detail

### Spawn kinds (each a colored, filterable layer)
- **PlayerBot — fixed-role:** a bot locked to one behavior forever (e.g. a
  BankSitter that NEVER transitions via the lifecycle). Place "a permanent
  banker here." No lifecycle ticking on it.
- **PlayerBot — lifecycle:** a normal bot that transitions through behaviors
  (the population/lifecycle system). Placing these seeds WHERE bots are born /
  population density (feeds the idea of "20 bots originate in Trinsic, 5 in
  the countryside"). These roam — the point is an origin/seed, not a tether.
- **Monster:** ModernUO Spawner with creature type(s), count, homerange,
  respawn timer. Stays near its point like a normal spawner.
- **NPC:** non-hostile townsfolk types.
- **Vendor:** shopkeeper types.

### Filters (the requested view control)
Toggle layers independently: PlayerBots-only / Monsters-only / NPCs / Vendors
/ all. Each kind a distinct color+icon (e.g. bots blue, monsters red, NPCs
yellow, vendors green). Same filter system reused later for live entities
(Phase 2) and Nerun spawns (Phase 3) — one filter UI, multiple data sources.

### Spawn data model (spawns.json — one structure for all kinds)
- **Location** (X, Y, Z) — clicked tile
- **Kind** — PlayerBotFixed / PlayerBotLifecycle / Monster / NPC / Vendor
  (drives generator type + filter layer + color)
- **What** — specific type(s): "Dragon", "Orc", vendor type; for fixed bots a
  behavior (BankSitter, etc.); for lifecycle bots maybe a class/tier spec
- **Count** — how many
- **Range** — spawn radius (homerange)
- **Timer** — respawn delay
- **Source** (Phase 3) — "custom" vs "nerun", to distinguish/filter additions

### Getting spawns into the game
JSON-file + reload-command pattern (fits destinations/waypoints/zones):
editor writes spawns.json → `[GenerateCustomSpawners` reads it → creates the
right object per kind:
- Monster/NPC/Vendor → a ModernUO `Spawner` (match its real fields).
- PlayerBot-fixed → spawn the bot, set its behavior, and DON'T register it
  with the lifecycle manager (so it never transitions).
- PlayerBot-lifecycle → feed the population/lifecycle system this as a spawn
  origin / seed point.

### Reconnaissance needed before building (don't guess — read the codebase)
1. ModernUO `Spawner` fields (the Nerun importer creates these — reference it).
2. How PlayerBot spawners differ from monster Spawners (PlayerBotSpawner /
   population system) — fixed vs lifecycle bots generate DIFFERENT things.
3. Dump the valid spawnable type names (every Mobile type) to a file the
   editor reads for its "what spawns here" dropdown.
4. (Phase 3) Nerun's .map line format, for parse + write-back.

### Build order (Phase 1)
1. Recon (the 4 items above) — pull real spawner format + type list first.
2. Spawn data model + spawns.json.
3. Editor placement UI (click tile → dialog: kind/what/count/range/timer) +
   colored filterable layers.
4. `[GenerateCustomSpawners` command (per-kind generation).
5. Filter UI.

## Other map-editor tools brainstormed (capture for later)
High value-for-effort, roughly ranked:
- **Component coloring** — color waypoints by connected-component ID; islands,
  disconnected pockets, phantom bridges become visible at a glance. (We already
  compute components — cheap. Would've solved the island-edge debugging
  instantly.)
- **Live bot positions + paths** — show bots at real positions colored by
  behavior; draw a selected bot's planned route. Turns the editor into a live
  debugger ("why is this bot stuck" = a glance).
- **Coverage heatmap** — shade by distance-to-nearest-waypoint; under-covered
  regions (where bots strand) glow. Shows where to paint next.
- **Bulk waypoint stamping / auto-road-trace** — drag a line of auto-connected
  waypoints along a road, or auto-suggest waypoints by following road tiles.
  Big speedup for expanding to new cities.
- **Lint button** — run all checks (orphan waypoints, far destinations,
  disconnected components, arrival points on unwalkable tiles) → clickable
  problem list. Catches strand-causing data bugs before [Reload.
- **QoL:** search/jump-to-name, undo/redo, layer toggles, coordinate readout.
- **Reload-from-editor button** — trigger [ReloadWaypoints etc. on the running
  shard from the editor, no alt-tab.

---

# 4. Live Events

Random (and manually-triggerable) dramatic events that drop into the living
world: a zombie invasion from the Britain graveyard, a balron roaming Trinsic,
an evil mage leading an undead army, a lich lord, etc. The ambient bot life is
the world's everyday texture; events are the "something is HAPPENING"
punctuation. The magic: events disrupt a world already full of autonomous bots,
and the bots REACT — adventurers run to fight, the ordinary rhythm breaks and
then restores. Emergent battles, not scripted set-pieces.

## Core pattern: one engine, many data-defined event templates
Every example (graveyard zombies, Trinsic balron, mage + undead army, lich
lord) is the same skeleton: **spawn a hostile force at a location -> it does
something threatening -> the world reacts -> it resolves -> cleanup.** So:
one event ENGINE, many event TEMPLATES as data (same one-engine-many-instances
pattern as dungeons and crafters).

## Anatomy of an event
1. **Trigger** — random timer rolls an event (VERY low frequency), weighted so
   bigger events are rarer. AND manual `[TriggerEvent <name>` for testing / GM
   fun. Both supported.
2. **Spawn** — the hostile force appears at a location with a composition
   (20 zombies; 1 lich + 30 undead; a lone balron). Events are essentially
   temporary dramatic spawns with behavior attached — reuses the spawn system.
3. **Behavior / objective** — what the force DOES (this is what makes it an
   event, not a monster pile):
   - **Town invasion:** horde advances toward the town center / vendors,
     attacking NPCs and bots on the way.
   - **Roaming boss:** a balron just wanders the city menacingly, attacking
     what it meets.
   - **Army with a leader:** undead follow the lich/mage; killing the leader
     scatters/ends it.
4. **World reaction** — combat-capable bots (Adventurers) near the event NOTICE
   and engage; town guards too. Turns the event into an emergent battle a
   player walks into. ("Guards alone won't be enough" — bots make the
   difference.)
5. **Resolution** — ends by: force defeated; objective reached (horde trashes
   the vendors, then despawns — bad guys "win"); timer expiry (never-defeated
   event despawns so it doesn't linger); or leader killed (army events).
6. **Cleanup** — despawn leftover creatures, reset for the next event.
7. **Announcement** — town criers / server message: "The dead rise in Britain's
   graveyard!" Alerts the player something's happening and where. (Town criers
   already exist — they can announce.)

## Locked decisions
1. **Triggering: both random AND manual.** Random ambient events for the
   living-world feel; `[TriggerEvent` for testing and on-demand drama.
2. **Stakes are REAL: vendors and NPCs actually die.** A zombie can kill the
   Britain banker; the bank is broken until it respawns. NPCs/vendors
   **respawn eventually** (on a timer) so the world heals. Real consequences,
   not theater.
3. **NOT combat-dependent.** Build the event framework now; the battles simply
   get better when combat is reworked later. Framework doesn't wait on combat.
4. **Tier/escalation system, VERY low frequency.** Event magnitude tiers
   (minor / major / raid-tier) with frequency inversely proportional to size,
   and the overall event rate is very low — events are rare punctuation, not
   constant. (Same frequency-vs-magnitude idea as crafter loot tiers.)

## Architecture
**Event engine:** timer/trigger -> pick a weighted event template -> spawn its
force -> run its behavior -> check resolution -> cleanup -> announce.
**Event templates (data):** (where, what force, behavior type, resolution,
announcement). Adding a new event = a new template, no new engine code.

## Ties into other systems
- **Spawn system / spawn editor:** events are dynamic spawns; if the spawn
  system can place a horde, events reuse that.
- **Zones / destinations:** events target authored locations ("spawn at the
  graveyard destination, advance toward the town-center zone"). Existing map
  data feeds events.
- **Bots:** the living population is what makes events feel alive — they react.
- **Vendor/NPC respawn:** needs a respawn-on-timer mechanism for killed
  townsfolk (so real stakes don't permanently break towns).

## Example templates (all the same engine)
- **Graveyard zombie invasion** — undead spawn at Britain graveyard, advance on
  town, attack vendors/bots; resolves on defeat or timer.
- **Roaming balron in Trinsic** — one powerful demon wanders the city
  attacking; resolves on death or timer.
- **Evil mage + undead army** — leader + retinue; army follows leader; killing
  leader ends it.
- **Lich lord** — boss with minions; similar to the mage army.

## Build order (when ready)
1. Event engine (trigger -> spawn -> behavior -> resolution -> cleanup ->
   announce) + ONE simple event (graveyard zombies) to prove the loop.
2. Vendor/NPC death + respawn-on-timer (for real stakes).
3. The advance-on-town behavior (horde pathing toward a target zone, attacking
   en route).
4. More event templates as data.
5. Tier/escalation weighting + the random-trigger timer.
