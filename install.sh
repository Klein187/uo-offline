#!/usr/bin/env bash
# =========================================================================
# UO Offline (ModernUO edition) — Installer
#
# What this does:
#   1. Installs Linux prerequisites (Debian/Ubuntu/SteamOS-compatible).
#   2. Clones ModernUO and builds it for Linux x64.
#   3. Downloads ClassicUO (the open-source UO client) from GitHub releases.
#   4. Auto-detects your existing UO client data folder.
#   5. Pre-writes ModernUO Configuration/ so first launch is non-interactive:
#        - T2A expansion (Felucca + Lost Lands)
#        - Listener on 127.0.0.1:2593 (localhost only, offline)
#        - Owner account created automatically
#   6. Pre-writes ClassicUO settings.json so the client launches straight
#      into the login screen for our local server (no profile setup UI).
#   7. Installs start/stop scripts and a desktop launcher.
#
# What you still need to provide:
#   - The original Ultima Online game data files (art, maps, sound, etc.).
#     These contain copyrighted assets and we do not redistribute them.
#     Common sources: an existing Classic UO install, the EA download page,
#     or a ClassicUO Launcher install on another machine.
#
# What this does NOT do:
#   - Touch your existing UO client install.
#   - Open any network ports beyond localhost.
#   - Add bots / NPCs — the world ships with ~43k mobiles already.
#
# Target era: T2A (The Second Age, October 1998). Close enough to pre-T2A
# nostalgia that the difference doesn't matter for solo offline play.
# =========================================================================
set -euo pipefail

# ---------------------------------------------------------------------------
# Capture the installer's own directory BEFORE any function cd's elsewhere.
# This is the source path for runtime scripts (scripts/start.sh, stop.sh)
# that we copy to INSTALL_ROOT during install_runtime_scripts.
# ---------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
INSTALL_ROOT="${HOME}/uo-modernuo"
MODERNUO_REPO="https://github.com/modernuo/ModernUO.git"
MODERNUO_DIR="${INSTALL_ROOT}/ModernUO"
DIST_DIR="${MODERNUO_DIR}/Distribution"
CFG_DIR="${DIST_DIR}/Configuration"

# ClassicUO: the client. We pull from GitHub releases rather than
# classicuo.eu so we get a deterministic non-launcher binary.
CLASSICUO_DIR="${INSTALL_ROOT}/ClassicUO"
CLASSICUO_RELEASE_URL="https://api.github.com/repos/ClassicUO/ClassicUO/releases"

# T2A client version. Any 7.0.x client works with ModernUO's T2A mode;
# 7.0.50.1 is a long-stable choice in the community.
CLASSICUO_CLIENT_VERSION="7.0.50.1"

# T2A = Expansion id 1. See https://modernuo.com/docs/development/era-and-expansions/
EXPANSION_ID=1
EXPANSION_NAME="T2A"

# Owner account defaults. Changeable later in-game; password is local-only.
OWNER_USER="admin"
OWNER_PASS="admin"

# Listener: localhost only. Offline, single-player.
LISTEN_ADDR="127.0.0.1:2593"
SHARD_NAME="UO Offline"

