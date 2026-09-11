# Remote workspaces

Vibe Harder keeps agents on the machine that owns their workspace. Closing or
disconnecting a client does not stop the host's agent. The desktop supports local
workspaces and remote hosts together; Android is a remote-only client.

## Desktop host

Open **Settings → Remote hosts and server**. Enable the server, choose the host's
tailnet, VPN, or LAN IP address and port (default 2222), and paste each allowed
client's OpenSSH public key into the authorized-key field. Save, then reopen these
settings to read the host fingerprint. Loopback is the default listen address.

The application exposes an SSH endpoint exclusively for its session protocol.
It does not expose shell execution, SSH forwarding, SFTP, or OS account login.
Public-key authentication supports Ed25519 and RSA. Encrypted private keys can
be unlocked using a passphrase on the client; passphrases are not saved.

On the client, add the host address, port, private-key path, and the SHA256 host
fingerprint obtained on the host. The application rejects unknown or changed
host keys rather than accepting them automatically. Client private keys stay on
the client. The settings dialog can generate an RSA key and copy its public key.

Allowed client keys live in the app profile's `remote/authorized_keys` and host
identity in `remote/host_ed25519`. These are separate from the operating system's
`~/.ssh/authorized_keys` and `known_hosts`. Removing an allowed key blocks new
connections and disconnects that client's existing connection within five seconds.

Authorized clients can read workspace history, start or queue messages, steer
when the provider supports it, stop and resume turns, configure models, and answer
permission requests. They have access to the same agent capabilities as a local
user. SSH protects the connection even when the route is a VPN or local network.

## Headless host

Run the installed binary without a graphical session:

```sh
VibeHarder --headless --listen 100.64.0.10 --port 2222
```

On Windows the executable is `VibeHarder.exe`. Use `CODEX_MANAGER_DATA` to choose
an isolated profile, or omit it to use the desktop's saved workspaces and chats.
Only one process owns a profile at a time; use the desktop's server mode if its
window is already running. The server can run under a normal user service manager.

Create `remote/authorized_keys` under the profile with your client public keys.
The server writes its public fingerprint to `remote/host_fingerprint`. Configure
and authenticate Codex, Claude, or OpenCode in that host's environment before use.
WSL workspaces use their distro's own agent commands and credentials.

Desktop profiles retain the legacy `CodexManager` data directory to preserve
existing sessions during the Vibe Harder rebrand. Linux's default directory comes
from .NET's LocalApplicationData location, normally `~/.local/share/CodexManager`.
Windows uses `%LOCALAPPDATA%\CodexManager`.

Interrupted requests remain recoverable. With automatic resume enabled in the
profile, headless startup continues them without a prompt. Otherwise use **Resume**
on a remote client. Explicitly stopped chats are not automatically resumed.

## Android

Install `VibeHarder-Android.apk` from a GitHub release. It contains no terminal,
tray service, or locally running agents. Import an existing SSH private key or
generate an app-private RSA key, add its public key on the host, and enter the host
address and fingerprint. Open the chat drawer to select a workspace or start a
Codex, Claude, or OpenCode chat. Approvals and model options come from the host.

Obtainium can track the GitHub repository's releases and select the stable
`VibeHarder-Android.apk` asset. Releases use a persistent signing identity so an
update can replace an earlier installed release.
