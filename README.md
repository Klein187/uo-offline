# UO Offline

A single-player Ultima Online shard that runs entirely on your own machine. Works on **Windows, Linux, and the Steam Deck**. One installer sets everything up, and after it finishes you never need the internet again.

The point of it is the PlayerBots. The world is full of bots that fight, shop, bank, ride, travel the roads, crawl dungeons, join guilds, run war bands, gossip about things that actually happened, answer you when you talk to them, and log off for dinner. It plays like a busy 1999 shard instead of an empty map.

Built on [ModernUO](https://github.com/modernuo/ModernUO) and [ClassicUO](https://github.com/ClassicUO/ClassicUO). T2A era, all on localhost.

<details>
<summary><b>What's new — August 2026</b></summary>

<br>

Newest first.

- **Bots open doors.** Walk a bot into a closed door and it opens, the same way the client's auto-open-doors setting has always worked for players. Before this they only opened doors during stuck-recovery, so a bot that walked into a dungeon room or the Britain bank stayed shut in there until a rescue timer fired. Locked doors still stay locked, and house doors still run their own access checks.
- **One button sets up the world.** The GM panel's WORLD tab was a 3x3 grid of setup commands that always got run in the same order anyway. It is now a single **First Time Setup** button, and it also places the PKs, which the old Run All never did.
- **Twice as many bots.** The world population target went from 800 to 1600, and the reds scale with it. Dial it back live with `[SetBotPopulation` if your machine complains.
- **The launcher checks for updates.** Starting the game asks GitHub whether there is a newer version, and if there is you get one prompt listing what changed. Up to date, offline, or GitHub having a bad day all mean it says nothing at all.
- **Moongates work for normal characters.** Walking into a city moongate did nothing unless you were on the staff account. New characters count as "young", and the young destination list only offers Trammel, which a Felucca-only shard doesn't have, so the menu never opened. The young player system is off now (it's a UO:R feature, not T2A), and the gate falls back to the full city list if a ruleset ever leaves someone with nowhere to go.
- **Dungeon waypoints cleaned up.** The underground road network carried 541 duplicate and stacked waypoints left over from generation: corridors recorded twice on the same tiles, plus blobs around teleporter pads. They're merged or deleted, the graph went from 176 disconnected pieces down to 78, and it audits clean now — no duplicates, no dangling edges, no one-way links.
- **Map editor panel tidied.** The side panel is grouped into collapsible sections (Layers, Live, Edit tools, Game actions) instead of one long column.
- **Recall is really cast.** Bots cast the actual Recall spell: a marked rune pulled from the pack, real reagents burned, real scrolls used up, real skill checks. Fizzles get re-cast like a player mashing a macro, and a trip that won't take gets walked instead.
- **A real Windows installer, with Razor in the box.** `install.bat` opens a graphical installer that shows a live checklist while it works. It also installs Razor and wires it into ClassicUO, so the desktop shortcut starts the server, opens the game with Razor attached, and logs you in.
- **Crafters run a real economy.** Smiths, tailors, and the new Carpenter class burn actual stock from their packs, buy the miners' ore and lumberjacks' logs for real coin, and sell finished goods to browsing bots. Each trade has its own shop talk, and gossip now spreads at walking speed instead of instantly.
- **Tamers use their pets.** They claim a pet at the stables and give orders out loud — "all kill", "all stay", "all follow me". Pets get vet-bandaged and fed real raw ribs, and a tamer who runs out of food watches it go wild.
- **Crawlers loot, bards play.** Corpses give up gold, gems, scrolls, and reagents, and a good magic drop gets bragged about at the bank for days. Bards use Provocation everywhere, and some run the Peacemaking build instead. Skill-checked, sour notes included.
- **The reds got organized.** PKs use the era's murderer templates, mostly Red Mages plus field dexxers with Tracking and Hiding. They hunt in gangs, ambush dungeon entrances, and drink pots and bandage mid-fight. A fresh world seeds red crews at the chokepoints automatically.
- **1999 gear.** Kit is cheap exceptional crafted work, and magic items stay rare and tier-gated. A veteran's pack is the classic wall of bottles: reagents by the stack, heal and cure potions, explosion pots, trapped pouches, a spare halberd.
- **Real T2A builds.** Era stat caps (100/225) and seven-skill templates for every class. Tank mages re-equip the halberd mid-cast so the swing lands with the e-bolt, and two new classes joined: the Treasure Hunter and the Merchant.
- **Party up with the bots.** Target one with the normal party gump and it accepts. Or shout "lfg despise anyone" at the bank, ask the person next to you "wanna group?", or answer "me" when a bot shouts its own LFG and get a real invite back. Party members follow you, jump into your fights, and say goodbye when the group breaks up.
- **Talk to them and they answer.** Say a bot's name and it turns and responds. Greet the bank and someone greets back, or the room ignores you, which is also 1999. Ask a question and you get a shrug, because they're players, not tour guides.
- **No roleplay theater.** Every `*emote*` is gone from the shard. Bots type the way people actually typed: "gl" and "gf" around duels, "ty" when coins change hands, "vendor buy" at a shop. The whole chat corpus got the same pass.
- **Banks are busy places.** Every bank keeps a standing crowd of five: regulars talking trade, hawkers spamming WTS, statues who said "afk" an hour ago, someone raising resist by cursing himself over and over, someone flickering in and out of hiding, someone creeping circles training stealth.
- **Stables are real places.** Miners and lumberjacks stop at the stables, lead out a named pack animal, work with it, then walk it back and stable it. They don't own riding horses anymore, because you can't ride a pack animal and a working gatherer walked.
- **Newbies walk.** Recall scrolls scale with wealth, so a fresh Novice carries none and walks everywhere. Buying your first scrolls at the mage shop is a rite of passage again.
- **The dungeon layout got ground-truthed.** Every stair, entrance, and teleporter in all twelve dungeons was re-checked against the engine. Mislabeled stairs that sent "exiting" bots deeper are fixed, phantom graph edges and unstandable nodes are gone, and the cross-dungeon passages work.
- **Recall is the transport.** Long trips go by Recall or real scrolls, GM mages open public gates anyone can use, and a wedged bot casts its way out. Ferries are gone; they were never a T2A thing, so the outer isles are reached by magic.
- **Supplies run out.** Arrows, reagents, bandages, and scrolls are real and nothing refills invisibly. When a bot runs low it drops what it's doing and goes shopping, visibly, for gold.
- **Guild convoys and war bands.** Guildmates walk road trips together. Order and Chaos squads patrol and go looking for each other, and nearby faction-mates get pulled into the fight, up to 4v4.
- **Smarter fights.** Fighters bandage and cure mid-fight and retreat earlier when they're swarmed. Skilled mages answer a charging monster with Paralyze, step back, and resume.
- **Gossip got personal.** Bots tell their own stories in first person, war band clashes and guild outings make the bank rounds, and old news fades out.
- **Era-correct clothes.** Every hue comes from the classic dye tub range, with true black as the rare flex. Mana potions are gone from mage kits; they didn't exist yet.
- **The shard watches itself.** A live status page tracks stuck bots and rescues, and the fleet routes around road sections that keep causing trouble.

