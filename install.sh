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
#   6b. Swaps in genuine T2A-era Felucca map art (intact Magincia) from the
#      UO Second Age distribution. Reversible; INSTALL_T2A_MAP=0 to skip.
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
# Where everything goes. Override it either way:
#   ./install.sh --install-root /mnt/games/uo-offline
#   INSTALL_ROOT=/mnt/games/uo-offline ./install.sh
# Everything below hangs off this, so it has to be settled before they are.
for _arg_i in $(seq 1 $#); do
  if [[ "${!_arg_i}" == "--install-root" ]]; then
    _next=$((_arg_i + 1))
    INSTALL_ROOT="${!_next:-}"
  elif [[ "${!_arg_i}" == --install-root=* ]]; then
    INSTALL_ROOT="${!_arg_i#*=}"
  elif [[ "${!_arg_i}" == "--no-map-editor" ]]; then
    INSTALL_MAP_EDITOR=0
  fi
done

# The map editor is a builder's tool - waypoints, spawns, a live view of the
# bots - not something you need in order to play. On by default, off with
# --no-map-editor or INSTALL_MAP_EDITOR=0.
INSTALL_MAP_EDITOR="${INSTALL_MAP_EDITOR:-1}"
unset _arg_i _next

INSTALL_ROOT="${INSTALL_ROOT:-${HOME}/uo-modernuo}"
INSTALL_ROOT="${INSTALL_ROOT%/}"

if [[ "${INSTALL_ROOT}" != /* ]]; then
  INSTALL_ROOT="$(pwd)/${INSTALL_ROOT}"
fi
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

# Genuine T2A-era Felucca map art (intact Magincia, pre-destruction world),
# pulled from the official UO Second Age (client 5.0.8.3) distribution. The
# 7.0.23.1 data above ships modern map art with 15+ years of EA world edits;
# swapping these three files restores the T2A look. Set INSTALL_T2A_MAP=0 to
# keep modern map art. See docs/T2A-MAP.md.
INSTALL_T2A_MAP=1
T2A_INSTALLER_URL="https://download.uosecondage.com/UOSA_Client_Setup.exe"
T2A_SRC_DIR="${INSTALL_ROOT}/t2a-src"

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

  command -v curl   >/dev/null || die "curl is required."
  command -v sudo   >/dev/null || warn "sudo not found — dependency install will fail if deps are missing."

  # The install root can be anywhere, so check it here rather than failing
  # several steps later with a confusing message.
  mkdir -p "${INSTALL_ROOT}" 2>/dev/null \
    || die "Cannot create ${INSTALL_ROOT}. Pick a folder you can write to."
  [[ -w "${INSTALL_ROOT}" ]] \
    || die "${INSTALL_ROOT} is not writable. Pick a folder you own."

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
      libgdiplus p7zip-full unzip build-essential git
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
      libgdiplus p7zip unzip base-devel git
  elif command -v dnf >/dev/null; then
    say "Fedora-family distro detected. Using dnf."
    sudo dnf install -y libicu libdeflate-devel zstd libargon2-devel \
      liburing-devel libgdiplus p7zip unzip @development-tools git
  else
    die "Unsupported package manager. Install manually: git, libicu, libdeflate, zstd, libargon2, liburing, p7zip, unzip."
  fi

  command -v git >/dev/null || die "git is still missing after the dependency step. Install git and re-run."

  ok "Dependencies installed."
}

# ---------------------------------------------------------------------------
# Step 3 — Clone ModernUO (full history, required by Nerdbank.GitVersioning)
# ---------------------------------------------------------------------------
fetch_modernuo() {
  banner "Fetching ModernUO source"

  if [[ -d "${MODERNUO_DIR}/.git" ]]; then
    say "ModernUO already cloned."
    # Engine patches are needed only while compiling. Older installer runs
    # left them as tracked worktree changes, which can block checkout/pull.
    # Remove them only when HEAD/index is still unpatched and the worktree can
    # reverse the patch cleanly; unrelated user edits are left in place.
    remove_engine_patches
    cd "${MODERNUO_DIR}"

    if [[ -f .git/shallow ]]; then
      say "Unshallowing existing clone..."
      git fetch --unshallow || git fetch --depth=2147483647
    fi

    # --force because upstream moves tags (build-tool-latest is re-pointed
    # every release); without it the fetch fails with "would clobber existing
    # tag". None of this is fatal - a clone that will not update still builds.
    # Installer-owned engine patches were removed above, but an unrelated user
    # edit to a tracked file can still make checkout or pull refuse safely.
    git fetch --all --tags --force || warn "git fetch failed; using the checkout on disk."
    git checkout main               || warn "git checkout main failed; using the current branch."
    git pull --ff-only              || warn "git pull failed; using the checkout on disk." 
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
# Delete every Projects/*/obj and /bin. Pure build output - restore and the
# next build regenerate all of it.
clear_build_artifacts() {
  local projects_dir="${MODERNUO_DIR}/Projects"
  [[ -d "${projects_dir}" ]] || return 0

  local removed=0
  local d
  for d in "${projects_dir}"/*/obj "${projects_dir}"/*/bin; do
    if [[ -d "${d}" ]]; then
      rm -rf "${d}"
      removed=$((removed + 1))
    fi
  done
  say "Cleared ${removed} stale build output folder(s)."
}

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
  ./publish.sh release linux x64 || true

  if [[ ! -f "${DIST_DIR}/ModernUO.dll" ]]; then
    # A build can fail on stale intermediate output left behind by a
    # DIFFERENT .NET SDK - if a distro package or another install is on PATH,
    # whichever ran last wins. The giveaway is the build tool reporting
    # "'Cleaning project' failed with exit code 1", with a
    # ResolvePackageAssets NullReferenceException buried in the output.
    # Clearing obj/ and bin/ makes restore regenerate them, so try once
    # before giving up.
    warn "Build produced no ModernUO.dll. Clearing stale build output and retrying once..."
    clear_build_artifacts
    ./publish.sh release linux x64 || true
  fi

  [[ -f "${DIST_DIR}/ModernUO.dll" ]] || die "Build produced no ModernUO.dll. Check output above."
  ok "Build artifacts at ${DIST_DIR}"
}

# ---------------------------------------------------------------------------
# Step 5b — Fix Felucca season
# ---------------------------------------------------------------------------
# ModernUO ships with Felucca's season set to 4 (Desolation) — a Renaissance-era
# lore choice that makes all trees on Felucca render leafless. For an offline
# single-player experience we want a leafy world year-round. Change to 1 (Summer).
#
# Idempotent: re-running on an already-fixed file is a no-op.
# ---------------------------------------------------------------------------
fix_felucca_season() {
 banner "Setting Felucca season to Summer"

 local mapdef="${MODERNUO_DIR}/Distribution/Data/map-definitions.json"

 if [[ ! -f "${mapdef}" ]]; then
   warn "map-definitions.json not found at ${mapdef}. Skipping season fix."
   return
 fi

 if grep -A 5 '"name": "Felucca"' "${mapdef}" | grep -q '"season": 1'; then
   say "Felucca already set to Summer. Skipping."
   return
 fi

 cp "${mapdef}" "${mapdef}.original"
 sed -i '/"name": "Felucca"/,/"rules"/ s/"season": 4/"season": 1/' "${mapdef}"

 if grep -A 5 '"name": "Felucca"' "${mapdef}" | grep -q '"season": 1'; then
   ok "Felucca season set to Summer (leafy trees)."
 else
   warn "Season fix may not have applied. Check ${mapdef} manually."
 fi
}

# ---------------------------------------------------------------------------
# Step 6 — UO game data: detect existing, or auto-download
# ---------------------------------------------------------------------------
# ---------------------------------------------------------------------------
# Is this folder actually a usable UO data set?
#
# Checking that a couple of files merely EXIST is not enough, and tiledata.mul
# is the reason. The server picks that file's record layout from its size
# alone: a truncated 3.1 MB file looks to it like an intact 1.6 MB one, so it
# reads the smaller layout, runs off the end, and dies with
#
#   System.IO.EndOfStreamException at Server.TileData.Load()
#
# on first launch. The install "succeeds", the player clicks the icon, and
# gets a .NET stack trace. Catching it here costs one stat per file.
#
# Sizes are floors, not exact matches, so a legitimately different client
# build still passes. tiledata.mul's floor is the server's own 7.0.0 bracket.
# Returns 0 if the folder is usable, 1 otherwise (reason on stderr).
# ---------------------------------------------------------------------------
uo_data_problem() {
  local dir="$1"
  local spec name min actual

  for spec in \
    "tiledata.mul:1644544" \
    "art.mul:10000000"    \
    "artidx.mul:100000"   \
    "map0.mul:50000000"   \
    "statics0.mul:1000000" \
    "staidx0.mul:500000"  \
    "hues.mul:100000"     \
    "radarcol.mul:100000"
  do
    name="${spec%%:*}"
    min="${spec##*:}"

    if [[ ! -f "${dir}/${name}" ]]; then
      printf '%s is missing' "${name}"
      return 1
    fi

    actual="$(stat -c%s "${dir}/${name}" 2>/dev/null || echo 0)"
    if [[ "${actual}" -lt "${min}" ]]; then
      printf '%s is only %s bytes (expected at least %s) - the file is truncated' \
        "${name}" "${actual}" "${min}"
      return 1
    fi
  done

  return 0
}

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
      # Only accept folders that hold a COMPLETE data set. A half-copied
      # or half-extracted folder used to be adopted here on the strength of
      # two files existing, and then killed the server at first launch.
      if [[ -f "${c}/art.mul" ]] && [[ -f "${c}/map0.mul" ]]; then
        local why
        if why="$(uo_data_problem "${c}")"; then
          UO_DATA="${c}"
          ok "Found UO data: ${UO_DATA}"
          return
        fi
        warn "Ignoring incomplete UO data at ${c}"
        warn "  ${why}"
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

  # ~929 MB down, ~1.5 GB extracted. Running out of disk half way through is
  # how a folder full of 0-byte .mul files gets made in the first place.
  local need_mb=2600 free_mb
  free_mb="$(df -Pm "${INSTALL_ROOT}" 2>/dev/null | awk 'NR==2 {print $4}')"
  if [[ -n "${free_mb:-}" ]] && [[ "${free_mb}" -lt "${need_mb}" ]]; then
    die "Not enough disk space for the UO client: ${free_mb} MB free, ${need_mb} MB needed."
  fi

  # A part-downloaded .exe left by an interrupted run used to be reused on
  # the strength of existing. 7z then unpacks the file table without the
  # data behind it, which is a folder of 0-byte .mul files and a server that
  # dies on tiledata.mul at first launch. Judge the file by its size.
  local min_exe=900000000 have=0
  local ua="Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36"

  if [[ -f "${exe_path}" ]]; then
    have="$(stat -c%s "${exe_path}" 2>/dev/null || echo 0)"
  fi

  if [[ "${have}" -ge "${min_exe}" ]]; then
    say "Installer already at ${exe_path}, skipping download."
  else
    if [[ "${have}" -gt 0 ]]; then
      warn "The download at ${exe_path} is incomplete (${have} bytes). Resuming it."
    else
      say "Downloading (this can take 5-15 minutes)..."
    fi
    # The mirror 403's on default wget User-Agent. curl with a real one is
    # fine. -C - resumes rather than starting the 929 MB over again.
    curl -fL --progress-bar -C - -A "${ua}" -o "${exe_path}" "${UO_DATA_URL}"

    have="$(stat -c%s "${exe_path}" 2>/dev/null || echo 0)"
    if [[ "${have}" -lt "${min_exe}" ]]; then
      die "The UO client download finished at ${have} bytes, short of the expected ~929 MB. Delete ${exe_path} and re-run."
    fi
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

  # Verify the extract before trusting it. A download that ended early, or a
  # disk that filled up mid-extract, leaves a folder that passes the old
  # art.mul/map0.mul check and then fails at first launch.
  local why
  if ! why="$(uo_data_problem "${UO_DATA}")"; then
    warn "The extracted UO data is not complete:"
    warn "  ${why}"
    warn "Keeping ${exe_path} so this can be retried without downloading again."
    die "UO data extraction is incomplete. Delete ${INSTALL_ROOT}/UOData and re-run this installer."
  fi

  ok "UO data extracted to: ${UO_DATA}"

  # Keep or delete the installer .exe? Deleting saves 1GB.
  say "Removing installer .exe to save ~929 MB..."
  rm -f "${exe_path}"
}

# ---------------------------------------------------------------------------
# Step 6b — Swap in genuine T2A-era Felucca map art
#
# The UO data dir is shared by BOTH the ModernUO server and the ClassicUO
# client, so swapping map0/statics0/staidx0 here updates rendering AND
# server-side collision/spawn at once, with no desync. radarcol/tiledata are
# left modern (stable across eras). Fully reversible — the modern files are
# backed up to _backup-modern-map/ first. See docs/T2A-MAP.md.
# ---------------------------------------------------------------------------
swap_t2a_map() {
  banner "Installing T2A-era map art"
  [[ "${INSTALL_T2A_MAP}" == "1" ]] || { say "INSTALL_T2A_MAP off; keeping modern map art."; return; }
  [[ -n "${UO_DATA:-}" ]]           || { warn "UO data dir not resolved; skipping T2A map swap."; return; }

  local backup_dir="${UO_DATA}/_backup-modern-map"
  if [[ -f "${backup_dir}/map0.mul" ]]; then
    say "T2A map already swapped (modern backup exists). Skipping."
    return
  fi

  command -v 7z >/dev/null || { warn "7z not found; skipping T2A map swap (install p7zip and re-run)."; return; }

  # 1. Obtain the UOSA installer (cached so re-runs don't re-download ~349 MB).
  mkdir -p "${T2A_SRC_DIR}"
  local uosa_exe="${T2A_SRC_DIR}/UOSA_Client_Setup.exe"
  if [[ ! -f "${uosa_exe}" ]]; then
    say "Downloading UO Second Age client (~349 MB, EA content via uosecondage.com) for its T2A map art..."
    curl -fL --progress-bar \
      -A "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36" \
      -o "${uosa_exe}" "${T2A_INSTALLER_URL}"
  else
    say "UOSA installer already cached at ${uosa_exe}."
  fi

  # 2. Extract the three map files (7z reads the NSIS archive directly).
  local extract_dir="${T2A_SRC_DIR}/uosa-install"
  mkdir -p "${extract_dir}"
  say "Extracting T2A map files with 7z..."
  7z x -y "-o${extract_dir}" "${uosa_exe}" map0.mul statics0.mul staidx0.mul >/dev/null || true

  # Locate them (the NSIS layout may nest the files).
  local missing=0 f src
  declare -A src_path
  for f in map0.mul statics0.mul staidx0.mul; do
    src="$(find "${extract_dir}" -maxdepth 4 -name "${f}" -print -quit 2>/dev/null || true)"
    if [[ -z "${src}" ]]; then warn "T2A ${f} not found after extract."; missing=1; else src_path[${f}]="${src}"; fi
  done
  [[ "${missing}" == "0" ]] || { warn "Aborting T2A swap; modern map kept."; return; }

  # 3. Back up the modern files (the 3 swapped + radarcol/tiledata for safety).
  mkdir -p "${backup_dir}"
  for f in map0.mul statics0.mul staidx0.mul radarcol.mul tiledata.mul; do
    [[ -f "${UO_DATA}/${f}" ]] && cp -f "${UO_DATA}/${f}" "${backup_dir}/${f}"
  done
  ok "Backed up modern map -> ${backup_dir}"

  # 4. Copy the T2A files over the live data dir.
  for f in map0.mul statics0.mul staidx0.mul; do
    cp -f "${src_path[${f}]}" "${UO_DATA}/${f}"
  done
  ok "T2A map art installed (intact Magincia). Revert: cp ${backup_dir}/* back over the data dir."
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
  # Keep a shard name that is already set. ClassicUO stores each player's
  # audio, video, interface and macros under the server's name, so renaming
  # the shard makes all of it look wiped. Only a fresh install gets ours.
  RESOLVED_SHARD_NAME="${SHARD_NAME}"
  local _cfg="${CFG_DIR}/modernuo.json"
  if [[ -f "${_cfg}" ]]; then
    local _prev
    _prev="$(grep -oE '"serverListing\.serverName"[[:space:]]*:[[:space:]]*"[^"]*"' "${_cfg}" \
      | head -n1 | sed -E 's/.*"([^"]*)"[[:space:]]*$/\1/')"
    if [[ -n "${_prev}" ]]; then
      RESOLVED_SHARD_NAME="${_prev}"
      say "Keeping this install's existing shard name: ${_prev}"
    fi
  fi

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
    "serverListing.name": "${RESOLVED_SHARD_NAME}",
    "serverListing.serverName": "${RESOLVED_SHARD_NAME}",
    "accountHandler.enableAutoAccountCreation": "True",
    "pathfinding.prebakeMaps": "True"
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
    "ContextMenus": true,
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

  # FeatureFlags/flags.json - the Young player system is a UO:R-era feature
  # that did not exist in T2A. Left on, young characters also get a
  # Trammel-only public moongate list, which filters down to nothing on this
  # Felucca-only shard and makes the city moongates silently do nothing for
  # every non-staff player.
  mkdir -p "${CFG_DIR}/FeatureFlags"
  cat > "${CFG_DIR}/FeatureFlags/flags.json" <<'EOF'
[
  {
    "Key": "young_player_system",
    "Description": "UO:R-era new player (Young) system. Off for T2A: no (Young) name suffix, no young monster protection, no Haven transport, no New Player Ticket, and no Trammel-only public moongate list.",
    "Enabled": false,
    "DefaultEnabled": true,
    "Category": "Content",
    "LastModified": "2026-08-23T00:00:00Z",
    "LastModifiedBy": "T2A ruleset"
  }
]
EOF
  ok "Wrote FeatureFlags/flags.json (Young player system off - not a T2A feature)"
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

  # The launcher's update checker is optional - an install without it just
  # never offers updates, which is the quiet way to fail.
  if [[ -f "${src_dir}/update-check.sh" ]]; then
    cp "${src_dir}/update-check.sh" "${INSTALL_ROOT}/update-check.sh"
    chmod +x "${INSTALL_ROOT}/update-check.sh"
    ok "Installed update-check.sh"
  fi

  write_version_stamp

  chmod +x "${INSTALL_ROOT}/start.sh" \
           "${INSTALL_ROOT}/stop.sh" \
           "${INSTALL_ROOT}/reset-first-launch.sh"

  ok "Installed start.sh, stop.sh, reset-first-launch.sh"
}

