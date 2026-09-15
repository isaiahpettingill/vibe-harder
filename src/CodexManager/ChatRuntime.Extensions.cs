using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

public sealed partial class ChatRuntime
{
    private TaskCompletionSource? whisperAccepted;
    private bool SupportsDiracWhisper;
    public bool SupportsCheckpoints { get; private set; }
    private void ReadExtensionCapabilities(JsonElement init)
    {
        bool Has(string name) => init.TryGetProperty("agentCapabilities", out var caps) && caps.TryGetProperty("_meta", out var meta) &&
            meta.TryGetProperty(name, out var supported) && supported.ValueKind == JsonValueKind.True;
        SupportsDiracWhisper = Has("dev.dirac/whisper") && Has("dev.dirac/steering_status");
        SupportsCheckpoints = Has("dev.dirac/checkpoints.list") && Has("dev.dirac/checkpoints.restore");
    }
    public async Task<JsonObject> Checkpoints()
    {
        if (IsChangingHistory) throw new IOException("Wait for the history change to finish.");
        await Connect();
        if (!SupportsCheckpoints) throw new IOException("Checkpoints are not available for this session.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var response = await client!.Request("_dev.dirac/checkpoints.list", RpcJson.Object(("sessionId", chat.SessionId)), timeout.Token);
        return JsonNode.Parse(response.GetRawText())!.AsObject();
    }
    public Task<Chat> RestoreCheckpoint(string checkpointId) => IsChangingHistory
        ? Task.FromException<Chat>(new IOException("A history change is already in progress."))
        : historyTask = RestoreCheckpointCore(checkpointId);
    private async Task<Chat> RestoreCheckpointCore(string checkpointId)
    {
        if (chat.Busy || IsLoadingHistory || IsReconnecting || IsConfiguring || IsSteering || IsRecovering) throw new IOException("Stop the running chat before restoring a checkpoint.");
        var checkpoints = await Checkpoints();
        if (checkpoints["checkpoints"] is not JsonArray available || !available.Any(c => c?["id"]?.GetValue<string>() == checkpointId)) throw new IOException("This checkpoint is no longer available.");
        if (chat.Busy || IsChangingHistory) throw new IOException("Wait for the current chat operation to finish.");
        IsChangingHistory = true; chat.Busy = true; Changed?.Invoke();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await client!.Request("_dev.dirac/checkpoints.restore", RpcJson.Object(("sessionId", chat.SessionId), ("checkpointId", checkpointId)), timeout.Token);
            // Dirac does not replay history when loading an already attached session.
            // Reconnect after restoring so the adapter rehydrates the restored task.
            connected = false; await client.DisposeAsync(); client = null;
            store.Setting("historyIncomplete:" + chat.Id, "1"); store.ClearHistory(chat);
            chat.QueuedInputs.Clear(); chat.PendingInput = chat.InterruptedInput = null; chat.NeedsPermission = false;
            store.Setting("interrupted:" + chat.Id, "");
            await Connect(true);
            foreach (var message in chat.Messages) store.SaveMessage(chat, message);
            store.Setting("historyIncomplete:" + chat.Id, "0"); chat.HistoryLoaded = true; chat.Status = "Ready";
            store.Save(chat); await store.FlushAsync(); return chat;
        }
        finally
        {
            chat.Busy = false; IsChangingHistory = false;
            try { store.Save(chat); await store.FlushAsync(); }
            catch (Exception error) { AppDiagnostics.Record("Save checkpoint restoration state", error); }
            Changed?.Invoke();
        }
    }
}
