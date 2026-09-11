# Install Vibe Harder

- **Windows:** run `VibeHarder-<version>-win-<arch>-aot-Setup.exe`. The per-user installer creates Start menu/desktop shortcuts and an uninstaller, without administrator access. Quit from the tray before upgrading.
- **Linux:** extract the matching `.tar.gz` and run `sh install.sh`. This creates an application-menu `.desktop` entry and installs under `~/.local/share/codex-manager` (or `$XDG_DATA_HOME/codex-manager`). Desktop use requires Avalonia/Skia dependencies, fontconfig, and an X11/XWayland session.
- **macOS:** unzip and drag `Vibe Harder.app` to Applications. Bundles are ad-hoc signed; Developer ID notarization requires your own Apple credentials.
- **Android:** install `VibeHarder-Android.apk`, the remote-only client for Android API 24+ (ARM64/x64). Its stable asset name works with Obtainium.

The default desktop package is stripped Native AOT. `bundled` packages contain
the .NET runtime; `framework` packages require .NET 11 installed separately.
macOS releases provide both managed modes and exclude AOT.

Node.js/npm are bundled on Windows, macOS, and glibc Linux. Musl Linux uses system
Node.js/npm: install with `apk add nodejs npm`. WSL distros need their own Node.js.
Agent adapters download on first use and authenticate in the workspace's own
environment. Uninstallation preserves saved sessions and settings.

## Build

Install the .NET 11 SDK pinned in `global.json`, Node.js, and PowerShell 7. Windows
installers additionally require NSIS on PATH. AOT builds run on their target OS.

```powershell
./tools/package.ps1 -Runtime win-x64 -Mode aot
./tools/package.ps1 -Runtime linux-x64 -Mode bundled
./tools/package.ps1 -Runtime osx-arm64 -Mode framework
```

Alpine/musl builds use `sh tools/package-musl.sh linux-musl-x64 aot 1.0.0` inside
an Alpine .NET SDK environment with clang, build-base, zlib-dev, Node.js, and npm.

## GitHub releases

The manually triggered **Release** workflow builds x64/ARM64 Windows and
glibc/musl Linux in `aot`, `bundled`, and `framework` modes. It also builds both
managed macOS modes and the Android APK. Windows assets are NSIS installers.

Android requires `ANDROID_KEYSTORE_BASE64` and `ANDROID_KEYSTORE_PASSWORD`
repository secrets, with signing alias `codexmanager`. Preserve that signing
identity for every update.

Choose **Actions → Release → Run workflow**, supply a numeric `major.minor.patch`
version, and wait for all builds. Only after every platform succeeds does the
workflow publish a release with all assets and `SHA256SUMS.txt`.

See [remote and headless setup](../REMOTE.md) for SSH access and Android pairing.
