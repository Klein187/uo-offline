#!/usr/bin/env bash
# =========================================================================
# UO Offline (ModernUO edition) — macOS Apple Silicon Installer
#
# What this does:
#   1. Checks macOS environment (Darwin arm64, Homebrew, Rosetta 2).
#   2. Installs dependencies via Homebrew (7-Zip).
#   3. Clones ModernUO and bootstraps .NET SDK (arm64) per-user.
#   4. Deploys PlayerBots source files into ModernUO and applies patches.
#   5. Builds ModernUO natively for macOS Apple Silicon (osx arm64).
#   6. Downloads ClassicUO client (macOS release).
#   7. Downloads UO Classic 7.0.23.1 game data from community mirror
#      (or uses an existing install if found).
#   7b. Swaps in genuine T2A-era Felucca map art (intact Magincia).
#   8. Downloads Nerun's pre-T2A spawn map for world population.
#   9. Writes ModernUO and ClassicUO configs (T2A, localhost-only).
#  10. Installs macOS launch scripts and a Desktop launcher.
#
# Server listens on 127.0.0.1:2593 only. Fully offline, no network exposure.
# =========================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---------------------------------------------------------------------------
# Arguments and Paths
# ---------------------------------------------------------------------------
INSTALL_MAP_EDITOR=1
INSTALL_T2A_MAP=1

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --install-root)
      if [[ "$#" -lt 2 || -z "$2" ]]; then
        printf '%s\n' "[ERROR] --install-root requires a path." >&2
        exit 1
      fi
      INSTALL_ROOT="$2"
      shift 2
      ;;
    --install-root=*)
      INSTALL_ROOT="${1#*=}"
      [[ -n "${INSTALL_ROOT}" ]] || {
        printf '%s\n' "[ERROR] --install-root requires a path." >&2
        exit 1
      }
      shift
      ;;
    --no-map-editor)
      INSTALL_MAP_EDITOR=0
      shift
      ;;
    --no-t2a-map)
      INSTALL_T2A_MAP=0
      shift
      ;;
    *)
      shift
      ;;
  esac
done

INSTALL_ROOT="${INSTALL_ROOT:-${HOME}/uo-modernuo}"
[[ "${INSTALL_ROOT}" == "/" ]] || INSTALL_ROOT="${INSTALL_ROOT%/}"

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

UO_DATA_URL="https://mirror.ashkantra.de/fullclients/7.0.23.1.exe"
UO_DATA_VERSION="7.0.23.1"
UO_DATA_DIR="${INSTALL_ROOT}/UOData/${UO_DATA_VERSION}"

SPAWN_MAP_URL="https://raw.githubusercontent.com/Nerun/runuo-nerun-distro/master/Distro/Data/Nerun's%20Distro/Spawns/uoclassic/UOClassic.map"

T2A_INSTALLER_URL="https://download.uosecondage.com/UOSA_Client_Setup.exe"
T2A_SRC_DIR="${INSTALL_ROOT}/t2a-src"

EXPANSION_ID=1
EXPANSION_NAME="T2A"
OWNER_USER="admin"
OWNER_PASS="admin"
LISTEN_ADDR="127.0.0.1:2593"
SHARD_NAME="UO Offline"

DOTNET_ROOT="${HOME}/.dotnet"

