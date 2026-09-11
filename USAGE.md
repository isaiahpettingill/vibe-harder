# Usage reference

<img src="LOGO.png" alt="Vibe Harder logo" width="128">

A compact cross-platform Avalonia desktop app for Codex, Claude, and OpenCode chats, with bundled Noto Sans for UI/chat and NeoSpleen Nerd Font for code, tools, and terminals. Windows and WSL are first-class: pick a distro, browse its Linux folders, and run chats and terminal tabs there from Windows.

The logo source is [LOGO.png](LOGO.png). Code is [MIT licensed](LICENSE).

**Themes:** Original, Catppuccin Mocha, Monokai, Solarized Dark, Gruvbox, and Solarized Light, shared by chat, controls, and terminal ANSI colors.

**Remote access:** [Paired hosts, headless mode, and Android](REMOTE.md). Agents stay on their owning host when a client disconnects. Android is a remote-only client with a chat drawer and no terminal or tray mode.

## Run

Native packages contain the app, without Node.js. See [installation and packaging](packaging/README.md) for the Windows installer, Linux application-menu installation, and macOS app bundle. Sign in from the chat pane when prompted; each WSL distro has its own account and needs Node.js 22+.

Building from source requires the .NET 11 SDK pinned in `global.json` (currently 11.0.100-rc.1.26425.128) and Node.js 22+. The SDK is installed locally in `.dotnet` on this workstation; other machines can install the pinned SDK from Microsoft. You can also authenticate Codex from a shell:

```sh
npx -y @openai/codex@0.154.0 login
```

From this repository:

```sh
dotnet run --project src/CodexManager
```

On Windows, `./run.ps1` builds and launches the app. A prepared Windows build is at `artifacts/publish/win-x64-aot/VibeHarder.exe` after publishing.

## Responsiveness and activity

Workspace chevrons collapse chat lists without stopping agents. A spinner shows busy chats; a dot marks a completed reply until you open that chat. The tray tooltip reports the actual number of running local agents, and its menu lists their provider, workspace, title, and status.

The transcript virtualizes rendered rows and keeps a 200-message window. The up/down history controls fetch earlier/newer pages asynchronously; the latest control returns to the live conversation. Older history stays in SQLite, including during provider replay. Full-chat copy and history search run against the saved history in background work.

Archiving hides the chat immediately and cancels its active history loader or turn. Database writes use an ordered background writer with durable flushes before starting a provider prompt. The recovery marker is cleared only after the completed transcript is saved.

## Workflow

