#if !MOBILE_CLIENT
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace CodexManager;

// The helper owns the PTYs, not the GUI or a remote connection. Its private
// runtime copy also lets Windows replace the installed executable during updates.
public static class TerminalBroker
{
    private static readonly SemaphoreSlim launchGate = new(1);
    private static string PipeName(string profile) => "VibeHarder-terminals-v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? Path.GetFullPath(profile).ToUpperInvariant() : Path.GetFullPath(profile))))[..24];
    public static async Task<JsonNode> Request(string profile, JsonObject request, CancellationToken token, bool launch = false)
    {
        if (launch)
        {
            await launchGate.WaitAsync(token);
            try
            {
                try { return await Exchange(profile, request, token, 300); }
                catch (TimeoutException) { }
                catch (IOException) { }
                Start(profile);
            }
            finally { launchGate.Release(); }
        }
        return await Exchange(profile, request, token, 10000);
    }
    private static async Task<JsonNode> Exchange(string profile, JsonObject request, CancellationToken token, int connectTimeout)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(".", PipeName(profile), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(connectTimeout, timeout.Token);
        await Write(pipe, request, timeout.Token);
        var result = await Read(pipe, timeout.Token);
        if (result["error"] is { } error) throw new IOException(error.GetValue<string>());
        return result;
    }
    private static void Start(string profile)
    {
        var source = AppContext.BaseDirectory;
        var native = Path.Combine(source, OperatingSystem.IsWindows() ? "VibeHarder.exe" : "VibeHarder");
        var managed = Path.Combine(source, "VibeHarder.dll");
        var entry = File.Exists(managed) ? managed : native;
        if (!File.Exists(entry)) throw new IOException("The terminal helper is missing. Reinstall the desktop app.");
        using var stream = File.OpenRead(entry);
        var version = Convert.ToHexString(SHA256.HashData(stream))[..24];
        var ui = Path.Combine(source, "VibeHarder.UI.dll");
        if (File.Exists(ui)) { using var uiStream = File.OpenRead(ui); version += Convert.ToHexString(SHA256.HashData(uiStream))[..12]; }
        var root = Path.Combine(Path.GetFullPath(profile), "terminal-runtime", version);
        if (!Directory.Exists(root))
        {
            var staging = root + "." + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));
            var runtimes = Path.Combine(source, "runtimes");
            if (Directory.Exists(runtimes))
                foreach (var file in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
                { var target = Path.Combine(staging, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); }
            try { Directory.Move(staging, root); }
            catch (IOException) when (Directory.Exists(root)) { /* Another launcher finished the same immutable runtime. */ }
        }
        var executable = Path.Combine(root, Path.GetFileName(native));
        if (!OperatingSystem.IsWindows() && File.Exists(executable)) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root };
        if (File.Exists(managed) && !File.Exists(Path.Combine(source, OperatingSystem.IsWindows() ? "coreclr.dll" : OperatingSystem.IsMacOS() ? "libcoreclr.dylib" : "libcoreclr.so")))
        {
            start.FileName = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            start.ArgumentList.Add(Path.Combine(root, "VibeHarder.dll"));
        }
        start.ArgumentList.Add("--terminal-broker"); start.ArgumentList.Add(profile);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the terminal helper.");
    }
    private static async Task Write(Stream stream, JsonNode value, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
        if (bytes.Length > 4 * 1024 * 1024) throw new IOException("Terminal response is too large.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token); await stream.WriteAsync(bytes, token);
    }
    private static async Task<JsonNode> Read(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BitConverter.ToInt32(header);
        if (length is <= 0 or > 4 * 1024 * 1024) throw new IOException("Invalid terminal request.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return JsonNode.Parse(bytes) ?? throw new IOException("Empty terminal request.");
    }
    public static async Task Serve(string profile, CancellationToken token)
    {
        var shells = new Dictionary<string, Shell>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(PipeName(profile), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (shells.Values.All(s => s.Exited)) idle.CancelAfter(TimeSpan.FromMinutes(1));
                try { await pipe.WaitForConnectionAsync(idle.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var request = await Read(pipe, timeout.Token);
                    JsonNode result;
                    try { result = await Dispatcher.UIThread.InvokeAsync(() => Handle(request, profile, shells)); }
                    catch (Exception error) { result = new JsonObject { ["error"] = error.Message }; }
                    await Write(pipe, result, timeout.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException) { Trace.WriteLine(error.Message); }
            }
        }
        finally { await Dispatcher.UIThread.InvokeAsync(() => { foreach (var shell in shells.Values) shell.Session.Dispose(); }); }
    }
    private sealed class Shell
    {
        public readonly TerminalSession Session = new();
        public readonly StringBuilder Output = new();
        public long Offset;
        public bool Exited;
        public Shell()
        {
            Session.Completed += () => Exited = true;
            Session.RawOutput += text => { Output.Append(text); if (Output.Length > 262144) { var trim = Output.Length - 131072; Output.Remove(0, trim); Offset += trim; } };
        }
    }
    private static async Task<JsonNode> Handle(JsonNode request, string profile, Dictionary<string, Shell> shells)
    {
        var id = request["id"]!.GetValue<string>(); var method = request["method"]!.GetValue<string>();
        if (method == "open")
        {
            if (shells.TryGetValue(id, out var previous) && !previous.Exited) return new JsonObject { ["id"] = id };
            if (request["resumeOnly"]?.GetValue<bool>() == true) throw new IOException("This terminal has closed. Open it again to start a new shell.");
            if (previous is not null) { previous.Session.Dispose(); shells.Remove(id); }
            if (shells.Count >= 64) throw new IOException("Close an existing terminal before opening another.");
            using var store = new Store(profile);
            var workspace = new Workspace(request["workspaceId"]!.GetValue<string>(), "Terminal", request["path"]!.GetValue<string>(), request["distro"]?.GetValue<string>());
            var shell = new Shell();
            try { await shell.Session.Start(workspace, settings: store); shells.Add(id, shell); }
            catch { shell.Session.Dispose(); throw; }
            return new JsonObject { ["id"] = id };
        }
        if (!shells.TryGetValue(id, out var active))
        {
            if (method == "close") return new JsonObject();
            throw new IOException("This terminal has closed.");
        }
        switch (method)
        {
            case "read":
                var offset = request["offset"]!.GetValue<long>(); var end = active.Offset + active.Output.Length; var start = Math.Clamp(offset, active.Offset, end);
                return new JsonObject { ["text"] = active.Output.ToString((int)(start - active.Offset), (int)(end - start)), ["offset"] = end, ["reset"] = offset < active.Offset || offset > end, ["exited"] = active.Exited };
            case "input": active.Session.Input(request["text"]!.GetValue<string>()); break;
            case "resize": active.Session.Resize(request["cols"]!.GetValue<int>(), request["rows"]!.GetValue<int>()); break;
            case "close": active.Session.Dispose(); shells.Remove(id); break;
            default: throw new IOException("Unknown terminal operation.");
        }
        return new JsonObject();
    }
}
#endif
