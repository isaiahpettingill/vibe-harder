namespace CodexManager;

// What the chat pane needs from a chat's backend: a local or WSL ChatRuntime, or a chat on a
// paired computer (RemoteChatSession). The desktop pane only talks to this interface, so
// features and fixes reach local and remote chats alike.
public interface IChatSession : IAsyncDisposable
{
    event Action? Changed;
    bool IsLoadingHistory { get; }
    bool IsReconnecting { get; }
    bool IsPrompting { get; }
    bool IsConfiguring { get; }
    bool IsPreparing { get; }
    bool IsConnected { get; }
    bool SupportsSteering { get; }
    bool IsSteering { get; }
    bool IsRecovering { get; }
    Task Send(string text, Attachment[] attachments, bool autoResume = false);
    void Queue(PendingInput input);
    void RemoveQueued(PendingInput input);
    Task<bool> Steer(PendingInput input);
    Task SendQueuedNow(PendingInput input, bool waitForCompletion = true);
    Task AdvanceQueued(bool interrupt = false);
    Task Stop();
    Task SetConfig(SessionConfig config, string value);
    Task Reconnect(bool automatic = false);
    Task LoadHistory();
}
