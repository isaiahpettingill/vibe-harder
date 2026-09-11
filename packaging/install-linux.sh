#!/bin/sh
set -eu
source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
data_dir=${XDG_DATA_HOME:-"$HOME/.local/share"}
case "$data_dir" in /*) ;; *) echo 'XDG_DATA_HOME must be absolute' >&2; exit 1;; esac
target="$data_dir/codex-manager"
desktop_dir="$data_dir/applications"
icon_dir="$data_dir/icons/hicolor/256x256/apps"
test -f "$source_dir/VibeHarder" || { echo 'Run this script from an extracted Vibe Harder package.' >&2; exit 1; }
case "$(uname -m):$(cat "$source_dir/runtime.txt")" in
  x86_64:linux-x64|aarch64:linux-arm64|arm64:linux-arm64|x86_64:linux-musl-x64|aarch64:linux-musl-arm64|arm64:linux-musl-arm64) ;;
  *) echo 'This package does not match your Linux architecture.' >&2; exit 1;;
esac
mkdir -p "$target" "$desktop_dir" "$icon_dir"
if [ "$source_dir" != "$target" ]; then cp -a "$source_dir/." "$target/"; fi
chmod +x "$target/VibeHarder"
cp "$target/Assets/app.png" "$icon_dir/codex-manager.png"
# Quote the Exec argument according to the Desktop Entry specification; percent
# signs must be doubled because they introduce field codes, even inside quotes.
exec_path=$(printf '%s' "$target/VibeHarder" | sed 's/\\/\\\\\\\\/g; s/"/\\\\"/g; s/`/\\\\`/g; s/\$/\\\\$/g; s/%/%%/g')
cat > "$desktop_dir/codex-manager.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Vibe Harder
Comment=Chats and terminals for your workspaces
Exec="$exec_path"
Icon=codex-manager
Terminal=false
Categories=Development;Utility;
StartupWMClass=CodexManager
EOF
if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database "$desktop_dir"; fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -f -t "$data_dir/icons/hicolor" >/dev/null 2>&1 || true; fi
printf 'Installed Vibe Harder. Open it from your application menu.\n'
