using System.Text.Json.Nodes;
using System.Text.Json;
using Avalonia.Threading;

namespace CodexManager;

public sealed partial class ChatRuntime(Chat chat, Workspace workspace, Store store, string command) : IAsyncDisposable
{
    private AcpClient? client;
    public BackendUpdates? BackendMaintenance { get; set; }
    public Func<string>? ResolveCommand { get; set; }
    public bool HasBackendProcess => client?.Alive == true;
    private HashSet<string>? authenticatedProviders;
    private string vtOpenAiMethod = "chatgpt";
    private bool vtOpenAiPinned;
    private bool restoreDiracContext;
    private bool loading;
    private bool replaying;
    private bool connected;
    private Message? activePlan;
    private readonly Dictionary<string, string> activeToolInputs = [];
    private bool detachedTurn;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? turn;
    private Task? activeTask;
    private Task? reconnectTask;
    private Task? recoveryTask;
    private CancellationTokenSource? recoveryCancellation;
    private bool lastTurnRecoverable;
    private bool advancingQueue;
    public bool IsRecovering { get; private set; }
    public event Action? AuthenticationSucceeded;
    private Task<bool>? steeringTask;
    private bool reconnecting;
    private int rapidDisconnects;
    private DateTimeOffset connectedAt;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);
    public Func<bool>? IsActiveView { get; set; }
    private DateTimeOffset? idleSince;
    private DateTimeOffset remoteViewUntil;
    private DispatcherTimer? idleTimer;
    private Task? idleShutdown;
    private int pendingPermissions;
    private int pendingQuestions;
    public void KeepAlive() { remoteViewUntil = DateTimeOffset.UtcNow.AddSeconds(15); idleSince = null; }
    public async Task ReleaseIfIdle(DateTimeOffset now)
    {
        if (client is null || lifetime.IsCancellationRequested) { idleTimer?.Stop(); idleSince = null; return; }
        if (chat.Busy || IsChangingHistory || chat.NeedsPermission || loading || reconnecting || IsLoadingHistory || IsConfiguring || IsRecovering || IsSteering || advancingQueue || IsActiveView?.Invoke() == true || now < remoteViewUntil)
        { idleSince = null; return; }
        idleSince ??= now;
        if (now - idleSince < IdleTimeout) return;
        // Detach before disposal so the intentional exit cannot trigger auto-reconnect.
        var previous = client; client = null; connected = false; idleTimer?.Stop(); idleSince = null;
        await previous.DisposeAsync();
        Changed?.Invoke();
    }
    private void StartIdleTimer()
    {
        if (idleTimer is null)
        {
            idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            idleTimer.Tick += async (_, _) =>
            {
                if (idleShutdown is { IsCompleted: false }) return;
                try { await (idleShutdown = ReleaseIfIdle(DateTimeOffset.UtcNow)); }
                catch (Exception error) { AppDiagnostics.Record("Stop idle agent", error); }
            };
        }
        idleSince = null; idleTimer.Start();
    }
    public Func<JsonElement, CancellationToken, Task<JsonObject>>? Permission { get; set; }
    public Func<JsonElement, CancellationToken, Task<JsonObject>>? Elicitation { get; set; }
    private void UpdatePendingInput()
    {
        chat.NeedsPermission = pendingPermissions + pendingQuestions > 0;
        chat.Status = pendingQuestions > 0 ? "Needs input" : pendingPermissions > 0 ? "Needs permission" : chat.Busy ? "Working…" : "Ready";
        Changed?.Invoke();
    }
    public event Action? Changed;
    public bool IsLoadingHistory { get; private set; }
    public bool IsReconnecting => reconnecting;
    public bool IsPrompting => (turn is not null || detachedTurn) && chat.Busy;
    public bool IsConfiguring { get; private set; }
    public bool IsPreparing => IsChangingHistory || IsRecovering || chat.Busy && (!IsConnected || loading || reconnecting || IsLoadingHistory || !IsPrompting);
    public bool IsConnected => connected && client?.Alive == true;
    public bool SupportsSteering { get; private set; }
    public bool IsSteering { get; private set; }
    public void Queue(PendingInput input) { chat.QueuedInputs.Add(input); store.Save(chat); Changed?.Invoke(); }
    public void RemoveQueued(PendingInput input) { chat.QueuedInputs.Remove(input); store.Save(chat); Changed?.Invoke(); }
    public void EditQueued(PendingInput input, string text)
    {
        var index = chat.QueuedInputs.IndexOf(input);
        if (index < 0) throw new IOException("This message is no longer queued.");
        if (string.IsNullOrWhiteSpace(text) && input.Attachments.Length == 0) throw new IOException("Enter a message.");
        chat.QueuedInputs[index] = input with { Text = text }; store.Save(chat); Changed?.Invoke();
    }
    public Task<bool> Steer(PendingInput input) => IsSteering ? Task.FromResult(false) : steeringTask = SteerCore(input);
    private async Task<bool> SteerCore(PendingInput input)
    {
        if (IsChangingHistory || !SupportsSteering || !IsPrompting || IsSteering || client is null) return false;
        IsSteering = true; store.Setting("steering:" + chat.Id, JsonSerializer.Serialize(input, StoreJsonContext.Default.PendingInput)); Changed?.Invoke();
        try
        {
            if (SupportsDiracWhisper)
            {
                if (input.Attachments.Length > 0 || string.IsNullOrWhiteSpace(input.Text)) return false;
                var accepted = whisperAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await client.Notify("_dev.dirac/whisper", RpcJson.Object(("sessionId", chat.SessionId), ("text", input.Text)));
                await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
                var whispered = new Message { Role = "user", Provider = chat.Provider, Text = input.Text };
                chat.Messages.Add(whispered); store.SaveMessage(chat, whispered);
                return true;
            }
            var content = new JsonArray();
            if (!string.IsNullOrWhiteSpace(input.Text)) content.Add((JsonNode)RpcJson.Object(("type", "text"), ("text", input.Text)));
            foreach (var a in input.Attachments) content.Add((JsonNode)a.ToContent());
            var result = await client.Request("_session/steering", RpcJson.Object(("sessionId", chat.SessionId), ("prompt", content),
                ("_meta", RpcJson.Object(("steering", RpcJson.Object(("idleBehavior", "promptRequired")))))), lifetime.Token);
            var outcome = result.GetProperty("outcome").GetString();
            if (outcome == "promptRequired") return false;
            if (outcome is not "injected" and not "startedNewTurn") throw new IOException("Provider did not accept steering: " + outcome);
            if (outcome == "startedNewTurn")
            {
                // Older adapters can win the idle race and start an unowned
                // prompt. They do not report its completion. Keep Stop available
                // and pause the queue instead of pretending that turn is idle.
                detachedTurn = true; chat.Busy = true;
                chat.Status = "Steered turn running — this adapter does not report its completion; Stop before sending another turn";
                store.Setting("interrupted:" + chat.Id, JsonSerializer.Serialize(input, StoreJsonContext.Default.PendingInput));
            }
            var message = new Message { Role = "user", Provider = chat.Provider, Text = input.Text };
            foreach (var a in input.Attachments) message.Attachments.Add(a);
            chat.Messages.Add(message); store.SaveMessage(chat, message);
            return true;
        }
        catch (Exception error) { chat.Status = "Could not steer: " + error.Message; return false; }
        finally { whisperAccepted = null; store.Setting("steering:" + chat.Id, ""); IsSteering = false; Changed?.Invoke(); }
    }
    public async Task SendQueuedNow(PendingInput input, bool waitForCompletion = true)
    {
        if (!chat.QueuedInputs.Contains(input)) return;
        if (IsPrompting) await Stop();
        if (chat.Busy || lifetime.IsCancellationRequested) return;
        RemoveQueued(input); var sending = Send(input.Text, input.Attachments); if (waitForCompletion) await sending;
    }
    public async Task AdvanceQueued(bool interrupt = false)
    {
        if (IsChangingHistory || advancingQueue || IsRecovering || IsReconnecting || IsConfiguring || installation is not null || chat.NeedsLogin || chat.QueuedInputs.Count == 0) return;
        advancingQueue = true;
        try
        {
            var queued = chat.QueuedInputs.ToArray();
            var input = new PendingInput(string.Join("\n\n", queued.Select(q => q.Text).Where(t => !string.IsNullOrWhiteSpace(t))), queued.SelectMany(q => q.Attachments).ToArray());
            if (IsPrompting && !interrupt && SupportsSteering)
            {
                if (await Steer(input)) foreach (var item in queued) RemoveQueued(item);
                // An uncertain steering failure must not duplicate an accepted input.
                return;
            }
            if (IsPrompting) await Stop();
            if (chat.Busy || lifetime.IsCancellationRequested || queued.Any(item => !chat.QueuedInputs.Contains(item))) return;
            foreach (var item in queued) RemoveQueued(item);
            _ = Send(input.Text, input.Attachments);
        }
        finally { advancingQueue = false; }
    }
    private void Configure(JsonElement response)
    {
        if (!response.TryGetProperty("configOptions", out _) && !response.TryGetProperty("models", out _) && !response.TryGetProperty("modes", out _)) return;
        chat.ConfigOptions = ProviderAuthentication.Filter(SessionConfig.Read(response), authenticatedProviders);
        if (chat.Provider == AgentProvider.VTCode && chat.ConfigOptions.Any(c => c.Id == "provider" && c.Current == "openai"))
            chat.ConfigOptions = chat.ConfigOptions.Append(VtCodeLaunch.AuthenticationBadge(vtOpenAiMethod, vtOpenAiPinned)).ToArray();
        chat.ConfigVersion++; Changed?.Invoke();
    }
    public async Task SetConfig(SessionConfig config, string value)
    {
        if (IsChangingHistory || loading || reconnecting || IsConfiguring || client is null || chat.SessionId is null) return;
        await SetConfigCore(config, value, remember: true);
        await RestoreConfiguration();
    }
    private async Task RestoreConfiguration()
    {
        await ApplySessionDefaults();
        await RestoreAccess();
    }
    private static SessionValue? FullAccess(SessionConfig option) => option.Values.FirstOrDefault(v => v.Name.Equals("Full access", StringComparison.OrdinalIgnoreCase) || v.Name.Equals("Bypass permissions", StringComparison.OrdinalIgnoreCase));
    private string AccessKey(SessionConfig option) => "sessionAccess:" + chat.Id + ":" + option.Id;
    private async Task RestoreAccess()
    {
        if (IsConfiguring || client is null || chat.SessionId is null) return;
        foreach (var option in chat.ConfigOptions.ToArray())
        {
            if (FullAccess(option) is not { } full) continue;
            var value = PreferredConfig(option) ?? full.Value;
            if (option.Current != value && option.Values.Any(v => v.Value == value)) await SetConfigCore(option, value);
            if (chat.ConfigOptions.FirstOrDefault(c => c.Id == option.Id)?.Current == value)
            {
                store.Setting(ChatConfigKey(option.Id), value);
                store.Setting(AccessKey(option), value);
            }
        }
    }
    private async Task SetConfigCore(SessionConfig config, string value, bool remember = false)
    {
        if (config.Id == VtCodeLaunch.AuthenticationOption || !config.Values.Any(v => v.Value == value)) return;
        IsConfiguring = true; Changed?.Invoke();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var parameters = RpcJson.Object(("sessionId", chat.SessionId));
            var method = "session/set_config_option";
            if (config.Kind is "model" or "mode") { method = "session/set_" + config.Kind; parameters[config.Kind + "Id"] = value; }
            else { parameters["configId"] = config.Id; parameters["value"] = config.Kind == "boolean" ? JsonValue.Create(value == "true") : JsonValue.Create(value); if (config.Kind == "boolean") parameters["type"] = "boolean"; }
            var result = await client!.Request(method, parameters, timeout.Token);
            chat.ConfigOptions = chat.ConfigOptions.Select(c => c.Id == config.Id ? c with { Current = value } : c).ToArray(); chat.ConfigVersion++;
            Configure(result);
            if (chat.ConfigOptions.FirstOrDefault(c => c.Id == config.Id)?.Current != value)
                throw new IOException("The agent did not accept the selected value.");
            if (remember)
            {
                store.Setting(ChatConfigKey(config.Id), value);
                store.Setting(WorkspaceConfigKey(config.Id), value);
                store.Setting(GlobalConfigKey(config.Id), value);
            }
            if (FullAccess(config) is not null) store.Setting(AccessKey(config), value);
            if (ModelPicker.IsModel(config)) ModelPicker.Remember(store, chat.Provider, value);
        }
        catch (Exception error) { chat.NeedsLogin |= AgentProviders.IsAuthenticationError(error); chat.Status = "Could not change " + config.Name + ": " + error.Message; chat.ConfigVersion++; }
        finally { IsConfiguring = false; Changed?.Invoke(); }
    }
    private async Task ConnectWithRecovery(bool replayHistory, CancellationToken token)
    {
        var previous = chat.Messages.ToArray();
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { await Connect(replayHistory); return; }
            catch (Exception error) when (attempt < 5 && error is not OperationCanceledException && !IsMissingCodexRollout(error) && !AgentProviders.IsAuthenticationError(error) && installation is null && !chat.NeedsLogin && !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
            {
                if (replayHistory) { chat.Messages.Clear(); foreach (var message in previous) chat.Messages.Add(message); }
                chat.Status = $"Reconnecting ({attempt}/4)…"; Changed?.Invoke();
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)), token);
            }
        }
    }
    private bool IsMissingCodexRollout(Exception error) => chat.Provider == AgentProvider.Codex && error is AcpException acp &&
        acp.Message.Contains("no rollout", StringComparison.OrdinalIgnoreCase);
    public Task Reconnect(bool automatic = false)
    {
        if (IsChangingHistory) return Task.CompletedTask;
        if (reconnecting) return reconnectTask ?? Task.CompletedTask;
        if (!automatic || DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromSeconds(30)) rapidDisconnects = 0;
        if (automatic && ++rapidDisconnects > 4)
        {
            chat.Status = "Agent keeps exiting — check connection settings, then reconnect"; Changed?.Invoke(); return Task.CompletedTask;
        }
        return reconnectTask = ReconnectCore();
    }
    private async Task ReconnectCore()
    {
        detachedTurn = false;
        reconnecting = true; connected = false;
        try
        {
            turn?.Cancel();
            if (client is not null) await client.DisposeAsync();
            if (activeTask is not null) await activeTask;
            if (lifetime.IsCancellationRequested) return;
            if (chat.SessionId is not null && chat.Messages.Count == 0 &&
                (store.Setting("historyIncomplete:" + chat.Id) == "1" ||
                 (await store.ReadPageAsync(chat, limit: 1, token: lifetime.Token)).Length == 0))
            { await LoadHistory(); return; }
            chat.Busy = true; chat.Status = "Reconnecting…"; Changed?.Invoke();
            await ConnectWithRecovery(false, recoveryCancellation?.Token ?? lifetime.Token); chat.Status = "Ready";
        }
        catch (OperationCanceledException) { chat.Status = lifetime.IsCancellationRequested ? "Disconnected" : "Reconnect timed out or was cancelled — try reconnecting"; }
        catch (Exception error) { chat.NeedsLogin |= AgentProviders.IsAuthenticationError(error); chat.Status = "Reconnect failed: " + error.Message; ShowInstallation(); }
        finally
        {
            chat.Busy = false; reconnecting = false; Changed?.Invoke();
            SendQueuedWhenReady();
        }
    }
    private void SendQueuedWhenReady()
    {
        if (connected && !chat.NeedsLogin && !reconnecting && chat.Status == "Ready" && chat.QueuedInputs.FirstOrDefault() is { } next)
            Dispatcher.UIThread.Post(async () =>
            {
                if (!lifetime.IsCancellationRequested && connected && !reconnecting && !IsPreparing && !chat.Busy && chat.QueuedInputs.Contains(next))
                { RemoveQueued(next); await Send(next.Text, next.Attachments); }
            });
    }
    public async Task Connect(bool replayHistory = false)
    {
        installation = null;
        try { await ConnectCore(replayHistory); }
        catch (Exception error) when (AgentInstallation.FromError(chat.Provider, error) is { } missing)
        {
            installation = missing; ShowInstallation(); throw;
        }
    }
    private AgentInstallation? installation;
    private void ShowInstallation()
    {
        if (installation is not { } missing) return;
        chat.Status = missing.Message;
        var text = $"[{missing.Message}]({missing.Url})\n\nInstall it in {workspace.Host}, then reconnect this chat.";
        if (!chat.Messages.Any(m => m.Role == "system" && m.Text == text)) Add("system", text);
        Changed?.Invoke();
    }
    private async Task ConnectCore(bool replayHistory)
    {
        if (connected && client?.Alive == true) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, turn?.Token ?? lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        connected = false;
        if (client is not null) await client.DisposeAsync();
        chat.Commands = []; Changed?.Invoke();
        authenticatedProviders = await ProviderAuthentication.Read(workspace, chat.Provider);
        lifetime.Token.ThrowIfCancellationRequested();
        DisconnectSubagents();
        var files = new AcpFileSystem(workspace, () => chat.SessionId, subagentRoots.ContainsKey);
        var launchCommand = ResolveCommand?.Invoke() ?? command;
        if (chat.Provider == AgentProvider.VTCode)
        {
            vtOpenAiMethod = VtCodeLaunch.Method(store, workspace);
            (launchCommand, vtOpenAiPinned) = VtCodeLaunch.Prepare(launchCommand, authenticatedProviders, vtOpenAiMethod);
        }
        void StartAgent() => client = new(AgentProviders.Start(workspace, launchCommand, chat.Provider)) { ReadTextFile = files.Read, WriteTextFile = files.Write };
        if (BackendMaintenance is { } maintenance) await maintenance.BeforeStart(workspace, chat.Provider, StartAgent, timeout.Token);
        else StartAgent();
        if (client is null) throw new IOException("Could not start the agent.");
        StartIdleTimer();
        var connection = client;
        client.ExtensionNotification += (method, parameters) => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(client, connection) || lifetime.IsCancellationRequested) return;
            if (method == "_dev.dirac/steering_status" && parameters.TryGetProperty("sessionId", out var session) && session.GetString() == chat.SessionId &&
                parameters.TryGetProperty("status", out var status) && status.GetString() is "queued" or "sent") whisperAccepted?.TrySetResult();
        });
        client.AuthenticationChanged += needsLogin => Dispatcher.UIThread.Post(() => { if (!lifetime.IsCancellationRequested && ReferenceEquals(client, connection)) { chat.NeedsLogin = needsLogin; Changed?.Invoke(); } });
        client.Disconnected += () => Dispatcher.UIThread.Post(() =>
        {
            if (!connected || !ReferenceEquals(client, connection) || lifetime.IsCancellationRequested) return;
            connected = false;
            DisconnectSubagents();
            if (connection.DisconnectReason is { } disconnectReason)
                chat.NeedsLogin |= AgentProviders.IsAuthenticationError(new IOException(disconnectReason));
            if (!chat.Busy)
            {
                chat.Status = connection.DisconnectReason ?? "Agent disconnected.";
                Add("system", chat.Status);
                Changed?.Invoke();
            }
            if (!chat.Busy && !chat.NeedsLogin && (IsActiveView?.Invoke() == true || DateTimeOffset.UtcNow < remoteViewUntil)) _ = Reconnect(true);
        });
        client.SessionUpdateAsync = async (session, update) => await Dispatcher.UIThread.InvokeAsync(() => RouteUpdate(session, update), DispatcherPriority.Background);
        client.PermissionRequested = async (request, token) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, turn?.Token ?? lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (PermissionPolicy.AutoApprove(store, request) is { } approved) return approved;
            if (Permission is null) return RpcJson.Permission();
            await Dispatcher.UIThread.InvokeAsync(() => { pendingPermissions++; UpdatePendingInput(); });
            try { return await Dispatcher.UIThread.InvokeAsync(() => Permission(request, linked.Token).WaitAsync(linked.Token)); }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    pendingPermissions--; UpdatePendingInput();
                });
            }
        };
        client.ElicitationRequested = async (request, token) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, turn?.Token ?? lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (Elicitation is null) return ElicitationForm.Cancel();
            await Dispatcher.UIThread.InvokeAsync(() => { pendingQuestions++; UpdatePendingInput(); });
            try { return await Dispatcher.UIThread.InvokeAsync(() => Elicitation(request, linked.Token).WaitAsync(linked.Token)); }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { return ElicitationForm.Cancel(); }
            finally { await Dispatcher.UIThread.InvokeAsync(() => { pendingQuestions--; UpdatePendingInput(); }); }
        };
        var init = await client.Initialize(timeout.Token);
        ReadHistoryCapabilities(init);
        ReadExtensionCapabilities(init);
        SupportsSteering = SupportsDiracWhisper || init.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("steering", out var steering) && steering.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True;
        loading = chat.SessionId is not null;
        restoreDiracContext = chat.Provider == AgentProvider.Dirac && loading;
        replaying = loading && replayHistory;
        try
        {
            if (loading)
            {
                foreach (var option in chat.ConfigOptions)
                    if (FullAccess(option) is null && store.Setting(ChatConfigKey(option.Id)) is null) store.Setting(ChatConfigKey(option.Id), option.Current);
                if (!init.GetProperty("agentCapabilities").GetProperty("loadSession").GetBoolean()) throw new IOException("This adapter does not support session resume. Update its connection command.");
                try
                {
                    Configure(await client.Request("session/load", RpcJson.Object(("sessionId", chat.SessionId), ("cwd", workspace.Path), ("mcpServers", new JsonArray())), timeout.Token));
                    await NormalizeVtCodeModel();
                    await ApplySessionDefaults();
                }
                catch (AcpException error) when (IsMissingCodexRollout(error)
                    && store.Setting("unmaterialized:" + chat.Id) == chat.SessionId && chat.Messages.All(m => m.Role == "system"))
                {
                    // A new Codex session may exist only in the adapter process until
                    // its first prompt. Replace only sessions we know never received one.
                    loading = false; replaying = false;
                    await NewSession(timeout.Token);
                }
                catch (AcpException error) when (IsMissingCodexRollout(error) && !replayHistory && chat.Messages.Any(m => m.Role is "user" or "assistant" or "tool"))
                {
                    loading = false;
                    await NewSession(timeout.Token);
                    store.Setting("restoreContext:" + chat.Id, "1");
                    Add("system", "The Codex session was unavailable. A replacement session was started and will use this chat's saved transcript as context for the next message.");
                }
            }
            else
            {
                await NewSession(timeout.Token);
            }
            if (chat.Provider is AgentProvider.VTCode or AgentProvider.Dirac && authenticatedProviders is { Count: > 0 } &&
                chat.ConfigOptions.FirstOrDefault(c => c.Id == "provider") is { } providerOption && !authenticatedProviders.Contains(providerOption.Current) && providerOption.Values.FirstOrDefault() is { } available)
                await SetConfigCore(providerOption, available.Value);
            await RestoreAccess();
            if (chat.ConfigOptions.FirstOrDefault(c => c.Id == "provider") is { } selectedProvider && authenticatedProviders?.Contains(selectedProvider.Current) == true) chat.NeedsLogin = false;
            connected = true; connectedAt = DateTimeOffset.UtcNow; StartIdleTimer();
        }
        finally { loading = false; replaying = false; }
    }
    private async Task NewSession(CancellationToken token)
    {
        var session = await client!.Request("session/new", RpcJson.Object(("cwd", workspace.Path), ("mcpServers", new JsonArray())), token);
        chat.SessionId = session.GetProperty("sessionId").GetString();
        store.Setting("unmaterialized:" + chat.Id, chat.SessionId!); store.Save(chat); Configure(session);
        await NormalizeVtCodeModel();
        await ApplySessionDefaults();
    }

    private async Task NormalizeVtCodeModel()
    {
        // A CLI provider override can inherit another provider's workspace model.
        // Reapplying the route lets VT Code choose its own compatible default.
        if (chat.Provider == AgentProvider.VTCode && chat.ConfigOptions.FirstOrDefault(c => c.Id == "provider") is { } provider)
            await SetConfigCore(provider, provider.Current);
    }
    private string DefaultConfigKey(string id) => $"sessionDefault:{chat.Provider}:{workspace.Distro ?? "local"}:{id}";
    private string ChatConfigKey(string id) => $"chatConfig:{chat.Provider}:{chat.Id}:{id}";
    private string WorkspaceConfigKey(string id) => $"workspaceConfig:{chat.Provider}:{workspace.Id}:{id}";
    private string GlobalConfigKey(string id) => $"globalConfig:{chat.Provider}:{id}";
    private string? SavedConfig(string id) => store.Setting(ChatConfigKey(id)) ?? store.Setting(WorkspaceConfigKey(id))
        ?? store.Setting(GlobalConfigKey(id)) ?? store.Setting(DefaultConfigKey(id));
    private string? PreferredConfig(SessionConfig option) => FullAccess(option) is { } full
        ? store.Setting(AccessKey(option)) ?? SavedConfig(option.Id) ?? full.Value
        : SavedConfig(option.Id);
    private async Task ApplySessionDefaults()
    {
        // Mode/provider changes can replace the available model and reasoning options.
        HashSet<string> applied = [];
        while (chat.ConfigOptions.Where(c => !applied.Contains(c.Id)).OrderBy(c => c.Id == "mode" || c.Kind == "mode" ? 0 : c.Id == "provider" ? 1 : ModelPicker.IsModel(c) ? 2 : 3).FirstOrDefault() is { } option)
        {
            var id = option.Id; applied.Add(id);
            var value = PreferredConfig(option);
            // Older OpenCode installs kept model choices only in picker recents.
            // Migrate those only when no scoped (or legacy) preference exists.
            if (value is null && chat.Provider == AgentProvider.OpenCode && ModelPicker.IsModel(option))
                value = ModelPicker.Recent(store, chat.Provider).FirstOrDefault(recent => option.Values.Any(v => v.Value == recent));
            if (value is null && chat.Provider == AgentProvider.Dirac && id is "yolo" or "auto_approve") value = "true";
            if (id == "provider" && authenticatedProviders is { Count: > 0 } &&
                (value is null || !option.Values.Any(v => v.Value == value)) && !authenticatedProviders.Contains(option.Current)) value = option.Values.FirstOrDefault()?.Value;
            if (value is not null && option.Values.Any(v => v.Value == value))
            {
                if (FullAccess(option) is not null) store.Setting(AccessKey(option), value);
                if (value != option.Current) await SetConfigCore(option, value);
                // Once used, inherited settings belong to this chat even if
                // another chat later changes workspace or global defaults.
                if (chat.ConfigOptions.FirstOrDefault(c => c.Id == id)?.Current == value)
                    store.Setting(ChatConfigKey(id), value);
            }
            else if (store.Setting(ChatConfigKey(id)) is null)
                store.Setting(ChatConfigKey(id), option.Current);
        }
    }

    public Task LoadHistory() => chat.Busy ? activeTask ?? Task.CompletedTask : activeTask = LoadHistoryCore();
    private async Task LoadHistoryCore()
    {
        activePlan = null;
        chat.Busy = true; IsLoadingHistory = true; chat.Status = "Loading history…"; Changed?.Invoke();
        var previous = chat.Messages.ToArray();
        store.Setting("historyIncomplete:" + chat.Id, "1"); store.ClearHistory(chat);
        try
        {
            await ConnectWithRecovery(true, lifetime.Token);
            for (var i = 0; i < chat.Messages.Count; i++)
            {
                lifetime.Token.ThrowIfCancellationRequested(); store.SaveMessage(chat, chat.Messages[i]);
                if (i % 64 == 0) await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            store.Setting("historyIncomplete:" + chat.Id, "0"); await store.FlushAsync(); chat.HistoryLoaded = true; chat.Status = "Ready";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { chat.Status = "Ready"; }
        catch (Exception error)
        {
            chat.NeedsLogin |= AgentProviders.IsAuthenticationError(error);
            chat.Messages.Clear(); foreach (var message in previous) chat.Messages.Add(message);
            chat.Status = "History unavailable: " + error.Message;
            ShowInstallation();
        }
        finally { chat.Busy = false; IsLoadingHistory = false; Changed?.Invoke(); SendQueuedWhenReady(); }
    }
    public Task Send(string text, Attachment[] attachments, bool autoResume = false) => IsChangingHistory ? Task.FromException(new IOException("Wait for the history change to finish.")) : reconnecting ? reconnectTask ?? Task.CompletedTask : chat.Busy ? activeTask ?? Task.CompletedTask : activeTask = SendCore(text, attachments, autoResume);
    private async Task SendCore(string text, Attachment[] attachments, bool autoResume)
    {
        activePlan = null;
        if (chat.Busy) return;
        if (chat.Archived) throw new IOException("Unarchive this chat before using it.");
        lastTurnRecoverable = false;
        chat.HasUnreadCompletion = false; chat.Busy = true; chat.Status = "Connecting…"; Changed?.Invoke();
        turn = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        chat.PendingInput = new(text, attachments); store.Save(chat);
        store.Setting("interrupted:" + chat.Id, JsonSerializer.Serialize(chat.PendingInput, StoreJsonContext.Default.PendingInput));
        var recoverConnection = false;
        var completed = false;
        try
        {
            if (!chat.HistoryLoaded) store.ApplyRecentPage(chat, await store.ReadPageAsync(chat, limit: chat.RetainHistory ? Chat.HistoryPageSize : 1, token: turn.Token));
            await store.FlushAsync();
            await ConnectWithRecovery(chat.Messages.Count == 0, turn.Token);
            await RestoreAccess();
            turn.Token.ThrowIfCancellationRequested();
            if (chat.Provider == AgentProvider.VTCode)
                VtCodeLaunch.EnsureAuthentication(chat.ConfigOptions, vtOpenAiPinned);
            string? restoredContext = null;
            var restoreContext = restoreDiracContext || store.Setting("restoreContext:" + chat.Id) == "1";
            if (restoreContext)
            {
                foreach (var message in chat.Messages) store.SaveMessage(chat, message);
                restoredContext = (await store.ExportChatAsync(chat).WaitAsync(turn.Token)).Plain;
            }
            var continuation = string.IsNullOrWhiteSpace(text) && attachments.Length == 0;
            var user = new Message { Role = continuation ? "system" : "user", Provider = chat.Provider, Text = continuation ? autoResume ? "Chat auto-resumed after unexpected restart" : "Chat resumed" : text + string.Concat(attachments.Select(a => $"\n\n📎 {a.Name}")) };
            foreach (var a in attachments) user.Attachments.Add(a);
            chat.Messages.Add(user); store.SaveMessage(chat, user);
            chat.Status = "Working…"; Changed?.Invoke();
            var content = new JsonArray();
            if (!string.IsNullOrWhiteSpace(restoredContext)) content.Add((JsonNode)RpcJson.Object(("type", "text"), ("text", "Saved conversation from this same chat, restored after the agent reconnected. Treat this as prior conversation, not a new request. Continue with the new user message below.\n\n<saved_conversation>\n" + restoredContext + "</saved_conversation>")));
            if (!string.IsNullOrEmpty(text)) content.Add((JsonNode)RpcJson.Object(("type", "text"), ("text", text)));
            foreach (var attachment in attachments) content.Add((JsonNode)attachment.ToContent());
            store.Setting("unmaterialized:" + chat.Id, "");
            var result = await client!.Request("session/prompt", RpcJson.Object(("sessionId", chat.SessionId), ("prompt", content)), lifetime.Token);
            if (chat.Provider == AgentProvider.Pi && !turn.IsCancellationRequested &&
                (!result.TryGetProperty("stopReason", out var piStop) || piStop.GetString() != "cancelled") &&
                !chat.Messages.Any(m => m.Sequence > user.Sequence && m.Role is "assistant" or "tool" && !string.IsNullOrWhiteSpace(m.Text)))
                throw new IOException(await PiDiagnostics.ReadError(workspace, chat.SessionId!, user.Timestamp ?? DateTimeOffset.UtcNow)
                    ?? "Pi ended the turn without a response. The pi-acp adapter can suppress Pi errors. Check Pi's model and authentication in this workspace's environment; your message has been kept for retry.");
            restoreDiracContext = false;
            if (restoreContext) store.Setting("restoreContext:" + chat.Id, "");
            chat.Status = result.TryGetProperty("stopReason", out var reason) && reason.GetString() == "cancelled" ? "Interrupted" : "Ready";
            completed = chat.Status == "Ready" && !turn.IsCancellationRequested;
            if (completed) { chat.NeedsLogin = false; AuthenticationSucceeded?.Invoke(); chat.InterruptedInput = null; chat.HasUnreadCompletion = true; }

        }
        catch (OperationCanceledException)
        {
            chat.Status = "Interrupted";
            RestoreInput(text, attachments);
        }
        catch (Exception error)
        {
            chat.NeedsLogin |= AgentProviders.IsAuthenticationError(error);
            recoverConnection = (client?.Alive != true || IsNetworkFailure(error)) && !turn.IsCancellationRequested && !chat.NeedsLogin && installation is null;
            lastTurnRecoverable = recoverConnection;
            chat.InterruptedInput = new(text, attachments);
            chat.Status = "Connection error";
            ShowInstallation();
            var help = chat.Provider == AgentProvider.VTCode ? VtCodeLaunch.FailureHelp(error.Message) : null;
            if (!IsRecovering && installation is null) Add("system", recoverConnection ? "**Connection interrupted.** Waiting to reconnect and restore this session.\n\n" + error.Message : "**Could not complete the turn.**\n\n" + error.Message + "\n\n" + (help ?? $"Check the {AgentProviders.Get(chat.Provider).Name} adapter command and authentication in this workspace’s environment."));
            // Keep failed input available to retry, including attachments.
            if (!IsRecovering) RestoreInput(text, attachments);
        }
        finally
        {
            chat.Updated = DateTimeOffset.UtcNow;
            chat.PendingInput = null;
            var finishedTurn = turn; turn = null;
            try { finishedTurn?.Cancel(); } finally { finishedTurn?.Dispose(); chat.Busy = detachedTurn; }
            try
            {
                foreach (var message in chat.Messages) store.SaveMessage(chat, message);
                store.Save(chat);
                await store.FlushAsync();
                if (completed && !lifetime.IsCancellationRequested && !detachedTurn) { store.Setting("interrupted:" + chat.Id, ""); await store.FlushAsync(); }
                if (!chat.RetainHistory) store.ReleaseHistory(chat);
            }
            catch (Exception error) { completed = false; chat.Status = "Could not save completed turn: " + error.Message; }
            activeToolInputs.Clear(); Changed?.Invoke();
            if (recoverConnection && !lifetime.IsCancellationRequested && !IsRecovering)
                Dispatcher.UIThread.Post(() => { if (!lifetime.IsCancellationRequested && chat.InterruptedInput is { } input) recoveryTask = RecoverConnection(input); });
            if (completed && !IsSteering && !lifetime.IsCancellationRequested && chat.QueuedInputs.FirstOrDefault() is { } next)
                Dispatcher.UIThread.Post(async () => { if (!lifetime.IsCancellationRequested && !IsChangingHistory && !chat.Busy && chat.QueuedInputs.Contains(next)) { RemoveQueued(next); await Send(next.Text, next.Attachments); } });
        }
    }
    private void RestoreInput(string text, Attachment[] attachments)
    {
        chat.RecoverInput(new(text, attachments));
    }
    public static bool IsNetworkFailure(Exception error) => !AgentProviders.IsAuthenticationError(error) &&
        (error is System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException ||
        new[] { "connection reset", "connection refused", "connection closed", "network is unreachable", "network unreachable", "network error", "network request failed", "error sending request", "failed to fetch", "fetch failed", "stream disconnected", "stream closed", "dns", "name resolution", "econnreset", "econnrefused", "enotfound", "etimedout", "connection timed out" }
            .Any(value => error.Message.Contains(value, StringComparison.OrdinalIgnoreCase)) ||
        error.InnerException is { } inner && IsNetworkFailure(inner));
    private async Task RecoverConnection(PendingInput input)
    {
        IsRecovering = true; recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = recoveryCancellation.Token;
        try
        {
            var attempt = 0;
            while (!token.IsCancellationRequested)
            {
                chat.Status = $"Recovering connection (attempt {++attempt})…"; Changed?.Invoke();
                await Reconnect();
                if (token.IsCancellationRequested) return;
                if (installation is not null) { ShowInstallation(); return; }
                if (chat.NeedsLogin) { chat.Status = "Interrupted — sign in to resume"; return; }
                if (IsConnected)
                {
                    // Network recovery continues an already-authorized live Codex turn.
                    // The startup auto-resume preference is a separate decision.
                    if (chat.Provider != AgentProvider.Codex && store.Setting("autoResume") != "1") { chat.Status = "Interrupted — resume required"; return; }
                    if (chat.Draft == input.Text) chat.Draft = "";
                    foreach (var attachment in input.Attachments) chat.Attachments.Remove(attachment);
                    await Send(" ", [], autoResume: true);
                    if (chat.InterruptedInput is null || !lastTurnRecoverable || token.IsCancellationRequested) return;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 2)), token);
            }
        }
        catch (OperationCanceledException) { }
        finally { IsRecovering = false; recoveryCancellation.Dispose(); recoveryCancellation = null; if (!lifetime.IsCancellationRequested) { if (token.IsCancellationRequested) chat.Status = "Interrupted — automatic recovery stopped"; Changed?.Invoke(); } }
    }
    public async Task Stop()
    {
        recoveryCancellation?.Cancel(); chat.InterruptedInput = null;
        store.Setting("interrupted:" + chat.Id, "");
        if (IsRecovering && turn is null && client is not null) await client.DisposeAsync();
        if (detachedTurn)
        {
            detachedTurn = false;
            if (client?.Alive == true && chat.SessionId is not null) await client.Notify("session/cancel", RpcJson.Object(("sessionId", chat.SessionId)));
            connected = false;
            if (client is not null) await client.DisposeAsync();
            if (activeTask is not null) await activeTask;
            chat.Busy = false; chat.Status = "Interrupted"; Changed?.Invoke(); return;
        }
        // Reading a saved transcript is not an agent turn. Never cancel the
        // provider's session merely because its history is being replayed.
        if (turn is null) return;
        var activeTurn = turn;
        var promptTask = activeTask;
        turn?.Cancel();
        if (client?.Alive == true && chat.SessionId is not null)
        {
            await client.Notify("session/cancel", RpcJson.Object(("sessionId", chat.SessionId)));
            if (chat.Busy && ReferenceEquals(turn, activeTurn)) { chat.Status = "Interrupting…"; Changed?.Invoke(); }
            // A broken adapter must not leave a chat permanently busy.
            await Task.WhenAny(promptTask ?? Task.CompletedTask, Task.Delay(TimeSpan.FromSeconds(8), lifetime.Token));
            if (chat.Busy && ReferenceEquals(turn, activeTurn)) await client.DisposeAsync();
        }
        else if (client is not null) await client.DisposeAsync();
    }
    private void Add(string role, string text, string? toolId = null)
    {
        if (replaying && chat.Messages.LastOrDefault() is { } previous) store.SaveMessage(chat, previous);
        var m = new Message { Timestamp = replaying ? null : DateTimeOffset.UtcNow, Role = role, Provider = chat.Provider, Text = text, ToolId = toolId, Sequence = chat.NextSequence++ };
        chat.Messages.Add(m); store.TrimHistory(chat); if (!replaying) store.SaveMessage(chat, m);
    }
    private async Task Update(JsonElement update)
    {
        if (update.TryGetProperty("sessionUpdate", out var commandKind) && commandKind.GetString() == "available_commands_update")
        { chat.Commands = SlashCommand.Read(update); Changed?.Invoke(); return; }
        if (update.TryGetProperty("sessionUpdate", out var configKind) && configKind.GetString() == "config_option_update") { Configure(update); if (connected && !loading && !reconnecting && !IsConfiguring) _ = RestoreConfiguration(); return; }
        if ((loading && !replaying) || lifetime.IsCancellationRequested) return;
        var kind = update.GetProperty("sessionUpdate").GetString();
        if (kind is "agent_message_chunk" or "agent_thought_chunk" or "user_message_chunk")
        {
            var content = update.GetProperty("content");
            if (content.GetProperty("type").GetString() != "text") return;
            var role = kind == "agent_message_chunk" ? "assistant" : kind == "user_message_chunk" ? "user" : "thought";
            if (kind == "user_message_chunk") activePlan = null;
            var protocolId = update.TryGetProperty("messageId", out var messageId) && messageId.ValueKind == JsonValueKind.String ? messageId.GetString() : null;
            var last = chat.Messages.LastOrDefault();
            if (last?.Role != role || protocolId is not null && last.ProviderMessageId is not null && last.ProviderMessageId != protocolId) { Add(role, ""); last = chat.Messages.Last(); }
            if (protocolId is not null) last.ProviderMessageId = protocolId;
            last.Text += content.GetProperty("text").GetString();
        }
        else if (kind is "tool_call" or "tool_call_update")
        {
            var id = update.GetProperty("toolCallId").GetString();
            var message = chat.Messages.LastOrDefault(m => m.ToolId == id);
            if (message is null && id is not null) message = (await store.ReadPageAsync(chat, limit: 1, token: lifetime.Token, toolId: id)).FirstOrDefault();
            if (lifetime.IsCancellationRequested) return;
            if (message is null) { Add("tool", "", id); message = chat.Messages.Last(); }
            var previousOutput = ToolMessageContent.Split(message.Text).Output;
            message.Subagent = SubagentInfo.FromTool(update, message.Subagent);
            if (update.TryGetProperty("messageId", out var toolMessageId) && toolMessageId.ValueKind == JsonValueKind.String) message.ProviderMessageId = toolMessageId.GetString();
            if (id is not null && activeToolInputs.TryGetValue(id, out var previousInput)) message.ToolInput = previousInput;
            var title = update.TryGetProperty("title", out var t) ? t.GetString() : message.Text.Split('\n')[0];
            var status = update.TryGetProperty("status", out var s) ? s.GetString() : "running";
            if (update.TryGetProperty("rawInput", out var input) && input.ValueKind is not JsonValueKind.Null)
                message.ToolInput = "\n\n```\n" + (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("command", out var commandInput) && commandInput.ValueKind == JsonValueKind.String ? commandInput.GetString() : input.GetRawText()) + "\n```";
            if (id is not null && message.ToolInput.Length > 0) activeToolInputs[id] = message.ToolInput;
            var details = previousOutput;
            var hasContent = update.TryGetProperty("content", out var contents) && contents.ValueKind == JsonValueKind.Array;
            if (hasContent)
            {
                details = "";
                foreach (var item in contents.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var c) && c.TryGetProperty("text", out var value)) details += "\n\n" + value.GetString();
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "diff") details += "\n\n```diff\n" + (item.TryGetProperty("oldText", out var old) ? "- " + old.GetString() : "") + "\n+ " + item.GetProperty("newText").GetString() + "\n```";
                }
            }
            if (update.TryGetProperty("rawOutput", out var rawOutput) && (!hasContent || details.Length == 0))
                details = rawOutput.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                    JsonValueKind.String => rawOutput.GetString() ?? "",
                    _ => rawOutput.GetRawText()
                };
            message.Text = $"{title}\n\n*{status}*{message.ToolInput}{(details.Length > 0 && !details.StartsWith('\n') ? "\n\n" : "")}{details}"; if (!replaying) store.SaveMessage(chat, message);
            if (id is not null && status is "completed" or "failed") activeToolInputs.Remove(id);
        }
        else if (kind == "plan")
        {
            if (activePlan is null) { Add("plan", ""); activePlan = chat.Messages.Last(); }
            activePlan.Text = string.Join("\n", update.GetProperty("entries").EnumerateArray().Select(e => $"- [{(e.GetProperty("status").GetString() == "completed" ? "x" : " ")}] {e.GetProperty("content").GetString()}"));
            store.SaveMessage(chat, activePlan);
        }
        Changed?.Invoke();
    }
    private Task? disposal;
    public ValueTask DisposeAsync() => new(disposal ??= DisposeCore());
    private async Task DisposeCore()
    {
        DisconnectSubagents();
        idleTimer?.Stop(); lifetime.Cancel();
        var previous = client; client = null; connected = false;
        var tasks = new[] { previous?.DisposeAsync().AsTask(), idleShutdown, activeTask, steeringTask, reconnectTask, recoveryTask, WaitForHistoryShutdown() }.OfType<Task>();
        await Task.WhenAll(tasks);
    }
}
