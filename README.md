# UO Offline

A one-command installer for offline single-player Ultima Online on Linux and Steam Deck, with a custom PlayerBots system that populates Britannia with bots that fight, travel, chat, and live their own lives.

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

**1.** Open chat (press Enter) and run these six commands, one at a time:

```
[Decorate
[SignGen
[TelGen
[MoonGen
[TownCriers
[GenerateSpawners Spawners/uoclassic/UOClassic.map
```

This spawns ~1700 vendors, monsters, signs, moongates, and town criers across the world.

**2.** Type `[BotPanel` to open the bot admin panel. This is the GM panel we built to simplify setup. Click **Run All** under "Fresh World Setup" — it seeds bots at every bank in the world. Done.

Now the Lifecycle system takes over. Bots gradually transition between roles — bank-sitters become adventurers, adventurers travel to other cities, etc. The world stays alive as long as the server is running.

---

## Features

**Behaviors.** Five distinct bot behaviors: Idle, Wander, BankSitter, Adventurer (with real combat — sword swings, retreat at low HP, permadeath), and Traveler (walks between cities along roads).

**Combat.** Adventurers engage hostile creatures in melee, retreat when wounded, die permanently, get replaced by their spawner. No magic yet — that's coming.

**Navigation.** Short-range pathfinding via ModernUO's A*. Long-range navigation via a waypoint graph: a curated set of named locations across Britannia, each linked to its neighbors. Dijkstra picks routes; PathFollower walks each leg.

**Lifecycle.** Every bot has a personality — weighted tendencies toward each behavior plus optional traits (Restless, Homebody, Brave, Cautious, Wealthy, Rough). Every 60 seconds the lifecycle manager evaluates each bot; if their current phase (30-180 minutes) has elapsed, they transition. Smart placement: a bot becoming a BankSitter teleports to a random bank, an Adventurer to a random dungeon interior. The world feels demographically alive.

**Admin tools.** `[BotPanel` is the central GM gump for everything bot-related. Spawn bots at custom locations, teleport to cities and dungeons, manage spawners, save the world. Plus `[BotGoals` to see what every bot is doing, `[LifecycleStatus` for system health, `[ReloadWaypoints` for hot-reloading the navigation graph after edits.

---

## Currently being worked on

- **Expanding the waypoint graph.** Right now it's a 7-node chain along the south road out of Britain. Adding waypoints in other cities and on roads between them is the highest-impact way to make travelers more interesting.
- **Verifying dungeon interior coordinates.** Despise is verified; the other 8 dungeons have placeholder coords that need real values from inside each dungeon.

## What's coming

- **Death and resurrection.** Dead adventurer bots walk as ghosts to the nearest healer, get resurrected, walk back to their corpse, re-equip their loot.
- **More behaviors.** Shopper, Crafter, PK (murderer), Tamer, Mage.
- **Per-personality chat.** A Wealthy bot says different things than a Rough bot.
- **Bot story memory.** Click a bot to see their recent history — "this bot was in Despise yesterday, traveled to Britain this morning."
- **Magic combat.** High-Int adventurers cast spells instead of swinging swords.

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