</details>

<details open>
<summary><b>Install</b></summary>

<br>

The installer does the whole job: it builds ModernUO with the PlayerBots compiled in, sets up .NET, downloads ClassicUO, Razor, and the UO Classic game data (or uses an existing install if it finds one), grabs Nerun's spawn map, writes the T2A configs, and makes a launcher. It takes 15-25 minutes. Re-running it is safe, since it skips anything already done.

### Windows (easiest)

1. On this GitHub page, click the green **Code** button, then **Download ZIP**.
2. Unzip it anywhere. You'll get a folder named `uo-offline-main`.
3. Open that folder and **double-click `install.bat`**.

That's it. A graphical installer opens, shows you what's about to happen, lets you toggle the T2A map art and Razor, and lets you pick where it installs (**Change...** next to the folder, or type a path). Then it works through a live checklist. Nothing else to install first, no Windows settings to change — it fetches .NET, git and the game data itself, and none of it needs admin rights.

It defaults to `%USERPROFILE%\uo-modernuo` and needs about **6 GB**. Any drive is fine, so a second disk works if your C: is tight. From the console version, pass the path instead:

```
powershell -ExecutionPolicy Bypass -File install.ps1 -InstallPath "D:\Games\UO Offline"
``` If you'd rather use a plain console, run `powershell -ExecutionPolicy Bypass -File install.ps1` instead. Both do the same steps.

