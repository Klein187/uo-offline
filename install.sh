#!/usr/bin/env bash
# =========================================================================
# UO Offline (ModernUO edition) — Installer
#
# What this does:
#   1. Installs Linux prerequisites (Debian/Ubuntu/SteamOS/Fedora).
#   2. Clones ModernUO and bootstraps .NET 10 per-user.
#   3. Deploys the PlayerBots source files into the ModernUO source tree.
#   4. Builds ModernUO (including the bots) for Linux x64.
#   5. Downloads ClassicUO from GitHub releases.
#   6. Downloads UO Classic 7.0.23.1 game data from a community mirror
#      (or uses an existing install if one is already on disk).
#   7. Downloads Nerun's pre-T2A spawn map for world population.
#   8. Writes correct ModernUO and ClassicUO configs (T2A, localhost-only).
#   9. Installs start/stop scripts and a desktop launcher.
#
# After install, run start.sh (or click the UO Offline desktop icon).
# First launch creates the owner account and populates the world.
# Subsequent launches just start the server and open the client.
#
# Server listens on 127.0.0.1:2593 only. Nothing exposed to the network.
#
# Notes:
#   - UO Classic game files are © Electronic Arts. The installer downloads
#     them from mirror.ashkantra.de — a long-running community mirror.
#     If you already have a 7.0.59 or earlier UO Classic install, the
#     installer will auto-detect and use it instead.
#   - ClassicUO and ModernUO are open source (BSD and GPL-3.0). They
#     don't ship game assets.
# =========================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---------------------------------------------------------------------------
# Paths and URLs
# ---------------------------------------------------------------------------
INSTALL_ROOT="${HOME}/uo-modernuo"
MODERNUO_REPO="https://github.com/modernuo/ModernUO.git"
MODERNUO_DIR="${INSTALL_ROOT}/ModernUO"
DIST_DIR="${MODERNUO_DIR}/Distribution"
CFG_DIR="${DIST_DIR}/Configuration"
SPAWNERS_DIR="${DIST_DIR}/Spawners/uoclassic"

CLASSICUO_DIR="${INSTALL_ROOT}/ClassicUO"
CLASSICUO_RELEASE_URL="https://api.github.com/repos/ClassicUO/ClassicUO/releases"

# UO Classic 7.0.23.1 from the ashkantra mirror. Old enough that ClassicUO's
# animation loader handles it without crashing on UOP formats, new enough to
# have all the T2A-era art needed.
UO_DATA_URL="https://mirror.ashkantra.de/fullclients/7.0.23.1.exe"
UO_DATA_VERSION="7.0.23.1"
UO_DATA_DIR="${INSTALL_ROOT}/UOData/${UO_DATA_VERSION}"

# Nerun's pre-T2A spawn data. ModernUO's [GenerateSpawners command parses
# the .map format directly.
SPAWN_MAP_URL="https://raw.githubusercontent.com/Nerun/runuo-nerun-distro/master/Distro/Data/Nerun's%20Distro/Spawns/uoclassic/UOClassic.map"

# ---------------------------------------------------------------------------
# Config defaults
# ---------------------------------------------------------------------------
EXPANSION_ID=1
EXPANSION_NAME="T2A"
OWNER_USER="admin"
OWNER_PASS="admin"
LISTEN_ADDR="127.0.0.1:2593"
SHARD_NAME="UO Offline"

# Per-user .NET install location. Avoids needing root and survives SteamOS
# read-only filesystem reverts.
DOTNET_ROOT="${HOME}/.dotnet"

