#!/bin/sh
set -eu
if [ "$#" -gt 1 ]; then echo 'Usage: vibe-harder [directory]' >&2; exit 2; fi
if [ "$#" -eq 1 ]; then
    if [ ! -d "$1" ]; then printf 'Directory does not exist: %s\n' "$1" >&2; exit 2; fi
    directory=$(CDPATH= cd -- "$1" && pwd -P)
    set -- --workspace "$directory"
fi
# Resolve the CLI symlink without changing the caller's working directory.
launcher=$0
while [ -L "$launcher" ]; do
    parent=$(CDPATH= cd -- "$(dirname -- "$launcher")" && pwd -P)
    launcher=$(readlink "$launcher")
    case "$launcher" in /*) ;; *) launcher="$parent/$launcher";; esac
done
app_dir=$(CDPATH= cd -- "$(dirname -- "$launcher")" && pwd -P)
if [ ! -x "$app_dir/VibeHarder" ]; then echo 'Vibe Harder executable is missing beside the launcher.' >&2; exit 1; fi
nohup "$app_dir/VibeHarder" "$@" </dev/null >/dev/null 2>&1 &
