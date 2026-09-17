using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class MessageProviderTests
{
    [Fact]
    public void UnknownMessagesUseAgentAndKnownMessagesUseTheirProvider()
    {
        Assert.Equal("AGENT", new Message().Label);
        Assert.Equal("AGENT", new Message { Provider = (AgentProvider)999 }.Label);
        foreach (var provider in AgentProviders.All)
            Assert.Equal(provider.Name.ToUpperInvariant(), new Message { Provider = provider.Provider }.Label);
        Assert.Equal("YOU", new Message { Role = "user" }.Label);
        Assert.Equal("SESSION", new Message { Role = "system" }.Label);
    }

    [AvaloniaFact]
    public void RemoteLiveAndHistoryMessagesRetainTheSelectedProvider()
    {
        using var view = new RemoteView(new RemoteHost("Test", "localhost", 1, "", ""));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var providerField = typeof(RemoteView).GetField("messageProvider", flags)!;
        var read = typeof(RemoteView).GetMethod("ReadMessage", flags)!;
        var apply = typeof(RemoteView).GetMethod("ApplyMessages", flags)!;
        var messages = (ObservableCollection<Message>)typeof(RemoteView).GetField("messages", flags)!.GetValue(view)!;
        foreach (var provider in AgentProviders.All)
        {
            providerField.SetValue(view, provider.Provider);
            var row = new JsonObject { ["id"] = provider.Name, ["role"] = "assistant", ["text"] = "Hello", ["sequence"] = 1 };
            var history = (Message)read.Invoke(view, [row])!;
            Assert.Equal(provider.Name.ToUpperInvariant(), history.Label);
            apply.Invoke(view, [new JsonArray(row)]);
            Assert.Equal(history.Label, messages.Last().Label);
        }
    }
}
