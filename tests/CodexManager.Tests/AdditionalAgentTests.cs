using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class AdditionalAgentTests
{
    [Trait("Category", "CI")]
    [Fact]
    public async Task HostAdvertisesOnlyEnabledProvidersAndRejectsDisabledCreation()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("additional-agents-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        var chats = new List<Chat>();
        using var service = new SessionService(store, [workspace], chats, (_, _) => throw new InvalidOperationException("Must not start an agent"));
        async Task<string[]> Listed() => (await service.Handle(new JsonObject { ["method"] = "list" }))!["providers"]!.AsArray().Select(p => p!.GetValue<string>()).Order().ToArray();
        var additional = AgentProviders.All.Where(p => AgentProviders.IsAdditional(p.Provider)).Select(p => p.Provider.ToString()).ToArray();
        var standard = AgentProviders.All.Select(p => p.Provider.ToString()).Except(additional).Order().ToArray();
        Assert.NotEmpty(additional);
        Assert.Equal(standard, await Listed());
        foreach (var provider in additional)
            await Assert.ThrowsAsync<IOException>(() => service.Handle(new JsonObject { ["method"] = "create", ["workspaceId"] = "w", ["provider"] = provider }));
        Assert.Empty(chats);
        store.Setting("additionalAgentsEnabled", "1");
        Assert.Equal(AgentProviders.All.Select(p => p.Provider.ToString()).Order(), await Listed());
    }

    [Trait("Category", "CI")]
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
        var wsl = OperatingSystem.IsWindows() ? AgentProviders.Start(new Workspace("w", "Test", "/tmp", "Debian"), "vtcode acp", AgentProvider.VTCode).ArgumentList.Last() : null;
        // WSL processes do not inherit the Windows environment, so the switches travel on the command line.
        foreach (var name in new[] { "VT_ACP_ENABLED", "VT_ACP_ZED_ENABLED" })
        {
            Assert.Equal("1", local.Environment[name]);
            if (wsl is not null) Assert.Contains($"'{name}=1'", wsl);
        }
        if (wsl is not null) Assert.EndsWith(" vtcode acp", wsl);
    }

}
