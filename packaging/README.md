# Install Vibe Harder

- **Windows:** run `VibeHarder-<version>-win-<arch>-aot-Setup.exe`. The per-user installer creates Start menu/desktop shortcuts and an uninstaller, without administrator access. Quit from the tray before upgrading.
- **Linux:** extract the matching `.tar.gz` and run `sh install.sh`. This creates an application-menu `.desktop` entry and installs under `~/.local/share/codex-manager` (or `$XDG_DATA_HOME/codex-manager`). Desktop use requires Avalonia/Skia dependencies, fontconfig, and an X11/XWayland or Wayland session (see backend options below).
- **macOS:** unzip and drag `Vibe Harder.app` to Applications. Bundles are ad-hoc signed; Developer ID notarization requires your own Apple credentials.
- **Android:** install `VibeHarder-Android.apk`, the remote-only client for Android API 24+ (ARM64/x64). Its stable asset name works with Obtainium.

## Linux latest-release installer

Installed desktop releases also check for updates after startup and every four hours. Use **Download update**, then **Restart to update** in the status bar. Downloads are verified against GitHub's SHA-256 digest and retain your platform, architecture, and package mode. The restart saves chats and resumes active requests once, without changing the normal automatic-resume preference. Linux/macOS updates require write access to the installation's parent directory; system-owned installs must be updated by their owner. Development builds, headless hosts, and Android do not poll for updates.

```sh
curl -fsSL https://github.com/isaiahpettingill/vibe-harder/releases/latest/download/install.sh -o /tmp/vibe-harder-install.sh
sh /tmp/vibe-harder-install.sh
```

The standalone [install.sh](../install.sh) detects x64/ARM64 and glibc/musl, downloads the latest stable release, verifies the package checksum, and runs its per-user installer. It prefers Native AOT, with a bundled-runtime fallback for glibc older than 2.38. Run it again to update. It supports curl or wget and needs tar plus sha256sum or shasum. Other CPU architectures are not currently published.

The script does not change system packages. Desktop use requires X11/XWayland, fontconfig, and native Skia dependencies. On Debian/Ubuntu these include `libfontconfig1 libx11-6 libice6 libsm6 libicu-dev`; on Alpine, `fontconfig libx11 libice libsm icu-libs`. The glibc packages require glibc 2.34 or newer (for example Ubuntu 22.04+ or Debian 12+). Headless use is described in [REMOTE.md](../REMOTE.md).

The default desktop package is stripped Native AOT. `bundled` packages contain
the .NET runtime; `framework` packages require .NET 11 installed separately.
macOS releases provide both managed modes and exclude AOT.

The app and remote server do not bundle or require Node.js. Install your chosen
provider adapter separately; default npx-based adapters require Node.js in the
workspace environment (including each WSL distro). Uninstallation preserves data.

## Build

Install the .NET 11 SDK pinned in `global.json` and PowerShell 7. Windows
installers additionally require NSIS on PATH. AOT builds run on their target OS.

```powershell
./tools/package.ps1 -Runtime win-x64 -Mode aot
./tools/package.ps1 -Runtime linux-x64 -Mode bundled
./tools/package.ps1 -Runtime osx-arm64 -Mode framework
```

Alpine/musl builds use `sh tools/package-musl.sh linux-musl-x64 aot 1.0.0` inside
an Alpine .NET SDK environment with clang, build-base, zlib-dev, and icu-libs.

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

See [remote and headless setup](../REMOTE.md) for persistent pairing and Android access.

Linux uses X11/XWayland when DISPLAY is available, including Wayland desktops with XWayland. Native Wayland is selected in Wayland-only sessions; set VIBE_HARDER_LINUX_BACKEND=wayland to opt in, or VIBE_HARDER_LINUX_BACKEND=x11 to force X11. Avalonia native Wayland support is experimental; XWayland is preferred for KDE launcher and tray integration.

Android release APKs use .NET 11 Native AOT and full trimming. The mobile UI compiles the same shared Avalonia source through `CodexManager.Mobile.UI`, with desktop startup and the local `Porta.Pty` dependency excluded. The terminal renderer remains for remote shells; SQLite remains for saved connections/settings. Markdown/editor libraries retain the existing explicit trimming roots needed by their reflection-based templates. Debug Android builds remain managed for debugging. Android Native AOT is experimental in the pinned SDK; release packages are checked for a native application binary and the absence of CoreCLR, JIT, and assembly-store payloads.

Android reconnects when the activity resumes or the default network becomes available. Empty remote workspaces also poll for connectivity. A poll waiting behind a slow host operation no longer closes a healthy socket, and remote terminals retain their session IDs and output offsets while reconnecting.
