#!/usr/bin/env bash
# =========================================================================
# stop.sh — Cleanly shut down the UO Offline server.
#
# SIGTERM first: lets ModernUO save the world before exiting.
# SIGKILL only as last resort. Losing the world save mid-write would mean
# losing your progress since the last autosave.
# =========================================================================
set -uo pipefail

INSTALL_ROOT="${HOME}/uo-modernuo"
PIDFILE="${INSTALL_ROOT}/modernuo.pid"

# Close the client first — no save needed.
if pkill -f "ClassicUO" 2>/dev/null; then
  echo "ClassicUO stopped."
fi

if [[ -f "${PIDFILE}" ]] && kill -0 "$(cat "${PIDFILE}")" 2>/dev/null; then
  pid="$(cat "${PIDFILE}")"
  echo "Sending SIGTERM to ModernUO (pid ${pid}) to save and exit..."
  kill -TERM "${pid}"

  # ModernUO saves can take 10-20s on a populated world. Wait up to 30s.
  for _ in $(seq 1 30); do
    if ! kill -0 "${pid}" 2>/dev/null; then
      echo "ModernUO stopped cleanly."
      rm -f "${PIDFILE}"
      exit 0
    fi
    sleep 1
  done

  echo "Server did not stop within 30s. Forcing kill — world state may be lost." >&2
  kill -9 "${pid}" 2>/dev/null || true
  rm -f "${PIDFILE}"
else
  # Fallback: kill any stray dotnet ModernUO process.
  if pgrep -f "ModernUO.dll" >/dev/null; then
    echo "Found orphan ModernUO process. Sending SIGTERM..."
    pkill -TERM -f "ModernUO.dll" || true
    sleep 5
    pkill -9 -f "ModernUO.dll" 2>/dev/null || true
  fi
fi

echo "Done."
