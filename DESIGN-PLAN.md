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
