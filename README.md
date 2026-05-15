# UO Offline

Offline, single-player Ultima Online for Linux and Steam Deck. T2A era, fully populated world, runs entirely on localhost.

Bundles [ModernUO](https://github.com/modernuo/ModernUO) (server) and [ClassicUO](https://github.com/ClassicUO/ClassicUO) (client) into a one-command install. Click an icon, play UO. That's the whole product.

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

This creates a `uo-modernuo` folder inside Downloads with `install.sh`, `uninstall.sh`, this README, and a `scripts` folder inside it.

### 4. Run the installer

```
cd ~/Downloads/uo-modernuo && chmod +x install.sh scripts/*.sh uninstall.sh && ./install.sh
```

The installer will:

- Install dependencies (prompts for your password).
- Download and build ModernUO.
- Download ClassicUO.
- Auto-detect your UO game files, or ask where they are.
- Write all config files.
- Add a **UO Offline** icon to your desktop.

Takes about 5-10 minutes the first time.

### 5. UO game files

You need the original UO `.mul` and `.uop` files. The installer searches common locations and prompts if it can't find them. See [UO game files](#uo-game-files) below.

### 6. Play

Double-click the **UO Offline** desktop icon. Log in with `admin` / `admin`.

---

## UO game files

ModernUO and ClassicUO are open-source — neither ships UO's copyrighted art, maps, or sounds. You provide the data folder.

Any of these will work:

- An existing UO Classic install (the EA-distributed client).
- A folder created by ClassicUO Launcher on another machine.
- The data folder from an old UO Classic CD install.

The folder should contain files like `art.mul`, `map0.mul`, `tiledata.mul`, or their `.uop` equivalents.

---

## Daily use

| Command | What it does |
| --- | --- |
| `./start.sh` | Launch server and client. Same as the desktop icon. |
| `./stop.sh` | Force-stop the server (only needed if it's stuck). |
| `./uninstall.sh` | Remove everything except your UO data folder. |

**Closing the client closes the server.** When you exit ClassicUO, the server saves the world and shuts down automatically. No need to open a terminal.

If you want the server to keep running after the client exits (e.g. you're going to relaunch the client right away, or connect from another machine on your LAN), start it like this instead:

```
KEEP_SERVER_RUNNING=1 ./start.sh
```

---

## In-game commands

You start as the `admin` owner account with full GM powers:

| Command | What it does |
| --- | --- |
| `[password newpassword` | Change your admin password. |
| `[where` | Show your coordinates. |
| `[go britain` | Teleport to Britain. |
| `[m` | Toggle GM invulnerability. |
| `[set invul true` | Explicit invulnerability on. |

Type `[help` in-game for the full list.

---

## What's included

- T2A expansion (Felucca + Lost Lands), id 1.
- ~43,000 spawned mobiles in the world by default — vendors, monsters, NPCs, animals.
- Localhost listener (`127.0.0.1:2593`). No network exposure.
- Auto-save every 5 minutes.
- `admin` / `admin` owner account on first launch (change the password).

## What's not included

- **Bots / AI companions.** The world is populated with vendors and monsters but no fake players.
- **Pre-placed housing.** Houses are placeable in-game; the world doesn't ship with sample houses.
- **Custom quests.** Only the era's original quest content is included.

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
│       └── Saves/
├── ClassicUO/
│   ├── ClassicUO
│   └── settings.json
├── start.sh
├── stop.sh
└── modernuo.log
```

---

## Troubleshooting

**Installer fails partway through.** Re-run the same command. It picks up where it left off; nothing is destroyed.

**Server starts but client can't connect.** Check `modernuo.log` in `~/uo-modernuo/` for errors. Confirm the listener line appears: `Listening: 127.0.0.1:2593`.

**Server quits immediately.** Usually a missing or wrong UO data path. Edit `~/uo-modernuo/ModernUO/Distribution/Configuration/modernuo.json` and fix the `dataDirectories` entry, or delete it and re-run `./install.sh`.

**SteamOS update broke something.** Re-run the steps in section 2 above, then `./install.sh` again to reinstall any system packages that were reverted.

---

## Requirements

- Linux x86_64 (Debian, Ubuntu, Mint, Arch, SteamOS, Fedora, openSUSE).
- ~3 GB free disk space.
- An internet connection during install (not needed afterward).
- A copy of UO's `.mul`/`.uop` data files.

---

## License

This installer is provided as-is. ModernUO and ClassicUO retain their own licenses (GPL-3.0 and BSD respectively). Ultima Online is © Electronic Arts. This project does not redistribute any EA-copyrighted assets.
