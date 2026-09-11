# Verification — 2026-09-11

Automatic recovery follow-up: 10 targeted lifecycle/queue/composer/streaming tests passed, followed by 2 startup-command/single-instance tests. Automatic restart resumes an interrupted fixture without presenting the recovery dialog; opting out still prompts; manual Stop has no recovery marker. Second instances signal the owner through a current-user pipe. Startup registration remains opt-in and wasn't enabled on the user's machine by these tests. Windows native AOT build validates the implementation; macOS LaunchAgent and Linux autostart registration have not been run on native desktops.

Queue/lifecycle follow-up: targeted UI, store, queue, and streaming run passed 21 tests; subsequent changed lifecycle/composer/queue/recovery run passed 9 tests. Covered advertised steering without cancelling the owning turn, Enter-to-queue, double Escape stopping while preserving queued input, FIFO dispatch, queued input surviving restart, sidebar title/archive persistence, pane width persistence, and X-close leading to a startup recovery dialog without automatic resubmission. Inspected `artifacts/ui-composer-queue.png`. Tray uses native Avalonia integration and falls back to normal close when no tray menu exporter is available; native tray interaction and macOS behavior were not exercised in this environment.

Connection/streaming follow-up: Debian's real adapter returned `-32603` with `data.details` reporting a missing rollout for the failed empty chat; login status was authenticated. A separate `session/new` probe succeeded without submitting a prompt. ACP errors now expose their detail. Locally created sessions are marked until first prompt dispatch, allowing missing, never-prompted Codex sessions to recover without replacing established history. Nine targeted StreamingTests / RecoveryTests passed, including partial Markdown visible before prompt completion, real mouse clicks on icon-button padding, and refusal to replace an unmarked missing session. Icon controls now use Button's style key and 30-pixel minimum hit areas.

Follow-up: slash-command reception, filtering, unchanged prompt dispatch, session isolation, keyboard completion without sending, toolbar, and icon copying passed the focused SlashCommandTests / UiTests / ChatPresentationTests run (16 tests). SessionConfigTests also passed. Windows stripped .NET 11 AOT packaging succeeded; the installer exited 0 and the reopened app responded with its window title. Existing third-party Markdown trimming and Avalonia COM warnings remain. Goals use only advertised ACP slash commands. Point-in-history fork/rewind was not added in this follow-up. Linux/macOS packages were not rebuilt for this follow-up.

## Workspace history, presentation, session options, and settings

- Workspace history lists previously opened folders, including closed workspaces, with search and removal. Missing local folders are pruned; WSL launch failures are treated as unknown instead of deletion. Removing history preserves chats. Explicitly opening an empty workspace starts the last-used provider (Codex before any choice).
- Windows terminal choices detect installed pwsh, Git Bash, MSYS2, Cygwin, Windows PowerShell, cmd, and Nushell. Custom commands execute through cmd. Unix environments use the system shell or a configured command, with separate WSL distro preferences. Actual quoted Windows custom-shell and Debian PTY round trips passed.
- UI, Chat, and Code font settings default to embedded NeoSpleen Nerd Font and save independently. Code fonts also apply to terminal controls. Settings save/default checks passed.
- A read-only replay of an existing OpenCode session succeeded both at the protocol level and through the real Avalonia window. No prompt or cancellation was sent. Stop now represents an actual prompt, not history loading or reconnecting.
- Code blocks replace the upstream overlapping header with a separate header and visible copy button; editor bounds and clipboard content were checked. Message and whole-chat copying provide HTML alongside plain text. Selected bold text retains `<strong>` formatting. Native clipboard formats are CF_HTML on Windows, public.html on macOS, and text/html on Linux; rich paste in external editors still depends on the receiving app.
- Tool output, edits, and thinking start collapsed. Expanded details use a bounded scroll area; a fixed composer action collapses expanded details from any chat scroll position. Command previews truncate at 100 characters; full inputs remain available when expanded, in smaller gray text.
- Session setup/update responses populate provider-supplied model, reasoning/variant, fast-mode, and other configuration controls. Select and boolean changes are sent to the agent, and returned dependent options replace the displayed state. Legacy models/modes remain supported. Three provider fixtures passed configuration-change checks; unsupported options are not fabricated.
- Integrated UI/recovery checks passed 19 cases, followed by five targeted settings/presentation/configuration checks. Separate workspace, terminal, and read-only OpenCode replay checks also passed. Screenshots of chat/compact layouts and the small terminal-settings pane were inspected.

## Login links and terminal clipboard

- Debian user `isaiahjp` reported signed out through both Bash and the default Zsh shell; no credentials were changed during diagnosis.
- Login output exposes its full URL in a selectable field with browser and copy actions. URL extraction preserves query/fragment encoding across PTY read boundaries and ANSI styling. Captured login output stays in memory.
- Terminal Ctrl+V pastes; Ctrl+C copies a selection, and Ctrl+Shift+C copies without interrupting the process. Context-menu copy/paste/select-all actions are available. With no selection, ordinary Ctrl+C still interrupts the terminal process.
- “Already signed in? Check again” stops the pending login command and reconnects to refresh provider authentication.
- Three focused test cases passed, including an actual fixture login PTY, exact URL clipboard content, terminal keyboard copy/paste, and authentication recheck.