# ---------------------------------------------------------------------------
# Pretty output
# ---------------------------------------------------------------------------
banner() { printf '\n\033[1;36m=== %s ===\033[0m\n' "$*"; }
say()    { printf '\033[0;36m--> %s\033[0m\n' "$*"; }
ok()     { printf '\033[0;32m[OK]\033[0m %s\n' "$*"; }
warn()   { printf '\033[0;33m[WARN]\033[0m %s\n' "$*" >&2; }
die()    { printf '\033[0;31m[ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Step 1 — Pre-flight checks
# ---------------------------------------------------------------------------
preflight() {
  banner "Pre-flight checks"

  [[ "$(uname -s)" == "Linux" ]] || die "Linux-only installer."
  [[ "${EUID}" -ne 0 ]]         || die "Run as your normal user, not root. sudo will be invoked when needed."

  command -v git    >/dev/null || die "git is required."
  command -v curl   >/dev/null || die "curl is required."
  command -v sudo   >/dev/null || warn "sudo not found — dependency install will fail if deps are missing."

  mkdir -p "${INSTALL_ROOT}"
  ok "Install root: ${INSTALL_ROOT}"
}

# ---------------------------------------------------------------------------
# Step 2 — Native dependencies
# ---------------------------------------------------------------------------
install_deps() {
  banner "Installing native dependencies"

  if command -v apt-get >/dev/null; then
    say "Debian-family distro detected. Using apt."
    sudo apt-get update -y
    sudo apt-get install -y \
      libicu-dev libdeflate-dev zstd libargon2-dev liburing-dev \
      libgdiplus p7zip-full unzip build-essential
  elif command -v pacman >/dev/null; then
    say "Arch-family distro detected. Using pacman."
    if [[ -f /etc/os-release ]] && grep -qi steamos /etc/os-release; then
      warn "SteamOS detected. If you haven't already, run:"
      warn "    sudo steamos-readonly disable"
      warn "    sudo pacman-key --init && sudo pacman-key --populate"
      warn "Press Ctrl-C now to abort, or any key to continue."
      read -r -n 1 -s
    fi
    sudo pacman -S --needed --noconfirm \
      icu libdeflate zstd argon2 liburing \
      libgdiplus p7zip unzip base-devel
  elif command -v dnf >/dev/null; then
    say "Fedora-family distro detected. Using dnf."
    sudo dnf install -y libicu libdeflate-devel zstd libargon2-devel \
      liburing-devel libgdiplus p7zip unzip @development-tools
  else
    die "Unsupported package manager. Install manually: libicu, libdeflate, zstd, libargon2, liburing, p7zip, unzip."
  fi

  ok "Dependencies installed."
}

# ---------------------------------------------------------------------------
# Step 3 — Clone ModernUO (full history, required by Nerdbank.GitVersioning)
# ---------------------------------------------------------------------------
fetch_modernuo() {
  banner "Fetching ModernUO source"

  if [[ -d "${MODERNUO_DIR}/.git" ]]; then
    say "ModernUO already cloned."
    cd "${MODERNUO_DIR}"

    if [[ -f .git/shallow ]]; then
      say "Unshallowing existing clone..."
      git fetch --unshallow || git fetch --depth=2147483647
    fi

    git fetch --all --tags
    git checkout main
    git pull --ff-only
  else
    say "Cloning ModernUO (full history)..."
    git clone "${MODERNUO_REPO}" "${MODERNUO_DIR}"
  fi

  ok "ModernUO source at ${MODERNUO_DIR}"
}

# ---------------------------------------------------------------------------
# Step 4 — Bootstrap .NET SDK per-user
# ---------------------------------------------------------------------------
bootstrap_dotnet() {
  banner "Bootstrapping .NET SDK"

  local channel="LTS"
  local gj="${MODERNUO_DIR}/global.json"
  if [[ -f "${gj}" ]]; then
    local sdk_ver
    sdk_ver="$(grep -oE '"version"[[:space:]]*:[[:space:]]*"[^"]+"' "${gj}" \
      | head -n1 | sed -E 's/.*"([^"]+)".*/\1/' || true)"
    if [[ -n "${sdk_ver}" ]]; then
      channel="$(echo "${sdk_ver}" | awk -F. '{print $1"."$2}')"
      say "ModernUO wants SDK ${sdk_ver}; using channel ${channel}."
    fi
  fi

  if [[ -x "${DOTNET_ROOT}/dotnet" ]] \
     && "${DOTNET_ROOT}/dotnet" --list-sdks 2>/dev/null | grep -qE "^${channel}\."; then
    ok "Found compatible SDK at ${DOTNET_ROOT}"
    export PATH="${DOTNET_ROOT}:${PATH}"
    export DOTNET_ROOT
    return
  fi

  say "Downloading dotnet-install.sh..."
  local tmp="${INSTALL_ROOT}/.dotnet-install.sh"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${tmp}"
  chmod +x "${tmp}"

  say "Installing .NET SDK ${channel} into ${DOTNET_ROOT}..."
  "${tmp}" --channel "${channel}" --install-dir "${DOTNET_ROOT}"
  rm -f "${tmp}"

  export PATH="${DOTNET_ROOT}:${PATH}"
  export DOTNET_ROOT

  [[ -x "${DOTNET_ROOT}/dotnet" ]] || die "dotnet not installed at ${DOTNET_ROOT}/dotnet."
  ok "Installed: $(${DOTNET_ROOT}/dotnet --version)"
}

