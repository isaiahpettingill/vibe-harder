#!/bin/sh
set -eu
rid=${1:-linux-musl-x64}
mode=${2:-aot}
version=${3:-1.0.0}
case "$rid" in linux-musl-x64|linux-musl-arm64) ;; *) exit 2;; esac
case "$mode" in aot) aot=true; bundled=true;; bundled) aot=false; bundled=true;; framework) aot=false; bundled=false;; *) exit 2;; esac
publish="artifacts/publish/$rid-$mode"
mkdir -p "$publish" artifacts/packages
dotnet publish src/CodexManager -c Release -r "$rid" --self-contained "$bundled" -p:PublishAot="$aot" -p:StripSymbols=true -p:Version="$version" -o "$publish"
find "$publish" -maxdepth 1 -type f \( -name '*.pdb' -o -name '*.dbg' \) -delete
cp packaging/install-linux.sh "$publish/install.sh"
cp packaging/README.md "$publish/INSTALL.md"
cp LICENSE "$publish/LICENSE"
printf '%s' "$rid" > "$publish/runtime.txt"
printf '{"version":"%s","runtime":"%s","mode":"%s"}\n' "$version" "$rid" "$mode" > "$publish/update.json"
chmod +x "$publish/install.sh" "$publish/VibeHarder"
tar -czf "artifacts/packages/VibeHarder-$version-$rid-$mode.tar.gz" -C "$publish" .
