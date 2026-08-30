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

# install.sh copies this script INTO the install root, so our own
# directory is the install root - including when the player chose a
# custom location. Hardcoding ~/uo-modernuo broke every such install.
INSTALL_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
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

LAUNCHLOG="${INSTALL_ROOT}/launch.log"
: > "${LAUNCHLOG}" 2>/dev/null || true

log_line() { printf '%s %s\n' "$(date '+%H:%M:%S')" "$*" >> "${LAUNCHLOG}" 2>/dev/null || true; }

# A launch failure the player can actually read. The desktop icon runs with
# Terminal=false, so without this a failed start is indistinguishable from
# the icon doing nothing at all.
gui_error() {
  local msg="$1"
  local full="${msg}

Full details: ${LAUNCHLOG}"
  if [[ "$(uname -s)" == "Darwin" ]]; then
    if command -v osascript >/dev/null 2>&1; then
      local escaped_full="${full//\"/\\\"}"
      osascript -e "display dialog \"${escaped_full}\" with title \"UO Offline\" buttons {\"OK\"} default button \"OK\" with icon stop" >/dev/null 2>&1 &
    fi
  elif [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]]; then
    if command -v zenity >/dev/null 2>&1; then
      zenity --error --title="UO Offline" --no-wrap --text="${full}" >/dev/null 2>&1 &
    elif command -v kdialog >/dev/null 2>&1; then
      kdialog --title "UO Offline" --error "${full}" >/dev/null 2>&1 &
    fi
  fi
}

is_port_listening() {
  local port="$1"
  if command -v ss >/dev/null 2>&1; then
    ss -tln 2>/dev/null | grep -q ":${port} "
  elif command -v lsof >/dev/null 2>&1; then
    lsof -iTCP:"${port}" -sTCP:LISTEN -n -P >/dev/null 2>&1
  elif command -v nc >/dev/null 2>&1; then
    nc -z 127.0.0.1 "${port}" 2>/dev/null
  else
    netstat -an 2>/dev/null | grep -i listen | grep -q "[.:]${port}[[:space:]]"
  fi
}

say()  { printf '\033[0;36m--> %s\033[0m\n' "$*"; log_line "--> $*"; }
warn() { printf '\033[0;33m[WARN]\033[0m %s\n' "$*" >&2; log_line "[WARN] $*"; }
die()  { printf '\033[0;31m[ERROR]\033[0m %s\n' "$*" >&2; log_line "[ERROR] $*"; gui_error "$*"; exit 1; }

[[ -f "${DIST_DIR}/ModernUO.dll" ]] || die "ModernUO not built. Run install.sh first."

# ---------------------------------------------------------------------------
# Ask GitHub whether there is a newer UO Offline before starting anything.
#
# The checker stays silent unless there is genuinely something new, and any
# failure at all - no internet, GitHub down, rate limited - falls straight
# through to launching the game. Exit code 10 means the player chose to
# update and the installer is now running, so we get out of the way.
# ---------------------------------------------------------------------------
UPDATER="${INSTALL_ROOT}/update-check.sh"
if [[ -x "${UPDATER}" ]]; then
  "${UPDATER}"
  if [[ $? -eq 10 ]]; then
    exit 0
  fi
fi

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
    # "y" got captured as the shard name). We watch the log for each
    # prompt's text and reply only after we see it.
    #
    # But the answers have to arrive down a TERMINAL, not a pipe.
    # ModernUO sets Core.Headless from Console.IsInputRedirected, and
    # ConsoleInputHandler.ReadLine THROWS when headless -- the throw is
    # not caught, so the server kills itself the moment it asks the
    # question. Piping the answers in is the one thing that guarantees
    # they can never be read. That is why this runs under a pseudo-terminal:
    # stdin is a tty, the prompts wait like they would for a person, and
    # we still get the output in the log.
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

    if [[ "$(uname -s)" == "Darwin" ]] || ! command -v script >/dev/null 2>&1 || ! script -V 2>&1 | grep -q "util-linux"; then
      if command -v python3 >/dev/null 2>&1; then
        nohup python3 -c '
import pty, os, sys, select
master, slave = pty.openpty()
pid = os.fork()
if pid == 0:
    os.close(master)
    os.setsid()
    os.dup2(slave, 0)
    os.dup2(slave, 1)
    os.dup2(slave, 2)
    if slave > 2:
        os.close(slave)
    os.execvp(sys.argv[1], sys.argv[1:])
else:
    os.close(slave)
    while True:
        try:
            r, _, _ = select.select([sys.stdin.fileno(), master], [], [])
        except (ValueError, OSError):
            break
        if sys.stdin.fileno() in r:
            try:
                data = os.read(sys.stdin.fileno(), 1024)
                if data:
                    os.write(master, data)
            except OSError:
                pass
        if master in r:
            try:
                data = os.read(master, 1024)
                if not data:
                    break
                sys.stdout.buffer.write(data)
                sys.stdout.buffer.flush()
            except OSError:
                break
    os.close(master)
    _, status = os.waitpid(pid, 0)
    sys.exit(os.WEXITSTATUS(status) if os.WIFEXITED(status) else 0)