# ---------------------------------------------------------------------------
# Output formatting
# ---------------------------------------------------------------------------
banner() { printf '\n\033[1;36m=== %s ===\033[0m\n' "$*"; }
say()    { printf '\033[0;36m--> %s\033[0m\n' "$*"; }
ok()     { printf '\033[0;32m[OK]\033[0m %s\n' "$*"; }
warn()   { printf '\033[0;33m[WARN]\033[0m %s\n' "$*" >&2; }
die()    { printf '\033[0;31m[ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

find_7z_extractor() {
  local name
  for name in 7zz 7z 7za; do
    if command -v "${name}" >/dev/null 2>&1; then
      printf '%s' "${name}"
      return 0
    fi
  done
  return 1
}

# Unpack the UO Classic full-client installer.
#
# The file is named .exe and feeding it to 7-Zip is wrong in a way that looks
# almost right: it is a WinRAR self-extracting archive with a RAR5 payload
# about 1.1 MB in. 7-Zip parses the RAR container well enough to LIST every
# entry, so the extract appears to start, then fails every file with
# "Unsupported Method" -- neither p7zip nor the Homebrew 7-Zip build carries a
# working RAR5 decoder. Windows never hits this; it runs the SFX's own WinRAR
# stub with -s2 -y -d.
#
#   unrar  reads RAR5 and scans for the payload itself
#   7zz    official 7-Zip build; scans, but cannot decode RAR5 here
#   unar   free-licensed (brew install unar), reads RAR5, but only sniffs the
#          front of a file -- it needs the stub stripped off first
unpack_uo_exe() {
  local exe="$1" dest="$2" tool rc=1

  # Pass 1: tools that can find the payload behind the SFX stub on their own.
  for tool in unrar 7zz 7z unar; do
    command -v "${tool}" >/dev/null || continue
    say "Extracting with ${tool}..."
    run_extractor "${tool}" "${exe}" "${dest}" && { rc=0; break; }
    warn "${tool} could not read it directly."
  done

  # Pass 2: strip the stub and hand over a plain .rar. The payload is a
  # complete standalone archive, so everything that speaks RAR5 takes it.
  if [[ "${rc}" -ne 0 ]]; then
    local off rar="${exe%.exe}.rar"
    off="$(rar_payload_offset "${exe}")"
    if [[ -n "${off}" ]]; then
      say "Stripping the self-extractor stub (payload at byte ${off})..."
      if tail -c "+$((off + 1))" "${exe}" > "${rar}"; then
        for tool in unar unrar 7zz 7z; do
          command -v "${tool}" >/dev/null || continue
          say "Extracting with ${tool}..."
          run_extractor "${tool}" "${rar}" "${dest}" && { rc=0; break; }
          warn "${tool} could not read the stripped archive either."
        done
      fi
      rm -f "${rar}"
    else
      warn "No RAR5 payload found inside ${exe}; the download may be damaged."
    fi
  fi

  [[ "${rc}" -eq 0 ]] && return 0

  warn "Could not unpack ${exe}."
  warn "It is a WinRAR (RAR5) self-extracting archive, which needs a real RAR"
  warn "decoder. Install one and re-run this script:"
  warn "    brew install unar"
  return 1
}

# One extraction attempt. Judged by whether art.mul actually appeared, never
# by the exit code -- 7-Zip "succeeds" while failing every single file.
run_extractor() {
  local tool="$1" archive="$2" dest="$3"

  case "${tool}" in
    # -D stops unar wrapping everything in a folder named after the archive;
    # the payload already carries its own version folder.
    unar)  unar -q -f -D -o "${dest}" "${archive}" >/dev/null 2>&1 || true ;;
    unrar) unrar x -y -inul "${archive}" "${dest}/" >/dev/null 2>&1 || true ;;
    *)     "${tool}" x -y "-o${dest}" "${archive}" >/dev/null 2>&1 || true ;;
  esac

  if [[ -n "$(find "${dest}" -maxdepth 3 -name art.mul -size +0c -print -quit 2>/dev/null)" ]]; then
    ok "Extracted with ${tool}."
    return 0
  fi
  return 1
}

# Byte offset of the RAR5 signature inside the self-extractor, or empty.
# python3 only: BSD grep has no -P, so the GNU binary-grep fallback the Linux
# installer uses cannot work here. python3 is already a hard dependency.
rar_payload_offset() {
  local exe="$1" off=""

  command -v python3 >/dev/null 2>&1 || return 0

  off="$(python3 - "${exe}" <<'PYEOF' 2>/dev/null
import sys
sig = b"Rar!\x1a\x07\x01\x00"
pos, prev, base = -1, b"", 0
with open(sys.argv[1], "rb") as f:
    while True:
        chunk = f.read(8 << 20)
        if not chunk:
            break
        buf = prev + chunk
        i = buf.find(sig)
        if i >= 0:
            pos = base - len(prev) + i
            break
        prev = buf[-16:]
        base += len(chunk)
print(pos if pos >= 0 else "")
PYEOF
)"

  [[ "${off}" =~ ^[0-9]+$ ]] && printf '%s' "${off}"
}

json_escape() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

has_uo_data() {
  local dir="$1"
  [[ -s "${dir}/art.mul" ]] \
    && [[ -s "${dir}/map0.mul" ]] \
    && [[ -s "${dir}/tiledata.mul" ]]
}

# ---------------------------------------------------------------------------
# Step 1 — Pre-flight checks
# ---------------------------------------------------------------------------
preflight() {
  banner "Pre-flight checks (macOS Apple Silicon)"

  [[ "$(uname -s)" == "Darwin" ]] || die "This installer is for macOS. Use install.sh on Linux or install.bat on Windows."
  [[ "${EUID}" -ne 0 ]]          || die "Run as your normal user, not root."

  local arch
  arch="$(uname -m)"
  [[ "${arch}" == "arm64" ]] || die "Detected ${arch}; this installer requires Apple Silicon (arm64)."
  ok "Apple Silicon (arm64) architecture confirmed."

  command -v curl >/dev/null || die "curl is required."

  # Detect Homebrew
  if ! command -v brew >/dev/null 2>&1; then
    if [[ -x "/opt/homebrew/bin/brew" ]]; then
      eval "$(/opt/homebrew/bin/brew shellenv)"
    elif [[ -x "/usr/local/bin/brew" ]]; then
      eval "$(/usr/local/bin/brew shellenv)"
    else
      die "Homebrew is required for dependencies. Install it from https://brew.sh and re-run."
    fi
  fi
  ok "Homebrew found: $(brew --version | head -n1)"

  # Verify Rosetta 2
  if ! arch -x86_64 /usr/bin/true 2>/dev/null; then
    say "Rosetta 2 is needed for ClassicUO. Installing..."
    softwareupdate --install-rosetta --agree-to-license || true
    arch -x86_64 /usr/bin/true 2>/dev/null || die "Rosetta 2 is required for the macOS ClassicUO client."
  else
    ok "Rosetta 2 translation layer ready."
  fi

  mkdir -p "${INSTALL_ROOT}" 2>/dev/null \
    || die "Cannot create ${INSTALL_ROOT}. Pick a folder you can write to."
  [[ -w "${INSTALL_ROOT}" ]] \
    || die "${INSTALL_ROOT} is not writable. Pick a folder you own."

  ok "Install root: ${INSTALL_ROOT}"
}

