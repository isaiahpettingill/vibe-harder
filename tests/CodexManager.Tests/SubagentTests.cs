using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace CodexManager.Tests;

public class SubagentTests
{
    [AvaloniaFact]
    public async Task NativeChildSessionsStaySeparateAndPersistNestedTools()
    {
        using var store = new Store(Directory.CreateTempSubdirectory("subagents-").FullName);
        var workspace = new Workspace("w", "Test", store.DirectoryPath); store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id }; store.Save(chat);
        await using var runtime = new ChatRuntime(chat, workspace, store, "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        await runtime.Send("subagents", []);
        Assert.Equal("Parent answer", Assert.Single(chat.Messages, m => m.Role == "assistant").Text);
        var root = Assert.Single(chat.Messages, m => m.Subagent is not null).Subagent!;
        Assert.Equal("completed", root.Status);
        Assert.Equal("Child answer", root.Activity[0].Text);
        Assert.Contains("File contents", root.Activity[1].Text);
        Assert.Contains("completed", root.Activity[1].Text);
        Assert.Equal("Nested reasoning", Assert.Single(root.Activity[2].Child!.Activity).Text);
        Assert.Equal("completed", root.Activity[2].Child!.Status);
        var saved = Assert.Single(await store.ReadPageAsync(chat), m => m.Subagent is not null).Subagent!;
        Assert.Equal("Nested reasoning", Assert.Single(saved.Activity[2].Child!.Activity).Text);
        Assert.False(chat.Busy);
    }

    [Fact]
    public void LegacyTaskAndPartialUpdatesPreservePromptAndResult()
    {
        using var start = JsonDocument.Parse("""{"title":"Delegate","status":"in_progress","rawInput":{"description":"Build assembler","subagent_type":"general","prompt":"Implement it"},"content":[{"content":{"text":"Result"}}]}""");
        var info = SubagentInfo.FromTool(start.RootElement)!;
        using var finish = JsonDocument.Parse("""{"status":"completed"}""");
        info = SubagentInfo.FromTool(finish.RootElement, info)!;
        Assert.Equal("Implement it", info.Prompt); Assert.Equal("Result", info.Output); Assert.Equal("completed", info.Status);
        var legacy = SubagentInfo.FromLegacy("Delegate\n\n*completed*\n\n```\n{\"subagent_type\":\"general\",\"prompt\":\"Implement it\"}\n```\n\nResult")!;
        Assert.Equal("Result", legacy.Output); Assert.Equal("Implement it", legacy.Prompt);
    }

    [AvaloniaFact]
    public void FullscreenIsReadOnlyLiveAndEscapeClosesIt()
    {
        var root = new Message { Role = "tool", Subagent = new("Task", "Subagent", "", "running", "child", "", [new("a", "assistant", "First answer")]) };
        var closed = false;
        var inspector = new SubagentInspector(root, [], () => closed = true);
        var window = new Window { Content = inspector, Width = 800, Height = 600 }; window.Show(); window.UpdateLayout();
        try
        {
            Assert.Empty(inspector.GetVisualDescendants().OfType<TextBox>());
            root.Subagent = root.Subagent with { Activity = [new("a", "assistant", "Updated answer"), new("t", "tool", "Read file\n\nContents")] };
            window.UpdateLayout();
            Assert.Contains(inspector.GetVisualDescendants().OfType<MessageView>(), v => v.Message?.Text == "Updated answer");
            Assert.Contains(inspector.GetVisualDescendants().OfType<MessageView>(), v => v.Message?.Text == "Read file\n\nContents");
            inspector.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }); Assert.True(closed);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FullscreenCoversOnlyTheChatPaneAndClosesWithTheChat()
    {
        var directory = Directory.CreateTempSubdirectory("subagent-pane-").FullName; Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        store.Save(new Workspace("w", "Test", directory));
        store.Save(new Chat { WorkspaceId = "w", Title = "First" }); store.Save(new Chat { WorkspaceId = "w", Title = "Second" });
        var window = new MainWindow(store) { Width = 1200, Height = 800 }; window.Show();
        try
        {
            var chats = UiTests.Named<ListBox>(window, "Chats_w");
            chats.SelectedItem = chats.Items.OfType<Chat>().Single(c => c.Title == "First");
            var root = new Message { Role = "tool", Subagent = new("Task", "Subagent", "", "running", "child", "", []) };
            window.View.ShowSubagent(root, []); window.UpdateLayout();
            var inspector = window.GetVisualDescendants().OfType<SubagentInspector>().Single();
            var sidebar = chats.TranslatePoint(new Point(chats.Bounds.Width, 0), window)!.Value.X;
            Assert.True(inspector.TranslatePoint(default, window)!.Value.X >= sidebar);
            // Choosing another chat in the sidebar, which stays usable, closes it.
            chats.SelectedItem = chats.Items.OfType<Chat>().Single(c => c.Title == "Second"); window.UpdateLayout();
            Assert.Empty(window.GetVisualDescendants().OfType<SubagentInspector>());
        }
        finally { window.RequestExit(); var until = DateTime.UtcNow.AddSeconds(10); while (window.IsVisible && DateTime.UtcNow < until) await Task.Delay(25); }
    }

}
