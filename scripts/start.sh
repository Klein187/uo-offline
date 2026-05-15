#!/usr/bin/env bash
# =========================================================================
# start.sh — Launch UO Offline (ModernUO + ClassicUO).
#
# Behavior:
#   - First run: also creates the owner account by feeding scripted answers
#     to the server over stdin.
#   - Subsequent runs: just launches the server in the background, waits
#     for it to be listening, then launches ClassicUO.
#   - Exiting ClassicUO does NOT stop the server. Use stop.sh for that.
# =========================================================================
set -uo pipefail

INSTALL_ROOT="${HOME}/uo-modernuo"
DIST_DIR="${INSTALL_ROOT}/ModernUO/Distribution"
PIDFILE="${INSTALL_ROOT}/modernuo.pid"
LOGFILE="${INSTALL_ROOT}/modernuo.log"
MARKER="${INSTALL_ROOT}/.needs-owner-account"

OWNER_USER="admin"
OWNER_PASS="admin"
LISTEN_PORT=2593

# ClassicUO lives inside our install root, alongside the server.
CLASSICUO_DIR="${INSTALL_ROOT}/ClassicUO"

# .NET was installed per-user by install.sh into ~/.dotnet/. Make dotnet
# reachable here so we don't depend on the user's shell rc files having
# been re-sourced since install.
DOTNET_ROOT="${HOME}/.dotnet"
export DOTNET_ROOT
export PATH="${DOTNET_ROOT}:${PATH}"

