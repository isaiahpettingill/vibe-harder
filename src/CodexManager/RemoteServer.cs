using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace CodexManager;

public sealed class RemoteServer : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task loop;
    private readonly SemaphoreSlim writer = new(1);
    public string? Fingerprint { get; private set; }
    public string? Error { get; private set; }
    public static string DirectoryPath => Path.Combine(Store.DataDirectory, "remote");
    public RemoteServer(string directory, string address, int port, Func<JsonObject, Task<JsonNode?>> handle)
    {
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(Hosts.ResourceDirectory, "remote-server.cjs"));
        start.ArgumentList.Add(directory); start.ArgumentList.Add(address); start.ArgumentList.Add(port.ToString());
        process = Process.Start(start) ?? throw new IOException("Could not start SSH service.");
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } error) Error = error; }; process.BeginErrorReadLine();
        loop = Read();
        async Task Read()
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var message = JsonNode.Parse(line)!.AsObject();
                if (message["ready"]?.GetValue<bool>() == true) { Fingerprint = message["fingerprint"]!.GetValue<string>(); continue; }
                _ = Respond(message);
            }
        }
        async Task Respond(JsonObject message)
        {
            var request = message["request"]!.AsObject();
            var response = new JsonObject { ["id"] = request["id"]?.DeepClone() };
            try { response["result"] = await Dispatcher.UIThread.InvokeAsync(() => handle(request)).ConfigureAwait(false); }
            catch (Exception error) { response["error"] = error.Message; }
            await writer.WaitAsync();
            try { if (!process.HasExited) await process.StandardInput.WriteLineAsync(new JsonObject { ["channel"] = message["channel"]?.DeepClone(), ["response"] = response }.ToJsonString()); }
            catch (IOException) { }
            finally { writer.Release(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { process.Kill(true); }
        await loop; process.Dispose();
    }
}
