#!/bin/sh
# Exercise real process exclusion in the shipped AOT mode, across Unix sessions.
set -eu
scratch=$(mktemp -d)
owner=
cleanup() { if [ -n "$owner" ]; then kill "$owner" 2>/dev/null || true; wait "$owner" 2>/dev/null || true; fi; rm -rf "$scratch"; }
trap cleanup EXIT HUP INT TERM
case "$(uname -m)" in x86_64) rid=linux-x64;; aarch64|arm64) rid=linux-arm64;; *) exit 1;; esac
dotnet publish tests/AppInstanceProbe/AppInstanceProbe.csproj -c Release -r "$rid" --artifacts-path "$scratch/build" -o "$scratch/app" --verbosity quiet
probe="$scratch/app/AppInstanceProbe"
"$probe" "$scratch/profile" > "$scratch/owner.log" &
owner=$!
until grep -q OWNER "$scratch/owner.log"; do kill -0 "$owner"; sleep 0.1; done
timeout 5s setsid "$probe" "$scratch/profile/" > "$scratch/second.log"
grep -q SECONDARY "$scratch/second.log"
sleep 0.2
grep -q ACTIVATED "$scratch/owner.log"
kill -KILL "$owner"; wait "$owner" 2>/dev/null || true; owner=
"$probe" "$scratch/profile" > "$scratch/replacement.log" &
owner=$!
until grep -q OWNER "$scratch/replacement.log"; do kill -0 "$owner"; sleep 0.1; done
echo 'Native AOT: second launch activates owner; crash releases ownership.'