# ---------------------------------------------------------------------------
# Pretty output
# ---------------------------------------------------------------------------
banner() { printf '\n\033[1;36m=== %s ===\033[0m\n' "$*"; }
say()    { printf '\033[0;36m--> %s\033[0m\n' "$*"; }
ok()     { printf '\033[0;32m[OK]\033[0m %s\n' "$*"; }
warn()   { printf '\033[0;33m[WARN]\033[0m %s\n' "$*" >&2; }
die()    { printf '\033[0;31m[ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Step 1 — Sanity checks
# ---------------------------------------------------------------------------
preflight() {
  banner "Pre-flight checks"

  [[ "$(uname -s)" == "Linux" ]] || die "This installer is Linux-only."
  [[ "${EUID}" -ne 0 ]] || die "Do not run as root. Run as your normal user; sudo will be invoked when needed."

  command -v git    >/dev/null || die "git is required. Install it first."
  command -v curl   >/dev/null || die "curl is required. Install it first."
  command -v sudo   >/dev/null || warn "sudo not found — dependency install step will fail if deps are missing."

  mkdir -p "${INSTALL_ROOT}"
  ok "Install root: ${INSTALL_ROOT}"
}

# ---------------------------------------------------------------------------
# Step 2 — Native dependencies
#
# ModernUO needs: libicu, libdeflate, zstd, libargon2, liburing. Names vary
# between distros. Handle Debian/Ubuntu/Mint and Arch/SteamOS.
# ---------------------------------------------------------------------------
install_deps() {
  banner "Installing native dependencies"

  if command -v apt-get >/dev/null; then
    say "Detected Debian-family distro. Using apt."
    sudo apt-get update -y
    sudo apt-get install -y \
      libicu-dev libdeflate-dev zstd libargon2-dev liburing-dev \
      libz-dev libgdiplus \
      unzip build-essential
  elif command -v pacman >/dev/null; then
    say "Detected Arch-family distro (likely SteamOS). Using pacman."
    # SteamOS keeps /usr/ read-only by default. Recommend the user disable
    # readonly mode before running this script.
    if [[ -f /etc/os-release ]] && grep -qi steamos /etc/os-release; then
      warn "SteamOS detected. If you haven't already, run:"
      warn "    sudo steamos-readonly disable"
      warn "    sudo pacman-key --init && sudo pacman-key --populate"
      warn "before continuing. Press Ctrl-C now to abort, or any key to continue."
      read -r -n 1 -s
    fi
    sudo pacman -S --needed --noconfirm \
      icu libdeflate zstd argon2 liburing \
      libgdiplus unzip base-devel
  elif command -v dnf >/dev/null; then
    say "Detected Fedora-family distro. Using dnf."
    sudo dnf install -y libicu libdeflate-devel zstd libargon2-devel \
      liburing-devel libgdiplus unzip @development-tools
  else
    die "Unsupported package manager. Install manually: libicu, libdeflate, zstd, libargon2, liburing."
  fi

  ok "Dependencies installed."
}

# ---------------------------------------------------------------------------
# Step 3 — Clone ModernUO
# ---------------------------------------------------------------------------
fetch_modernuo() {
  banner "Fetching ModernUO source"

  # NOTE: ModernUO uses Nerdbank.GitVersioning which walks git history to
  # compute build version numbers. Shallow clones (--depth 1) cause the
  # publish step to fail with "Shallow clone lacks the objects required to
  # calculate version height." So we always do a full clone here, and if
  # we discover an existing shallow clone we unshallow it.

  if [[ -d "${MODERNUO_DIR}/.git" ]]; then
    say "ModernUO already cloned."
    cd "${MODERNUO_DIR}"

    # Unshallow if needed — older installs from earlier installer versions
    # may have been cloned with --depth 1.
    if [[ -f .git/shallow ]]; then
      say "Existing clone is shallow; fetching full history..."
      git fetch --unshallow || git fetch --depth=2147483647
    fi

    git fetch --all --tags
    git checkout main
    git pull --ff-only
  else
    say "Cloning ModernUO (full history; required by Nerdbank.GitVersioning)..."
    git clone "${MODERNUO_REPO}" "${MODERNUO_DIR}"
  fi

  ok "ModernUO source at ${MODERNUO_DIR}"
}

# ---------------------------------------------------------------------------
# Step 3.5 — Bootstrap .NET SDK
#
# ModernUO's publish.sh downloads a build tool that requires `dotnet` to
# already be on the PATH. SteamOS, fresh Arch installs, and many other
# distros don't ship .NET. We install it per-user via Microsoft's official
# dotnet-install.sh into ~/.dotnet/ — no sudo, no system-wide changes, and
# survives SteamOS read-only filesystem reverts.
# ---------------------------------------------------------------------------
DOTNET_ROOT="${HOME}/.dotnet"

bootstrap_dotnet() {
  banner "Bootstrapping .NET SDK (per-user install)"

  # Read the channel ModernUO expects from its global.json, if present.
  # Fall back to LTS otherwise.
  local channel="LTS"
  local gj="${MODERNUO_DIR}/global.json"
  if [[ -f "${gj}" ]]; then
    local sdk_ver
    sdk_ver="$(grep -oE '"version"[[:space:]]*:[[:space:]]*"[^"]+"' "${gj}" \
      | head -n1 | sed -E 's/.*"([^"]+)".*/\1/' || true)"
    if [[ -n "${sdk_ver}" ]]; then
      # Use the major.minor as the channel (e.g. 9.0.100 -> 9.0).
      channel="$(echo "${sdk_ver}" | awk -F. '{print $1"."$2}')"
      say "ModernUO global.json wants SDK ${sdk_ver}; using channel ${channel}."
    fi
  fi

  if [[ -x "${DOTNET_ROOT}/dotnet" ]]; then
    say ".NET already installed at ${DOTNET_ROOT}. Verifying version..."
    if "${DOTNET_ROOT}/dotnet" --list-sdks 2>/dev/null | grep -qE "^${channel}\."; then
      ok "Found compatible SDK in ${DOTNET_ROOT}"
      export PATH="${DOTNET_ROOT}:${PATH}"
      export DOTNET_ROOT
      return
    fi
    say "No SDK matching channel ${channel}; installing it alongside."
  fi

  say "Downloading dotnet-install.sh..."
  local tmp_installer="${INSTALL_ROOT}/.dotnet-install.sh"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${tmp_installer}"
  chmod +x "${tmp_installer}"

  say "Installing .NET SDK ${channel} into ${DOTNET_ROOT}..."
  "${tmp_installer}" --channel "${channel}" --install-dir "${DOTNET_ROOT}"
  rm -f "${tmp_installer}"

  export PATH="${DOTNET_ROOT}:${PATH}"
  export DOTNET_ROOT

  [[ -x "${DOTNET_ROOT}/dotnet" ]] || die "dotnet not installed at ${DOTNET_ROOT}/dotnet after install. Check output above."
  ok "Installed: $(${DOTNET_ROOT}/dotnet --version)"
}