Two things to expect while it runs:

- A **UO Classic setup window** may pop up while the game data downloads. Install to the default location and click through it. The installer carries on by itself afterwards.
- When it's done, click **Play Now**, or use the **UO Offline** desktop shortcut any time after. One click starts the server, opens the game with Razor attached, and logs you into the shard. Don't run `start.ps1` directly — Windows blocks unsigned scripts, and the shortcut works around that for you.

### Linux / Steam Deck

**Steam Deck only:** in Desktop Mode, run these once so you can install things. The first `passwd` sets a sudo password if you've never set one.

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

No git? On the GitHub page click **Code**, **Download ZIP**, unzip it, then `cd uo-offline-main` and run the same `chmod` and `./install.sh`.

It installs to `~/uo-modernuo` and needs about 6 GB. To put it somewhere else:

```
./install.sh --install-root /mnt/games/uo-offline
```

### Updates

Starting the game checks GitHub for a newer version. If there is one, you get a
single prompt listing what changed, and you can update, play anyway, or skip
that version for good. If you are up to date, or offline, or GitHub is having a
bad day, it says nothing at all and the game just starts. Updating re-runs the
installer, which rebuilds the server with the new bots and keeps your world,
characters and accounts.

</details>

<details>
<summary><b>First-time setup</b></summary>

<br>

Double-click the **UO Offline** desktop icon. It starts the server, opens the game with Razor attached, and logs you in as `admin` (the account is created on first login). Make a character and pick any starting city. Razor's window comes up alongside the game — set up macros there, or minimize it and forget about it.

The world starts empty. To fill it:

**1.** Type `[GmPanel` to open the GM panel and click **★ First Time Setup**. That one button lays down decor, signs, teleporters, moongates, town criers, the monster and vendor spawners, and the whole player bot population — town and road bots plus the reds — then saves. It is safe to run again later.

**2.** That's all. The Lifecycle system takes over from there. Bank-sitters become shoppers and adventurers, travelers walk the roads, and bots step through moongates to other cities.

### Play as a normal (non-GM) character

The `admin` account is a Game Master. It's how you set the world up, but a GM isn't a normal player — other characters treat you differently and it's easy to use GM powers by accident. Once the world is seeded, make a separate account to actually play on:

**1.** At the login screen, type a new account name and password you haven't used before and log in. The server creates the account the first time you use it.

**2.** Make a character on that account and play. You can switch back to `admin` whenever you need GM tools by logging out and back in.

</details>

<details>
<summary><b>Features</b></summary>

<br>

**Bot identity.** Every bot has a class (Warrior, Mage, Fencer, Archer, Tamer, Healer, Thief, Bard, Ranger, Treasure Hunter, Merchant, plus the working classes: Smith, Tailor, Fisherman, Lumberjack, Miner) and a skill tier from Novice to Grandmaster, spread on a bell curve. Skills, stats, and gear all come from class plus tier under the real T2A caps: 100 per stat, 225 total, seven-skill templates. A Grandmaster Mage really is one, usually a Tank Mage with a halberd, a full spellbook, and a hued robe.

**Unique names and home cities.** No two live bots share a name. Some carry surnames like "Tessa Ravenwood" or "Mara of Yew", and a few use the handles real players used, including lowercase "bob". Every bot has a home city its travels favor, so regulars turn up: keep visiting the Britain forge and you keep seeing the same smith.

**Guilds and the Order/Chaos war.** Thirteen era guilds ("The Undead Lords", "DOOM", "Knights of Yew") with both big-zerg and small-crew rosters. About 40% of bots wear a `[TAG]`. Six guilds carry Order or Chaos shields, and opposing shields fight on sight, in town, with the guards ignoring it, the way T2A worked.

**Login and logout sessions.** Bots don't exist forever. They log in, play one to four hours, say "gtg dinner", and vanish. The population follows a daily curve: dead at 5am, packed in the evening. Fresh spawns arrive as logins ("hey all", "what did i miss").

**The event journal and gossip.** The shard keeps a record of everything notable — kills, deaths, murders, duels, hunts, red sightings — and bots at banks retell real events. "Aldreth got pked at despise earlier!!" only gets said if it actually happened. Bots that hunted or dueled together become friends and greet each other by name from then on.

