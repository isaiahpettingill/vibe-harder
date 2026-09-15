using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CodexManager;

public sealed class AcpClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writes = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task reader;
    private readonly Task errorReader;
    private long nextId;
    private int disposed;
    private string diagnostics = "";
    public bool Alive => !lifetime.IsCancellationRequested && !process.HasExited;
    public event Action<JsonElement>? Update;
    public event Action? Disconnected;
    public event Action<bool>? AuthenticationChanged;
    public event Action<string, JsonElement>? ExtensionNotification;
    public Func<JsonElement, Task>? UpdateAsync { get; set; }
    public Func<JsonElement, CancellationToken, Task<JsonObject>>? PermissionRequested { get; set; }
    public AcpClient(ProcessStartInfo start)
    {
        process = Process.Start(start) ?? throw new IOException("Could not start the agent.");
        errorReader = Task.Run(async () => { try { while (await process.StandardError.ReadLineAsync(lifetime.Token) is { } line) diagnostics = (diagnostics + "\n" + line)[^Math.Min(4000, diagnostics.Length + line.Length + 1)..]; } catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { } });
        reader = Task.Run(ReadLoop);
    }
    public async Task<JsonElement> Request(string method, JsonObject parameters, CancellationToken cancellation = default)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
        try { await Write(RpcJson.Object(("jsonrpc", "2.0"), ("id", id), ("method", method), ("params", parameters)), linked.Token); return await completion.Task; }
        finally { pending.TryRemove(id, out _); }
    }
    public Task Notify(string method, JsonObject parameters) => Write(RpcJson.Object(("jsonrpc", "2.0"), ("method", method), ("params", parameters)));
    private async Task Write(JsonObject message, CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        await writes.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), linked.Token).ConfigureAwait(false); await process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false); }
        finally { writes.Release(); }
    }
    private async Task ReadLoop()
    {
        Exception? failure = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line); var message = doc.RootElement;
                if (message.TryGetProperty("method", out var method))
                {
                    if (message.TryGetProperty("id", out var id)) _ = Respond(id.Clone(), method.GetString()!, message.GetProperty("params").Clone());
                    else if (method.GetString() == "session/update")
                    {
                        var update = message.GetProperty("params").GetProperty("update").Clone();
                        Update?.Invoke(update);
                        if (UpdateAsync is not null) await UpdateAsync(update);
                    }
                    else if (method.GetString() == "_auth/status_update" &&
                        message.GetProperty("params").TryGetProperty("authStatus", out var auth) &&
                        auth.ValueKind == JsonValueKind.Object && auth.TryGetProperty("kind", out var kind))
                        AuthenticationChanged?.Invoke(kind.GetString() == "none");
                    else if (method.GetString() is { } extension && extension.StartsWith('_') && message.TryGetProperty("params", out var parameters))
                        ExtensionNotification?.Invoke(extension, parameters.Clone());
                }
                else if (message.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && pending.TryRemove(number, out var completion))
                {
                    if (message.TryGetProperty("error", out var error)) completion.TrySetException(new AcpException(error));
                    else completion.TrySetResult(message.GetProperty("result").Clone());
                }
            }
            await Task.WhenAny(errorReader, Task.Delay(200));
            failure = new IOException("Agent exited. " + diagnostics.Trim());
        }
        catch (Exception error) { failure = error; }
        finally
        {
            foreach (var item in pending.Values) item.TrySetException(failure ?? new IOException("Agent disconnected."));
            lifetime.Cancel();
            Disconnected?.Invoke();
        }
    }
    private async Task Respond(JsonElement id, string method, JsonElement parameters)
    {
        try
        {
            if (method != "session/request_permission")
            { await Write(RpcJson.Object(("jsonrpc", "2.0"), ("id", JsonNode.Parse(id.GetRawText())), ("error", RpcJson.Object(("code", -32601), ("message", "Client capability not supported: " + method))))); return; }
            var result = PermissionRequested is null ? RpcJson.Permission() : await PermissionRequested(parameters, lifetime.Token);
            await Write(RpcJson.Object(("jsonrpc", "2.0"), ("id", JsonNode.Parse(id.GetRawText())), ("result", result)));
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
    public Task<JsonElement> Initialize(CancellationToken token = default) => Request("initialize", RpcJson.Object(("protocolVersion", 1), ("clientInfo", RpcJson.Object(("name", "codex-manager"), ("version", "1.0.0"))), ("clientCapabilities", RpcJson.Object(("fs", RpcJson.Object(("readTextFile", false), ("writeTextFile", false))), ("terminal", false), ("session", RpcJson.Object(("configOptions", RpcJson.Object(("boolean", new JsonObject())))))))), token);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { await reader; return; }
        lifetime.Cancel();
        await Task.Run(() => { try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        await reader;
        process.Dispose();
    }
}

