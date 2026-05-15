# UO Offline

> One-command installer for offline single-player Ultima Online on Linux and Steam Deck.

T2A era, runs entirely on localhost. Bundles [ModernUO](https://github.com/modernuo/ModernUO) (server) and [ClassicUO](https://github.com/ClassicUO/ClassicUO) (client). One command installs both, plus the UO Classic 7.0.23.1 game data and Nerun's pre-T2A spawn map. After install, click the desktop icon and play.

**Tested on:** Steam Deck (SteamOS 3.x, Desktop Mode). Should work on any modern x86_64 Linux distro with apt, pacman, or dnf.

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
```

```
sudo pacman-key --init
```

```
sudo pacman-key --populate
```

### 3. Get the installer

Download `uo-modernuo.zip` to your **Downloads** folder, then unzip it:

```
cd ~/Downloads
```

```
unzip uo-modernuo.zip
```

This creates a `uo-modernuo` folder inside Downloads with `install.sh`, `uninstall.sh`, this README, and a `scripts` folder.

### 4. Run the installer

```
cd ~/Downloads/uo-modernuo && chmod +x install.sh scripts/*.sh uninstall.sh && ./install.sh
```

The installer will:

- Install Linux dependencies (asks for your sudo password).
- Clone and build ModernUO (~3 minutes).
- Download and install .NET 10 (~200 MB).
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
```

```
[SignGen
```

```
[TelGen
```

```
[MoonGen
```

```
[TownCriers
```

```
[GenerateSpawners Spawners/uoclassic/UOClassic.map
```

Each one prints a progress message and takes a few seconds. The last one — `GenerateSpawners` — is the big one: it spawns ~1700 spawn points across Britannia (vendors, guards, monsters, animals) in under 3 seconds. `MoonGen` is what places the blue swirly portals in each city for fast travel.

You only do this once. The state persists with the world save.

A copy of these commands lives at `~/uo-modernuo/POPULATE-WORLD.txt` if you forget.

---

## Daily use

| Command | What it does |
| --- | --- |
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

You start as the `admin` owner account with full GM powers. Some useful ones:

| Command | What it does |
| --- | --- |
| `[where` | Show your coordinates. |
| `[go britain` | Teleport to Britain. |
| `[go destard` | Teleport to a dragon dungeon. |
| `[m` | Toggle GM movement (walk through walls). |
| `[invul` | Toggle invulnerability. |
| `[password new` | Change your admin password. |
| `[help` | Full command list. |

---

## What's included

- T2A expansion (Felucca map only).
- UO Classic 7.0.23.1 game data, auto-downloaded.
- Nerun's pre-T2A spawn map (~1700 spawners, era-authentic).
- Localhost listener (`127.0.0.1:2593`), no network exposure.
- Auto-save every 5 minutes.
- `admin` / `admin` owner account.

## What's not included

- **Bots / AI companions.** The world has vendors, monsters, and town NPCs, but no fake players.
- **Pre-placed housing.** Houses are placeable in-game; the world doesn't ship with sample houses.
- **Custom quests.** Only the era's original quest content.

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
│       └── Spawners/uoclassic/
│           └── UOClassic.map            ← Nerun's pre-T2A spawn data
├── ClassicUO/
│   ├── ClassicUO
│   └── settings.json
├── UOData/7.0.23.1/                     ← UO Classic game files
├── start.sh
├── stop.sh
├── reset-first-launch.sh
├── POPULATE-WORLD.txt                   ← in-game commands cheat sheet
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

Ultima Online is © Electronic Arts. This project does not redistribute any EA-copyrighted assets. The installer downloads them from a third-party community mirror that has hosted them publicly for years.
