# ACP integration review

Reviewed 2026-09-15. Optional providers remain disabled until enabled in Settings → Agents. Runtime capabilities, rather than provider names alone, determine available actions.

| Provider | Integration |
| --- | --- |
| Dirac (`dirac-cli@0.5.13 --acp`) | Standard session creation/loading, history discovery, model/provider/mode controls, boolean options, reasoning/thinking controls, streamed tools, permissions, advertised slash commands, and standard forking where advertised. Added acknowledged text steering through `dev.dirac/whisper` and checkpoint listing/restoration through its advertised extensions. |
| Pi (`pi-acp@0.0.33`) | Standard session creation/loading/history discovery, model and thinking settings, streamed tools/diffs, attachments supported by the adapter, and advertised slash commands. Pi must be installed separately. Its `/steering` command configures delivery mode; it does not advertise the generic `_session/steering` extension, so that extension is not assumed. |
| VT Code (`vtcode acp`) | Standard session creation/loading, session configuration, streamed messages/tools, and permissions. ACP environment switches are supplied to both local and WSL processes. Account setup uses `vtcode login openai`. |

Dirac's ACP session configuration currently exposes mode, approval toggles, provider/model, inference speed, reasoning effort, and thinking budget. Separate utility-model and completion-verifier settings are not exposed by that configuration interface. Goal mode is explicitly limited to the interactive CLI. No UI controls or unsupported-feature notices are added for these features.

Checkpoint restoration changes both task history and workspace files. The UI describes that effect before the user confirms. Restoration requires an idle session, verifies that the checkpoint is still listed, drops queued messages, and reconnects to replay the restored history. Merely opening the checkpoint list has no restoring side effects.

Dirac whisper accepts text, not attachments. Steering waits for the agent's queued/sent notification; uncertain acknowledgement does not trigger an automatic resend. Standard steering retains the existing request/response path for adapters that advertise it.

VT Code also has custom lifecycle methods in its source, but they are not advertised as standard session capabilities by its initialize response. The client does not assume those are standard ACP fork/rollback methods. Client-side terminal callbacks remain unadvertised. Chat connections now advertise and implement ACP filesystem reads and writes on the workspace host, including WSL paths; writes reject stale contents after an external edit.

Validation uses protocol fixtures for Dirac extensions and capability gating, plus existing session configuration/history tests. No live model credentials were used.

Sources:

- [Dirac ACP agent and extension dispatch](https://github.com/dirac-run/dirac/blob/master/cli/src/acp/AcpAgent.ts)
- [Dirac capabilities and checkpoint/steering implementation](https://github.com/dirac-run/dirac/blob/master/cli/src/agent/DiracAgent.ts)
- [Dirac session configuration](https://github.com/dirac-run/dirac/blob/master/cli/src/agent/sessionConfig.ts)
- [Dirac CLI/ACP feature boundaries](https://github.com/dirac-run/dirac/blob/master/cli/README.md)
- [Pi ACP capabilities and configuration](https://github.com/svkozak/pi-acp/blob/main/src/acp/agent.ts)
- [VT Code ACP handlers](https://github.com/vinhnx/VTCode/blob/main/crates/codegen/vtcode-acp/src/zed/agent/handlers.rs)


September 15 follow-up: VT Code 0.162.4 exposes primary agent, provider, model, and effort as ACP session configuration. A live initialize/new-session/set-provider check confirmed that switching from an unauthenticated OpenRouter default to an existing OpenAI ChatGPT login also selects an OpenAI-compatible model. No Flex/service-tier configuration is advertised by that version. Provider menus now use host-side credential availability (VT Code auth/secret status; Dirac CLI storage metadata), and new sessions restore the user's last successful per-agent settings. Dirac's initial YOLO and auto-approve defaults are on until the user selects otherwise.

Dirac 0.5.13 resumes completed persisted tasks by creating a fresh core task for the next ACP prompt. On reconnect, the client supplies the saved conversation as context once, preserving follow-up meaning without duplicating transcript rows. Live connected turns continue normally.

September 15 archive/Cline follow-up:

- Archived chat checkboxes support bulk unarchive and deletion across local and remote workspaces. Provider cleanup is best-effort: Codex uses its permanent-delete CLI, OpenCode uses `session delete`, and other adapters use `session/delete` only when advertised. Failed or unsupported cleanup does not prevent local deletion; an import tombstone prevents the same history returning. Active chats cannot be deleted. Provider-owned child sessions and worktrees may also be removed by provider deletion.
- All workspace and chat reordering uses a 500 ms hold followed by dragging, for mouse and touch. Movement before the hold cancels it so scrolling still works.
- Cline is optional in Settings → Agents, using `cline --acp` and `cline auth` on the workspace host. Standard ACP configuration supplies model, provider, Plan/Act, auto-approval, and organization choices when advertised. Existing session loading, image prompts, filesystem callbacks, and permissions are reused. Current Cline does not advertise session listing or deletion, so those capabilities are not assumed.

Sources: [Cline ACP documentation](https://docs.cline.bot/usage/acp), [Cline ACP implementation](https://github.com/cline/cline/blob/main/apps/cli/src/acp/acpAgent.ts), [OpenCode session commands](https://opencode.ai/v2/docs/cli/commands/), [Dirac session deletion](https://github.com/dirac-run/dirac/blob/master/cli/src/agent/DiracAgent.ts).
