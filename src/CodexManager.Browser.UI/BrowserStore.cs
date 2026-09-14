namespace CodexManager;

// Only client preferences and drafts live in the browser. Host history stays on the host.
public sealed class Store : IDisposable
{
    public static string DataDirectory => "/browser";
    public string DirectoryPath => DataDirectory;
    public Store(string? directory = null, bool backgroundWrites = false) { }
    public string? Setting(string key) => BrowserPlatform.Read("setting:" + key);
    public void Setting(string key, string value) => BrowserPlatform.Write("setting:" + key, value);
    public Task FlushAsync() => Task.CompletedTask;
    public List<Workspace> Workspaces() => [];
    public List<Chat> Chats() => [];
    private static Exception RemoteOnly() => new NotSupportedException("Open a workspace on your connected computer.");
    public void Save(Workspace workspace) => throw RemoteOnly();
    public void Save(Chat chat) => throw RemoteOnly();
    public void SaveMessage(Chat chat, Message message) => throw RemoteOnly();
    public void LoadMessages(Chat chat) => throw RemoteOnly();
    public Task<Message[]> ReadPageAsync(Chat chat, int? before = null, int limit = Chat.HistoryPageSize, CancellationToken token = default, string? toolId = null, bool newer = false) => throw RemoteOnly();
    public Task<(string Plain, string Html)> ExportChatAsync(Chat chat) => throw RemoteOnly();
    public Task<HashSet<string>> SearchChatIdsAsync(string query, CancellationToken token) => throw RemoteOnly();
    public void ClearHistory(Chat chat) => throw RemoteOnly();
    public void ApplyRecentPage(Chat chat, Message[] messages) => throw RemoteOnly();
    public void ReleaseHistory(Chat chat) => throw RemoteOnly();
    public void TrimHistory(Chat chat) => throw RemoteOnly();
    public void Delete(Chat chat) => throw RemoteOnly();
    public void Dispose() { }
}
