using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace CodexManager;

// Desktop remote chats. Each paired computer's workspaces and chats become ordinary Workspace and
// Chat objects (scoped ids, Remote set) that the same sidebar and chat pane show as local ones;
// only their backend differs (RemoteHostClient and RemoteChatSession). Remote-only clients
// (mobile and browser) keep using RemoteView.
public partial class MainView
{
    private readonly Dictionary<RemoteHost, RemoteHostClient> remoteHosts = [];
    private readonly Dictionary<string, Workspace> remoteWorkspaces = [];
    private readonly Dictionary<string, Chat> remoteChats = [];
    private readonly Dictionary<RemoteHost, string[]> remoteWorkspaceOrder = [];
    private readonly Dictionary<string, Chat[]> remoteChatOrder = [];
    private readonly Dictionary<RemoteHost, string> remoteStructure = [];
    private readonly Dictionary<RemoteHost, TextBlock> remoteStatusTexts = [];
    // A selection requested before the host's catalog listed it (a new chat, a notification).
    private readonly Dictionary<RemoteHost, (string? Workspace, string? Chat)> remoteRequests = [];
    private readonly Dictionary<string, TabItem> remoteTerminalTabs = [];

    private RemoteHostClient RemoteHostFor(RemoteHost host)
    {
        if (remoteHosts.TryGetValue(host, out var client)) return client;
        client = new RemoteHostClient(host, () => store.Setting("allowAllPermissions") == "1", id => { ShowFromTray(); OpenRemoteChat(host, id); });
        remoteHosts[host] = client;
        client.Collapsed = store.Setting(RemoteCollapsedKey(host)) == "1";
        client.CatalogChanged += () => { if (!closing) ApplyRemoteCatalog(client); };
        client.StatusChanged += () =>
        {
            if (closing) return;
            UpdateRemoteStatus(client);
            if (current?.Remote == host) UpdateControls();
        };
        client.Start();
        return client;
    }
    private void UpdateRemoteStatus(RemoteHostClient client)
    {
        if (!remoteStatusTexts.TryGetValue(client.Host, out var text)) return;
        text.Text = client.Status; text.IsVisible = !client.Connected || client.Catalog is null;
    }
    private IEnumerable<Chat> ChatsFor(string workspaceId) =>
        remoteWorkspaces.ContainsKey(workspaceId) ? remoteChatOrder.GetValueOrDefault(workspaceId) ?? [] : chats.Where(c => c.WorkspaceId == workspaceId);
    private Workspace? FindWorkspace(string id) => workspaces.FirstOrDefault(w => w.Id == id) ?? remoteWorkspaces.GetValueOrDefault(id);

