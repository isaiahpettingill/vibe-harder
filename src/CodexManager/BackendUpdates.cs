using System.Diagnostics;
using System.Collections.Concurrent;

namespace CodexManager;

// One loop per host process. Store and workspace access remain on the caller's
// dispatcher; only subprocess work runs on the pool. No updates run in a browser.
public sealed class BackendUpdates(Store store, Func<IReadOnlyList<Workspace>> workspaces, Func<Workspace, AgentProvider, bool> busy,
    Func<Workspace, string, CancellationToken, Task<int>>? execute = null, Func<Workspace, string, CancellationToken, Task<string>>? resolve = null)
{
    // Injected executors (tests) get a fixed version rather than querying npm.
    private readonly Func<Workspace, string, CancellationToken, Task<string>> resolve = resolve ?? (execute is null
        ? (workspace, command, token) => Hosts.Capture(Hosts.Agent(workspace, command), TimeSpan.FromMinutes(1)).WaitAsync(token)
        : (_, _, _) => Task.FromResult("1.0.0"));
    public const string EnabledKey = "autoUpdateBackends";
    public static bool Enabled(Store store) => store.Setting(EnabledKey) != "0";
    private readonly ConcurrentDictionary<string, DateTimeOffset> nextCheck = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    public string LastSummary { get; private set; } = "";
    // Update is null when the adapter bundles its agent (see BundledPackages): the user's own
    // installation is left alone because nothing launches it.
    public static (string Executable, string? Update) Plan(AgentProvider provider) => provider switch
    {
        AgentProvider.Codex => ("codex", null),
        AgentProvider.Claude => ("claude", null),
        AgentProvider.OpenCode => ("opencode", "opencode upgrade"),
        AgentProvider.VTCode => ("vtcode", "vtcode update"),
        AgentProvider.Dirac => ("dirac", null),
        // pi-acp runs the installed pi, so Pi still needs a system update.
        AgentProvider.Pi => ("pi", "npm install -g --ignore-scripts @earendil-works/pi-coding-agent@latest"),
        AgentProvider.Cline => ("cline", "npm install -g cline@latest"),
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
        await CheckCore(token, force, installProvider);
    }
    public async Task BeforeStart(Workspace workspace, AgentProvider provider, Action start, CancellationToken token)
    {
        var gate = Gate(workspace, provider);
        var checkedForLaunch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Updates are opportunistic. One that is slow or hung (network, npm lock, a busy
        // environment) finishes in the background instead of stalling every chat that
        // launches this provider; it is never killed mid-install on the launch's behalf.
        // Not awaited: the check keeps running on this context (Store access stays on the
        // dispatcher) while the launch waits for it only up to LaunchWait.
        _ = CheckForLaunch();
        async Task CheckForLaunch()
        {
            var held = false;
            try
            {
                await gate.WaitAsync(token); held = true;
                if (Enabled(store)) await CheckCore(token, force: false, installProvider: null, workspace, provider, gateHeld: true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { AppDiagnostics.Record("Backend update before launch", error); }
            finally
            {
                checkedForLaunch.TrySetResult();
                // Normally hold the gate through the launch so a background update cannot race it.
                if (held) { await Task.WhenAny(launched.Task, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)); gate.Release(); }
            }
        }
        try
        {
            await Task.WhenAny(checkedForLaunch.Task, Task.Delay(LaunchWait, token));
            token.ThrowIfCancellationRequested();
            start();
        }
        finally { launched.TrySetResult(); }
    }
    public TimeSpan LaunchWait { get; init; } = TimeSpan.FromSeconds(20);

    public sealed record AvailableUpdate(Workspace Environment, AgentProvider Provider, string Detail);
    // npm packages behind the system installs updated in place, used only to compare versions.
    private static string? SystemPackage(AgentProvider provider) => provider switch
    {
        AgentProvider.OpenCode => "opencode-ai",
        AgentProvider.VTCode => "@vinhnx/vtcode",
        AgentProvider.Pi => "@earendil-works/pi-coding-agent",
        AgentProvider.Cline => "cline",
        _ => null
    };
    private Workspace[] Environments(Workspace? only = null)
    {
        var local = new Workspace("backend-updates", "Local", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return (only is null ? new[] { local }.Concat(workspaces().Where(w => w.IsWsl && store.Setting("closed:" + w.Id) != "1")) : [only])
            .DistinctBy(w => w.Distro ?? "local").ToArray();
    }
    // Looks up newer versions without installing anything, reporting each as it is found.
    // Environments are checked in parallel so one slow distro does not hold up the rest.
    public async Task FindUpdates(Action<AvailableUpdate> found, CancellationToken token)
    {
        var providers = AgentProviders.All.Where(p => AgentProviders.IsEnabled(store, p.Provider)).Select(p => p.Provider).ToArray();
        var pinned = Environments().ToDictionary(e => e, e => providers.ToDictionary(p => p, p => BundledPackages.For(p).Select(package => (package, BundledPackages.Pinned(store, e, package))).ToArray()));
        await Task.WhenAll(pinned.Select(environment => Task.Run(async () =>
        {
            foreach (var provider in providers)
            {
                try
                {
                    var changes = new List<string>();
                    // Unpinned sets launch @latest and are current by definition.
                    foreach (var (package, version) in environment.Value[provider])
                        if (version is not null && Newer(BundledPackages.ParseVersion(await resolve(environment.Key, BundledPackages.Lookup(package), token)), version) is { } latest)
                            changes.Add(package.Split('/')[^1] + " " + latest);
                    if (Plan(provider).Update is not null && SystemPackage(provider) is { } system)
                    {
                        string installed;
                        try { installed = await resolve(environment.Key, Plan(provider).Executable + " --version", token); }
                        catch (Exception error) when (error is not OperationCanceledException) { installed = ""; }
                        if (installed.Length > 0 && Newer(await resolve(environment.Key, BundledPackages.Lookup(system), token), installed) is { } latest)
                            changes.Add(Plan(provider).Executable + " " + latest);
                    }
                    if (changes.Count > 0) found(new(environment.Key, provider, string.Join(", ", changes)));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { AppDiagnostics.Record("Backend update check " + Key(environment.Key, provider), error); }
            }
        }, token)));
    }
    // Returns the latest version when it is newer than the installed one.
    public static string? Newer(string latest, string installed)
    {
        static Version? Parse(string text) => System.Text.RegularExpressions.Regex.Match(text, @"\d+\.\d+\.\d+") is { Success: true } match ? Version.Parse(match.Value) : null;
        return Parse(latest) is { } a && Parse(installed) is { } b && a > b ? a.ToString() : null;
    }
    // Updates one backend now, whether or not automatic updates are on. Returns a problem, if any.
    public async Task<string?> Apply(AvailableUpdate update, CancellationToken token)
    {
        var (failures, deferred) = await CheckCore(token, force: true, installProvider: null, Environments(update.Environment)[0], update.Provider, manual: true);
        return failures.Count > 0 ? store.Setting("backendUpdate:" + Key(update.Environment, update.Provider)) ?? "Update failed."
            : deferred ? "It is in use. Close its chats, then try again." : null;
    }
    private string Key(Workspace workspace, AgentProvider provider) => (workspace.Distro ?? "local") + ":" + provider;
    private SemaphoreSlim Gate(Workspace workspace, AgentProvider provider) => gates.GetOrAdd(Key(workspace, provider), _ => new SemaphoreSlim(1, 1));
    private async Task<(List<string> Failures, bool Deferred)> CheckCore(CancellationToken token, bool force, AgentProvider? installProvider, Workspace? onlyWorkspace = null, AgentProvider? onlyProvider = null, bool gateHeld = false, bool manual = false)
    {
        List<string> failures = []; var deferred = false;
        var environments = Environments(onlyWorkspace);
        foreach (var environment in environments)
            foreach (var provider in AgentProviders.All)
            {
                token.ThrowIfCancellationRequested();
                if (!Enabled(store) && installProvider is null && !manual) return (failures, deferred);
                if (installProvider is { } selected && selected != provider.Provider) continue;
                if (onlyProvider is { } selectedProvider && selectedProvider != provider.Provider) continue;
                if (!AgentProviders.IsEnabled(store, provider.Provider)) continue;
                var key = Key(environment, provider.Provider);
                var gate = gateHeld ? null : Gate(environment, provider.Provider);
                if (gate is not null) await gate.WaitAsync(token);
                try
                {
                    var plan = Plan(provider.Provider);
                    var run = execute ?? Execute;
                    var failed = false;
                    // System installs are replaced in place, so wait until nothing runs them.
                    // Bundled packages install side by side and can update while chats run.
                    if (plan.Update is not null && busy(environment, provider.Provider)) { deferred = true; continue; }
                    if (!force && nextCheck.TryGetValue(key, out var next) && next > DateTimeOffset.UtcNow) continue;
                    nextCheck[key] = DateTimeOffset.UtcNow.AddHours(6);
                    if (plan.Update is not null && await run(environment, ProcessProbe(provider.Provider, OperatingSystem.IsWindows() && !environment.IsWsl), token) != 0)
                    { deferred = true; nextCheck.TryRemove(key, out _); continue; }
                    if (plan.Update is { } update)
                    {
                        var installed = await run(environment, plan.Executable + " --version", token) == 0;
                        if (installed && installProvider is null)
                            failed = await run(environment, update, token) != 0;
                        else if (!installed && (installProvider is not null || force))
                            await BackendInstallers.Install(environment, provider.Provider, run, token);
                    }
                    if (BundledPackages.For(provider.Provider) is { Length: > 0 } packages)
                    {
                        if (await run(environment, "npm --version", token) != 0) throw new IOException("Node.js and npm are required for this ACP adapter.");
                        failed |= !await InstallLatest(environment, provider.Provider, packages, run, token);
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
                finally { gate?.Release(); }
            }
        LastSummary = failures.Count > 0 ? "Backend updates need attention: " + string.Join(", ", failures) + ". See Agents settings."
            : deferred ? "Backend checks completed; busy agents will be checked when idle." : "Enabled backend checks completed.";
        return (failures, deferred);
    }
    // Installs the newest versions as a fresh set and pins them only once that set runs, so a
    // bad publish or interrupted install never replaces the working one. No agent is started.
    private async Task<bool> InstallLatest(Workspace environment, AgentProvider provider, string[] packages, Func<Workspace, string, CancellationToken, Task<int>> run, CancellationToken token)
    {
        var versions = new Dictionary<string, string>();
        foreach (var package in packages) versions[package] = BundledPackages.ParseVersion(await resolve(environment, BundledPackages.Lookup(package), token));
        if (packages.All(package => BundledPackages.Pinned(store, environment, package) == versions[package])) return true;
        if (await run(environment, BundledPackages.Install(provider, versions), token) != 0) return false;
        BundledPackages.Pin(store, environment, versions);
        return true;
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
