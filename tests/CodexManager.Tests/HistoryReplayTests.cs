using Avalonia.Headless.XUnit;
using System.Text.Json;
using Avalonia.Controls;

namespace CodexManager.Tests;

public class HistoryReplayTests
{
    [AvaloniaFact]
    public async Task ReadOnlyOpenCodeReplay()
    {
        var file = Environment.GetEnvironmentVariable("CODEX_MANAGER_REPLAY_INPUT");
        if (string.IsNullOrEmpty(file)) return;
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var input = config.RootElement;
        var workspace = new Workspace("replay", "Read-only replay", input.GetProperty("cwd").GetString()!, input.GetProperty("distro").GetString());
        var data = Path.Combine(Path.GetTempPath(), "codex-replay", Guid.NewGuid().ToString("N"));
        using var store = new Store(data);
        store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id, Provider = AgentProvider.OpenCode, SessionId = input.GetProperty("session").GetString() };
        store.Save(chat);
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, workspace.IsWsl), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        store.Setting(AgentProviders.CommandKey(AgentProvider.OpenCode, workspace.IsWsl), input.GetProperty("command").GetString()!);
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", data);
        var window = new MainWindow(); window.Show();
        try
        {
            var selected = Assert.IsType<Chat>(UiTests.Named<ListBox>(window, "Chats_replay").SelectedItem);
            var until = DateTime.UtcNow.AddSeconds(45);
            while (selected.Busy && DateTime.UtcNow < until)
            { Assert.False(window.FindControl<Button>("StopButton")!.IsVisible); await Task.Delay(50); }
            Assert.Equal("Ready", selected.Status);
            Assert.NotEmpty(selected.Messages);
            Assert.False(selected.Busy);
        }
        finally { window.Close(); await Task.Delay(200); }
    }
}
