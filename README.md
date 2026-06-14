# UO Offline

A one-command installer for offline single-player Ultima Online on Linux and Steam Deck, with a custom PlayerBots system that populates Britannia with bots that fight, travel, shop, bank, chat, ride horses, cross between cities through moongates, and live their own lives.

Built on [ModernUO](https://github.com/modernuo/ModernUO) and [ClassicUO](https://github.com/ClassicUO/ClassicUO). T2A era, runs entirely on localhost.

---

## Install

Download `uo-offline.zip` from the [Releases page](../../releases), then in a terminal:

```
cd ~/Downloads
unzip uo-offline.zip
cd uo-offline
chmod +x install.sh
./install.sh
```

That's it. The installer handles everything: ModernUO, ClassicUO, .NET 10, the UO game data, the spawn map, and the PlayerBots. Takes 15-25 minutes.

**Steam Deck users:** before the install command above, run these once to allow installs:

```
passwd
sudo steamos-readonly disable
sudo pacman-key --init
sudo pacman-key --populate
```

---

## First-time setup

Double-click the **UO Offline** desktop icon. Log in as `admin` / `admin`, create a character, pick any starting city.

The world starts empty. To populate it:

**1.** Type `[GmPanel` to open the GM admin panel. Click **★ Run All** under "WORLD" — this seeds the world with vendors, monsters, signs, moongates, town criers, and PlayerBots at every bank.

**2.** Done. Bots are now alive. The Lifecycle system takes over — bank-sitters become shoppers and adventurers, travelers walk the roads to vendors and dungeons, bots step through moongates to other cities.

---

## Features

**Bot identity.** Every bot has a class (Warrior, Mage, Fencer, Archer, Tamer, Crafter, Healer, Thief, Bard, Ranger) and a skill tier (Novice through Grandmaster, bell-curve distributed). Skills, stats, equipment, and behavior preferences all derive from class + tier. A Grandmaster Mage really IS a Grandmaster Mage — 99 Magery, 125 Int, with a fancy hued robe and spellbook.

**Equipment variety.** Beyond class signatures (Warriors in plate, Mages in robes), every bot rolls universal accessories: hats from 18 types (floppy hat, jester hat, feathered cap, tribal mask, etc.), cloaks in any color, body sashes, beards, varied hair. Some Warriors wear chain or studded instead of plate. Some Mages wear studded leather. The visual feel matches classic UO bank gatherings.

**Mounts.** 70% of bots spawn mounted on a horse, ostard, or llama. Horse coat colors vary realistically (browns, grays, palominos). Mounted bots move at proper UO mount speed. Mounts despawn cleanly with their rider on death or removal.

**Behaviors.** Bots run one of several behaviors, swapped by the lifecycle system and by arriving at the right kind of place:
- **Idle / Wander** — light local movement.
- **BankSitter** — stands at a bank, chats.
- **Traveler** — walks or rides between destinations along the waypoint road network.
- **Shopper** — stands at a vendor area and browses ("vendor buy", browsing chatter), then moves on.
- **Crafter** — settles at its station (Smith → Forge, Tailor, Bowyer) for long working sessions.
- **Adventurer** — real melee combat: sword swings, retreat at low HP, permadeath.
- **PK** — hostile player-killer behavior.

**Destinations, waypoints, and zones.** Travelers go to actual *places*, not random spots. The world is described by three layers:
- **Waypoints** — the road network. A graph of nodes Travelers thread with A*/Dijkstra routing, hot-reloadable via `[ReloadWaypoints`.
- **Destinations** — places of interest (banks, vendors, taverns, healers, moongates, dungeon entrances), class-weighted so Bards prefer taverns, Crafters prefer forges, etc.
- **Zones** — painted *areas* (open regions like bank plazas and docks where a behavior happens throughout) and *portals* (doorway thresholds). Managed by `ZoneRegistry`, hot-reloadable via `[ReloadZones`.

**Arrival points.** The key to bots reaching places they can actually stand. A destination can carry one or more **arrival points** — specific reachable tiles (a vendor counter, a doorstep, a moongate teleporter) — each with its own preferred route waypoints. A bot picks one arrival point, routes to the nearest of its waypoints, and arrives *on a standable tile* instead of grinding a wall trying to reach an unreachable interior coordinate. This is what lets a Shopper stop at the counter and a Traveler step onto a moongate cleanly.

**Moongate travel.** Bots that reach a moongate have a high chance to step through it and emerge at a random other city's gate, then resume exploring wherever they land — circulating the population across Britannia instead of pooling in one city. Bots whose destination is across water (on an island unreachable by foot) automatically reroute to a reachable moongate and gate out.

**Combat.** Adventurers engage hostile creatures (negative-Karma monsters) in melee, retreat when wounded, die permanently, get replaced by their spawner. Bots respect the notoriety system — they don't attack innocents or wildlife. Magic combat is still to come.

**Stuck recovery.** When bots get pinned against terrain, automatic detection nudges them in a walkable direction, opens doors in the way, and repaths — with a give-up after repeated failure so nothing paces forever.

**Navigation.** Short-range pathfinding via ModernUO's A*. Long-range via the waypoint graph. A distance-field final-approach system carries bots the last few tiles into areas. Bots fire dungeon/moongate teleporters naturally by stepping on the tile (via arrival points) — no fake "go inside" magic.

**Lifecycle.** Every bot has a personality — weighted tendencies toward each behavior plus optional traits (Restless, Homebody, Brave, Cautious, Wealthy, Rough). The lifecycle manager periodically evaluates each bot and transitions it when its current phase elapses.

---

## The map editor

A browser-based editor for the world's navigation data, served live from the running shard. Under `tools/map/`:

```
cd ~/uo-map        # (installed location)
./uo-map-launch.sh # serves on http://localhost:8777
```

It renders the full Felucca map and overlays your waypoints, destinations, and zones, reading them **live** from the shard's JSON every refresh. In EDIT mode you can:

- **Waypoints** — click to add (snaps to walkable road, auto-connects neighbors), drag to move, link/sever edges, delete.
- **Destinations** — drag to move, enable/disable (promote from the generated archive into the live catalog), paint **areas** over them (the shape becomes the destination), create new destinations.
- **Arrival points** — drop one or more on reachable tiles per destination (interior floors included), drag/delete them, and link each to route waypoints (click the gold marker, then a waypoint — a dashed gold line confirms the link).

Changes write straight to the shard's JSON (with backups); `[ReloadDestinations` / `[ReloadWaypoints` / `[ReloadZones` make them live without a restart.

The map background PNG is a generated artifact — regenerate it from your UO client's map files with `make_interactive_map.py` if it's missing.

---

## GM commands

**World marking (capture as you walk):**
- `[MarkWay <name>` — record a waypoint at your position, walkability-gated, auto-connects to reachable neighbors within 38 tiles.
- `[MarkSpot <type> <name>` — record a destination (Bank, Tavern, VendorSmith, etc.) with auto-detected city and nearest waypoint.
- `[RecordWay` … `[RecordWayStop` — drop waypoints automatically as you walk a route.
- `[DelWay` / `[DelSpot` — remove the nearest waypoint / destination (with confirmation).

**Graph maintenance:**
- `[ResyncWaypoints` — recompute every destination's nearest waypoint to match the current graph (dry-run by default, `apply` to write).
- `[AuditEdges` — flag waypoint edges that are blocked, too costly, or too far.
- `[ReloadWaypoints` / `[ReloadDestinations` / `[ReloadZones` — hot-reload the data files.

**Diagnostics:**
- `[BotInfo` — target a bot, dump class/tier/stats/skills/notoriety/behavior/destination.
- `[BotWhere`, `[hpacomponents`, `[hpaedges`, field-debug commands.

**Admin:**
- `[GmPanel` — central GM gump: world setup, spawning, teleporting, cleanup (with confirmation gumps for destructive actions).

---

## Currently being worked on

- **Expanding the world beyond Britain.** Trinsic, Vesper, Yew, Minoc, Moonglow, etc. — each city needs its waypoint road network, destination cluster, and painted areas. The map editor makes this point-and-click.
- **Painting vendor areas and arrival points** across the active destinations so every shop hands off to a Shopper cleanly.

## What's coming

- **Dungeons.** Design is complete (see `LIFECYCLE-DESIGN.md` / `TODO.md`): a DungeonCrawler behavior scoped to a dungeon by tag, entering via an arrival point on the entrance teleporter, roaming painted combat areas, descending through level teleporters (skill-weighted so weak bots stay shallow), and climbing back out one level at a time. Entrance/exit teleporters reuse the moongate teleport pattern; combat areas reuse the zone painter.
- **Death and resurrection.** Dead bots walk as ghosts to a healer, resurrect, return to their corpse, re-equip. (Interim: respawn at spawn point.)
- **Improved combat.** Magic combat; reworked melee.
- **Random events.** Town invasions bots will turn out to fight.
- **Per-personality chat** and **bot story memory** (recent travel/history per bot).

---

## Credits

- **[ModernUO](https://github.com/modernuo/ModernUO)** — the game server emulator. GPL-3.0.
- **[ClassicUO](https://github.com/ClassicUO/ClassicUO)** — the open-source UO client. BSD.
- **[Nerun's Distro](https://github.com/Nerun/runuo-nerun-distro)** — the pre-T2A spawn map. Decades of community work.
- **[mirror.ashkantra.de](https://mirror.ashkantra.de/)** — community mirror hosting the EA UO Classic installer.
- **Origin Systems / Electronic Arts** — for making Ultima Online in the first place.
- **Richard Garriott** — for the world we're all still playing in.

The PlayerBots system was built specifically for this project. GPL-3.0.

Ultima Online is © Electronic Arts. This project doesn't redistribute any EA-copyrighted assets; the installer downloads them from a third-party community mirror.
