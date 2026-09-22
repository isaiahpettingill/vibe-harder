using System.Diagnostics;

namespace CodexManager;

// One loop per host process. Store and workspace access remain on the caller's
// dispatcher; only subprocess work runs on the pool. No updates run in a browser.
public sealed class BackendUpdates(Store store, Func<IReadOnlyList<Workspace>> workspaces, Func<Workspace, AgentProvider, bool> busy,
    Func<Workspace, string, CancellationToken, Task<int>>? execute = null)
{
    public const string EnabledKey = "autoUpdateBackends";
    public static bool Enabled(Store store) => store.Setting(EnabledKey) != "0";
    private readonly Dictionary<string, DateTimeOffset> nextCheck = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    public string LastSummary { get; private set; } = "";
    public static (string Executable, string Update, string? Adapter) Plan(AgentProvider provider) => provider switch
    {
        AgentProvider.Codex => ("codex", "codex update", "@agentclientprotocol/codex-acp"),
        AgentProvider.Claude => ("claude", "claude update", "@agentclientprotocol/claude-agent-acp"),
        AgentProvider.OpenCode => ("opencode", "opencode upgrade", null),
        AgentProvider.VTCode => ("vtcode", "vtcode update", null),
        AgentProvider.Dirac => ("dirac", "npm update -g dirac-cli", "dirac-cli"),
        AgentProvider.Pi => ("pi", "npm install -g --ignore-scripts @earendil-works/pi-coding-agent@latest", "pi-acp"),
        AgentProvider.Cline => ("cline", "npm install -g cline@latest", null),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
    public async Task Run(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Check(token);
                await Task.Delay(TimeSpan.FromMinutes(5), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { AppDiagnostics.Record("Backend update checks", error); }
    }
    public async Task Check(CancellationToken token, bool force = false, AgentProvider? installProvider = null)
    {
        if (!Enabled(store) && installProvider is null) return;
        await gate.WaitAsync(token);
        try { await CheckCore(token, force, installProvider); }
        finally { gate.Release(); }
    }
    public async Task BeforeStart(Workspace workspace, AgentProvider provider, Action start, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (Enabled(store)) await CheckCore(token, force: true, installProvider: null, workspace, provider);
            token.ThrowIfCancellationRequested();
            start(); // Start while holding the update gate so another check cannot race it.
        }
        finally { gate.Release(); }
    }
    private async Task CheckCore(CancellationToken token, bool force, AgentProvider? installProvider, Workspace? onlyWorkspace = null, AgentProvider? onlyProvider = null)
    {
        List<string> failures = []; var deferred = false;
        var local = new Workspace("backend-updates", "Local", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var environments = (onlyWorkspace is null ? new[] { local }.Concat(workspaces().Where(w => w.IsWsl && store.Setting("closed:" + w.Id) != "1")) : [onlyWorkspace])
            .DistinctBy(w => w.Distro ?? "local").ToArray();
        foreach (var environment in environments)
            foreach (var provider in AgentProviders.All)
            {
                token.ThrowIfCancellationRequested();
                if (!Enabled(store) && installProvider is null) return;
                if (installProvider is { } selected && selected != provider.Provider) continue;
                if (onlyProvider is { } selectedProvider && selectedProvider != provider.Provider) continue;
                if (!AgentProviders.IsEnabled(store, provider.Provider)) continue;
                if (busy(environment, provider.Provider)) { deferred = true; continue; }
                var key = (environment.Distro ?? "local") + ":" + provider.Provider;
                if (!force && nextCheck.TryGetValue(key, out var next) && next > DateTimeOffset.UtcNow) continue;
                nextCheck[key] = DateTimeOffset.UtcNow.AddHours(6);
                try
                {
                    var plan = Plan(provider.Provider);
                    var run = execute ?? Execute;
                    var failed = false;
                    if (await run(environment, ProcessProbe(provider.Provider, OperatingSystem.IsWindows() && !environment.IsWsl), token) != 0)
                    { deferred = true; nextCheck.Remove(key); continue; }
                    var installed = await run(environment, plan.Executable + " --version", token) == 0;
                    if (installed && installProvider is null)
                        failed = await run(environment, plan.Update, token) != 0;
                    else if (!installed && (installProvider is not null || force))
                        await BackendInstallers.Install(environment, provider.Provider, run, token);
                    // Resolve latest npm adapters without starting an agent or sending a prompt.
                    if (plan.Adapter is not null)
                    {
                        if (await run(environment, "npm --version", token) == 0)
                            failed |= await run(environment, "npx -y --package=" + plan.Adapter + "@latest -- node -e 0", token) != 0;
                        else throw new IOException("Node.js and npm are required for this ACP adapter.");
                    }
                    store.Setting("backendUpdate:" + key, failed ? "Update failed; will retry." : "Checked " + DateTimeOffset.Now.ToString("g"));
                    if (failed) { failures.Add(key); nextCheck[key] = DateTimeOffset.UtcNow.AddMinutes(30); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    nextCheck[key] = DateTimeOffset.UtcNow.AddMinutes(30);
                    failures.Add(key);
                    store.Setting("backendUpdate:" + key, "Update failed: " + error.Message);
                    AppDiagnostics.Record("Backend update " + key, error);
                }
            }
        LastSummary = failures.Count > 0 ? "Backend updates need attention: " + string.Join(", ", failures) + ". See Agents settings."
            : deferred ? "Backend checks completed; busy agents will be checked when idle." : "Enabled backend checks completed.";
    }
    public static string ProcessProbe(AgentProvider provider, bool windows)
    {
        var executable = Plan(provider).Executable;
        string[] packages = provider switch
        {
            AgentProvider.Codex => ["@openai/codex", "@agentclientprotocol/codex-acp"],
            AgentProvider.Claude => ["@anthropic-ai/claude-code", "@agentclientprotocol/claude-agent-acp"],
            AgentProvider.OpenCode => ["@opencode/cli", "opencode-ai"],
            AgentProvider.VTCode => ["@vinhnx/vtcode"],
            AgentProvider.Dirac => ["dirac-cli"],
            AgentProvider.Pi => ["@earendil-works/pi-coding-agent", "@mariozechner/pi-coding-agent", "pi-acp"],
            AgentProvider.Cline => ["cline"],
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        var windowsPackages = string.Join("|", packages.Select(p => System.Text.RegularExpressions.Regex.Escape("node_modules/" + p + "/").Replace("/", @"[\\/]")));
        var unixPackages = string.Join(" || ", packages.Select(p => "index($0, \"node_modules/" + p + "/\")"));
        // A failed process-list query is treated as in-use, not permission to
        // update. Node backends are recognized by their package/script path.
        return windows
            ? "$ErrorActionPreference='Stop'; $vibeProcesses=Get-CimInstance Win32_Process; if ($vibeProcesses | Where-Object { $_.Name -match '^" + executable + "(\\.exe)?$' -or ($_.Name -match '^node(\\.exe)?$' -and $_.CommandLine -match '" + windowsPackages + "') }) { exit 1 }; exit 0"
            : "sh -c " + Hosts.Quote("vibe_processes=$(ps -eo comm=,args=) || exit 2; printf '%s\\n' \"$vibe_processes\" | awk '$1 ~ /^(" + executable + ")(\\.exe)?$/ || ($1 ~ /node/ && (" + unixPackages + ")) { found=1 } END { exit found ? 1 : 0 }'");
    }
    private static Task<int> Execute(Workspace workspace, string command, CancellationToken token) => Task.Run(async () =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var process = Process.Start(Hosts.Agent(workspace, command)) ?? throw new IOException("Could not start backend updater.");
        // Drain incrementally: updater output must not accumulate in memory.
        var stdout = Drain(process.StandardOutput, timeout.Token);
        var stderr = Drain(process.StandardError, timeout.Token);
        try
        {
            if (command == "vtcode update") await process.StandardInput.WriteLineAsync("y".AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }, token);
    private static async Task Drain(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { }
    }
}