# ---------------------------------------------------------------------------
# Step 2 — Dependencies
# ---------------------------------------------------------------------------
install_deps() {
  banner "Installing dependencies via Homebrew"

  if ! command -v 7zz >/dev/null 2>&1; then
    say "Installing modern 7-Zip..."
    brew install sevenzip
  fi

  # The UO client installer is a RAR5 self-extractor. unar is the only
  # free, still-maintained RAR5 reader on macOS: the `rar` cask is
  # deprecated (disabled 2026-09-01) and homebrew-core has no unrar.
  if ! command -v unar >/dev/null 2>&1 && ! command -v unrar >/dev/null 2>&1; then
    say "Installing unar (RAR5 extractor)..."
    brew install unar
  fi

  command -v git >/dev/null 2>&1 || {
    say "Installing git..."
    brew install git
  }

  command -v python3 >/dev/null 2>&1 || {
    say "Installing python3..."
    brew install python
  }

  find_7z_extractor >/dev/null || die "Could not find a 7-Zip executable after installing dependencies."
  command -v unar >/dev/null 2>&1 || command -v unrar >/dev/null 2>&1 \
    || die "No RAR5 extractor found. Install one with 'brew install unar' and retry."

  ok "Native dependencies ready."
}

# ---------------------------------------------------------------------------
# Step 3 — Clone ModernUO
# ---------------------------------------------------------------------------
fetch_modernuo() {
  banner "Fetching ModernUO source"

  if [[ -d "${MODERNUO_DIR}/.git" ]]; then
    say "ModernUO already cloned."
    cd "${MODERNUO_DIR}"

    local before_sha=""
    local stash_needed=0
    before_sha="$(git rev-parse HEAD 2>/dev/null || true)"

    # Engine patches are intentionally kept as local changes. Stash tracked
    # changes while updating so those patches do not block fast-forward pulls.
    if ! git diff --quiet || ! git diff --cached --quiet; then
      if git stash push -m "uo-offline installer update" >/dev/null 2>&1; then
        stash_needed=1
      else
        warn "Could not stash local ModernUO changes; update may be skipped."
      fi
    fi

    if [[ -f .git/shallow ]]; then
      say "Unshallowing existing clone..."
      git fetch --unshallow || git fetch --depth=2147483647
    fi

    git fetch --all --tags --force || warn "git fetch failed; using checkout on disk."
    git checkout main               || warn "git checkout main failed; using current branch."
    git pull --ff-only              || warn "git pull failed; using checkout on disk."

    if [[ "${stash_needed}" == "1" ]]; then
      git stash pop >/dev/null 2>&1 || warn "Could not reapply local ModernUO changes cleanly."
    fi

    local after_sha=""
    after_sha="$(git rev-parse HEAD 2>/dev/null || true)"
    if [[ -n "${before_sha}" && -n "${after_sha}" && "${before_sha}" != "${after_sha}" ]]; then
      rm -f "${DIST_DIR}/ModernUO.dll"
      say "ModernUO changed; forcing a rebuild."
    fi
  else
    say "Cloning ModernUO (full history)..."
    git clone "${MODERNUO_REPO}" "${MODERNUO_DIR}"
  fi

  ok "ModernUO source at ${MODERNUO_DIR}"
}

# ---------------------------------------------------------------------------
# Step 4 — Bootstrap .NET SDK (arm64)
# ---------------------------------------------------------------------------
bootstrap_dotnet() {
  banner "Bootstrapping .NET SDK (arm64)"

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

  say "Installing .NET SDK ${channel} (arm64) into ${DOTNET_ROOT}..."
  "${tmp}" --channel "${channel}" --architecture arm64 --install-dir "${DOTNET_ROOT}"
  rm -f "${tmp}"

  export PATH="${DOTNET_ROOT}:${PATH}"
  export DOTNET_ROOT

  [[ -x "${DOTNET_ROOT}/dotnet" ]] || die "dotnet not installed at ${DOTNET_ROOT}/dotnet."
  ok "Installed .NET: $(${DOTNET_ROOT}/dotnet --version)"
}

