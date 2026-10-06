#if !MOBILE_CLIENT
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexManager;

// The vh command line talks to the running app (desktop or --headless) through a pipe only the
// same user can open. Requests use the remote protocol's methods, so the CLI drives chats
// exactly as a paired device does, with no pairing needed on this machine.
public static class CliPipe
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("CODEX_MANAGER_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexManager");
    private static string Name(string profile) => "VibeHarder-cli-v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? Path.GetFullPath(profile).ToUpperInvariant() : Path.GetFullPath(profile))))[..24];

    public static async Task<JsonNode?> Request(string profile, JsonObject request, CancellationToken token)
    {
        await using var pipe = new NamedPipeClientStream(".", Name(profile), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(2000, token); }
        catch (TimeoutException) { throw new IOException("Vibe Harder is not running. Start the app, or run VibeHarder --headless."); }
        await RemoteWire.Write(pipe, request, token);
        var response = await RemoteWire.Read(pipe, token);
        if (response["error"] is { } error) throw new RemoteOperationException(error.GetValue<string>());
        return response["result"]?.DeepClone();
    }

    public static async Task Serve(string profile, Func<JsonObject, Task<JsonNode?>> handle, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(Name(profile), PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(token); }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); return; }
            catch (IOException) { await pipe.DisposeAsync(); await Task.Delay(1000, token); continue; }
            _ = Respond(pipe, handle, token);
        }
    }
    private static async Task Respond(NamedPipeServerStream pipe, Func<JsonObject, Task<JsonNode?>> handle, CancellationToken token)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                var request = await RemoteWire.Read(pipe, timeout.Token);
                JsonObject response;
                try { response = new JsonObject { ["result"] = await handle(request) }; }
                catch (Exception error) when (error is not OperationCanceledException) { response = new JsonObject { ["error"] = error.Message }; }
                await RemoteWire.Write(pipe, response, timeout.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException) { System.Diagnostics.Trace.WriteLine("CLI request: " + error.Message); }
        }
    }
}
#endif
