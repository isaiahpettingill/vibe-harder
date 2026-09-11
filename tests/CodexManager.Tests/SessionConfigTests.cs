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
        }
    }
}
