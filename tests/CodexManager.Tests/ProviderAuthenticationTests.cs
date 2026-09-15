namespace CodexManager.Tests;

public class ProviderAuthenticationTests
{
    [Fact]
    public void VtStatusDistinguishesCredentialsFromMissingAndLocalOrManagedPlaceholders()
    {
        var ids = ProviderAuthentication.ReadVtCode("OpenAI: authenticated (ChatGPT)\nOpenRouter: not authenticated\nGitHub Copilot: CLI unavailable", "  Anthropic (anthropic)\n    Status: Ready\n    Source: OS keyring / encrypted file\n  Local (ollama)\n    Status: Ready\n    Source: Local — no key required\n  Copilot (copilot)\n    Status: Ready\n    Source: Managed auth (external CLI)");
        Assert.Equal(new[] { "anthropic", "openai" }, ids.Order().ToArray());
        var option = new SessionConfig("provider", "Provider", "select", "openrouter", [new("openrouter", "OpenRouter"), new("openai", "OpenAI")]);
        Assert.Equal("openai", Assert.Single(ProviderAuthentication.Filter([option], ids).Single().Values).Value);
        Assert.Empty(ProviderAuthentication.Filter([option], []).Single().Values);
        Assert.Equal(2, ProviderAuthentication.Filter([option], null).Single().Values.Count);
    }
    [Fact]
    public void VtLoginMigratesTheBrokenDefaultButKeepsCustomCommands()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("vt-login-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        Assert.Equal("vtcode login openai", AgentProviders.LoginCommand(store, workspace, AgentProvider.VTCode));
        store.Setting("VTCode:localLoginCommand", "vtcode login");
        Assert.Equal("vtcode login openai", AgentProviders.LoginCommand(store, workspace, AgentProvider.VTCode));
        store.Setting("VTCode:localLoginCommand", "vtcode login openrouter");
        Assert.Equal("vtcode login openrouter", AgentProviders.LoginCommand(store, workspace, AgentProvider.VTCode));
    }
    [Theory]
    [InlineData("mode", "Mode", "mode")]
    [InlineData("auto_approve", "Approve for me", "auto-approve")]
    [InlineData("yolo", "YOLO", "yolo")]
    [InlineData("provider", "Provider", "provider")]
    [InlineData("service_tier", "Service tier", "speed")]
    [InlineData("thought_level", "Effort level", "reasoning")]
    [InlineData("thinking_budget", "Thinking budget", "budget")]
    public void SessionControlsUseSemanticIcons(string id, string name, string icon) =>
        Assert.Equal(icon, OptionContent.IconFor(new SessionConfig(id, name, "select", "", [])));
}
