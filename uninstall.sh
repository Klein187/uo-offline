#!/usr/bin/env bash
# =========================================================================
# uninstall.sh — Remove the UO Offline (ModernUO) install.
# Does NOT touch your UO client data directory.
# =========================================================================
set -euo pipefail

INSTALL_ROOT="${HOME}/uo-modernuo"

echo "This will delete:"
echo "  ${INSTALL_ROOT}"
echo "  UO Offline desktop launcher"
echo ""
echo "It will NOT touch your UO client data folder."
echo ""
read -r -p "Type 'yes' to confirm: " ans
[[ "${ans}" == "yes" ]] || { echo "Cancelled."; exit 0; }

# Make sure nothing's running first.
pkill -TERM -f "ModernUO.dll" 2>/dev/null || true
pkill -9    -f "ClassicUO"    2>/dev/null || true
sleep 2
pkill -9    -f "ModernUO.dll" 2>/dev/null || true

rm -rf "${INSTALL_ROOT}"
rm -f "${HOME}/Desktop/UO-Offline.desktop"
rm -f "${HOME}/.local/share/applications/UO-Offline.desktop"

update-desktop-database "${HOME}/.local/share/applications" >/dev/null 2>&1 || true
kbuildsycoca5 --noincremental >/dev/null 2>&1 \
  || kbuildsycoca6 --noincremental >/dev/null 2>&1 || true

echo "Uninstalled."
