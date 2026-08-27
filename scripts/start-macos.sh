#!/usr/bin/env bash
# =========================================================================
# start-macos.sh — Launch UO Offline (ModernUO + ClassicUO) on macOS.
#
# Behavior:
#   - First run: also creates the owner account by feeding scripted answers
#     to the server over stdin.
#   - Subsequent runs: launches the server in the background, waits
#     for it to listen on 127.0.0.1:2593, then launches ClassicUO.
#   - Exiting ClassicUO cleanly triggers server shutdown and world save.
# =========================================================================
set -uo pipefail

INSTALL_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="${INSTALL_ROOT}/ModernUO/Distribution"
PIDFILE="${INSTALL_ROOT}/modernuo.pid"
LOGFILE="${INSTALL_ROOT}/modernuo.log"
MARKER="${INSTALL_ROOT}/.needs-owner-account"

OWNER_USER="admin"
OWNER_PASS="admin"
LISTEN_PORT=2593

CLASSICUO_DIR="${INSTALL_ROOT}/ClassicUO"

DOTNET_ROOT="${HOME}/.dotnet"
export DOTNET_ROOT
export PATH="${DOTNET_ROOT}:${PATH}"

say()  { printf '\033[0;36m--> %s\033[0m\n' "$*"; }
warn() { printf '\033[0;33m[WARN]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[0;31m[ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

is_modernuo_pid() {
  local pid="$1"
  local command_line
  [[ "${pid}" =~ ^[0-9]+$ ]] || return 1
  kill -0 "${pid}" 2>/dev/null || return 1
  command_line="$(ps -p "${pid}" -o command= 2>/dev/null)"
  [[ "${command_line}" == *"ModernUO.dll"* ]]
}

check_port_listening() {
  local pid="${1:-}"
  if [[ -n "${pid}" ]] && command -v lsof >/dev/null 2>&1; then
    lsof -nP -a -p "${pid}" -iTCP:"${LISTEN_PORT}" -sTCP:LISTEN >/dev/null 2>&1
    return $?
  fi
  command -v nc >/dev/null 2>&1 || return 1
  nc -z 127.0.0.1 "${LISTEN_PORT}" >/dev/null 2>&1
}

[[ -f "${DIST_DIR}/ModernUO.dll" ]] || die "ModernUO not built. Run install-osx-silicon.sh first."

# ---------------------------------------------------------------------------
# Update check
# ---------------------------------------------------------------------------
UPDATER="${INSTALL_ROOT}/update-check.sh"
if [[ -x "${UPDATER}" ]]; then
  "${UPDATER}"
  UPDATE_STATUS=$?
  if [[ "${UPDATE_STATUS}" -eq 10 ]]; then
    exit 0
  fi
  [[ "${UPDATE_STATUS}" -eq 0 ]] || die "UO Offline update failed. Check the installer output and retry."
fi

# ---------------------------------------------------------------------------
# Server start / already running check
# ---------------------------------------------------------------------------
SERVER_WAS_ALREADY_RUNNING=0
SERVER_PID=""
if [[ -f "${PIDFILE}" ]] && is_modernuo_pid "$(cat "${PIDFILE}")"; then
  SERVER_PID="$(cat "${PIDFILE}")"
  say "Server already running (pid ${SERVER_PID}). Launching client only."
  SERVER_WAS_ALREADY_RUNNING=1
else
  cd "${DIST_DIR}" || die "Cannot enter ${DIST_DIR}."

  # A first launch that got far enough to create the account but not far
  # enough to clear the marker would otherwise re-run the wizard forever,
  # warning its way through prompts that never come again.
  if [[ -f "${MARKER}" ]] && [[ -f "${DIST_DIR}/Saves/Accounts/Accounts.bin" ]]; then
    say "Owner account already exists; skipping the first-launch wizard."
    rm -f "${MARKER}"
  fi

  if [[ -f "${MARKER}" ]]; then
    say "First launch: running ModernUO setup wizard and creating owner account."
    say "This takes 30-60 seconds while world saves and distance fields are generated."

    : > "${LOGFILE}"

    command -v expect >/dev/null 2>&1 || die "The macOS 'expect' utility is required for the first-launch wizard."
    rm -f "${PIDFILE}"
    nohup expect -f /dev/stdin "${PIDFILE}" "${OWNER_USER}" "${OWNER_PASS}" >"${LOGFILE}" 2>&1 <<'EXPECT' &
set timeout -1
set pidfile [lindex $argv 0]
set owner_user [lindex $argv 1]
set owner_pass [lindex $argv 2]
log_user 1

# A wide pty keeps the wizard's prompts on one line, so the log greps below
# still match them.
set stty_init "rows 60 columns 200"

# spawn returns the pid. exp_pid is a command, not a variable, so reading
# it as $exp_pid is an error on the expect macOS ships (5.45).
set server_pid [spawn dotnet ModernUO.dll]
set pid_handle [open $pidfile w]
puts $pid_handle $server_pid
close $pid_handle

expect {
  -re {name of your shard} { send "\r"; exp_continue }
  -re {create the owner account} { send "y\r"; exp_continue }
  -re {Input Username} { send "$owner_user\r"; exp_continue }
  -re {Input Password} { send "$owner_pass\r"; exp_continue }
  eof
}
EXPECT
    EXPECT_PID=$!

    SERVER_PID=""
    for _ in $(seq 1 10); do
      if [[ -f "${PIDFILE}" ]]; then
        SERVER_PID="$(cat "${PIDFILE}")"
        is_modernuo_pid "${SERVER_PID}" && break
      fi
      kill -0 "${EXPECT_PID}" 2>/dev/null || break
      sleep 1
    done
    is_modernuo_pid "${SERVER_PID}" || die "ModernUO failed to start. See ${LOGFILE}"

    wait_for_log_line() {
      local pattern="$1"
      local timeout="${2:-30}"
      local elapsed=0
      while [[ ${elapsed} -lt ${timeout} ]]; do
        if grep -qE "${pattern}" "${LOGFILE}" 2>/dev/null; then
          return 0
        fi
        if ! kill -0 "${SERVER_PID}" 2>/dev/null; then
          warn "Server process died during wizard. See ${LOGFILE}"
          return 1
        fi
        sleep 1
        elapsed=$((elapsed + 1))
      done
      warn "Timed out (${timeout}s) waiting for log pattern: ${pattern}"
      return 1
    }

    # Step 1: shard-name prompt → accept default
    if wait_for_log_line "name of your shard" 35; then
      say "Shard-name prompt detected → accepting default name."
    fi

    # Step 2: account-creation prompt → answer "y"
    if wait_for_log_line "create the owner account" 35; then
      say "Account-creation prompt detected → answering y."
    fi

    # Step 3: username prompt
    if wait_for_log_line "Input Username" 20; then
      say "Username prompt detected → ${OWNER_USER}."
    fi

    # Step 4: password prompt
    if wait_for_log_line "Input Password" 20; then
      say "Password prompt detected → (hidden)."
    fi

    # Wait for account creation confirmation
    if wait_for_log_line "Owner account created" 20; then
      say "Owner account created."
      rm -f "${MARKER}"
    else
      warn "Did not see 'Owner account created' confirmation in log."
      warn "Leaving the first-launch marker in place; check ${LOGFILE}"
    fi
  else
    say "Starting ModernUO server..."
    : > "${LOGFILE}"
    nohup dotnet ModernUO.dll </dev/null >"${LOGFILE}" 2>&1 &
    SERVER_PID=$!
    echo "${SERVER_PID}" > "${PIDFILE}" || die "Cannot write ${PIDFILE}."
  fi

fi

# The first start bakes the pathfinding cache for every map before it opens
# the listener, which takes a couple of minutes on an M-series machine and
# only happens once. Later starts read that cache and are up in seconds. Wait
# long enough for the slow case, or the client opens onto a dead port.
LISTEN_TIMEOUT=600
say "Waiting for server to listen on port ${LISTEN_PORT}..."
announced_prebake=0
for i in $(seq 1 "${LISTEN_TIMEOUT}"); do
  if check_port_listening "${SERVER_PID}"; then
    say "Server is up (took ${i}s)."
    break
  fi
  if ! is_modernuo_pid "${SERVER_PID}"; then
    die "Server died during startup. See ${LOGFILE}"
  fi
  if [[ "${announced_prebake}" == "0" ]] \
     && grep -q "pre-baking map" "${LOGFILE}" 2>/dev/null; then
    announced_prebake=1
    say "First run: pre-baking the pathfinding cache. This takes a few minutes, once."
  fi
  if [[ $((i % 30)) -eq 0 ]]; then
    say "Still waiting (${i}s)..."
  fi
  sleep 1
done

if ! check_port_listening "${SERVER_PID}"; then
  warn "Server didn't report listening within ${LISTEN_TIMEOUT}s. Check ${LOGFILE}"
  warn "Leaving it running; it may still come up."
fi

# ---------------------------------------------------------------------------
# Sync client version into ClassicUO settings.json
# ---------------------------------------------------------------------------
sync_client_version() {
  [[ -f "${LOGFILE}" ]] || return 0

  local detected
  detected="$(grep -oE 'Automatically detected client version [0-9]+\.[0-9]+\.[0-9]+\.[0-9]+' "${LOGFILE}" \
    | tail -n1 | awk '{print $NF}')"

  if [[ -z "${detected}" ]]; then
    return 0
  fi

  local settings_file current
  local settings_files=("${CLASSICUO_DIR}/settings.json")
  if [[ -f "${CLASSICUO_DIR}/ClassicUO.app/Contents/Resources/settings.json" ]]; then
    settings_files+=("${CLASSICUO_DIR}/ClassicUO.app/Contents/Resources/settings.json")
  fi

  for settings_file in "${settings_files[@]}"; do
    [[ -f "${settings_file}" ]] || continue
    current="$(grep -oE '"clientversion"[[:space:]]*:[[:space:]]*"[^"]*"' "${settings_file}" \
      | sed -E 's/.*"([^"]*)"[[:space:]]*$/\1/')"

    if [[ "${current}" != "${detected}" ]]; then
      say "Updating ClassicUO clientversion: ${current} → ${detected}"
      sed -i '' -E "s/(\"clientversion\"[[:space:]]*:[[:space:]]*\")[^\"]*(\")/\1${detected}\2/" "${settings_file}"
    fi
  done
}
sync_client_version