**Hunting parties.** A fighter broadcasts "LFG despise anyone?", nearby bots answer and converge, and the group walks real roads to a dungeon, goes in together, and fights as a unit until the run ends with "gg all". Guildmates and friends get asked first. You can answer too: say "me" and the leader sends you a real party invite.

**Play with them.** The bots treat you like another player. Say a bot's name and it answers. Greet the bank and somebody greets back, or nobody does. Ask a question and get a shrug. Form a group through the party gump, by shouting LFG, or by asking "wanna group?" — members follow you, run to keep up, fight your fights, and beg off in character when they're busy. Beggars and lost newbies will latch on and follow you across the plaza.

**Real deaths and corpse runs.** Novices misjudge fights and sometimes die, and retreat thresholds scale with experience. Then the most famous thing in UO happens: the ghost haunts its corpse moaning OoOoOo, walks to a healer or shrine, resurrects in a death robe, and runs back hoping the loot is still there. It self-loots the vanilla way, or wails "WHO LOOTED MY CORPSE" if the corpse rotted.

**PKs and region danger.** Reds run the era's murderer builds at Master and Grandmaster strength, in gangs that converge on victims, ambush dungeon entrances, and bandage and chug pots mid-fight. Murders heat up a danger map, and hot places empty of foot traffic. A civilian who spots a red screams "RED AT {PLACE}!!" and nearby travelers scatter.

**A visible economy.** Lumberjacks and Miners work 40 real wilderness sites, fill their packs with actual logs and ore, and haul the load to town. The matching crafter pays real gold from its own purse, and the raw haul becomes that crafter's ingots or boards. If no buyer is around, the bank takes it. Adventurers buy finished pieces off the shelf ("how much for a katana" then "800 gold" then "ty"), and the katana and the coin really change packs.

**Duels outside the bank.** Two fighters call a challenge, trade a "gl", walk ten tiles clear of the crowd, and fight to low health but never to the death. Then they close with "gf" while the loser demands a rematch or blames lag. Legal in town, as it was.

**Recall is the transport.** Casters keep travel Magery, and established characters carry recall scrolls scaled by wealth. Long trips go by magic scaled to distance, GM mages open real public gates anyone can hop through, and the gateless outer isles — Valor, Humility, Dagger Isle, Fire Isle — get their pilgrims by Recall, since that's the only way short of a boat. A hopelessly wedged bot recalls out too.

**Supply runs.** Bows eat arrows, casts eat reagents, bandages get used up, and nothing refills invisibly. When a bot runs low it leaves what it's doing and goes shopping: the bowyer for arrows, the provisioner for bandages, the mage shop for reagents and scrolls, or its own bank box. The purchase happens on arrival, visibly, for gold.

**Permanent bank crowds.** Every bank keeps five people standing around: regulars talking trade, hawkers spamming WTS, statues who went afk an hour ago, someone casting curse on himself over and over to raise resist (real spells, real reagents, refilled from the bank box when the pouch runs dry), someone hiding, and someone training stealth. Individuals die and get replaced, but the crowd never goes away.

**Guild convoys and war bands.** Guildmates muster and walk road trips together ("guild trip to trinsic, who walks with me?"), fight as a group when the road bites back, and split up on arrival. Order and Chaos squads patrol to faction spots and set intercept courses on enemy patrols. When shields meet, nearby faction-mates get drafted in, up to 4v4.

**Pack animals at the stables.** Miners and lumberjacks own no riding horses. Heading out they stop at the stables, lead out a named beast, load double the haul onto it, and after selling in town they walk it back and stable it. Tamers use the same counter for their horses.

**Treasure hunts.** Maps change hands before anyone digs, bought off the bank crowd or off a fisherman at the docks, and the rolled-up map rides in the pack for the whole trip. The hunter walks to one of 24 dig sites, digs with real shovel swings, and the guardians erupt mid-dig. Fight them down, open the chest, pocket the coin, and carry the story back to the bank.

**The fishing SOS.** A fisherman on a pier occasionally reels in a corked bottle with a map in it and hawks it on the spot ("i fish, i dont dig. map for sale"). If an adventurer is nearby, the map changes hands and a real hunt sets out. If not, the story still makes the rounds.

**Visible taming.** Tamers stalk wild animals and work them with the classic client spam ("I've always wanted an animal like you"). Sometimes the beast shies away, sometimes it submits. Tamed pets follow their master through town, get hawked at the bank, and either sell to a bystander or get released. No bot accumulates a permanent pet.

