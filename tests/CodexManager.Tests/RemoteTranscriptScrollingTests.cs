using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class RemoteTranscriptScrollingTests
{
    [AvaloniaFact]
    public async Task RemotePollingAndMarkdownResizeKeepPositionWithoutLoadingHistory()
    {
        var directory = Directory.CreateTempSubdirectory("remote-scroll-").FullName;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var shortContent = false; var polls = 0; var historyLoads = 0;
        var longText = string.Join("\n\n", Enumerable.Repeat("A paragraph of **streaming output** with `code` and more text.", 30));
        await using var server = new RemoteServer(directory, "127.0.0.1", port, request =>
        {
            if (request["method"]!.GetValue<string>() == "list")
                return Task.FromResult<JsonNode?>(new JsonObject { ["workspaces"] = new JsonArray(new JsonObject { ["id"] = "w", ["name"] = "Test" }), ["chats"] = new JsonArray(new JsonObject { ["id"] = "c", ["workspaceId"] = "w", ["title"] = "Chat", ["archived"] = false }) });
            if (request["method"]!.GetValue<string>() != "chat") return Task.FromResult<JsonNode?>(new JsonObject());
            polls++;
            if (request["before"] is not null || request["after"] is not null) historyLoads++;
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["provider"] = "Claude", ["busy"] = true, ["preparing"] = false, ["status"] = "Working", ["queued"] = 0,
                ["config"] = new JsonArray(), ["permissions"] = new JsonArray(), ["queue"] = new JsonArray(),
                ["messages"] = new JsonArray(
                    new JsonObject { ["id"] = "one", ["role"] = "assistant", ["sequence"] = 100, ["text"] = shortContent ? "Short answer" : longText },
                    new JsonObject { ["id"] = "two", ["role"] = "assistant", ["sequence"] = 101, ["text"] = shortContent ? "Done" : longText })
            });
        });
        async Task Wait(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(condition());
        }
        await Wait(() => server.Fingerprint is not null);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(directory, "localhost", port, "Host"), Path.Combine(directory, "key"), "Test", TestContext.Current.CancellationToken);
        using var view = new RemoteView(host);
        var window = new Window { Content = view, Width = 420, Height = 800 }; window.Show();
        try
        {
            await Wait(() => polls >= 2); await Task.Delay(100); window.UpdateLayout();
            var panel = view.GetVisualDescendants().OfType<TranscriptPanel>().Single();
            Assert.True(panel.IsFollowingEnd);
            shortContent = true; var count = polls; await Wait(() => polls >= count + 3); window.UpdateLayout();
            Assert.Equal(0, historyLoads); Assert.True(panel.IsFollowingEnd);
            Assert.InRange(Math.Abs(panel.Extent.Height - panel.Viewport.Height - panel.Offset.Y), 0, 1);
            shortContent = false; count = polls; await Wait(() => polls >= count + 2); window.UpdateLayout();
            panel.Offset = new Vector(0, 300); window.UpdateLayout();
            var anchor = panel.CaptureAnchor();
            var list = panel.GetVisualAncestors().OfType<ListBox>().First();
            list.SelectedIndex = 1; window.UpdateLayout();
            count = polls; await Wait(() => polls >= count + 3); window.UpdateLayout();
            Assert.Equal(anchor, panel.CaptureAnchor()); Assert.False(panel.IsFollowingEnd);
            Assert.Equal(0, historyLoads);
        }
        finally { window.Close(); }
    }
}
