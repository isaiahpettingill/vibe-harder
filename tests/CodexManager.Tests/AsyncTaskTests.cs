using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class AsyncTaskTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs");
    private static async Task Wait(Func<bool> ready, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(ready());
    }
    private static (Store Store, Chat Chat, ChatRuntime Runtime) Start()
    {
        var directory = Directory.CreateTempSubdirectory("async-tasks-").FullName;
        var store = new Store(directory);
        var workspace = new Workspace("w", "Fixture", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", Provider = AgentProvider.Claude }; store.Save(chat);
        var command = OperatingSystem.IsWindows() ? $"node '{Fixture.Replace("'", "''")}'" : $"node {Hosts.Quote(Fixture)}";
        return (store, chat, new ChatRuntime(chat, workspace, store, command));
    }

    [AvaloniaFact]
    public async Task BackgroundCommandShowsOnItsCardKeepsTheAgentAndWakesUpAfterwards()
    {
        var (store, chat, runtime) = Start();
        ChatRuntime.BackgroundQuietTime = TimeSpan.FromMilliseconds(500);
        try
        {
            var woke = 0; runtime.BackgroundCompleted += () => woke++;
            await runtime.Send("background", []).WaitAsync(TimeSpan.FromSeconds(15));
            var card = chat.Messages.Single(m => m.ToolId == "bash-1");
            Assert.StartsWith("Start the dev server\n\n*running in background*\n\n```\nnpm run dev\n```", card.Text);
            Assert.Equal(new AsyncTaskInfo("task-1", "running", null, true), card.AsyncTask);
            Assert.DoesNotContain(chat.Messages, m => m.ToolId == "task:task-1");
            // The turn is over, but the agent process holds the task: idle shutdown must wait.
            await runtime.ReleaseIfIdle(DateTimeOffset.UtcNow); await runtime.ReleaseIfIdle(DateTimeOffset.UtcNow.AddHours(1));
            Assert.True(runtime.IsConnected);

            await Wait(() => card.AsyncTask?.State == "completed");
            Assert.StartsWith("Start the dev server\n\n*completed*", card.Text); Assert.Contains("Server ready on :3000", card.Text);
            Assert.False(card.AsyncTask!.Running);
            // The agent then reports on its own, outside any prompt.
            await Wait(() => chat.Messages.Any(m => m.Text.Contains("The dev server is up on port 3000.")));
            Assert.False(chat.Busy);
            await Wait(() => woke == 1);
            Assert.True(chat.HasUnreadCompletion); Assert.Equal("Ready", chat.Status);
        }
        finally { ChatRuntime.BackgroundQuietTime = TimeSpan.FromSeconds(4); await runtime.DisposeAsync(); store.Dispose(); }
    }

    [AvaloniaFact]
    public async Task TasksStopFromTheCardAndRemoteClientsSeeThem()
    {
        var (store, chat, runtime) = Start();
        try
        {
            await runtime.Send("background-long", []).WaitAsync(TimeSpan.FromSeconds(15));
            var card = chat.Messages.Single(m => m.ToolId == "bash-1");
            Assert.True(card.AsyncTask!.Running);
            // Remote clients get the task with the message and stop it through the session service.
            using var service = new SessionService(store, [new Workspace("w", "Fixture", store.DirectoryPath)], [chat], (_, _) => runtime);
            var page = await service.Handle(new JsonObject { ["method"] = "chat", ["chatId"] = chat.Id, ["activate"] = false });
            var row = page!["messages"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == card.Id)!;
            Assert.Equal("task-1", AsyncTaskInfo.FromJson(row["asyncTask"])!.Id);

            var view = new MessageView { Message = card };
            var window = new Window { Content = view, Width = 700, Height = 400 }; window.Show();
            try
            {
                var stop = view.GetLogicalDescendants().OfType<IconButton>().Single(b => b.Name == "StopAsyncTask");
                Assert.True(stop.IsVisible);
                Assert.True(view.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "AsyncTaskBadge").IsVisible);
                await service.Handle(new JsonObject { ["method"] = "task/stop", ["chatId"] = chat.Id, ["taskId"] = "task-1" });
                await Wait(() => card.AsyncTask?.State == "stopped");
                Assert.False(stop.IsVisible);
                Assert.StartsWith("Start the dev server\n\n*stopped*", card.Text);
                await Assert.ThrowsAsync<IOException>(() => runtime.StopAsyncTask("task-1"));
            }
            finally { window.Close(); }
        }
        finally { await runtime.DisposeAsync(); store.Dispose(); }
    }

    [AvaloniaFact]
    public async Task TaskWithoutAToolCallGetsItsOwnCard()
    {
        var (store, chat, runtime) = Start();
        try
        {
            await runtime.Send("workflow", []).WaitAsync(TimeSpan.FromSeconds(15));
            var card = chat.Messages.Single(m => m.ToolId == "task:task-1");
            Assert.Equal("tool", card.Role);
            Assert.StartsWith("Nightly workflow\n\n*running in background*", card.Text); Assert.Contains("Runs the nightly checks", card.Text);
            await Wait(() => card.AsyncTask?.State == "completed");
            Assert.Contains("Server ready on :3000", card.Text);
        }
        finally { await runtime.DisposeAsync(); store.Dispose(); }
    }
}