# ---------------------------------------------------------------------------
# Launch ClassicUO
# ---------------------------------------------------------------------------
CLASSICUO_BIN=""
if [[ -f "${INSTALL_ROOT}/.classicuo-bin-path" ]]; then
  CLASSICUO_BIN="$(cat "${INSTALL_ROOT}/.classicuo-bin-path")"
fi

if [[ -z "${CLASSICUO_BIN}" ]] || [[ ! -e "${CLASSICUO_BIN}" ]] || \
   ([[ ! -d "${CLASSICUO_BIN}" ]] && [[ ! -x "${CLASSICUO_BIN}" ]]); then
  for name in ClassicUO.app ClassicUO ClassicUO.bin.osx ClassicUO.bin.x86_64 cuo; do
    if [[ -e "${CLASSICUO_DIR}/${name}" ]]; then
      CLASSICUO_BIN="${CLASSICUO_DIR}/${name}"
      break
    fi
  done
fi

if [[ -z "${CLASSICUO_BIN}" ]] || [[ ! -e "${CLASSICUO_BIN}" ]] || \
   ([[ ! -d "${CLASSICUO_BIN}" ]] && [[ ! -x "${CLASSICUO_BIN}" ]]); then
  warn "ClassicUO not found under ${CLASSICUO_DIR}."
  warn "Server is running on 127.0.0.1:${LISTEN_PORT}. Launch your client manually."
  warn "Run ${INSTALL_ROOT}/stop.sh when you're done to save and shut down the server."
  exit 0
