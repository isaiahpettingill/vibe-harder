#!/bin/sh
# Download and install the latest stable release for this Linux machine.
set -eu
fail() { printf '%s\n' "$*" >&2; exit 1; }
if [ "${1:-}" = '--help' ]; then
    printf '%s\n' 'Install the latest Vibe Harder release for Linux x64/ARM64 (glibc or musl).' \
        'Usage: sh install.sh [--help]' \
        'Installs per-user under ${XDG_DATA_HOME:-$HOME/.local/share}/codex-manager.' \
        'Requires curl or wget, tar, and sha256sum or shasum. No sudo or .NET SDK needed.'
    exit 0
fi
[ "$#" -eq 0 ] || fail 'Unknown argument. Use --help.'
[ "$(uname -s)" = Linux ] || fail 'This installer is for Linux. Use the Windows or macOS release instead.'
case "$(uname -m)" in
    x86_64|amd64) arch=x64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) fail 'Supported Linux architectures: x86_64 and aarch64. No release is available for this architecture.' ;;
esac
libc=linux
mode=aot
if ldd --version 2>&1 | grep -qi musl; then
    libc=linux-musl
else
    for loader in /lib/ld-musl-*.so.1; do
        if [ -f "$loader" ] && ! getconf GNU_LIBC_VERSION >/dev/null 2>&1; then libc=linux-musl; break; fi
    done
fi
for tool in tar mktemp sed awk grep; do command -v "$tool" >/dev/null 2>&1 || fail "Install $tool first."; done
if [ "$libc" = linux ]; then
    glibc=$(getconf GNU_LIBC_VERSION 2>/dev/null || true)
    glibc=${glibc##* }
    if ! awk -v v="$glibc" 'BEGIN { split(v, n, "."); exit !(n[1] > 2 || (n[1] == 2 && n[2] >= 38)) }'; then
        fail 'Native AOT requires glibc 2.38 or newer. Upgrade this distribution or manually download a bundled release.'
    fi
fi
if command -v curl >/dev/null 2>&1; then
    fetch() { curl --fail --silent --show-error --location --retry 3 --connect-timeout 20 --output "$2" "$1"; }
elif command -v wget >/dev/null 2>&1; then
    fetch() { wget -q -T 20 -O "$2" "$1"; }
else
    fail 'Install curl or wget first.'
fi
if command -v sha256sum >/dev/null 2>&1; then
    verify() { sha256sum -c "$1"; }
elif command -v shasum >/dev/null 2>&1; then
    verify() { shasum -a 256 -c "$1"; }
else
    fail 'Install sha256sum (coreutils) or shasum first.'
fi
scratch=$(mktemp -d)
trap 'rm -rf -- "$scratch"' 0
trap 'exit 1' HUP INT TERM
repo=https://github.com/isaiahpettingill/vibe-harder
fetch https://api.github.com/repos/isaiahpettingill/vibe-harder/releases/latest "$scratch/release.json"
tag=$(sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$scratch/release.json")
printf '%s\n' "$tag" | grep -Eq '^v[0-9]+\.[0-9]+\.[0-9]+$' || fail 'Could not determine the latest stable release.'
asset="VibeHarder-${tag#v}-$libc-$arch-$mode.tar.gz"
printf 'Downloading %s…\n' "$asset"
fetch "$repo/releases/download/$tag/$asset" "$scratch/$asset"
fetch "$repo/releases/download/$tag/SHA256SUMS.txt" "$scratch/SHA256SUMS.txt"
expected=$(awk -v name="$asset" '$2 == name || $2 == "*" name {print $1}' "$scratch/SHA256SUMS.txt")
printf '%s\n' "$expected" | grep -Eq '^[[:xdigit:]]{64}$' || fail 'Release checksum is missing or invalid.'
printf '%s  %s\n' "$expected" "$asset" > "$scratch/checksum"
(cd "$scratch" && verify checksum) || fail 'Checksum verification failed. Nothing was installed.'
mkdir "$scratch/package"
tar -xzf "$scratch/$asset" -C "$scratch/package"
sh "$scratch/package/install.sh"
printf 'Installed %s (%s-%s). Run this script again to update.\n' "$tag" "$libc" "$arch"
printf '%s\n' 'Desktop use needs X11/XWayland, fontconfig and the native GUI libraries documented in packaging/README.md.'
