#!/usr/bin/env bash
# =========================================================================
# reset-first-launch.sh — wipe partial first-launch state and re-arm.
#
# Use this when the first launch failed midway (e.g. the wizard captured
# the wrong stdin) and left an inconsistent state. It clears:
#   - World saves (Distribution/Saves/)
#   - Server runtime configs that the wizard writes (modernuo.json,
#     expansion.json, server-access.json)
#   - PID file and log
#
# It does NOT touch:
#   - The ModernUO build (Distribution/ModernUO.dll, Assemblies/, etc.)
#   - .NET SDK install
#   - ClassicUO install
#   - Your UO game data folder
#
# After running this, re-run install.sh to rewrite the configs, then
# start.sh to do a clean first launch.
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

echo "Wiping server runtime configs..."
rm -f "${DIST_DIR}/Configuration/modernuo.json"
rm -f "${DIST_DIR}/Configuration/expansion.json"
rm -f "${DIST_DIR}/Configuration/server-access.json"

echo "Clearing PID file and log..."
rm -f "${INSTALL_ROOT}/modernuo.pid"
rm -f "${INSTALL_ROOT}/modernuo.log"

echo "Re-arming first-launch marker..."
touch "${INSTALL_ROOT}/.needs-owner-account"

echo ""
echo "Done. Next steps:"
echo "  1. Re-run the installer to rewrite configs:"
echo "       cd ~/Downloads/uo-modernuo && ./install.sh"
echo "  2. Then start the server:"
echo "       ~/uo-modernuo/start.sh"