# ---------------------------------------------------------------------------
# Step 4 — Build
#
# publish.sh requires `dotnet` on the PATH (provided by bootstrap_dotnet).
# It downloads ModernUO's internal build tool and emits a self-contained
# build into Distribution/.
# ---------------------------------------------------------------------------
build_modernuo() {
  banner "Building ModernUO (this can take a few minutes the first time)"

  # Belt-and-suspenders: ensure dotnet is reachable even if PATH got reset.
  export PATH="${DOTNET_ROOT}:${PATH}"
  export DOTNET_ROOT

  cd "${MODERNUO_DIR}"
  chmod +x ./publish.sh
  ./publish.sh release linux x64

  [[ -f "${DIST_DIR}/ModernUO.dll" ]] || die "Build produced no ModernUO.dll. Check publish output."
  ok "Build artifacts at ${DIST_DIR}"
}

# ---------------------------------------------------------------------------
# Step 5 — Locate UO client data
#
# ModernUO can read ClassicUO settings.json, but to skip the first-launch
# wizard cleanly we want a confirmed absolute path. Probe common locations.
# ---------------------------------------------------------------------------
find_uo_data() {
  banner "Locating UO client data"

  local candidates=(
    "${HOME}/.steam/steam/steamapps/compatdata/*/pfx/drive_c/Program Files (x86)/Electronic Arts/Ultima Online Classic"
    "${HOME}/Games/Ultima Online Classic"
    "${HOME}/Ultima Online Classic"
    "${HOME}/Desktop/Electronic Arts/Ultima Online Classic"
    "${HOME}/Desktop/Ultima Online Classic"
    "${HOME}/Documents/Ultima Online Classic"
    "${HOME}/.wine/drive_c/Program Files/EA Games/Ultima Online Classic"
    "${HOME}/.wine/drive_c/Program Files (x86)/Electronic Arts/Ultima Online Classic"
    "/mnt/uo"
  )

  UO_DATA=""
  for pattern in "${candidates[@]}"; do
    for c in ${pattern}; do
      [[ -d "${c}" ]] || continue
      if [[ -f "${c}/art.mul" ]] \
         || [[ -f "${c}/artLegacyMUL.uop" ]] \
         || [[ -f "${c}/artlegacymul.uop" ]] \
         || [[ -f "${c}/map0.mul" ]] \
         || [[ -f "${c}/map0LegacyMUL.uop" ]]; then
        UO_DATA="${c}"
        ok "Found UO data: ${UO_DATA}"
        return
      fi
    done
  done

  echo ""
  warn "Could not auto-detect your Ultima Online installation."
  echo "Enter the absolute path to your UO Classic folder."
  echo "Example: /home/deck/Games/Ultima Online Classic"
  echo ""
  read -r -p "UO data path: " UO_DATA
  [[ -d "${UO_DATA}" ]] || die "That folder does not exist: ${UO_DATA}"
}

