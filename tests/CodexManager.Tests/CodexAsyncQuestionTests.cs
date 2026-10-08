using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class CodexAsyncQuestionTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs");
    private static string Line(string name, object arguments, string callId) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-10-05T20:41:45.189Z", type = "response_item",
        payload = new { type = "function_call", name, arguments = JsonSerializer.Serialize(arguments), call_id = callId }
    });

    [Trait("Category", "CI")]
    [Fact]
    public void RolloutCallsBecomeQuestionsAndRepliesUseCodexEnvelope()
    {
        var ask = CodexAsyncQuestions.Parse(Line("request_user_input_async", new { questions = new object[] { new { title = "Which rates?\nPick one", options = new[] { "1x", "2x" } }, new { title = "Notes?" } } }, "call_1"))!;
        Assert.Equal("call_1", ask.CallId);
        Assert.Equal(["1x", "2x"], ask.Questions[0].Options);
        Assert.Equal("**Which rates?\nPick one**\n- 1x\n- 2x\n\n**Notes?**", CodexAsyncQuestions.Text(ask));
        var note = CodexAsyncQuestions.Parse(Line("send_message_to_user_async", new { message = "Staging is down." }, "call_2"))!;
        Assert.Equal("Staging is down.", CodexAsyncQuestions.Text(note));
        Assert.Null(CodexAsyncQuestions.Parse(Line("exec_command", new { cmd = "ls _async" }, "call_3")));
        Assert.Null(CodexAsyncQuestions.Parse("{not json _async"));

        var form = CodexAsyncQuestions.Form(ask);
        Assert.Equal("1x", form["requestedSchema"]!["properties"]!["q0"]!["default"]!.GetValue<string>());
        Assert.Null(ElicitationForm.Validate(form, new JsonObject { ["q0"] = "2x" }));

        // An own-words answer wins over the preselected option; unanswered questions are left out.
        var reply = CodexAsyncQuestions.Reply(ask, new JsonObject { ["q0"] = "1x", ["q0_other"] = " 3x for urgent " })!;
        Assert.Equal("<send_user_message_question_reply>\n[{\"answer\":\"3x for urgent\",\"question\":\"Which rates? Pick one\",\"questionItemId\":\"[\\\"request_user_input_async\\\",\\\"call_1\\\",0]\"}]\n</send_user_message_question_reply>", reply);
        Assert.Equal("> Which rates? Pick one\n\n3x for urgent", CodexAsyncQuestions.Display(reply));
        Assert.Null(CodexAsyncQuestions.Reply(ask, new JsonObject()));
        Assert.Equal("plain text", CodexAsyncQuestions.Display("plain text"));
    }

    [Trait("Category", "Integration")]
    [AvaloniaFact]
    public async Task QuestionsAskedDuringATurnAreShownAndAnsweredAfterIt()
    {
        var directory = Directory.CreateTempSubdirectory("codex-async-").FullName;
        var day = DateTime.UtcNow;
        var folder = Path.Combine(directory, "codex-home", "sessions", day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd")); Directory.CreateDirectory(folder);
        var rollout = Path.Combine(folder, "rollout-2026-10-05T14-32-42-fixture-session.jsonl");
        File.WriteAllText(rollout, Line("request_user_input_async", new { questions = new[] { new { title = "An old question" } } }, "call_old") + "\n");
        using var store = new Store(directory);
        var workspace = new Workspace("w", "Fixture", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", Provider = AgentProvider.Codex }; store.Save(chat);
        var command = OperatingSystem.IsWindows() ? $"node '{Fixture.Replace("'", "''")}'" : $"node {Hosts.Quote(Fixture)}";
        await using var runtime = new ChatRuntime(chat, workspace, store, command) { CodexHome = () => Task.FromResult(Path.Combine(directory, "codex-home")) };
        var asked = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Elicitation = (request, token) => { asked.TrySetResult(request); return answer.Task.WaitAsync(token); };

        await runtime.Send("async-question:" + rollout, []).WaitAsync(TimeSpan.FromSeconds(15));
        var request = await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Which rates apply?", request.GetProperty("requestedSchema").GetProperty("properties").GetProperty("q0").GetProperty("title").GetString());
        Assert.True(chat.NeedsPermission);
        var texts = chat.Messages.Select(m => m.Text).ToArray();
        Assert.Contains("Heads up: **staging** is down.", texts);
        Assert.Contains("**Which rates apply?**\n- 1x / 1x / 1x\n- Ask the manager\n\n**Anything else?**", texts);
        Assert.DoesNotContain(texts, t => t.Contains("An old question"));
        Assert.Contains(chat.Messages, m => m.ProviderMessageId == "call_ask");

        answer.SetResult(ElicitationForm.Accept(new JsonObject { ["q0"] = "Ask the manager", ["q1"] = "No" }));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (chat.Messages.LastOrDefault()?.Text != "Got it" && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.Equal("Got it", chat.Messages.Last().Text);
        var reply = chat.Messages.Last(m => m.Role == "user");
        Assert.Equal("> Which rates apply?\n\nAsk the manager\n\n> Anything else?\n\nNo", reply.Text);
        Assert.Empty(chat.QueuedInputs);
        Assert.False(chat.NeedsPermission);
    }
}
