using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class VtCodeLaunchTests
{
    [Theory]
    [InlineData("vtcode acp", "vtcode --provider openai --api-key-env OPENAI_API_KEY acp")]
    [InlineData("\"C:\\Agent Tools\\vtcode.exe\" acp", "\"C:\\Agent Tools\\vtcode.exe\" --provider openai --api-key-env OPENAI_API_KEY acp")]
    [InlineData("/home/user/.local/bin/vtcode --config /tmp/agent.toml acp", "/home/user/.local/bin/vtcode --provider openai --api-key-env OPENAI_API_KEY --config /tmp/agent.toml acp")]
    [InlineData("vtcode acp --provider=openai", "vtcode --api-key-env OPENAI_API_KEY acp --provider=openai")]
    [InlineData("vtcode acp --provider openrouter", "vtcode acp --provider openrouter")]
    [InlineData("vtcode acp --api-key-env COMPANY_KEY", "vtcode acp --api-key-env COMPANY_KEY")]
    [InlineData("node custom-bridge.mjs", "node custom-bridge.mjs")]
    [InlineData("vtcode acp; echo done", "vtcode acp; echo done")]
    public void InitializesOpenAiAuthWithoutOverwritingExplicitRoutes(string command, string expected) =>
        Assert.Equal(expected, VtCodeLaunch.WithAuthentication(command, new HashSet<string> { "openai" }));

    [Fact]
    public void DoesNotGuessAuthenticationWhenProbeFailsOrOpenAiIsUnavailable()
    {
        Assert.Equal("vtcode acp", VtCodeLaunch.WithAuthentication("vtcode acp", null));
        Assert.Equal("vtcode acp", VtCodeLaunch.WithAuthentication("vtcode acp", new HashSet<string> { "anthropic" }));
    }
    [AvaloniaFact]
    public async Task CompletedPromptClearsStaleAuthenticationState()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("auth-success-").FullName);
        var workspace = new Workspace("w", "Auth", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id, NeedsLogin = true }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var cleared = false; runtime.AuthenticationSucceeded += () => cleared = true;
        await runtime.Send("hello", []);
        Assert.Equal("Ready", chat.Status); Assert.False(chat.NeedsLogin); Assert.True(cleared);
    }
    [AvaloniaFact]
    public async Task PlanUpdatesStaySeparateFromStreamedAnswersAndPreviousTurns()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("vt-plan-").FullName);
        var workspace = new Workspace("w", "Plans", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        await runtime.Send("plan", []);
        Assert.Equal("- [x] Answer the user", Assert.Single(chat.Messages, m => m.Role == "plan").Text);
        Assert.Equal("Hello **world**", Assert.Single(chat.Messages, m => m.Role == "assistant").Text);
        await runtime.Send("plan", []);
        Assert.Equal(2, chat.Messages.Count(m => m.Role == "plan"));
        var saved = await store.ReadPageAsync(chat);
        Assert.All(saved.Where(m => m.Role == "plan"), m => Assert.Equal("- [x] Answer the user", m.Text));
    }
    [AvaloniaFact]
    public async Task ExistingWslChatGptLoginCompletesAnActualPromptWhenExplicitlyRequested()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VIBE_TEST_VTCODE") is not { } distro ||
            Environment.GetEnvironmentVariable("VIBE_TEST_VTCODE_WORKSPACE") is not { } path) return;
        using var store = new Store(Directory.CreateTempSubdirectory("vt-live-auth-").FullName);
        var workspace = new Workspace("w", "VT auth diagnostic", path, distro); store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id, Provider = AgentProvider.VTCode, NeedsLogin = true }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "vtcode acp");
        await runtime.Send("Authentication diagnostic only. Reply exactly OK. Do not inspect or modify files, invoke tools, or run commands.", []).WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.Equal("Ready", chat.Status); Assert.False(chat.NeedsLogin);
        Assert.Equal("openai", chat.ConfigOptions.Single(c => c.Id == "provider").Current);
        Assert.DoesNotContain("mimo", chat.ConfigOptions.Single(c => c.Id == "model").Current);
        Assert.Contains(chat.Messages, m => m.Text.Trim() == "OK");
    }
}
