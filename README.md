# UO Offline

A single-player, fully offline Ultima Online experience you can play on **Windows, Linux, and the Steam Deck**. One installer sets up everything and runs the whole game on your own machine — no servers, no accounts, no internet required after install.

What makes it feel alive is a custom PlayerBots system that populates Britannia with bots that fight, travel, shop, bank, chat, ride horses, recall across the map, crawl dungeons and loot them by torchlight, form guilds, hunting parties and Order/Chaos war bands, wage the shield war in the streets, duel outside the bank, walk their ore to town by pack llama, run out of arrows and go buy more, die and run back for their corpses, gossip about things that really happened, answer when you say hi, and log off for dinner — so the world feels like a busy 1999 shard instead of an empty map.

Built on [ModernUO](https://github.com/modernuo/ModernUO) and [ClassicUO](https://github.com/ClassicUO/ClassicUO). T2A era, runs entirely on localhost.

---

## What's new — August 2026

The theme of this month's work: **bots that pass for players.** Newest first:

- **Dungeon crawlers loot like it's 1999 — and the bards sing.** Clearing a room now means picking the corpses OVER: gold, gems, scrolls, and reagents all ride home, and a **magic drop** is the run's payday — the crawler blurts it out on the spot ("no way. its magic") and the find hits the event journal, so the bank crowd retells it for days ("{he} pulled a halberd of vanquishing off a corpse down Shame??"). And the era's richest character finally plays like itself — **everywhere, not just underground**: a bard with real **Provocation** works its lute roadside, at the graveyard, or five floors down — its own attacker gets a well-played tune and turns on the nearest other monster ("provoked a dragon onto a dragon") while the bard slips out of the fight, and idle monster pairs get set brawling so there's always a corpse to pick over. Bards now roll one of two era builds: the classic **Eval provo bard**, or the **peace bard** who trades Eval Int for **Peacemaking** — when there's nothing to redirect an attacker onto, the defensive tune calms it down for a skill-scaled window and the bard walks away. Sour notes included, skill-checked, monsters only (the first live test provoked a giant rat onto the town weaver — fixed).
- **The reds got organized.** Player-killers are rebuilt on the era's murderer templates: most are **Red Mages** (the tank mage with a halberd, throwing real Paralyze → e-bolt → explosion combos and re-arming the hally between casts) and the rest field-PK dexxers carrying **Tracking and Hiding** — the dungeon-entrance hunter's build. They work as crews now: every spawner is one gang that converges on a victim together, marches out to **ambush dungeon mouths** ("lurking the mouth of Destard"), and camps shrines where fresh resurrections wander. Mid-fight they bandage, chug cure and heal pots, and break off to reset when a fight sours — and they fight back properly when wildlife jumps the patrol. Reds hunt other bots *and you*; a fresh world seeds its red crews automatically at dungeon entrances and wilderness chokepoints (the map editor can redraw them). Two bugs died in the process: reds no longer quietly *retire* into friendly adventurers when their patrol crosses a graveyard, and a re-derived character now sheds its old class's skills.
- **The 1999 PvP loadout — cheap gear, deep packs.** Equipment now matters the way it did in T2A: not much. Most gear is plain **exceptional crafted** — inexpensive and replaceable, because you were going to lose it — with magic items rare and tier-gated (a *Supremely Accurate Halberd of Vanquishing* or Invulnerability plate exists, but only on the rare veteran; colored ore suits mark the rich). Tank mages field the era rig: **medable leather** (full plate meant meditation penalties; some mix in a plate arm or gorget), often with the robe worn over. And snooping a veteran's pack now shows the classic wall of bottles: 75–100 of every reagent, stacks of Greater Heal / Greater Cure / Total Refresh, strength, agility and explosion pots, **trapped pouches** (the era essential — popping one broke Paralyze), a spare halberd, recall scrolls, bandages, and a modest purse. Dexxers carry the 100-bandage kit with a few recall reagents; veteran lumberjacks wear plate and swing an exceptional — sometimes Vanquishing — **Executioner's Axe**. Everything scales by tier: a Novice carries a starter kit, a Grandmaster the whole hotkeyed arsenal.
- **Real T2A character builds — the Hally Mage walks again.** Every bot is now built exactly like a 1999 character: stats obey the era caps (100 per stat, 225 total — a mage is 100/25/100, a dexxer 100/100/25) and each class runs an authentic seven-skill template. Most mages roll the era's king, the **Tank Mage** — GM Magery *and* a weapon line — and carry the famous halberd (some roll war hammer or spear variants; a share stay pure scribes). In a fight they do the real thing: the cast pockets the weapon (the old hands-free rule), they hold their ground at melee range, re-equip, and let the swing land while the next e-bolt winds up. Dexxers are proper dexxers now too — GM weapon/Tactics/Anatomy/Healing/Resist/Wrestling with just enough utility Magery to Recall and Cure — the lumberjack runs the era's nastiest axe burst, and the Bard, Tamer, Smith, and Fisherman all match their classic PvM templates. Two classic specialists joined the population as their own classes: the **Treasure Hunter** (Cartography, Lockpicking, Remove Trap, real Magery — walks around with a shovel and reagents, buys maps off the bank crowd in a real gold-for-map deal or snaps up a fisherman's bottle map at the docks, and answers erupting chest guardians with e-bolts) and the **Merchant** (Item ID, Taste ID, Arms Lore — the richest character in T2A: fine clothes, a heavy purse, appraiser's scales, fights nothing).
- **Party up with the bots and go adventuring.** Three ways, all era-true. Use the normal party gump: target a bot and after a beat it accepts — "im in" — and it's in your party bar for real. Or just talk: shout **"lfg despise anyone"** at the bank and a few free bots answer and join; ask the person next to you **"wanna group?"** and they join or beg off ("cant, im working"). And when a bot shouts its own "LFG despise anyone?", answer **"me"** — the leader sends you a real invite ("aye, come along — inv sent") and you tag along on *their* run. Party members follow you (running to keep up), jump into your fights and defend the group, and when the party ends they say their goodbyes and wander back to their own lives. A member who dies ghosts off to a healer — re-invite them after the res, like everyone did.
- **Talk to the bots — they answer.** Say a bot's name and it turns and responds ("yeah?"). Say hi near the bank and the closest person greets you back — or the room ignores you, which is also exactly 1999. Ask a question and you'll get a shrug ("dunno", "no idea m8" — they're players, not tour guides). One reply per remark, after a human typing delay, and the AFK macroers stay silent, because they're *away*.
- **No more roleplay theater.** Every `*emote*` is gone from the entire shard — real players never typed "*hands over the coins*". Actions are silent or spoken the way 1999 typed them: "gl" / "gf" around duels, "ty" when coins change hands, "gold or die" on the road (the stage-highwayman "Stand and deliver!" is dead), and browsing a shop says "vendor buy", not inner monologue. The whole chat corpus got the same pass — lowercase, terse, shorthand, occasionally misspelled.
- **The bank is a 1999 bank now.** The permanent crowd at every bank grew to five and split into the real cast: regulars talking trade, hawkers spamming WTS every few seconds, statues who said "afk" an hour ago, someone raising **resist** by genuinely casting curse on himself over and over — the real spell system: words of power, chant, cast delay, fizzles, reagents burned from the pack and restocked from the bank box — someone blinking in and out of **hiding**, and someone creeping circles training **stealth**, who occasionally fails, pops visible, crouches, and vanishes again.
- **Stables are real places now.** Pack animals live at the stables: a miner heading to work stops there first, says **"vendor claim"**, and walks out leading a named beast ("Bessie follow me") — and after selling the haul, walks it back ("Bessie stay", "vendor stable"). Tamers work the same counter, boarding and claiming their horses. And miners and lumberjacks don't own riding horses at all anymore — you can't ride a pack animal, and a working gatherer walked.
- **Newbies walk.** Recall scrolls now scale with wealth: a fresh Novice carries none and walks everywhere — exactly like every new character did — a Journeyman keeps one or two saved-up escapes, and veterans carry the real stack. Buying your first recall scrolls at the mage shop is a rite of passage again.
- **The dungeon underworld got ground-truthed.** Every stair, entrance, and teleporter record in all twelve dungeons was re-verified against the engine itself: mislabeled stairs that sent "exiting" bots *deeper* are re-typed from the real topology (the old Covetous/Hythloth/Deceit level scramble is fixed), a hundred phantom graph edges and three dozen waypoints on unstandable tiles are gone, every remaining teleporter pad was probe-walked and confirmed firing, and bots now queue politely when someone's standing on the stairs. Deceit's depths are reachable by walking the Destard passage; Terathan Keep exits on foot through Despise — the cross-dungeon links work.
- **Recall is how everyone (established) gets around** — the way it actually was. Travel magery or real **recall scrolls** from the pack; long trips recall, GM mages open public gates, and a wedged bot **casts its way out** ("Kal Ort Por", flash, gone). Ferries are gone — never a T2A thing; the outer isles are reached by magic.
- **Real supplies, real shopping.** Arrows, reagents, bandages, and scrolls genuinely run out; bots stop what they're doing and go buy more — visibly, in gold. Hunters break off hunts; crawlers cut dungeon runs short ("out of supplies — heading up").
- **Guild convoys and the Order/Chaos war bands.** Guildmates walk road trips together; faction squads patrol — and deliberately intercept enemy patrols. When shields meet, nearby faction-mates pile in (up to 4v4 street battles).
- **Smarter fights.** Fighters bandage and cure mid-fight, retreat earlier when swarmed, and skilled mages answer a charging monster with the classic **Paralyze** — freeze, step back, resume the barrage.
- **Dungeon crawlers behave like people.** They loot the gold off their kills, carry lit torches into the dark, stop picking fights on the way OUT, and recall home when the stairs won't cooperate.
- **Gossip got personal.** Bots tell their own stories in first person ("i got murdered by Sarn at the crossroads, watch yourself"); war-band clashes and guild outings make the bank rounds, and old news fades.
- **An era-true wardrobe.** Every clothing hue comes from the classic dye-tub range — true black stays the rare flex it was. Mana potions (which didn't exist yet) are gone from mage kits.
- **The shard watches itself.** A live "Stuck & Rescues" status section and a telemetry feed map trouble hotspots, and the fleet routes around chokepoint road edges automatically.

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

**Bot identity.** Every bot has a class (Warrior, Mage, Fencer, Archer, Tamer, Healer, Thief, Bard, Ranger, Treasure Hunter, Merchant, plus the working classes: Smith, Tailor, Fisherman, Lumberjack, Miner) and a skill tier (Novice through Grandmaster, bell-curve distributed). Skills, stats, equipment, and behavior preferences all derive from class + tier, and every build obeys the real T2A caps: 100 per stat, 225 total, seven-skill templates straight from 1999. A Grandmaster Mage really IS a Grandmaster Mage — usually a **Tank Mage** at 100/25/100 with GM Magery, Eval, Med, Resist, Wrestling *and* a weapon line, halberd in hand ("Hally Mage"), fancy hued robe, filled spellbook. Warriors are proper pure dexxers (100/100/25 with utility Magery for Recall), lumberjacks swing the era's highest-burst axe, and the Bard/Tamer/Smith/Fisherman run their classic PvM templates.

**Unique names + home cities.** No two live bots share a name. A minority carry surnames ("Tessa Ravenwood", "Halric the Grey", "Mara of Yew"), and a few use the handles real 1999 players did — Gandalf, Drizzt, lowercase "bob". Every bot also has a home city its travels favor, so *regulars* emerge: keep visiting the Britain forge and you keep seeing the same smith.

**Player guilds + the Order/Chaos war.** Thirteen era-flavored guilds ("The Undead Lords", "DOOM", "Knights of Yew"…) with big-zerg and small-crew rosters; ~40% of bots wear a `[TAG]`. Six guilds carry Order or Chaos shields — and opposing shields fight **on sight, in town, guards ignoring it**, exactly as T2A worked. Street fights outside the bank are back.

**Login/logout sessions.** Bots have play sessions, not eternal existence: they log in, play 1–4 hours, say "gtg dinner", and vanish — and the population follows a daily curve (dead at 5am, packed in the evening). Fresh spawns are logins ("hey all", "what did i miss").

**The event journal + gossip.** The shard keeps a journal of everything notable — kills, deaths, murders, duels, hunts, red sightings — and bots at banks *retell real events*: "Aldreth got pked at despise earlier!!" is only ever said if it actually happened. Bots that hunted or dueled together become friends and greet each other by first name for the rest of their lives.

**Hunting parties.** The LFG spam concludes: a fighter broadcasts "LFG despise anyone?", nearby bots answer and converge, and the group marches down real roads to a dungeon, enters together, and fights as a unit until the run ends with "gg all". Guildmates and friends get invited first — and **you can answer too**: say "me" and the leader sends you a real party invite.

**Play WITH them.** The bots treat you like another player. Say a bot's name and it turns and answers; greet the bank and somebody greets back (or nobody does — also authentic); ask a question and get a shrug ("no idea m8"). Form a group through the normal party gump, by shouting "lfg despise anyone", or by asking "wanna group?" — members follow you, run to keep up, jump into your fights, defend the group, and beg off in character when they're busy ("cant, im working"). Beggars and lost newbies latch onto you and follow you across the plaza.

**Real deaths + corpse runs.** Novices misjudge fights (retreat thresholds scale with experience) and sometimes die. Then UO's most iconic experience plays out: the ghost haunts its corpse moaning OoOoOo, walks to a healer or shrine, resurrects in a death robe, and runs back hoping the loot's still there — self-looting its own corpse the vanilla way, or wailing "WHO LOOTED MY CORPSE" if it rotted.

**PK ecology + region danger.** The reds run the era's murderer builds — mostly **Red Mages** (tank mages casting real Paralyze/e-bolt/explosion between halberd swings) with field-PK dexxers (Tracking, Hiding) filling out the crew — at Master/Grandmaster strength, carrying the full explosion-pot-and-trapped-pouch kit. Every spawner is one gang: they hunt in packs, converge on a victim together, march out to **ambush dungeon entrances**, camp shrines, bandage and chug potions mid-fight, and only commit when the crowd math favors them. A fresh world seeds red crews automatically at dungeon mouths and wilderness chokepoints; the map editor can redraw them. Murders heat a danger map; hot places drain of foot traffic as the population routes around them. A civilian who spots a red screams "RED AT {PLACE}!!", the sighting hits the gossip mill, and nearby travelers scatter.

**A visible economy.** Lumberjacks and Miners work real wilderness sites (40 generated across the map), fill their packs with actual logs and ore, and haul the load to town — selling to a working crafter in a coins-for-materials scene, or banking it. Adventurers buy from crafters ("how much for a katana" → "800 gold" → "ty" → "sold!"), and the endless WTS bank spam occasionally *concludes* with a real deal.

**Duels outside the bank.** Two fighters call a challenge ("1v1 me"), trade a "gl", walk ten tiles clear of the crowd, and fight to low health — never to the death — then close with "gf" while the loser demands a rematch (or blames lag). Era-perfect, legal in town.

**Recall is the transport.** Exactly as in 1998: casters keep travel Magery, and established characters carry **recall scrolls** — scaled by wealth, because a fresh Novice carries none and walks everywhere, exactly like every new character did; buying your first scrolls at the mage shop is a rite of passage. Long trips go by magic scaled to distance, GM mages open **real public gates** anyone can hop through, and the gateless outer isles — Valor, Humility, Dagger Isle, Fire Isle — get their pilgrims by Recall, since that's the only way short of owning a boat. A stranded or hopelessly wedged bot recalls out too, the single most era-true escape there is.

**Supply runs.** Consumables are real: bows eat arrows, casts eat reagents, bandages get used up — and **nothing refills invisibly**. When a bot runs low it leaves what it's doing and goes shopping: the bowyer for arrows, the provisioner for bandages, the mage shop for reagents and scrolls, or the bank box reserve. The purchase happens on arrival, visibly, for gold. Hunters break off hunts for it; crawlers cut dungeon runs short for it.

**Permanent bank crowds — the full 1999 cast.** Every bank keeps a standing crowd of five: regulars talking trade, hawkers spamming WTS, statues who said "afk" an hour ago, someone **actually casting** curse on himself over and over to raise resist (real spell system — words, chant, cast delay, reagents burned from the pack, refilled from the bank box when the pouch runs dry), someone blinking in and out of hiding, and someone creeping circles training stealth. Individuals may die and be replaced, but the crowd is eternal.

**Guild convoys and war bands.** Guildmates muster and walk road trips together ("guild trip to trinsic, who walks with me?"), fighting as a group when the road bites back and dispersing into the destination on arrival. Order/Chaos squads patrol to faction-flavored spots — and new bands deliberately set intercept courses on enemy patrols. When opposing shields sight each other, nearby faction-mates are drafted in (up to 4v4), and any band involved dissolves into the battle.

**Pack animals live at the stables.** Miners and lumberjacks own no riding horses — you can't ride a pack animal, and a working gatherer walked. Heading out, they stop at the stables ("vendor claim") and lead out a named beast ("Bessie follow me"); the yield loads onto it — double the haul — and after selling in town they walk it back ("Bessie stay", "vendor stable"). Tamers work the same counter, boarding and claiming their horses.

**Treasure hunts.** Maps change hands before anyone digs: a Treasure Hunter buys one off another player at the bank ("wts treasure map, found it on a brigand" → "deal. hand it over" → the coin clinks) or takes a fisherman's bottle map at the docks, while a fighter's map has its own story — looted "off a dead brigand". The rolled-up map rides in the pack for the whole trip (snoop it at the bank; find it on the corpse if the trip goes wrong). Then the hunter walks out to one of 24 wilderness dig sites, digs with shovel swings and sounds — and the guardians erupt from the ground mid-dig. Fight them down, pry open the unearthed chest ("GOLD! actual gold!!"), pocket the coin, and carry the story back to the bank where the gossip mill spreads it.

**The fishing SOS.** A fisherman working a pier occasionally reels in a corked bottle with a map inside, and hawks it on the spot ("i fish, i dont dig. map for sale"). If an adventurer is standing around the docks, the map changes hands and a real treasure hunt sets out; if not, the tale still makes the rounds.

**Visible taming.** Tamer bots stalk wild animals and work them with the classic client spam ("I've always wanted an animal like you", "Will you be my friend?") — sometimes the beast shies away, sometimes it submits. Tamed pets follow their master through town, get hawked at the bank ("selling {pet}, 2k firm"), and either sell to a bystander bot or get released with a shrug. No permanent pet ever accumulates.

**Bot homesteads.** Small era houses (stone cottages, log cabins, thatched-roof cottages) scattered along the wilderness roads, placed with the REAL house placement rules, each with a locked door and a named sign ("Aldric's cottage") — ownerless, ageless, and fully removable (`[BotHouses scatter/clear`).

**Gear progression.** Dungeon runs pay: survive three and the next bank visit is shopping day — a visible tier promotion with better skills and kit ("finally saved up for new gear"). Regulars get better gear over weeks.

**Street characters.** Banks grow their own street life: the beggar ("gold plz") and the lost newbie ("how do i get to minoc??") — both of whom will latch onto a real player and follow them across the plaza.

**Chatter with texture — and zero roleplay theater.** Era voice throughout ("ne1", "thx m8", rare all-caps drama), late-night lines after 9pm, nervy lines inside dungeons ("something ahead", "i had looting rights on that"), gossip about real events, and the occasional "asdf" / "oops wrong window". No bot ever types an `*emote*` — actions are silent or spoken the way 1999 typed them. Ghost speech garbles for the living, exactly as it should.

**Shard status page.** `Data/Live/status.html` regenerates every minute: who's online (names, guild tags, class/tier, what they're doing, where), population vs the daily curve, live counts of hunting parties / guild convoys / war bands, a Latest News feed straight from the event journal (war-band clashes included), and a "Stuck & Rescues" telemetry section — the classic 1999 shard status page, telling the truth about 400 bots.

**Equipment variety — strictly era.** Beyond class signatures (Warriors in plate, Mages in robes), every bot rolls universal accessories: hats from the classic 1998 set (floppy hat, jester hat, feathered cap, tricorne…), cloaks and sashes dyed **only in colors the T2A dye tub could actually mix** (with true black as the rare holiday-tub flex), beards, varied hair. Metal armor is iron or genuine colored ore; magic gear uses the real era system (Ruin → Vanquishing, Exceptional maker's marks that GM crafter bots *announce* when they pull one off). Some Warriors wear chain or studded instead of plate; some Mages wear studded leather. The visual feel matches a classic UO bank gathering — and nothing on anyone's back postdates 1998.

**Mounts.** Most bots spawn mounted on a horse, ostard, or llama (working folk excepted: gatherers walk with their pack beasts, and fishermen work the pier on foot). Horse coat colors vary realistically (browns, grays, palominos). Mounted bots move at proper UO mount speed. Mounts despawn cleanly with their rider on death or removal.

**Behaviors.** Bots run one of many behaviors, swapped by the lifecycle system and by arriving at the right kind of place:
- **Idle / Wander** — light local movement.
- **BankSitter** — stands at a bank, chats (and occasionally challenges someone to a duel or closes a WTS deal).
- **Traveler** — walks or rides between destinations along the waypoint road network.
- **Shopper** — stands at a vendor area and browses ("vendor buy", browsing chatter), then moves on.
- **Crafter** — settles at its station (Smith → Forge, Tailor → shop, Fisherman → dock) for long working sessions, producing real goods.
- **Gatherer** — works a wilderness site (chop/mine animations, real logs and ore into the pack), then hauls to town to sell.
- **Adventurer** — full combat: melee, archery, and real magic (spell ladders up to Flamestrike, kiting, target switching, threat assessment); retreats scale with experience.
- **DungeonCrawler** — enters dungeons through the real entrance teleporters (torch lit if a hand is free), sweeps rooms floor by floor (skill-weighted descent — novices stay shallow), **loots the gold off its kills**, camps respawns, and climbs out when the run timer expires or the supplies run dry — fighting only in self-defense on the way up, because leaving should look like leaving.
- **PartyMember / Duelist / Ghost / CorpseReclaim / Beggar / Newbie** — the hunting-party follower, the bank duelist, the death story, and the street characters.
- **PlayerGroup** — a bot adventuring in YOUR party: follows your lead, fights your fights, says goodbye when the group ends.
- **TreasureHunter** — the dig-site scene: dig, fight the risen guardians, open the chest, walk home rich.
- **Tamer** — stalks a wild animal, tames it with the era spam, parades it to town, and sells it.
- **PK** — hostile player-killer behavior (and the reason civilians scream RED).

**Destinations, waypoints, and zones.** Travelers go to actual *places*, not random spots. The world is described by three layers:
- **Waypoints** — the road network. A graph of nodes Travelers thread with A*/Dijkstra routing, hot-reloadable via `[ReloadWaypoints`.
- **Destinations** — places of interest (banks, vendors, taverns, healers, moongates, dungeon entrances), class-weighted so Bards prefer taverns, Crafters prefer forges, etc.
- **Zones** — painted *areas* (open regions like bank plazas and docks where a behavior happens throughout) and *portals* (doorway thresholds). Managed by `ZoneRegistry`, hot-reloadable via `[ReloadZones`.

**Arrival points.** The key to bots reaching places they can actually stand. A destination can carry one or more **arrival points** — specific reachable tiles (a vendor counter, a doorstep, a moongate teleporter) — each with its own preferred route waypoints. A bot picks one arrival point, routes to the nearest of its waypoints, and arrives *on a standable tile* instead of grinding a wall trying to reach an unreachable interior coordinate. This is what lets a Shopper stop at the counter and a Traveler step onto a moongate cleanly.

**Moongate, Recall, and Gate travel.** Bots that reach a moongate have a high chance to step through and emerge at another city's gate — circulating the population across Britannia. Long hauls reroute through the gate network automatically, and cross-water city trips split naturally between Recall and the moongates so the gate plazas keep their crowds. Casters Recall on mana; scroll users spend their stack thriftily (scrolls cost gold — they save them for the long hauls), and GM mages open a **real** Gate Travel pair ("gate to despise up, hurry") that lingers for anyone — players included — to hop through.

**Combat.** Adventurers engage hostile creatures with class-appropriate fighting: melee with attack-slot fanning (bots surround a monster instead of stacking), archer/mage kiting, and a real spell book from Magic Arrow to Flamestrike with era-correct openers — including **Paralyze** against a monster closing on a skilled mage, and cure potions when the poison lands. Fighters bandage *mid-fight* on a fast combat pulse (and the bandage actually completes), retreat thresholds scale with experience **and with how many monsters are piling on** — surviving a swarm means leaving earlier — and after a rough win, fighters quietly bandage up while casters hum through meditation. Bots respect notoriety — no attacking innocents or wildlife.

**Stuck recovery.** When bots get pinned against terrain, automatic detection nudges them in a walkable direction, opens doors in the way, and repaths — escalating through sidesteps and wedge extraction to a full **recall-out** for a bot that can cast or carries a scroll (a wedged 1998 player's actual move). Every firing feeds a live telemetry ledger: the status page shows a "Stuck & Rescues" section with trouble hotspots, `Data/Live/stuck_report.json` feeds tooling, and road edges that keep defeating bots take a temporary routing penalty so **the whole fleet detours around chokepoints automatically**.

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
- `[BotParties [form | convoy | warband]` — list live parties of all kinds, or force-form a hunt, a guild convoy, or a faction war band.
- `[BotFactions [fight]` — Order/Chaos counts and active fights, or force a street fight.
- `[BotDuel` — force a bank duel near you.
- `[BotTrade` — force a trade scene (crafter purchase or WTS deal).
- `[BotDanger` — list places with recent murder heat.
- Headless test tokens (for soaks, no client needed): drop a number into `Data/Live/party_request.txt`, `death_request.txt`, `faction_request.txt`, or `gossip_request.txt` (composes a batch of gossip lines into `gossip_ack.json`) and watch the console / `*_ack.json`.

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

The remaining island cities need their own waypoint pockets and arrival handling. Six virtue shrines are live as walkable pilgrimage destinations (Chaos, Spirituality, Compassion, Sacrifice, Justice, Honor — each with a server-verified overland trail); Valor and Humility sit on gateless isles and receive their pilgrims by Recall, as the era intended. Honesty's island still needs its authoring.

**Dungeons — all twelve wired up and now ground-truthed.** Every Felucca dungeon has generated interiors (floor meshes, rooms, and teleporter records for entrances, descends, ascends, and the cross-dungeon passages), six overland approach trails, and Recall service to the isle dungeons. The big cleanup pass has happened: every stair record was re-typed against the real teleporter topology (the old scrambled level numbering is gone), the waypoint graph was audited against the engine itself — phantom edges and unstandable nodes cut, with the edge-walk audit reporting **zero blocked links** — and a probe was walked onto every remaining teleporter pad to confirm it fires. Floors the engine says are genuinely sealed pockets are treated honestly: bots inside hunt what they can reach and recall out, era-style.

**What's left underground** is the long tail: a few tight corridors where crawlers still jam under monster traffic (the telemetry flags them), and the deep Hythloth floors whose T2A geometry sits under later-era region labels and hasn't been meshed yet.

**Known issues being fixed.**

- **Bots stuck at login** — bots that don't resume their routine after coming back into the world.
- **Player houses** — bot behavior in and around player-owned housing.

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