    private void ApplyRemoteCatalog(RemoteHostClient client)
    {
        if (client.Catalog is not { } catalog) return;
        var host = client.Host;
        var order = new List<string>(); var structure = new System.Text.StringBuilder();
        foreach (var node in catalog["workspaces"]!.AsArray().OfType<JsonNode>())
        {
            var id = node["id"]!.GetValue<string>(); var scoped = client.Scoped(id);
            var updated = new Workspace(scoped, node["name"]!.GetValue<string>(), node["path"]?.GetValue<string>() ?? "", node["distro"]?.GetValue<string>()) { Remote = host, RemoteId = id };
            if (!remoteWorkspaces.TryGetValue(scoped, out var existing) || existing != updated)
            {
                remoteWorkspaces[scoped] = updated;
                if (workspace?.Id == scoped) { workspace = updated; WorkspaceHeading.Text = $"{updated.Host}  /  {updated.Path}"; }
            }
            order.Add(scoped); structure.Append(scoped).Append('\0').Append(updated.Name).Append('\0').Append(updated.Distro).Append('\n');
        }
        remoteWorkspaceOrder[host] = order.ToArray();
        var seen = new HashSet<string>(); var byWorkspace = new Dictionary<string, List<Chat>>();
        foreach (var record in catalog["chats"]!.AsArray().OfType<JsonNode>())
        {
            var id = record["id"]!.GetValue<string>(); var scoped = client.Scoped(id);
            if (!remoteChats.TryGetValue(scoped, out var chat))
            {
                if (!Enum.TryParse<AgentProvider>(record["provider"]?.GetValue<string>(), out var provider)) continue;
                remoteChats[scoped] = chat = new Chat
                {
                    Id = scoped, WorkspaceId = client.Scoped(record["workspaceId"]!.GetValue<string>()), Provider = provider,
                    Remote = host, RemoteId = id, Draft = store.Setting("remoteDraft:" + scoped) ?? ""
                };
            }
            var wasUnread = chat.HasUnreadCompletion; var wasBusy = chat.Busy;
            RemoteChatSession.ApplySummary(chat, record);
            if (chat.HasUnreadCompletion && !chat.Busy) chat.Status = "Done";
            if (wasBusy && chat.HasUnreadCompletion && !wasUnread && !chat.Busy) NotifyCompleted(chat, background: false);
            seen.Add(scoped);
            if (!byWorkspace.TryGetValue(chat.WorkspaceId, out var list)) byWorkspace[chat.WorkspaceId] = list = [];
            list.Add(chat);
        }
        foreach (var scoped in order) remoteChatOrder[scoped] = byWorkspace.GetValueOrDefault(scoped)?.ToArray() ?? [];
        foreach (var stale in remoteChats.Where(p => p.Value.Remote == host && !seen.Contains(p.Key)).Select(p => p.Value).ToArray())
        {
            remoteChats.Remove(stale.Id);
            if (runtimes.Remove(stale.Id, out var session)) _ = session.DisposeAsync();
            if (ReferenceEquals(current, stale)) ClearChat();
        }
        foreach (var stale in remoteWorkspaces.Keys.Where(k => k.StartsWith(client.Scope, StringComparison.Ordinal) && !order.Contains(k)).ToArray())
        {
            remoteWorkspaces.Remove(stale); remoteChatOrder.Remove(stale);
            if (workspace?.Id == stale) { ClearChat(); workspace = null; }
        }
        var signature = structure.ToString();
        if (remoteStructure.GetValueOrDefault(host) != signature) { remoteStructure[host] = signature; BuildWorkspaceTree(); }
        else RefreshChats();
        UpdateRemoteStatus(client);
        if (remoteRequests.Remove(host, out var request))
        {
            if (request.Chat is { } chatId) OpenRemoteChat(host, chatId);
            else if (request.Workspace is { } workspaceId) OpenRemoteHost(host, workspaceId);
        }
        if (current?.Remote == host) UpdateControls();
    }
    // Desktop: shows the host's workspaces in the sidebar and optionally selects one.
    private void OpenRemoteDesktop(RemoteHost host, string? workspaceId)
    {
        var client = RemoteHostFor(host);
        if (client.Collapsed) { store.Setting(RemoteCollapsedKey(host), "0"); client.Collapsed = false; BuildWorkspaceTree(); }
        if (workspaceId is null) { _ = client.RefreshCatalog(); return; }
        if (remoteWorkspaces.TryGetValue(client.Scoped(workspaceId), out var owner)) { SelectWorkspace(owner); CollapseSidebar(); }
        else { remoteRequests[host] = (workspaceId, null); _ = client.RefreshCatalog(); }
    }
    private void OpenRemoteChat(RemoteHost host, string remoteChatId)
    {
        if (remoteOnly) { OpenRemoteHost(host); remoteView?.SelectChat(remoteChatId); return; }
        var client = RemoteHostFor(host);
        if (remoteChats.TryGetValue(client.Scoped(remoteChatId), out var chat) && remoteWorkspaces.TryGetValue(chat.WorkspaceId, out var owner))
        {
            if (showArchived && !chat.Archived) ShowArchiveView(false);
            SearchBox.Text = "";
            if (workspace?.Id != owner.Id) SelectWorkspace(owner);
            RefreshChats(); ChatList.SelectedItem = chat;
        }
        else { remoteRequests[host] = (null, remoteChatId); _ = client.RefreshCatalog(); }
    }
    private RemoteChatSession RemoteSession(Chat chat, Workspace owner)
    {
        if (runtimes.GetValueOrDefault(chat.Id) is RemoteChatSession existing) return existing;
        var session = new RemoteChatSession(RemoteHostFor(owner.Remote!), chat)
        {
            Permission = (request, token) => Permission(chat, request, token, remote: true),
            Elicitation = (request, token) => Elicit(chat, request, token, remote: true)
        };
        session.Changed += () => { if (!closing) ReflectSessionChange(chat, session); };
        runtimes[chat.Id] = session;
        return session;
    }
    private RemoteChatSession? RemoteSessionOf(Chat? chat) =>
        chat is { IsRemote: true } && remoteWorkspaces.TryGetValue(chat.WorkspaceId, out var owner) ? RemoteSession(chat, owner) : null;
    private static string Unscope(RemoteHostClient client, string id) => id.StartsWith(client.Scope, StringComparison.Ordinal) ? id[client.Scope.Length..] : id;

