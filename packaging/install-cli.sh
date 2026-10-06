#!/bin/sh
set -eu
source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
launcher="$source_dir/vibe-harder.sh"
test -f "$launcher" || { echo 'Run the install-cli.sh inside the installed app.' >&2; exit 1; }
bin_dir="$HOME/.local/bin"
mkdir -p "$bin_dir"
# Links a command into ~/.local/bin, leaving any other command of that name alone.
install_link() {
    target=$1; link="$bin_dir/$2"
    if [ -e "$link" ] || [ -L "$link" ]; then
        if [ ! -L "$link" ] || [ "$(readlink "$link")" != "$target" ]; then
            printf 'Leaving existing command unchanged: %s\nAvailable at: %s\n' "$link" "$target"
            return
        fi
    else
        ln -s "$target" "$link"
    fi
    printf 'CLI installed: %s\n' "$link"
}
install_link "$launcher" vibe-harder
# vh, the non-interactive chat command line, ships beside the app.
if [ -x "$source_dir/vh" ]; then install_link "$source_dir/vh" vh; fi
case ":$PATH:" in *":$bin_dir:"*) ;; *) printf 'Add %s to your PATH to use vibe-harder and vh.\n' "$bin_dir";; esac
