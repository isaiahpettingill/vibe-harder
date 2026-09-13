using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class SlashCommandTests
{
    [AvaloniaFact]
    public async Task ComposerCompletesWithoutSending()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-commands", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        using (var store = new Store(directory))
        {
            store.Setting("runInTray", "0"); store.Setting("remoteEnabled", "0");
            store.Save(new Workspace("w", "Commands", directory));
            store.Save(new Chat { WorkspaceId = "w", SessionId = "fixture-session" });
            foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        }
        var window = new MainWindow(); window.Show();
        try
        {
            await Task.Delay(500);
            var chat = (Chat)UiTests.Named<ListBox>(window, "Chats_w").SelectedItem!;
            chat.Commands = [new("goal", "Set objective", "objective")];
            var composer = window.FindControl<TextBox>("Composer")!; composer.Text = "/go";
            await Task.Delay(50);
            Assert.True(window.FindControl<ListBox>("SlashCommands")!.IsVisible);
            Assert.True(window.View.GetLogicalDescendants().OfType<SlashCommandOverlay>().Single().IsOpen);
            composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Equal("/go", composer.Text); Assert.False(window.FindControl<ListBox>("SlashCommands")!.IsVisible);
            // Polling/configuration refreshes must not reopen a dismissed query.
            window.FindControl<ListBox>("SlashCommands")!.IsVisible = true;
            Assert.False(window.View.GetLogicalDescendants().OfType<SlashCommandOverlay>().Single().IsOpen);
            composer.Text = ""; await Task.Delay(50);
            composer.Text = "/go"; await Task.Delay(50);
            Assert.True(window.View.GetLogicalDescendants().OfType<SlashCommandOverlay>().Single().IsOpen);
            window.FindControl<ListBox>("SlashCommands")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.False(window.View.GetLogicalDescendants().OfType<SlashCommandOverlay>().Single().IsOpen);
            composer.Text = "/g";
            await Task.Delay(50);
            composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal("/goal ", composer.Text);
            Assert.DoesNotContain(chat.Messages, m => m.Role == "user");
            Assert.False(window.FindControl<ListBox>("SlashCommands")!.IsVisible);
            composer.CaretIndex = composer.Text.Length;
            composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control });
            Assert.Equal("/goal \n", composer.Text);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task AdvertisedCommandsAreSessionScopedAndSentUnchanged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-commands", Guid.NewGuid().ToString("N"));
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Commands", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = workspace.Id }; store.Save(chat);
        var command = "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" --commands";
        await using var runtime = new ChatRuntime(chat, workspace, store, command);
        await runtime.Connect();
        Assert.Equal(2, chat.Commands.Count);
        Assert.Equal("objective", SlashCommand.Match(chat.Commands, "/go").Single().Hint);
        Assert.Empty(SlashCommand.Match(chat.Commands, "/goal argument"));
        Assert.Empty(SlashCommand.Match(chat.Commands, "/unsupported"));
        await runtime.Send("/goal finish the migration", []);
        Assert.Equal("/goal finish the migration", chat.Messages.Last().Text);
        var other = new Chat { WorkspaceId = workspace.Id };
        Assert.Empty(other.Commands);
    }
}
