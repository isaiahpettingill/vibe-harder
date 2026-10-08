using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class MessageTimeTests
{
    [AvaloniaFact]
    public async Task TimestampsSurviveStorageAndShowOnlyWhenKnown()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("message-time-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        var time = DateTimeOffset.UtcNow.AddMinutes(-5);
        var message = new Message { Role = "tool", Text = "Run tests", Timestamp = time };
        store.SaveMessage(chat, message);
        store.LoadMessages(chat);
        Assert.Equal(time, Assert.Single(chat.Messages).Timestamp);
        var page = await store.ReadPageAsync(chat, null, 10, TestContext.Current.CancellationToken);
        Assert.Equal(time, Assert.Single(page).Timestamp);
        var view = new MessageView { Message = message };
        var window = new Window { Content = view }; window.Show();
        try
        {
            var label = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MessageTimestamp");
            Assert.True(label.IsVisible); Assert.False(string.IsNullOrWhiteSpace(label.Text));
            view.Message = new Message { Timestamp = null };
            Assert.False(label.IsVisible);
        }
        finally { window.Close(); }
    }
}
