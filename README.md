# Vibe Harder

<img src="LOGO.svg" alt="Vibe Harder" width="64">

Use Codex, Claude, and OpenCode across your computers.

## Install

**Windows — paste into PowerShell:**

```powershell
$r = Invoke-RestMethod https://api.github.com/repos/isaiahpettingill/vibe-harder/releases/latest; $a = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_IDENTIFIER -match 'ARM') { 'arm64' } else { 'x64' }; $u = $r.assets | Where-Object name -Like "*-win-$a-aot-Setup.exe"; $p = Join-Path $env:TEMP $u.name; Invoke-WebRequest $u.browser_download_url -OutFile $p; Start-Process $p
```

**Linux — paste into your shell:**

```sh
curl -fsSL https://github.com/isaiahpettingill/vibe-harder/releases/latest/download/install.sh -o /tmp/vibe-harder-install.sh && sh /tmp/vibe-harder-install.sh
```

**macOS:** [Download](https://github.com/isaiahpettingill/vibe-harder/releases/latest) the `osx-arm64-bundled.zip` (Apple Silicon) or `osx-x64-bundled.zip` (Intel), unzip, and move **Vibe Harder.app** to Applications.

**Android:** [Download and install VibeHarder-Android.apk](https://github.com/isaiahpettingill/vibe-harder/releases/latest/download/VibeHarder-Android.apk), then [pair your computer](REMOTE.md).

## Start chatting

1. Install your chosen agent: Codex, Claude Code, or OpenCode. Codex and Claude ACP bridges also require Node.js 22+ in the workspace environment.
2. Open Vibe Harder and choose **Open workspace** to select a folder.
3. Create a chat, choose your agent, and sign in when prompted.
4. Type a message and send it.

To open the current folder from a new terminal:

```sh
vibe-harder .
```

On Linux, add `~/.local/bin` to PATH if needed. On macOS, first run `sh "/Applications/Vibe Harder.app/Contents/MacOS/install-cli.sh"`.

## Use another computer

Follow [Remote setup](REMOTE.md) to pair a desktop, Android device, or browser with your host. For browser access, enable the web UI in the host's settings.

## Update

On desktop, use **Check for updates** in the bottom-right corner, then **Download update** and **Restart to update**. On Android, install the latest APK.

[More usage help](USAGE.md) · [Installation details](packaging/README.md)
