"""Exercise the published host with Python's standard library; no Node or SSH."""
import base64
import hashlib
import json
import os
from pathlib import Path
import signal
import socket
import ssl
import struct
import subprocess
import sys
import tempfile
import time

directory = tempfile.mkdtemp(prefix="vibe-headless-")
with socket.socket() as reserve:
    reserve.bind(("127.0.0.1", 0))
    port = reserve.getsockname()[1]


def launch():
    return subprocess.Popen(
        [str(Path(sys.argv[1]).resolve()), "--headless", "--port", str(port)],
        env={**os.environ, "CODEX_MANAGER_DATA": directory, "PATH": os.environ.get("SystemRoot", "/usr") + ("/System32" if os.name == "nt" else "/bin")},
        stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )


def connect():
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE  # Pin checked below, before sending credentials.
    stream = context.wrap_socket(socket.create_connection(("127.0.0.1", port), timeout=10), server_hostname="localhost")
    actual = "SHA256:" + base64.b64encode(hashlib.sha256(stream.getpeercert(binary_form=True)).digest()).decode().rstrip("=")
    assert actual == Path(directory, "remote", "host_fingerprint").read_text().strip()
    return stream


def exact(stream, length):
    data = b""
    while len(data) < length:
        chunk = stream.recv(length - len(data))
        if not chunk:
            raise EOFError("Host disconnected")
        data += chunk
    return data


def request(stream, **value):
    payload = json.dumps({"id": "probe", **value}).encode()
    stream.sendall(struct.pack(">I", len(payload)) + payload)
    length = struct.unpack(">I", exact(stream, 4))[0]
    assert 0 < length <= 32 * 1024 * 1024
    reply = json.loads(exact(stream, length))
    assert "error" not in reply, reply
    return reply["result"]


host = launch()
try:
    for _ in range(150):
        if Path(directory, "remote", "host_fingerprint").exists():
            break
        if host.poll() is not None:
            raise RuntimeError(host.stderr.read().decode())
        time.sleep(0.1)
    # Install an invite into this disposable profile, just as the host UI does.
    secret = os.urandom(32).hex().upper()
    Path(directory, "remote", "pairing.json").write_text(json.dumps({"hash": hashlib.sha256(secret.encode()).hexdigest().upper(), "expires": int(time.time()) + 600}))
    with connect() as stream:
        credential = request(stream, method="pair", code=secret, name="Native smoke test")
        assert request(stream, method="list")["workspaces"] == []
        workspace = request(stream, method="workspace", path=directory, name="Headless probe")
        request(stream, method="create", workspaceId=workspace, provider="Codex")
    host.terminate()
    host.wait(timeout=15)
    host = launch()
    time.sleep(1)
    with connect() as stream:
        assert request(stream, method="auth", **credential)
        assert len(request(stream, method="list")["chats"]) == 1
    print("Native TLS host: pairing, persistence, restart, and workspace/chat RPC passed without bundled Node.")
finally:
    host.terminate()
    host.wait(timeout=15)
