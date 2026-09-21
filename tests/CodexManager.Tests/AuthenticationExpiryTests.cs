using System.Text.Json;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class AuthenticationExpiryTests
{
    [Theory]
    [InlineData("OAuth token has expired")]
    [InlineData("refresh_token_reused")]
    [InlineData("invalid_grant")]
    [InlineData("authentication_error")]
    [InlineData("Credentials have expired")]
    public void RecognizesExpiredCredentialsInsideWrappedErrors(string message)
    {
        Assert.True(AgentProviders.IsAuthenticationError(new IOException("Request failed", new IOException(message))));
        Assert.False(ChatRuntime.IsNetworkFailure(new IOException(message)));
    }

    [Theory]
    [InlineData("context window exceeded")]
    [InlineData("Rate limit exceeded (429)")]
    [InlineData("Connection timed out")]
    [InlineData("Permission denied writing file")]
    public void DoesNotMistakeOtherFailuresForExpiredCredentials(string message) => Assert.False(AgentProviders.IsAuthenticationError(new IOException(message)));

    [AvaloniaFact]
    public async Task EveryProviderStopsForNestedExpiredAuthenticationAndKeepsInput()
    {
        foreach (var provider in AgentProviders.All)
        {
            using var store = new Store(Directory.CreateTempSubdirectory("auth-expiry-").FullName);
            var workspace = new Workspace("w", "Test", store.DirectoryPath); store.Save(workspace);
            var chat = new Chat { WorkspaceId = "w", Provider = provider.Provider }; store.Save(chat);
            await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --expired-auth");
            if (provider.Provider == AgentProvider.VTCode)
            {
                await runtime.Connect();
                chat.ConfigOptions = [new SessionConfig("provider", "Provider", "select", "fixture", [new("fixture", "Fixture")])];
            }
            runtime.Queue(new("queued", []));
            await runtime.Send("work", []);
            Assert.True(chat.NeedsLogin, provider.Name + ": " + chat.Status + " " + string.Join(" | ", chat.Messages.Where(m => m.Role == "system").Select(m => m.Text)));
            Assert.False(runtime.IsRecovering);
            Assert.Equal("work", chat.Draft);
            Assert.Single(chat.QueuedInputs);
        }
    }

    [Fact]
    public void RecognizesStructuredUnauthorizedStatus()
    {
        using var json = JsonDocument.Parse("""{"code":-32603,"message":"Request failed","data":{"statusCode":401}}""");
        Assert.True(AgentProviders.IsAuthenticationError(new AcpException(json.RootElement)));
    }
}
