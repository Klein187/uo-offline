#!/usr/bin/env bash
# =========================================================================
# update-check.sh - "is there a newer UO Offline?" check, run at launch.
#
# The Linux/Steam Deck counterpart to update-check.ps1. Same three rules:
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
# Asking the player is the awkward part on Linux. The desktop entry runs
# with Terminal=false, so a read prompt would be invisible and the game
# would just appear to hang. So we ask through whatever is actually there,
# in order: kdialog (Steam Deck's KDE), zenity (GNOME and friends), then a
# plain terminal prompt, and if none of those exist we stay silent and
# launch - never block on a prompt nobody can see.
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
      | awk '{a[i++]=$0} END {for (j=i-1; j>=0;) print a[j--]}')"
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
have_gui() { [[ -n "${DISPLAY:-}" || -n "${WAYLAND_DISPLAY:-}" ]]; }

ask() {
  if [[ "$(uname -s)" == "Darwin" ]] && command -v osascript >/dev/null 2>&1; then
    local escaped_body="${BODY//\"/\\\"}"
    local escaped_title="${TITLE//\"/\\\"}"
    local res
    res="$(osascript -e "
try
  set res to button returned of (display dialog \"${escaped_body}\" with title \"${escaped_title}\" buttons {\"Skip This Version\", \"Play Now\", \"Update Now\"} default button \"Update Now\" cancel button \"Play Now\")
  return res
on error number -128
  return \"Play Now\"
end try
" 2>/dev/null || true)"
    case "${res}" in
      "Update Now") printf 'update' ;;
      "Skip This Version") printf 'skip' ;;
      *) printf 'play' ;;
    esac
    return
  fi

  if have_gui && command -v kdialog >/dev/null 2>&1; then
    kdialog --title "${TITLE}" \
            --yes-label "Update Now" \
            --no-label "Play Now" \
            --cancel-label "Skip This Version" \
            --yesnocancel "${BODY}" >/dev/null 2>&1
    case $? in
      0) printf 'update' ;;
      1) printf 'play'   ;;
      *) printf 'skip'   ;;
    esac
    return
  fi

  if have_gui && command -v zenity >/dev/null 2>&1; then
    local extra
    extra="$(zenity --question --title="${TITLE}" --no-wrap \
              --text="${BODY}" \
              --ok-label="Update Now" --cancel-label="Play Now" \
              --extra-button="Skip This Version" 2>/dev/null)"
    local rc=$?
    if [[ "${extra}" == "Skip This Version" ]]; then printf 'skip'
    elif [[ ${rc} -eq 0 ]]; then printf 'update'
    else printf 'play'
    fi
    return
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

  # No kdialog, no zenity, no terminal. Never block on a prompt the player
  # cannot see - just start the game.
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
     "https://github.com/${REPO}/archive/${BRANCH}.zip" 2>/dev/null; then
  rm -rf "${WORK}"
  exit 0
fi

unzip -q "${ZIP}" -d "${WORK}" 2>/dev/null || { rm -rf "${WORK}"; exit 0; }

INSTALLER="$(find "${WORK}" -maxdepth 3 -name install.sh -type f 2>/dev/null | head -n1)"
if [[ -z "${INSTALLER}" ]]; then
  rm -rf "${WORK}"
  exit 0
fi
chmod +x "${INSTALLER}" 2>/dev/null

# In a terminal, just run it here so the player watches the build. Launched
# from the desktop icon there is no terminal, so open one - the rebuild
# takes minutes and a silent background job looks like nothing happened.
if [[ -t 1 ]]; then
  ( cd "$(dirname "${INSTALLER}")" && bash "${INSTALLER}" )
  exit 10
fi

if [[ "$(uname -s)" == "Darwin" ]]; then
  osascript -e "tell application \"Terminal\" to do script \"cd '$(dirname "${INSTALLER}")' && bash '${INSTALLER}'\"" >/dev/null 2>&1 &
  exit 10
fi

for term in konsole gnome-terminal xfce4-terminal x-terminal-emulator xterm; do
  command -v "${term}" >/dev/null 2>&1 || continue
  case "${term}" in
    gnome-terminal) "${term}" -- bash -lc "cd '$(dirname "${INSTALLER}")' && bash '${INSTALLER}'; echo; read -r -p 'Done. Press Enter to close.'" & ;;
    *)              "${term}" -e bash -lc "cd '$(dirname "${INSTALLER}")' && bash '${INSTALLER}'; echo; read -r -p 'Done. Press Enter to close.'" & ;;
  esac
  exit 10
done

# No terminal emulator available. Tell the player where the installer is
# rather than running a multi-minute rebuild behind their back.
MSG="The update downloaded to:

${INSTALLER}

No terminal program was found to run it in. Run that script yourself to
finish updating. Starting the game as normal for now."

if [[ "$(uname -s)" == "Darwin" ]] && command -v osascript >/dev/null 2>&1; then
  local escaped_msg="${MSG//\"/\\\"}"
  local escaped_title="${TITLE//\"/\\\"}"
  osascript -e "display dialog \"${escaped_msg}\" with title \"${escaped_title}\" buttons {\"OK\"} default button \"OK\"" >/dev/null 2>&1
elif have_gui && command -v kdialog >/dev/null 2>&1; then
  kdialog --title "${TITLE}" --msgbox "${MSG}" >/dev/null 2>&1
elif have_gui && command -v zenity >/dev/null 2>&1; then
  zenity --info --title="${TITLE}" --no-wrap --text="${MSG}" >/dev/null 2>&1
fi
exit 0
