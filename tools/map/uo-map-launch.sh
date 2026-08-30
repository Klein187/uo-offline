#!/bin/bash
# =========================================================================
# uo-map-launch.sh — one click to the living map.
# Starts serve_map.py if it isn't already running (idempotent: clicking
# again just opens another browser tab), waits for it to answer, then
# opens the map in the default browser.
# =========================================================================
URL="http://localhost:8777/map.html"
LOG="$HOME/uo-map/serve_map.log"

# already serving? (probe the port rather than guessing at process names)
if ! curl -s -o /dev/null --max-time 1 "$URL"; then
    nohup python3 "$HOME/uo-map/serve_map.py" >"$LOG" 2>&1 &
    # wait up to 5s for it to come up
    for _ in $(seq 1 10); do
        sleep 0.5
        curl -s -o /dev/null --max-time 1 "$URL" && break
    done
fi

if [[ "$(uname -s)" == "Darwin" ]]; then
    open "$URL"
elif command -v xdg-open >/dev/null 2>&1; then
    xdg-open "$URL"
fi
