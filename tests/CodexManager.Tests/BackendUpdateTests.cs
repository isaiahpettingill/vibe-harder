namespace CodexManager.Tests;

public class BackendUpdateTests
{
    // Injected executors resolve every bundled package to 1.0.0.
    private static string Installed(AgentProvider provider) => BundledPackages.Install(provider, BundledPackages.For(provider).ToDictionary(p => p, _ => "1.0.0"));
    [Trait("Category", "Integration")]
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ReconnectResolvesCommandAgainWithoutRepeatingRecentUpdate()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-runtime-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id };
        var command = "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --config";
        var resolved = command; var resolutions = 0; var updates = 0;
        await using var runtime = new ChatRuntime(chat, workspace, store, command);
        runtime.ResolveCommand = () => { resolutions++; return resolved; };
        runtime.BackendMaintenance = new BackendUpdates(store, () => [workspace], (_, _) => runtime.HasBackendProcess, (_, update, _) =>
        { if (update == Installed(AgentProvider.Codex)) { Assert.False(runtime.HasBackendProcess); updates++; } return Task.FromResult(0); });
        await runtime.Connect();
        Assert.DoesNotContain(chat.ConfigOptions, c => c.Id == "mode");
        resolved += " --access";
        await runtime.Reconnect();
        Assert.Contains(chat.ConfigOptions, c => c.Id == "mode");
        Assert.Equal(2, resolutions); Assert.Equal(1, updates);
    }

    [Trait("Category", "CI")]
    [Fact]
    public async Task EnabledProvidersAreUpdatedOncePerEnvironmentAndDisabledSettingStopsChecks()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-updates-").FullName);
        Assert.True(BackendUpdates.Enabled(store));
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.EnabledKey(provider.Provider), provider.Provider == AgentProvider.OpenCode ? "1" : "0");
        Workspace[] workspaces = [new("one", "One", "/tmp", "Debian"), new("two", "Two", "/other", "Debian")];
        List<(string Host, string Command)> calls = [];
        var updater = new BackendUpdates(store, () => workspaces, (_, _) => false, (owner, command, _) =>
        { calls.Add((owner.Distro ?? "local", command)); return Task.FromResult(0); });
        var update = BackendUpdates.Plan(AgentProvider.OpenCode).Update;
        await updater.Check(TestContext.Current.CancellationToken);
        // Two workspaces share the Debian environment, so it is updated once.
        Assert.Equal(new[] { "local", "Debian" }, calls.Where(c => c.Command == update).Select(c => c.Host));
        var first = calls.Count;
        await updater.Check(TestContext.Current.CancellationToken); Assert.Equal(first, calls.Count);
        await updater.Check(TestContext.Current.CancellationToken, force: true); Assert.Equal(2 * first, calls.Count);
        store.Setting(BackendUpdates.EnabledKey, "0");
        var disabled = new BackendUpdates(store, () => workspaces, (_, _) => false, (_, _, _) => throw new Exception("Must not execute"));
        await disabled.Check(TestContext.Current.CancellationToken);
    }
    [Trait("Category", "CI")]
    [Fact]
    public async Task ReconnectUpdatesOnlyItsEnvironmentAndDefersWhileAnotherInstanceRuns()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-reconnect-").FullName);
        var active = false; List<string> commands = [];
        var owner = new Workspace("w", "WSL", "/tmp", "Debian");
        var updater = new BackendUpdates(store, () => [owner], (_, _) => active, (environment, command, _) =>
        {
            Assert.Equal("Debian", environment.Distro); commands.Add(command); return Task.FromResult(0);
        });
        await updater.BeforeStart(owner, AgentProvider.Codex, () => active = true, TestContext.Current.CancellationToken);
        Assert.Single(commands, c => c == Installed(AgentProvider.Codex));
        var startedSecond = false;
        await updater.BeforeStart(owner, AgentProvider.Codex, () => startedSecond = true, TestContext.Current.CancellationToken);
        Assert.True(startedSecond); Assert.Single(commands, c => c == Installed(AgentProvider.Codex));
        active = false;
        await updater.BeforeStart(owner, AgentProvider.Codex, () => { }, TestContext.Current.CancellationToken);
        Assert.Single(commands, c => c == Installed(AgentProvider.Codex));
    }
    [Trait("Category", "CI")]
    [Fact]
    public async Task SlowUpdateOfAnotherProviderDoesNotDelayStartingAnAgent()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-gates-").FullName);
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.EnabledKey(provider.Provider), provider.Provider is AgentProvider.Codex or AgentProvider.OpenCode ? "1" : "0");
        var updating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updater = new BackendUpdates(store, () => [], (_, _) => false, async (_, command, token) =>
        {
            if (command == Installed(AgentProvider.Codex)) { updating.SetResult(); await release.Task.WaitAsync(token); }
            return 0;
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var check = updater.Check(timeout.Token);
        try
        {
            await updating.Task.WaitAsync(timeout.Token);
            var started = false;
            await updater.BeforeStart(new Workspace("w", "Local", store.DirectoryPath), AgentProvider.OpenCode, () => started = true, timeout.Token);
            Assert.True(started);
            Assert.False(check.IsCompleted);
        }
        finally { release.TrySetResult(); await check; }
    }
    [Trait("Category", "CI")]
    [Fact]
    public async Task HungUpdateDoesNotBlockChatsFromStarting()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-hung-").FullName);
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updater = new BackendUpdates(store, () => [], (_, _) => false, async (_, command, token) =>
        {
            if (command == Installed(AgentProvider.Codex)) await hung.Task.WaitAsync(token);
            return 0;
        }) { LaunchWait = TimeSpan.FromMilliseconds(200) };
        var owner = new Workspace("w", "Local", store.DirectoryPath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            // The first chat starts the stuck update; neither it nor later chats wait on it.
            var started = 0;
            await updater.BeforeStart(owner, AgentProvider.Codex, () => started++, timeout.Token);
            await updater.BeforeStart(owner, AgentProvider.Codex, () => started++, timeout.Token);
            await updater.BeforeStart(owner, AgentProvider.Codex, () => started++, timeout.Token);
            Assert.Equal(3, started);
        }
        finally { hung.TrySetResult(); }
    }
    [Trait("Category", "CI")]
    [Fact]
    public async Task ExternalProcessesPreventBackendUpdates()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-external-").FullName);
        store.Setting(AgentProviders.EnabledKey(AgentProvider.Pi), "1");
        var probe = BackendUpdates.ProcessProbe(AgentProvider.Pi, OperatingSystem.IsWindows());
        List<string> commands = [];
        var updater = new BackendUpdates(store, () => [], (_, _) => false, (_, command, _) =>
        { commands.Add(command); return Task.FromResult(command == probe ? 1 : 0); });
        var started = false;
        await updater.BeforeStart(new("w", "Local", store.DirectoryPath), AgentProvider.Pi, () => started = true, TestContext.Current.CancellationToken);
        Assert.True(started); Assert.DoesNotContain(BackendUpdates.Plan(AgentProvider.Pi).Update!, commands);
    }
    [Trait("Category", "CI")]
    [Fact]
    public async Task EnablingMissingProviderInstallsEvenWhenAutomaticUpdatesAreOff()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-install-").FullName);
        store.Setting(BackendUpdates.EnabledKey, "0");
        store.Setting(AgentProviders.EnabledKey(AgentProvider.Pi), "1");
        var installers = BackendInstallers.For(AgentProvider.Pi, OperatingSystem.IsWindows()).Select(i => i.Command).ToArray();
        var version = BackendUpdates.Plan(AgentProvider.Pi).Executable + " --version";
        var installed = false; List<string> commands = [];
        var updater = new BackendUpdates(store, () => [], (_, _) => false, (_, command, _) =>
        {
            commands.Add(command);
            if (installers.Contains(command)) installed = true;
            return Task.FromResult(command == version && !installed ? 1 : 0);
        });
        await updater.Check(TestContext.Current.CancellationToken, force: true, installProvider: AgentProvider.Pi);
        Assert.True(installed);
        Assert.Contains(Installed(AgentProvider.Pi), commands);
        Assert.DoesNotContain(Installed(AgentProvider.Codex), commands);
    }
    [Trait("Category", "CI")]
    [Fact]
    public async Task BundledAgentsRefreshTheirPackagesWithoutTouchingSystemInstalls()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-bundled-").FullName);
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.EnabledKey(provider.Provider), provider.Provider is AgentProvider.Codex or AgentProvider.Claude or AgentProvider.Dirac ? "1" : "0");
        List<string> commands = [];
        var updater = new BackendUpdates(store, () => [], (_, _) => false, (_, command, _) => { commands.Add(command); return Task.FromResult(command.EndsWith(" --version") && command != "npm --version" ? 1 : 0); });
        await updater.Check(TestContext.Current.CancellationToken, force: true);
        foreach (var provider in new[] { AgentProvider.Codex, AgentProvider.Claude, AgentProvider.Dirac })
        {
            await updater.Check(TestContext.Current.CancellationToken, force: true, installProvider: provider);
            Assert.Contains(Installed(provider), commands);
        }
        Assert.DoesNotContain(commands, c => c is "codex --version" or "claude --version" or "dirac --version" || c.Contains("update") || c.Contains("install"));
    }
    // Each row makes the first N installers unavailable (their probe fails); exactly the next one runs.
    [Trait("Category", "CI")]
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task OpenCodeInstallFallsThroughToTheFirstAvailableInstaller(int unavailable)
    {
        var installers = BackendInstallers.For(AgentProvider.OpenCode, windows: false);
        var failingProbes = installers.Take(unavailable).Select(i => i.Probe).ToHashSet();
        var commands = new List<string>();
        var install = BackendInstallers.Install(new("w", "WSL", "/tmp", "Debian"), AgentProvider.OpenCode, (_, command, _) =>
        {
            commands.Add(command);
            return Task.FromResult(failingProbes.Contains(command) ? 1 : 0);
        }, TestContext.Current.CancellationToken);
        var ran = () => commands.Where(c => installers.Any(i => i.Command == c)).ToArray();
        if (unavailable == installers.Length)
        {
            await Assert.ThrowsAsync<IOException>(() => install);
            Assert.Empty(ran());
            return;
        }
        await install;
        Assert.Equal([installers[unavailable].Command], ran());
        Assert.DoesNotContain(commands, c => installers.Skip(unavailable + 1).Any(i => i.Probe == c));
    }
    [Trait("Category", "CI")]
    [Fact]
    public async Task BusyProvidersWaitAndFailuresDoNotStopOtherProviders()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-busy-").FullName);
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.EnabledKey(provider.Provider), "1");
        var busy = true; List<string> commands = [];
        var updater = new BackendUpdates(store, () => [], (_, p) => busy && p == AgentProvider.Codex, (_, command, _) =>
        { commands.Add(command); return Task.FromResult(command == Installed(AgentProvider.Claude) ? 1 : 0); });
        await updater.Check(TestContext.Current.CancellationToken);
        // Codex's bundled packages install side by side, so being busy does not hold them back.
        Assert.Contains(Installed(AgentProvider.Codex), commands);
        // Claude's failure does not stop any later provider.
        foreach (var update in AgentProviders.All.Select(p => BackendUpdates.Plan(p.Provider).Update).OfType<string>()) Assert.Contains(update, commands);
        Assert.Contains(Installed(AgentProvider.Pi), commands);
        Assert.StartsWith("Update failed", store.Setting("backendUpdate:local:Claude"));
        busy = false; await updater.Check(TestContext.Current.CancellationToken);
        Assert.Single(commands, c => c == Installed(AgentProvider.Codex));
        Assert.Single(commands, c => c == BackendUpdates.Plan(AgentProvider.OpenCode).Update);
    }
    [Trait("Category", "CI")]
    [Theory]
    [InlineData(AgentProvider.Codex, "npx -y @agentclientprotocol/codex-acp@1.13.0")]
    [InlineData(AgentProvider.Claude, "npx -y @agentclientprotocol/claude-agent-acp@0.76.0")]
    [InlineData(AgentProvider.Dirac, "npx -y dirac-cli@0.5.13 --acp")]
    [InlineData(AgentProvider.Pi, "npx -y pi-acp@0.0.33")]
    public void PreviousAdapterDefaultsMigrateToLatest(AgentProvider provider, string old)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-default-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        store.Setting(AgentProviders.CommandKey(provider, false), old);
        Assert.Contains("@latest", AgentProviders.Command(store, workspace, provider));
        store.Setting(AgentProviders.CommandKey(provider, false), "wrapper " + old);
        Assert.Equal("wrapper " + old, AgentProviders.Command(store, workspace, provider));
    }
    [Trait("Category", "CI")]
    [Fact]
    public async Task FindingUpdatesOnlyComparesVersionsAndApplyingUpdatesThatBackend()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-find-").FullName);
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.EnabledKey(provider.Provider), provider.Provider is AgentProvider.Codex or AgentProvider.Claude or AgentProvider.Pi ? "1" : "0");
        // Automatic updates are off: the manual check and update still work.
        store.Setting(BackendUpdates.EnabledKey, "0");
        var wsl = new Workspace("u", "Ubuntu", "/home/me", "Ubuntu-24.04");
        BundledPackages.Pin(store, new Workspace("x", "Local", "C:\\"), new Dictionary<string, string> { ["@agentclientprotocol/codex-acp"] = "2.1.1", ["@openai/codex"] = "0.160.1" });
        BundledPackages.Pin(store, wsl, new Dictionary<string, string> { ["@agentclientprotocol/claude-agent-acp"] = "0.84.0", ["pi-acp"] = "1.0.0" });
        List<string> commands = [];
        var latest = new Dictionary<string, string> { ["@agentclientprotocol/codex-acp"] = "2.1.2", ["@openai/codex"] = "0.160.1", ["@agentclientprotocol/claude-agent-acp"] = "0.84.0", ["pi-acp"] = "1.0.0", ["@earendil-works/pi-coding-agent"] = "0.9.0" };
        var updater = new BackendUpdates(store, () => [wsl], (_, _) => false,
            (_, command, _) => { lock (commands) commands.Add(command); return Task.FromResult(0); },
            (environment, command, _) => command == "pi --version" ? (environment.IsWsl ? Task.FromResult("pi 0.8.4") : Task.FromException<string>(new IOException("not found")))
                : Task.FromResult(latest[command["npm view ".Length..^" version".Length]]));
        List<BackendUpdates.AvailableUpdate> found = [];
        await updater.FindUpdates(update => { lock (found) found.Add(update); }, TestContext.Current.CancellationToken);
        Assert.Equal(["local Codex codex-acp 2.1.2", "Ubuntu-24.04 Pi pi 0.9.0"], found.Select(u => $"{u.Environment.Distro ?? "local"} {u.Provider} {u.Detail}").Order());
        Assert.Empty(commands);

        var codex = found.Single(u => u.Provider == AgentProvider.Codex);
        Assert.Null(await updater.Apply(codex, TestContext.Current.CancellationToken));
        Assert.Contains(commands, c => c.Contains("--package=@agentclientprotocol/codex-acp@2.1.2"));
        Assert.Equal("2.1.2", BundledPackages.Pinned(store, codex.Environment, "@agentclientprotocol/codex-acp"));
        Assert.DoesNotContain(commands, c => c.Contains("claude") || c.Contains("pi"));
        Assert.Equal("2.1.2", BackendUpdates.Newer("2.1.2", "codex-acp 2.1.1"));
        Assert.Null(BackendUpdates.Newer("0.160.1", "codex-cli 0.160.1"));
    }
}
