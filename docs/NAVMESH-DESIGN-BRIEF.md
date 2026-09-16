# PlayerBot Navigation. Hand-Drawn Walk Zones and Vendor Areas

**Project:** Klein187/uo-offline (ModernUO PlayerBots)
**Status:** Built 2026-09-16. Everything below exists in the code; the Britain soak with counts is still to run. What was built and where:

| Piece | Where |
|---|---|
| Walk zones, tags, costs, Z ranges, computed links, `[zones` / `[zonelinks` | `ZoneRegistry.cs` |
| Fleet mode `[zonenav off\|half\|on\|verbose`, link strikes, link-graph A\* | `Nav/ZoneNav.cs` |
| In-zone walker with side steps and local A\*, engine fallback | `Nav/ZoneFollower.cs` |
| `[NavShow`, `[NavHide`, `[NavPath <bot>` | `Nav/ZoneNavView.cs` |
| Shopper wander and walk-to-vendor | `Behaviors/ShopperBehavior.cs` |
| Real purchase from the vendor's shelf | `BotVendorPurchase.cs` |
| Traveler: zone walker per leg, drift into drawn areas, restock gate | `Behaviors/TravelerBehavior.cs` |
| `vendor_request.txt` rig, `zone_stall` kind, status page Nav section | `EditorReloadWatcher.cs`, `BotStuckTelemetry.cs`, `BotStatusPage.cs` |
| Editor: Walk kind, tags, red shading, live links, corner drag | `tools/map/map.html`, `tools/map/serve_map.py` |

Two details differ from the text below. Z ranges are measured by the game on the first mesh build, not filled by the editor, because there is no full-map Z atlas. And the per-bot flag became a fleet mode with a per-bot override, so the half split can be flipped without a restart.

**Replaces:** the first 9/16 pass. That pass had a script propose rectangles from the walk atlas and a human approve them. The owner ruled that out: every zone is drawn by hand in the map editor, the same way portals and areas are drawn today. It also adds the second half of the ask: a drawn vendor area is a place bots walk to, move around in, and buy from.

---

## What this is, in one paragraph

Bots keep the waypoint graph for routing between towns and across the map. Inside towns they get a second, local layer: **walk zones**, polygons of clear ground that the owner draws on the map. Inside a walk zone a bot walks straight. Between zones it crosses a computed link. The existing painted **areas** (bank, weapon vendor, dock, mine) become part of the same layer, so a bot can walk the zones to the weapons vendor's area, step inside, wander around the shop, and buy something from the real vendor NPC. Dungeons are not touched. They stay on waypoints, per the 9/4 decision.

---

## What already exists and is reused as-is

The painted zone system is the foundation. Nothing here replaces it.

- **`playerbots/data/Zones/zones.json`** loaded by `ZoneRegistry`. Two kinds today: `Portal` (a small polygon at a doorless doorway) and `Area` (a polygon that IS a destination, linked by name to `destinations.json`). Destinations with a `Polygon` field are merged in as Areas at load.
- **Drawing in `tools/map/map.html`.** Tick EDIT zones, pick a kind and type, click corners, Enter saves. Areas absorb the destination dot inside them or create a new destination. Delete removes a selected zone. Save writes the JSON and the Reload button or `[ReloadZones` loads it into the running shard.
- **Arrival by stepping inside.** `TravelerBehavior` calls `ZoneRegistry.AreaForDestination` every leg check. The moment the bot is inside the destination's Area it is arrived, no matter how many legs were left. Drift then walks it toward the area centre, via the nearest Portal if one serves the area.
- **Shopper handoff.** For every `Vendor*` destination type the Traveler hands off to `ShopperBehavior` with an 80% chance once `ZoneArrival(bot, 15)` passes. The Shopper stands still, turns now and then, and says "vendor buy" style lines on a timer. It does not walk and does not buy.
- **Restock on arrival.** `BotSupplies.TryRestockAtArrival` runs at every destination. It creates arrows, bandages and reagents straight into the pack and takes gold out. No vendor NPC is involved.
- **Walk atlas** (`tools/walkmap_atlas.py`, `tools/map/walk_atlas.pgm` plus the Z sidecar). Used here only to shade unwalkable tiles while drawing. It never proposes anything.
- **Watchdogs and telemetry.** `BotNavWatch`, the frozen-position watchdog, `BotStuckTelemetry` and `stuck_report.json`.
- **In-game overlay.** `[showways` and `WaypointMarkerItem : ProjectedItem`. Overlay art must never be a real item ID. `0xF9` walled bots in once.

