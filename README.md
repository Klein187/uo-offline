# UO Offline

A one-command installer for offline single-player Ultima Online on Linux and Steam Deck, with a custom PlayerBots system that populates Britannia with bots that fight, travel, chat, ride horses, and live their own lives.

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

**2.** Done. Bots are now alive. The Lifecycle system takes over — bank-sitters transition to adventurers, adventurers travel to dungeons, etc.

---

## Features

**Bot identity.** Every bot has a class (Warrior, Mage, Fencer, Archer, Tamer, Crafter, Healer, Thief, Bard, Ranger) and a skill tier (Novice through Grandmaster, bell-curve distributed). Skills, stats, equipment, and behavior preferences all derive from class + tier. A Grandmaster Mage really IS a Grandmaster Mage — 99 Magery, 125 Int, with a fancy hued robe and spellbook.

**Equipment variety.** Beyond class signatures (Warriors in plate, Mages in robes), every bot rolls universal accessories: hats from 18 types (floppy hat, jester hat, feathered cap, tribal mask, etc.), cloaks in any color, body sashes, beards, varied hair. Some Warriors wear chain or studded instead of plate. Some Mages wear studded leather. The visual feel matches classic UO bank gatherings.

**Mounts.** 70% of bots spawn mounted on a horse, ostard, or llama. Horse coat colors vary realistically (browns, grays, palominos). Mounted bots move at proper UO mount speed. Mounts despawn cleanly with their rider on death or removal.

**Behaviors.** Five distinct bot behaviors: Idle, Wander, BankSitter, Adventurer (real combat — sword swings, retreat at low HP, permadeath), and Traveler (walks/rides between destinations along roads).

**Destinations and waypoints.** Travelers don't just wander to random forest spots — they go to actual places. 17 destinations in Britain so far (bank, tailor, alchemist, inn, healer, bowyer, weaponer, bakery, tavern). Class-weighted destination picking: Bards prefer taverns, Crafters prefer smithies, Mages prefer the moongate. The waypoint graph has 35 nodes covering Britain's road network, with hot-reload via `[ReloadWaypoints`.

**Combat.** Adventurers engage hostile creatures (negative-Karma monsters) in melee, retreat when wounded, die permanently, get replaced by their spawner. Bots respect the notoriety system — they don't attack innocents or wildlife. No magic combat yet — coming.

**Stuck recovery.** When bots get pinned against terrain (lightposts, corners, walls), automatic detection nudges them 3 tiles in a random walkable direction every 4 seconds until they're free.

**Navigation.** Short-range pathfinding via ModernUO's A*. Long-range via the waypoint graph: Dijkstra picks routes; PathFollower walks each leg. Bots fire dungeon entrance teleporters naturally (no fake "go inside" magic — they actually step on the teleporter tile).

**Lifecycle.** Every bot has a personality — weighted tendencies toward each behavior plus optional traits (Restless, Homebody, Brave, Cautious, Wealthy, Rough). Every 60 seconds the lifecycle manager evaluates each bot; if their current phase (30-180 minutes) has elapsed, they transition. Smart placement: bots becoming Adventurers walk or recall to a dungeon, BankSitters teleport to a random bank.

**Admin tools.** `[GmPanel` is the central GM gump for everything. World setup, spawning bots with custom behavior mixes, teleporting to cities and dungeons, cleanup (single-target remove, mass spawner remove, all with confirmation gumps for destructive actions).

**World-marking commands** for capturing waypoints and destinations as you walk:
- `[MarkWay <name>` — record a waypoint at your position, auto-connects to neighbors within 38 tiles
- `[MarkSpot <name> <type>` — record a destination (Bank, Tavern, Inn, VendorSmith, etc.) with auto-detected nearest waypoint
- Both write JSON-ready snippets to draft files, ready to integrate into the live data

**Diagnostics.** `[BotInfo` targets a bot and dumps their class, tier, stats, skills, and notoriety. `[BotGoals` shows every bot's current state and destination. `[LifecycleStatus` for system health. `[SetBotVerbose true` enables per-bot debug logging.

---

## Currently being worked on

- **Expanding the waypoint graph beyond Britain.** Trinsic, Vesper, Yew, Minoc, Moonglow, etc. Each city needs its own road network + destinations cluster.
- **Verifying dungeon interior coordinates.** Despise is verified; the other 8 dungeons have placeholder coords pending in-game verification.
- **Special dungeon-entrance waypoints.** A waypoint flag system that lets bots walk to a dungeon entrance teleporter and step on it naturally, replacing the current teleport-with-pause fallback.

## What's coming

- **Death and resurrection.** Dead adventurer bots walk as ghosts to the nearest healer, get resurrected, walk back to their corpse, re-equip their loot.
- **More behaviors.** Shopper, Crafter (with smithing animations), PK, Tamer (with pets), Mage combat.
- **Random events.** Zombie invasions of towns. Bots will engage them — guards alone won't be enough.
- **Per-personality chat.** A Wealthy bot says different things than a Rough bot.
- **Bot story memory.** Click a bot to see their recent history — "this bot was in Despise yesterday, traveled to Britain this morning."

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
