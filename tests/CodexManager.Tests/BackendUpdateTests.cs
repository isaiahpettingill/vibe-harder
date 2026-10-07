using Avalonia.Controls;
namespace CodexManager.Tests;

public class BackendUpdateTests
{
    // Injected executors resolve every bundled package to 1.0.0.
    private static string Installed(AgentProvider provider) => BundledPackages.Install(provider, BundledPackages.For(provider).ToDictionary(p => p, _ => "1.0.0"));
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
        await updater.Check(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls.Count(c => c.Command == "opencode upgrade"));
        Assert.Equal(new[] { "local", "Debian" }, calls.Where(c => c.Command == "opencode upgrade").Select(c => c.Host));
        await updater.Check(TestContext.Current.CancellationToken); Assert.Equal(6, calls.Count);
        await updater.Check(TestContext.Current.CancellationToken, force: true); Assert.Equal(12, calls.Count);
        store.Setting(BackendUpdates.EnabledKey, "0");
        var disabled = new BackendUpdates(store, () => workspaces, (_, _) => false, (_, _, _) => throw new Exception("Must not execute"));
        await disabled.Check(TestContext.Current.CancellationToken);
    }
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
        Assert.Contains("when idle", updater.LastSummary);
    }
    [Fact]
    public async Task EnablingMissingProviderInstallsEvenWhenAutomaticUpdatesAreOff()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-install-").FullName);
        store.Setting(BackendUpdates.EnabledKey, "0");
        store.Setting(AgentProviders.EnabledKey(AgentProvider.Pi), "1");
        var installed = false; List<string> commands = [];
        var updater = new BackendUpdates(store, () => [], (_, _) => false, (_, command, _) =>
        {
            commands.Add(command);
            if (command.StartsWith("npm install -g --ignore-scripts @earendil-works/pi-coding-agent")) installed = true;
            return Task.FromResult(command == "pi --version" && !installed ? 1 : 0);
        });
        await updater.Check(TestContext.Current.CancellationToken, force: true, installProvider: AgentProvider.Pi);
        Assert.True(installed);
        Assert.Contains(commands, c => c.Contains("--package=pi-acp@1.0.0"));
        Assert.DoesNotContain(Installed(AgentProvider.Codex), commands);
    }
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
        Assert.Equal("Enabled backend checks completed.", updater.LastSummary);
    }
    [Fact]
    public async Task OpenCodeFallsBackAfterUnusableBashAndMissingNpm()
    {
        var commands = new List<string>();
        await BackendInstallers.Install(new("w", "WSL", "/tmp", "Debian"), AgentProvider.OpenCode, (_, command, _) =>
        {
            commands.Add(command);
            return Task.FromResult(command == "bash --version" || command == "npm --version" ? 1 : 0);
        }, TestContext.Current.CancellationToken);
        Assert.Contains("bun install -g --trust @opencode/cli@latest", commands);
        Assert.DoesNotContain(commands, c => c.StartsWith("npm install"));
        Assert.DoesNotContain("brew --version", commands);
    }
    [Fact]
    public async Task BusyProvidersWaitAndFailuresDoNotStopOtherProviders()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-busy-").FullName);
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.EnabledKey(provider.Provider), "1");
        var busy = true; List<string> commands = [];
        var updater = new BackendUpdates(store, () => [], (_, p) => busy && p == AgentProvider.Codex, (_, command, _) =>
        { commands.Add(command); return Task.FromResult(command == Installed(AgentProvider.Claude) ? 1 : 0); });
        await updater.Check(TestContext.Current.CancellationToken);
        Assert.Contains(Installed(AgentProvider.Codex), commands);
        Assert.Contains("opencode upgrade", commands); Assert.Contains("vtcode update", commands);
        Assert.Contains("npm install -g --ignore-scripts @earendil-works/pi-coding-agent@latest", commands);
        Assert.Contains("npm install -g cline@latest", commands);
        Assert.Contains(commands, c => c.Contains("--package=pi-acp@1.0.0"));
        Assert.StartsWith("Update failed", store.Setting("backendUpdate:local:Claude"));
        busy = false; await updater.Check(TestContext.Current.CancellationToken);
        Assert.Single(commands, c => c == Installed(AgentProvider.Codex));
        Assert.Single(commands, c => c == "opencode upgrade");
    }
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
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task BackendUpdatesAppearAsTrayItemsLeftOfTheAppUpdate()
    {
        var directory = Directory.CreateTempSubdirectory("backend-tray-").FullName; Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0"); store.Setting(BackendUpdates.EnabledKey, "0");
        var window = new MainWindow(store) { Width = 1000, Height = 500 }; window.Show();
        try
        {
            var show = typeof(MainView).GetMethod("ShowBackendUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            show.Invoke(window.View, [new BackendUpdates.AvailableUpdate(new Workspace("l", "Local", directory), AgentProvider.Claude, "claude-agent-acp 0.85.0")]);
            show.Invoke(window.View, [new BackendUpdates.AvailableUpdate(new Workspace("u", "Ubuntu", "/home/me", "Ubuntu-24.04"), AgentProvider.Claude, "claude-agent-acp 0.85.0")]);
            show.Invoke(window.View, [new BackendUpdates.AvailableUpdate(new Workspace("u", "Ubuntu", "/home/me", "Ubuntu-24.04"), AgentProvider.Claude, "claude-agent-acp 0.85.1")]);
            window.UpdateLayout();
            var panel = UiTests.Named<StackPanel>(window, "BackendUpdates");
            var buttons = panel.Children.OfType<Button>().ToArray();
            Assert.Equal(["BackendUpdate_local_Claude", "BackendUpdate_Ubuntu-24.04_Claude"], buttons.Select(b => b.Name));
            Assert.Contains("0.85.1", ToolTip.GetTip(buttons[1]) as string);
        }
        finally { window.RequestExit(); var until = DateTime.UtcNow.AddSeconds(10); while (window.IsVisible && DateTime.UtcNow < until) await Task.Delay(25); }
    }
}
