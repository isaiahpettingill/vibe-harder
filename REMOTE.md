# Connect your computers

Agents run on the computer that owns the workspace. Closing Android or another remote client does not stop them.

## Pair once

1. Keep the desktop app running.
2. On Android, enter the computer's address and tap **Connect**. On desktop, open a workspace and choose **Remote computer…** under **Choose where Codex works**. The same pairing flow is also available in **Settings → Remote hosts and server**.
3. A popup on the host shows a six-digit number. Enter that number on the connecting device and tap **Pair**.

Use a hostname such as `my-desktop`, a Tailscale MagicDNS name such as `my-desktop.tail123.ts.net`, or an IPv4/IPv6 address. Hostnames are resolved by the operating system; the field does not require an IP address. Add `:port` when using a non-default port, for example `my-desktop:3333` or `[::1]:3333`.

The number expires after two minutes and allows one attempt. Request a new number after an incorrect entry. Closing the desktop popup cancels that request. Connections are saved and Android reconnects on launch or return from the background. Existing Android connections and the selected theme migrate on upgrade.

The desktop listens on port **2222** by default. **Allow my other devices to connect** controls incoming connections; an existing disabled setting stays disabled. The port is under **Advanced**. You do not need to enter the host's own address in its settings.

Both devices must be able to reach the host and port. Allow that port through the host firewall for your LAN or VPN. For Tailscale, connect both devices to the tailnet and enable MagicDNS. There is no public relay.

## Shared interface

Desktop and Android load the same Avalonia application, `MainView`, remote chat view, themes, Markdown renderer, and message controls from `CodexManager.UI`. Android's activity only hosts that shared view and forwards lifecycle events.

Use **☰ Chats** to open or collapse the sidebar. On narrow screens it opens over the chat and closes after choosing a chat; wider screens use a resizable sidebar. Desktop remembers a manually collapsed sidebar. Android hides terminal controls, local agent setup, and system tray settings. Remote agents continue running on the host.

## Headless host

```sh
VibeHarder --headless --listen 0.0.0.0 --port 2222 --pair
```

With `--pair`, a connection request prints its six-digit number to the host console. Omit `--pair` on later starts to allow only already-paired devices. A separate profile can be selected with `CODEX_MANAGER_DATA`.

## Connection security

Numeric pairing uses SRP-6a with a 3072-bit group and SHA-256, bound to the host's TLS certificate. The number is not sent over the connection. Successful pairing pins that certificate and saves a random device credential; the host stores only the credential hash. Pairing is limited to one pending request and five requests per minute.

Revoking a device under **Paired devices** blocks new requests and disconnects idle connections within five seconds. Paired devices can operate the host's agent workspaces. A changed host certificate requires pairing again. Existing saved credentials remain compatible with this update.