# ---------------------------------------------------------------------------
# Step 5 — Engine patches & PlayerBots
# ---------------------------------------------------------------------------
apply_engine_patches() {
  banner "Applying engine patches"

  local patch_dir="${SCRIPT_DIR}/patches"
  if [[ ! -d "${patch_dir}" ]]; then
    say "No patches directory; skipping."
    return 0
  fi

  local patches=("${patch_dir}"/*.patch)
  if [[ ! -e "${patches[0]}" ]]; then
    say "No patches to apply."
    return 0
  fi

  local patch name
  for patch in "${patches[@]}"; do
    [[ -f "${patch}" ]] || continue
    name="$(basename "${patch}")"

    if git -C "${MODERNUO_DIR}" apply --reverse --check "${patch}" 2>/dev/null; then
      ok "${name} (already applied)"
      continue
    fi

    if ! git -C "${MODERNUO_DIR}" apply --check "${patch}" 2>/dev/null; then
      die "${name} does not apply cleanly to ModernUO."
    fi

    git -C "${MODERNUO_DIR}" apply "${patch}"
    rm -f "${DIST_DIR}/ModernUO.dll"
    ok "${name} applied"
  done
}

install_playerbots() {
  banner "Installing PlayerBots"

  local src_dir="${SCRIPT_DIR}/playerbots"
  if [[ ! -d "${src_dir}" ]]; then
    warn "No playerbots/ directory found; skipping."
    return
  fi

  local src_target="${MODERNUO_DIR}/Projects/UOContent/CustomBots"

  local new_hash
  if command -v shasum >/dev/null 2>&1; then
    new_hash="$(find "${src_dir}/source" "${src_dir}/data" -type f -exec shasum -a 256 {} + 2>/dev/null \
      | sort | shasum -a 256 | cut -d' ' -f1)"
  else
    new_hash="$(find "${src_dir}/source" "${src_dir}/data" -type f -exec sha256sum {} + 2>/dev/null \
      | sort | sha256sum | cut -d' ' -f1)"
  fi

  local hash_file="${src_target}/.deployed-hash"
  local prev_hash=""
  [[ -f "${hash_file}" ]] && prev_hash="$(cat "${hash_file}")"

  if [[ -d "${src_target}" && "${new_hash}" == "${prev_hash}" ]]; then
    say "PlayerBot sources unchanged. Skipping deploy."
    return
  fi

  say "Deploying bot source -> ${src_target}"
  rm -rf "${src_target}"
  mkdir -p "${src_target}"
  cp -R "${src_dir}/source/CustomBots/." "${src_target}/"

  for sub in Destinations Waypoints Zones PlayerBotChat; do
    rm -rf "${DIST_DIR}/Data/${sub}"
    if [[ -d "${src_dir}/data/${sub}" ]]; then
      say "Deploying ${sub} -> ${DIST_DIR}/Data/${sub}"
      mkdir -p "${DIST_DIR}/Data/${sub}"
      cp -R "${src_dir}/data/${sub}/." "${DIST_DIR}/Data/${sub}/"
    fi
  done
  mkdir -p "${DIST_DIR}/Data/Navigation"

  # Clean legacy files
  rm -f "${src_target}/Behaviors/RouteRegistry.cs" \
        "${src_target}/Behaviors/ReloadRoutesCommand.cs" \
        "${src_target}/Behaviors/DestinationRegistry.cs"
  rm -rf "${DIST_DIR}/Data/Routes"

  if [[ -f "${DIST_DIR}/ModernUO.dll" ]]; then
    say "Bot sources changed — clearing build cache to trigger rebuild"
    rm -f "${DIST_DIR}/ModernUO.dll"
  fi

  echo "${new_hash}" > "${hash_file}"

  ok "PlayerBots deployed."
}

# ---------------------------------------------------------------------------
# Step 6 — Map editor
# ---------------------------------------------------------------------------
install_map_editor() {
  banner "Installing map editor"

  if [[ "${INSTALL_MAP_EDITOR}" != "1" ]]; then
    say "Skipped (--no-map-editor)."
    return
  fi

  local src_dir="${SCRIPT_DIR}/tools/map"
  if [[ ! -d "${src_dir}" ]]; then
    say "No tools/map/ in repo; skipping map editor."
    return
  fi

  if ! command -v python3 >/dev/null; then
    warn "The map editor needs python3. Skipping."
    return
  fi

  local map_dir="${INSTALL_ROOT}/map-editor"
  mkdir -p "${map_dir}"

  local f
  for f in "${src_dir}"/*; do
    case "$(basename "${f}")" in
      __pycache__|*.bak-*) continue ;;
    esac
    cp -R "${f}" "${map_dir}/"
  done

  local map_dir_q install_root_q
  map_dir_q="$(printf '%q' "${map_dir}")"
  install_root_q="$(printf '%q' "${INSTALL_ROOT}")"
  cat > "${map_dir}/uo-map-launch.sh" <<EOF
#!/bin/bash
export UO_MAP_DIR=${map_dir_q}
export UO_SHARD_ROOT=${install_root_q}
URL="http://localhost:8777/map.html"
LOG=${map_dir_q}/serve_map.log

if ! curl -s -o /dev/null --max-time 1 "\${URL}"; then
    nohup python3 ${map_dir_q}/serve_map.py >"\${LOG}" 2>&1 &
    for _ in \$(seq 1 10); do
        sleep 0.5
        curl -s -o /dev/null --max-time 1 "\${URL}" && break
    done
fi

open "\${URL}"
EOF
  chmod +x "${map_dir}/uo-map-launch.sh"

  ok "Map editor ready at ${map_dir}/uo-map-launch.sh"
}

# ---------------------------------------------------------------------------
# Step 7 — Build ModernUO (osx arm64)
# ---------------------------------------------------------------------------
clear_build_artifacts() {
  local projects_dir="${MODERNUO_DIR}/Projects"
  [[ -d "${projects_dir}" ]] || return 0
  for d in "${projects_dir}"/*/obj "${projects_dir}"/*/bin; do
    [[ -d "${d}" ]] && rm -rf "${d}"
  done
}

build_modernuo() {
  banner "Building ModernUO for macOS Apple Silicon (osx arm64)"

  export PATH="${DOTNET_ROOT}:${PATH}"
  export DOTNET_ROOT

  if [[ -f "${DIST_DIR}/ModernUO.dll" ]]; then
    say "ModernUO already built. Skipping (delete ${DIST_DIR}/ModernUO.dll to force rebuild)."
    return
  fi

  cd "${MODERNUO_DIR}"
  chmod +x ./publish.sh
  local first_status=0
  ./publish.sh release osx arm64 || first_status=$?

  if [[ "${first_status}" -ne 0 || ! -f "${DIST_DIR}/ModernUO.dll" ]]; then
    warn "First build attempt did not produce ModernUO.dll. Clearing obj/bin and retrying..."
    clear_build_artifacts
    ./publish.sh release osx arm64 || die "Build command failed on retry."
  fi

  [[ -f "${DIST_DIR}/ModernUO.dll" ]] || die "Build failed to produce ModernUO.dll. Check build logs."
  ok "Build artifacts generated at ${DIST_DIR}"
}

# ---------------------------------------------------------------------------
# Step 8 — Fix Felucca season
# ---------------------------------------------------------------------------
fix_felucca_season() {
  banner "Setting Felucca season to Summer"

  local mapdef="${MODERNUO_DIR}/Distribution/Data/map-definitions.json"
  [[ -f "${mapdef}" ]] || { warn "map-definitions.json not found."; return; }
  command -v python3 >/dev/null 2>&1 || die "python3 is required to update map-definitions.json."

  python3 - "${mapdef}" <<'PY'
import json
p = __import__('sys').argv[1]
with open(p, 'r') as f:
    data = json.load(f)
found = False
maps = data if isinstance(data, list) else data.get('maps', [])
if not isinstance(maps, list):
    raise SystemExit('Unexpected map-definitions.json format')
for m in maps:
    if not isinstance(m, dict):
        continue
    if m.get('name') == 'Felucca':
        m['season'] = 1
        found = True
if not found:
    raise SystemExit('Felucca map definition not found')
with open(p, 'w') as f:
    json.dump(data, f, indent=2)
PY

  ok "Felucca season verified."
}

# ---------------------------------------------------------------------------
# Step 9 — UO game data
# ---------------------------------------------------------------------------
find_or_download_uo_data() {
  banner "Locating UO game data"

  local candidates=(
    "${HOME}/Ultima Online Classic"
    "${HOME}/Desktop/Ultima Online Classic"
    "${HOME}/Documents/Ultima Online Classic"
    "${HOME}/Games/Ultima Online Classic"
    "${INSTALL_ROOT}/UOData/${UO_DATA_VERSION}"
  )

  for c in "${candidates[@]}"; do
    if [[ -d "${c}" ]] && has_uo_data "${c}"; then
      UO_DATA="${c}"
      ok "Found existing UO data: ${UO_DATA}"
      return
    fi
  done

  local existing
  existing="$(find "${INSTALL_ROOT}/UOData" -maxdepth 4 -type f -name "art.mul" -size +0c -print -quit 2>/dev/null || true)"
  if [[ -n "${existing}" ]] && has_uo_data "$(dirname "${existing}")"; then
    UO_DATA="$(dirname "${existing}")"
    ok "Found existing UO data: ${UO_DATA}"
    return
  fi

  say "Downloading UO Classic ${UO_DATA_VERSION} (~929 MB)..."
  mkdir -p "${INSTALL_ROOT}/UOData"
  local exe_path="${INSTALL_ROOT}/UOData/${UO_DATA_VERSION}.exe"

  if [[ ! -f "${exe_path}" ]]; then
    local exe_tmp="${exe_path}.part.$$"
    curl -fL --progress-bar \
      -A "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36" \
      -o "${exe_tmp}" \
      "${UO_DATA_URL}"
    mv -f "${exe_tmp}" "${exe_path}"
  fi

  say "Extracting game data..."
  mkdir -p "${UO_DATA_DIR}"
  unpack_uo_exe "${exe_path}" "${INSTALL_ROOT}/UOData" \
    || die "Could not extract ${exe_path}. See the messages above."

  if ! has_uo_data "${UO_DATA_DIR}"; then
    local found
    found="$(find "${INSTALL_ROOT}/UOData" -maxdepth 3 -type f -name "art.mul" -size +0c -print -quit 2>/dev/null)"
    if [[ -n "${found}" ]] && has_uo_data "$(dirname "${found}")"; then
      UO_DATA_DIR="$(dirname "${found}")"
    else
      die "Extraction succeeded but required UO data files are missing or empty under ${INSTALL_ROOT}/UOData."
    fi
  fi

  UO_DATA="${UO_DATA_DIR}"
  rm -f "${exe_path}"
  ok "UO game data ready: ${UO_DATA}"
}

# ---------------------------------------------------------------------------
# Step 9b — Swap T2A map art
# ---------------------------------------------------------------------------
swap_t2a_map() {
  banner "Installing T2A-era map art"
  [[ "${INSTALL_T2A_MAP}" == "1" ]] || { say "T2A map swap skipped (--no-t2a-map)."; return; }
  [[ -n "${UO_DATA:-}" ]]           || { warn "UO data dir not resolved; skipping T2A map swap."; return; }

  local backup_dir="${UO_DATA}/_backup-modern-map"
  if [[ -s "${backup_dir}/map0.mul" ]] \
     && [[ -s "${backup_dir}/statics0.mul" ]] \
     && [[ -s "${backup_dir}/staidx0.mul" ]]; then
    say "T2A map already installed (backup exists)."
    return
  fi
  [[ -d "${backup_dir}" ]] && rm -rf "${backup_dir}"

  local extractor
  extractor="$(find_7z_extractor)" || die "No 7-Zip executable found."

  mkdir -p "${T2A_SRC_DIR}"
  local uosa_exe="${T2A_SRC_DIR}/UOSA_Client_Setup.exe"
  if [[ ! -f "${uosa_exe}" ]]; then
    say "Downloading UO Second Age client (~349 MB) for T2A map art..."
    local uosa_tmp="${uosa_exe}.part.$$"
    curl -fL --progress-bar \
      -A "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36" \
      -o "${uosa_tmp}" "${T2A_INSTALLER_URL}"
    mv -f "${uosa_tmp}" "${uosa_exe}"
  fi

  local extract_dir="${T2A_SRC_DIR}/uosa-install"
  mkdir -p "${extract_dir}"
  if ! "${extractor}" x -y "-o${extract_dir}" "${uosa_exe}" map0.mul statics0.mul staidx0.mul >/dev/null; then
    warn "Could not extract T2A map files; modern map kept."
    return
  fi

  local missing=0 f src
  local map0_src="" statics0_src="" staidx0_src=""
  for f in map0.mul statics0.mul staidx0.mul; do
    src="$(find "${extract_dir}" -maxdepth 4 -name "${f}" -print -quit 2>/dev/null || true)"
    if [[ -z "${src}" ]]; then
      warn "T2A ${f} not found in archive."
      missing=1
    else
      case "${f}" in
        map0.mul) map0_src="${src}" ;;
        statics0.mul) statics0_src="${src}" ;;
        staidx0.mul) staidx0_src="${src}" ;;
      esac
    fi
  done
  [[ "${missing}" == "0" ]] || { warn "Aborting T2A swap; modern map kept."; return; }

  mkdir -p "${backup_dir}"
  for f in map0.mul statics0.mul staidx0.mul radarcol.mul tiledata.mul; do
    [[ -f "${UO_DATA}/${f}" ]] && cp -f "${UO_DATA}/${f}" "${backup_dir}/${f}"
  done

  for f in map0.mul statics0.mul staidx0.mul; do
    case "${f}" in
      map0.mul) cp -f "${map0_src}" "${UO_DATA}/${f}" ;;
      statics0.mul) cp -f "${statics0_src}" "${UO_DATA}/${f}" ;;
      staidx0.mul) cp -f "${staidx0_src}" "${UO_DATA}/${f}" ;;
    esac
  done

  ok "T2A map art installed (intact Magincia)."
}

# ---------------------------------------------------------------------------
# Step 10 — Spawn map
# ---------------------------------------------------------------------------
fetch_spawn_map() {
  banner "Fetching Nerun's pre-T2A spawn map"

  mkdir -p "${SPAWNERS_DIR}"
  local target="${SPAWNERS_DIR}/UOClassic.map"

  if [[ -f "${target}" ]] && [[ -s "${target}" ]]; then
    say "Spawn map already present."
    return
  fi

  say "Downloading spawn map..."
  local tmp="${target}.part.$$"
  curl -fL --progress-bar -o "${tmp}" "${SPAWN_MAP_URL}"

  if head -1 "${tmp}" | grep -qi '<!doctype\|<html'; then
    rm -f "${tmp}"
    die "Downloaded file is invalid. Check ${SPAWN_MAP_URL}"
  fi

  mv -f "${tmp}" "${target}"

  ok "Spawn map ready ($(wc -l < "${target}" | tr -d ' ') lines)."
}

# ---------------------------------------------------------------------------
# Step 11 — ClassicUO client
# ---------------------------------------------------------------------------
install_classicuo() {
  banner "Downloading ClassicUO client (macOS)"

  if [[ -d "${CLASSICUO_DIR}" ]] \
     && [[ -n "$(ls -A "${CLASSICUO_DIR}" 2>/dev/null)" ]] \
     && [[ -f "${INSTALL_ROOT}/.classicuo-bin-path" ]]; then
    say "ClassicUO already installed."
    return
  fi

  command -v unzip >/dev/null 2>&1 || die "unzip is required to install ClassicUO."
  mkdir -p "${CLASSICUO_DIR}"
  local tmp_zip="${INSTALL_ROOT}/.classicuo.zip"

  say "Querying GitHub for latest macOS release..."
  local asset_url=""
  asset_url="$(curl -fsSL "${CLASSICUO_RELEASE_URL}/latest" 2>/dev/null \
      | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | grep -iE '(osx|mac)' | grep -iE '\.zip"' | head -n1 \
      | sed -E 's/.*"(https[^"]+)".*/\1/' || true)"

  if [[ -z "${asset_url}" ]]; then
    say "Checking dev-release tag for macOS asset..."
    asset_url="$(curl -fsSL "${CLASSICUO_RELEASE_URL}/tags/ClassicUO-dev-release" 2>/dev/null \
      | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
      | grep -iE '(osx|mac)' | grep -iE '\.zip"' | head -n1 \
      | sed -E 's/.*"(https[^"]+)".*/\1/' || true)"
  fi

  [[ -n "${asset_url}" ]] || die "Could not find a ClassicUO macOS release on GitHub."

  say "Downloading: ${asset_url}"
  local zip_tmp="${tmp_zip}.part.$$"
  curl -fL --progress-bar -o "${zip_tmp}" "${asset_url}"
  mv -f "${zip_tmp}" "${tmp_zip}"

  say "Extracting..."
  unzip -q -o "${tmp_zip}" -d "${CLASSICUO_DIR}"
  rm -f "${tmp_zip}"

  # Strip macOS quarantine attribute
  xattr -cr "${CLASSICUO_DIR}" 2>/dev/null || true

  local cuo_bin=""
  for name in ClassicUO.app ClassicUO ClassicUO.bin.osx ClassicUO.bin.x86_64 cuo; do
    if [[ -e "${CLASSICUO_DIR}/${name}" ]]; then
      cuo_bin="${CLASSICUO_DIR}/${name}"
      break
    fi
  done

  if [[ -z "${cuo_bin}" ]]; then
    cuo_bin="$(find "${CLASSICUO_DIR}" -maxdepth 3 \( -name "ClassicUO.app" -o -name "ClassicUO" \) -print -quit 2>/dev/null || true)"
  fi

  if [[ -n "${cuo_bin}" ]]; then
    [[ -f "${cuo_bin}" ]] && chmod +x "${cuo_bin}"
    echo "${cuo_bin}" > "${INSTALL_ROOT}/.classicuo-bin-path"
    ok "ClassicUO located at: ${cuo_bin}"
  else
    warn "ClassicUO extracted; binary path will be auto-detected at launch."
  fi
}

