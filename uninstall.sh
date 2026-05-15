#!/usr/bin/env bash
# =========================================================================
# uninstall.sh — Remove the UO Offline (ModernUO) install.
# =========================================================================
set -uo pipefail

INSTALL_ROOT="${HOME}/uo-modernuo"
DOTNET_ROOT="${HOME}/.dotnet"

echo ""
echo "This will delete:"
echo ""
echo "  ${INSTALL_ROOT}"
echo "    - ModernUO server build and world saves"
echo "    - ClassicUO client"
echo "    - UO Classic 7.0.23.1 game data (auto-downloaded by the installer)"
echo "    - Configuration, logs, helper scripts"
echo ""
echo "  Desktop launcher (UO-Offline.desktop)"
echo ""
echo "This will NOT delete:"
echo ""
echo "  ~/.dotnet/  (the .NET 10 SDK, ~200 MB — asked separately below)"
echo "  Any pre-existing UO Classic install you had outside ~/uo-modernuo/"
echo ""

read -r -p "Type 'yes' to continue, anything else to cancel: " ans
[[ "${ans}" == "yes" ]] || { echo "Cancelled."; exit 0; }

echo ""
echo "Stopping any running server or client..."
pkill -TERM -f "ModernUO.dll" 2>/dev/null || true
pkill -9    -f "ClassicUO"    2>/dev/null || true
sleep 2
pkill -9    -f "ModernUO.dll" 2>/dev/null || true

echo "Removing ${INSTALL_ROOT}..."
rm -rf "${INSTALL_ROOT}"

echo "Removing desktop launcher..."
rm -f "${HOME}/Desktop/UO-Offline.desktop"
rm -f "${HOME}/.local/share/applications/UO-Offline.desktop"

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
  if [[ "${ans,,}" == "y" ]] || [[ "${ans,,}" == "yes" ]]; then
    echo "Removing ${DOTNET_ROOT}..."
    rm -rf "${DOTNET_ROOT}"
    echo "Removed."
  else
    echo "Keeping ${DOTNET_ROOT}."
  fi
fi

echo ""
echo "Uninstall complete."
