using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;

namespace CodexManager.Tests;

public class RobustnessTests
{
    [AvaloniaFact]
    public async Task IncrementalChatOmitsUnchangedTextAndAttachmentsAndIncludesEdits()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("delta-chat-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { Id = "c", WorkspaceId = "w", HistoryLoaded = true }; store.Save(chat);
        var message = new Message { Text = "first" }; message.Attachments.Add(new("image", "image/png", new string('a', 100000)));
        chat.Messages.Add(message); store.SaveMessage(chat, message);
        await using var runtime = new ChatRuntime(chat, workspace, store, "unused");
        using var service = new SessionService(store, [workspace], [chat], (_, _) => runtime);
        var first = (await service.Handle(new() { ["method"] = "chat", ["chatId"] = chat.Id }))!;
        var revision = first["messages"]![0]!["revision"]!.GetValue<string>();
        var request = new JsonObject { ["method"] = "chat", ["chatId"] = chat.Id, ["knownMessages"] = new JsonObject { [message.Id] = revision } };
        var unchanged = (await service.Handle(request))!;
        Assert.Null(unchanged["messages"]![0]!["text"]); Assert.Null(unchanged["messages"]![0]!["attachments"]);
        Assert.True(unchanged.ToJsonString().Length < first.ToJsonString().Length / 10);
        message.Text = "edited";
        var edited = (await service.Handle(request))!;
        Assert.Equal("edited", edited["messages"]![0]!["text"]!.GetValue<string>());
        Assert.NotEqual(revision, edited["messages"]![0]!["revision"]!.GetValue<string>());
    }
    [AvaloniaFact]
    public async Task CollapsedConnectionDoesNotPollAtStartupOrAfterWake()
    {
        var directory = Directory.CreateTempSubdirectory("collapsed-remote-").FullName;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var requests = 0;
        await using var server = new RemoteServer(directory, "127.0.0.1", port, request =>
        {
            requests++;
            return Task.FromResult<JsonNode?>(new JsonObject { ["workspaces"] = new JsonArray(), ["chats"] = new JsonArray() });
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (server.Fingerprint is null) await Task.Delay(20, timeout.Token);
        var host = await RemoteConnection.Pair(RemoteTrust.Invite(directory, "localhost", port, "Host"), Path.Combine(directory, "key"), "Test", timeout.Token);
        using var view = new RemoteView(host); view.SetConnectionCollapsed(true);
        var window = new Window { Content = view }; window.Show();
        try
        {
            await Task.Delay(500, timeout.Token); Assert.Equal(0, requests);
            view.SetConnectionCollapsed(false);
            while (requests == 0) await Task.Delay(20, timeout.Token);
            view.SetConnectionCollapsed(true); await Task.Delay(100, timeout.Token); var paused = requests;
            view.SetConnectionSuspended(true); view.SetConnectionSuspended(false); view.SetPresentationSleeping(false);
            await Task.Delay(2500, timeout.Token); Assert.Equal(paused, requests);
            view.SetConnectionCollapsed(false);
            while (requests == paused) await Task.Delay(20, timeout.Token);
        }
        finally { window.Close(); }
    }
    [Fact]
    public async Task StorageFailureFailsFlushAndRejectsLaterWrites()
    {
        var store = new Store(Directory.CreateTempSubdirectory("failed-writer-").FullName, backgroundWrites: true);
        store.Save(new Chat { WorkspaceId = "missing-workspace" });
        await Assert.ThrowsAnyAsync<Exception>(() => store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Throws<IOException>(() => store.Setting("afterFailure", "value"));
        Assert.Throws<IOException>(() => store.Dispose());
    }
    [Fact]
    public async Task RepeatedMessageSnapshotsCoalesceWhileStorageIsBlocked()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("coalesced-writer-").FullName, backgroundWrites: true);
        store.Save(new Workspace("w", "Test", store.DirectoryPath));
        var chat = new Chat { Id = "c", WorkspaceId = "w" }; store.Save(chat); await store.FlushAsync();
        using var blocker = new SqliteConnection($"Data Source={Path.Combine(store.DirectoryPath, "sessions.db")}"); blocker.Open();
        using (var transaction = blocker.BeginTransaction())
        {
            var message = new Message();
            for (var i = 0; i < 10000; i++) { message.Text = "snapshot " + i; store.SaveMessage(chat, message); }
            transaction.Commit();
        }
        await store.FlushAsync();
        Assert.Equal("snapshot 9999", (await store.ReadPageAsync(chat, token: TestContext.Current.CancellationToken)).Single().Text);
    }
}
