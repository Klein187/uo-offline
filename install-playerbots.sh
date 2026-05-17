#!/usr/bin/env bash
# =========================================================================
# install-playerbots.sh — Add the PlayerBots system to an existing
# uo-offline install. Safe to run on a working server; it copies bot
# source files into the ModernUO source tree and rebuilds.
#
# Run after the main install.sh has completed and your server is working.
# =========================================================================

set -e

INSTALL_ROOT="${HOME}/uo-modernuo"
MODERNUO_DIR="${INSTALL_ROOT}/ModernUO"
SRC_TARGET="${MODERNUO_DIR}/Projects/UOContent/CustomBots"
CHAT_TARGET="${MODERNUO_DIR}/Distribution/Data/PlayerBotChat"
WAYPOINTS_TARGET="${MODERNUO_DIR}/Distribution/Data/Waypoints"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BOTS_DIR="${HERE}/playerbots"

# Sanity checks ----------------------------------------------------------

if [[ ! -d "${MODERNUO_DIR}" ]]; then
    echo "ERROR: ModernUO not found at ${MODERNUO_DIR}"
    echo "Run ./install.sh first to set up the base UO server."
    exit 1
fi

if [[ ! -d "${BOTS_DIR}/source/CustomBots" ]]; then
    echo "ERROR: bot source missing at ${BOTS_DIR}/source/CustomBots"
    echo "Make sure you cloned the full repo (or downloaded the release zip)."
    exit 1
fi

if [[ ! -d "${BOTS_DIR}/data/PlayerBotChat" ]]; then
    echo "ERROR: chat data missing at ${BOTS_DIR}/data/PlayerBotChat"
    exit 1
fi

# Deploy source files ---------------------------------------------------

echo "==> Deploying bot source -> ${SRC_TARGET}"
mkdir -p "${SRC_TARGET}"
cp -rT "${BOTS_DIR}/source/CustomBots" "${SRC_TARGET}"

echo "==> Deploying chat data -> ${CHAT_TARGET}"
mkdir -p "${CHAT_TARGET}"
cp -rT "${BOTS_DIR}/data/PlayerBotChat" "${CHAT_TARGET}"

if [[ -d "${BOTS_DIR}/data/Waypoints" ]]; then
    echo "==> Deploying waypoint graph -> ${WAYPOINTS_TARGET}"
    mkdir -p "${WAYPOINTS_TARGET}"
    cp -rT "${BOTS_DIR}/data/Waypoints" "${WAYPOINTS_TARGET}"
fi

# Clean up legacy files from older versions of the bot system -----------

LEGACY_FILES=(
    "${SRC_TARGET}/Behaviors/RouteRegistry.cs"
    "${SRC_TARGET}/Behaviors/ReloadRoutesCommand.cs"
    "${SRC_TARGET}/Behaviors/DestinationRegistry.cs"
    "${SRC_TARGET}/Behaviors/ReloadDestinationsCommand.cs"
)
for f in "${LEGACY_FILES[@]}"; do
    [[ -f "$f" ]] && rm -f "$f" && echo "   removed legacy: $(basename $f)"
done

LEGACY_DIRS=(
    "${MODERNUO_DIR}/Distribution/Data/Routes"
    "${MODERNUO_DIR}/Distribution/Data/Destinations"
)
for d in "${LEGACY_DIRS[@]}"; do
    [[ -d "$d" ]] && rm -rf "$d" && echo "   removed legacy dir: $(basename $d)"
done

# Rebuild ModernUO -------------------------------------------------------

echo
echo "==> Rebuilding ModernUO (this can take a few minutes)"
cd "${MODERNUO_DIR}"

if [[ ! -x "./publish.sh" ]]; then
    echo "ERROR: publish.sh not found in ${MODERNUO_DIR}"
    echo "Your ModernUO install may be incomplete."
    exit 1
fi

./publish.sh release linux x64

echo
echo "==> PlayerBots installed."
echo
echo "Next steps:"
echo "  1. Restart the server:   ~/uo-modernuo/stop.sh && ~/uo-modernuo/start.sh"
echo "                           (or use the desktop icon)"
echo
echo "  2. In-game, log in as admin and type:  [BotPanel"
echo "     The bot admin panel lets you spawn bots at banks, dungeons,"
echo "     and anywhere else."
echo
echo "  3. The Lifecycle system is enabled by default — bots will gradually"
echo "     transition between behaviors (BankSitter, Adventurer, Traveler)"
echo "     based on per-bot personalities. The world stays alive over time."
echo
echo "See README.md for the full feature list and in-game command reference."
