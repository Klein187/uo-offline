# UO Offline

A single-player, fully offline Ultima Online experience you can play on **Windows, Linux, and the Steam Deck**. One installer sets up everything and runs the whole game on your own machine — no servers, no accounts, no internet required after install.

What makes it feel alive is a custom PlayerBots system that populates Britannia with bots that fight, travel, shop, bank, chat, ride horses, crawl dungeons, form guilds and hunting parties, wage the Order/Chaos war in the streets, duel outside the bank, gather and trade, die and run back for their corpses, gossip about things that really happened, and log off for dinner — so the world feels like a busy 1999 shard instead of an empty map.

Built on [ModernUO](https://github.com/modernuo/ModernUO) and [ClassicUO](https://github.com/ClassicUO/ClassicUO). T2A era, runs entirely on localhost.

---

## Install

The installer does everything for you: it builds ModernUO with the PlayerBots compiled in, bootstraps .NET, downloads ClassicUO and the UO Classic game data (from a community mirror, or uses an existing install if it finds one), grabs Nerun's spawn map, writes the T2A/localhost configs, and sets up a launcher. Takes 15-25 minutes, and re-running it is safe -- it skips anything already done.

### Windows (easiest)

1. On this GitHub page, click the green **Code** button, then **Download ZIP**.
2. Unzip it anywhere (your Desktop is fine). You'll get a folder named `uo-offline-main`.
3. Open that folder and **double-click `install.bat`**.

That's it. `install.bat` launches the installer with the right permissions, so you don't have to change any Windows settings or install anything else first.

Two things to expect during the install:
- A **UO Classic setup window** may pop up while the game data downloads -- just install to the default location and click through it. The installer continues automatically after.
- When it finishes, launch the game with the **UO Offline** desktop shortcut (or `start.bat`). Don't run `start.ps1` directly -- Windows blocks unsigned scripts, which the shortcut and `.bat` work around for you.

### Linux / Steam Deck

**Steam Deck users only:** first, in Desktop Mode, run these once to allow installs. The first `passwd` sets a sudo password if you've never set one:

```
passwd
sudo steamos-readonly disable
sudo pacman-key --init
sudo pacman-key --populate
```

Then clone and run:

```
git clone https://github.com/Klein187/uo-offline.git
cd uo-offline
chmod +x install.sh
./install.sh
```

(No git? On the GitHub page click the green **Code** button, **Download ZIP**, unzip it, then `cd uo-offline-main` and run the same `chmod`/`./install.sh`.)

---

## First-time setup

Double-click the **UO Offline** desktop icon. Log in as `admin` / `admin`, create a character, pick any starting city.

The world starts empty. To populate it:

**1.** Type `[GmPanel` to open the GM admin panel. Click **★ Run All** under "WORLD" — this seeds the world with vendors, monsters, signs, moongates, town criers, and PlayerBots at every bank.

**2.** Done. Bots are now alive. The Lifecycle system takes over — bank-sitters become shoppers and adventurers, travelers walk the roads to vendors and dungeons, bots step through moongates to other cities.

### Play as a normal (non-GM) character

The `admin` account is a Game Master — it's how you set up and manage the world, but a GM isn't a normal player (other characters treat you differently, and it's easy to accidentally use GM powers). Once the world is seeded, make a separate account for actually *playing*:

**1.** At the login screen, type a new account name and password you haven't used before and log in. The server creates the account automatically the first time you use it.

**2.** Create a character on that account and play normally. You can switch back to `admin` any time you need GM tools — just log out and log back in with the `admin` account.

---

## Features

**Bot identity.** Every bot has a class (Warrior, Mage, Fencer, Archer, Tamer, Healer, Thief, Bard, Ranger, plus the working classes: Smith, Tailor, Fisherman, Lumberjack, Miner) and a skill tier (Novice through Grandmaster, bell-curve distributed). Skills, stats, equipment, and behavior preferences all derive from class + tier. A Grandmaster Mage really IS a Grandmaster Mage — 99 Magery, 125 Int, with a fancy hued robe and a filled spellbook.

**Unique names + home cities.** No two live bots share a name. A minority carry surnames ("Tessa Ravenwood", "Halric the Grey", "Mara of Yew"), and a few use the handles real 1999 players did — Gandalf, Drizzt, lowercase "bob". Every bot also has a home city its travels favor, so *regulars* emerge: keep visiting the Britain forge and you keep seeing the same smith.

**Player guilds + the Order/Chaos war.** Thirteen era-flavored guilds ("The Undead Lords", "DOOM", "Knights of Yew"…) with big-zerg and small-crew rosters; ~40% of bots wear a `[TAG]`. Six guilds carry Order or Chaos shields — and opposing shields fight **on sight, in town, guards ignoring it**, exactly as T2A worked. Street fights outside the bank are back.

**Login/logout sessions.** Bots have play sessions, not eternal existence: they log in, play 1–4 hours, say "gtg dinner", and vanish — and the population follows a daily curve (dead at 5am, packed in the evening). Fresh spawns are logins ("hey all", "what did i miss").

**The event journal + gossip.** The shard keeps a journal of everything notable — kills, deaths, murders, duels, hunts, red sightings — and bots at banks *retell real events*: "Aldreth got pked at despise earlier!!" is only ever said if it actually happened. Bots that hunted or dueled together become friends and greet each other by first name for the rest of their lives.

**Hunting parties.** The LFG spam concludes: a fighter broadcasts "LFG despise anyone?", nearby bots answer and converge, and the group marches down real roads to a dungeon, enters together, and fights as a unit until the run ends with "gg all". Guildmates and friends get invited first.

**Real deaths + corpse runs.** Novices misjudge fights (retreat thresholds scale with experience) and sometimes die. Then UO's most iconic experience plays out: the ghost haunts its corpse moaning OoOoOo, walks to a healer or shrine, resurrects in a death robe, and runs back hoping the loot's still there — self-looting its own corpse the vanilla way, or wailing "WHO LOOTED MY CORPSE" if it rotted.

**PK ecology + region danger.** Murders heat a danger map; hot places drain of foot traffic as the population routes around them. A civilian who spots a red screams "RED AT {PLACE}!!", the sighting hits the gossip mill, and nearby travelers scatter.

**A visible economy.** Lumberjacks and Miners work real wilderness sites (40 generated across the map), fill their packs with actual logs and ore, and haul the load to town — selling to a working crafter in a coins-for-materials scene, or banking it. Adventurers buy from crafters ("how much for a katana" → "800 gold" → *hands over the coins* → "sold!"), and the endless WTS bank spam occasionally *concludes* with a real deal.

**Duels outside the bank.** Two fighters emote a challenge, bow, walk ten tiles clear of the crowd, and fight to low health — never to the death — then bow again while the loser demands a rematch. Era-perfect theater, legal in town.

**Ferries to the outer isles.** Docks pair up into ferry routes (Trinsic ↔ Valor Isle, Magincia ↔ Humility Isle): a bot whose trip crosses open sea walks to the pier, waits for the boat ("*pays the ferryman*"), and steps off at the far dock — "land ho". With the ferry network live, **all eight virtue shrines are walkable**, and pilgrims actually show up on the shrine islands.

**Treasure hunts.** A bot announces it "got a map off a dead brigand", walks out to one of 24 wilderness dig sites, digs with shovel swings and sounds — and the guardians erupt from the ground mid-dig. Fight them down, pry open the unearthed chest ("GOLD! actual gold!!"), pocket the coin, and carry the story back to the bank where the gossip mill spreads it.

**The fishing SOS.** A fisherman working a pier occasionally reels in a corked bottle with a map inside, and hawks it on the spot ("i fish, i dont dig. map for sale"). If an adventurer is standing around the docks, the map changes hands and a real treasure hunt sets out; if not, the tale still makes the rounds.

**Visible taming.** Tamer bots stalk wild animals and work them with the classic client spam ("I've always wanted an animal like you", "Will you be my friend?") — sometimes the beast shies away, sometimes it submits. Tamed pets follow their master through town, get hawked at the bank ("selling {pet}, 2k firm"), and either sell to a bystander bot or get released with a shrug. No permanent pet ever accumulates.

**Bot homesteads.** Small era houses (stone cottages, log cabins, thatched-roof cottages) scattered along the wilderness roads, placed with the REAL house placement rules, each with a locked door and a named sign ("Aldric's cottage") — ownerless, ageless, and fully removable (`[BotHouses scatter/clear`).

**Gear progression.** Dungeon runs pay: survive three and the next bank visit is shopping day — a visible tier promotion with better skills and kit ("finally saved up for new gear"). Regulars get better gear over weeks.

**Street characters.** Banks grow their own street life: the beggar ("gold plz") and the lost newbie ("how do i get to minoc??") — both of whom will latch onto a real player and follow them across the plaza.

**Chatter with texture.** Era voice throughout ("ne1", "thx m8", rare all-caps drama), late-night lines after 9pm, nervy whispers inside dungeons ("quiet... something ahead"), real emotes (*bows*, *dances*), gossip about real events, and the occasional "asdf". Ghost speech garbles for the living, exactly as it should.

**Shard status page.** `Data/Live/status.html` regenerates every minute: who's online (names, guild tags, class/tier, what they're doing, where), population vs the daily curve, and a Latest News feed straight from the event journal — the classic 1999 shard status page, telling the truth about 400 bots.

**Equipment variety.** Beyond class signatures (Warriors in plate, Mages in robes), every bot rolls universal accessories: hats from 18 types (floppy hat, jester hat, feathered cap, tribal mask, etc.), cloaks in any color, body sashes, beards, varied hair. Some Warriors wear chain or studded instead of plate. Some Mages wear studded leather. The visual feel matches classic UO bank gatherings.

**Mounts.** 70% of bots spawn mounted on a horse, ostard, or llama. Horse coat colors vary realistically (browns, grays, palominos). Mounted bots move at proper UO mount speed. Mounts despawn cleanly with their rider on death or removal.

**Behaviors.** Bots run one of many behaviors, swapped by the lifecycle system and by arriving at the right kind of place:
- **Idle / Wander** — light local movement.
- **BankSitter** — stands at a bank, chats (and occasionally challenges someone to a duel or closes a WTS deal).
- **Traveler** — walks or rides between destinations along the waypoint road network.
- **Shopper** — stands at a vendor area and browses ("vendor buy", browsing chatter), then moves on.
- **Crafter** — settles at its station (Smith → Forge, Tailor → shop, Fisherman → dock) for long working sessions, producing real goods.
- **Gatherer** — works a wilderness site (chop/mine animations, real logs and ore into the pack), then hauls to town to sell.
- **Adventurer** — full combat: melee, archery, and real magic (spell ladders up to Flamestrike, kiting, target switching, threat assessment); retreats scale with experience.
- **DungeonCrawler** — enters dungeons through the real entrance teleporters, sweeps rooms floor by floor (skill-weighted descent — novices stay shallow), camps respawns, and climbs back out when the run timer expires.
- **PartyMember / Duelist / Ghost / CorpseReclaim / Beggar / Newbie** — the hunting-party follower, the bank duelist, the death story, and the street characters.
- **TreasureHunter** — the dig-site scene: dig, fight the risen guardians, open the chest, walk home rich.
- **Tamer** — stalks a wild animal, tames it with the era spam, parades it to town, and sells it.
- **PK** — hostile player-killer behavior (and the reason civilians scream RED).

**Destinations, waypoints, and zones.** Travelers go to actual *places*, not random spots. The world is described by three layers:
- **Waypoints** — the road network. A graph of nodes Travelers thread with A*/Dijkstra routing, hot-reloadable via `[ReloadWaypoints`.
- **Destinations** — places of interest (banks, vendors, taverns, healers, moongates, dungeon entrances), class-weighted so Bards prefer taverns, Crafters prefer forges, etc.
- **Zones** — painted *areas* (open regions like bank plazas and docks where a behavior happens throughout) and *portals* (doorway thresholds). Managed by `ZoneRegistry`, hot-reloadable via `[ReloadZones`.

**Arrival points.** The key to bots reaching places they can actually stand. A destination can carry one or more **arrival points** — specific reachable tiles (a vendor counter, a doorstep, a moongate teleporter) — each with its own preferred route waypoints. A bot picks one arrival point, routes to the nearest of its waypoints, and arrives *on a standable tile* instead of grinding a wall trying to reach an unreachable interior coordinate. This is what lets a Shopper stop at the counter and a Traveler step onto a moongate cleanly.

**Moongate, Recall, and Gate travel.** Bots that reach a moongate have a high chance to step through and emerge at another city's gate — circulating the population across Britannia. Long hauls reroute through the gate network automatically. Mages with the skill and mana skip the walk entirely: Recall ("Kal Ort Por") straight to their destination, or open a **real** Gate Travel pair ("gate to despise up, hurry") that lingers for anyone — players included — to hop through.

**Combat.** Adventurers engage hostile creatures with class-appropriate fighting: melee with attack-slot fanning (bots surround a monster instead of stacking), archer/mage kiting, and a real spell book from Magic Arrow to Flamestrike with era-correct openers. Threat assessment scales with tier (bravery in numbers — crowds swarm bosses), targets switch mid-fight to whoever's actually biting, and after a rough win, fighters sit and *bandage wounds* while casters *meditate* their mana back before moving on. Bots respect notoriety — no attacking innocents or wildlife.

**Stuck recovery.** When bots get pinned against terrain, automatic detection nudges them in a walkable direction, opens doors in the way, and repaths — with a give-up after repeated failure so nothing paces forever.

**Navigation.** Short-range pathfinding via ModernUO's A*. Long-range via the waypoint graph. A distance-field final-approach system carries bots the last few tiles into areas. Bots fire dungeon/moongate teleporters naturally by stepping on the tile (via arrival points) — no fake "go inside" magic.

**Lifecycle.** Every bot has a personality — weighted tendencies toward each behavior plus optional traits (Restless, Homebody, Brave, Cautious, Wealthy, Rough). The lifecycle manager periodically evaluates each bot and transitions it when its current phase elapses.

---

## The map editor

A browser-based editor for the world's navigation data and population, served live from the running shard. Under `tools/map/`:

```
# Linux / Steam Deck
cd ~/uo-map && ./uo-map-launch.sh    # serves on http://localhost:8777

# Windows — double-click the "UO Map Editor" desktop icon
#           (or run tools/map/uo-map-launch.ps1)
```

It renders the full Felucca map and overlays your waypoints, destinations, zones, and spawns, reading them **live** from the shard's JSON every refresh. In EDIT mode you can:

- **Waypoints** — click to add (snaps to walkable road, auto-connects neighbors), drag to move, link/sever edges, delete.
- **Destinations** — drag to move, enable/disable (promote from the generated archive into the live catalog), paint **areas** over them (the shape becomes the destination), create new destinations.
- **Arrival points** — drop one or more on reachable tiles per destination (interior floors included), drag/delete them, and link each to route waypoints (click the gold marker, then a waypoint — a dashed gold line confirms the link).
- **Spawns (spawn editor)** — place spawn points of every kind (PlayerBot fixed-role / PlayerBot lifecycle / Monster / NPC / Vendor) with a count, range, and respawn timer; filter the view by kind; drag/edit/delete. The type dropdown is the full list of spawnable mobiles. `[GenerateCustomSpawners` turns the placed `spawns.json` into real in-game spawners.

Two read-only overlays help you see and debug the world:

- **Live entities** — polls the running shard (`[LiveMap on` in game) and draws every bot and creature at its real position, bots colored by current behavior, filterable by kind, with a **density heatmap** and click-to-inspect. Click a traveling bot to draw its planned route (magenta = remaining, grey = traveled).
- **WP coverage gaps** — shades the map by distance to the nearest waypoint: yellow = marginal (28–38t), red = a real gap (>38t, where bots can strand). Shows exactly where to extend the road network next.

Changes write straight to the shard's JSON (with backups). Two buttons apply them in the running game without alt-tabbing to the client:

- **↻ Reload in game** — hot-reloads waypoints, destinations (+ arrival points), and zones (= `[ReloadWaypoints` / `[ReloadDestinations` / `[ReloadZones`).
- **⚛ Regenerate bots in game** — re-lays the whole bot population (= `[GenerateBots`), so bank/shop crowds move onto your current arrival points.

(These work via a small token-file bridge the game polls — `EditorReloadWatcher`. You can still run the `[Reload…` commands by hand if you prefer.)

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
- `[CombatDebug on|off` — toggle verbose per-cast combat logging at runtime.

**Living shard:**
- `[BotGuilds` — guild rosters with live member counts.
- `[BotSessions [on|off]` — session layer status (live vs curve target) or toggle.
- `[BotParties [form]` — list live hunting parties, or force-form one near you.
- `[BotFactions [fight]` — Order/Chaos counts and active fights, or force a street fight.
- `[BotDuel` — force a bank duel near you.
- `[BotTrade` — force a trade scene (crafter purchase or WTS deal).
- `[BotDanger` — list places with recent murder heat.
- Headless test tokens (for soaks, no client needed): drop a number into `Data/Live/party_request.txt`, `death_request.txt`, or `faction_request.txt` and watch the console / `*_ack.json`.

**Admin / population:**
- `[GmPanel` — central GM gump: world setup, spawning, teleporting, cleanup (with confirmation gumps for destructive actions).
- `[GenerateBots` — (re)lay the ambient bot population: BankSitters on bank arrival points, Shoppers on vendor arrival points (one spawner per point, evenly spread), the rest roaming Travelers.
- `[GenerateCustomSpawners` — materialize the spawn editor's `spawns.json` into real in-game spawners (Monster/NPC/Vendor + fixed/lifecycle PlayerBots).
- `[LiveMap on|off [seconds]` — stream a live entity snapshot to the map editor's "Live entities" layer.

---

## Currently being worked on

**Cities — the mainland is done.** All eight mainland cities have their waypoint road networks, destination clusters, arrival points, and painted areas live:

| Done | In progress / planned |
|---|---|
| Britain | Magincia (markers only) |
| Trinsic | Nujel'm (markers only) |
| Vesper | Buccaneer's Den (markers only) |
| Minoc | Occlo |
| Yew | |
| Moonglow | |
| Skara Brae | |
| Jhelom | |

The island cities need boat-arrival illusion + their own waypoint pockets (no moongate lands on Buc's Den, so it needs special handling). Four virtue shrines are live as pilgrimage destinations (Chaos, Spirituality, Compassion, Sacrifice — the last with a 29-node server-verified desert trail); Justice, Honesty, Honor, Humility, and Valor await their trails.

**Dungeons — Despise is live, the rest are next.** Despise Level 1 is fully authored and soak-verified: bots walk in through the real entrance teleporter, sweep and camp its rooms, descend, and climb back out on their own. Level 2 is partially authored (bots land and hunt locally; full room/waypoint coverage in progress). **Deceit is next** — its skeleton is already generated (`tools/skeleton-deceit.json`), then Shame, Wrong, and Covetous, each following the same pipeline: generate skeleton → author rooms/waypoints per floor → headless audit → soak.

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
