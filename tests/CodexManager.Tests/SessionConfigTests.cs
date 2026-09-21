using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class SessionConfigTests
{
    [AvaloniaFact]
    public async Task SavedAccessWinsOverAdapterDefaultAndIsInheritedByWorkspace()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("access-defaults-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        // Older clients could save the adapter's default in the general setting
        // while the dedicated access setting still held the user's selection.
        store.Setting($"chatConfig:Codex:{chat.Id}:mode", "ask");
        store.Setting($"sessionAccess:{chat.Id}:mode", "full-access");
        var command = "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --config --access --reset-access";
        await using var runtime = new ChatRuntime(chat, workspace, store, command);
        await runtime.Connect();
        Assert.Equal("full-access", chat.ConfigOptions.Single(c => c.Id == "mode").Current);
        await runtime.SetConfig(chat.ConfigOptions.Single(c => c.Id == "mode"), "ask");
        var next = new Chat { WorkspaceId = "w" }; store.Save(next);
        await using var nextRuntime = new ChatRuntime(next, workspace, store, command);
        await nextRuntime.Connect();
        Assert.Equal("ask", next.ConfigOptions.Single(c => c.Id == "mode").Current);
        await runtime.SetConfig(chat.ConfigOptions.Single(c => c.Id == "mode"), "full-access");
        await nextRuntime.Reconnect();
        Assert.Equal("ask", next.ConfigOptions.Single(c => c.Id == "mode").Current);
        await runtime.Reconnect();
        Assert.Equal("full-access", chat.ConfigOptions.Single(c => c.Id == "mode").Current);
    }

    [AvaloniaFact]
    public async Task ModelPreferenceUsesChatThenWorkspaceThenGlobal()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("model-precedence-").FullName);
        var workspace = new Workspace("one", "One", store.DirectoryPath);
        var other = new Workspace("two", "Two", store.DirectoryPath);
        store.Save(workspace); store.Save(other);
        var command = "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --config";
        var first = new Chat { WorkspaceId = workspace.Id };
        await using var runtime = new ChatRuntime(first, workspace, store, command);
        await runtime.Connect();
        await runtime.SetConfig(first.ConfigOptions.Single(c => c.Id == "model"), "large");
        var second = new Chat { WorkspaceId = other.Id };
        await using var secondRuntime = new ChatRuntime(second, other, store, command);
        await secondRuntime.Connect();
        Assert.Equal("large", second.ConfigOptions.Single(c => c.Id == "model").Current);
        await secondRuntime.SetConfig(second.ConfigOptions.Single(c => c.Id == "model"), "small");
        var third = new Chat { WorkspaceId = workspace.Id };
        await using var thirdRuntime = new ChatRuntime(third, workspace, store, command);
        await thirdRuntime.Connect();
        Assert.Equal("large", third.ConfigOptions.Single(c => c.Id == "model").Current);
        await thirdRuntime.SetConfig(third.ConfigOptions.Single(c => c.Id == "model"), "small");
        await runtime.Reconnect();
        Assert.Equal("large", first.ConfigOptions.Single(c => c.Id == "model").Current);
    }

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
            // A fresh adapter process reports its small-model default on load.
            // Resume must restore the most recent explicit selection instead.
            await runtime.Reconnect();
            Assert.Equal("large", chat.ConfigOptions.Single(c => c.Id == "model").Current);
            Assert.Equal("high", chat.ConfigOptions.Single(c => c.Id == "reasoning").Current);
            Assert.Equal("true", chat.ConfigOptions.Single(c => c.Id == "fast").Current);
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