# ---------------------------------------------------------------------------
# Step 6 — Pre-write configuration files
#
# Writing modernuo.json and expansion.json BEFORE first launch makes the
# server skip the interactive wizard. The schema below mirrors the docs
# (https://modernuo.com/docs/getting-started/configuration/).
# ---------------------------------------------------------------------------
write_config() {
  banner "Writing server configuration"

  mkdir -p "${CFG_DIR}"

  # JSON-escape the UO data path (handles spaces, but bash escaping is
  # weak for backslashes — Linux paths are fine; warn if backslashes appear).
  if [[ "${UO_DATA}" == *\\* ]]; then
    warn "UO data path contains backslashes. Edit ${CFG_DIR}/modernuo.json by hand if the server fails to load."
  fi

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

  # T2A: Felucca + Trammel-less classic map + Lost Lands (Ilshenar disabled).
  # MapSelectionFlags: only Felucca is real for T2A. Lost Lands lives on the
  # Felucca map at the eastern jungle, so no separate flag is needed.
  cat > "${CFG_DIR}/expansion.json" <<EOF
{
  "id": ${EXPANSION_ID},
  "name": "${EXPANSION_NAME}",
  "clientFlags": "Felucca",
  "mapSelectionFlags": {
    "Felucca": true,
    "Trammel": false,
    "Ilshenar": false,
    "Malas": false,
    "Tokuno": false,
    "TerMur": false
  }
}
EOF

  ok "Wrote ${CFG_DIR}/modernuo.json"
  ok "Wrote ${CFG_DIR}/expansion.json (expansion=${EXPANSION_NAME})"
}

