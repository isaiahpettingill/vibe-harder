# Vibe Harder

<img src="LOGO.png" alt="Vibe Harder" width="96">

Your agent chats, across your computers. Windows, Linux, macOS, and an Android remote client.

[**Download**](https://github.com/isaiahpettingill/vibe-harder/releases/latest) · [Remote setup](REMOTE.md) · [Build & install](packaging/README.md) · [Usage](USAGE.md)

- **Codex, Claude, OpenCode:** streaming, model controls, queues, and ACP slash commands.
- **Local, WSL, or remote workspaces:** agents stay running on their host when you disconnect.
- **Pair once:** paste a connection code; saved devices reconnect without host access.
- **Tray and recovery:** keep working in the background; choose prompted or automatic resume.
- **Comfortable chat:** collapsible tools, styled copying, resizable panes, themes, and separate fonts.
- **Optional UI sleep:** enable in Settings to freeze inactive windows after two seconds; off by default. Inactive transcripts unload after a two-second grace period. Agents keep running.
- **Small native app:** Native AOT on Windows/Linux. No bundled Node.js or browser engine.

Android phones should use `VibeHarder-Android.apk` (ARM64). Intel Android devices and x64 emulators have a separate `VibeHarder-Android-x64.apk`, so each download contains only its own runtime.

Android **Settings → App security → Enable biometric unlock** optionally requires authentication on launch and when returning from the background. Android handles fingerprint/strong face authentication and device PIN, pattern, or password fallback; Android 6–8 use the device screen lock. Enabling and disabling the lock both require authentication. Screenshots remain available while unlocked; backgrounding still covers the app with its lock screen. This protects access to remote sessions through the app UI; it does not add encryption to stored chats or pairing keys.

Desktop releases check GitHub at startup and every four hours. When a newer matching package is available, a **Download update** button appears in the status bar. After download and checksum verification, **Restart to update** installs it, reopens your chats, and resumes requests that were active at restart. Nothing appears when you are current or a check fails. Android has no built-in updater.

With paired computers, **Open workspace** separates **This computer** from each remote host. Remote folder browsing offers that host's filesystem and WSL distributions; remote workspaces are never saved as local workspaces or used to hop through another remote host.

Switching to a local chat keeps remote workspaces in the sidebar and preserves remote drafts. Inactive remote views pause polling until reopened. Interrupted live Codex turns automatically recover from network/adapter disconnections, reload the same session, and continue from saved history with retry backoff. Authentication errors and explicit Stop require user action; the startup auto-resume preference remains separate.

Linux releases use a profile file lock to prevent duplicate instances, including Native AOT builds. Additional launches activate the existing window. Its X11/XWayland class matches the installed launcher, and reinstalling preserves custom desktop-entry icons and actions. If KDE already created an unmatched task-manager pin with an older build, remove that pin once and pin the installed **Vibe Harder** application again.

Ordinary UI failures trigger a partial chat-view reload while preserving running agents and unsent drafts. Repeated failures leave a **Reload chat** screen instead of repeatedly rebuilding the view. Startup failures offer a retry screen. Resource exhaustion and memory-corruption failures remain unrecoverable.

For troubleshooting, use **Settings → Open diagnostic logs** on desktop or **Copy diagnostic log** in connection settings. Error logs include stack traces, rotate at 512 KiB, and retain one previous file. Expected UI cancellation is contained; unexpected process failures are recorded for diagnosis.

### Start

On Linux (x64/ARM64, glibc or musl), install or update to the latest release:

```sh
curl -fsSL https://github.com/isaiahpettingill/vibe-harder/releases/latest/download/install.sh -o /tmp/vibe-harder-install.sh
sh /tmp/vibe-harder-install.sh
```

The script selects a compatible package, verifies its SHA-256 checksum, and installs for your user without sudo or a .NET SDK. It prefers Native AOT and uses the bundled runtime on older glibc distributions. A graphical session and native GUI libraries are required to use the desktop app; see [Linux requirements](packaging/README.md).

1. Install the package for your platform.
2. Open a local folder, WSL workspace, or [pair a remote computer](REMOTE.md).
3. Configure an installed provider adapter and sign in when prompted.

Providers are installed separately. Codex and Claude use `npx` and require your own Node.js 22+ installation. OpenCode uses `opencode acp` directly. Vibe Harder bundles neither Node.js nor agents.

### Build

Install the SDK in `global.json`, then:

```sh
dotnet run --project src/CodexManager -p:PublishAot=false
```

[MIT license](LICENSE). App logo: [LOGO.png](LOGO.png).