**Bot homesteads.** Small era houses — stone cottages, log cabins, thatched-roof cottages — sit along the wilderness roads, placed with the real house placement rules. Each has a locked door and a named sign. They're ownerless, ageless, and removable with `[BotHouses scatter/clear`.

**Gear progression.** Dungeon runs pay. Survive three and the next bank visit is shopping day: a visible tier promotion with better skills and kit ("finally saved up for new gear"). Regulars get better over weeks.

**Street characters.** Banks grow their own street life. The beggar ("gold plz") and the lost newbie ("how do i get to minoc??") will both latch onto a real player and follow them across the plaza.

**Chatter with texture.** Era voice throughout ("ne1", "thx m8", the odd all-caps drama), late-night lines after 9pm, nervy lines inside dungeons, gossip about real events, and the occasional "asdf" or "oops wrong window". No bot ever types an emote in asterisks. Ghost speech garbles for the living.

**Shard status page.** `Data/Live/status.html` rebuilds every minute: who's online with names, guild tags, class and tier, what they're doing and where; population against the daily curve; live counts of parties, convoys, and war bands; a news feed from the event journal; and a "Stuck & Rescues" section.

**Equipment, strictly era.** Beyond class signatures, every bot rolls accessories from the classic 1998 set: floppy hats, jester hats, feathered caps, tricornes, cloaks and sashes dyed only in colors the T2A dye tub could mix, with true black as the rare one. Metal armor is iron or genuine colored ore. Magic gear uses the real era system, Ruin through Vanquishing, with exceptional maker's marks that GM crafters announce when they pull one off. Nothing on anyone's back postdates 1998.

**Mounts.** Most bots spawn on a horse, ostard, or llama. Working folk are the exception: gatherers walk with their pack beasts, and fishermen work the pier on foot. Coat colors vary, mounted bots move at proper mount speed, and mounts despawn cleanly with their rider.

**Behaviors.** Bots run one of many behaviors, swapped by the lifecycle system and by arriving somewhere that calls for a different one:

- **Idle / Wander** — light local movement.
- **BankSitter** — stands at a bank and chats, and sometimes starts a duel or closes a WTS deal.
- **Traveler** — walks or rides between destinations on the waypoint road network.
- **Shopper** — browses a vendor area, then moves on.
- **Crafter** — settles at its station (Smith to forge, Tailor to shop, Carpenter to carpentry shop, Fisherman to dock) for long working sessions, making real goods from real materials and restocking when the shelf empties.
- **Gatherer** — works a wilderness site with real chop and mine animations, then hauls the load to town.
- **Adventurer** — full combat: melee, archery, and real magic up to Flamestrike, with kiting, target switching, and threat assessment. Retreat thresholds scale with experience.
- **DungeonCrawler** — enters through the real teleporters with a torch lit if a hand is free, sweeps floor by floor (novices stay shallow), loots the gold off its kills, camps respawns, and climbs out when the timer or the supplies run out. On the way up it only fights in self-defense, because leaving should look like leaving.
- **PartyMember / Duelist / Ghost / CorpseReclaim / Beggar / Newbie** — the hunting-party follower, the bank duelist, the death story, and the street characters.
- **PlayerGroup** — a bot in *your* party: follows your lead, fights your fights, says goodbye at the end.
- **TreasureHunter** — dig, fight the guardians, open the chest, walk home rich.
- **Tamer** — stalk an animal, tame it, parade it to town, sell it.
- **PK** — hostile player-killer, and the reason civilians scream RED.

**Destinations, waypoints, and zones.** Travelers go to actual places, not random spots. Three layers describe the world:

- **Waypoints** — the road network. A graph of nodes that Travelers thread with A*/Dijkstra routing. Hot-reloadable with `[ReloadWaypoints`.
- **Destinations** — places of interest (banks, vendors, taverns, healers, moongates, dungeon entrances), weighted by class so Bards prefer taverns and Crafters prefer forges.
- **Zones** — painted areas like bank plazas and docks where a behavior happens throughout, plus portals for doorway thresholds. Hot-reloadable with `[ReloadZones`.

