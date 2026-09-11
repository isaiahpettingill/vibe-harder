#!/bin/sh
# Run in Linux against an extracted package (or artifacts/publish/linux-x64).
set -eu
package=$(CDPATH= cd -- "$1" && pwd)
scratch=$(mktemp -d /tmp/codex-manager-package.XXXXXXXX)
case "$scratch" in /tmp/codex-manager-package.*) ;; *) exit 1;; esac
trap 'rm -rf -- "$scratch"' EXIT HUP INT TERM
export XDG_DATA_HOME="$scratch/share with spaces"
sh "$package/install.sh"
app="$XDG_DATA_HOME/codex-manager"
test -x "$app/VibeHarder"
test -f "$XDG_DATA_HOME/applications/codex-manager.desktop"
test -f "$XDG_DATA_HOME/icons/hicolor/256x256/apps/codex-manager.png"
export CODEX_MANAGER_DATA="$scratch/profile"
set +e
timeout 8s xvfb-run -a "$app/VibeHarder" > "$scratch/launch.log" 2>&1
result=$?
set -e
cat "$scratch/launch.log"
test "$result" -eq 124 || { echo "App exited unexpectedly: $result" >&2; exit 1; }
test -f "$CODEX_MANAGER_DATA/sessions.db"
test ! -e "$app/runtime/node"
test ! -e "$app/node_modules"
echo 'Linux installation and desktop startup passed without bundled Node.'