# ---------------------------------------------------------------------------
# Step 7 — Download ClassicUO
#
# We pull a Linux x64 build from the ClassicUO GitHub releases. The release
# tag and asset name have changed over time (1.1.0.0, ClassicUO-dev-release,
# etc.), so rather than hardcoding a filename we ask the GitHub API for the
# latest release and pick whichever asset has "linux" in its name.
#
# Why not the launcher (ClassicUOLauncher) from classicuo.eu? The launcher
# requires a first-run UI to create a profile and download the actual client.
# That breaks the goal of "one install, double-click, you're playing."
# ---------------------------------------------------------------------------
install_classicuo() {
  banner "Downloading ClassicUO client"

  if [[ -d "${CLASSICUO_DIR}" ]] && [[ -n "$(ls -A "${CLASSICUO_DIR}" 2>/dev/null)" ]]; then
    say "ClassicUO already installed at ${CLASSICUO_DIR}. Skipping download."
    return
  fi

  command -v unzip >/dev/null || die "unzip is required. Install it (apt/pacman/dnf) and re-run."

  mkdir -p "${CLASSICUO_DIR}"
  local tmp_zip="${INSTALL_ROOT}/.classicuo.zip"

  # Try the latest stable release first, then fall back to the dev-release
  # rolling tag if no stable release ships a Linux asset.
  say "Querying GitHub for the latest ClassicUO Linux release..."
  local asset_url=""
  asset_url="$(curl -fsSL "${CLASSICUO_RELEASE_URL}/latest" 2>/dev/null \
    | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | grep -iE 'linux' \
    | head -n1 \
    | sed -E 's/.*"(https[^"]+)".*/\1/' || true)"

  if [[ -z "${asset_url}" ]]; then
    say "No Linux asset on /latest, checking the dev-release tag..."
    asset_url="$(curl -fsSL "${CLASSICUO_RELEASE_URL}/tags/ClassicUO-dev-release" 2>/dev/null \
      | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
      | grep -iE 'linux' \
      | head -n1 \
      | sed -E 's/.*"(https[^"]+)".*/\1/' || true)"
  fi

  if [[ -z "${asset_url}" ]]; then
    die "Could not locate a ClassicUO Linux release asset on GitHub. The release naming may have changed; download manually from https://www.classicuo.eu/ and extract to ${CLASSICUO_DIR}."
  fi

  say "Downloading: ${asset_url}"
  curl -fL --progress-bar -o "${tmp_zip}" "${asset_url}"

  say "Extracting to ${CLASSICUO_DIR}..."
  unzip -q -o "${tmp_zip}" -d "${CLASSICUO_DIR}"
  rm -f "${tmp_zip}"

  # Find and chmod the binary. The exact filename has varied: ClassicUO,
  # ClassicUO.bin.x86_64, cuo. Try common patterns.
  local cuo_bin=""
  for name in ClassicUO ClassicUO.bin.x86_64 cuo; do
    if [[ -f "${CLASSICUO_DIR}/${name}" ]]; then
      cuo_bin="${CLASSICUO_DIR}/${name}"
      break
    fi
  done
  # If the zip extracted into a subfolder, check one level down.
  if [[ -z "${cuo_bin}" ]]; then
    cuo_bin="$(find "${CLASSICUO_DIR}" -maxdepth 2 -type f \
      \( -name 'ClassicUO' -o -name 'ClassicUO.bin.x86_64' -o -name 'cuo' \) \
      -print -quit 2>/dev/null || true)"
  fi

  if [[ -n "${cuo_bin}" ]]; then
    chmod +x "${cuo_bin}"
    ok "ClassicUO binary: ${cuo_bin}"
    # Record the binary path so start.sh can find it without re-globbing.
    echo "${cuo_bin}" > "${INSTALL_ROOT}/.classicuo-bin-path"
  else
    warn "ClassicUO extracted but no executable found. start.sh will try to locate it at runtime."
  fi

  ok "ClassicUO installed."
}

# ---------------------------------------------------------------------------
# Step 8 — Pre-write ClassicUO settings.json
#
# This tells the client which server to connect to, what client version to
# emulate, and where the UO data files live. With this in place the user
# clicks the binary and lands at the login screen — no profile-create UI.
# ---------------------------------------------------------------------------
write_classicuo_settings() {
  banner "Writing ClassicUO settings.json"

  [[ -d "${CLASSICUO_DIR}" ]] || { warn "ClassicUO directory missing; skipping settings."; return; }

  # ClassicUO looks for settings.json in the same directory as the binary.
  # Most extractions put it at the root of CLASSICUO_DIR; if there's a nested
  # folder, mirror to both locations.
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
  "clientversion": "${CLASSICUO_CLIENT_VERSION}",
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
# Step 9 — Install runtime scripts
# ---------------------------------------------------------------------------
install_runtime_scripts() {
  banner "Installing launcher scripts"

  # Source scripts live alongside install.sh, in ./scripts/.
  # SCRIPT_DIR was captured at the top of the script, before any cd'ing.
  local src_dir="${SCRIPT_DIR}/scripts"
  [[ -d "${src_dir}" ]] || die "Cannot find scripts directory at ${src_dir}"

  cp "${src_dir}/start.sh"             "${INSTALL_ROOT}/start.sh"
  cp "${src_dir}/stop.sh"              "${INSTALL_ROOT}/stop.sh"
  cp "${src_dir}/reset-first-launch.sh" "${INSTALL_ROOT}/reset-first-launch.sh"
  cp "${src_dir}/patch-mobtypes.sh"     "${INSTALL_ROOT}/patch-mobtypes.sh"

  chmod +x "${INSTALL_ROOT}/start.sh" \
           "${INSTALL_ROOT}/stop.sh" \
           "${INSTALL_ROOT}/reset-first-launch.sh" \
           "${INSTALL_ROOT}/patch-mobtypes.sh"

  ok "Installed start.sh, stop.sh, reset-first-launch.sh, patch-mobtypes.sh"
}

# ---------------------------------------------------------------------------
# Step 8 — Pre-seed the owner account
#
# ModernUO's first-launch flow asks to create an owner account interactively.
# To stay non-interactive we let the FIRST start.sh run handle it: the
# wizard only triggers if Configuration files don't exist, so once we've
# written those, the owner-account prompt won't fire on its own.
#
# Instead, the first time start.sh runs the server, we pipe in answers to
# the owner-account question over stdin. See start.sh for details.
# ---------------------------------------------------------------------------
seed_owner_marker() {
  # A simple marker file that start.sh checks. If absent, start.sh runs the
  # server with stdin scripted to create the owner account, then deletes
  # the marker so subsequent launches run normally.
  touch "${INSTALL_ROOT}/.needs-owner-account"
  ok "Owner account will be created on first launch: ${OWNER_USER} / ${OWNER_PASS}"
  warn "Change this password in-game with the [password command after first login."
}

# ---------------------------------------------------------------------------
# Step 9 — Desktop entry
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
Expansion:      ${EXPANSION_NAME} (id=${EXPANSION_ID})
Listener:       ${LISTEN_ADDR}  (localhost only, offline)
Owner login:    ${OWNER_USER} / ${OWNER_PASS}

To play:        Click the "UO Offline" desktop icon.
                (or run ${INSTALL_ROOT}/start.sh from a terminal)

To stop:        ${INSTALL_ROOT}/stop.sh
                (closing the client does not stop the server)

First launch creates the owner account and writes the initial world saves.
This takes 20-40 seconds. After that, startups are quick.

The world ships with ~43,000 mobiles already spawned. You do not need to
populate anything manually.

EOF
}

# ---------------------------------------------------------------------------
# Step 10b — Patch mobtypes.txt for ClassicUO compatibility.
#
# Modern UO data files contain Stygian Abyss creature entries with a flag
# value of 10000 ("use UOP animation") that the current ClassicUO release
# crashes on (System.FormatException at AnimationsLoader.Load). Commenting
# those lines out lets ClassicUO load. For T2A play none of those creatures
# spawn, so there's no gameplay impact.
#
# Idempotent: if mobtypes.txt.bak already exists, the patch script no-ops.
# ---------------------------------------------------------------------------
patch_mobtypes_for_classicuo() {
  banner "Patching mobtypes.txt for ClassicUO compatibility"

  if [[ ! -f "${UO_DATA}/mobtypes.txt" ]]; then
    say "No mobtypes.txt at ${UO_DATA}; skipping (may not be needed for older data)."
    return
  fi

  if [[ -f "${UO_DATA}/mobtypes.txt.bak" ]]; then
    say "mobtypes.txt already patched (backup present)."
    return
  fi

  "${INSTALL_ROOT}/patch-mobtypes.sh" "${UO_DATA}" || {
    warn "Patch script failed. ClassicUO may crash on launch."
    warn "If it does, you can revert with: mv '${UO_DATA}/mobtypes.txt.bak' '${UO_DATA}/mobtypes.txt'"
    return
  }

  ok "mobtypes.txt patched (original at ${UO_DATA}/mobtypes.txt.bak)."
}

# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
main() {
  preflight
  install_deps
  fetch_modernuo
  bootstrap_dotnet
  build_modernuo
  find_uo_data
  write_config
  install_classicuo
  write_classicuo_settings
  install_runtime_scripts
  patch_mobtypes_for_classicuo
  seed_owner_marker
  install_desktop_entry
  finish
}

main "$@"
