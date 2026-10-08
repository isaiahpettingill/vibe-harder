namespace CodexManager.Tests;

public class ProviderAuthenticationTests
{
    [Trait("Category", "CI")]
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
    [Trait("Category", "CI")]
    [Theory]
    [InlineData(AgentProvider.VTCode, null, false)]
    [InlineData(AgentProvider.VTCode, "vtcode login", false)]
    [InlineData(AgentProvider.VTCode, "  vtcode login  ", false)]
    [InlineData(AgentProvider.VTCode, "vtcode login openrouter", true)]
    [InlineData(AgentProvider.Claude, "npx -y @anthropic-ai/claude-code@2.1.268 auth login", false)]
    [InlineData(AgentProvider.Claude, "claude setup-token", true)]
    [InlineData(AgentProvider.Dirac, "npx -y dirac-cli@0.5.13 auth", false)]
    public void SavedLoginCommandsReplaceOnlyKnownBrokenDefaults(AgentProvider provider, string? saved, bool kept)
    {
        using var store = new Store(Directory.CreateTempSubdirectory("vt-login-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath);
        var fallback = AgentProviders.LoginCommand(store, workspace, provider);
        if (saved is not null) store.Setting(provider + ":localLoginCommand", saved);
        Assert.Equal(kept ? saved : fallback, AgentProviders.LoginCommand(store, workspace, provider));
        if (!kept && saved is not null) Assert.NotEqual(saved.Trim(), fallback);
    }
}