# ---------------------------------------------------------------------------
# Step 5 — Build ModernUO
# ---------------------------------------------------------------------------
build_modernuo() {
  banner "Building ModernUO"

  export PATH="${DOTNET_ROOT}:${PATH}"
  export DOTNET_ROOT

  if [[ -f "${DIST_DIR}/ModernUO.dll" ]]; then
    say "ModernUO already built. Skipping (delete ${DIST_DIR}/ModernUO.dll to force rebuild)."
    return
  fi

  cd "${MODERNUO_DIR}"
  chmod +x ./publish.sh
  ./publish.sh release linux x64

  [[ -f "${DIST_DIR}/ModernUO.dll" ]] || die "Build produced no ModernUO.dll. Check output above."
  ok "Build artifacts at ${DIST_DIR}"
}

# ---------------------------------------------------------------------------
# Step 6 — UO game data: detect existing, or auto-download
# ---------------------------------------------------------------------------
find_or_download_uo_data() {
  banner "Locating UO game data"

  # Common locations for an existing install. Modern client versions (post
  # 7.0.59) crash ClassicUO's animation loader, so we only accept older.
  local candidates=(
    "${HOME}/.steam/steam/steamapps/compatdata/*/pfx/drive_c/Program Files (x86)/Electronic Arts/Ultima Online Classic"
    "${HOME}/Games/Ultima Online Classic"
    "${HOME}/Ultima Online Classic"
    "${HOME}/Desktop/Electronic Arts/Ultima Online Classic"
    "${HOME}/Desktop/Ultima Online Classic"
    "${HOME}/Documents/Ultima Online Classic"
    "${HOME}/.wine/drive_c/Program Files/EA Games/Ultima Online Classic"
    "${HOME}/.wine/drive_c/Program Files (x86)/Electronic Arts/Ultima Online Classic"
    "${INSTALL_ROOT}/UOData/${UO_DATA_VERSION}"
    "/mnt/uo"
  )

  for pattern in "${candidates[@]}"; do
    for c in ${pattern}; do
      [[ -d "${c}" ]] || continue
      # Only accept folders that contain the required .mul files.
      if [[ -f "${c}/art.mul" ]] && [[ -f "${c}/map0.mul" ]]; then
        UO_DATA="${c}"
        ok "Found UO data: ${UO_DATA}"
        return
      fi
    done
  done

  # Nothing found. Auto-download from the community mirror.
  warn "No existing UO data found. Downloading UO Classic ${UO_DATA_VERSION}."
  warn "Source: ${UO_DATA_URL} (~929 MB, third-party mirror, EA-copyrighted content)."
  echo ""

  command -v 7z >/dev/null || die "7z not found. Install p7zip first (it should have been installed by the dependency step)."

  mkdir -p "${INSTALL_ROOT}/UOData"
  local exe_path="${INSTALL_ROOT}/UOData/${UO_DATA_VERSION}.exe"

  if [[ ! -f "${exe_path}" ]]; then
    say "Downloading (this can take 5-15 minutes)..."
    # The mirror 403's on default wget User-Agent. curl with a real one is fine.
    curl -fL --progress-bar \
      -A "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36" \
      -o "${exe_path}" \
      "${UO_DATA_URL}"
  else
    say "Installer already at ${exe_path}, skipping download."
  fi

  say "Extracting with 7z..."
  mkdir -p "${UO_DATA_DIR}"
  # The installer extracts to a nested folder; -y auto-yes, -o sets output.
  # Discard 7z's per-file output; we want a clean log.
  7z x -y "-o${INSTALL_ROOT}/UOData" "${exe_path}" >/dev/null

  # The 7z extract creates ${INSTALL_ROOT}/UOData/${UO_DATA_VERSION}/ with
  # the .mul files. Verify.
  if [[ ! -f "${UO_DATA_DIR}/art.mul" ]] || [[ ! -f "${UO_DATA_DIR}/map0.mul" ]]; then
    # Maybe the extract put files at a different path. Search.
    local found
    found="$(find "${INSTALL_ROOT}/UOData" -maxdepth 3 -name "art.mul" -print -quit 2>/dev/null)"
    if [[ -n "${found}" ]]; then
      UO_DATA_DIR="$(dirname "${found}")"
    else
      die "Extraction succeeded but no art.mul found under ${INSTALL_ROOT}/UOData."
    fi
  fi

  UO_DATA="${UO_DATA_DIR}"
  ok "UO data extracted to: ${UO_DATA}"

  # Keep or delete the installer .exe? Deleting saves 1GB.
  say "Removing installer .exe to save ~929 MB..."
  rm -f "${exe_path}"
}

