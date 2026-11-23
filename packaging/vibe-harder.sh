#!/bin/sh
set -eu
# Resolve the CLI symlink without changing the caller's working directory.
launcher=$0
while [ -L "$launcher" ]; do
    parent=$(CDPATH= cd -- "$(dirname -- "$launcher")" && pwd -P)
    launcher=$(readlink "$launcher")
    case "$launcher" in /*) ;; *) launcher="$parent/$launcher";; esac
done
app_dir=$(CDPATH= cd -- "$(dirname -- "$launcher")" && pwd -P)
if [ "${1-}" = wait-for-pr ]; then
    if [ -n "${VIBE_HARDER_HOST_CLI-}" ]; then
        export WSLENV="${WSLENV:+$WSLENV:}VIBE_HARDER_CHAT_ID/w:CODEX_MANAGER_DATA/w"
        exec "$(wslpath "$VIBE_HARDER_HOST_CLI")" "$@"
    fi
    exec "$app_dir/vh" "$@"
fi
if [ "$#" -gt 1 ]; then echo 'Usage: vibe-harder [directory] or vibe-harder wait-for-pr OWNER/REPO PR [--cancel]' >&2; exit 2; fi
if [ "$#" -eq 1 ]; then
    if [ ! -d "$1" ]; then printf 'Directory does not exist: %s\n' "$1" >&2; exit 2; fi
    directory=$(CDPATH= cd -- "$1" && pwd -P)
    set -- --workspace "$directory"
fi
if [ ! -x "$app_dir/VibeHarder" ]; then echo 'Vibe Harder executable is missing beside the launcher.' >&2; exit 1; fi
nohup "$app_dir/VibeHarder" "$@" </dev/null >/dev/null 2>&1 &
