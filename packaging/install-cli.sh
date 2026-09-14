#!/bin/sh
set -eu
source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
launcher="$source_dir/vibe-harder.sh"
test -f "$launcher" || { echo 'Run the install-cli.sh inside the installed app.' >&2; exit 1; }
bin_dir="$HOME/.local/bin"
mkdir -p "$bin_dir"
link="$bin_dir/vibe-harder"
if [ -e "$link" ] || [ -L "$link" ]; then
    if [ ! -L "$link" ] || [ "$(readlink "$link")" != "$launcher" ]; then
        printf 'Leaving existing command unchanged: %s\nLauncher available at: %s\n' "$link" "$launcher"
        exit 0
    fi
else
    ln -s "$launcher" "$link"
fi
printf 'CLI installed: %s\n' "$link"
case ":$PATH:" in *":$bin_dir:"*) ;; *) printf 'Add %s to your PATH to use vibe-harder.\n' "$bin_dir";; esac