fi

# ---------------------------------------------------------------------------
# Shutdown handler
# ---------------------------------------------------------------------------
shutdown_server() {
  if [[ ! -f "${PIDFILE}" ]]; then
    return
  fi
  local pid
  pid="$(cat "${PIDFILE}")"
  if ! is_modernuo_pid "${pid}"; then
    rm -f "${PIDFILE}"
    return
  fi

  say "Client closed. Saving world and shutting down server (pid ${pid})..."
  kill -TERM "${pid}"

  for _ in $(seq 1 30); do
    if ! kill -0 "${pid}" 2>/dev/null; then
      say "Server stopped cleanly."
      rm -f "${PIDFILE}"
      return
    fi
    sleep 1
  done

  warn "Server didn't stop within 30s. Forcing kill — world state since last autosave may be lost."
  is_modernuo_pid "${pid}" && kill -9 "${pid}" 2>/dev/null || true
  rm -f "${PIDFILE}"
}

if [[ "${KEEP_SERVER_RUNNING:-0}" != "1" ]] && [[ "${SERVER_WAS_ALREADY_RUNNING}" == "0" ]]; then
  trap shutdown_server EXIT INT TERM
fi

# ---------------------------------------------------------------------------
# Execute client and wait
# ---------------------------------------------------------------------------
say "Launching ClassicUO..."
cd "${CLASSICUO_DIR}" || die "Cannot enter ${CLASSICUO_DIR}."

if [[ "${CLASSICUO_BIN}" == *.app ]]; then
  # Launch app and wait for process
  open -W -a "${CLASSICUO_BIN}"
elif [[ -x "${CLASSICUO_BIN}" ]]; then
  "${CLASSICUO_BIN}"
else
  warn "ClassicUO binary at ${CLASSICUO_BIN} is not executable."
fi
