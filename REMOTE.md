# Connect your computers

Agents run on the computer that owns the workspace. Closing a remote client does not stop them. You can mix local and remote workspaces in the desktop app; Android is a remote client only.

## Pair once

1. On the host, open **Settings → Remote connections**.
2. Enter its LAN, VPN, or Tailscale IP address. Choose **Copy connection code and start hosting**.
3. On your other device, paste the code in **Remote connections** (or **Connect** on Android) and pair.

The code expires after 10 minutes and works once. The saved device credential survives restarts and stays valid until you revoke it under **Paired devices**. Keep the host's app data to preserve pairing.

Both devices must be able to reach the chosen IP and TCP port (default **2222**). Allow that port through the host firewall for your LAN or VPN. No SSH installation, SSH keys, Node.js, or repeated access to the host is required. There is no public relay; use a VPN/tailnet for access away from home.

## Headless host

```sh
VibeHarder --headless --listen 0.0.0.0 --port 2222 --pair 100.64.0.10
```

Replace `100.64.0.10` with the address clients can reach. The command prints a connection code. Omit `--pair` on later starts; existing devices remain paired. Use your operating system's service manager to start this command at login or boot. A separate profile can be selected with `CODEX_MANAGER_DATA`.

## Connection security

Connections use TLS with the host certificate pinned by the pairing code. Paired clients receive a random device credential; the host stores its hash. Revoking a device blocks new requests and disconnects idle connections within five seconds. Paired devices can operate the host's agent workspaces, so share codes only with your own trusted devices.

Treat connection codes and app-data backups as credentials. A changed host certificate requires pairing again. Older SSH-based saved connections also need to pair again after upgrading.

## Android

Install **VibeHarder-Android.apk** from Releases, tap **Connect**, and paste a code. The client remembers its paired host and reconnects on launch. It has no local agents, terminal, or system tray.
