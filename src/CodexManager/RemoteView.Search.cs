using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Text.Json.Nodes;

namespace CodexManager;

public sealed partial class RemoteView
{
    private readonly ChatSearchBar chatSearch = new();
    private TranscriptNavigation? transcriptNavigation;
    public void OpenChatSearch() { if (chatId is not null) chatSearch.Open(); }
    private void InitializeChatSearch()
    {
        chatSearch.Search = async (query, token) =>
        {
            if (chatId is not { } id) return [];
            var result = await Call(new() { ["method"] = "chat/search", ["chatId"] = id, ["query"] = query });
            token.ThrowIfCancellationRequested();
            if (result is not JsonArray rows) throw new IOException("Chat search requires an updated host.");
            return rows.OfType<JsonNode>().Select(r => new ChatSearchHit(r["id"]!.GetValue<string>(), r["sequence"]!.GetValue<int>(), r["preview"]!.GetValue<string>())).ToArray();
        };
        chatSearch.Navigate = async (hit, token) =>
        {
            if (chatId is not { } id) return;
            var result = await Call(new() { ["method"] = "chat", ["chatId"] = id, ["before"] = hit.Sequence + 25 });
            token.ThrowIfCancellationRequested();
            if (id != chatId || result is null) return;
            var page = result["messages"]!.AsArray().OfType<JsonNode>().Select(ReadMessage).ToArray();
            viewingHistory = true; output.ItemsSource = page; output.UpdateLayout();
            if (output.ItemsPanelRoot is TranscriptPanel transcript) transcript.RevealMessage(Array.FindIndex(page, m => m.Id == hit.Id));
            UpdateSendAction(); transcriptNavigation?.Update();
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled) return;
            if (e.Key == Key.F && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { e.Handled = true; OpenChatSearch(); }
            else if (e.Key == Key.Escape && chatSearch.IsVisible) { e.Handled = true; chatSearch.Close(); }
        }, RoutingStrategies.Tunnel);
    }
}