## What is wrong today

- A leg longer than 38 tiles cannot be walked, because `PathFollower` searches a box of about 38 tiles. Long legs through building clusters fail more often than short ones.
- A stuck leg burns three repath cycles and then the bot drops the trip.
- Bots deliver to a vendor's doorstep and stop. The Shopper never enters the shop, never moves once it is there, and never buys anything. The "vendor buy" line is theatre, since a bot has no client to receive the buy gump.
- The restock that does happen is invisible. Items appear in the pack at any destination that "satisfies" the need, with no vendor, no walk to a counter and no shop stock.

The stuck report from the 9/8 soak shows this is not the stuck fix. Dungeon depth is the stuck problem and is fixed with waypoint data. This is a **movement and shopping quality** feature.

---

## Decision

Two things, built on the painted zone system:

1. **Walk zones.** A new zone kind, `Walk`, drawn by hand in the map editor. Inside towns the leg walker steps through walk zones and areas instead of asking the engine pathfinder.
2. **Vendor areas that work.** An `Area` of a `Vendor*` type is walkable ground in the same layer. A bot routes to it, walks in, wanders inside the polygon, goes to the vendor NPC, and buys from the vendor's real stock for real gold.

What does not change:

- The waypoint graph is the router everywhere. It is not deleted, converted or shrunk.
- Dungeons never use zones. Do not re-pitch this for dungeons.
- No machine proposes zones. The owner draws every one. Tools may shade tiles and warn, never author.

### Why hand-drawn polygons, not machine rectangles

The owner already draws portals and areas by clicking corners and knows the towns. A proposal tool would produce hundreds of slivers around lamp posts and benches that the owner would then have to clean up. It is faster to draw the street as one polygon and let the walker deal with the odd post.

That means a walk zone is **not** guaranteed clean. The walker must cope with a blocked tile inside a zone. See Pathfinding.

---

## Data model

Everything lives in `zones.json`. One file, one loader, one reload command.

### Walk zone

```
{
  "Name": "Britain Main Street",
  "Kind": "Walk",
  "Tag":  "road",           // road | plaza | interior | dock | no-bots
  "Cost": 1.0,              // from the tag, editable per zone
  "ZMin": -5, "ZMax": 12,   // filled by the editor from the atlas Z sidecar
  "Points": [[x,y], ...]    // polygon, same as Portal and Area
}
```

Z is a range, not a single value. Britain streets drift several Z over one block. A second floor and the ground under it are separate zones because their Z ranges do not touch. Stairs are a small zone whose range spans both.

### Area

Unchanged in shape. Gains the same optional `Tag`, `Cost`, `ZMin`, `ZMax` fields. Default tag for a `Vendor*` area is `interior`. For the walker, an Area is a walk zone that also happens to be a destination.

### Link

**Computed at load, never drawn.** Two zones (walk or area) are linked when their polygons, each grown by one tile, share at least one tile that the atlas marks standable, with compatible Z. The shared tile run is the link. Its midpoint is the default crossing target.

A hand-drawn `Portal` zone that touches two zones also links them. This is how an interior area joins the street through a doorway, and it is what the owner already draws.

### Doors

`PlayerBot` is not a `BaseCreature`, so the engine pathfinder treats every door as a wall. A link whose shared tiles hold a door is a **door link**. The walker calls `DoorHelper.TryOpenAhead` before crossing it. The Shopper already opens doors, so the walker uses the same helper.

---

## Pathfinding

Long haul is unchanged. `WaypointGraph.FindPath` still picks the node sequence.

For each leg:

1. If both ends of the leg sit inside zones on the same map, look up the zone for each.
2. Run A\* over the link graph. A few hundred nodes per town. Link cost = distance × zone cost × strike penalty.
3. Walk toward each link midpoint, cross, repeat. In the final zone walk to the leg end.
4. If either end is outside every zone, or no link route exists, the leg falls back to `PathFollower` exactly as today.

The 38-tile cap stops applying to any leg inside zone coverage. Long legs in towns can be authored once zones exist.

### Walking inside a zone

Step straight toward the target. When the next tile is blocked or outside the polygon, run a small A\* over the tiles inside this zone only. A zone is a few hundred tiles at most, so this is cheap and it is how lamp posts, wells and benches inside a hand-drawn street get walked around. The engine's own walkability check is the truth for each tile, the same as `PathFollower` uses.

### Costs

Tags set the default cost. Road cheap, plaza normal, interior expensive, dock normal, no-bots infinite. Bots prefer streets without any node being hand-placed along them.