- The workspace-name selector shows searchable folder history, including workspaces you closed. Remove an entry with **×**; its saved chats remain intact. Missing folders disappear from history automatically when the selector opens. Unavailable WSL distributions retain their entries. Opening an empty workspace starts the last-used chat provider.
- The chat input shows the model, reasoning/variant, fast mode, and other settings reported by the selected provider. Options update when the agent changes its available configuration; unsupported options remain hidden.
- Tool output, edits, and thinking start collapsed, with short command previews and smaller gray text. Expand a section for the full content; Each expanded output keeps its collapse control inline above the scrolling content. **Copy code**, per-message **Copy**, and **Copy chat** support copying output. Select chat text and press Ctrl+C/Cmd+C to copy with formatting into applications that accept HTML.
- **Settings → Fonts** configures UI, Chat, Code, and Terminal families and sizes independently. UI/chat default to Noto Sans; code/tools and terminals default to NeoSpleen Nerd Font. **Settings → Terminal settings** chooses an installed Windows shell or a custom command through cmd.exe; Linux/macOS and each WSL distro support a shell-command override. Changes apply to new terminal tabs; agent/login commands retain their separate configuration.
- **Open workspace** automatically discovers installed WSL distros. Select Local or a distro. A single click selects a folder and updates the path; **Open selected folder** opens that folder. Double-click browses inside. With no selection, **Open current folder** opens the displayed directory. Home, Parent, Back, Enter/Right, absolute paths, filtering, and hidden folders are supported.
- Each workspace heading contains **＋** (new chat) and **×** (close workspace), with its chats underneath. Choose **Claude**, **Codex**, or **OpenCode** from **＋** to start a chat immediately. Provider SVGs identify chats; all providers stay visible together. Closing a workspace saves its drafts and history and stops its processes. Reopening the same folder/environment restores its chats.
- Opening a folder discovers previous sessions from all three providers for that folder. **Import** refreshes selected providers. Re-importing skips existing provider/session pairs and preserves archived chats. Opening an imported chat replays its transcript and resumes its original session. Search spans all open workspaces and providers.
- When an agent reports that you are signed out, **Log in to Codex**, **Log in to Claude**, or **Add provider** appears inside the chat pane. Account setup runs there in the current local/WSL environment. It uses the configured provider CLI's login command; default commands download the CLI through npm. When the command exits, idle chats reconnect and history refreshes. Account commands can be customized in connection settings, and login is always available from the command palette.
- **Ctrl+Shift+P** (Cmd+Shift+P on macOS), or **⌘** beside Settings, opens the command palette. Search for chats, terminals, login, imports, reconnect, connection settings, or global instruction/configuration files. Files open through the OS default file association; WSL files use `\\wsl.localhost\<distro>`. Discovery respects the selected environment’s `CODEX_HOME`, `CLAUDE_CONFIG_DIR`, `XDG_CONFIG_HOME`, `OPENCODE_CONFIG_DIR`, and `OPENCODE_CONFIG`, plus home-directory fallbacks. It includes Codex Markdown/TOML, Claude Markdown/JSON, and OpenCode Markdown/JSON/JSONC and its optional Claude fallback. Only existing local files appear. Environment overrides hidden inside arbitrary adapter wrappers must also be exported to the app or WSL login shell for discovery.
- Startup/load failures retry up to five times with backoff. Idle and active ACP process exits reconnect automatically; repeated immediate exits stop after four recovery cycles. **↻** reconnects a stuck server. Session IDs, partial transcripts, and in-flight input survive failures and app restarts. Uncertain prompts are not automatically resent; input and attachments are recovered for review. Chats remain saved if WSL is still unavailable.
- Enter sends; Shift+Enter inserts a newline. **Stop** or Escape interrupts a running turn. A stuck adapter is terminated after eight seconds so the chat can reconnect.
- Ctrl+V (Cmd+V on macOS) pastes images with a thumbnail and an inline `[Image #1]` reference at the cursor. It also accepts copied files and preserves text/URLs, including query strings and fragments. Ctrl+Shift+V pastes text only. **＋ Files** and drag-and-drop attach images or embed text files. Files are embedded, so a Windows file can be referenced by a WSL chat without pretending its Windows path exists in Linux. Click an attachment to remove it and its image reference before sending. Individual attachments are limited to 20 MB.
- Markdown renders headings, lists, links, code blocks, and tables. Tool activity and permission requests appear in the chat. Permission choices come from the agent; closing the dialog rejects the request.
- **Terminal** opens a pane to the right of the chat with compact tabs; drag its left edge to resize. **Ctrl+`** toggles the pane from the chat or terminal, preserving running shells and the selected tab when hidden. **＋ Terminal** starts another independent shell. Windows uses PowerShell through ConPTY; WSL uses the selected distro’s default shell in the workspace folder; Linux/macOS use `$SHELL` through a native PTY. Switching folders restores their running terminal tabs. Terminal processes end when the workspace or app closes.
- **Archive** hides a chat from the normal list without changing Codex history. Click **Chats ▾** to switch to **Archived ▾**, then **Restore** to bring it back.
- **Delete** confirms permanent removal of the app transcript, attachments, and the Codex session and its descendants. Project files are preserved. The app keeps its copy if Codex deletion fails. The current ACP adapter maps `session/delete` to archive, so permanent deletion uses `codex delete --force <UUID>` in the workspace’s environment. For Claude and OpenCode, deletion removes only the app’s copy and excludes the session from future imports; original agent history is retained.

Folders, chats, selected folder/chat, drafts, attachments, ACP session IDs, and transcripts survive restarts. Continuing an existing chat uses ACP `session/load`, preserving actual Codex context. Replayed messages are not duplicated in the app transcript.

## Configuration and data

**Connection settings** exposes separate local and WSL ACP and account commands for each provider. Defaults:

```text
Codex ACP: npx -y @agentclientprotocol/codex-acp@1.11.0
Claude:    npx -y @agentclientprotocol/claude-agent-acp@0.76.0
OpenCode:  npx -y opencode-ai@1.18.30 acp
Codex CLI: npx -y @openai/codex@0.154.0
```

Default agent commands download on first use through npm. Packaged builds bundle Node.js 22.23.2 for local agents; WSL environments need their own Node.js. Each adapter uses its provider’s authentication and configuration. The app does not store API keys. Custom commands may use installed binaries, environment variables, or wrappers. Existing saved command overrides are preserved. Use the same configuration-directory overrides for ACP, account, and deletion commands. Existing chat processes retain their command until the app restarts.

SQLite lives in the OS local application-data directory under `CodexManager/sessions.db` (on Windows, `%LOCALAPPDATA%\CodexManager`). Set `CODEX_MANAGER_DATA` to an alternate directory for isolated profiles. Back up the entire directory while the app is closed. This is ordinary local storage, not an encrypted vault.

The app supports local, WSL, and [paired remote hosts](REMOTE.md).

## Build and validation

```sh
dotnet build src/CodexManager
dotnet test tests/CodexManager.Tests --filter "FullyQualifiedName~StoreTests|FullyQualifiedName~AcpTests|FullyQualifiedName~UiTests|FullyQualifiedName~TerminalTests"
```

Windows-only WSL tests require Debian and `CODEX_MANAGER_TEST_WSL=1`. Live integration tests are opt-in with `CODEX_MANAGER_TEST_LIVE=1`; they send small prompts using your existing Codex account, test images, files, and resume in Windows and Debian, then permanently delete only their own disposable test sessions. Create `/tmp/codex-manager-smoke` inside Debian before running them.

```powershell
$env:CODEX_MANAGER_TEST_WSL = '1'
$env:CODEX_MANAGER_TEST_LIVE = '1'
dotnet test tests/CodexManager.Tests --filter 'FullyQualifiedName~LiveTests|FullyQualifiedName~UiTests'
```

Publish a stripped Native AOT app (the default):

```sh
dotnet publish src/CodexManager -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64-aot
```

Native AOT requires the target OS and its native compiler toolchain: Visual Studio C++ Build Tools on Windows, clang and zlib development headers on Linux, or Xcode command-line tools on macOS. Use `-p:PublishAot=false` for an explicit managed publish, or `-Managed` with the packaging script. Release builds optimize for size and exclude debug symbols from packages.

Other supported runtime targets: `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`. Publish and test on each target OS before distribution. Linux desktop execution needs the usual Avalonia X11/font libraries; the Linux PTY native library is bundled by Porta.Pty. macOS runtime testing is not available in this workspace.

UI tests run real Avalonia controls with the headless platform and Skia rendering, producing `artifacts/ui-chat.png` and `artifacts/ui-folders.png`. They cover workspace selection, chat drafts, search, clipboard images/text, file drops, and archive/restore. These tests do not replace native desktop interaction testing of OS dialogs and drag-and-drop.

## Components

- `AcpClient`: newline-delimited JSON-RPC, request correlation, streaming, server permission requests, and subprocess lifetime.
- `ChatRuntime`: one process per chat, new/load/prompt/cancel, transcript updates and persistence.
- `Hosts`: native/WSL argument handling, distro discovery, folder listing.
- `Store`: SQLite metadata, transcript, attachments, archive state, and atomic local deletion.
- `TerminalSession`: [SvcSystems.UI.Terminal](https://github.com/IvanJosipovic/SvcSystems.UI.Terminal) and [Porta.Pty](https://github.com/tomlm/Porta.Pty).
- [Codex ACP adapter](https://github.com/agentclientprotocol/codex-acp), [Markdown.Avalonia](https://github.com/whistyun/Markdown.Avalonia), and Avalonia 12.

## Provider documentation and font

During an active turn, Enter queues a follow-up. The queue supports editing, removal, steering through the advertised ACP extension, and sending immediately by interrupting the current turn. Escape steers the draft (or first queued message); a second Escape within 650 ms stops. Unsupported steering leaves the message intact. Queues persist across app restarts and stay paused after stopping or a failed turn.

Some adapters can start a separate turn if steering arrives just as the original finishes, without exposing a completion event. In that case the app keeps Stop available and pauses queued messages; stop or reconnect before starting another turn.

Enabled by default, Settings → “Keep agents running in the system tray” makes X hide the window while agents, terminals, and WSL connections continue in the same background process. The tray menu opens the window or quits. This is a per-user tray application, not a privileged OS service. Tray support uses Avalonia's Windows/macOS integration; Linux depends on the desktop's tray support. If a usable tray menu is unavailable, X exits normally. Permission requests reopen the window.

After a normal exit or forced termination interrupts a turn, startup offers a checkbox list of chats to resume. By default nothing is resubmitted without choosing Resume. Enable automatic resume to bypass this prompt. Continuation requests tell the agent to inspect progress before repeating actions. Stop deliberately clears that recovery marker. Sidebar and terminal widths are saved; chat rows have rename/archive actions; transcript image previews start collapsed.

Enable “Automatically resume interrupted chats when the app starts” to skip that prompt. Enable “Start Vibe Harder when I sign in” as well for recovery after a reboot; this starts after user sign-in, not before login. Both settings default off. Reconnection retries while startup services become available; existing provider authentication and permission rules still apply. Startup registration is per user (Windows Run key, macOS LaunchAgent, Linux desktop autostart). Reopening an already running copy activates its window instead of starting duplicate agents. Manual Stop remains stopped.

Icon buttons use smaller glyphs and padding; sidebar rename/archive actions appear on hover or keyboard focus. Expand/collapse controls stay inline with each output, rather than in the composer.

Type `/` in the composer for commands advertised by the connected ACP session. Up/Down selects; Enter or Tab inserts without sending; Escape dismisses. Commands are sent unchanged through `session/prompt`. `/goal` appears only when advertised; CLI-only commands are not added. The list refreshes on `available_commands_update` and clears on reconnect.

Model and thinking options use compact bottom-toolbar menus. Copy and collapse actions use icons with tooltips. Fenced code wraps within the chat width; inline code has a light foreground and normal font weight.

- [Claude ACP adapter](https://github.com/agentclientprotocol/claude-agent-acp) and [Claude CLI account commands](https://code.claude.com/docs/en/cli-reference).
- [OpenCode ACP](https://opencode.ai/docs/acp/), [account commands](https://opencode.ai/docs/cli/), [configuration](https://opencode.ai/docs/config/), and [global rules](https://opencode.ai/docs/rules/).
- [Codex global instructions](https://developers.openai.com/codex/guides/agents-md/) and [Claude configuration directories](https://code.claude.com/docs/en/claude-directory).
- [NeoSpleen](https://github.com/mbwilding/NeoSpleen) Nerd Font Regular and Bold are embedded. Font licensing notices ship under `Assets/Fonts` in published builds.

`node tools/acp-history-check.mjs <codex|claude|opencode> [workspace] [WSL-distro]` checks the real ACP handshake and paginated history without creating a session or sending a prompt.