say()  { printf '\033[0;36m--> %s\033[0m\n' "$*"; }
warn() { printf '\033[0;33m[WARN]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[0;31m[ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

[[ -f "${DIST_DIR}/ModernUO.dll" ]] || die "ModernUO not built. Run install.sh first."

# ---------------------------------------------------------------------------
# Already running?
#
# If the server is already up (user clicked the desktop icon twice, or
# something else launched it), we attach the client to it but DON'T shut
# it down when the client exits. The user opened a new client, not the
# whole session.
# ---------------------------------------------------------------------------
SERVER_WAS_ALREADY_RUNNING=0
if [[ -f "${PIDFILE}" ]] && kill -0 "$(cat "${PIDFILE}")" 2>/dev/null; then
  say "Server already running (pid $(cat "${PIDFILE}")). Launching client only."
  SERVER_WAS_ALREADY_RUNNING=1
else
  cd "${DIST_DIR}"

  if [[ -f "${MARKER}" ]]; then
    # ---------------------------------------------------------------------
    # First-launch wizard answers.
    #
    # On a fresh install, ModernUO walks an interactive wizard:
    #   1. "Please enter the name of your shard: [ModernUO]>"  → press Enter
    #      to accept the default. (Our modernuo.json's serverListing.name
    #      doesn't suppress this prompt; the wizard always runs once.)
    #   2. If expansion.json is missing, an expansion-selection prompt
    #      runs here. We pre-write expansion.json so this is skipped.
    #   3. "This server has no accounts."
    #      "Do you want to create the owner account now? (y/n):"  → y
    #   4. "Input Username:"  → admin
    #   5. "Input Password:"  → admin
    #
    # Previous versions of this script sent all answers after a fixed
    # sleep, which caused them to land on the wrong prompts (the leading
    # "y" got captured as the shard name). The robust fix: watch the log
    # file for each prompt's distinctive text and send the matching reply
    # only after we see it.
    # ---------------------------------------------------------------------
    say "First launch: running ModernUO setup wizard and creating owner account."
    say "This takes 30-60 seconds while the world saves are generated."

    # FIFO keeps stdin open across multiple `printf` writes.
    FIFO="$(mktemp -u "${INSTALL_ROOT}/.stdin.XXXXXX")"
    mkfifo "${FIFO}"
    exec 9<>"${FIFO}"
    rm -f "${FIFO}"

    # Truncate log so we don't match prompts from a previous failed run.
    : > "${LOGFILE}"

    nohup dotnet ModernUO.dll <&9 >"${LOGFILE}" 2>&1 &
    SERVER_PID=$!
    echo "${SERVER_PID}" > "${PIDFILE}"

    # ---------------------------------------------------------------------
    # wait_for_log_line <pattern> <timeout-seconds>
    # Returns 0 when the pattern appears in the log, 1 on timeout or if
    # the server process died.
    # ---------------------------------------------------------------------
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

    # Step 1: shard-name prompt → accept default.
    if wait_for_log_line "name of your shard" 30; then
      say "Shard-name prompt detected → accepting default name."
      printf '\n' >&9
    fi

    # Step 3: account-creation prompt → answer "y".
    if wait_for_log_line "create the owner account" 30; then
      say "Account-creation prompt detected → answering y."
      printf 'y\n' >&9
    fi

    # Step 4: username prompt.
    if wait_for_log_line "Input Username" 15; then
      say "Username prompt detected → ${OWNER_USER}."
      printf '%s\n' "${OWNER_USER}" >&9
    fi

    # Step 5: password prompt.
    if wait_for_log_line "Input Password" 15; then
      say "Password prompt detected → (hidden)."
      printf '%s\n' "${OWNER_PASS}" >&9
    fi

    # Wait for account creation confirmation before clearing the marker.
    if wait_for_log_line "Owner account created" 15; then
      say "Owner account created."
      rm -f "${MARKER}"
    else
      warn "Did not see 'Owner account created' confirmation in log."
      warn "Leaving the first-launch marker in place; check ${LOGFILE} to see what went wrong."
    fi
  else
    say "Starting ModernUO server..."
    : > "${LOGFILE}"
    nohup dotnet ModernUO.dll </dev/null >"${LOGFILE}" 2>&1 &
    SERVER_PID=$!
    echo "${SERVER_PID}" > "${PIDFILE}"
  fi

  # Wait for the listener to come up. Up to 60 seconds — first launch with
  # world generation is slower than subsequent ones.
  say "Waiting for server to listen on port ${LISTEN_PORT}..."
  for i in $(seq 1 60); do
    if ss -tln 2>/dev/null | grep -q ":${LISTEN_PORT} "; then
      say "Server is up (took ${i}s)."
      break
    fi
    if ! kill -0 "${SERVER_PID}" 2>/dev/null; then
      die "Server died during startup. See ${LOGFILE}"
    fi
    sleep 1
  done

  if ! ss -tln 2>/dev/null | grep -q ":${LISTEN_PORT} "; then
    warn "Server didn't start listening within 60s. Check ${LOGFILE}"
    warn "Leaving it running; it may still come up."
  fi
fi

# ---------------------------------------------------------------------------
# Sync client version into ClassicUO settings.json.
#
# Different UO data folders are different versions (7.0.50, 7.0.103, 7.0.115,
# etc.). ModernUO auto-detects the version from the data files and logs it.
# If our settings.json's clientversion doesn't match, ClassicUO either fails
# to parse the data files (FormatException at AnimationsLoader.Load) or
# gets kicked by the server's version-restriction check.
#
# We read the version ModernUO detected and patch settings.json to match.
# ---------------------------------------------------------------------------
sync_client_version() {
  local settings_file="${CLASSICUO_DIR}/settings.json"
  [[ -f "${settings_file}" ]] || return 0
  [[ -f "${LOGFILE}" ]] || return 0

  local detected
  detected="$(grep -oE 'Automatically detected client version [0-9]+\.[0-9]+\.[0-9]+\.[0-9]+' "${LOGFILE}" \
    | tail -n1 | awk '{print $NF}')"

  if [[ -z "${detected}" ]]; then
    return 0
  fi

  local current
  current="$(grep -oE '"clientversion"[[:space:]]*:[[:space:]]*"[^"]*"' "${settings_file}" \
    | sed -E 's/.*"([^"]*)"[[:space:]]*$/\1/')"

  if [[ "${current}" == "${detected}" ]]; then
    return 0
  fi

  say "Updating ClassicUO clientversion: ${current} → ${detected}"
  sed -i -E "s/(\"clientversion\"[[:space:]]*:[[:space:]]*\")[^\"]*(\")/\1${detected}\2/" "${settings_file}"
}
sync_client_version

