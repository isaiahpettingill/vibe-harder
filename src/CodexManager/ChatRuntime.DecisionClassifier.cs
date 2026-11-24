using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

public sealed partial class ChatRuntime
{
    internal DecisionClassifier? Classifier { get; set; }
    internal Func<bool>? IsWaitingForWorkflowRun { get; set; }

    private async Task<JsonElement> ReviewCompletion(string request, long sequence, JsonElement result,
        Func<JsonArray?, Task<JsonElement>> prompt, CancellationToken token)
    {
        if (Classifier is not { Enabled: true } classifier || string.IsNullOrWhiteSpace(request)) return result;
        string? previousResponse = null;
        for (var i = 0; i < classifier.Attempts && !token.IsCancellationRequested && turnFailure is null && !HasBackgroundWork &&
            chat.QueuedInputs.Count == 0 && !IsSteering && !detachedTurn && !PullRequestWaits.IsWaiting(store, chat) && IsWaitingForWorkflowRun?.Invoke() != true &&
            result.TryGetProperty("stopReason", out var reason) && reason.GetString() == "end_turn"; i++)
        {
            var response = string.Join("\n", chat.Messages.Where(message => message.Sequence > sequence && message.Role == "assistant").Select(message => message.Text));
            if (response == previousResponse || string.IsNullOrWhiteSpace(response)) break;
            chat.Status = "Checking the response…"; Changed?.Invoke();
            if (!await classifier.NeedsContinuation(request, response, token) || chat.QueuedInputs.Count > 0 || IsSteering || detachedTurn) break;
            token.ThrowIfCancellationRequested();
            previousResponse = response;
            sequence = chat.Messages.LastOrDefault()?.Sequence ?? sequence;
            Add("system", "The classifier found unfinished work. The agent will check and continue where authorized.");
            chat.Status = "Working…"; Changed?.Invoke();
            result = await prompt(new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = classifier.ResumePrompt
            }));
        }
        return result;
    }
}
