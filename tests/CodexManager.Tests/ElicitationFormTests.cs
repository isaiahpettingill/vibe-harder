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
            card.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedIndex = 1;
            card.GetLogicalDescendants().OfType<TextBox>().Single().Text = "Keep tests";
            card.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Send answer")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("accept", answer?["action"]?.GetValue<string>());
            Assert.Equal("broad", answer?["content"]?["approach"]?.GetValue<string>());
            Assert.Equal("Keep tests", answer?["content"]?["note"]?.GetValue<string>());
            Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
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
}
