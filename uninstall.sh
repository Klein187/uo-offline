#!/usr/bin/env bash
# =========================================================================
# uninstall.sh — Remove the UO Offline (ModernUO) install.
# =========================================================================
set -uo pipefail

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --install-root)
      if [[ "$#" -lt 2 || -z "$2" ]]; then
        echo "--install-root requires a path." >&2
        exit 1
      fi
      INSTALL_ROOT="$2"
      shift 2
      ;;
    --install-root=*)
      INSTALL_ROOT="${1#*=}"
      [[ -n "${INSTALL_ROOT}" ]] || { echo "--install-root requires a path." >&2; exit 1; }
      shift
      ;;
    *)
      shift
      ;;
  esac
done

INSTALL_ROOT="${INSTALL_ROOT:-${HOME}/uo-modernuo}"
DOTNET_ROOT="${HOME}/.dotnet"

# Saying "Uninstall complete" after removing nothing is how someone ends up
# with a 6 GB install they think is gone. If the root is not there, say so.
if [[ ! -d "${INSTALL_ROOT}" ]]; then
  echo ""
  echo "No install found at ${INSTALL_ROOT}"
  echo ""
  echo "If you installed somewhere else, pass that path:"
  echo "    ./uninstall.sh --install-root /path/to/uo-offline"
  echo ""
  exit 1
fi

echo ""
echo "This will delete:"
echo ""
echo "  ${INSTALL_ROOT}"
echo "    - ModernUO server build and world saves"
echo "    - ClassicUO client"
echo "    - UO Classic 7.0.23.1 game data (auto-downloaded by the installer)"
echo "    - Configuration, logs, helper scripts"
echo ""
echo "  Desktop launcher (UO-Offline.desktop / UO Offline.command)"
echo ""
echo "This will NOT delete:"
echo ""
echo "  ~/.dotnet/  (the .NET 10 SDK, ~200 MB — asked separately below)"
echo "  Any pre-existing UO Classic install you had outside the install folder"
if [[ "$(uname -s)" == "Darwin" ]]; then
  echo "  Homebrew packages (sevenzip, git, python, mono-libgdiplus and deps)"
  echo "  Rosetta 2 — other apps may need it"
fi
echo ""

read -r -p "Type 'yes' to continue, anything else to cancel: " ans
[[ "${ans}" == "yes" ]] || { echo "Cancelled."; exit 0; }

echo ""
echo "Stopping any running server or client..."
pkill -TERM -f "ModernUO.dll" 2>/dev/null || true
pkill -9    -f "ClassicUO"    2>/dev/null || true
sleep 2
pkill -9    -f "ModernUO.dll" 2>/dev/null || true

# The map editor runs a detached python server on port 8777. Deleting its
# files out from under it leaves the process holding the port.
if pgrep -f "serve_map.py" >/dev/null 2>&1; then
  echo "Stopping map editor server..."
  pkill -TERM -f "serve_map.py" 2>/dev/null || true
  sleep 1
  pkill -9 -f "serve_map.py" 2>/dev/null || true
fi

echo "Removing ${INSTALL_ROOT}..."
rm -rf "${INSTALL_ROOT}"

echo "Removing desktop launcher..."
rm -f "${HOME}/Desktop/UO-Offline.desktop"
rm -f "${HOME}/.local/share/applications/UO-Offline.desktop"
rm -f "${HOME}/Desktop/UO Offline.command"

# Refresh the application menu so KDE notices the launcher is gone.
update-desktop-database "${HOME}/.local/share/applications" >/dev/null 2>&1 || true
kbuildsycoca5 --noincremental >/dev/null 2>&1 \
  || kbuildsycoca6 --noincremental >/dev/null 2>&1 || true

# -----------------------------------------------------------------------------
# Optional: remove the per-user .NET SDK we installed.
#
# We leave it by default because:
#   - Other apps you install may need it.
#   - Re-installing UO Offline later avoids a 200 MB re-download.
# But if the user wants a fully clean slate, offer it.
# -----------------------------------------------------------------------------
if [[ -d "${DOTNET_ROOT}" ]]; then
  echo ""
  read -r -p "Also remove the .NET 10 SDK at ${DOTNET_ROOT}? (y/N): " ans
  if [[ "${ans}" == [yY] ]] || [[ "${ans}" == [yY][eE][sS] ]]; then
    echo "Removing ${DOTNET_ROOT}..."
    rm -rf "${DOTNET_ROOT}"
    echo "Removed."
  else
    echo "Keeping ${DOTNET_ROOT}."
  fi
fi

echo ""
echo "Uninstall complete."