# ---------------------------------------------------------------------------
# Launch ClassicUO and wait for it.
#
# When the player closes the client, this script triggers a clean server
# shutdown so the world saves and nothing has to be done in a terminal.
#
# Override with KEEP_SERVER_RUNNING=1 ./start.sh if you want the server to
# stay up after the client exits (e.g. you're going to relaunch the client,
# or you connect from a second machine on your LAN).
# ---------------------------------------------------------------------------
CLASSICUO_BIN=""
if [[ -f "${INSTALL_ROOT}/.classicuo-bin-path" ]]; then
  CLASSICUO_BIN="$(cat "${INSTALL_ROOT}/.classicuo-bin-path")"
fi

if [[ -z "${CLASSICUO_BIN}" ]] || [[ ! -x "${CLASSICUO_BIN}" ]]; then
  for name in ClassicUO ClassicUO.bin.x86_64 cuo; do
    if [[ -x "${CLASSICUO_DIR}/${name}" ]]; then
      CLASSICUO_BIN="${CLASSICUO_DIR}/${name}"
      break
    fi
  done
fi

if [[ -z "${CLASSICUO_BIN}" ]] || [[ ! -x "${CLASSICUO_BIN}" ]]; then
  warn "ClassicUO binary not found under ${CLASSICUO_DIR}."
  warn "Server is running on 127.0.0.1:${LISTEN_PORT}. Launch your client manually."
  warn "Run ${INSTALL_ROOT}/stop.sh when you're done to save and shut down the server."
  exit 0
fi

# ---------------------------------------------------------------------------
# shutdown_server: SIGTERM the server, wait for clean save, fall back to kill.
# Mirrors stop.sh so behavior is identical regardless of which path closes
# the server.
# ---------------------------------------------------------------------------
shutdown_server() {
  if [[ ! -f "${PIDFILE}" ]]; then
    return
  fi
  local pid
  pid="$(cat "${PIDFILE}")"
  if ! kill -0 "${pid}" 2>/dev/null; then
    rm -f "${PIDFILE}"
    return
  fi

  say "Client closed. Saving world and shutting down server (pid ${pid})..."
  kill -TERM "${pid}"

  # ModernUO saves on SIGTERM. Populated worlds take 10-20s; allow 30.
  for _ in $(seq 1 30); do
    if ! kill -0 "${pid}" 2>/dev/null; then
      say "Server stopped cleanly."
      rm -f "${PIDFILE}"
      return
    fi
    sleep 1
  done

  warn "Server didn't stop within 30s. Forcing kill — world state since last autosave may be lost."
  kill -9 "${pid}" 2>/dev/null || true
  rm -f "${PIDFILE}"
}

# Run shutdown on script exit (including Ctrl-C) unless:
#   - the user opted out with KEEP_SERVER_RUNNING=1, or
#   - the server was already running before we got here (someone else owns it).
if [[ "${KEEP_SERVER_RUNNING:-0}" != "1" ]] && [[ "${SERVER_WAS_ALREADY_RUNNING}" == "0" ]]; then
  trap shutdown_server EXIT INT TERM
fi

say "Launching ClassicUO: ${CLASSICUO_BIN}"
cd "$(dirname "${CLASSICUO_BIN}")"

# Run in the foreground and wait. When the client window closes, the
# process exits and the EXIT trap above shuts down the server.
"./$(basename "${CLASSICUO_BIN}")"

# Explicit exit so the trap fires cleanly with a known status.
exit 0
