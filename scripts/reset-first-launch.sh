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

INSTALL_ROOT="${HOME}/uo-modernuo"
DIST_DIR="${INSTALL_ROOT}/ModernUO/Distribution"

[[ -d "${INSTALL_ROOT}" ]] || { echo "No install found at ${INSTALL_ROOT}"; exit 1; }

# Stop the server if it's running.
if [[ -f "${INSTALL_ROOT}/modernuo.pid" ]] \
   && kill -0 "$(cat "${INSTALL_ROOT}/modernuo.pid")" 2>/dev/null; then
  echo "Stopping running server..."
  kill -TERM "$(cat "${INSTALL_ROOT}/modernuo.pid")" 2>/dev/null || true
  sleep 5
  kill -9 "$(cat "${INSTALL_ROOT}/modernuo.pid")" 2>/dev/null || true
fi
pkill -9 -f ModernUO.dll 2>/dev/null || true

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
echo "       ~/uo-modernuo/start.sh"
echo ""
echo "After your character is created, re-run the [-commands listed in:"
echo "       ~/uo-modernuo/POPULATE-WORLD.txt"
echo "to repopulate the world."
