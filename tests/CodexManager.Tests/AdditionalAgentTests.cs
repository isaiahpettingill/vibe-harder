using System.Text.Json.Nodes;

namespace CodexManager.Tests;

public class AdditionalAgentTests
{
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
        Assert.Equal(6, list!["providers"]!.AsArray().Count);
        Assert.Equal(6, AgentProviders.Enabled(store).Count());
    }

    [Fact]
    public void VtCodeEnablesAcpForLocalAndWslProcesses()
    {
        var local = AgentProviders.Start(new Workspace("w", "Test", Path.GetTempPath()), "vtcode acp", AgentProvider.VTCode);
        Assert.Equal("1", local.Environment["VT_ACP_ENABLED"]);
        Assert.Equal("1", local.Environment["VT_ACP_ZED_ENABLED"]);
        if (!OperatingSystem.IsWindows()) return;
        var wsl = AgentProviders.Start(new Workspace("w", "Test", "/tmp", "Debian"), "vtcode acp", AgentProvider.VTCode);
        Assert.Contains("exec env 'VT_ACP_ENABLED=1' 'VT_ACP_ZED_ENABLED=1' vtcode acp", wsl.ArgumentList.Last());
    }

    [Theory]
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