' dotnet ModernUO.dll <&9 >"${LOGFILE}" 2>&1 &
      else
        warn "python3 not found - running dotnet directly; setup wizard may need terminal interaction."
        nohup dotnet ModernUO.dll >"${LOGFILE}" 2>&1 &
      fi
    else
      # util-linux script on Linux
      nohup script -qefc "dotnet ModernUO.dll" /dev/null <&9 >"${LOGFILE}" 2>&1 &
    fi
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

    # Step 1: shard-name prompt → accept default (if asked).
    if wait_for_log_line "name of your shard" 5; then
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
      warn ""
      warn "Create it by hand instead - this takes a minute and only happens once:"
      warn ""
      warn "    cd ${DIST_DIR}"
      warn "    ${DOTNET_ROOT}/dotnet ModernUO.dll"
      warn ""
      warn "(The full path matters: .NET is installed privately under"
      warn "${DOTNET_ROOT} and is not on your PATH, so a bare 'dotnet' will"
      warn "say command not found. Do NOT apt install dotnet - you have it.)"
      warn ""
      warn "Answer 'y' when it asks about the owner account, then give it a"
      warn "username and password (admin / admin is fine on your own machine)."
      warn "Wait for 'Listening: 127.0.0.1:2593', then press Ctrl+C to stop it."
      warn "After that:"
      warn ""
      warn "    rm -f ${MARKER}"
      warn ""
      warn "and start the game normally. Full log: ${LOGFILE}"
    fi
  else
    say "Starting ModernUO server..."
    : > "${LOGFILE}"
    nohup dotnet ModernUO.dll </dev/null >"${LOGFILE}" 2>&1 &
    SERVER_PID=$!
    echo "${SERVER_PID}" > "${PIDFILE}"
  fi

  # Wait for the listener to come up. Up to 180 seconds — first launch with
  # world generation and path baking is slower than subsequent ones.
  say "Waiting for server to listen on port ${LISTEN_PORT}..."
  for i in $(seq 1 180); do
    if is_port_listening "${LISTEN_PORT}"; then
      say "Server is up (took ${i}s)."
      break
    fi
    if ! kill -0 "${SERVER_PID}" 2>/dev/null; then
      die "Server died during startup. See ${LOGFILE}"
    fi
    sleep 1
  done

  if ! is_port_listening "${LISTEN_PORT}"; then
    warn "Server didn't start listening within 180s. Check ${LOGFILE}"
    warn "Leaving it running; it may still come up."
    log_line "--- last 40 lines of ${LOGFILE} ---"
    tail -n 40 "${LOGFILE}" >> "${LAUNCHLOG}" 2>/dev/null || true
    gui_error "The server did not finish starting within 180 seconds.

The game will still try to open. If it cannot connect, the reason is in:
${LOGFILE}"
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
  if command -v python3 >/dev/null 2>&1; then
    python3 -c '
import sys, re
path, ver = sys.argv[1], sys.argv[2]
with open(path, "r", encoding="utf-8") as f:
    c = f.read()
c = re.sub(r"(\"clientversion\"\s*:\s*\")[^\"]*(\")", r"\g<1>" + ver + r"\2", c)
with open(path, "w", encoding="utf-8") as f:
    f.write(c)
' "${settings_file}" "${detected}"
  else
    sed -i.bak -E "s/(\"clientversion\"[[:space:]]*:[[:space:]]*\")[^\"]*(\")/\1${detected}\2/" "${settings_file}" 2>/dev/null \
      || sed -i -E "s/(\"clientversion\"[[:space:]]*:[[:space:]]*\")[^\"]*(\")/\1${detected}\2/" "${settings_file}"
    rm -f "${settings_file}.bak"
  fi
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
  for name in ClassicUO ClassicUO.bin.osx ClassicUO.bin.x86_64 cuo; do
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
  gui_error "The game client (ClassicUO) is missing.

Looked in: ${CLASSICUO_DIR}

The server itself started and is running on 127.0.0.1:${LISTEN_PORT}.
Re-running install.sh will fetch the client again."
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
#
# Its output is teed into launch.log: a client that dies on startup (missing
# system library, unreadable UO data) otherwise leaves nothing behind at all
# when the desktop icon launched it.
CLIENT_START="$(date +%s)"
"./$(basename "${CLASSICUO_BIN}")" 2>&1 | tee -a "${LAUNCHLOG}"
CLIENT_RC="${PIPESTATUS[0]}"
CLIENT_RAN="$(( $(date +%s) - CLIENT_START ))"

if [[ "${CLIENT_RC}" -ne 0 ]]; then
  warn "ClassicUO exited with code ${CLIENT_RC} after ${CLIENT_RAN}s."
  gui_error "The game client closed straight away (exit code ${CLIENT_RC}).

This is usually a missing system library or a UO data folder the client
cannot read. The client's own error output is at the end of:
${LAUNCHLOG}"
elif [[ "${CLIENT_RAN}" -lt 5 ]]; then
  warn "ClassicUO exited cleanly after only ${CLIENT_RAN}s."
  gui_error "The game client opened and closed again after ${CLIENT_RAN} seconds.

Its output is at the end of:
${LAUNCHLOG}"
fi

# Explicit exit so the trap fires cleanly with a known status.
exit 0
