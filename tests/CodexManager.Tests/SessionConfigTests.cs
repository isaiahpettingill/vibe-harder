using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class SessionConfigTests
{
    [AvaloniaFact]
    public async Task ProviderOptionsSupportModelsReasoningAndBooleanFastMode()
    {
        foreach (var provider in AgentProviders.All)
        {
            var directory = Path.Combine(Path.GetTempPath(), "codex-config", Guid.NewGuid().ToString("N"));
            using var store = new Store(directory);
            var workspace = new Workspace("w", "Config", directory); store.Save(workspace);
            var chat = new Chat { WorkspaceId = workspace.Id, Provider = provider.Provider }; store.Save(chat);
            var command = "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --config";
            await using var runtime = new ChatRuntime(chat, workspace, store, command);
            await runtime.Connect(); Assert.Equal(3, chat.ConfigOptions.Count);
            await runtime.SetConfig(chat.ConfigOptions.Single(c => c.Id == "model"), "large");
            await runtime.SetConfig(chat.ConfigOptions.Single(c => c.Id == "reasoning"), "high");
            await runtime.SetConfig(chat.ConfigOptions.Single(c => c.Id == "fast"), "true");
            Assert.Equal("large", chat.ConfigOptions.Single(c => c.Id == "model").Current);
            Assert.Equal("high", chat.ConfigOptions.Single(c => c.Id == "reasoning").Current);
            Assert.Equal("true", chat.ConfigOptions.Single(c => c.Id == "fast").Current);
            Assert.False(runtime.IsConfiguring);
            var next = new Chat { WorkspaceId = workspace.Id, Provider = provider.Provider }; store.Save(next);
            await using var nextRuntime = new ChatRuntime(next, workspace, store, command);
            await nextRuntime.Connect();
            Assert.Equal("large", next.ConfigOptions.Single(c => c.Id == "model").Current);
            Assert.Equal("high", next.ConfigOptions.Single(c => c.Id == "reasoning").Current);
            Assert.Equal("true", next.ConfigOptions.Single(c => c.Id == "fast").Current);
        }
    }
    [AvaloniaFact]
    public async Task DiracDefaultsCanBeTurnedOffAndStayOffForNewChats()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("dirac-defaults-").FullName);
        var workspace = new Workspace("w", "Config", store.DirectoryPath); store.Save(workspace);
        var command = "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --config --dirac-defaults";
        var first = new Chat { WorkspaceId = "w", Provider = AgentProvider.Dirac };
        await using var runtime = new ChatRuntime(first, workspace, store, command);
        await runtime.Connect();
        foreach (var id in new[] { "yolo", "auto_approve" })
        {
            Assert.Equal("true", first.ConfigOptions.Single(c => c.Id == id).Current);
            await runtime.SetConfig(first.ConfigOptions.Single(c => c.Id == id), "false");
        }
        var second = new Chat { WorkspaceId = "w", Provider = AgentProvider.Dirac };
        await using var next = new ChatRuntime(second, workspace, store, command);
        await next.Connect();
        Assert.All(second.ConfigOptions.Where(c => c.Id is "yolo" or "auto_approve"), c => Assert.Equal("false", c.Current));
    }

}
