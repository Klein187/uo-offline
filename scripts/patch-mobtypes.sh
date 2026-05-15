#!/usr/bin/env bash
# =========================================================================
# patch-mobtypes.sh — Work around a ClassicUO parser bug.
#
# Modern UO client data files (7.0.59.8+) added Stygian Abyss creatures
# with a 4-column format in mobtypes.txt:
#     ID  TYPE  FLAGS  EXTRA
# ClassicUO's AnimationsLoader expects 3 columns and crashes parsing
# these lines with "Format_InvalidStringWithValue, 10000".
#
# For T2A-era play we never spawn these creatures (gargoyles, raptors,
# medusa, etc. are all SA+ content), so commenting them out has zero
# gameplay impact while letting ClassicUO load.
#
# This script makes a one-time backup at mobtypes.txt.bak before editing.
# Re-running is safe — if the backup exists, no further changes are made.
# =========================================================================
set -uo pipefail

UO_DATA="${1:-}"

if [[ -z "${UO_DATA}" ]]; then
  echo "Usage: $0 <path-to-uo-data-folder>"
  echo ""
  echo "Example:"
  echo "  $0 '/home/deck/Desktop/Electronic Arts/Ultima Online Classic'"
  exit 1
fi

TARGET="${UO_DATA}/mobtypes.txt"

if [[ ! -f "${TARGET}" ]]; then
  echo "ERROR: ${TARGET} not found." >&2
  exit 1
fi

# Bail early if we've already patched.
if [[ -f "${TARGET}.bak" ]]; then
  echo "Already patched — backup at ${TARGET}.bak exists."
  echo "If you want to re-patch from scratch, run:"
  echo "  mv '${TARGET}.bak' '${TARGET}'"
  echo "  $0 '${UO_DATA}'"
  exit 0
fi

cp "${TARGET}" "${TARGET}.bak"
echo "Backup saved: ${TARGET}.bak"

# The crashing trigger is the flag value 10000, which marks "use UOP
# animation" — a format ClassicUO's loader doesn't fully handle. Some
# of these lines are 3-column, some 4-column; both crash. We comment
# out every non-comment line whose third field is 10000.
awk '
  # Pass through already-commented lines and blanks unchanged.
  /^[[:space:]]*#/ || /^[[:space:]]*$/ { print; next }

  {
    # Strip any inline comment so the column count is honest.
    line = $0
    sub(/[[:space:]]*#.*$/, "", line)
    n = split(line, parts, /[[:space:]]+/)
    start = (parts[1] == "" ? 2 : 1)

    # Field 3 (relative to start) is the FLAGS column.
    flags = parts[start + 2]

    if (flags == "10000") {
      print "# [classicuo-compat-patch] " $0
      patched++
    } else {
      print
    }
  }
' "${TARGET}.bak" > "${TARGET}"

# Report what we did.
patched=$(grep -c '^# \[classicuo-compat-patch\]' "${TARGET}")
echo "Commented ${patched} incompatible lines."
echo "Original preserved at ${TARGET}.bak"