**Arrival points.** This is what lets bots reach places they can actually stand on. A destination can carry several arrival points, each a specific reachable tile — a vendor counter, a doorstep, a moongate — with its own preferred route waypoints. A bot picks one, routes to the nearest of its waypoints, and arrives on a standable tile instead of grinding against a wall trying to reach an unreachable interior coordinate.

**Moongate, Recall, and Gate travel.** Bots that reach a moongate usually step through and come out at another city's gate, which circulates the population around Britannia. Long hauls reroute through the gate network automatically, and cross-water trips split between Recall and the moongates so the gate plazas keep their crowds. Casters Recall on mana, scroll users spend their stack carefully, and GM mages open a real Gate Travel pair that lingers for anyone, players included, to hop through.

**Combat.** Adventurers fight with class-appropriate tactics: melee bots fan around a monster instead of stacking on it, archers and mages kite, and the spellbook runs from Magic Arrow to Flamestrike with era openers, including Paralyze against a monster closing on a skilled mage and cure potions when poison lands. Fighters bandage mid-fight on a fast combat pulse and the bandage actually completes. Retreat thresholds scale with experience and with how many monsters are piling on. After a rough win, fighters bandage up and casters meditate. Nobody attacks innocents or wildlife.

**Stuck recovery.** When a bot gets pinned against terrain, it gets nudged toward walkable ground, doors in the way get opened, and it repaths — escalating through sidesteps and wedge extraction to a full recall-out if it can cast or has a scroll. Every firing feeds a telemetry ledger: the status page shows trouble hotspots, `Data/Live/stuck_report.json` feeds tooling, and road edges that keep defeating bots take a temporary routing penalty, so the whole fleet detours around chokepoints on its own.

**Navigation.** Short-range pathfinding uses ModernUO's A*. Long-range uses the waypoint graph. A distance-field final approach carries bots the last few tiles into an area. Bots fire dungeon and moongate teleporters by stepping on the tile, with no fake "go inside" magic.

**Lifecycle.** Every bot has a personality: weighted leanings toward each behavior plus optional traits (Restless, Homebody, Brave, Cautious, Wealthy, Rough). The lifecycle manager checks each bot periodically and moves it to a new behavior when its current phase runs out.

</details>

<details>
<summary><b>The map editor</b></summary>

<br>

A browser-based editor for the world's navigation data and population, served live from the running shard.

The installer sets it up for you — it's one of the tick-boxes on the first screen, on by default. Untick it if you only want to play. On Windows it needs Python; if you haven't got one, the installer fetches the small embeddable build rather than making you install anything.

```
# Windows — double-click the "UO Map Editor" desktop icon
#           (or run map-editor\uo-map.bat inside your install folder)

# Linux / Steam Deck
~/uo-modernuo/map-editor/uo-map-launch.sh     # serves on http://localhost:8777
```

Skip it at install time with `--no-map-editor` on Linux, or by unticking the box on Windows.

It draws the full Felucca map with your waypoints, destinations, zones, and spawns on top, read live from the shard's JSON on every refresh. In EDIT mode you can:

- **Waypoints** — click to add (snaps to walkable road and auto-connects neighbors), drag to move, link or sever edges, delete.
- **Destinations** — drag to move, enable or disable, paint areas over them so the shape becomes the destination, or create new ones.
- **Arrival points** — drop them on reachable tiles, including interior floors, drag and delete them, and link each to route waypoints by clicking the gold marker and then a waypoint.
- **Spawns** — place spawn points of every kind (PlayerBot fixed-role, PlayerBot lifecycle, Monster, NPC, Vendor) with a count, range, and respawn timer. Filter by kind, drag, edit, delete. `[GenerateCustomSpawners` turns the saved `spawns.json` into real in-game spawners.

Two read-only overlays help you debug:

- **Live entities** — polls the running shard (`[LiveMap on` in game) and draws every bot and creature where it really is, colored by behavior, filterable, with a density heatmap and click-to-inspect. Click a traveling bot to see its planned route: magenta is remaining, grey is traveled.
- **WP coverage gaps** — shades the map by distance to the nearest waypoint. Yellow is marginal (28-38 tiles), red is a real gap (over 38, where bots can strand). It shows exactly where to extend the roads next.

Changes write straight to the shard's JSON, with backups. Two buttons apply them in the running game without alt-tabbing:

