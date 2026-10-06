using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class ElicitationFormTests
{
    [AvaloniaFact]
    public void InlineCardReturnsEditedChoiceAndNote()
    {
        var request = JsonNode.Parse("""
            {"mode":"form","message":"Which approach?","requestedSchema":{"type":"object","properties":{"approach":{"type":"string","oneOf":[{"const":"simple","title":"Simple"},{"const":"broad","title":"Broad"}]},"note":{"type":"string"}},"required":["approach"]}}
            """)!.AsObject();
        JsonObject? answer = null;
        var card = new ElicitationCard(request, value => { answer = value; return Task.CompletedTask; });
        var window = new Window { Content = card }; window.Show();
        try
        {
            card.GetLogicalDescendants().OfType<RadioButton>().ElementAt(1).IsChecked = true;
            card.GetLogicalDescendants().OfType<TextBox>().Single().Text = "Keep tests";
            card.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Send answer")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("accept", answer?["action"]?.GetValue<string>());
            Assert.Equal("broad", answer?["content"]?["approach"]?.GetValue<string>());
            Assert.Equal("Keep tests", answer?["content"]?["note"]?.GetValue<string>());
            Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
    }
    private static JsonObject? Answer(string json, Action<ElicitationCard> act)
    {
        JsonObject? answer = null;
        var card = new ElicitationCard(JsonNode.Parse(json)!.AsObject(), value => { answer = value; return Task.CompletedTask; });
        var window = new Window { Content = card }; window.Show();
        try
        {
            act(card);
            card.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Send answer")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return answer;
        }
        finally { window.Close(); }
    }
    private static RadioButton Radio(ElicitationCard card, string label) =>
        card.GetLogicalDescendants().OfType<RadioButton>().Single(r => r.Content is string s ? s == label : r.GetLogicalDescendants().OfType<TextBlock>().First().Text == label);

    // claude-agent-acp: a select field per question plus an "Other" companion naming it.
    private const string ClaudeQuestion = """
        {"mode":"form","message":"Which database?","requestedSchema":{"type":"object","properties":{
        "question_0":{"type":"string","title":"Database","oneOf":[{"const":"Postgres","title":"Postgres","description":"Relational"},{"const":"Redis","title":"Redis"}]},
        "question_0_custom":{"type":"string","title":"Other","_meta":{"jetbrains":{"air":{"version":1,"customAnswer":{"questionId":"question_0","isCustomAnswer":true}}},"_askUserQuestionCustomAnswer":{"questionId":"question_0","isCustomAnswer":true}}},
        "question_1":{"type":"array","title":"Extras","items":{"anyOf":[{"const":"Cache","title":"Cache"},{"const":"Queue","title":"Queue"}]}},
        "question_1_custom":{"type":"string","title":"Other","_meta":{"jetbrains":{"air":{"version":1,"customAnswer":{"questionId":"question_1","isCustomAnswer":true}}}}}}}}
        """;
    // codex-acp: a required select per question and a note field; typed text in the select is the answer.
    private const string CodexQuestion = """
        {"mode":"form","message":"Codex needs your input to continue.","requestedSchema":{"type":"object","properties":{
        "rates":{"type":"string","title":"Which rates?","oneOf":[{"const":"1x","title":"1x"},{"const":"2x","title":"2x"}]},
        "rates_note":{"type":"string","title":"Additional answer or note","_meta":{"codex":{"questionId":"rates","role":"user_note"},"_askUserQuestionCustomAnswer":true,"jetbrains":{"air":{"version":1,"customAnswer":true}}}}},"required":["rates"]}}
        """;

    [AvaloniaFact]
    public void WriteInAnswersAttachToTheirQuestionInEachAgentsFormat()
    {
        // Claude: the companion is the write-in, rendered under its own question, never as a loose field.
        var claude = Answer(ClaudeQuestion, card =>
        {
            Assert.Equal(2, card.GetLogicalDescendants().OfType<TextBox>().Count(t => t.Name == "OtherAnswer"));
            Assert.DoesNotContain(card.GetLogicalDescendants().OfType<TextBlock>(), t => t.FontWeight == Avalonia.Media.FontWeight.Medium && t.Text is "Other" or "Other *");
            card.GetLogicalDescendants().OfType<TextBox>().First().Text = "SQLite";
            Assert.True(Radio(card, "Other").IsChecked, "typing should select Other; Postgres=" + Radio(card, "Postgres").IsChecked);
            card.GetLogicalDescendants().OfType<CheckBox>().First().IsChecked = true;
        })!;
        Assert.Equal("accept", claude["action"]!.GetValue<string>());
        Assert.False(claude["content"]!.AsObject().ContainsKey("question_0"));
        Assert.Equal("SQLite", claude["content"]!["question_0_custom"]!.GetValue<string>());
        Assert.Equal("Cache", claude["content"]!["question_1"]![0]!.GetValue<string>());

        // Claude: text next to a picked option is a note on it.
        var noted = Answer(ClaudeQuestion, card => { Radio(card, "Redis").IsChecked = true; card.GetLogicalDescendants().OfType<TextBox>().First().Text = "for sessions"; })!;
        Assert.Equal("Redis", noted["content"]!["question_0"]!.GetValue<string>());
        Assert.Equal("for sessions", noted["content"]!["question_0_custom"]!.GetValue<string>());

        // Codex: the write-in goes in the required choice field itself, and passes validation.
        var codex = Answer(CodexQuestion, card => { Radio(card, "Other").IsChecked = true; card.GetLogicalDescendants().OfType<TextBox>().Single().Text = "3x for urgent"; })!;
        Assert.Equal("accept", codex["action"]!.GetValue<string>());
        Assert.Equal("3x for urgent", codex["content"]!["rates"]!.GetValue<string>());
        Assert.False(codex["content"]!.AsObject().ContainsKey("rates_note"));
        var codexNote = Answer(CodexQuestion, card => { Radio(card, "1x").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); card.GetLogicalDescendants().OfType<TextBox>().Single().Text = "only this year"; })!;
        Assert.Equal("1x", codexNote["content"]!["rates"]!.GetValue<string>());
        Assert.Equal("only this year", codexNote["content"]!["rates_note"]!.GetValue<string>());
    }

    [Fact]
    public void SupportsCodexAndClaudeChoiceSchemasAndValidatesAnswers()
    {
        var codex = JsonNode.Parse("""
            {"mode":"form","message":"Choose an approach","requestedSchema":{"type":"object","properties":{"approach":{"type":"string","oneOf":[{"const":"simple","title":"Simple"},{"const":"broad","title":"Broad"}]},"note":{"type":"string"}},"required":["approach"]}}
            """)!.AsObject();
        Assert.Null(ElicitationForm.Validate(codex, new JsonObject { ["approach"] = "broad", ["note"] = "More tests" }));
        Assert.NotNull(ElicitationForm.Validate(codex, new JsonObject { ["approach"] = "unknown" }));
        Assert.NotNull(ElicitationForm.Validate(codex, new JsonObject()));

        var claude = JsonNode.Parse("""
            {"mode":"form","requestedSchema":{"type":"object","properties":{"features":{"type":"array","items":{"type":"string","anyOf":[{"const":"search","title":"Search"},{"const":"sort","title":"Sort"}]},"minItems":1},"features_custom":{"type":"string","title":"Other"}},"required":["features"]}}
            """)!.AsObject();
        Assert.Null(ElicitationForm.Validate(claude, new JsonObject { ["features"] = new JsonArray("search", "sort"), ["features_custom"] = "Export" }));
        Assert.NotNull(ElicitationForm.Validate(claude, new JsonObject { ["features"] = new JsonArray() }));
        Assert.NotNull(ElicitationForm.Validate(claude, new JsonObject { ["features"] = new JsonArray("delete") }));
    }

    [Fact]
    public void ChecksNumbersAndBoundsPatternEvaluation()
    {
        var request = JsonNode.Parse("""
            {"mode":"form","requestedSchema":{"type":"object","properties":{"port":{"type":"integer","minimum":1,"maximum":65535},"name":{"type":"string","minLength":2,"pattern":"^[a-z]+$"}},"required":["port","name"]}}
            """)!.AsObject();
        Assert.Null(ElicitationForm.Validate(request, new JsonObject { ["port"] = 8080, ["name"] = "web" }));
        Assert.NotNull(ElicitationForm.Validate(request, new JsonObject { ["port"] = 1.5, ["name"] = "web" }));
        Assert.NotNull(ElicitationForm.Validate(request, new JsonObject { ["port"] = 70000, ["name"] = "web" }));
        Assert.NotNull(ElicitationForm.Validate(request, new JsonObject { ["port"] = 8080, ["name"] = "A" }));
    }

    [AvaloniaFact]
    public async Task TypingInAQuestionKeepsFocusWhileTheChatUpdates()
    {
        var directory = Directory.CreateTempSubdirectory("question-focus-").FullName; Environment.SetEnvironmentVariable("CODEX_MANAGER_DATA", directory);
        var store = new Store(directory); store.Setting("remoteEnabled", "0"); store.Setting("runInTray", "0");
        store.Save(new Workspace("w", "Test", directory)); store.Save(new Chat { WorkspaceId = "w" });
        foreach (var provider in AgentProviders.All) store.Setting(AgentProviders.CommandKey(provider.Provider, false), "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\"");
        var window = new MainWindow(store); window.Show();
        try
        {
            var composer = window.FindControl<ComposerEditor>("Composer")!;
            composer.Text = "question"; window.FindControl<IconButton>("SendButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var until = DateTime.UtcNow.AddSeconds(15);
            ElicitationCard? card = null;
            while (card is null && DateTime.UtcNow < until) { await Task.Delay(25); window.UpdateLayout(); card = window.GetLogicalDescendants().OfType<ElicitationCard>().FirstOrDefault(); }
            Assert.NotNull(card);
            var field = card!.GetLogicalDescendants().OfType<TextBox>().First();
            field.Focus(); field.Text = "half typed"; Assert.True(field.IsFocused);
            // New agent output refreshes the pending-request panel; the field must keep focus and text.
            var refresh = typeof(MainView).GetMethod("UpdatePermissions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            for (var i = 0; i < 3; i++) { refresh.Invoke(window.View, []); window.UpdateLayout(); }
            Assert.True(field.IsFocused); Assert.Equal("half typed", field.Text);
            Assert.Same(card, window.GetLogicalDescendants().OfType<ElicitationCard>().Single());
        }
        finally { window.RequestExit(); var end = DateTime.UtcNow.AddSeconds(10); while (window.IsVisible && DateTime.UtcNow < end) await Task.Delay(25); }
    }

    [AvaloniaFact]
    public void EnterSendsTheAnswerAndCtrlEnterAddsALine()
    {
        JsonObject? answer = null;
        var card = new ElicitationCard(JsonNode.Parse(ClaudeQuestion)!.AsObject(), value => { answer = value; return Task.CompletedTask; });
        var window = new Window { Content = card }; window.Show();
        try
        {
            var buttons = card.GetLogicalDescendants().OfType<Button>().Where(b => b is not RadioButton && b.Content is string).ToArray();
            Assert.Equal(["Cancel", "Decline", "Send answer"], buttons.Select(b => (string)b.Content!));
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, ((Control)buttons[0].Parent!).HorizontalAlignment);
            var field = card.GetLogicalDescendants().OfType<TextBox>().First();
            field.Focus(); field.Text = "first line"; field.CaretIndex = field.Text.Length;
            field.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter, KeyModifiers = Avalonia.Input.KeyModifiers.Control });
            Assert.Null(answer); Assert.Equal("first line\n", field.Text);
            field.SelectedText = "second";
            field.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            Assert.Equal("accept", answer?["action"]?.GetValue<string>());
            Assert.Equal("first line\nsecond", answer!["content"]!["question_0_custom"]!.GetValue<string>());
        }
        finally { window.Close(); }

        answer = null;
        var choice = new ElicitationCard(JsonNode.Parse(CodexQuestion)!.AsObject(), value => { answer = value; return Task.CompletedTask; });
        window = new Window { Content = choice }; window.Show();
        try
        {
            choice.GetLogicalDescendants().OfType<RadioButton>().First().RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            Assert.Equal("1x", answer?["content"]?["rates"]?.GetValue<string>());
        }
        finally { window.Close(); }
    }
}
