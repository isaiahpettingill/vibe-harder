using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CodexManager.Tests;

public class RemoteQueueMenuTests
{
    [AvaloniaFact]
    public async Task MessageMenuOffersCopyAndAttachmentsWithoutHistorySupport()
    {
        var anchor = new Button(); var window = new Window { Content = anchor }; window.Show();
        try
        {
            var message = new Message { Role = "user", Text = "Copy this message" };
            message.Attachments.Add(new Attachment("notes.txt", "text/plain", "notes"));
            var menu = await HistoryActions.Show(anchor, message, _ => Task.FromResult<JsonNode?>(new JsonObject { ["reason"] = "Unsupported" }), _ => { });
            Assert.Equal(new[] { "Copy text", "Show attachments" }, menu.Items.OfType<MenuItem>().Select(i => i.Header));
            menu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            using var data = await window.Clipboard!.TryGetDataAsync();
            Assert.Equal(message.Text, await data!.TryGetTextAsync()); menu.Hide();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RemoteQueueEditPreservesIdentityAttachmentsAndOrder()
    {
        using var store = new Store(Path.Combine(Path.GetTempPath(), "queue-edit-" + Guid.NewGuid().ToString("N")));
        var workspace = new Workspace("w", "Test", store.DirectoryPath); var chat = new Chat { Id = "c", WorkspaceId = "w" };
        store.Save(workspace); store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "unused");
        var input = new PendingInput("Original", [new Attachment("notes.txt", "text/plain", "notes")]); runtime.Queue(input); runtime.Queue(new("Next", []));
        using var service = new SessionService(store, [workspace], [chat], (_, _) => runtime);
        await service.Handle(new() { ["method"] = "queue/edit", ["chatId"] = "c", ["queueId"] = input.Id, ["text"] = "Edited" });
        Assert.Equal(input.Id, chat.QueuedInputs[0].Id); Assert.Equal("Edited", chat.QueuedInputs[0].Text); Assert.Equal(input.Attachments, chat.QueuedInputs[0].Attachments); Assert.Equal("Next", chat.QueuedInputs[1].Text);
    }
}
