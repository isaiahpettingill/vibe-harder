using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class MessageProviderTests
{
    [Trait("Category", "CI")]
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
            Assert.Equal(provider.Provider, history.Provider);
            apply.Invoke(view, [new JsonArray(row)]);
            Assert.Equal(provider.Provider, messages.Last().Provider);
        }
    }
}
