using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class AdditionalAgentTests
{
    [Fact]
    public void ClineUsesDocumentedCommandsAndRecognizesItsLoginPrompt()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("cline-setup-").FullName);
        var workspace = new Workspace("w", "Cline", store.DirectoryPath);
        Assert.Equal("cline --acp", AgentProviders.Command(store, workspace, AgentProvider.Cline));
        Assert.Equal("cline auth", AgentProviders.LoginCommand(store, workspace, AgentProvider.Cline));
        Assert.True(AgentProviders.IsAuthenticationError(new IOException("Call authenticate before starting a session")));
    }
    [Fact]
    public async Task HostAdvertisesOnlyEnabledProvidersAndRejectsDisabledCreation()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("additional-agents-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        var chats = new List<Chat>();
        using var service = new SessionService(store, [workspace], chats, (_, _) => throw new InvalidOperationException("Must not start an agent"));
        var list = await service.Handle(new JsonObject { ["method"] = "list" });
        Assert.Equal(3, list!["providers"]!.AsArray().Count);
        foreach (var option in AgentProviders.All.Where(p => AgentProviders.IsAdditional(p.Provider)))
        {
            Assert.False(AgentProviders.IsEnabled(store, option.Provider));
            await Assert.ThrowsAsync<IOException>(() => service.Handle(new JsonObject { ["method"] = "create", ["workspaceId"] = "w", ["provider"] = option.Provider.ToString() }));
            Assert.False(string.IsNullOrWhiteSpace(AgentProviders.LoginCommand(store, workspace, option.Provider)));
        }
        Assert.Empty(chats);
        store.Setting("additionalAgentsEnabled", "1");
        list = await service.Handle(new JsonObject { ["method"] = "list" });
        Assert.Equal(7, list!["providers"]!.AsArray().Count);
        Assert.Equal(7, AgentProviders.Enabled(store).Count());
    }

    [Fact]
    public async Task IndividualSwitchesOverrideLegacySettingAndCanDisableEveryProvider()
    {
        var directory = Directory.CreateTempSubdirectory("agent-switches-").FullName;
        using (var store = new Store(directory))
        {
            store.Setting("additionalAgentsEnabled", "1");
            var workspace = new Workspace("w", "Test", directory);
            var chats = new List<Chat>();
            using var service = new SessionService(store, [workspace], chats, (_, _) => throw new InvalidOperationException("Must not start an agent"));
            foreach (var option in AgentProviders.All)
            {
                Assert.True(AgentProviders.IsEnabled(store, option.Provider));
                store.Setting(AgentProviders.EnabledKey(option.Provider), "0");
                Assert.False(AgentProviders.IsEnabled(store, option.Provider));
                await Assert.ThrowsAsync<IOException>(() => service.Handle(new JsonObject { ["method"] = "create", ["workspaceId"] = "w", ["provider"] = option.Provider.ToString() }));
            }
            var list = await service.Handle(new JsonObject { ["method"] = "list" });
            Assert.Empty(list!["providers"]!.AsArray());
            Assert.Empty(chats);
            store.Setting(AgentProviders.EnabledKey(AgentProvider.Pi), "1");
            Assert.Equal(AgentProvider.Pi, Assert.Single(AgentProviders.Enabled(store)).Provider);
            await store.FlushAsync();
        }
        using var reopened = new Store(directory);
        Assert.Equal(AgentProvider.Pi, Assert.Single(AgentProviders.Enabled(reopened)).Provider);
    }

    [Fact]
    public void VtCodeEnablesAcpForLocalAndWslProcesses()
    {
        var local = AgentProviders.Start(new Workspace("w", "Test", Path.GetTempPath()), "vtcode acp", AgentProvider.VTCode);
        Assert.Equal("1", local.Environment["VT_ACP_ENABLED"]);
        Assert.Equal("1", local.Environment["VT_ACP_ZED_ENABLED"]);
        if (!OperatingSystem.IsWindows()) return;
        var wsl = AgentProviders.Start(new Workspace("w", "Test", "/tmp", "Debian"), "vtcode acp", AgentProvider.VTCode);
        Assert.Contains("exec env 'VT_ACP_ENABLED=1' 'VT_ACP_ZED_ENABLED=1' 'NO_COLOR=1' vtcode acp", wsl.ArgumentList.Last());
    }

    [Theory]
    [InlineData(AgentProvider.Cline, "cline")]
    [InlineData(AgentProvider.VTCode, "vtcode")]
    [InlineData(AgentProvider.Dirac, "dirac")]
    [InlineData(AgentProvider.Pi, "pi")]
    [InlineData(AgentProvider.Pi, "pi-acp")]
    public void MissingOptionalAgentHasInstallGuidance(AgentProvider provider, string binary)
    {
        var installation = AgentInstallation.FromError(provider, new IOException($"spawn {binary} ENOENT"));
        Assert.NotNull(installation);
        Assert.StartsWith("Install", installation.Message);
        Assert.StartsWith("https://", installation.Url);
    }
}
