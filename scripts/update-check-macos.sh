#!/usr/bin/env bash
# =========================================================================
# update-check-macos.sh - "is there a newer UO Offline?" check, run at launch.
#
# The macOS counterpart to update-check.sh / update-check.ps1. Same rules:
#
#   - No internet, GitHub down, rate limited, anything at all goes wrong:
#     say nothing and let the game start. A failed check must never cost
#     the player their session.
#   - Already up to date: say nothing. No output, no prompt.
#   - Behind: show what the update contains and let the player choose.
#     Declining is remembered per version, so it never nags twice.
#
# Exit codes, which start.sh reads:
#   0   carry on and launch the game
#   10  the installer was started; do not launch the game
#
# Asking is simpler here than on Linux: every Mac has osascript, so the
# prompt is a native dialog. The Desktop launcher is a .command file, which
# Terminal opens, so a terminal prompt is a real fallback rather than the
# invisible one it would be under a Linux desktop entry.
# =========================================================================
set -uo pipefail

INSTALL_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAMP="${INSTALL_ROOT}/uo-offline-version.json"
SKIP="${INSTALL_ROOT}/uo-offline-skipped.txt"
TIMEOUT=6

TITLE="UO Offline - update available"

# Nothing to compare against, or no way to ask: launch the game.
[[ -f "${STAMP}" ]] || exit 0
command -v curl >/dev/null 2>&1 || exit 0

json_field() { # <field> <file-or-stdin-text>
  printf '%s' "$2" \
    | grep -oE "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
    | head -n1 \
    | sed -E 's/.*"([^"]*)"[[:space:]]*$/\1/'
}

STAMP_TEXT="$(cat "${STAMP}" 2>/dev/null)" || exit 0
LOCAL_SHA="$(json_field Sha    "${STAMP_TEXT}")"
REPO="$(json_field Repo        "${STAMP_TEXT}")"
BRANCH="$(json_field Branch    "${STAMP_TEXT}")"

[[ -n "${LOCAL_SHA}" && -n "${REPO}" && -n "${BRANCH}" ]] || exit 0

API="https://api.github.com/repos/${REPO}"
UA="User-Agent: uo-offline-launcher"

HEAD_JSON="$(curl -fsSL --max-time "${TIMEOUT}" -H "${UA}" "${API}/commits/${BRANCH}" 2>/dev/null)"
[[ -n "${HEAD_JSON}" ]] || exit 0

REMOTE_SHA="$(printf '%s' "${HEAD_JSON}" \
  | grep -oE '"sha"[[:space:]]*:[[:space:]]*"[0-9a-f]{40}"' \
  | head -n1 | sed -E 's/.*"([0-9a-f]{40})".*/\1/')"

[[ -n "${REMOTE_SHA}" ]] || exit 0

# Up to date. Say nothing at all.
[[ "${REMOTE_SHA}" != "${LOCAL_SHA}" ]] || exit 0

# The player already said "skip" to exactly this version.
if [[ -f "${SKIP}" ]] && [[ "$(tr -d '[:space:]' < "${SKIP}")" == "${REMOTE_SHA}" ]]; then
  exit 0
fi

# -------------------------------------------------------------------------
# What does the update contain? python3 when it is available because it
# parses the json properly; the grep path is the fallback for a box that
# does not have it. Either way, a failure here downgrades to a generic
# line rather than dropping the whole check.
# -------------------------------------------------------------------------
CMP_JSON="$(curl -fsSL --max-time "${TIMEOUT}" -H "${UA}" \
  "${API}/compare/${LOCAL_SHA}...${BRANCH}" 2>/dev/null)"

CHANGELOG=""
if [[ -n "${CMP_JSON}" ]]; then
  if command -v python3 >/dev/null 2>&1; then
    CHANGELOG="$(printf '%s' "${CMP_JSON}" | python3 -c '
import json, sys
try:
    data = json.load(sys.stdin)
except Exception:
    sys.exit(0)
# Subject on the bullet, the commit body indented under it. A change
# worth explaining carries its explanation in the body, and that is
# the part the player wants; the subject alone says what changed and
# nothing about what it means. Capped so one chatty commit cannot
# fill the whole box.
entries = []
for c in data.get("commits", []):
    raw = c.get("commit", {}).get("message", "")
    parts = raw.replace("\r", "").split("\n")
    subject = parts[0].strip()
    if not subject:
        continue
    block = ["  - " + subject]
    shown = 0
    for line in parts[1:]:
        if shown >= 8:
            break
        text = line.strip()
        if not text:
            if shown and block[-1] != "":
                block.append("")
            continue
        # Trailers are bookkeeping, not news. The player does not need
        # Co-Authored-By on a "what is in this update" screen.
        low = text.lower()
        if low.startswith(("co-authored-by:", "signed-off-by:", "claude-session:",
                           "reviewed-by:", "refs:", "fixes:", "closes:",
                           "http://", "https://", "generated with")):
            continue
        block.append("      " + text)
        shown += 1
    if shown:
        block.append("")
    entries.append("\n".join(block))
entries.reverse()
print("\n".join(entries))
' 2>/dev/null)"
  else
    # No python3. Slice out just the "commits" array first - the compare
    # response also carries base_commit and merge_base_commit, which are
    # commits the player ALREADY has, and a trailing "files" array whose
    # patch text can contain anything at all.
    CHANGELOG="$(printf '%s' "${CMP_JSON}" \
      | sed -E 's/.*"commits"[[:space:]]*:[[:space:]]*\[//' \
      | sed -E 's/"files"[[:space:]]*:[[:space:]]*\[.*//' \
      | grep -oE '"message"[[:space:]]*:[[:space:]]*"[^"]*"' \
      | sed -E 's/^"message"[[:space:]]*:[[:space:]]*"//; s/"$//' \
      | sed -E 's/[\]n.*$//' \
      | sed -E 's/^/  - /' \
      | tac)"
  fi
