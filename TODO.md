# TODO

Roadmap for upcoming work on the PlayerBots system. Ordered roughly by priority/dependency — items higher up unlock or simplify items lower down.

---

## In progress

### 1. Expand the waypoint graph beyond Britain

Britain has 35 waypoints and 17 destinations — a real road network across the city's east shops, north shops, virtue path, and southern road toward Trinsic. Other cities have nothing.

**Priority routes** (each batch ~10-20 waypoints + ~5-10 destinations):
- **Trinsic** (extends from existing Britain south road)
- **Yew** (west)
- **Vesper** (east through Cove)
- **Minoc** (northeast)
- **Skara Brae**
- **Moonglow / Magincia** (eastern islands)
- **Jhelom** (south)

Workflow per city:
1. Walk routes between landmarks, `[MarkWay <name>` every 25-30 tiles
2. Walk into shops, taverns, etc., `[MarkSpot <name> <type>` at each
3. Send drafts; integrate into `waypoints.json` and `destinations.json`
4. `[ReloadWaypoints` + `[ReloadDestinations`

**Effort:** Open-ended; each city ~45-60 min of in-game walking plus integration time.

---

### 2. Verify dungeon interior coordinates

8 of 9 dungeons have placeholder coords in `BotPanelActions.DungeonInsideCoords`. Only Despise is real. Without real coords, lifecycle-transitioned Adventurer bots teleport to wrong/invalid spots inside their target dungeon.

**The 8 placeholders that need real coords** (capture via `[go <dungeon>` → walk inside → `[where`):

- Covetous
- Deceit
- Destard
- Hythloth
- Shame
- Wrong
- Ice
- Fire

**Effort:** ~10 minutes of in-game walking per dungeon, ~90 minutes total.

---

### 3. Special dungeon-entrance waypoints

Bots verified able to fire teleporters (`Player = true` fix). To unlock the immersive "walk to dungeon and enter naturally" entry path, the waypoint graph needs nodes positioned ON dungeon entrance teleporter tiles.

**What needs building:**
- New `Type` or `TeleporterDestination` field on waypoints to identify them as teleporter tiles
- `TravelerBehavior` change: when arriving at a teleporter-type waypoint, swap to the destination behavior BEFORE stepping onto the tile. The teleporter fires naturally, bot arrives inside the dungeon, no fake pause needed
- `DungeonEntry.cs` update: prefer this path when graph reaches the entrance; only fall back to teleport-with-pause when graph can't reach

**Data work first:** walk a route from existing graph to one dungeon entrance (Despise likely first since closest), the final waypoint stands EXACTLY on the teleporter tile.

**Effort:** ~3-4 hours code once data is in. Data collection is incremental.

---

## Upcoming

### 4. Death and resurrection

Currently dead Adventurer bots get permadelete + respawn by spawner. More interesting: dead bot ghosts walk to nearest healer, get resurrected, walk back to their corpse, re-equip their loot. Full UO ghost-run experience.

**Scope:**
- Hook PlayerBot.OnDeath to enter "ghost" state instead of deleting
- Ghost behavior: pathfind to nearest Healer destination
- On resurrection, walk back to corpse coord, loot equipment, resume original behavior

**Effort:** Medium — touches OnDeath, ghost-mode PathFollower compatibility, corpse tracking.

---

### 5. More bot behaviors

In rough order of complexity:

**a. Crafter (easy)** — Lingers near a forge/anvil, plays smithing animations periodically, chats about crafting. Pure visual life — no actual crafting needed.

**b. Shopper (easy-medium)** — Walks between vendor destinations within a city, stops at each, plays browse animation, moves on. Adds visible movement to towns.

**c. Mage combat (medium)** — Adventurer variant with spell-casting. Magic Arrow, Lightning, Energy Bolt, Heal. Mana management. Retreats when low mana, not just HP. Built on top of AdventurerBehavior.

**d. Tamer (medium-hard)** — Has a pet that follows and fights. Pet persistence across saves. Pet feeds, pet death handling.

**e. PK / Murderer (hard)** — Targets other bots or players. Goes red. Travels in gangs. Avoids guard zones. Real notoriety system integration.

**f. Thief (hard)** — Steals from other bots / players. Uses Hiding/Stealth. Risk of being caught and made visible. Notoriety system integration.

---

### 6. Random world events

The Karma filter means bots will defend against any negative-Karma monster — including monsters that invade towns. This enables event-style gameplay:

- **Zombie invasion of Britain** — spawn 20 zombies near the bank. Bots engage. Bards may flee, Warriors charge in. Healers patch up the wounded.
- **Orc raid on Yew** — orcs spawn outside the city, march in.
- **Dragon at the gate** — single-boss event. Multiple bots needed to bring it down.

**Scope:** event-triggering admin commands, optional autoscheduler for random events. Bot behavior already supports this — just need the triggers.

**Effort:** Small (events) to medium (scheduler).

---

## Future ideas (no commitment)

- **Per-personality chat.** Wealthy bots talk differently than Rough bots. Class-specific (Mages talk magic, Warriors talk combat).
- **Bot story memory.** Click a bot to see their phase history — "Despise yesterday, Britain this morning."
- **Bot factions / friend groups.** Bots who share waypoints develop "knows" relations; chat refers to each other by name.
- **Day/night cycle behavior.** Bots prefer banks at night, dungeons during the day.
- **Bot housing.** Each bot has a home tile they return to between phases.
- **Naturalistic behaviors.** Bots sit in chairs at taverns, browse vendor goods, dance at events.

---

## Done

- ✅ AdventurerBehavior with PathFollower combat (v9-v12b)
- ✅ TravelerBehavior with waypoint graph navigation (v13-v15g)
- ✅ BotPersonality system with weighted tendencies + traits (v16)
- ✅ BotLifecycleManager with phase transitions (v16-v16e)
- ✅ Smart lifecycle placement (v16d)
- ✅ Final-leg arrival offset to prevent stacking (v15g)
- ✅ BotClass + SkillTier system — 10 classes, 7 tiers, bell-curve distribution (v17)
- ✅ Real UO skill templates with stat profiles (v18)
- ✅ `[BotInfo` diagnostic command with notoriety (v18b, v20e)
- ✅ Town guards filter — bots don't attack guards, vendors, NPCs (v18c)
- ✅ GM Panel redesign — sectioned layout, behavior picker grid, destructive-action confirmation gumps, single-target Remove (v19)
- ✅ Remove ALL spawners button (v19)
- ✅ DungeonEntry — Recall path (Magery ≥ 50) or teleport-to-entrance fallback (v20)
- ✅ Auto-running on long legs (v20b)
- ✅ Bots can fire teleporters (`Player = true` fix) (v20c)
- ✅ Hunger/Thirst pinned (v20d)
- ✅ Destinations system Phase 1+2 — places of interest, class-weighted picking (v22)
- ✅ `[MarkWay`, `[MarkSpot` in-game capture commands (v22b-c)
- ✅ Inn, Dungeon, VendorWeaponer, VendorProvisioner destination types (v22j-m)
- ✅ Aggressive stuck-recovery with 3-tile nudge every 4 seconds (v22f-i)
- ✅ Equipment variety pass — hats, cloaks, sashes, armor combos, expanded palettes (v23)
- ✅ Bot mounts — 70% mounted on horse/ostard/llama, realistic colors, mount-speed movement (v24-v24c)
- ✅ Per-bot destination spread (±5 tile offset on arrival) (v25)
- ✅ Britain waypoint graph + destinations expanded to 35 nodes / 17 places
