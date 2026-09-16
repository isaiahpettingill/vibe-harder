using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CodexManager;

public partial class MainView
{
    private void InitializeChatSearch()
    {
        ChatSearch.Search = async (query, token) =>
        {
            if (current is not { } chat) return [];
            foreach (var message in chat.Messages) store.SaveMessage(chat, message);
            return await store.SearchMessagesAsync(chat, query, token);
        };
        ChatSearch.Navigate = async (hit, token) =>
        {
            if (current is not { } chat) return;
            var page = await store.ReadPageAsync(chat, before: hit.Sequence + 25, token: token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(current, chat)) return;
            pageLoad?.Cancel(); viewingHistory = true;
            MessageList.ItemsSource = page; MessageList.UpdateLayout();
            if (MessageList.ItemsPanelRoot is TranscriptPanel transcript) transcript.RevealMessage(Array.FindIndex(page, m => m.Id == hit.Id));
            UpdateHistoryNavigation(); UpdateComposerAction();
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (subagentInspector is not null) return;
            if (e.Key == Key.F && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            { e.Handled = true; if (remoteView is { } remote) remote.OpenChatSearch(); else if (current is not null) ChatSearch.Open(); }
            else if (e.Key == Key.Escape && ChatSearch.IsVisible) { e.Handled = true; ChatSearch.Close(); }
        }, RoutingStrategies.Tunnel);
    }
    private void SearchChatClick(object? sender, RoutedEventArgs e) { if (current is not null) ChatSearch.Open(); }
}