fi

[[ -n "${CHANGELOG}" ]] || CHANGELOG="  - A new version is available on GitHub."

BODY="A new version of UO Offline is available.

What's in it:

${CHANGELOG}

Updating re-runs the installer, which rebuilds the server with the new
bots. Your world, characters and accounts are kept."

# -------------------------------------------------------------------------
# ask -> prints one of: update | play | skip
# -------------------------------------------------------------------------
ask() {
  if command -v osascript >/dev/null 2>&1; then
    local res
    res="$(osascript - "${TITLE}" "${BODY}" 2>/dev/null <<'APPLESCRIPT'
on run argv
  set dialogTitle to item 1 of argv
  set dialogBody to item 2 of argv
  try
    set r to display dialog dialogBody with title dialogTitle ¬
      buttons {"Skip This Version", "Play Now", "Update Now"} ¬
      default button "Update Now" cancel button "Play Now"
    return button returned of r
  on error number -128
    return "Play Now"
  end try
end run
APPLESCRIPT
)"
    case "${res}" in
      "Update Now")        printf 'update'; return ;;
      "Skip This Version") printf 'skip';   return ;;
      "Play Now")          printf 'play';   return ;;
    esac
    # osascript unavailable to this session (no WindowServer, ssh): fall through.
  fi

  # Terminal prompt. Only when someone is actually there to read it.
  if [[ -t 0 && -t 1 ]]; then
    printf '\n\033[0;36m=========================================================\033[0m\n' >&2
    printf '%s\n' "${BODY}" >&2
    printf '\033[0;36m=========================================================\033[0m\n' >&2
    printf '  [u] update now    [p] play now    [s] skip this version\n' >&2
    local reply=""
    read -r -p "  choice [p]: " reply </dev/tty
    case "${reply}" in
      u|U|update) printf 'update' ;;
      s|S|skip)   printf 'skip'   ;;
      *)          printf 'play'   ;;
    esac
    return
  fi

  # No dialog, no terminal. Never block on a prompt the player cannot see -
  # just start the game.
  printf 'play'
}

CHOICE="$(ask)"

if [[ "${CHOICE}" == "skip" ]]; then
  printf '%s\n' "${REMOTE_SHA}" > "${SKIP}"
  exit 0
fi

[[ "${CHOICE}" == "update" ]] || exit 0

# -------------------------------------------------------------------------
# Update: fetch the branch zip and hand off to its installer. The installer
# knows how to deploy and rebuild and is safe to re-run, so there is no
# separate update path to maintain.
# -------------------------------------------------------------------------
command -v unzip >/dev/null 2>&1 || exit 0

WORK="$(mktemp -d 2>/dev/null)" || exit 0
ZIP="${WORK}/source.zip"

if ! curl -fL --max-time 300 -o "${ZIP}" \
     "https://github.com/${REPO}/archive/${REMOTE_SHA}.zip" 2>/dev/null; then
  rm -rf "${WORK}"
  exit 0
fi

unzip -q "${ZIP}" -d "${WORK}" 2>/dev/null || { rm -rf "${WORK}"; exit 0; }

INSTALLER="$(find "${WORK}" -maxdepth 3 -name install-osx-silicon.sh -type f 2>/dev/null | head -n1)"
if [[ -z "${INSTALLER}" ]]; then
  rm -rf "${WORK}"
  exit 0
fi
chmod +x "${INSTALLER}" 2>/dev/null

# In a terminal, just run it here so the player watches the build. Launched
# from the desktop icon there is no terminal, so open one - the rebuild
# takes minutes and a silent background job looks like nothing happened.
# --install-root matters: without it an install that lives anywhere other
# than the default would be rebuilt into ~/uo-modernuo instead of updated.
if [[ -t 1 ]]; then
  ( cd "$(dirname "${INSTALLER}")" \
    && UO_OFFLINE_SOURCE_SHA="${REMOTE_SHA}" \
       bash "${INSTALLER}" --install-root "${INSTALL_ROOT}" )
  exit 10
fi

# Launched from the Desktop .command with no tty (rare, but Finder can do
# it). Hand the rebuild to Terminal.app so the player can watch it.
if command -v osascript >/dev/null 2>&1; then
  RUN="cd $(printf '%q' "$(dirname "${INSTALLER}")") && UO_OFFLINE_SOURCE_SHA=$(printf '%q' "${REMOTE_SHA}") bash $(printf '%q' "${INSTALLER}") --install-root $(printf '%q' "${INSTALL_ROOT}")"
  if osascript -e 'on run argv
  tell application "Terminal"
    activate
    do script (item 1 of argv)
  end tell
end run' "${RUN}" >/dev/null 2>&1; then
    exit 10
  fi
fi

# No terminal emulator available. Tell the player where the installer is
# rather than running a multi-minute rebuild behind their back.
MSG="The update downloaded to:

${INSTALLER}

Nothing was available to run it in. Run that script yourself to finish
updating. Starting the game as normal for now."

if command -v osascript >/dev/null 2>&1; then
  osascript - "${TITLE}" "${MSG}" >/dev/null 2>&1 <<'APPLESCRIPT'
on run argv
  display dialog (item 2 of argv) with title (item 1 of argv) buttons {"OK"} default button "OK"
end run
APPLESCRIPT
fi
exit 0