# ---------------------------------------------------------------------------
# Version stamp - what the launcher's update check compares against.
#
# Prefer the git sha of the source we are installing FROM, because that is
# exactly what the player has on disk. Downloaded zips carry no sha, so for
# those we fall back to the current branch head, which is accurate to within
# however long ago the zip was downloaded.
#
# Failing to write this is not an install failure. It only means the launcher
# will not offer updates, which is the quiet, safe direction to fail in.
# ---------------------------------------------------------------------------
write_version_stamp() {
  local repo="Klein187/uo-offline"
  local branch="main"
  local sha=""
  local api="https://api.github.com/repos/${repo}/commits/${branch}"

  if command -v git >/dev/null 2>&1 && [[ -d "${SCRIPT_DIR}/.git" ]]; then
    sha="$(git -C "${SCRIPT_DIR}" rev-parse HEAD 2>/dev/null || true)"
  fi

  if [[ -z "${sha}" ]] && command -v curl >/dev/null 2>&1; then
    sha="$(curl -fsSL --max-time 10 -H "User-Agent: uo-offline-installer" "${api}" 2>/dev/null | grep -oE '"sha"[[:space:]]*:[[:space:]]*"[0-9a-f]{40}"' | head -n1 | grep -oE '[0-9a-f]{40}' || true)"
  fi

  if [[ -z "${sha}" ]]; then
    warn "Could not determine the source version; the launcher will not check for updates."
    return 0
  fi

  cat > "${INSTALL_ROOT}/uo-offline-version.json" <<EOF
{
  "Repo": "${repo}",
  "Branch": "${branch}",
  "Sha": "${sha}",
  "InstalledUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF
  ok "Version stamp: ${sha:0:7}"
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
# Step 4c — Map editor: install the browser-based waypoint/zone/arrival editor
# ---------------------------------------------------------------------------
install_map_editor() {
  banner "Installing map editor"

  if [[ "${INSTALL_MAP_EDITOR}" != "1" ]]; then
    say "Skipped by choice (--no-map-editor)."
    return
  fi

  local src_dir="${SCRIPT_DIR}/tools/map"
  if [[ ! -d "${src_dir}" ]]; then
    say "No tools/map/ in repo; skipping map editor (optional)."
    return
  fi

  if ! command -v python3 >/dev/null; then
    warn "The map editor needs python3, which is not installed. Skipping."
    warn "Install python3 and re-run to get it."
    return
  fi

  local map_dir="${INSTALL_ROOT}/map-editor"
  mkdir -p "${map_dir}"

  # Everything but the debris a working checkout collects.
  local f
  for f in "${src_dir}"/*; do
    case "$(basename "${f}")" in
      __pycache__|*.bak-*) continue ;;
    esac
    cp -r "${f}" "${map_dir}/"
  done

  # Generated, not copied: it has to know where this install actually is, and
  # serve_map.py reads both roots from the environment.
  cat > "${map_dir}/uo-map-launch.sh" <<EOF
#!/bin/bash
# Starts the map editor server if it is not already up, then opens it.
export UO_MAP_DIR="${map_dir}"
export UO_SHARD_ROOT="${INSTALL_ROOT}"
URL="http://localhost:8777/map.html"
LOG="${map_dir}/serve_map.log"

if ! curl -s -o /dev/null --max-time 1 "\${URL}"; then
    nohup python3 "${map_dir}/serve_map.py" >"\${LOG}" 2>&1 &
    for _ in \$(seq 1 10); do
        sleep 0.5
        curl -s -o /dev/null --max-time 1 "\${URL}" && break
    done
fi

xdg-open "\${URL}"
EOF
  chmod +x "${map_dir}/uo-map-launch.sh"

  say "Map editor installed to ${map_dir}."
  say "Run ${map_dir}/uo-map-launch.sh to serve it on http://localhost:8777"
  ok "Map editor ready."
}

# ---------------------------------------------------------------------------
# Step 4b — PlayerBots: deploy bot source files into the ModernUO source tree
#
# This runs BEFORE build_modernuo so the bot code is compiled into the same
# build pass. The bot files live in this repo at ./playerbots/.
# ---------------------------------------------------------------------------
# ---------------------------------------------------------------------------
# Engine patches.
#
# Two stock ModernUO files need a small change for the bots to work
# properly, and they cannot live in CustomBots/ because they ARE engine
# files. They ship as unified diffs under patches/ and go on with
# git apply, which every install already has because it clones ModernUO.
#
# Never fatal. An upstream that has moved on will refuse a patch, and a
# shard missing them still runs - it just loses bot housing across
# restarts. INTEGRATION-NOTES.txt describes both by hand.
# ---------------------------------------------------------------------------
apply_engine_patches() {
  banner "Applying engine patches"

  local patch_dir="${SCRIPT_DIR}/patches"
  if [[ ! -d "${patch_dir}" ]]; then
    say "No patches directory; nothing to apply."
    return 0
  fi

  shopt -s nullglob
  local patches=("${patch_dir}"/*.patch)
  shopt -u nullglob

  if [[ ${#patches[@]} -eq 0 ]]; then
    say "No patches to apply."
    return 0
  fi

  local patch name
  for patch in "${patches[@]}"; do
    name="$(basename "${patch}")"

    # Already applied? Reversing it cleanly is the test.
    if git -C "${MODERNUO_DIR}" apply --reverse --check "${patch}" 2>/dev/null; then
      ok "${name} (already applied)"
      continue
    fi

    if ! git -C "${MODERNUO_DIR}" apply --check "${patch}" 2>/dev/null; then
      warn "${name} does not apply to this ModernUO checkout - skipping."
      warn "See INTEGRATION-NOTES.txt if you need it applied by hand."
      continue
    fi

    git -C "${MODERNUO_DIR}" apply "${patch}"
    ok "${name} applied"
  done
}

remove_engine_patches() {
  local patch_dir="${SCRIPT_DIR}/patches"
  [[ -d "${MODERNUO_DIR}/.git" && -d "${patch_dir}" ]] || return 0

  shopt -s nullglob
  local patches=("${patch_dir}"/*.patch)
  shopt -u nullglob

  local patch name
  for patch in "${patches[@]}"; do
    name="$(basename "${patch}")"

    # --cached checks the unmodified index, while the second check examines
    # the worktree. Together they prove this is a local application of the
    # patch, not the same change already incorporated upstream.
    if git -C "${MODERNUO_DIR}" apply --cached --check "${patch}" 2>/dev/null \
       && git -C "${MODERNUO_DIR}" apply --reverse --check "${patch}" 2>/dev/null; then
      git -C "${MODERNUO_DIR}" apply --reverse "${patch}"
      ok "${name} removed from the source checkout (compiled output is unchanged)"
    fi
  done
}

install_playerbots() {
  banner "Installing PlayerBots"

  local src_dir="${SCRIPT_DIR}/playerbots"
  if [[ ! -d "${src_dir}" ]]; then
    warn "No playerbots/ directory next to install.sh; skipping bot install."
    return
  fi

  local src_target="${MODERNUO_DIR}/Projects/UOContent/CustomBots"

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

  # Deploy every bot data directory present in the repo. The bots need
  # Destinations (where to go), Waypoints (the road graph), Zones (painted
  # areas + portals for arrival), Navigation (field caches), and
  # PlayerBotChat (speech lines). Whole-dir copy so new dirs are picked up
  # automatically.
  for sub in Destinations Waypoints Zones PlayerBotChat; do
    if [[ -d "${src_dir}/data/${sub}" ]]; then
      say "Deploying ${sub} -> ${DIST_DIR}/Data/${sub}"
      mkdir -p "${DIST_DIR}/Data/${sub}"
      cp -rT "${src_dir}/data/${sub}" "${DIST_DIR}/Data/${sub}"
    fi
  done
  # Navigation/fields_cache.bin is a generated distance-field cache; the
  # bots rebuild it on first run. Not shipped (would be stale for a fresh
  # world). Just ensure the dir exists for them to write into.
  mkdir -p "${DIST_DIR}/Data/Navigation"

  # Clean up any legacy files from older bot system versions
  local legacy_files=(
    "${src_target}/Behaviors/RouteRegistry.cs"
    "${src_target}/Behaviors/ReloadRoutesCommand.cs"
    "${src_target}/Behaviors/DestinationRegistry.cs"
  )
  for f in "${legacy_files[@]}"; do
    [[ -f "$f" ]] && rm -f "$f"
  done

  local legacy_dirs=(
    "${DIST_DIR}/Data/Routes"
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
  apply_engine_patches
  # Always make a best-effort cleanup if a later install step exits early.
  trap remove_engine_patches EXIT
  install_playerbots
  install_map_editor
  build_modernuo
  remove_engine_patches
  trap - EXIT
  fix_felucca_season
  find_or_download_uo_data
  swap_t2a_map
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