## .NET 11 and stripped Native AOT

- Installed SDK `11.0.100-rc.1.26425.128` in the repository's `.dotnet` directory on Windows and `/usr/share/dotnet` in Debian WSL. `global.json` selects it on both platforms. App, tests, launcher, documentation, and CI use .NET 11.
- All 27 non-live test cases passed on .NET 11. Protocol JSON now uses explicit nodes; stored JSON uses source-generated metadata; application bindings compile ahead of time.
- Native AOT and symbol stripping are the publish/package defaults, with size optimization. `-Managed` is an explicit packaging opt-out. Packages exclude `.pdb`/`.dbg` files and have separate AOT/managed names.
- A populated native Linux launch initially exposed missing Markdown theme metadata. Preserving the Markdown/AvaloniaEdit renderer assemblies fixed it. `tools/check-native-linux.sh` checks for binding errors and renders `artifacts/native-linux11.png`; headings, links, highlighted code, tables, workspace hierarchy, and a recovered draft were visually verified.
- Third-party Markdown trim/AOT warnings remain visible rather than suppressed. The renderer assemblies are preserved specifically because their current templates and plugin discovery use reflection. Avalonia Win32 also reports COM callback analysis diagnostics; the native Windows app opens its desktop window and closes successfully in the isolated startup check.
- Windows NSIS and Linux `.tar.gz` outputs use .NET 11 AOT. The Linux installer was exercised in an isolated XDG directory, including desktop entry/icon, bundled Node/npm/npx, and Xvfb startup. Native macOS AOT build/signing/validation requires the configured macOS runner and has not run here.
- Earlier .NET 10 artifacts were moved to `C:/Users/isaia/AppData/Local/CodexManager-build-backup-20260911` to free K: space. They are historical outputs, not the current AOT packages.

## Workspace hierarchy, clipboard, and distribution update

22 distinct targeted cases passed across UI, store, terminal, configuration, and ACP checks. These were focused runs rather than the full suite. The final additional checks cover shutting down during workspace closure and choosing `npx.cmd` on Windows without changing execution policy.

- Workspace headers contain their chats and a Claude/Codex/OpenCode new-chat menu. All three providers import into the same list without filtering each other out.
- Closing/reopening a workspace preserves provider history and drafts across app restarts. Closing a running workspace while exiting the app recovers its interrupted input.
- Ctrl+V bitmap paste adds a thumbnail and inline image reference. Text/URL paste preserves selected-text replacement, percent escapes, query parameters, and fragments. File drop, archive/restore, and draft switching pass.
- Authentication notifications show the login action in the chat pane. Fixture login commands run there without opening a separate terminal tab.
- Terminal tabs use compact headers in the right-hand pane. Screenshots `artifacts/ui-chat.png`, `ui-compact.png`, and `ui-terminal.png` were rendered and visually inspected.
- Windows NSIS setup compiled without warnings. Bundled Node, npm, and npx report their versions successfully. The installer was not run against the user's actual installed profile.
- Linux installation was exercised under a temporary XDG directory containing spaces. Desktop entry/icon creation, preserved npm/npx symlinks, bundled runtime execution, and native app startup under Xvfb passed.
- A macOS ARM64 `.app` archive was cross-built with Info.plist, ICNS icon, executable, .NET, and Node. Native macOS execution, Developer ID signing, and notarization have not been performed.

Packaging entry point: `tools/package.ps1`. Linux installation smoke check: `tools/check-linux-package.sh`. Output installers are under `artifacts/packages`. Real account sign-in and paid prompts were not exercised in this update.

## Earlier verification on the same date

The compact UI/provider/recovery update was validated with 24 targeted test cases across UI, ACP, store, recovery, configuration resolution, and terminal tests. The main selected run passed 21 tests; additional migration and concurrent-draft recovery tests passed separately. After the last UI/import/recovery changes, their affected tests were rerun and passed. WSL-only test methods remained opt-in during this run; the separate real backend probes below did run in Debian.

- Single-click folder selection opens the child; manual path edits clear stale selection.
- Automatic Codex discovery, Claude/OpenCode imports, provider persistence, paginated discovery, deduplication, and imported replay pass.
- Startup retry, idle/mid-turn ACP exit recovery, no automatic prompt resubmission, authentication notifications, and restart recovery of input/attachments pass.
- Recovery retains a new draft typed during a connecting request alongside the interrupted input, including after switching chats or restarting.
- Command-palette keyboard filtering, provider creation, and account-command execution in an integrated native terminal pass using disposable fixtures.
- Configuration overrides, XDG fallback behavior, WSL path isolation, default-editor launch arguments, and login-command routing pass.
- Existing SQLite databases migrate provider and recovery fields without changing saved Codex sessions or drafts.
- Real read-only ACP handshake/history probes passed for Codex 1.11.0 and Claude 0.76.0 on Windows and Debian WSL, and OpenCode 1.18.29 on Windows. The Debian OpenCode probe returned exit 127: OpenCode is not installed there. No prompts were sent or sessions created by these probes.
- Rendered `artifacts/ui-chat.png` and `artifacts/ui-compact.png` were visually reviewed at 1220×840 and 840×620, including bundled NeoSpleen, composer spacing, and code-block fonts. The compact composer controls fit inside their container.
- The Windows self-contained package was rebuilt at `artifacts/publish/win-x64/CodexManager.exe`. A native startup smoke check with an isolated data directory found the app window and closed it gracefully with exit code 0. Font licenses are present in the package; font binaries are embedded.

