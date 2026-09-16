using System.Collections.Concurrent;
using System.Text.Json;

namespace CodexManager;

public sealed partial class ChatRuntime
{
    private readonly ConcurrentDictionary<string, Message> subagentRoots = new();
    private async Task RouteUpdate(string? session, JsonElement update)
    {
        if (lifetime.IsCancellationRequested || loading && !replaying) return;
        var kind = SubagentInfo.Text(update, "sessionUpdate");
        if (kind is "subagent_update" or "subagent_spawned" or "subagent_state_update")
        {
            var childId = SubagentInfo.Text(update, "subagentSessionId");
            if (string.IsNullOrEmpty(childId) || childId == session || childId == chat.SessionId) return;
            if (!subagentRoots.TryGetValue(childId, out var root))
            {
                if (subagentRoots.Count >= 256) return;
                var child = new SubagentInfo(SubagentInfo.Text(update, "name") ?? "Subagent", "Subagent", SubagentInfo.Text(update, "task") ?? SubagentInfo.Text(update, "description") ?? "", "running", childId, "", []);
                if (session is not null && subagentRoots.TryGetValue(session, out var parent))
                {
                    if (parent.Subagent!.Depth(session) >= 8) return;
                    root = parent;
                    root.Subagent = root.Subagent!.Apply(session, value => value with { Activity = value.Activity.Append(new SubagentEntry(childId, "tool", child.Title, child)).TakeLast(128).ToArray() });
                }
                else
                {
                    if (session is not null && chat.SessionId is not null && session != chat.SessionId) return;
                    root = chat.Messages.FirstOrDefault(m => m.Subagent?.SessionId == childId);
                    if (root is null) { Add("tool", child.Title, "subagent:" + childId); root = chat.Messages.Last(); root.Subagent = child; }
                    else if (replaying) root.Subagent = child;
                }
                subagentRoots[childId] = root;
            }
            root.Subagent = root.Subagent!.Apply(childId, child => child with
            {
                Title = SubagentInfo.Text(update, "name") ?? child.Title,
                Prompt = SubagentInfo.Text(update, "task") ?? SubagentInfo.Text(update, "description") ?? child.Prompt,
                Status = SubagentInfo.Text(update, "state") ?? child.Status
            });
            root.Text = root.Subagent.Title + "\n\n" + root.Subagent.Status;
            if (!replaying) store.SaveMessage(chat, root);
            Changed?.Invoke(); return;
        }
        if (session is not null && subagentRoots.TryGetValue(session, out var owner))
        {
            owner.Subagent = owner.Subagent!.Apply(session, child => child.Append(update));
            if (!replaying) store.SaveMessage(chat, owner);
            Changed?.Invoke(); return;
        }
        if (session is not null && chat.SessionId is not null && session != chat.SessionId) return;
        await Update(update);
    }
    private void DisconnectSubagents()
    {
        foreach (var root in subagentRoots.Values.Distinct())
        {
            root.Subagent = root.Subagent?.Disconnected();
            store.SaveMessage(chat, root);
        }
        subagentRoots.Clear();
    }
}
