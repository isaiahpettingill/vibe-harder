#!/bin/sh
# Run in Linux against an extracted package (or artifacts/publish/linux-x64).
set -eu
package=$(CDPATH= cd -- "$1" && pwd)
scratch=$(mktemp -d /tmp/codex-manager-package.XXXXXXXX)
case "$scratch" in /tmp/codex-manager-package.*) ;; *) exit 1;; esac
trap 'rm -rf -- "$scratch"' EXIT HUP INT TERM
export XDG_DATA_HOME="$scratch/share with spaces"
export HOME="$scratch/home"
sh "$package/install.sh"
fail() { echo "$1" >&2; exit 1; }
app="$XDG_DATA_HOME/codex-manager"
test -x "$app/VibeHarder" || fail "The app was not installed to $app."
test -f "$XDG_DATA_HOME/applications/codex-manager.desktop" || fail 'The desktop entry was not installed.'
test -f "$XDG_DATA_HOME/icons/hicolor/256x256/apps/codex-manager.png" || fail 'The launcher icon was not installed.'
export CODEX_MANAGER_DATA="$scratch/profile"
# Start the app and wait for it to create its database; a slow runner can take a while.
timeout 60s xvfb-run -a "$app/VibeHarder" > "$scratch/launch.log" 2>&1 &
pid=$!
waited=0
while [ ! -f "$CODEX_MANAGER_DATA/sessions.db" ]; do
  kill -0 "$pid" 2>/dev/null || { cat "$scratch/launch.log"; fail 'The app exited before creating its database.'; }
  [ "$waited" -lt 60 ] || { cat "$scratch/launch.log"; fail 'The app did not create its database within 30 seconds.'; }
  sleep 0.5; waited=$((waited + 1))
done
sleep 2
kill -0 "$pid" 2>/dev/null || { cat "$scratch/launch.log"; fail 'The app exited shortly after starting.'; }
kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null || true
cat "$scratch/launch.log"
test ! -e "$app/runtime/node" || fail 'The package bundles Node.'
test ! -e "$app/node_modules" || fail 'The package bundles node_modules.'
echo 'Linux installation and desktop startup passed without bundled Node.'
