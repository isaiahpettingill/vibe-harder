using Markdig;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodexManager;

public sealed class Store : IDisposable
{
    private readonly SqliteConnection db;
    private readonly RecoveryJournal recovery;
    private readonly StoreWriter? writer;
    private readonly Dictionary<string, string> settings = [];
    private List<Workspace>? workspaceCache;
    private readonly Dictionary<string, ChatSnapshot> savedChats = [];
    private readonly Dictionary<string, (WeakReference<Message> Message, int Revision)> savedMessages = [];
    private readonly Dictionary<string, WeakReference<Attachment[]>> savedAttachments = [];
    public static string DataDirectory => Environment.GetEnvironmentVariable("CODEX_MANAGER_DATA") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexManager");
    public string DirectoryPath { get; }
    public Store(string? directory = null, bool backgroundWrites = false)
    {
        directory ??= DataDirectory;
        DirectoryPath = Path.GetFullPath(directory);
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
        if (backgroundWrites)
        {
            using var read = db.CreateCommand(); read.CommandText = "SELECT key,value FROM settings";
            using (var values = read.ExecuteReader()) while (values.Read()) settings[values.GetString(0)] = values.GetString(1);
            using var ids = db.CreateCommand(); ids.CommandText = "SELECT id FROM chats";
            using (var rows = ids.ExecuteReader()) while (rows.Read())
            {
                var key = "interrupted:" + rows.GetString(0); var recovered = recovery.Read(key);
                if (recovered is not null) { try { if (recovered.Length == 0 || JsonSerializer.Deserialize(recovered, StoreJsonContext.Default.PendingInput) is not null) settings[key] = recovered; } catch (JsonException) { } }
            }
            workspaceCache = Workspaces();
            writer = new StoreWriter(db.ConnectionString);
        }
    }
    private void Execute(string sql, params (string, object?)[] args)
    {
        if (writer is not null) { writer.Enqueue(connection => ExecuteOn(connection, sql, args)); return; }
        ExecuteOn(db, sql, args);
    }
    private static void ExecuteOn(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public Task FlushAsync() => writer?.Flush() ?? Task.CompletedTask;
    public List<Workspace> Workspaces()
    {
        if (workspaceCache is not null) return workspaceCache.OrderBy(w => w.Name).ToList();
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,name,path,distro FROM workspaces ORDER BY name";
        using var r = cmd.ExecuteReader(); List<Workspace> result = [];
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return result;
    }
    public void Save(Workspace w)
    {
        if (workspaceCache is not null) { workspaceCache.RemoveAll(existing => existing.Id == w.Id); workspaceCache.Add(w); }
        Execute("INSERT INTO workspaces VALUES($id,$name,$path,$distro) ON CONFLICT(id) DO UPDATE SET name=$name,path=$path,distro=$distro", ("$id", w.Id), ("$name", w.Name), ("$path", w.Path), ("$distro", w.Distro));
    }
    public List<Chat> Chats()
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,workspace_id,session_id,title,updated,draft,archived,provider,pending_input FROM chats ORDER BY updated DESC";
        using var r = cmd.ExecuteReader(); List<Chat> result = [];
        while (r.Read()) result.Add(new() { Id = r.GetString(0), WorkspaceId = r.GetString(1), SessionId = r.IsDBNull(2) ? null : r.GetString(2), Title = r.GetString(3), Updated = DateTimeOffset.Parse(r.GetString(4)), Draft = r.GetString(5), Archived = r.GetBoolean(6), Provider = Enum.Parse<AgentProvider>(r.GetString(7)), PendingInput = r.IsDBNull(8) ? null : JsonSerializer.Deserialize(r.GetString(8), StoreJsonContext.Default.PendingInput) });
        r.Close();
        foreach (var c in result) foreach (var a in LoadAttachments(c.Id)) c.Attachments.Add(a);
        foreach (var chat in result)
        {
            var cachedStatus = Setting("status:" + chat.Id);
            chat.Status = Setting("busy:" + chat.Id) == "1" ? "Interrupted" : cachedStatus is null or "Needs permission" ? "Ready" : cachedStatus;
            chat.HasUnreadCompletion = Setting("unread:" + chat.Id) == "1";
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
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,role,text,tool_id,seq FROM messages WHERE chat_id=$id ORDER BY seq"; cmd.Parameters.AddWithValue("$id", chat.Id);
        using var r = cmd.ExecuteReader();
        while (r.Read()) chat.Messages.Add(new() { Provider = chat.Provider, Id = r.GetString(0), Role = r.GetString(1), Text = r.GetString(2), ToolId = r.IsDBNull(3) ? null : r.GetString(3), Sequence = r.GetInt32(4) });
        r.Close();
        chat.HistoryLoaded = true; chat.NextSequence = chat.Messages.Count == 0 ? 0 : chat.Messages.Max(m => m.Sequence) + 1;
        foreach (var m in chat.Messages)
        {
            foreach (var a in LoadAttachments(m.Id)) m.Attachments.Add(a);
            if (savedMessages.Count >= 2048) savedMessages.Clear();
            savedMessages[m.Id] = (new(m), m.Revision); savedAttachments[m.Id] = new(m.Attachments.ToArray());
        }
    }
    public async Task<Message[]> ReadPageAsync(Chat chat, int? before = null, int limit = Chat.HistoryPageSize, CancellationToken token = default, string? toolId = null, bool newer = false)
    {
        var id = chat.Id; var provider = chat.Provider; var connectionString = db.ConnectionString;
        await FlushAsync().WaitAsync(token);
        return await Task.Run(() =>
        {
            using var connection = new SqliteConnection(connectionString); connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT m.id,m.role,m.text,m.tool_id,m.seq,a.json FROM messages m LEFT JOIN attachments a ON a.owner_id=m.id WHERE m.chat_id=$id AND ($tool IS NULL OR m.tool_id=$tool) AND ($before IS NULL OR m.seq" + (newer ? ">" : "<") + "$before) ORDER BY m.seq " + (newer ? "ASC" : "DESC") + " LIMIT $limit";
            command.Parameters.AddWithValue("$tool", (object?)toolId ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value); command.Parameters.AddWithValue("$limit", limit);
            using var rows = command.ExecuteReader(); var result = new List<Message>();
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                var message = new Message { Id = rows.GetString(0), Role = rows.GetString(1), Text = rows.GetString(2), ToolId = rows.IsDBNull(3) ? null : rows.GetString(3), Sequence = rows.GetInt32(4), Provider = provider };
                if (!rows.IsDBNull(5)) foreach (var attachment in JsonSerializer.Deserialize(rows.GetString(5), StoreJsonContext.Default.AttachmentArray) ?? []) message.Attachments.Add(attachment);
                result.Add(message);
            }
            if (!newer) result.Reverse(); return result.ToArray();
        }, token);
    }
    public async Task<(string Plain, string Html)> ExportChatAsync(Chat chat)
    {
        var id = chat.Id; var provider = chat.Provider; var connectionString = db.ConnectionString;
        await FlushAsync();
        return await Task.Run(() =>
        {
            using var connection = new SqliteConnection(connectionString); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "SELECT role,text FROM messages WHERE chat_id=$id ORDER BY seq"; command.Parameters.AddWithValue("$id", id);
            using var rows = command.ExecuteReader(); var plain = new System.Text.StringBuilder(); var html = new System.Text.StringBuilder();
            var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
            while (rows.Read())
            {
                var role = rows.GetString(0); var text = rows.GetString(1); var label = role == "assistant" ? AgentProviders.Get(provider).Name : role.ToUpperInvariant();
                plain.Append(label).Append('\n').Append(text).Append("\n\n");
                html.Append("<h3>").Append(System.Net.WebUtility.HtmlEncode(label)).Append("</h3>").Append(Markdig.Markdown.ToHtml(text, pipeline));
            }
            return (plain.ToString(), html.ToString());
        });
    }
    public async Task<HashSet<string>> SearchChatIdsAsync(string query, CancellationToken token)
    {
        var connectionString = db.ConnectionString; await FlushAsync().WaitAsync(token);
        return await Task.Run(() =>
        {
            using var connection = new SqliteConnection(connectionString); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "SELECT DISTINCT chat_id FROM messages WHERE instr(lower(text),lower($query))>0"; command.Parameters.AddWithValue("$query", query);
            using var rows = command.ExecuteReader(); var result = new HashSet<string>();
            while (rows.Read()) { token.ThrowIfCancellationRequested(); result.Add(rows.GetString(0)); }
            return result;
        }, token);
    }
    public void ClearHistory(Chat chat)
    {
        Execute("DELETE FROM attachments WHERE owner_id IN (SELECT id FROM messages WHERE chat_id=$id); DELETE FROM messages WHERE chat_id=$id", ("$id", chat.Id));
        chat.Messages.Clear(); chat.NextSequence = 0;
    }
    public void ApplyRecentPage(Chat chat, Message[] messages)
    {
        chat.Messages.Clear(); foreach (var message in messages) { chat.Messages.Add(message); savedMessages[message.Id] = (new(message), message.Revision); }
        chat.HistoryLoaded = true; chat.NextSequence = messages.Length == 0 ? 0 : messages[^1].Sequence + 1;
    }
    public void ReleaseHistory(Chat chat)
    {
        if (chat.Busy) return;
        foreach (var message in chat.Messages) { SaveMessage(chat, message); savedMessages.Remove(message.Id); savedAttachments.Remove(message.Id); }
        chat.Messages.Clear(); chat.HistoryLoaded = false;
    }
    public void TrimHistory(Chat chat)
    {
        var limit = chat.RetainHistory ? Chat.HistoryPageSize : 1;
        if (!chat.RetainHistory) chat.HistoryLoaded = false;
        while (chat.Messages.Count > limit) { SaveMessage(chat, chat.Messages[0]); chat.Messages.RemoveAt(0); }
    }
    public void Save(Chat c)
    {
        Setting("status:" + c.Id, c.NeedsPermission ? "Working…" : c.Status);
        Setting("busy:" + c.Id, c.Busy ? "1" : "0");
        Setting("unread:" + c.Id, c.HasUnreadCompletion ? "1" : "0");
        Setting("queue:" + c.Id, JsonSerializer.Serialize(c.QueuedInputs.ToArray(), StoreJsonContext.Default.PendingInputArray));
        var snapshot = new ChatSnapshot(c.SessionId, c.Title, c.Updated, c.Draft, c.Archived, c.Provider, c.PendingInput);
        if (savedChats.GetValueOrDefault(c.Id) == snapshot) { SaveAttachments(c.Id, c.Attachments); return; }
        Execute("INSERT INTO chats(id,workspace_id,session_id,title,updated,draft,archived,provider,pending_input) VALUES($id,$workspace,$session,$title,$updated,$draft,$archived,$provider,$pending) ON CONFLICT(id) DO UPDATE SET session_id=$session,title=$title,updated=$updated,draft=$draft,archived=$archived,provider=$provider,pending_input=$pending", ("$id", c.Id), ("$workspace", c.WorkspaceId), ("$session", c.SessionId), ("$title", c.Title), ("$updated", c.Updated.ToString("O")), ("$draft", c.Draft), ("$archived", c.Archived), ("$provider", c.Provider.ToString()), ("$pending", c.PendingInput is null ? null : JsonSerializer.Serialize(c.PendingInput, StoreJsonContext.Default.PendingInput)));
        savedChats[c.Id] = snapshot;
        SaveAttachments(c.Id, c.Attachments);
    }
    public void SaveMessage(Chat c, Message m)
    {
        if (savedMessages.TryGetValue(m.Id, out var saved) && saved.Message.TryGetTarget(out var target) && ReferenceEquals(target, m) && saved.Revision == m.Revision) { SaveAttachments(m.Id, m.Attachments); return; }
        if (m.Sequence < 0) m.Sequence = c.NextSequence++;
        c.NextSequence = Math.Max(c.NextSequence, m.Sequence + 1);
        const string sql = "INSERT INTO messages VALUES($id,$chat,$role,$text,$tool,$seq) ON CONFLICT(id) DO UPDATE SET text=$text";
        (string, object?)[] args = [("$id", m.Id), ("$chat", c.Id), ("$role", m.Role), ("$text", m.Text), ("$tool", m.ToolId), ("$seq", m.Sequence)];
        if (writer is not null) writer.Enqueue(connection => ExecuteOn(connection, sql, args), "message:" + m.Id);
        else Execute(sql, args);
        if (savedMessages.Count >= 2048) savedMessages.Clear();
        savedMessages[m.Id] = (new(m), m.Revision);
        SaveAttachments(m.Id, m.Attachments);
    }
    private void SaveAttachments(string id, IEnumerable<Attachment> attachments)
    {
        var values = attachments.ToArray();
        if (savedAttachments.TryGetValue(id, out var cached) && cached.TryGetTarget(out var previous) && values.SequenceEqual(previous)) return;
        if (writer is not null) writer.Enqueue(connection => ExecuteOn(connection, "INSERT INTO attachments VALUES($id,$json) ON CONFLICT(owner_id) DO UPDATE SET json=$json WHERE json<>$json", ("$id", id), ("$json", JsonSerializer.Serialize(values, StoreJsonContext.Default.AttachmentArray))), "attachments:" + id);
        else Execute("INSERT INTO attachments VALUES($id,$json) ON CONFLICT(owner_id) DO UPDATE SET json=$json WHERE json<>$json", ("$id", id), ("$json", JsonSerializer.Serialize(values, StoreJsonContext.Default.AttachmentArray)));
        if (savedAttachments.Count >= 2048) savedAttachments.Clear();
        savedAttachments[id] = new(values);
    }
    private Attachment[] LoadAttachments(string id)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM attachments WHERE owner_id=$id"; cmd.Parameters.AddWithValue("$id", id);
        return JsonSerializer.Deserialize(cmd.ExecuteScalar() as string ?? "[]", StoreJsonContext.Default.AttachmentArray) ?? [];
    }
    public string? Setting(string key)
    {
        if (writer is not null) return settings.GetValueOrDefault(key);
        if (key.StartsWith("interrupted:", StringComparison.Ordinal) && recovery.Read(key) is { } saved)
        { try { if (saved.Length == 0 || JsonSerializer.Deserialize(saved, StoreJsonContext.Default.PendingInput) is not null) return saved; } catch (JsonException) { } }
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM settings WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key); return cmd.ExecuteScalar() as string;
    }
    public void Setting(string key, string value)
    {
        if (writer is not null)
        {
            if (settings.GetValueOrDefault(key) == value) return;
            writer.Enqueue(connection => { if (key.StartsWith("interrupted:", StringComparison.Ordinal)) recovery.Write(key, value); ExecuteOn(connection, "INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value", ("$key", key), ("$value", value)); });
            settings[key] = value;
        }
        else { if (key.StartsWith("interrupted:", StringComparison.Ordinal)) recovery.Write(key, value); Execute("INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value", ("$key", key), ("$value", value)); }
    }
    public void Delete(Chat chat)
    {
        const string sql = "DELETE FROM attachments WHERE owner_id=$id OR owner_id IN (SELECT id FROM messages WHERE chat_id=$id); DELETE FROM messages WHERE chat_id=$id; DELETE FROM chats WHERE id=$id";
        if (writer is not null) Execute(sql, ("$id", chat.Id));
        else { using var transaction = db.BeginTransaction(); Execute(sql, ("$id", chat.Id)); transaction.Commit(); }
        savedChats.Remove(chat.Id); savedAttachments.Remove(chat.Id);
        foreach (var m in chat.Messages) { savedMessages.Remove(m.Id); savedAttachments.Remove(m.Id); }
    }
    public void Dispose() { try { writer?.Close().GetAwaiter().GetResult(); } finally { db.Dispose(); } }
}