Commands used include scoped `dotnet format`, filtered `dotnet test tests/CodexManager.Tests --no-restore`, and `node tools/acp-history-check.mjs <provider> [workspace] [distro]`.

Default editor handoff was checked at the process-argument level; tests did not open or modify personal configuration files. Real interactive provider sign-ins and new paid prompts were not exercised. The existing Linux artifact below predates this update.

## Previous verification — 2026-09-10

The Windows self-contained release was launched successfully and exposes a responding `Codex Manager` desktop window. A Linux x64 self-contained release was also built.

| Area | Verification | Result |
| --- | --- | --- |
| Windows ACP | App sends prompts using the pinned adapter; streams Markdown replies | Passed, live Codex |
| WSL ACP | Same app flow launches in Debian with a Linux working directory | Passed, live Codex |
| Images and references | Codex identifies a generated blue image and reads the word from an embedded text file | Passed on Windows and WSL |
| Resume | Close the app, restart, and recall the prior word with the same ACP session ID and no duplicated replay | Passed on Windows and WSL |
| Interruption | Stop a live streaming turn through the app | Passed on Windows and WSL |
| Permanent deletion | Delete disposable test sessions using Codex CLI, then verify ACP cannot load them | Passed on Windows and WSL |
| Archive/restore | Hide chats in normal view, view archived chats, restore them; persist archive state | Passed |
| Folder picker | Discover Debian automatically, open a Linux path, navigate to its parent, list subfolders | Passed |
| Persistence | Reopen SQLite; preserve workspaces, distro, session IDs, draft attachments, ordered streamed transcript | Passed |
| Clipboard and drop | Paste a bitmap and plain text through Avalonia key input; deliver a file-drop event | Passed on Windows and Linux headless platforms |
| Markdown layout | Render real Avalonia controls through Skia and visually inspect PNGs | Passed |
| RPC failure handling | Concurrent response correlation, permission response, process failure, cancellation | Passed on Windows and Linux |
| Native terminals | Input/output round trip and PTY resize | Passed on Windows, Debian WSL, and Linux |
| Terminal tabs | Separate terminal controls/shells survive workspace switching and close with the app | Passed on Windows and Linux |

Focused commands were run against `tests/CodexManager.Tests`, using filters for the changed areas. Live tests require `CODEX_MANAGER_TEST_LIVE=1`; WSL-specific tests require `CODEX_MANAGER_TEST_WSL=1`. The live tests create and delete only their own temporary sessions.

Artifacts:

- `artifacts/publish/win-x64/CodexManager.exe`
- `artifacts/publish/linux-x64/CodexManager`
- `artifacts/ui-chat.png`
- `artifacts/ui-folders.png`

Limits: macOS runtime behavior was not tested. The Windows computer-use execution tool was unavailable; OS-level mouse/keyboard testing of native file dialogs and drag/drop was not performed. Headless UI input tests and live backend tests cover those flows at the application level. CI includes Windows, Linux, and macOS jobs, but this workflow has not run remotely.


## Vibe Harder branding and remote release validation (2026-09-11)

- Built the stripped Windows x64 Native AOT package and NSIS installer with the Vibe Harder executable, title, and root LOGO.png-derived icons.
- Ran an isolated native headless SSH probe: pinned host key, authorized client key, workspace creation, and chat catalog round trip passed. No provider prompt was sent.
- Eight focused remote authentication/disconnect, lifecycle, settings/font, and terminal appearance tests passed after branding. Earlier isolated Debian zsh clear/resize testing passed with one live prompt.
- Android Release compiled successfully and an emulator launch was checked. Android owns no agents; desktop/headless hosts retain them when clients disconnect.
- Remote tests use fake agents. Real provider billing, arbitrary shell configurations, actual operating-system restarts, and physical Android devices have not been exhaustively tested.


## Responsiveness update

- Locked-database test confirms UI-side saves return without waiting, and flushes preserve ordering.
- A 1,500-message fixture remains bounded to 200 recent messages; older/newer pages and full-history copy are verified.
- Visible transcript row count stays bounded while scrolling. Recycled message controls detach event subscriptions.
- A hanging history loader is cancelled by Archive; the sidebar updates immediately and the tray does not misreport history loading as a running agent.
- Completion flags persist; explicit cancellation does not create an unread completion.
- Fifteen focused recovery, paging, lifecycle, and chat interaction tests passed during integration.