### Stuck handling

The existing ladder stays. When a bot fails to cross a link, the strike goes on that link the same way `NavEdgeHealth` scores an edge, and the next route avoids it. The stuck report gains a `zone_stall` kind.

### Other mobiles

Never encoded. The existing sidestep logic and `PlayerBot.CheckShove` handle crowds.

---

## Vendor areas: walk to, walk in, buy

This is the second half of the ask. The example is the weapons vendor.

### Drawing it

Draw an `Area` of type `VendorWeaponer` around the shop floor in the map editor, exactly as bank and dock areas are drawn now. The polygon should cover the floor a customer can stand on. It absorbs the vendor's destination dot or creates one. Draw a `Portal` on the doorway if the door is doorless, or leave it to the door link if there is a real door.

Nothing else has to be drawn. The vendor NPC is found at runtime.

### Getting there

Waypoints to town, walk zones to the door, link through the doorway, inside. Arrival fires on the first tile inside the area, as it does today. The doorstep rule stays as the fallback for shops with no area drawn.

### Inside the area: the Shopper moves

`ShopperBehavior` gains wandering:

- Pick a random standable tile inside the polygon. Walk to it with the in-zone walker. Pause 4 to 10 seconds facing a random direction, as it does now. Repeat.
- Stay inside the polygon. A wander goal outside it is never picked.
- Once or twice per visit, walk to the vendor and buy. See below.
- The visit timer and the 80% handoff are unchanged.

### Buying for real

The vendor is the nearest `BaseVendor` inside the area or within a few tiles of it whose sell list matches the area type. The purchase is server-side, since a bot has no `NetState` and cannot use the buy gump:

1. Walk to within 2 tiles of the vendor with the in-zone walker. Face the vendor. Say "vendor buy" so players see the usual line.
2. Read the vendor's real buy list (`BaseVendor` sell info). Pick what the bot wants: for a weaponer, a weapon that fits the bot's class and is better than what it holds, using the existing `BotWants` and `EquipmentTable` logic. For a provisioner, bandages or food. For a mage vendor, reagents.
3. If the pack holds the gold, take it out of the pack, create the item from the vendor's entry and drop it in the pack. Emote the purchase the way the crafter sell-off does.
4. If the bot cannot afford it, say so and leave without buying. A bot with no gold does not get free goods.

`BotSupplies.TryRestockAtArrival` stops creating supplies at destinations that have a vendor area drawn. It stays as the fallback for destinations with no area, so existing behaviour is unchanged until an area is drawn. Each area the owner draws switches that shop from the illusion to the real purchase.

### Other area types

The same wandering applies to bank, tavern, inn and dock areas. `BankSitterBehavior` and `VisitorBehavior` get the in-area wander with their own pauses. This is a follow-on step, after the weapons vendor works.

---

## Authoring

Everything happens in `map.html`, in the EDIT zones mode that already exists. New pieces:

- **Kind `Walk`** in the kind dropdown, with a tag dropdown next to it.
- **Red tile shading while drawing.** Every tile inside the sketch that the atlas marks unwalkable shades red. Feedback only. The zone saves whatever was drawn.
- **Links draw live** in their own colour as zones change. A walk zone with no link is flagged in the side panel so gaps are visible before a bot finds them.
- **Z range filled automatically** from the atlas Z sidecar on save. Editable in the panel if the atlas is stale.
- **Drag a corner** of a selected zone to reshape it. Today a zone has to be deleted and redrawn.
- **Save and reload** unchanged: writes `zones.json`, Reload button or `[ReloadZones`.

The order is map editor first, in-game second. Client targeting reaches about 20 tiles, so boxing a city in-game means walking the whole city clicking corners. The map shows it at once.

### Checking in-game

Read-only commands, matching the `[showways` convention:

| Command | Behaviour |
|---|---|
| `[NavShow` | Draws zone outlines and links within about 20 tiles. Border tiles only. Hue by tag, links in their own hue. |
| `[NavHide` | Clears the overlay. |
| `[NavPath <bot>` | Lights up the link sequence the bot is following and the link it is trying to cross. |

No in-game drawing commands. Drawing is the editor's job.

### Invalidation

When a house is placed or removed, or a decoration pass changes a town, run one atlas strip for that area. Zones with newly unwalkable tiles are listed on the status page under a Nav section with the tile count. Nobody has to remember to check.

---

## Migration and measurement

