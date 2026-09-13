"""Validate that release APKs stay single-ABI Native AOT packages."""
import sys
import zipfile
import json
from pathlib import Path

rid, path = sys.argv[1:3]
expected = {"android-arm64": "arm64-v8a", "android-x64": "x86_64"}[rid]
with zipfile.ZipFile(path) as archive:
    names = archive.namelist()
    actual = {name.split("/")[1] for name in names if name.startswith("lib/")}
    assert actual == {expected}, f"Unexpected APK architectures: {actual}"
    assert f"lib/{expected}/libCodexManager.Android.so" in names, "Native AOT application is missing"
    forbidden = ("libcoreclr.so", "libclrjit.so", "libmonosgen-2.0.so", "libassembly-store.so", "Porta.Pty.dll")
    assert not any(name.endswith(forbidden) for name in names), "APK contains a managed runtime or local PTY dependency"
if len(sys.argv) > 3:
    libraries = json.loads(Path(sys.argv[3]).read_text(encoding="utf-8"))["libraries"]
    assert not any(name.startswith("Porta.Pty/") for name in libraries), "Mobile build references the desktop PTY provider"
print(f"Verified Native AOT APK ({expected}): {path}")
