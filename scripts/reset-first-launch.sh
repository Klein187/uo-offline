#!/usr/bin/env bash
# =========================================================================
# reset-first-launch.sh — wipe world saves and re-arm first-launch flow.
#
# Use this when you want to start over with a fresh world (e.g. testing,
# or first launch failed and left inconsistent state).
#
# Clears:
#   - World saves (Distribution/Saves/)
#   - server-access.json (wizard-written, gets regenerated)
#   - PID file and log
#
# Preserves:
#   - ModernUO build
#   - .NET SDK install
#   - ClassicUO install
#   - UO game data folder
#   - modernuo.json and expansion.json (we wrote these correctly; keep them)
#
# After running this, just run start.sh — it'll redo the owner-account
# wizard and world population.
# =========================================================================
set -uo pipefail

# install.sh copies this script INTO the install root, so our own
# directory is the install root - including a custom location.
INSTALL_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="${INSTALL_ROOT}/ModernUO/Distribution"

is_modernuo_pid() {
  local pid="$1"
  local command_line
  [[ "${pid}" =~ ^[0-9]+$ ]] || return 1
  kill -0 "${pid}" 2>/dev/null || return 1
  command_line="$(ps -p "${pid}" -o command= 2>/dev/null)"
  [[ "${command_line}" == *"ModernUO.dll"* ]]
}

[[ -d "${INSTALL_ROOT}" ]] || { echo "No install found at ${INSTALL_ROOT}"; exit 1; }

# Stop the server if it's running.
if [[ -f "${INSTALL_ROOT}/modernuo.pid" ]] \
   && is_modernuo_pid "$(cat "${INSTALL_ROOT}/modernuo.pid")"; then
  pid="$(cat "${INSTALL_ROOT}/modernuo.pid")"
  echo "Stopping running server..."
  kill -TERM "${pid}" 2>/dev/null || true
  sleep 5
  is_modernuo_pid "${pid}" && kill -9 "${pid}" 2>/dev/null || true
fi
if pgrep -f ModernUO.dll >/dev/null 2>&1; then
  echo "A ModernUO process is still running; refusing to wipe world saves." >&2
  exit 1
fi

echo "Wiping world saves..."
rm -rf "${DIST_DIR}/Saves"

echo "Removing wizard-written runtime config..."
rm -f "${DIST_DIR}/Configuration/server-access.json"

echo "Clearing PID file and log..."
rm -f "${INSTALL_ROOT}/modernuo.pid"
rm -f "${INSTALL_ROOT}/modernuo.log"

echo "Re-arming first-launch marker..."
touch "${INSTALL_ROOT}/.needs-owner-account"

echo ""
echo "Done. Next: run the server to redo first launch:"
echo "       ${INSTALL_ROOT}/start.sh"
echo ""
echo "After your character is created, re-run the [-commands listed in:"
echo "       ${INSTALL_ROOT}/POPULATE-WORLD.txt"
echo "to repopulate the world."
