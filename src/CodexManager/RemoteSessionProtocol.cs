using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace CodexManager;

// The native and browser listeners share authentication, pairing limits and revocation.
public sealed class RemoteSessionProtocol(string directory, Func<JsonObject, Task<JsonNode?>> handle, Func<string, string, CancellationToken, Action, Task>? showPairing)
{
    private sealed class PairingState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Queue<DateTimeOffset> Requests = new();
    }
    private static readonly ConcurrentDictionary<string, PairingState> Pairings = new();
    public async Task Serve(Func<CancellationToken, int, Task<JsonObject>> read, Func<JsonNode, CancellationToken, Task> write, Action disconnect, string binding, CancellationToken cancellation)
    {
        var pairingState = Pairings.GetOrAdd(Path.GetFullPath(directory), _ => new());
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        handshake.CancelAfter(TimeSpan.FromSeconds(15));
        var login = await read(handshake.Token, 16 * 1024);
        if (login["method"]?.GetValue<string>() == "pairing/start")
        {
            var response = new JsonObject { ["id"] = login["id"]?.DeepClone() };
            if (showPairing is null || !await pairingState.Gate.WaitAsync(0, cancellation))
            {
                response["error"] = showPairing is null ? "Pairing is unavailable on this host." : "Another device is pairing. Try again shortly.";
                await write(response, handshake.Token); return;
            }
            using var pairing = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            pairing.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                while (pairingState.Requests.TryPeek(out var time) && time < DateTimeOffset.UtcNow.AddMinutes(-1)) pairingState.Requests.Dequeue();
                if (pairingState.Requests.Count >= 5) throw new IOException("Too many pairing requests. Try again in a minute.");
                pairingState.Requests.Enqueue(DateTimeOffset.UtcNow);
                var challenge = new RemotePairingChallenge(binding);
                var name = login["name"]?.GetValue<string>() ?? "Device";
                name = new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray());
                await showPairing(name, challenge.Code, pairing.Token, pairing.Cancel);
                response["result"] = challenge.Offer();
                await write(response, pairing.Token);
                var proof = await read(pairing.Token, 16 * 1024);
                response = new JsonObject { ["id"] = proof["id"]?.DeepClone() };
                if (proof["method"]?.GetValue<string>() != "pairing/finish") throw new IOException("Invalid pairing request.");
                var evidence = challenge.Verify(proof);
                pairing.Token.ThrowIfCancellationRequested();
                response["result"] = new JsonObject { ["proof"] = evidence, ["credential"] = RemoteTrust.AddDevice(directory, name) };
                await write(response, pairing.Token);
            }
            catch (Exception error) when (!pairing.IsCancellationRequested)
            {
                response.Remove("result"); response["error"] = error.Message;
                await write(response, pairing.Token);
            }
            finally { pairing.Cancel(); pairingState.Gate.Release(); }
            return;
        }
        var reply = new JsonObject { ["id"] = login["id"]?.DeepClone() };
        string device; string secret;
        try
        {
            if (login["method"]?.GetValue<string>() == "pair")
            {
                var credential = RemoteTrust.Pair(directory, login["code"]!.GetValue<string>(), login["name"]?.GetValue<string>() ?? "Device");
                device = credential["device"]!.GetValue<string>(); secret = credential["token"]!.GetValue<string>(); reply["result"] = credential;
            }
            else
            {
                device = login["device"]?.GetValue<string>() ?? ""; secret = login["token"]?.GetValue<string>() ?? "";
                if (login["method"]?.GetValue<string>() != "auth" || !RemoteTrust.Authorized(directory, device, secret)) throw new IOException("Device is not authorized. Pair with this host again.");
                reply["result"] = true;
            }
        }
        catch (Exception error) { reply["error"] = error.Message; await write(reply, handshake.Token); return; }
        await write(reply, handshake.Token);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var revocation = Task.Run(async () =>
        {
            try { while (!session.IsCancellationRequested) { await Task.Delay(5000, session.Token); if (!RemoteTrust.Authorized(directory, device, secret)) { session.Cancel(); disconnect(); } } }
            catch (OperationCanceledException) { }
        });
        try
        {
            while (!session.IsCancellationRequested)
            {
                var request = await read(session.Token, RemoteWire.MaximumFrame);
                if (!RemoteTrust.Authorized(directory, device, secret)) break;
                var response = new JsonObject { ["id"] = request["id"]?.DeepClone() };
                try { response["result"] = await Dispatcher.UIThread.InvokeAsync(() => handle(request)).ConfigureAwait(false); }
                catch (Exception error) { response["error"] = error.Message; }
                await write(response, session.Token);
            }
        }
        finally { session.Cancel(); await revocation; }
    }
}