# ---------------------------------------------------------------------------
# Step 7 — Download Nerun's pre-T2A spawn map
# ---------------------------------------------------------------------------
fetch_spawn_map() {
  banner "Fetching Nerun's pre-T2A spawn map"

  mkdir -p "${SPAWNERS_DIR}"
  local target="${SPAWNERS_DIR}/UOClassic.map"

  if [[ -f "${target}" ]] && [[ -s "${target}" ]]; then
    say "Spawn map already present: ${target}"
    return
  fi

  say "Downloading from Nerun's repository..."
  curl -fL --progress-bar -o "${target}" "${SPAWN_MAP_URL}"

  # Sanity check: ensure we got the .map file, not a GitHub error page.
  if head -1 "${target}" | grep -qi '<!doctype\|<html'; then
    rm -f "${target}"
    die "Downloaded file looks like HTML, not a spawn map. Check ${SPAWN_MAP_URL}"
  fi

  ok "Spawn map: ${target} ($(wc -l < "${target}") lines)"
}

# ---------------------------------------------------------------------------
# Step 8 — Download ClassicUO
# ---------------------------------------------------------------------------
install_classicuo() {
  banner "Downloading ClassicUO client"

  if [[ -d "${CLASSICUO_DIR}" ]] \
     && [[ -n "$(ls -A "${CLASSICUO_DIR}" 2>/dev/null)" ]] \
     && [[ -f "${INSTALL_ROOT}/.classicuo-bin-path" ]]; then
    say "ClassicUO already installed. Skipping."
    return
  fi

  command -v unzip >/dev/null || die "unzip is required."
  mkdir -p "${CLASSICUO_DIR}"

  local tmp_zip="${INSTALL_ROOT}/.classicuo.zip"
  say "Querying GitHub for the latest Linux release..."

  local asset_url=""
  asset_url="$(curl -fsSL "${CLASSICUO_RELEASE_URL}/latest" 2>/dev/null \
    | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | grep -iE 'linux' | head -n1 \
    | sed -E 's/.*"(https[^"]+)".*/\1/' || true)"

  if [[ -z "${asset_url}" ]]; then
    say "No Linux asset on /latest. Checking dev-release tag..."
    asset_url="$(curl -fsSL "${CLASSICUO_RELEASE_URL}/tags/ClassicUO-dev-release" 2>/dev/null \
      | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
      | grep -iE 'linux' | head -n1 \
      | sed -E 's/.*"(https[^"]+)".*/\1/' || true)"
  fi

  [[ -n "${asset_url}" ]] || die "Could not find a ClassicUO Linux release on GitHub."

  say "Downloading: ${asset_url}"
  curl -fL --progress-bar -o "${tmp_zip}" "${asset_url}"

  say "Extracting..."
  unzip -q -o "${tmp_zip}" -d "${CLASSICUO_DIR}"
  rm -f "${tmp_zip}"

  local cuo_bin=""
  for name in ClassicUO ClassicUO.bin.x86_64 cuo; do
    [[ -f "${CLASSICUO_DIR}/${name}" ]] && { cuo_bin="${CLASSICUO_DIR}/${name}"; break; }
  done
  [[ -n "${cuo_bin}" ]] || cuo_bin="$(find "${CLASSICUO_DIR}" -maxdepth 2 -type f \
    \( -name 'ClassicUO' -o -name 'ClassicUO.bin.x86_64' -o -name 'cuo' \) \
    -print -quit 2>/dev/null || true)"

  if [[ -n "${cuo_bin}" ]]; then
    chmod +x "${cuo_bin}"
    echo "${cuo_bin}" > "${INSTALL_ROOT}/.classicuo-bin-path"
    ok "ClassicUO binary: ${cuo_bin}"
  else
    warn "ClassicUO extracted but binary not located. start.sh will try at launch."
  fi
}

# ---------------------------------------------------------------------------
# Step 9 — Write configs (using the correct schemas we learned the hard way)
# ---------------------------------------------------------------------------
write_modernuo_config() {
  banner "Writing ModernUO configuration"

  mkdir -p "${CFG_DIR}"

  # modernuo.json — server runtime config.
  cat > "${CFG_DIR}/modernuo.json" <<EOF
{
  "assemblyDirectories": ["./Assemblies"],
  "dataDirectories": ["${UO_DATA}"],
  "listeners": ["${LISTEN_ADDR}"],
  "settings": {
    "accountHandler.maxAccountsPerIP": "10",
    "autosave.enabled": "true",
    "autosave.saveDelay": "00:05:00",
    "serverList.address": "127.0.0.1",
    "serverList.autoDetect": "false",
    "serverListing.name": "${SHARD_NAME}"
  }
}
EOF
  ok "Wrote modernuo.json"

  # expansion.json — the REAL schema, capitalized keys, all flags spelled out.
  # T2A gets Felucca map only, ExpansionT2A flag on, LiveAccount on.
  cat > "${CFG_DIR}/expansion.json" <<EOF
{
  "Id": ${EXPANSION_ID},
  "ClientFlags": "None",
  "SupportedFeatures": {
    "ExpansionT2A": true,
    "T2A": true,
    "UOR": false,
    "UOTD": false,
    "LBR": false,
    "AOS": false,
    "SixthCharacterSlot": false,
    "SE": false,
    "ML": false,
    "EighthAge": false,
    "NinthAge": false,
    "TenthAge": false,
    "IncreasedStorage": false,
    "SeventhCharacterSlot": false,
    "RoleplayFaces": false,
    "TrialAccount": false,
    "LiveAccount": true,
    "SA": false,
    "HS": false,
    "Gothic": false,
    "Rustic": false,
    "Jungle": false,
    "Shadowguard": false,
    "TOL": false,
    "EJ": false
  },
  "CharacterListFlags": {
    "Unk1": false,
    "OverwriteConfigButton": false,
    "OneCharacterSlot": false,
    "ExpansionNone": false,
    "ExpansionUOTD": false,
    "ExpansionLBR": false,
    "ExpansionT2A": true,
    "ExpansionUOR": false,
    "ContextMenus": false,
    "SlotLimit": false,
    "AOS": false,
    "SixthCharacterSlot": false,
    "SE": false,
    "ML": false,
    "KR": false,
    "UO3DClientType": false,
    "Unk3": false,
    "SeventhCharacterSlot": false,
    "Unk4": false,
    "NewMovementSystem": false,
    "NewFeluccaAreas": false
  },
  "HousingFlags": {
    "AOS": false,
    "HousingAOS": false,
    "SE": false,
    "ML": false,
    "Crystal": false,
    "SA": false,
    "HS": false,
    "Gothic": false,
    "Rustic": false,
    "Jungle": false,
    "Shadowguard": false,
    "TOL": false,
    "EJ": false
  },
  "MobileStatusVersion": 0,
  "MapSelectionFlags": {
    "Felucca": true,
    "Trammel": false,
    "Ilshenar": false,
    "Malas": false,
    "Tokuno": false,
    "TerMur": false
  }
}
EOF
  ok "Wrote expansion.json (T2A, Felucca-only)"
}

# ---------------------------------------------------------------------------
# Step 10 — Write ClassicUO settings.json
# ---------------------------------------------------------------------------
write_classicuo_settings() {
  banner "Writing ClassicUO settings.json"

  [[ -d "${CLASSICUO_DIR}" ]] || { warn "ClassicUO dir missing; skipping."; return; }

  local cfg_targets=("${CLASSICUO_DIR}")
  local nested
  nested="$(dirname "$(cat "${INSTALL_ROOT}/.classicuo-bin-path" 2>/dev/null || echo "${CLASSICUO_DIR}/ClassicUO")")"
  if [[ "${nested}" != "${CLASSICUO_DIR}" ]] && [[ -d "${nested}" ]]; then
    cfg_targets+=("${nested}")
  fi

  for target in "${cfg_targets[@]}"; do
    cat > "${target}/settings.json" <<EOF
{
  "username": "${OWNER_USER}",
  "password": "",
  "ip": "127.0.0.1",
  "port": 2593,
  "ultimaonlinedirectory": "${UO_DATA}",
  "clientversion": "${UO_DATA_VERSION}",
  "lastservernum": 1,
  "last_server_name": "${SHARD_NAME}",
  "fps": 60,
  "debug": false,
  "encryption": 0,
  "save_password": false,
  "auto_login": false,
  "plugins": [],
  "music_volume": 30,
  "sound_volume": 70,
  "footsteps_sound": true,
  "combat_music": true,
  "music": true,
  "sound": true,
  "shard_type": 0
}
EOF
    ok "Wrote ${target}/settings.json"
  done
}

# ---------------------------------------------------------------------------
# Step 11 — Install runtime scripts
# ---------------------------------------------------------------------------
install_runtime_scripts() {
  banner "Installing launcher scripts"

  local src_dir="${SCRIPT_DIR}/scripts"
  [[ -d "${src_dir}" ]] || die "Cannot find scripts directory at ${src_dir}"

  cp "${src_dir}/start.sh"              "${INSTALL_ROOT}/start.sh"
  cp "${src_dir}/stop.sh"               "${INSTALL_ROOT}/stop.sh"
  cp "${src_dir}/reset-first-launch.sh" "${INSTALL_ROOT}/reset-first-launch.sh"

  chmod +x "${INSTALL_ROOT}/start.sh" \
           "${INSTALL_ROOT}/stop.sh" \
           "${INSTALL_ROOT}/reset-first-launch.sh"

  ok "Installed start.sh, stop.sh, reset-first-launch.sh"
}

# ---------------------------------------------------------------------------
# Step 12 — Mark for first-launch wizard
# ---------------------------------------------------------------------------
arm_first_launch() {
  touch "${INSTALL_ROOT}/.needs-owner-account"
  ok "Owner account will be created on first launch: ${OWNER_USER} / ${OWNER_PASS}"
}

# ---------------------------------------------------------------------------
# Step 12b — Drop a world-population cheat sheet next to start.sh
# ---------------------------------------------------------------------------
install_cheatsheet() {
  cat > "${INSTALL_ROOT}/POPULATE-WORLD.txt" <<'EOF'
After your first character is created and you're standing in Britannia,
the world will be empty — no NPCs, no signs, no monsters. To populate it,
open the in-game chat and type these six commands, one at a time.

Each command takes a few seconds and prints a progress message in chat.

  [Decorate
       Places fences, lamp posts, walls, plants, ~55,000 decoration items.

  [SignGen
       Hangs shop signs on all the buildings.

  [TelGen
       Places teleporters between cities and dungeons.

  [MoonGen
       Places the public moongate network (the blue swirly portals).
       One in each major city. Double-click to fast travel.

  [TownCriers
       Spawns town crier NPCs (the ones that read announcements).

  [GenerateSpawners Spawners/uoclassic/UOClassic.map
       The big one. Spawns ~1700 spawn points across Britannia: orcs in
       the orc fort, deer in forests, dragons in dungeons, vendors in
       every town. Takes about 3 seconds. This is the moment the world
       comes alive.

You only do this once. The state saves with the world and persists
forever. If you ever want to start fresh, run reset-first-launch.sh and
the world goes back to empty — then redo these commands.

Tip: type [help in-game for the full command list. Useful admin commands:

  [where           Show your X/Y/Z coordinates.
  [go britain      Teleport to Britain's center.
  [go destard      Teleport to a dragon dungeon.
  [m               Toggle GM movement (walk through walls).
  [invul           Toggle invulnerability.
  [password new    Change your admin password.
EOF
  ok "World-population cheat sheet: ${INSTALL_ROOT}/POPULATE-WORLD.txt"
}

# ---------------------------------------------------------------------------
# Step 13 — Desktop launcher
# ---------------------------------------------------------------------------
install_desktop_entry() {
  banner "Installing desktop launcher"

  local apps_dir="${HOME}/.local/share/applications"
  mkdir -p "${apps_dir}" "${HOME}/Desktop"

  local desktop_file="${apps_dir}/UO-Offline.desktop"
  cat > "${desktop_file}" <<EOF
[Desktop Entry]
Type=Application
Name=UO Offline
GenericName=Ultima Online (offline)
Comment=Offline Ultima Online — T2A era
Exec=${INSTALL_ROOT}/start.sh
Icon=applications-games
Terminal=false
Categories=Game;RolePlaying;
StartupNotify=false
EOF
  chmod +x "${desktop_file}"
  cp "${desktop_file}" "${HOME}/Desktop/UO-Offline.desktop" 2>/dev/null || true
  chmod +x "${HOME}/Desktop/UO-Offline.desktop" 2>/dev/null || true

  ok "Desktop launcher installed."
}

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
finish() {
  banner "Install complete"
  cat <<EOF

Install root:   ${INSTALL_ROOT}
Server:         ${DIST_DIR}
Client:         ${CLASSICUO_DIR}
UO data:        ${UO_DATA}
Expansion:      ${EXPANSION_NAME} (id=${EXPANSION_ID})
Listener:       ${LISTEN_ADDR}  (localhost only, offline)
Owner login:    ${OWNER_USER} / ${OWNER_PASS}

To play:        Click the "UO Offline" desktop icon.
                (or run ${INSTALL_ROOT}/start.sh from a terminal)

First launch flow:
  1. Server starts, owner account is created automatically (~10s).
  2. ClassicUO opens. Log in: ${OWNER_USER} / ${OWNER_PASS}.
  3. Create a character, pick a starting city, enter the world.
  4. The world is empty at first. To populate it, read:
       ${INSTALL_ROOT}/POPULATE-WORLD.txt
     and run the five [-commands shown there in chat.
  5. Done. World state saves automatically every 5 minutes.

EOF
}

# ---------------------------------------------------------------------------
# Step 4b — PlayerBots: deploy bot source files into the ModernUO source tree
#
# This runs BEFORE build_modernuo so the bot code is compiled into the same
# build pass. The bot files live in this repo at ./playerbots/.
# ---------------------------------------------------------------------------
install_playerbots() {
  banner "Installing PlayerBots"

  local src_dir="${SCRIPT_DIR}/playerbots"
  if [[ ! -d "${src_dir}" ]]; then
    warn "No playerbots/ directory next to install.sh; skipping bot install."
    return
  fi

  local src_target="${MODERNUO_DIR}/Projects/UOContent/CustomBots"
  local chat_target="${DIST_DIR}/Data/PlayerBotChat"
  local waypoints_target="${DIST_DIR}/Data/Waypoints"

  # Hash the source we're about to deploy so we know whether to force a
  # rebuild. If the hash matches what's already deployed, skip the touch
  # of ModernUO.dll so build_modernuo can skip cleanly.
  local new_hash
  new_hash="$(find "${src_dir}/source" "${src_dir}/data" -type f -exec sha256sum {} + 2>/dev/null \
    | sort | sha256sum | cut -d' ' -f1)"
  local hash_file="${src_target}/.deployed-hash"
  local prev_hash=""
  [[ -f "${hash_file}" ]] && prev_hash="$(cat "${hash_file}")"

  if [[ -d "${src_target}" && "${new_hash}" == "${prev_hash}" ]]; then
    say "PlayerBot sources unchanged. Skipping deploy."
    return
  fi

  say "Deploying bot source -> ${src_target}"
  mkdir -p "${src_target}"
  cp -rT "${src_dir}/source/CustomBots" "${src_target}"
  echo "${new_hash}" > "${hash_file}"

  if [[ -d "${src_dir}/data/PlayerBotChat" ]]; then
    say "Deploying chat data -> ${chat_target}"
    mkdir -p "${chat_target}"
    cp -rT "${src_dir}/data/PlayerBotChat" "${chat_target}"
  fi

  if [[ -d "${src_dir}/data/Waypoints" ]]; then
    say "Deploying waypoint graph -> ${waypoints_target}"
    mkdir -p "${waypoints_target}"
    cp -rT "${src_dir}/data/Waypoints" "${waypoints_target}"
  fi

  # Clean up any legacy files from older bot system versions
  local legacy_files=(
    "${src_target}/Behaviors/RouteRegistry.cs"
    "${src_target}/Behaviors/ReloadRoutesCommand.cs"
    "${src_target}/Behaviors/DestinationRegistry.cs"
    "${src_target}/Behaviors/ReloadDestinationsCommand.cs"
  )
  for f in "${legacy_files[@]}"; do
    [[ -f "$f" ]] && rm -f "$f"
  done

  local legacy_dirs=(
    "${DIST_DIR}/Data/Routes"
    "${DIST_DIR}/Data/Destinations"
  )
  for d in "${legacy_dirs[@]}"; do
    [[ -d "$d" ]] && rm -rf "$d"
  done

  # Force a rebuild on next build_modernuo by removing the marker file.
  if [[ -f "${DIST_DIR}/ModernUO.dll" ]]; then
    say "Bot sources changed — clearing build cache to trigger rebuild"
    rm -f "${DIST_DIR}/ModernUO.dll"
  fi

  ok "PlayerBots deployed (will be compiled by the next ModernUO build)"
}

# ---------------------------------------------------------------------------
main() {
  preflight
  install_deps
  fetch_modernuo
  bootstrap_dotnet
  install_playerbots
  build_modernuo
  find_or_download_uo_data
  fetch_spawn_map
  install_classicuo
  write_modernuo_config
  write_classicuo_settings
  install_runtime_scripts
  arm_first_launch
  install_cheatsheet
  install_desktop_entry
  finish
}

main "$@"
