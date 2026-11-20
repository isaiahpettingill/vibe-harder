using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

internal enum DecisionSource { Host, Remote, Acp }

internal sealed record DecisionSettings(bool Enabled, DecisionSource Source, string Command, string Endpoint, AgentProvider Provider, string Model)
{
    internal const string DefaultCriteria = "Did the agent stop with part of the user's request unfinished when it could still take a concrete authorized step without new information or approval? Answer no if the user asked to stop or the agent described a genuine blocker.";
    internal const int DefaultTimeout = 25, DefaultAttempts = 3, DefaultRequestLimit = 2400, DefaultResponseLimit = 5200, DefaultConfidence = 75;
    internal string Criteria { get; init; } = DefaultCriteria;
    internal int Timeout { get; init; } = DefaultTimeout;
    internal int Attempts { get; init; } = DefaultAttempts;
    internal int RequestLimit { get; init; } = DefaultRequestLimit;
    internal int ResponseLimit { get; init; } = DefaultResponseLimit;
    internal int Confidence { get; init; } = DefaultConfidence;

    internal const string Example = "llama-server -hf LiquidAI/d1-omni-600M-GGUF:Q8_0 --no-mmproj -ngl 99 -b 4096 -ub 4096";
    internal const string LocalAddress = "http://127.0.0.1:8080/v1/systemone";
    internal static DecisionSettings Read(Store store) => new(store.Setting("decisionEnabled") == "1",
        Enum.TryParse<DecisionSource>(store.Setting("decisionSource"), out var source) ? source : DecisionSource.Host,
        store.Setting("decisionCommand") ?? "", store.Setting("decisionEndpoint") ?? "",
        Enum.TryParse<AgentProvider>(store.Setting("decisionProvider"), out var provider) && Enum.IsDefined(provider) ? provider : AgentProvider.Pi,
        store.Setting("decisionModel") ?? "")
    {
        Criteria = store.Setting("decisionCriteria") is { } text && !string.IsNullOrWhiteSpace(text) ? text : DefaultCriteria,
        Timeout = ReadNumber(store, "decisionTimeout", DefaultTimeout, 1, 300),
        Attempts = ReadNumber(store, "decisionAttempts", DefaultAttempts, 1, 10),
        RequestLimit = ReadNumber(store, "decisionRequestLimit", DefaultRequestLimit, 1, 50000),
        ResponseLimit = ReadNumber(store, "decisionResponseLimit", DefaultResponseLimit, 1, 50000),
        Confidence = ReadNumber(store, "decisionConfidence", DefaultConfidence, 0, 100)
    };

    private static int ReadNumber(Store store, string key, int fallback, int min, int max) =>
        int.TryParse(store.Setting(key), out var value) && value >= min && value <= max ? value : fallback;

    internal Uri Address()
    {
        var address = Source == DecisionSource.Host && Endpoint.Length == 0 ? LocalAddress : Endpoint;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            (Source == DecisionSource.Host && !uri.IsLoopback))
            throw new IOException("Enter a valid System One endpoint. A host server must use a loopback address.");
        return uri.AbsolutePath is "/" or "/v1" or "/v1/" ? new Uri(uri, "/v1/systemone") : uri;
    }
}

internal sealed partial class DecisionClassifier : IAsyncDisposable
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly object gate = new();
    private readonly Queue<string> log = new();
    private CancellationTokenSource? serverLifetime;
    private Task server = Task.CompletedTask;
    private volatile DecisionSettings settings = new(false, DecisionSource.Host, "", "", AgentProvider.Pi, "");
    private string acpCommand = "";
    internal string Log { get { lock (gate) return string.Join("\n", log); } }
    internal bool Enabled => settings.Enabled;
    internal int Attempts => settings.Attempts;

    internal void Configure(DecisionSettings options, string adapterCommand = "")
    {
        var restart = LocalCommand(options) != LocalCommand(settings);
        settings = options;
        acpCommand = adapterCommand;
        if (!restart) return;

        static string? LocalCommand(DecisionSettings value) => value is { Enabled: true, Source: DecisionSource.Host, Command.Length: > 0 } ? value.Command : null;
        var previous = server;
        var previousLifetime = serverLifetime;
        previousLifetime?.Cancel();
        serverLifetime = options is { Enabled: true, Source: DecisionSource.Host, Command.Length: > 0 } ? new() : null;
        var token = serverLifetime?.Token;
        server = Task.Run(async () =>
        {
            await previous;
            previousLifetime?.Dispose();
            if (token is { } active) await Supervise(options.Command, active);
        });
    }

    private async Task Supervise(string command, CancellationToken token)
    {
        var retry = 1;
        while (!token.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                Append("Starting the System One server on this host.");
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                using var process = Process.Start(NativeProcess(command, home)) ?? throw new IOException("Could not start the server.");
                process.StandardInput.Close();
                var output = Task.WhenAll(Drain(process.StandardOutput, token), Drain(process.StandardError, token));
                try
                {
                    await process.WaitForExitAsync(token);
                    await output.WaitAsync(TimeSpan.FromSeconds(2), token);
                    Append($"Server exited with code {process.ExitCode}.");
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { Append(error.Message); }
            if (token.IsCancellationRequested) break;
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(30)) retry = 1;
            Append($"Restarting in {retry} s.");
            try { await Task.Delay(TimeSpan.FromSeconds(retry), token); }
            catch (OperationCanceledException) { break; }
            retry = Math.Min(20, retry * 2);
        }
        Append("Host server stopped.");
    }

    private async Task Drain(StreamReader stream, CancellationToken token)
    {
        try { while (await stream.ReadLineAsync(token) is { } line) Append(line); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void Append(string line)
    {
        if (line.Length > 3000) line = line[..3000];
        lock (gate)
        {
            log.Enqueue(line);
            while (log.Count > 400) log.Dequeue();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Configure(settings with { Enabled = false });
        await server;
    }
}
