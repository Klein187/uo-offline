# UO Offline

> One-command installer for offline single-player Ultima Online on Linux and Steam Deck — now with PlayerBots that make the world feel alive.

T2A era, runs entirely on localhost. Bundles [ModernUO](https://github.com/modernuo/ModernUO) (server) and [ClassicUO](https://github.com/ClassicUO/ClassicUO) (client). One command installs both, plus the UO Classic 7.0.23.1 game data, Nerun's pre-T2A spawn map, and a custom **PlayerBots** system that populates Britannia with bots that fight, travel, chat, and live their own lives.

**Tested on:** Steam Deck (SteamOS 3.x, Desktop Mode). Should work on any modern x86_64 Linux distro with apt, pacman, or dnf.

---

## What's New: PlayerBots

The world is no longer empty of players. After install, Britannia is populated with bots that:

- **Sit at banks**, chat about banking, hang around in town squares
- **Adventure in dungeons** — engage hostile creatures, fight in real combat (sword swings, blood, the works), retreat when wounded, die permanently
- **Travel between cities** along roads, using a waypoint graph for navigation
- **Drift between roles over time** — a bank-sitter in Trinsic might decide to become an adventurer in Despise, then later travel back as a banker in Vesper

Different bots have different personalities (rolled randomly per bot), so the world's population feels varied. See the **Features** section below for details.

---

## Install (Steam Deck / Konsole)

If you've never used a terminal: don't worry. To run a command, copy the whole line into Konsole and press Enter.

### 1. Open Konsole

On Steam Deck: **STEAM** button → **Power** → **Switch to Desktop**, then **Application Launcher** → **System** → **Konsole**.

### 2. Prepare Steam Deck (skip if you're on regular Linux)

SteamOS won't let you install software by default. Run these one at a time.

Set a password if you've never set one:

```
passwd
```

Type it twice. You won't see the letters as you type — that's normal. Remember it; you'll need it whenever something asks for "sudo".

Allow installs:

```
sudo steamos-readonly disable
sudo pacman-key --init
sudo pacman-key --populate
```

### 3. Get the installer

Download `uo-offline.zip` from the [Releases page](../../releases) to your **Downloads** folder, then unzip it:

```
cd ~/Downloads
unzip uo-offline.zip
```

### 4. Run the installer

```
cd ~/Downloads/uo-offline && chmod +x install.sh scripts/*.sh uninstall.sh install-playerbots.sh && ./install.sh
```

The installer will:

- Install Linux dependencies (asks for your sudo password).
- Clone and build ModernUO (~3 minutes).
- Download and install .NET 10 (~200 MB).
- **Deploy the PlayerBots system into the ModernUO source tree and compile them in.**
- Download ClassicUO from GitHub releases.
- Download UO Classic 7.0.23.1 game data from a community mirror (~929 MB, takes 5-15 minutes).
- Download Nerun's pre-T2A spawn map.
- Write all the configs.
- Add a "UO Offline" icon to your desktop.

Total install time: roughly 15-25 minutes on a typical home connection.

### 5. First launch

Double-click the **UO Offline** desktop icon. Behind the scenes:

1. Server starts, owner account is created automatically.
2. ClassicUO opens to the login screen.
3. Log in: `admin` / `admin`.
4. Create a character, pick any starting city.

You're in Britannia.

### 6. Populate the world

On first launch the world is empty — no NPCs, no signs, no monsters, no moongates. To populate it, open the in-game chat (press Enter) and type these six commands, one at a time:

```
[Decorate
[SignGen
[TelGen
[MoonGen
[TownCriers
[GenerateSpawners Spawners/uoclassic/UOClassic.map
```

Each one prints a progress message and takes a few seconds. The last one — `GenerateSpawners` — is the big one: it spawns ~1700 spawn points across Britannia (vendors, guards, monsters, animals) in under 3 seconds.

You only do this once. The state persists with the world save.

A copy of these commands lives at `~/uo-modernuo/POPULATE-WORLD.txt` if you forget.

### 7. Spawn the PlayerBots

In-game, type:

```
[BotPanel
```

This opens the bot admin panel. Click **Run All** in the "Fresh World Setup" section to seed bots at every bank in the world (~80 BankSitters across 9 cities). Then explore Britannia — bots are already there, walking around, chatting.

For dungeons, use the **Dungeons (Inside)** section to teleport yourself inside, then commit a spawner with Adventurer behavior.

The Lifecycle system is on by default, so the bots will gradually transition between roles over time on their own.

### Upgrading an existing install

If you already have v1.0.0 installed and just want to add bots without reinstalling everything:

```
cd ~/Downloads/uo-offline && ./install-playerbots.sh
```

This deploys the bot files and rebuilds ModernUO in place.

---

## Features

### Behaviors

Each bot runs one behavior at a time; the lifecycle system swaps behaviors over time.

| Behavior | What it does |
|----------|--------------|
| **Idle** | Stands around, occasional ambient chat. |
| **Wander** | Drifts within a small radius. |
| **BankSitter** | Hangs near a bank, chats about banking topics. |
| **Adventurer** | Patrols, engages hostile creatures in melee combat, retreats at low HP. Permadeath. |
| **Traveler** | Walks between named destinations via a waypoint graph. Lingers on arrival. |

### Combat

- Targets non-tame, non-summoned hostile creatures in sight range.
- Engages via standard combat (melee swings, hit/miss, damage, death).
- Retreats to safety at 30% HP, running with the proper animation.
- Permadeath — when a bot dies, its corpse drops; the spawner replaces it.
- Currently melee only; no magic.

### Movement

Two-timer architecture for smooth animation:

- **Decision tick (2 sec):** the bot thinks — picks goals, targets, retreat decisions.
- **Step timer (400ms walk / 200ms run):** takes one tile per fire, animating naturally at proper UO speed.

**Short-range pathfinding** uses ModernUO's built-in A* (`PathFollower`) for routing around obstacles.

**Long-range pathfinding** uses a waypoint graph: a curated set of named locations across the world, each within 38 tiles of its neighbors. Dijkstra finds a route through the graph; PathFollower handles each leg. The starter graph covers the south road out of Britain; you can expand it by editing `Data/Waypoints/waypoints.json` and running `[ReloadWaypoints` in-game.

### Lifecycle

Every bot has a **personality** — weighted tendencies toward each behavior plus optional traits (Restless, Homebody, Brave, Cautious, Wealthy, Rough). The lifecycle manager evaluates each bot every 60 seconds:

- If their phase duration has elapsed (30-180 minutes per bot), they transition to a new behavior weighted by personality
- Transitions are **smart**: a bot becoming a BankSitter teleports to a random bank; a bot becoming an Adventurer teleports to a random dungeon interior
- Transitions are **staggered** — max 5 bots transition per minute — so the world evolves smoothly without sudden chaos

The result: the world feels demographically alive. Different bots in different places at different times. You'll see a bot at the Britain bank today and find him in Trinsic next session.

### Admin tools

- `[BotPanel` — central admin gump. Spawn bots at custom locations, teleport to cities/dungeons, manage spawners, clear bots, save world.
- `[BotGoals` — list every bot's current state and destination.
- `[LifecycleStatus` — show the lifecycle system's status.
- `[SetLifecycle true/false` — toggle the lifecycle.
- `[ForceLifecycleTick` — trigger transitions immediately (useful for testing).
- `[ReloadWaypoints` — re-read `waypoints.json` without a server restart.

---

## Daily use

| Command | What it does |
|---------|--------------|
| `./start.sh` | Launch server and client. Same as the desktop icon. |
| `./stop.sh` | Force-stop the server (only needed if it's stuck). |
| `./reset-first-launch.sh` | Wipe world saves, redo first-launch flow. |
| `./uninstall.sh` | Remove everything except your UO data folder. |

**Closing the client closes the server.** When you exit ClassicUO, the server saves the world and shuts down automatically.

If you want the server to keep running after the client exits (e.g. relaunching the client, or connecting from another machine on your LAN):

```
KEEP_SERVER_RUNNING=1 ./start.sh
```

---

## In-game commands

You start as the `admin` owner account with full GM powers.

### Vanilla ModernUO

| Command | What it does |
|---------|--------------|
| `[where` | Show your coordinates. |
| `[go britain` | Teleport to Britain. |
| `[go destard` | Teleport to a dragon dungeon. |
| `[m` | Toggle GM movement (walk through walls). |
| `[invul` | Toggle invulnerability. |
| `[password new` | Change your admin password. |
| `[help` | Full command list. |

### PlayerBots

| Command | What it does |
|---------|--------------|
| `[BotPanel` | Open the bot admin gump. Most actions are accessible here. |
| `[BotGoals` | List every bot's current behavior and destination. |
| `[LifecycleStatus` | Show lifecycle stats. |
| `[SetLifecycle true/false` | Toggle the lifecycle system. |
| `[ForceLifecycleTick [name]` | Trigger lifecycle transitions immediately (all bots, or named bot). |
| `[SetBotVerbose true/false` | Toggle traveler navigation logging. |
| `[ReloadWaypoints` | Reload `waypoints.json` after editing it. |

---

## Currently being worked on

- **Expanding the waypoint graph.** The starter graph is a 7-node chain along the south road out of Britain. Adding more waypoints (especially in other cities and on roads between them) is the highest-impact way to make travelers more interesting. See `Data/Waypoints/waypoints.json` and the [LIFECYCLE-DESIGN.md](LIFECYCLE-DESIGN.md) doc for the design.
- **Verifying dungeon interior coordinates.** Despise interior is verified; the other 8 dungeons have placeholder coords that need to be replaced with real `[where` values from inside each dungeon.

## Future plans

- **Death and resurrection.** When an Adventurer bot dies, walk as a ghost to the nearest healer, get resurrected, walk back to corpse, re-equip loot. The full UO ghost-run experience.
- **More behaviors.** Shopper (buys from vendors, sells loot), Crafter (lingers at forge/anvil), PK (murderer who hunts other bots), Tamer (collects pets), Mage (casts spells in combat).
- **Per-personality chat lines.** A Wealthy bot says different things than a Rough bot.
- **Bot story memory.** Click a bot to see their recent phases — "this bot was in Despise yesterday, traveled to Britain this morning."
- **Magic combat.** Adventurer bots with high Intelligence cast spells instead of swinging swords.

---

## File layout

```
~/uo-modernuo/
├── ModernUO/
│   └── Distribution/
│       ├── ModernUO.dll
│       ├── Configuration/
│       │   ├── modernuo.json
│       │   └── expansion.json
│       ├── Saves/                       ← world state
│       ├── Data/
│       │   ├── PlayerBotChat/           ← ambient chat lines
│       │   └── Waypoints/               ← bot navigation graph
│       └── Spawners/uoclassic/
│           └── UOClassic.map            ← Nerun's pre-T2A spawn data
├── ClassicUO/
│   ├── ClassicUO
│   └── settings.json
├── UOData/7.0.23.1/                     ← UO Classic game files
├── start.sh
├── stop.sh
├── reset-first-launch.sh
├── POPULATE-WORLD.txt
└── modernuo.log
```

---

## Troubleshooting

**Installer fails partway through.** Re-run the same command. It picks up where it left off; nothing is destroyed.

**Server starts but client can't connect.** Check `modernuo.log` in `~/uo-modernuo/` for errors. Look for `Listening: 127.0.0.1:2593`.

**Server quits immediately.** Usually a missing or wrong UO data path. Edit `~/uo-modernuo/ModernUO/Distribution/Configuration/modernuo.json` and fix the `dataDirectories` entry.

**ClassicUO crashes with FormatException.** Your UO data files are too new (7.0.59+). ClassicUO's animation loader only handles older formats. The installer downloads 7.0.23.1 automatically, but if you redirected it at a newer install, point it back at `~/uo-modernuo/UOData/7.0.23.1`.

**SteamOS update broke something.** Re-run the steps in section 2, then `./install.sh` again to reinstall any system packages that were reverted.

**"No starting cities are available" on character creation.** The `expansion.json` got overwritten with default values. Run `./reset-first-launch.sh` and `./start.sh` to redo first launch — the installer-written expansion.json will be intact.

**Bot install fails: "ModernUO not found".** You need to run `./install.sh` first to set up the base server. Then run `./install-playerbots.sh`.

**Bots aren't doing anything.** Check that the Lifecycle is enabled — `[LifecycleStatus` in-game should show "Lifecycle enabled: True". If it's off, enable it with `[SetLifecycle true`. Also check the server console (`tail -f ~/uo-modernuo/modernuo.log`) for transition messages.

**Travelers get stuck.** They're probably outside the waypoint graph's coverage. The starter graph only covers a small area around Britain. Either teleport them to Britain Bank, or expand the graph by walking the world and adding waypoints to `Data/Waypoints/waypoints.json`.

---

## Requirements

- Linux x86_64 (Debian, Ubuntu, Mint, Arch, SteamOS, Fedora, openSUSE).
- ~5 GB free disk space (~1 GB UO data, ~1 GB ModernUO build, ~200 MB .NET, ~500 MB workspace).
- An internet connection during install.

---

## Credits and licenses

- [ModernUO](https://github.com/modernuo/ModernUO) — GPL-3.0. The game server.
- [ClassicUO](https://github.com/ClassicUO/ClassicUO) — BSD. The game client.
- [Nerun's Distro](https://github.com/Nerun/runuo-nerun-distro) — the pre-T2A spawn map, decades of community work.
- [mirror.ashkantra.de](https://mirror.ashkantra.de/) — community mirror hosting the EA UO Classic installer.

PlayerBots system originally developed for this project. GPL-3.0.

Ultima Online is © Electronic Arts. This project does not redistribute any EA-copyrighted assets. The installer downloads them from a third-party community mirror that has hosted them publicly for years.
