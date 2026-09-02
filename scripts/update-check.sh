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
# The notes are written by hand, in UPDATE-NOTES.txt on the branch.
#
# This used to be assembled from commit messages through the compare API,
# with a python3 path and a sed fallback for machines without it. It worked,
# and it read like a developer talking to another developer, because that is
# what commit messages are. Someone opening the launcher wants to know what
# is different in the GAME. A file in the repo says that better, and it
# deletes the whole JSON-scraping fallback along with it.
#
# Same failure rule as the rest of this script: if the fetch fails, show a
# generic line rather than dropping the check.
CHANGELOG="$(curl -fsSL --max-time "${TIMEOUT}" -H "${UA}" \
  "https://raw.githubusercontent.com/${REPO}/${BRANCH}/UPDATE-NOTES.txt" 2>/dev/null)"

[[ -n "${CHANGELOG}" ]] || CHANGELOG="A new version is available on GitHub."

# The notes speak for themselves; no generated heading on top of them. The
# installer line stays, because it is the one thing somebody needs to know
# before choosing Update and it is not news.
BODY="${CHANGELOG}

Updating re-runs the installer, which rebuilds the server with the new
bots. Your world, characters and accounts are kept."

# -------------------------------------------------------------------------
# ask -> prints one of: update | play | skip
# -------------------------------------------------------------------------
have_gui() { [[ -n "${DISPLAY:-}" || -n "${WAYLAND_DISPLAY:-}" ]]; }

ask() {
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

if have_gui && command -v kdialog >/dev/null 2>&1; then
  kdialog --title "${TITLE}" --msgbox "${MSG}" >/dev/null 2>&1
elif have_gui && command -v zenity >/dev/null 2>&1; then
  zenity --info --title="${TITLE}" --no-wrap --text="${MSG}" >/dev/null 2>&1
fi
exit 0
