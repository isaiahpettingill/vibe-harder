using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CodexManager;

public partial class MainView
{
    public Task ShowHistoryActions(Control anchor, Message? message)
    {
        if (current is not { } chat) return Task.CompletedTask;
        if (RemoteSessionOf(chat) is { } remote)
            return HistoryActions.Show(anchor, message, remote.Call, id =>
            {
                if (!ReferenceEquals(current, chat)) return;
                if (id == chat.RemoteId) { remote.ResetMessages(); return; }
                OpenRemoteChat(chat.Remote!, id);
            });
        return HistoryActions.Show(anchor, message, request => { request["chatId"] = chat.Id; return remoteSessions.Handle(request); }, id =>
        {
            RefreshChats();
            if (current != chat || workspace?.Id != chat.WorkspaceId) return;
            if (id != chat.Id) ChatList.SelectedItem = chats.FirstOrDefault(c => c.Id == id);
            else { MessageList.ItemsSource = chat.Messages; UpdateControls(); }
        });
    }
    private async void ForkChatClick(object? sender, RoutedEventArgs e) { if (sender is Control anchor) await ShowHistoryActions(anchor, null); }
}
