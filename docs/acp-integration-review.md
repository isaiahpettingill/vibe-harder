# ACP integration review

Reviewed 2026-09-15. Optional providers remain disabled until enabled in Settings → Additional agents. Runtime capabilities, rather than provider names alone, determine available actions.

| Provider | Integration |
| --- | --- |
| Dirac (`dirac-cli@0.5.13 --acp`) | Standard session creation/loading, history discovery, model/provider/mode controls, boolean options, reasoning/thinking controls, streamed tools, permissions, advertised slash commands, and standard forking where advertised. Added acknowledged text steering through `dev.dirac/whisper` and checkpoint listing/restoration through its advertised extensions. |
| Pi (`pi-acp@0.0.33`) | Standard session creation/loading/history discovery, model and thinking settings, streamed tools/diffs, attachments supported by the adapter, and advertised slash commands. Pi must be installed separately. Its `/steering` command configures delivery mode; it does not advertise the generic `_session/steering` extension, so that extension is not assumed. |
| VT Code (`vtcode acp`) | Standard session creation/loading, session configuration, streamed messages/tools, and permissions. ACP environment switches are supplied to both local and WSL processes. Account setup uses `vtcode login`. |

Dirac's ACP session configuration currently exposes mode, approval toggles, provider/model, inference speed, reasoning effort, and thinking budget. Separate utility-model and completion-verifier settings are not exposed by that configuration interface. Goal mode is explicitly limited to the interactive CLI. No UI controls or unsupported-feature notices are added for these features.

Checkpoint restoration changes both task history and workspace files. The UI describes that effect before the user confirms. Restoration requires an idle session, verifies that the checkpoint is still listed, drops queued messages, and reconnects to replay the restored history. Merely opening the checkpoint list has no restoring side effects.

Dirac whisper accepts text, not attachments. Steering waits for the agent's queued/sent notification; uncertain acknowledgement does not trigger an automatic resend. Standard steering retains the existing request/response path for adapters that advertise it.

VT Code also has custom lifecycle methods in its source, but they are not advertised as standard session capabilities by its initialize response. The client does not assume those are standard ACP fork/rollback methods. Client-side filesystem and terminal capabilities remain unadvertised because this client does not implement those callbacks.

Validation uses protocol fixtures for Dirac extensions and capability gating, plus existing session configuration/history tests. No live model credentials were used.

Sources:

- [Dirac ACP agent and extension dispatch](https://github.com/dirac-run/dirac/blob/master/cli/src/acp/AcpAgent.ts)
- [Dirac capabilities and checkpoint/steering implementation](https://github.com/dirac-run/dirac/blob/master/cli/src/agent/DiracAgent.ts)
- [Dirac session configuration](https://github.com/dirac-run/dirac/blob/master/cli/src/agent/sessionConfig.ts)
- [Dirac CLI/ACP feature boundaries](https://github.com/dirac-run/dirac/blob/master/cli/README.md)
- [Pi ACP capabilities and configuration](https://github.com/svkozak/pi-acp/blob/main/src/acp/agent.ts)
- [VT Code ACP handlers](https://github.com/vinhnx/VTCode/blob/main/crates/codegen/vtcode-acp/src/zed/agent/handlers.rs)
