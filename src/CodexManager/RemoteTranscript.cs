using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

internal static class RemoteTranscript
{
    public static Message Read(JsonNode row, AgentProvider? provider)
    {
        var message = new Message
        {
            Timestamp = DateTimeOffset.TryParse(row["timestamp"]?.GetValue<string>(), out var timestamp) ? timestamp : null,
            Provider = provider,
            Id = row["id"]!.GetValue<string>(),
            Role = row["role"]!.GetValue<string>(),
            Sequence = row["sequence"]?.GetValue<int>() ?? 0
        };
        Update(message, row);
        return message;
    }

    private static void Update(Message message, JsonNode row)
    {
        message.Text = row["text"]!.GetValue<string>();
        message.Subagent = row["subagent"]?.Deserialize(StoreJsonContext.Default.SubagentInfo);
        message.AsyncTask = AsyncTaskInfo.FromJson(row["asyncTask"]);
        if (row["attachments"] is not { } files) return;
        var incoming = files.Deserialize(StoreJsonContext.Default.AttachmentArray) ?? [];
        if (message.Attachments.SequenceEqual(incoming)) return;
        message.Attachments.Clear();
        foreach (var file in incoming) message.Attachments.Add(file);
    }

    // The live page lists every ID in host order, even when unchanged bodies are omitted.
    // Reconcile in place so selection and expanded tool output survive ordinary polls.
    public static void Apply(JsonArray rows, ObservableCollection<Message> messages, Dictionary<string, string> revisions, AgentProvider? provider)
    {
        var present = rows.Select(row => row!["id"]!.GetValue<string>()).ToHashSet();
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (present.Contains(messages[i].Id)) continue;
            revisions.Remove(messages[i].Id);
            messages.RemoveAt(i);
        }
        var index = 0;
        foreach (var row in rows)
        {
            var id = row!["id"]!.GetValue<string>();
            var message = index < messages.Count && messages[index].Id == id ? messages[index] : null;
            if (message is null)
            {
                message = index < messages.Count ? messages.FirstOrDefault(m => m.Id == id) : null;
                if (message is not null) messages.Move(messages.IndexOf(message), index);
                else
                {
                    if (row["text"] is null) { revisions.Remove(id); continue; }
                    messages.Insert(index, Read(row, provider));
                }
            }
            if (message is not null && row["text"] is not null) Update(message, row);
            if (row["revision"] is { } revision) revisions[id] = revision.GetValue<string>();
            index++;
        }
        var keep = HistoryWindow.Last(messages, Chat.HistoryPageSize).Length;
        while (messages.Count > keep) { revisions.Remove(messages[0].Id); messages.RemoveAt(0); }
    }
}