    // Calls the host and refreshes the catalog; a failure is shown in the status bar.
    private async Task<JsonNode?> RemoteAction(RemoteHost host, JsonObject request, string failure)
    {
        var client = RemoteHostFor(host);
        var result = await client.Call(request);
        if (result is null) { StatusText.Text = client.LastError ?? failure; return null; }
        await client.RefreshCatalog();
        return result;
    }
    private async Task NewRemoteChat(Workspace owner, AgentProvider provider)
    {
        var client = RemoteHostFor(owner.Remote!);
        if (!client.Providers.Contains(provider))
        {
            if (client.Providers.Count == 0) { StatusText.Text = $"Enable a provider in Settings > Agents on {owner.Remote!.Name} to start a chat."; return; }
            provider = client.Providers[0];
        }
        if (showArchived) ShowArchiveView(false);
        var result = await client.Call(new() { ["method"] = "create", ["workspaceId"] = owner.RemoteId, ["provider"] = provider.ToString() });
        if (result is null) { StatusText.Text = client.LastError ?? "Could not create the chat on " + owner.Remote!.Name + "."; return; }
        remoteRequests[owner.Remote!] = (null, result["id"]!.GetValue<string>());
        SearchBox.Text = "";
        await client.RefreshCatalog();
    }
    private async Task RemoteArchiveChat(Chat chat)
    {
        if (ReferenceEquals(current, chat)) pageLoad?.Cancel();
        var archive = !chat.Archived;
        if (await RemoteAction(chat.Remote!, new() { ["method"] = "archive", ["chatId"] = chat.RemoteId, ["archived"] = archive }, "Could not archive the chat.") is null) return;
        chat.Archived = archive;
        if (archive && runtimes.Remove(chat.Id, out var session)) await session.DisposeAsync();
        if (ReferenceEquals(current, chat)) SelectNextChat(); else { RefreshChats(); UpdateControls(); }
    }
    private async Task<string?> RemoteDeleteChat(Chat chat)
    {
        if (chat.IsDeleting || chat.Busy) throw new IOException("Stop the chat before deleting it.");
        if (ReferenceEquals(current, chat)) { pageLoad?.Cancel(); ClearChat(); }
        chat.IsDeleting = true;
        try
        {
            var result = await RemoteHostFor(chat.Remote!).Call(new() { ["method"] = "delete", ["chatId"] = chat.RemoteId })
                ?? throw new IOException(RemoteHostFor(chat.Remote!).LastError ?? "Could not delete the chat. Reconnect and try again.");
            if (result["deleted"]?.GetValue<bool>() != true) throw new IOException("The host did not confirm deletion.");
            if (runtimes.Remove(chat.Id, out var session)) await session.DisposeAsync();
            store.Setting("remoteDraft:" + chat.Id, "");
            await RemoteHostFor(chat.Remote!).RefreshCatalog();
            return result["warning"]?.GetValue<string>();
        }
        finally { chat.IsDeleting = false; RefreshChats(); UpdateControls(); }
    }
    private async Task RemoteCloseWorkspace(Workspace owner)
    {
        if (await RemoteAction(owner.Remote!, new() { ["method"] = "workspace/close", ["workspaceId"] = owner.RemoteId }, "Could not close the workspace.") is null) return;
        if (workspace?.Id == owner.Id)
        {
            ClearChat(); workspace = null;
            if (workspaces.FirstOrDefault() is { } next) SelectWorkspace(next);
            else { WorkspaceHeading.Text = "No workspace selected"; UpdateControls(); }
        }
    }
    private async Task RemoteImport(Workspace owner)
    {
        var client = RemoteHostFor(owner.Remote!);
        var providers = await ChooseImportProviders(owner, client.Providers.Select(AgentProviders.Get));
        if (providers is null || closing) return;
        var reports = new List<string>();
        foreach (var provider in providers)
        {
            var result = await client.Call(new() { ["method"] = "import", ["workspaceId"] = owner.RemoteId, ["provider"] = provider.ToString() });
            reports.Add(result is null ? $"Could not import {AgentProviders.Get(provider).Name} chats: " + (client.LastError ?? "the host did not respond.")
                : $"{AgentProviders.Get(provider).Name}: imported {result.GetValue<int>()} previous chats.");
        }
        await client.RefreshCatalog();
        if (!closing) { StatusText.Text = string.Join(" · ", reports); ToolTip.SetTip(StatusText, StatusText.Text); }
    }
    private void RemoteReorder(Workspace owner, string scope, string source, string target, bool after)
    {
        var client = RemoteHostFor(owner.Remote!);
        _ = RemoteAction(owner.Remote!, new() { ["method"] = "reorder", ["scope"] = scope, ["source"] = Unscope(client, source), ["target"] = Unscope(client, target), ["after"] = after }, "Could not reorder.");
    }
    private async Task OpenRemoteFileLink(RemoteChatSession session, Chat chat, string target)
    {
        var transfer = remoteDownloads.Begin(target);
        try
        {
            var path = await FileLinks.Download(session.Host.Call, chat.RemoteId!, target, discoveryLifetime.Token, (received, total) => remoteDownloads.Report(transfer.Id, received, total));
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            var file = await top.StorageProvider.TryGetFileFromPathAsync(path);
            if (file is null || !await top.Launcher.LaunchFileAsync(file)) throw new IOException("No installed app can open this file.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { StatusText.Text = "Could not open " + target + ": " + error.Message; }
        finally { remoteDownloads.Finish(transfer.Id); }
    }
    // The terminal drawer hosts one shell per remote workspace, running on the host.
    private async Task<TabItem?> OpenRemoteTerminal(Workspace owner)
    {
        if (!remoteTerminalTabs.TryGetValue(owner.Id, out var tab))
        {
            var view = new RemoteTerminalView(RemoteHostFor(owner.Remote!).Call, mobile: false);
            view.Back += () => TerminalDrawer.IsVisible = false;
            tab = new TabItem { Content = view, MinHeight = 26, Padding = new(6, 2), Header = new TextBlock { FontSize = 11, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, Text = owner.Host + " · " + owner.Name, VerticalAlignment = VerticalAlignment.Center } };
            remoteTerminalTabs[owner.Id] = tab;
        }
        var terminal = (RemoteTerminalView)tab.Content!;
        if (workspace?.Id == owner.Id)
        {
            terminalPaneWorkspace = null; SwitchTerminalWorkspace(owner.Id);
            TerminalTabs.SelectedItem = tab; TerminalDrawer.IsVisible = true;
        }
        terminal.SetVisible(true);
        try { if (terminal.TerminalId is null) await terminal.Open(owner.RemoteId!, owner.Host + " · " + owner.Name); }
        catch (Exception error) { StatusText.Text = "Could not open the remote terminal: " + error.Message; }
        return tab;
    }
    private void DisposeRemoteHosts()
    {
        foreach (var tab in remoteTerminalTabs.Values) (tab.Content as RemoteTerminalView)?.Dispose();
        remoteTerminalTabs.Clear();
        foreach (var client in remoteHosts.Values) client.Dispose();
        remoteHosts.Clear(); remoteWorkspaces.Clear(); remoteChats.Clear(); remoteChatOrder.Clear(); remoteWorkspaceOrder.Clear(); remoteStructure.Clear();
    }
    private void ForgetRemoteHost(RemoteHost host)
    {
        if (remoteHosts.Remove(host, out var client))
        {
            foreach (var chat in remoteChats.Values.Where(c => c.Remote == host).ToArray())
            {
                remoteChats.Remove(chat.Id);
                if (runtimes.Remove(chat.Id, out var session)) _ = session.DisposeAsync();
                if (ReferenceEquals(current, chat)) ClearChat();
            }
            foreach (var key in remoteWorkspaces.Keys.Where(k => k.StartsWith(client.Scope, StringComparison.Ordinal)).ToArray())
            {
                remoteWorkspaces.Remove(key); remoteChatOrder.Remove(key);
                if (remoteTerminalTabs.Remove(key, out var tab)) (tab.Content as RemoteTerminalView)?.Dispose();
                if (workspace?.Id == key) workspace = null;
            }
            client.Dispose();
        }
        remoteWorkspaceOrder.Remove(host); remoteStructure.Remove(host); remoteStatusTexts.Remove(host); remoteRequests.Remove(host);
    }
}
