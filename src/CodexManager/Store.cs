using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodexManager;

public sealed class Store : IDisposable
{
    private readonly SqliteConnection db;
    private readonly RecoveryJournal recovery;
    private readonly Dictionary<string, string> savedChats = [];
    private readonly Dictionary<string, string> savedMessages = [];
    private readonly Dictionary<string, Attachment[]> savedAttachments = [];
    public static string DataDirectory => Environment.GetEnvironmentVariable("CODEX_MANAGER_DATA") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexManager");
    public Store(string? directory = null)
    {
        directory ??= DataDirectory;
        Directory.CreateDirectory(directory);
        recovery = new RecoveryJournal(directory);
        db = new(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(directory, "sessions.db") }.ToString());
        db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,path TEXT NOT NULL,distro TEXT);
            CREATE TABLE IF NOT EXISTS chats(id TEXT PRIMARY KEY,workspace_id TEXT NOT NULL REFERENCES workspaces(id),session_id TEXT,title TEXT NOT NULL,updated TEXT NOT NULL,draft TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS messages(id TEXT PRIMARY KEY,chat_id TEXT NOT NULL REFERENCES chats(id),role TEXT NOT NULL,text TEXT NOT NULL,tool_id TEXT,seq INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS messages_chat ON messages(chat_id,seq);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS attachments(owner_id TEXT PRIMARY KEY,json TEXT NOT NULL);
            """);
        using var columns = db.CreateCommand(); columns.CommandText = "SELECT name FROM pragma_table_info('chats') WHERE name='archived'";
        if (columns.ExecuteScalar() is null) Execute("ALTER TABLE chats ADD COLUMN archived INTEGER NOT NULL DEFAULT 0");
        using var providerColumn = db.CreateCommand(); providerColumn.CommandText = "SELECT name FROM pragma_table_info('chats') WHERE name='provider'";
        if (providerColumn.ExecuteScalar() is null) Execute("ALTER TABLE chats ADD COLUMN provider TEXT NOT NULL DEFAULT 'Codex'");
        using var recoveryColumn = db.CreateCommand(); recoveryColumn.CommandText = "SELECT name FROM pragma_table_info('chats') WHERE name='pending_input'";
        if (recoveryColumn.ExecuteScalar() is null) Execute("ALTER TABLE chats ADD COLUMN pending_input TEXT");
    }
    private void Execute(string sql, params (string, object?)[] args)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public List<Workspace> Workspaces()
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,name,path,distro FROM workspaces ORDER BY name";
        using var r = cmd.ExecuteReader(); List<Workspace> result = [];
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return result;
    }
    public void Save(Workspace w) => Execute("INSERT INTO workspaces VALUES($id,$name,$path,$distro) ON CONFLICT(id) DO UPDATE SET name=$name,path=$path,distro=$distro", ("$id", w.Id), ("$name", w.Name), ("$path", w.Path), ("$distro", w.Distro));
    public List<Chat> Chats()
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,workspace_id,session_id,title,updated,draft,archived,provider,pending_input FROM chats ORDER BY updated DESC";
        using var r = cmd.ExecuteReader(); List<Chat> result = [];
        while (r.Read()) result.Add(new() { Id = r.GetString(0), WorkspaceId = r.GetString(1), SessionId = r.IsDBNull(2) ? null : r.GetString(2), Title = r.GetString(3), Updated = DateTimeOffset.Parse(r.GetString(4)), Draft = r.GetString(5), Archived = r.GetBoolean(6), Provider = Enum.Parse<AgentProvider>(r.GetString(7)), PendingInput = r.IsDBNull(8) ? null : JsonSerializer.Deserialize(r.GetString(8), StoreJsonContext.Default.PendingInput) });
        r.Close();
        foreach (var c in result) foreach (var a in LoadAttachments(c.Id)) c.Attachments.Add(a);
        foreach (var chat in result)
        {
            if (Setting("interrupted:" + chat.Id) is { Length: > 0 } interrupted)
                chat.InterruptedInput = JsonSerializer.Deserialize(interrupted, StoreJsonContext.Default.PendingInput);
            else if (Setting("interrupted:" + chat.Id) is null) chat.InterruptedInput = chat.PendingInput;
            foreach (var queued in JsonSerializer.Deserialize(Setting("queue:" + chat.Id) ?? "[]", StoreJsonContext.Default.PendingInputArray) ?? []) chat.QueuedInputs.Add(queued);
            if (Setting("steering:" + chat.Id) is { Length: > 0 } steering)
            { chat.RecoverInput(JsonSerializer.Deserialize(steering, StoreJsonContext.Default.PendingInput)!); Setting("steering:" + chat.Id, ""); }
            if (chat.PendingInput is not { } pending) continue;
            chat.RecoverInput(pending);
            chat.Status = "Interrupted — input recovered";
            chat.PendingInput = null;
        }
        return result;
    }
    public void LoadMessages(Chat chat)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,role,text,tool_id FROM messages WHERE chat_id=$id ORDER BY seq"; cmd.Parameters.AddWithValue("$id", chat.Id);
        using var r = cmd.ExecuteReader();
        while (r.Read()) chat.Messages.Add(new() { Provider = chat.Provider, Id = r.GetString(0), Role = r.GetString(1), Text = r.GetString(2), ToolId = r.IsDBNull(3) ? null : r.GetString(3) });
        r.Close();
        foreach (var m in chat.Messages) foreach (var a in LoadAttachments(m.Id)) m.Attachments.Add(a);
    }
    public void Save(Chat c)
    {
        Setting("queue:" + c.Id, JsonSerializer.Serialize(c.QueuedInputs.ToArray(), StoreJsonContext.Default.PendingInputArray));
        var snapshot = JsonSerializer.Serialize(new ChatSnapshot(c.SessionId, c.Title, c.Updated, c.Draft, c.Archived, c.Provider, c.PendingInput), StoreJsonContext.Default.ChatSnapshot);
        if (savedChats.GetValueOrDefault(c.Id) == snapshot) { SaveAttachments(c.Id, c.Attachments); return; }
        Execute("INSERT INTO chats(id,workspace_id,session_id,title,updated,draft,archived,provider,pending_input) VALUES($id,$workspace,$session,$title,$updated,$draft,$archived,$provider,$pending) ON CONFLICT(id) DO UPDATE SET session_id=$session,title=$title,updated=$updated,draft=$draft,archived=$archived,provider=$provider,pending_input=$pending", ("$id", c.Id), ("$workspace", c.WorkspaceId), ("$session", c.SessionId), ("$title", c.Title), ("$updated", c.Updated.ToString("O")), ("$draft", c.Draft), ("$archived", c.Archived), ("$provider", c.Provider.ToString()), ("$pending", c.PendingInput is null ? null : JsonSerializer.Serialize(c.PendingInput, StoreJsonContext.Default.PendingInput)));
        savedChats[c.Id] = snapshot;
        SaveAttachments(c.Id, c.Attachments);
    }
    public void SaveMessage(Chat c, Message m)
    {
        if (savedMessages.GetValueOrDefault(m.Id) == m.Text) { SaveAttachments(m.Id, m.Attachments); return; }
        Execute("INSERT INTO messages VALUES($id,$chat,$role,$text,$tool,$seq) ON CONFLICT(id) DO UPDATE SET text=$text", ("$id", m.Id), ("$chat", c.Id), ("$role", m.Role), ("$text", m.Text), ("$tool", m.ToolId), ("$seq", c.Messages.IndexOf(m)));
        savedMessages[m.Id] = m.Text;
        SaveAttachments(m.Id, m.Attachments);
    }
    private void SaveAttachments(string id, IEnumerable<Attachment> attachments)
    {
        var values = attachments.ToArray();
        if (savedAttachments.TryGetValue(id, out var previous) && values.SequenceEqual(previous)) return;
        Execute("INSERT INTO attachments VALUES($id,$json) ON CONFLICT(owner_id) DO UPDATE SET json=$json WHERE json<>$json", ("$id", id), ("$json", JsonSerializer.Serialize(values, StoreJsonContext.Default.AttachmentArray)));
        savedAttachments[id] = values;
    }
    private Attachment[] LoadAttachments(string id)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM attachments WHERE owner_id=$id"; cmd.Parameters.AddWithValue("$id", id);
        return JsonSerializer.Deserialize(cmd.ExecuteScalar() as string ?? "[]", StoreJsonContext.Default.AttachmentArray) ?? [];
    }
    public string? Setting(string key)
    {
        if (key.StartsWith("interrupted:", StringComparison.Ordinal) && recovery.Read(key) is { } saved)
        { try { if (saved.Length == 0 || JsonSerializer.Deserialize(saved, StoreJsonContext.Default.PendingInput) is not null) return saved; } catch (JsonException) { } }
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM settings WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key); return cmd.ExecuteScalar() as string;
    }
    public void Setting(string key, string value)
    {
        if (key.StartsWith("interrupted:", StringComparison.Ordinal)) recovery.Write(key, value);
        Execute("INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value", ("$key", key), ("$value", value));
    }
    public void Delete(Chat chat)
    {
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM attachments WHERE owner_id=$id OR owner_id IN (SELECT id FROM messages WHERE chat_id=$id); DELETE FROM messages WHERE chat_id=$id; DELETE FROM chats WHERE id=$id";
        command.Parameters.AddWithValue("$id", chat.Id); command.ExecuteNonQuery(); transaction.Commit();
        savedChats.Remove(chat.Id); savedAttachments.Remove(chat.Id);
        foreach (var m in chat.Messages) { savedMessages.Remove(m.Id); savedAttachments.Remove(m.Id); }
    }
    public void Dispose() => db.Dispose();
}
