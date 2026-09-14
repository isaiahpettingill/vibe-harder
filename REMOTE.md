# Connect your computers

Agents run on the computer that owns the workspace. Closing Android or another remote client does not stop them.

## Pair once

1. Keep the desktop app running. In **Settings → Connections**, click **Pair a new device** to enable new pairing requests for two minutes.
2. On Android, enter the computer's address and tap **Connect**. On desktop, use **＋ Open workspace → Open folder…** and choose **Remote computer…** under **Choose where Codex works**. The same pairing flow is also available in **Settings → Connections**.
3. A popup on the host shows a six-digit number. Enter that number on the connecting device and tap **Pair**.

Use a hostname such as `my-desktop`, a Tailscale MagicDNS name such as `my-desktop.tail123.ts.net`, or an IPv4/IPv6 address. Hostnames are resolved by the operating system; the field does not require an IP address. Add `:port` when using a non-default port, for example `my-desktop:3333` or `[::1]:3333`.

The number expires after two minutes and allows one attempt. Request a new number after an incorrect entry. Closing the desktop popup cancels that request. Connections are saved and Android reconnects on launch or return from the background. Existing Android connections and the selected theme migrate on upgrade.

The desktop listens on port **2222** by default. **Allow my other devices to connect** controls incoming connections; an existing disabled setting stays disabled. The port is under **Advanced**. You do not need to enter the host's own address in its settings.

Both devices must be able to reach the host and port. Allow that port through the host firewall for your LAN or VPN. For Tailscale, connect both devices to the tailnet and enable MagicDNS. There is no public relay.

## Web access (iPhone, iPad, and other browsers)

On the desktop, open **Settings → Connections → Enable web access**.
The app downloads `VibeHarder-Web.zip` from the GitHub release matching its installed
version, verifies its SHA-256 digest, and caches it locally. No web assets are
downloaded while this setting is off. A failed download offers **Apply / Retry**;
it does not start a server with an incomplete bundle.

Open the displayed HTTPS address or scan its QR code. The web port defaults to
**2223**, separate from the native remote port. Use the computer's LAN IP or
Tailscale MagicDNS name if necessary, and allow that port through its firewall.
Your phone must be able to reach the computer over the LAN or VPN. No purchased
domain, public relay, Mac, or iOS signing is required.

Click **Pair a new device** on the host before connecting a new browser. The web listener uses a self-signed certificate. Accept the browser's certificate
warning before pairing with the six-digit code shown by the desktop. The page and
its WebSocket connection use the same origin. Browser certificate-exception
behavior varies; iOS Safari must be checked on the actual device. This is not a
programmatic bypass of browser certificate validation.

The browser runs the shared Avalonia UI in WebAssembly and connects to the machine
serving the page. To use another host, open that host's web address. Chats and agents
remain on the host; paired credentials, preferences, and remote drafts stay in that
browser's local storage. Clearing site data forgets the pairing. Revoking a paired
device also revokes its web access. GitHub distributes the assets and receives no
chat traffic. Existing provider connections are unchanged.

Turning web access off stops its listener and connected web clients while leaving
host-owned agents and native remote connections running. Cached assets remain for
the next enable. After a desktop update, enabling or starting web access obtains the
matching bundle. Headless mode honors the same saved web settings; `--pair` prints
the browser's pairing number to the console.

### Build the web assets

The browser uses .NET 10 with the same Avalonia 12 UI source; its graphics libraries
require the matching Emscripten toolchain. With the repository SDK:

```sh
dotnet workload install wasm-tools-net10 --skip-manifest-update
pwsh tools/package-web.ps1 -Version 1.2.3
```

For development, set `VIBE_HARDER_WEB_ASSETS` to the published `wwwroot` directory
before launching a development desktop/headless build. Release installations always
use the verified matching GitHub asset. The release workflow publishes this archive
alongside the desktop and Android packages.

## Shared interface

Desktop and Android load the same Avalonia application, `MainView`, remote chat view, themes, Markdown renderer, and message controls from `CodexManager.UI`. Android's activity only hosts that shared view and forwards lifecycle events.

Use the **☰** icon to open or collapse the sidebar. On narrow screens it opens over the chat and closes after choosing a chat; wider screens use a resizable sidebar. Desktop remembers a manually collapsed sidebar. Android hides terminal controls, local agent setup, and system tray settings. Remote agents continue running on the host.

Remote workspace history is under **＋ Open workspace** in the sidebar. **Open folder…** browses directories on the remote host, including its WSL distributions. Upgrade the host to 1.0.8 or later for folder browsing. Each workspace has a **＋** action for a new chat; reconnect and import actions also live in the sidebar.

On Android the keyboard leaves room for the composer and action icons. The attachment button opens the system file picker; selected files remain visible as removable chips until sent. Code blocks wrap long lines without overlaying horizontal scrollbars. Copy buttons appear on code blocks rather than every message or tool call. New sessions select Full access when the provider exposes that option; existing sessions retain their selections.

## Headless host

```sh
VibeHarder --headless --listen 0.0.0.0 --port 2222 --pair
```

With `--pair`, new pairing requests are enabled for the first two minutes and print their six-digit number to the host console. Omit `--pair` on later starts to allow only already-paired devices. A separate profile can be selected with `CODEX_MANAGER_DATA`.

## Connection security

Numeric pairing uses SRP-6a with a 3072-bit group and SHA-256, bound to the host's TLS certificate. The number is not sent over the connection. Successful pairing pins that certificate and saves a random device credential; the host stores only the credential hash. Pairing is limited to one pending request and five requests per minute.

Revoking a device under **Paired devices** blocks new requests and disconnects idle connections within five seconds. Paired devices can operate the host's agent workspaces. A changed host certificate requires pairing again. Existing saved credentials remain compatible with this update.