- **Reload in game** — hot-reloads waypoints, destinations, and zones.
- **Regenerate bots in game** — re-lays the whole bot population, so bank and shop crowds move onto your current arrival points.

These work through a small token-file bridge the game polls. You can still run the `[Reload` commands by hand if you prefer.

The map background PNG is generated. If it's missing, rebuild it from your UO client's map files with `make_interactive_map.py`.

</details>

<details>
<summary><b>GM commands</b></summary>

<br>

**Marking the world as you walk:**

- `[MarkWay <name>` — record a waypoint where you stand, walkability-checked, auto-connecting to neighbors within 38 tiles.
- `[MarkSpot <type> <name>` — record a destination (Bank, Tavern, VendorSmith, and so on) with the city and nearest waypoint filled in.
- `[RecordWay` and `[RecordWayStop` — drop waypoints automatically as you walk a route.
- `[DelWay` / `[DelSpot` — remove the nearest waypoint or destination, with confirmation.

**Graph maintenance:**

- `[ResyncWaypoints` — recompute every destination's nearest waypoint against the current graph. Dry-run by default; add `apply` to write.
- `[AuditEdges` — flag waypoint edges that are blocked, too costly, or too far.
- `[ReloadWaypoints` / `[ReloadDestinations` / `[ReloadZones` — hot-reload the data files.

**Diagnostics:**

- `[BotInfo` — target a bot and dump its class, tier, stats, skills, notoriety, behavior, and destination.
- `[BotWhere`, `[hpacomponents`, `[hpaedges`, and the field-debug commands.
- `[CombatDebug on|off` — verbose per-cast combat logging at runtime.

**Living shard:**

- `[BotGuilds` — guild rosters with live member counts.
- `[BotSessions [on|off]` — session layer status, live against the curve target, or toggle it.
- `[BotParties [form | convoy | warband]` — list live parties, or force-form a hunt, a guild convoy, or a war band.
- `[BotFactions [fight]` — Order/Chaos counts and active fights, or force a street fight.
- `[BotDuel` — force a bank duel near you.
- `[BotTrade` — force a trade scene.
- `[BotDanger` — list places with recent murder heat.
- Headless test tokens for soaks, no client needed: drop a number into `Data/Live/party_request.txt`, `death_request.txt`, `faction_request.txt`, or `gossip_request.txt`, then watch the console or the matching `*_ack.json`.

**Admin and population:**

- `[GmPanel` — the central GM gump: world setup, spawning, teleporting, cleanup, with confirmations on anything destructive.
- `[GenerateBots` — re-lay the ambient population: BankSitters on bank arrival points, Shoppers on vendor arrival points, the rest roaming Travelers.
- `[GenerateCustomSpawners` — turn the spawn editor's `spawns.json` into real in-game spawners.
- `[LiveMap on|off [seconds]` — stream a live entity snapshot to the map editor.

</details>

<details>
<summary><b>Currently being worked on</b></summary>

<br>

**Cities — the mainland is done.** All eight mainland cities have their road networks, destination clusters, arrival points, and painted areas live:

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

The island cities still need their own waypoint pockets and arrival handling. Six virtue shrines are live as walkable pilgrimage destinations (Chaos, Spirituality, Compassion, Sacrifice, Justice, Honor), each with a server-verified overland trail. Valor and Humility sit on gateless isles and get their pilgrims by Recall. Honesty's island hasn't been authored yet.

**Dungeons.** Still being worked on, mainly the waypoint network the bots walk underground.

</details>

<details>
<summary><b>Credits</b></summary>

<br>

- **[ModernUO](https://github.com/modernuo/ModernUO)** — the game server emulator. GPL-3.0.
- **[ClassicUO](https://github.com/ClassicUO/ClassicUO)** — the open-source UO client. BSD.
- **[Nerun's Distro](https://github.com/Nerun/runuo-nerun-distro)** — the pre-T2A spawn map. Decades of community work.
- **[mirror.ashkantra.de](https://mirror.ashkantra.de/)** — community mirror hosting the EA UO Classic installer.
- **Origin Systems / Electronic Arts** — for making Ultima Online in the first place.
- **Richard Garriott** — for the world we're all still playing in.

The PlayerBots system was built for this project. GPL-3.0.

Ultima Online is © Electronic Arts. This project doesn't redistribute any EA-copyrighted assets; the installer downloads them from a third-party community mirror.

</details>
