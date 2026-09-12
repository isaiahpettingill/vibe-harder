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