- **Per-bot flag.** `TravelerBehavior.UseZones`, off by default. Turn it on for half the town-walking bots and leave the other half on `PathFollower`.
- **Same trips, two walkers.** Routing is unchanged, so both halves take the same node sequences. Only the leg walker differs.
- **Count, do not eyeball.** Compare `trip_stall`, `nav_stall`, `frozen_repick` and `zone_stall` per mode over a soak of at least 30 minutes. This rule caught the mage kiting regression.
- **Shopping counted too.** Log every real purchase: bot, vendor, item, gold. A `shop_request.txt` rig sends one bot to a named vendor area and reports the walk-in, the wander count and the purchase.
- **Roll out by behaviour.** Traveler first. Then Shopper, then BankSitter and the crafter errands.

---

## Dungeon walk zones (added 2026-09-16, owner's request)

A Walk zone tagged `dungeon` is a hand-drawn dungeon floor. The editor asks for the dungeon name and level when you tag one. What it does:

- **Standing in one makes a bot a crawler.** A Traveler that steps inside becomes a DungeonCrawler scoped to that dungeon and floor, at once. `DungeonRegistry.IsInDungeon` is true inside a dungeon zone too, so the lifecycle rule converts wanderers the same way, and a crawler that steps out of the last dungeon zone goes back to the road. A crawler that just climbed out gets three minutes before the entrance zone can convert it again.
- **Crawlers route through the zones.** When the bot and its target room, stair or entrance sit in linked dungeon zones, the crawler walks the zone mesh straight to it instead of hopping waypoints. Rooms, stairs and entrances stay the authored dungeon points; the zones are how the bot gets between them. Where zones do not cover both ends, the waypoint hops are used as before.
- **Walk-in exits.** In exit mode, a floor with no stairs sends the crawler to the dungeon entrance point through the zones, and stepping outside the zone ends the crawl.

- **Room outlines.** An Area of type `DungeonRoom` drawn around a room point takes the point in as its outline, the way a vendor area takes in the shop's dot. A crawler heading for that room has arrived the moment it steps inside the outline, wherever the point sits, and its room-clearing shuffle moves anywhere on the drawn floor instead of four tiles around one tile. An outline drawn where no point exists creates the point, scoped to the dungeon and level from the dungeon editor fields.

- **Parts.** A shape drawn over a wall is two chambers under one outline. At load the game flood-fills every zone's tiles with its own step rules and numbers each separately walkable patch a part. Links join parts, not outlines, and routing and reachability work on parts, so a crawler is never offered a room it cannot reach through the wall. The status page shows the part count per zone; more than one part means the outline straddles something solid.

- **Waypoints through rock are ignored.** Dungeons sit side by side in the map strip, and a floor with no waypoint of its own can have its nearest node in the next dungeon over, through solid rock. Hythloth's landing chamber anchored to Shame level 5 that way and crawlers pressed against the wall for minutes. On a drawn floor the crawler only uses a waypoint or a point it can reach on the mesh. Measured on Hythloth level 1 with the owner's zones: stuck events on the drawn floors went from 78 to 3 in five minutes, and Hythloth left the hotspot list.

Dungeon regions and teleporters keep working as they do today. The zones add to them; nothing about the existing crawl is removed.

## Out of scope

- Dungeon routing by anything other than drawn zones and waypoints.
- Machine-proposed zones of any kind.
- Selling to vendors. Bots sell through the crafter sell-off and bot-to-bot shops already.
- Flow fields for the last mile. Not needed while link graphs are a few hundred nodes.
- Deliberate imperfection at links. Add later as its own change if wanted.

---

## Build order

1. `Walk` kind, tag, cost and Z range in `zones.json` and `ZoneRegistry`. Link computation at load. Unit-checkable offline against the current zones file.
2. Editor: `Walk` kind, red-tile shading, live links, Z fill, corner drag. The owner draws Britain's main streets and the weapons vendor area and reloads.
3. In-zone walker and link-graph A\* inside `TravelerBehavior`, behind the flag, with `PathFollower` fallback.
4. `[NavShow`, `[NavPath`.
5. Shopper wander inside the area.
6. Real purchase from the vendor NPC. Illusion restock switched off for shops with a drawn area.
7. Britain soak, counted, plus the `shop_request.txt` rig on the weapons vendor.
8. `zone_stall` in the stuck report and the status page Nav section. Then the other towns, one per session, and wander for bank, tavern and dock areas.

Steps 2 and 3 are the risky ones. Step 2 because it decides whether drawing a city is pleasant. Step 3 because it is the first time bot movement visibly changes. Step 6 is the first time a bot's gold buys a real item from a real vendor, so it needs the counted rig before rollout.
