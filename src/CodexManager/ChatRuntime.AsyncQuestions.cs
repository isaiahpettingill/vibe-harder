using System.Text.Json;
using Avalonia.Threading;

namespace CodexManager;

public sealed partial class ChatRuntime
{
    private CodexAsyncQuestions? asyncQuestions;
    private string? asyncQuestionsSession;
    // Tests point this at a fixture Codex home.
    public Func<Task<string>>? CodexHome { get; set; }

    // Follows the rollout while a Codex turn runs; see CodexAsyncQuestions for why.
    private async Task FollowAsyncQuestions(CancellationToken token)
    {
        if (chat.Provider != AgentProvider.Codex || chat.SessionId is not { } session) return;
        if (asyncQuestionsSession != session)
        {
            asyncQuestionsSession = session;
            asyncQuestions = new(CodexHome ?? (() => ConfigurationFiles.CodexHome(workspace)), session, DateTimeOffset.UtcNow);
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                await ReadAsyncQuestions();
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (OperationCanceledException) { }
        // The turn often ends right after asking; pick up anything written since the last read.
        await ReadAsyncQuestions();
    }
    private async Task ReadAsyncQuestions()
    {
        if (asyncQuestions is not { } reader || lifetime.IsCancellationRequested) return;
        CodexAsyncQuestions.Item[] items;
        try { items = await reader.Read(lifetime.Token); }
        // Best effort: a missing or unreadable rollout only means these extras are not shown.
        catch (Exception error) { System.Diagnostics.Trace.WriteLine("Codex rollout: " + error.Message); return; }
        if (items.Length == 0) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var item in items)
            {
                if (chat.Messages.Any(m => m.ProviderMessageId == item.CallId)) continue;
                Add("assistant", CodexAsyncQuestions.Text(item));
                chat.Messages[^1].ProviderMessageId = item.CallId;
                if (item.Questions.Length > 0) _ = AskAsyncQuestion(item);
            }
            Changed?.Invoke();
        });
    }
    private async Task AskAsyncQuestion(CodexAsyncQuestions.Item item)
    {
        if (Elicitation is not { } ask) return;
        pendingQuestions++; UpdatePendingInput();
        try
        {
            using var form = JsonDocument.Parse(CodexAsyncQuestions.Form(item).ToJsonString());
            var result = await ask(form.RootElement.Clone(), lifetime.Token);
            if (result["action"]?.GetValue<string>() != "accept" || result["content"] is not System.Text.Json.Nodes.JsonObject content) return;
            if (CodexAsyncQuestions.Reply(item, content) is not { } reply || lifetime.IsCancellationRequested) return;
            // Codex reads the answer whenever it arrives, so never stop a running turn for it:
            // send it now when idle, steer it in when supported, or let the queue send it next.
            Queue(new PendingInput(reply, []));
            if (!chat.Busy || SupportsSteering) await AdvanceQueued();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { chat.Status = "Could not answer Codex: " + error.Message; }
        finally { pendingQuestions--; UpdatePendingInput(); Changed?.Invoke(); }
    }
}
