using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager.Cli;

// vh: a non-interactive command line for Vibe Harder. Each command prints the current state and
// exits; nothing streams. Commands go to the app running on this machine, or with --host to a
// paired computer, using the same requests as the mobile and web clients.
public static class Program
{
    private const string Usage = """
        Usage: vh [--host NAME] [--json] <command> [arguments]

        Chats
          status                       Overview: running chats and anything waiting for you
          workspaces                   List workspaces
          workspaces add PATH [--name NAME] [--distro DISTRO]
                                       Open a folder (on the host) as a workspace
          chats [-w WORKSPACE] [--all] List chats (--all includes archived)
          show CHAT [-n COUNT] [--full]
                                       Recent messages (default 20; --full includes tool output)
          status CHAT                  State, queue, and pending approvals or questions
          new WORKSPACE [-p PROVIDER] [MESSAGE...] [--wait]
                                       Start a chat, optionally with a first message
          send CHAT MESSAGE... [--wait] [--timeout SECONDS] [--queue | --now]
                                       Send a message ("-" reads it from stdin). Busy chats queue it.
                                       --wait checks until the turn ends, then prints the reply.
                                       --now interrupts the running turn.
          stop CHAT                    Stop the running turn
          approve CHAT [OPTION]        Answer a permission request (lists options when omitted)
          answer CHAT [VALUE | FIELD=VALUE...] [--decline]
                                       Answer an agent's question

        Computers
          hosts                        Paired computers
          pair ADDRESS[:PORT] [--name NAME]
                                       Pair with a computer; enter the code it shows
          unpair NAME                  Forget a paired computer

        CHAT accepts an id, an id prefix, or a title. WORKSPACE accepts an id, name, or path.
        Without --host, commands go to Vibe Harder running on this computer.
        Exit codes: 0 done, 1 error, 2 usage, 3 waiting for your input or timed out.
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try { return await Run(new Arguments(args)); }
        catch (UsageException error) { Console.Error.WriteLine(error.Message); Console.Error.WriteLine("Run vh help for usage."); return 2; }
        catch (Exception error) when (error is IOException or RemoteOperationException or FormatException or TimeoutException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or UnauthorizedAccessException)
        { Console.Error.WriteLine(error.Message); return 1; }
    }

    private static async Task<int> Run(Arguments args)
    {
        var json = args.Flag("--json");
        var hostName = args.Option("--host");
        var command = args.Next() ?? "help";
        if (command is "help" or "--help" or "-h") { Console.WriteLine(Usage); return 0; }
        if (command == "hosts") return Hosts(json);
        if (command == "pair") { var deviceName = args.Option("--name"); return await Pair(args.Required("ADDRESS"), deviceName); }
        if (command == "unpair") return Unpair(args.Required("NAME"));
        await using var client = await Client.Open(hostName);
        switch (command)
        {
            case "status":
                return args.Next() is { } statusChat ? await ChatStatus(client, statusChat, json) : await Overview(client, json);
            case "workspaces":
            {
                if (args.Next() is "add")
                {
                    var name = args.Option("--name"); var distro = args.Option("--distro");
                    var added = await client.Call(new() { ["method"] = "workspace", ["path"] = args.Required("PATH"), ["name"] = name, ["distro"] = distro });
                    if (json) return Print(added);
                    Console.WriteLine(added!.GetValue<string>()); return 0;
                }
                var list = await client.List();
                if (json) return Print(list["workspaces"]);
                foreach (var w in list["workspaces"]!.AsArray())
                    Console.WriteLine($"{Short(w!["id"])}  {w["name"]}  {w["path"]}{(w["distro"] is { } d ? $" ({d})" : "")}");
                return 0;
            }
            case "chats":
            {
                var all = args.Flag("--all"); var filter = args.Option("-w") ?? args.Option("--workspace");
                var list = await client.List();
                var workspace = filter is null ? null : Resolve.Workspace(list, filter);
                var chats = list["chats"]!.AsArray().Where(c => (all || c!["archived"]?.GetValue<bool>() != true) && (workspace is null || c!["workspaceId"]!.GetValue<string>() == workspace["id"]!.GetValue<string>())).ToArray();
                if (json) return Print(new JsonArray(chats.Select(c => c!.DeepClone()).ToArray()));
                var names = list["workspaces"]!.AsArray().ToDictionary(w => w!["id"]!.GetValue<string>(), w => w!["name"]!.GetValue<string>());
                foreach (var c in chats)
                    Console.WriteLine($"{Short(c!["id"])}  {State(c),-11} {c["provider"],-8} {names.GetValueOrDefault(c["workspaceId"]!.GetValue<string>(), "?")} / {c["title"]}");
                if (chats.Length == 0) Console.WriteLine("No chats.");
                return 0;
            }
            case "show":
            {
                var count = int.TryParse(args.Option("-n"), out var n) ? n : 20; var full = args.Flag("--full");
                var chat = await client.Chat(args.Required("CHAT"));
                if (json) return Print(chat);
                Header(chat);
                foreach (var message in chat["messages"]!.AsArray().TakeLast(count)) PrintMessage(message!, full);
                return 0;
            }
            case "new":
            {
                var wait = args.Flag("--wait"); var timeout = Seconds(args.Option("--timeout")); var requested = args.Option("-p") ?? args.Option("--provider");
                var list = await client.List();
                var workspace = Resolve.Workspace(list, args.Required("WORKSPACE"));
                var provider = requested ?? list["providers"]!.AsArray().FirstOrDefault()?.GetValue<string>() ?? throw new IOException("No agent providers are enabled on this computer.");
                provider = list["providers"]!.AsArray().Select(p => p!.GetValue<string>()).FirstOrDefault(p => p.Equals(provider, StringComparison.OrdinalIgnoreCase)) ?? throw new UsageException("Unknown or disabled provider: " + provider);
                var text = MessageText(args.Rest());
                var created = (await client.Call(new() { ["method"] = "create", ["workspaceId"] = workspace["id"]!.DeepClone(), ["provider"] = provider }))!.AsObject();
                var id = created["id"]!.GetValue<string>();
                if (text.Length == 0) { if (json) return Print(created); Console.WriteLine(id); return 0; }
                return await Send(client, id, text, "send", wait, timeout, json, -1);
            }
            case "send":
            {
                var wait = args.Flag("--wait"); var timeout = Seconds(args.Option("--timeout"));
                var method = args.Flag("--now") ? "send-now" : args.Flag("--queue") ? "queue" : "send";
                var chatName = args.Required("CHAT");
                var text = MessageText(args.Rest());
                if (text.Length == 0) throw new UsageException("Enter a message, or - to read it from stdin.");
                var chat = await client.Chat(chatName);
                var last = chat["messages"]!.AsArray().Select(m => m!["sequence"]?.GetValue<int>() ?? -1).DefaultIfEmpty(-1).Max();
                return await Send(client, chat["id"]!.GetValue<string>(), text, method, wait, timeout, json, last);
            }
            case "stop":
            {
                var chat = await client.Chat(args.Required("CHAT"));
                var result = await client.Call(new() { ["method"] = "stop", ["chatId"] = chat["id"]!.DeepClone() });
                if (json) return Print(result);
                Console.WriteLine("Stopped " + chat["title"] + "."); return 0;
            }
            case "approve": return await Approve(client, args.Required("CHAT"), args.Next(), json);
            case "answer": { var decline = args.Flag("--decline"); return await Answer(client, args.Required("CHAT"), args.Rest(), decline, json); }
            default: throw new UsageException("Unknown command: " + command);
        }
    }

    private static async Task<int> Overview(Client client, bool json)
    {
        var list = await client.List();
        if (json) return Print(list);
        var chats = list["chats"]!.AsArray().Where(c => c!["archived"]?.GetValue<bool>() != true).ToArray();
        Console.WriteLine($"{client.Name}: {list["workspaces"]!.AsArray().Count} workspaces, {chats.Length} chats. Agents: {string.Join(", ", list["providers"]!.AsArray().Select(p => p!.GetValue<string>()))}");
        foreach (var c in chats.Where(c => c!["busy"]?.GetValue<bool>() == true || c["needsPermission"]?.GetValue<bool>() == true || c["queued"]?.GetValue<int>() > 0 || c["interrupted"]?.GetValue<bool>() == true))
            Console.WriteLine($"  {Short(c!["id"])}  {State(c),-11} {c["title"]}  ({c["status"]})");
        foreach (var p in list["permissions"]!.AsArray())
            Console.WriteLine($"  Approval needed in {Short(p!["chatId"])} {p["chatTitle"]}: {p["toolCall"]?["title"] ?? "permission request"}  → vh approve {Short(p["chatId"])}");
        foreach (var q in list["elicitations"]!.AsArray())
            Console.WriteLine($"  Question in {Short(q!["chatId"])} {q["chatTitle"]}: {q["message"]}  → vh answer {Short(q["chatId"])}");
        return 0;
    }

    private static async Task<int> ChatStatus(Client client, string name, bool json)
    {
        var chat = await client.Chat(name);
        if (json) { chat.Remove("messages"); return Print(chat); }
        Header(chat);
        foreach (var q in chat["queue"]!.AsArray()) Console.WriteLine($"  Queued: {OneLine(q!["text"]!.GetValue<string>())}");
        PrintPending(chat);
        if (chat["interrupted"]?.GetValue<bool>() == true) Console.WriteLine("  The last request was interrupted.");
        return 0;
    }

    private static async Task<int> Send(Client client, string chatId, string text, string method, bool wait, TimeSpan timeout, bool json, int after)
    {
        var sent = await client.Call(new() { ["method"] = method, ["chatId"] = chatId, ["text"] = text });
        if (!wait)
        {
            if (json) return Print(sent);
            var queued = sent?["queued"]?.GetValue<int>() > 0 && method != "send-now";
            Console.WriteLine((queued ? "Queued in " : "Sent to ") + chatId[..8] + ". Check with: vh status " + chatId[..8]);
            return 0;
        }
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var chat = await client.Chat(chatId);
            var pending = chat["permissions"]!.AsArray().Count + chat["elicitations"]!.AsArray().Count > 0;
            var done = chat["busy"]?.GetValue<bool>() != true && chat["queue"]!.AsArray().Count == 0 && chat["preparing"]?.GetValue<bool>() != true;
            if (done || pending || DateTime.UtcNow > deadline)
            {
                var replies = chat["messages"]!.AsArray().Where(m => (m!["sequence"]?.GetValue<int>() ?? -1) > after && m["role"]?.GetValue<string>() != "user").ToArray();
                if (json) { chat["messages"] = new JsonArray(replies.Select(m => m!.DeepClone()).ToArray()); return Print(chat); }
                foreach (var message in replies) PrintMessage(message!, full: false);
                if (pending) { PrintPending(chat); return 3; }
                if (!done) { Console.Error.WriteLine($"Still working after {timeout.TotalSeconds:0} seconds. Check with: vh status {chatId[..8]}"); return 3; }
                return 0;
            }
        }
    }

    private static async Task<int> Approve(Client client, string name, string? choice, bool json)
    {
        var chat = await client.Chat(name);
        var pending = chat["permissions"]!.AsArray().OfType<JsonObject>().ToArray();
        if (pending.Length == 0) throw new IOException("No approval is pending in this chat.");
        var request = pending[0];
        var options = request["options"]!.AsArray().OfType<JsonObject>().ToArray();
        if (choice is null)
        {
            if (json) return Print(request);
            Console.WriteLine(request["toolCall"]?["title"] ?? "Permission request");
            foreach (var o in options) Console.WriteLine($"  {o["optionId"]}  {o["name"]}");
            Console.WriteLine($"Answer with: vh approve {Short(chat["id"])} OPTION");
            return 3;
        }
        var option = options.FirstOrDefault(o => Same(o["optionId"], choice)) ?? options.FirstOrDefault(o => Same(o["name"], choice)) ?? options.FirstOrDefault(o => Same(o["kind"], choice))
            ?? throw new UsageException("Unknown option. Choose one of: " + string.Join(", ", options.Select(o => o["optionId"])));
        await client.Call(new() { ["method"] = "approve", ["permissionId"] = request["id"]!.DeepClone(), ["optionId"] = option["optionId"]!.DeepClone() });
        Console.WriteLine("Answered: " + option["name"]); return 0;
    }

    private static async Task<int> Answer(Client client, string name, string[] values, bool decline, bool json)
    {
        var chat = await client.Chat(name);
        var request = chat["elicitations"]!.AsArray().OfType<JsonObject>().FirstOrDefault() ?? throw new IOException("No question is pending in this chat.");
        var properties = request["requestedSchema"]?["properties"] as JsonObject ?? [];
        if (!decline && values.Length == 0)
        {
            if (json) return Print(request);
            Console.WriteLine(request["message"]);
            foreach (var (field, schema) in properties)
            {
                var choices = Choices(schema);
                Console.WriteLine($"  {field}: {schema?["title"] ?? schema?["description"] ?? ""}{(choices.Length > 0 ? " [" + string.Join(" | ", choices.Select(c => c.Title)) + "]" : "")}");
            }
            Console.WriteLine($"Answer with: vh answer {Short(chat["id"])} VALUE  (or FIELD=VALUE for several fields, or --decline)");
            return 3;
        }
        var response = new JsonObject { ["action"] = decline ? "decline" : "accept" };
        if (!decline)
        {
            var content = new JsonObject();
            var first = properties.Select(p => p.Key).FirstOrDefault() ?? throw new IOException("This question has no fields to answer.");
            foreach (var value in values)
            {
                var split = value.IndexOf('=');
                var (field, text) = split > 0 && properties.ContainsKey(value[..split]) ? (value[..split], value[(split + 1)..]) : (first, value);
                content[field] = Typed(properties[field], text);
            }
            response["content"] = content;
        }
        await client.Call(new() { ["method"] = "elicitation/respond", ["elicitationId"] = request["id"]!.DeepClone(), ["response"] = response });
        Console.WriteLine(decline ? "Declined." : "Answered."); return 0;
    }
    private static (JsonNode? Value, string Title)[] Choices(JsonNode? schema) =>
        (schema?["oneOf"] ?? schema?["anyOf"]) is JsonArray options ? options.Select(o => (o?["const"]?.DeepClone(), o?["title"]?.GetValue<string>() ?? o?["const"]?.ToString() ?? "")).ToArray()
        : schema?["enum"] is JsonArray values ? values.Select(v => (v?.DeepClone(), v?.ToString() ?? "")).ToArray() : [];
    private static JsonNode? Typed(JsonNode? schema, string text)
    {
        if (Choices(schema).FirstOrDefault(c => c.Title.Equals(text, StringComparison.OrdinalIgnoreCase) || c.Value?.ToString() == text) is { Value: { } choice }) return choice;
        return schema?["type"]?.GetValue<string>() switch
        {
            "boolean" => text.ToLowerInvariant() is "true" or "yes" or "y" or "1",
            "integer" => long.Parse(text, CultureInfo.InvariantCulture),
            "number" => double.Parse(text, CultureInfo.InvariantCulture),
            "array" => new JsonArray(text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t => (JsonNode)t).ToArray()),
            _ => text
        };
    }

    private static int Hosts(bool json)
    {
        var hosts = HostStore.Load();
        if (json) return Print(new JsonArray(hosts.Select(h => (JsonNode)new JsonObject { ["name"] = h.Name, ["address"] = h.Address, ["port"] = h.Port, ["fingerprint"] = h.Fingerprint }).ToArray()));
        foreach (var h in hosts) Console.WriteLine($"{h.Name}  {h.Address}:{h.Port}");
        if (hosts.Count == 0) Console.WriteLine("No paired computers. Pair with: vh pair ADDRESS");
        return 0;
    }
    private static async Task<int> Pair(string address, string? name)
    {
        using var session = await RemotePairingSession.Start(address, name ?? Environment.MachineName + " (vh)", CancellationToken.None);
        Console.Error.Write("Enter the six-digit code shown on the computer: ");
        var code = Console.ReadLine()?.Trim() ?? throw new IOException("No code entered.");
        Directory.CreateDirectory(HostStore.Directory);
        var host = await session.Complete(code, Path.Combine(HostStore.Directory, Guid.NewGuid().ToString("N") + ".credential"), CancellationToken.None);
        var hosts = HostStore.Load().Where(h => !(h.Address == host.Address && h.Port == host.Port)).Append(host).ToList();
        HostStore.Save(hosts);
        Console.WriteLine($"Paired with {host.Name}. Use: vh --host \"{host.Name}\" status");
        return 0;
    }
    private static int Unpair(string name)
    {
        var hosts = HostStore.Load();
        var host = HostStore.Find(hosts, name);
        try { File.Delete(host.KeyPath); } catch (IOException) { }
        HostStore.Save(hosts.Where(h => h != host).ToList());
        Console.WriteLine("Forgot " + host.Name + "."); return 0;
    }

    private static void Header(JsonObject chat)
    {
        Console.WriteLine($"{chat["title"]}  [{Short(chat["id"])} · {chat["provider"]} · {State(chat)}]");
        if (chat["status"]?.GetValue<string>() is { Length: > 0 } status) Console.WriteLine("  " + status);
    }
    private static void PrintPending(JsonObject chat)
    {
        foreach (var p in chat["permissions"]!.AsArray())
        {
            Console.WriteLine($"  Approval needed: {p!["toolCall"]?["title"] ?? "permission request"}");
            foreach (var o in p["options"]!.AsArray()) Console.WriteLine($"    {o!["optionId"]}  {o["name"]}");
            Console.WriteLine($"  Answer with: vh approve {Short(chat["id"])} OPTION");
        }
        foreach (var q in chat["elicitations"]!.AsArray())
        {
            Console.WriteLine($"  Question: {q!["message"]}");
            foreach (var (field, schema) in q["requestedSchema"]?["properties"] as JsonObject ?? []) Console.WriteLine($"    {field}: {schema?["title"] ?? ""}");
            Console.WriteLine($"  Answer with: vh answer {Short(chat["id"])} VALUE");
        }
    }
    private static void PrintMessage(JsonNode message, bool full)
    {
        var role = message["role"]?.GetValue<string>() ?? "";
        var text = message["text"]?.GetValue<string>() ?? "";
        if (role == "thought" && !full) return;
        var time = DateTimeOffset.TryParse(message["timestamp"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToLocalTime().ToString("HH:mm") + " " : "";
        // Tool calls show their title unless asked for everything; their output can be huge.
        if (role == "tool" && !full) { Console.WriteLine($"{time}[tool] {OneLine(text)}"); return; }
        Console.WriteLine($"{time}[{role}]");
        Console.WriteLine(text.TrimEnd());
        Console.WriteLine();
    }
    private static string State(JsonNode? chat) =>
        chat?["needsPermission"]?.GetValue<bool>() == true ? "needs input" : chat?["busy"]?.GetValue<bool>() == true ? "working" : chat?["archived"]?.GetValue<bool>() == true ? "archived" : chat?["unread"]?.GetValue<bool>() == true ? "done" : "ready";
    private static string Short(JsonNode? id) => id?.GetValue<string>() is { Length: >= 8 } value ? value[..8] : id?.ToString() ?? "";
    private static string OneLine(string text) { var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? ""; return line.Length > 120 ? line[..119] + "…" : line; }
    private static bool Same(JsonNode? node, string text) => node?.GetValue<string>().Equals(text, StringComparison.OrdinalIgnoreCase) == true;
    private static int Print(JsonNode? node) { Console.WriteLine(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null"); return 0; }
    private static TimeSpan Seconds(string? value) => TimeSpan.FromSeconds(double.TryParse(value, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 600);
    private static string MessageText(string[] words) => words is ["-"] ? Console.In.ReadToEnd().Trim() : string.Join(' ', words).Trim();
}

internal sealed class UsageException(string message) : Exception(message);

// Flags and options may appear anywhere; the remaining words are positional.
internal sealed class Arguments(string[] args)
{
    private readonly List<string> items = [.. args];
    public bool Flag(string name) => items.Remove(name);
    public string? Option(string name)
    {
        var index = items.IndexOf(name);
        if (index < 0) return null;
        if (index + 1 >= items.Count) throw new UsageException(name + " needs a value.");
        var value = items[index + 1]; items.RemoveRange(index, 2); return value;
    }
    public string? Next() { if (items.Count == 0) return null; var value = items[0]; items.RemoveAt(0); return value; }
    public string Required(string name) => Next() ?? throw new UsageException("Missing " + name + ".");
    public string[] Rest() { var rest = items.ToArray(); items.Clear(); return rest; }
}

internal static class Resolve
{
    public static JsonObject Chat(JsonObject list, string name) => One(list["chats"]!.AsArray(), name, c => [c["title"]?.GetValue<string>()], "chat");
    public static JsonObject Workspace(JsonObject list, string name) => One(list["workspaces"]!.AsArray(), name, w => [w["name"]?.GetValue<string>(), w["path"]?.GetValue<string>()], "workspace");
    private static JsonObject One(JsonArray items, string name, Func<JsonObject, string?[]> labels, string kind)
    {
        var all = items.OfType<JsonObject>().ToArray();
        var matches = all.Where(i => i["id"]!.GetValue<string>() == name).ToArray();
        if (matches.Length == 0 && name.Length >= 4) matches = all.Where(i => i["id"]!.GetValue<string>().StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) matches = all.Where(i => labels(i).Any(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length == 0) matches = all.Where(i => labels(i).Any(l => l?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new IOException($"No {kind} matches \"{name}\"."),
            _ => throw new IOException($"\"{name}\" matches {matches.Length} {kind}s: " + string.Join(", ", matches.Take(5).Select(m => m["id"]!.GetValue<string>()[..8] + " " + labels(m)[0])) + ". Use the id.")
        };
    }
}

// The app on this computer (over a local pipe) or a paired computer (over the remote protocol).
internal sealed class Client : IAsyncDisposable
{
    private readonly RemoteConnection? remote;
    public string Name { get; }
    private Client(string name, RemoteConnection? remote) { Name = name; this.remote = remote; }
    public static async Task<Client> Open(string? hostName)
    {
        if (hostName is null) return new(Environment.MachineName, null);
        var host = HostStore.Find(HostStore.Load(), hostName);
        var connection = new RemoteConnection(host);
        try { await connection.Connect(CancellationToken.None); }
        catch { connection.Dispose(); throw; }
        return new(host.Name, connection);
    }
    public async Task<JsonNode?> Call(JsonObject request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        return remote is null ? await CliPipe.Request(CliPipe.DataDirectory, request, timeout.Token) : await remote.Request(request, timeout.Token);
    }
    public async Task<JsonObject> List() => (await Call(new() { ["method"] = "list" }))!.AsObject();
    // Reads without activating, so checking a chat never starts or reconnects its agent.
    public async Task<JsonObject> Chat(string name)
    {
        var id = name.Length == 32 && name.All(char.IsAsciiHexDigit) ? name : Resolve.Chat(await List(), name)["id"]!.GetValue<string>();
        return (await Call(new() { ["method"] = "chat", ["chatId"] = id, ["activate"] = false }))!.AsObject();
    }
    public ValueTask DisposeAsync() { remote?.Dispose(); return ValueTask.CompletedTask; }
}

internal static class HostStore
{
    public static string Directory => Path.Combine(CliPipe.DataDirectory, "cli");
    private static string File => Path.Combine(Directory, "hosts.json");
    public static List<RemoteHost> Load()
    {
        if (!System.IO.File.Exists(File)) return [];
        return JsonNode.Parse(System.IO.File.ReadAllText(File))!.AsArray().OfType<JsonObject>()
            .Select(n => new RemoteHost(n["name"]!.GetValue<string>(), n["address"]!.GetValue<string>(), n["port"]!.GetValue<int>(), n["key"]!.GetValue<string>(), n["fingerprint"]!.GetValue<string>())).ToList();
    }
    public static void Save(List<RemoteHost> hosts)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var json = new JsonArray(hosts.Select(h => (JsonNode)new JsonObject { ["name"] = h.Name, ["address"] = h.Address, ["port"] = h.Port, ["key"] = h.KeyPath, ["fingerprint"] = h.Fingerprint }).ToArray());
        RemoteKey.WritePrivate(File, Encoding.UTF8.GetBytes(json.ToJsonString()));
    }
    public static RemoteHost Find(List<RemoteHost> hosts, string name) =>
        hosts.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? hosts.FirstOrDefault(h => h.Address.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new IOException($"No paired computer named \"{name}\". See: vh hosts");
}
