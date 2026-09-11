#!/bin/sh
set -eu
if [ "${1:-}" != inside ]; then
    scratch=$(mktemp -d /tmp/codex-manager-native.XXXXXXXX)
    python3 tools/seed-native-smoke.py "$scratch/profile"
    export CODEX_MANAGER_DATA="$scratch/profile"
    export CODEX_MANAGER_TRACE=1
    exec xvfb-run -a sh "$0" inside
fi
artifacts/publish/linux-x64-aot/VibeHarder > artifacts/native-linux11.log 2>&1 &
app_pid=$!
trap 'kill "$app_pid" 2>/dev/null || true' EXIT HUP INT TERM
sleep 7
kill -0 "$app_pid"
import -window root artifacts/native-linux11.png
cat artifacts/native-linux11.log
python3 -c 'from pathlib import Path; log = Path("artifacts/native-linux11.log").read_text(); assert "[Binding]" not in log and "Unhandled exception" not in log, log'
