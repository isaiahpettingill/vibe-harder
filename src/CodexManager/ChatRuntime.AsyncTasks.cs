using System.Text.Json;
using Avalonia.Threading;

namespace CodexManager;

// Background work of AIR agents: async tasks (backgrounded commands, workflows) and the
// autonomous cycles in which the agent wakes up on its own, for example when such a task ends.
public sealed partial class ChatRuntime
{
    private sealed class AsyncTask
    {
        public required string Id;
        public string Name = "Background task";
        public string? Description, Summary, ToolCallId, LastToolName;
        public string State = "running";
        public bool CanStop;
    }
    private readonly Dictionary<string, AsyncTask> asyncTasks = [];
    private DispatcherTimer? backgroundQuiet;
    private DateTimeOffset promptEndedAt;
    public bool HasBackgroundWork => asyncTasks.Values.Any(t => t.State is "running" or "paused") || backgroundQuiet?.IsEnabled == true;
    // Raised when an autonomous cycle (work the agent started on its own) goes quiet.
    public event Action? BackgroundCompleted;
    // Raised when a prompt's turn completes normally.
    public event Action? TurnCompleted;
    public static TimeSpan BackgroundQuietTime { get; set; } = TimeSpan.FromSeconds(4);

    private void UpdateAsyncTask(string kind, JsonElement update)
    {
        if (!update.TryGetProperty("asyncTaskId", out var idValue) || idValue.GetString() is not { } id) return;
        if (!asyncTasks.TryGetValue(id, out var task)) asyncTasks[id] = task = new AsyncTask { Id = id };
        string? Text(string name) => update.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        task.Name = Text("name") ?? task.Name;
        task.Description = Text("description") ?? task.Description;
        task.Summary = Text("summary") ?? task.Summary;
        task.ToolCallId = Text("toolCallId") ?? task.ToolCallId;
        task.LastToolName = Text("lastToolName") ?? task.LastToolName;
        if (kind == "async_task_state_update") task.State = Text("state") ?? task.State;
        if (update.TryGetProperty("canStop", out var canStop)) task.CanStop = canStop.ValueKind == JsonValueKind.True;
        ShowAsyncTask(task);
    }
    // A task shows on the card of the tool call that started it, or on its own card.
    private void ShowAsyncTask(AsyncTask task)
    {
        var message = task.ToolCallId is { } toolId ? chat.Messages.LastOrDefault(m => m.ToolId == toolId) : null;
        var call = message is null ? null : toolCalls.TryGetValue(message.ToolId!, out var known) ? known : AcpToolCall.FromText(message.Text);
        if (message is null)
        {
            message = chat.Messages.LastOrDefault(m => m.ToolId == "task:" + task.Id);
            if (message is null) { Add("tool", "", "task:" + task.Id); message = chat.Messages[^1]; }
            call = message.Text.Length == 0 ? new AcpToolCall() : AcpToolCall.FromText(message.Text);
            call.Describe(task.Name, task.Description);
        }
        call!.SetTask(task.State, task.Summary ?? (task.LastToolName is { } tool ? "Using " + tool : null));
        message.Text = call.Render();
        message.AsyncTask = new AsyncTaskInfo(task.Id, task.State, task.Summary, task.CanStop && task.State is "running" or "paused");
        if (!replaying) store.SaveMessage(chat, message);
        if (task.State is "completed" or "failed" or "stopped") asyncTasks.Remove(task.Id);
    }
    public async Task StopAsyncTask(string taskId)
    {
        if (client is null || chat.SessionId is null) throw new IOException("The agent is not connected.");
        var result = await client.Request("_session/async_task/stop", RpcJson.Object(("sessionId", chat.SessionId), ("asyncTaskId", taskId)), lifetime.Token);
        if (!result.TryGetProperty("stopped", out var stopped) || stopped.ValueKind != JsonValueKind.True) throw new IOException("The task already ended.");
    }
    // The agent's process ends with its tasks; nothing will report them again.
    private void EndAsyncTasks()
    {
        foreach (var task in asyncTasks.Values.ToArray()) { task.State = "stopped"; task.Summary ??= "Ended when the agent stopped."; ShowAsyncTask(task); }
        asyncTasks.Clear();
    }

    // Output with no prompt open is an autonomous cycle. ACP marks no end, so a short quiet
    // period completes it, like the end of a turn.
    private void NoteBackgroundActivity(string kind)
    {
        if (IsPrompting || chat.Busy || loading || replaying || kind.StartsWith("async_task", StringComparison.Ordinal)) return;
        if (kind is not ("agent_message_chunk" or "agent_thought_chunk" or "tool_call" or "tool_call_update" or "plan")) return;
        // Updates can trail the prompt response by a moment; they belong to that turn.
        if (DateTimeOffset.UtcNow - promptEndedAt < TimeSpan.FromSeconds(2)) return;
        if (backgroundQuiet is null)
        {
            backgroundQuiet = new DispatcherTimer { Interval = BackgroundQuietTime };
            backgroundQuiet.Tick += (_, _) => FinishBackgroundCycle();
        }
        if (!backgroundQuiet.IsEnabled) { chat.Status = "Working in background…"; idleSince = null; }
        backgroundQuiet.Stop(); backgroundQuiet.Interval = BackgroundQuietTime; backgroundQuiet.Start();
    }
    private void FinishBackgroundCycle()
    {
        backgroundQuiet?.Stop();
        if (lifetime.IsCancellationRequested) return;
        if (chat.Status == "Working in background…") chat.Status = "Ready";
        if (IsActiveView?.Invoke() != true) chat.HasUnreadCompletion = true;
        foreach (var message in chat.Messages.TakeLast(20)) store.SaveMessage(chat, message);
        store.Save(chat);
        Changed?.Invoke();
        BackgroundCompleted?.Invoke();
    }
}
