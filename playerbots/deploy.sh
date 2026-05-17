#!/usr/bin/env bash
# =========================================================================
# deploy.sh — Drop bot code + chat data into a working ModernUO install.
#
#   source/CustomBots/   →  ~/uo-modernuo/ModernUO/Projects/UOContent/CustomBots/
#   data/PlayerBotChat/  →  ~/uo-modernuo/ModernUO/Distribution/Data/PlayerBotChat/
#
# After deploy:
#   cd ~/uo-modernuo/ModernUO && ./publish.sh release linux x64
#   ~/uo-modernuo/stop.sh && ~/uo-modernuo/start.sh
# =========================================================================
set -euo pipefail

INSTALL_ROOT="${HOME}/uo-modernuo"
MODERNUO_DIR="${INSTALL_ROOT}/ModernUO"
SRC_TARGET="${MODERNUO_DIR}/Projects/UOContent/CustomBots"
DATA_TARGET="${MODERNUO_DIR}/Distribution/Data/PlayerBotChat"
WP_TARGET="${MODERNUO_DIR}/Distribution/Data/Waypoints"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

[[ -d "${MODERNUO_DIR}" ]] || { echo "ModernUO not found at ${MODERNUO_DIR}"; exit 1; }
[[ -d "${HERE}/source/CustomBots" ]] || { echo "Missing source/CustomBots in this zip."; exit 1; }
[[ -d "${HERE}/data/PlayerBotChat" ]] || { echo "Missing data/PlayerBotChat in this zip."; exit 1; }

echo "Deploying source/CustomBots -> ${SRC_TARGET}"
mkdir -p "${SRC_TARGET}"
cp -rT "${HERE}/source/CustomBots" "${SRC_TARGET}"

echo "Deploying data/PlayerBotChat -> ${DATA_TARGET}"
mkdir -p "${DATA_TARGET}"
cp -rT "${HERE}/data/PlayerBotChat" "${DATA_TARGET}"

# Waypoints (graph for Traveler bots).
if [[ -d "${HERE}/data/Waypoints" ]]; then
    echo "Deploying data/Waypoints -> ${WP_TARGET}"
    mkdir -p "${WP_TARGET}"
    cp -rT "${HERE}/data/Waypoints" "${WP_TARGET}"
fi

# Clean up obsolete data dirs / source files from earlier versions.
OLD_ROUTES="${MODERNUO_DIR}/Distribution/Data/Routes"
OLD_DESTS="${MODERNUO_DIR}/Distribution/Data/Destinations"
[[ -d "${OLD_ROUTES}" ]] && { echo "Removing obsolete Data/Routes/"; rm -rf "${OLD_ROUTES}"; }
[[ -d "${OLD_DESTS}" ]] && { echo "Removing obsolete Data/Destinations/"; rm -rf "${OLD_DESTS}"; }

rm -f "${SRC_TARGET}/Behaviors/RouteRegistry.cs"
rm -f "${SRC_TARGET}/Behaviors/ReloadRoutesCommand.cs"
rm -f "${SRC_TARGET}/Behaviors/DestinationRegistry.cs"
rm -f "${SRC_TARGET}/Behaviors/ReloadDestinationsCommand.cs"

echo ""
echo "Files deployed. Next:"
echo "  cd ~/uo-modernuo/ModernUO && ./publish.sh release linux x64"
echo "  ~/uo-modernuo/stop.sh && ~/uo-modernuo/start.sh"
