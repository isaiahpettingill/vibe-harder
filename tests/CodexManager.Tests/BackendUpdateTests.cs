namespace CodexManager.Tests;

public class BackendUpdateTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ReconnectResolvesCommandAgainAndUpdatesBeforeStartingTheReplacement()
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
        { if (update == "codex update") { Assert.False(runtime.HasBackendProcess); updates++; } return Task.FromResult(0); });
        await runtime.Connect();
        Assert.DoesNotContain(chat.ConfigOptions, c => c.Id == "mode");
        resolved += " --access";
        await runtime.Reconnect();
        Assert.Contains(chat.ConfigOptions, c => c.Id == "mode");
        Assert.Equal(2, resolutions); Assert.Equal(2, updates);
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
        Assert.Single(commands, c => c == "codex update");
        var startedSecond = false;
        await updater.BeforeStart(owner, AgentProvider.Codex, () => startedSecond = true, TestContext.Current.CancellationToken);
        Assert.True(startedSecond); Assert.Single(commands, c => c == "codex update");
        active = false;
        await updater.BeforeStart(owner, AgentProvider.Codex, () => { }, TestContext.Current.CancellationToken);
        Assert.Equal(2, commands.Count(c => c == "codex update"));
    }
    [Fact]
    public async Task ExternalProcessesPreventBackendUpdates()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("backend-external-").FullName);
        var probe = BackendUpdates.ProcessProbe(AgentProvider.Codex, OperatingSystem.IsWindows());
        List<string> commands = [];
        var updater = new BackendUpdates(store, () => [], (_, _) => false, (_, command, _) =>
        { commands.Add(command); return Task.FromResult(command == probe ? 1 : 0); });
        var started = false;
        await updater.BeforeStart(new("w", "Local", store.DirectoryPath), AgentProvider.Codex, () => started = true, TestContext.Current.CancellationToken);
        Assert.True(started); Assert.DoesNotContain("codex update", commands);
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
        Assert.Contains(commands, c => c.Contains("--package=pi-acp@latest"));
        Assert.DoesNotContain("codex update", commands);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeInstallersUseTheirPlatformDefaults(bool windows)
    {
        var codex = BackendInstallers.For(AgentProvider.Codex, windows).Single().Command;
        var claude = BackendInstallers.For(AgentProvider.Claude, windows).Single().Command;
        Assert.Contains(windows ? "install.ps1" : "install.sh", codex);
        Assert.Contains(windows ? "install.ps1" : "install.sh", claude);
        var opencode = BackendInstallers.For(AgentProvider.OpenCode, windows);
        Assert.Contains("opencode.ai/v2/install", opencode[0].Command);
        Assert.Equal("npm install -g @opencode/cli@latest", opencode[1].Command);
        Assert.Equal("bun install -g --trust @opencode/cli@latest", opencode[2].Command);
        Assert.Equal("brew install anomalyco/tap/opencode-v2", opencode[3].Command);
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
        { commands.Add(command); return Task.FromResult(command == "claude update" ? 1 : 0); });
        await updater.Check(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("codex update", commands);
        Assert.Contains("opencode upgrade", commands); Assert.Contains("vtcode update", commands);
        Assert.Contains("npm install -g --ignore-scripts @earendil-works/pi-coding-agent@latest", commands);
        Assert.Contains("npm install -g cline@latest", commands);
        Assert.Contains(commands, c => c.Contains("--package=pi-acp@latest"));
        Assert.StartsWith("Update failed", store.Setting("backendUpdate:local:Claude"));
        busy = false; await updater.Check(TestContext.Current.CancellationToken);
        Assert.Contains("codex update", commands);
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
}
