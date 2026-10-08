# Repository rules for coding agents

Vibe Harder is a C#/Avalonia (.NET 11, native AOT) desktop app with Android and browser clients. Source is in `src/`, tests in `tests/CodexManager.Tests` (xUnit v3 with Avalonia.Headless; `fake-acp.mjs` is a scripted agent used by integration tests).

## Tests

Tests model how the system behaves as data passes through it. Write them data-oriented: the cases are data (`[Theory]`/`[AvaloniaTheory]` with `InlineData`/`MemberData`, or a table of input → expected behaviour) flowing through the real code path, with assertions on observable behaviour and output.

### Do not write these tests

1. Tests that check the contents of a source code or markup file.
2. Tests that check specific text on the screen (labels, headings, tooltips, copy, captions, status wording).
3. Tests of trivial behaviour or of a built-in or framework function; the language and framework test their own.
4. Tests that do a large amount of work to check something of little consequence.
5. Brittle UI tests of little value: layout positions, sizes, colours, styling, control counts.
6. Tests of a requirement invented for the test, with no bearing on what the product is for.
7. Tests that re-run the code under test and compare (source `a() => b(c(d()))`, test `assert a() == b(c(d()))`, and similar pairs).
8. Tests that are not data-oriented: asserting hard-coded values or copies of source code.

### Worth testing

- Critical logic, especially security configuration and sensitive data handling: pairing and trust, certificates, encryption, permission decisions, app lock, file access boundaries, credentials.
- Fast UI tests that catch common runtime errors (a view builds and handles its data without throwing).
- Contract tests for communication between important systems: the ACP protocol with agents, the host ↔ phone/browser remote protocol, push encryption, the CLI pipe.
- Fast tests of less critical behaviour that affects UX or performance, or that prevents a specific recorded bug from coming back.

### Categories and where tests run

- `[Trait("Category", "CI")]`: high-value and fast (well under two seconds, no agent process or app window flow). CI runs only these.
- `[Trait("Category", "Integration")]`: long-running flows, real processes (the app window with `fake-acp.mjs`, network servers, terminals), or anything slow. Local only.
- No category: medium-value tests. Local only.

Run everything locally with `dotnet test tests/CodexManager.Tests`. Run what CI runs with `--filter "Category=CI"`.

### Run the affected tests before finishing

When you change a system, run its tests, integration tests included, before reporting the work as done. Use `--filter "FullyQualifiedName~<Class>"` with the classes below; run the whole suite when a change spans several systems or touches shared code such as `ChatRuntime`, `Store`, `MainView`, or `RemoteView`.

| Changed area | Test classes |
|---|---|
| Agent protocol, chat runtime, streaming, tool calls, AIR extensions (`AcpClient`, `ChatRuntime*`, `AcpToolCall`, `Air`) | `AcpTests`, `AcpFileSystemTests`, `AirTests`, `AsyncTaskTests`, `StreamingTests`, `QueueTests`, `SubagentTests`, `ChatTitleTests`, `CodexAsyncQuestionTests`, `DiracExtensionTests`, `PiFailureTests`, `SlashCommandTests`, `PermissionPolicyTests`, `LiveTests` (opt-in, real agents) |
| Recovery, reconnects, idle agents, history replay and branching | `RecoveryTests`, `AutomaticRecoveryTests`, `IdleAgentTests`, `AuthenticationExpiryTests`, `HistoryBranchTests`, `HistoryReplayTests`, `RobustnessTests`, `PresentationRecoveryTests`, `DiagnosticsTests` |
| Store, persistence, history paging | `StoreTests`, `HistoryWindowTests`, `ViewMemoryTests`, `PresentationSleepTests` |
| Transcript panel, scrolling, message rendering | `TranscriptScrollingTests`, `TranscriptOutlineTests`, `TranscriptGroupingTests`, `TranscriptProgressTests`, `ChatPresentationTests`, `ChatMarkdownEngineTests`, `ChatMediaTests`, `ResponsivenessTests`, `ChatActivityTests`, `ChatSearchTests`, `MessageProviderTests`, `MessageTimeTests`, `LinkAndModelTests`, `VisualPerformanceTests` |
| Composer, questions, approvals | `ComposerInputTests`, `ComposerUiTests`, `ElicitationFormTests`, `ChatInteractionTests`, `InteractionReviewTests` |
| Remote hosts, pairing, phone and browser clients (`Remote*`, `SessionService`, `WebAccess`) | `RemoteTests`, `RemotePairingTests`, `RemoteDesktopTests`, `RemoteOfflineTests`, `RemoteDownloadsTests`, `RemoteTranscriptScrollingTests`, `MobileStartTests`, `MobileChatLayoutTests`, `MobileInteractionTests`, `WebAccessTests`, `RemoteCatalogTests`, `RemoteQueueMenuTests` |
| Notifications and push | `NotificationTests`, `WebPushTests`, `UnifiedPushHostTests` |
| Android app lock | `MobileAppSecurityTests` |
| Terminals | `TerminalTests`, `TerminalRecoveryTests`, `TerminalInputTests`, `TerminalLinkTests`, `TerminalFileLinkTests`, `WslShellTests`, `TerminalAppearanceTests` |
| Providers, installation, updates, launch configuration | `ConfigurationTests`, `ProviderAuthenticationTests`, `AgentInstallationTests`, `AdditionalAgentTests`, `BackendUpdateTests`, `DesktopUpdaterTests`, `VtCodeLaunchTests`, `SessionConfigTests` |
| Workspaces, sidebar, settings, tray, startup, CLI | `WorkspaceHistoryTests`, `WorkspaceLaunchTests`, `ArchiveSelectionTests`, `SidebarCustomizationTests`, `SettingsUiTests`, `TrayStartupTests`, `AppInstanceTests`, `CliTests`, `UiTests`, `NewFolderTests` |

When you add a test class, add it to this table.

## Work habits

- Match the surrounding code: its comment density, naming, and idiom.
- Commit in reasonable checkpoints once a change builds and its tests pass. Never add AI attribution (co-author trailers, "generated with" footers) to commits, pull requests, or release notes.