# ---------------------------------------------------------------------------
# Step 12 — Configurations
# ---------------------------------------------------------------------------
write_configs() {
  banner "Writing ModernUO and ClassicUO configurations"

  mkdir -p "${CFG_DIR}"
  local uo_data_json
  uo_data_json="$(json_escape "${UO_DATA}")"

  cat > "${CFG_DIR}/modernuo.json" <<EOF
{
  "assemblyDirectories": ["./Assemblies"],
  "dataDirectories": ["${uo_data_json}"],
  "listeners": ["${LISTEN_ADDR}"],
  "settings": {
    "accountHandler.maxAccountsPerIP": "10",
    "autosave.enabled": "true",
    "autosave.saveDelay": "00:05:00",
    "serverList.address": "127.0.0.1",
    "serverList.autoDetect": "false",
    "serverListing.name": "${SHARD_NAME}",
    "serverListing.serverName": "${SHARD_NAME}",
    "accountHandler.enableAutoAccountCreation": "True",
    "pathfinding.prebakeMaps": "True"
  }
}
EOF

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

  mkdir -p "${CFG_DIR}/FeatureFlags"
  cat > "${CFG_DIR}/FeatureFlags/flags.json" <<'EOF'
[
  {
    "Key": "young_player_system",
    "Description": "UO:R-era new player (Young) system. Off for T2A.",
    "Enabled": false,
    "DefaultEnabled": true,
    "Category": "Content",
    "LastModified": "2026-08-23T00:00:00Z",
    "LastModifiedBy": "T2A ruleset"
  }
]
EOF

  # ClassicUO settings
  local cfg_targets=("${CLASSICUO_DIR}")
  if [[ -d "${CLASSICUO_DIR}/ClassicUO.app/Contents/Resources" ]]; then
    cfg_targets+=("${CLASSICUO_DIR}/ClassicUO.app/Contents/Resources")
  fi

  for target in "${cfg_targets[@]}"; do
    [[ -f "${target}/settings.json" ]] && continue
    cat > "${target}/settings.json" <<EOF
{
  "username": "${OWNER_USER}",
  "password": "",
  "ip": "127.0.0.1",
  "port": 2593,
  "ultimaonlinedirectory": "${uo_data_json}",
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
  done

  ok "Configurations generated."
}

# ---------------------------------------------------------------------------
# Step 13 — Install runtime scripts & Desktop launcher
# ---------------------------------------------------------------------------
install_runtime_scripts() {
  banner "Installing macOS runtime scripts & launcher"

  local src_dir="${SCRIPT_DIR}/scripts"

  cp "${src_dir}/start-macos.sh"        "${INSTALL_ROOT}/start.sh"
  cp "${src_dir}/stop.sh"               "${INSTALL_ROOT}/stop.sh"
  cp "${src_dir}/reset-first-launch.sh" "${INSTALL_ROOT}/reset-first-launch.sh"

  if [[ -f "${src_dir}/update-check-macos.sh" ]]; then
    cp "${src_dir}/update-check-macos.sh" "${INSTALL_ROOT}/update-check.sh"
    chmod +x "${INSTALL_ROOT}/update-check.sh"
  fi

  chmod +x "${INSTALL_ROOT}/start.sh" \
           "${INSTALL_ROOT}/stop.sh" \
           "${INSTALL_ROOT}/reset-first-launch.sh"

  # Version stamp
  local sha="${UO_OFFLINE_SOURCE_SHA:-}"
  if command -v git >/dev/null 2>&1 && [[ -e "${SCRIPT_DIR}/.git" ]]; then
    sha="$(git -C "${SCRIPT_DIR}" rev-parse HEAD 2>/dev/null || true)"
  fi
  if [[ -z "${sha}" ]] && command -v curl >/dev/null 2>&1; then
    sha="$(curl -fsSL --max-time 10 \
      -H "User-Agent: uo-offline-installer" \
      "https://api.github.com/repos/Klein187/uo-offline/commits/main" 2>/dev/null \
      | grep -oE '"sha"[[:space:]]*:[[:space:]]*"[0-9a-f]{40}"' \
      | head -n1 | grep -oE '[0-9a-f]{40}' || true)"
  fi
  if [[ -n "${sha}" ]]; then
    cat > "${INSTALL_ROOT}/uo-offline-version.json" <<EOF
{
  "Repo": "Klein187/uo-offline",
  "Branch": "main",
  "Sha": "${sha}",
  "InstalledUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF
  else
    warn "Could not determine the source version; update checks are disabled."
  fi

  # Desktop launcher for macOS Finder (.command file)
  mkdir -p "${HOME}/Desktop"
  local install_root_q
  install_root_q="$(printf '%q' "${INSTALL_ROOT}")"
  cat > "${HOME}/Desktop/UO Offline.command" <<EOF
#!/bin/bash
cd ${install_root_q} && exec ./start.sh
EOF
  chmod +x "${HOME}/Desktop/UO Offline.command"

  if [[ ! -f "${INSTALL_ROOT}/.needs-owner-account" ]] \
     && [[ ! -f "${DIST_DIR}/Saves/Accounts/Accounts.bin" ]] \
     && [[ ! -f "${DIST_DIR}/Configuration/server-access.json" ]]; then
    touch "${INSTALL_ROOT}/.needs-owner-account"
  fi

  cat > "${INSTALL_ROOT}/POPULATE-WORLD.txt" <<'EOF'
After your first character is created and you're standing in Britannia,
type these commands in chat to populate the world:

  [Decorate
  [SignGen
  [TelGen
  [MoonGen
  [TownCriers
  [GenerateSpawners Spawners/uoclassic/UOClassic.map

Or open the GM panel:
  [GmPanel -> click ★ First Time Setup
EOF

  ok "Installed start.sh, stop.sh, and Desktop launcher."
}

# ---------------------------------------------------------------------------
# Finish
# ---------------------------------------------------------------------------
finish() {
  banner "Install complete"
  cat <<EOF

Install root:   ${INSTALL_ROOT}
Server:         ${DIST_DIR} (macOS arm64 native)
Client:         ${CLASSICUO_DIR}
UO Data:        ${UO_DATA}
Expansion:      ${EXPANSION_NAME}
Listener:       ${LISTEN_ADDR} (offline localhost)
Owner Account:  ${OWNER_USER} / ${OWNER_PASS}

To play:        Double-click "UO Offline.command" on your Desktop,
                or run: ${INSTALL_ROOT}/start.sh

First Launch:
  1. The server generates world caches and creates the admin account (~30s).
  2. ClassicUO opens automatically. Log in with admin / admin.
  3. Create your GM character and enter the world.
  4. Type [GmPanel in chat and click "★ First Time Setup" to populate the world.

EOF
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
main() {
  preflight
  install_deps
  fetch_modernuo
  bootstrap_dotnet
  apply_engine_patches
  install_playerbots
  install_map_editor
  build_modernuo
  fix_felucca_season
  find_or_download_uo_data
  swap_t2a_map
  fetch_spawn_map
  install_classicuo
  write_configs
  install_runtime_scripts
  finish
}

main "$@"
