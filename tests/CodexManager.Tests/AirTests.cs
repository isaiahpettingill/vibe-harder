using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class AirTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs");
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Trait("Category", "CI")]
    [Fact]
    public async Task ClientDeclaresAirAndATerminalOutputChannel()
    {
        await using var client = new AcpClient(Hosts.Info("node", Fixture));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.Initialize(timeout.Token);
        var capabilities = await client.Request("fixture/capabilities", new JsonObject(), timeout.Token);
        var air = capabilities.GetProperty("_meta").GetProperty("jetbrains").GetProperty("air");
        Assert.Equal(1, air.GetProperty("version").GetInt32());
        Assert.Equal(Air.Capabilities, air.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        Assert.True(capabilities.GetProperty("_meta").GetProperty("terminal_output_delta").GetBoolean());
    }

    [Trait("Category", "CI")]
    [Fact]
    public void PartialToolCallUpdatesMergeAndStreamedOutputAppends()
    {
        var call = new AcpToolCall();
        call.Merge(Json("""{"toolCallId":"c","title":"npm test","kind":"execute","status":"in_progress","rawInput":{"command":"npm test","description":"Run the tests"},"_meta":{"jetbrains":{"air":{"version":1,"commandTitle":"Run the tests"}}}}"""));
        // AIR sends only what changed: output chunks without a status keep the call running.
        call.Merge(Json("""{"toolCallId":"c","_meta":{"terminal_output_delta":{"terminal_id":"c","data":"PASS a\n"}}}"""));
        call.Merge(Json("""{"toolCallId":"c","_meta":{"terminal_output_delta":{"terminal_id":"c","data":"PASS b\n"}}}"""));
        Assert.Equal("in_progress", call.Status);
        var running = call.Render();
        Assert.StartsWith("Run the tests\n\n*in_progress*\n\n```\nnpm test\n```", running);
        Assert.Contains("```console\nPASS a\nPASS b\n```", running);
        call.Merge(Json("""{"toolCallId":"c","status":"completed","_meta":{"terminal_exit":{"terminal_id":"c","exit_code":0}}}"""));
        Assert.StartsWith("Run the tests\n\n*completed*", call.Render());
        Assert.Contains("PASS b", call.Render());

        // A backgrounded command keeps showing as running after its tool call completes.
        var background = new AcpToolCall();
        background.Merge(Json("""{"title":"npm run dev","status":"completed","_meta":{"jetbrains":{"air":{"version":1,"asyncTasks":{"backgrounded":true}}}}}"""));
        Assert.True(background.Backgrounded); Assert.StartsWith("npm run dev\n\n*running in background*", background.Render());

        // Saved calls resume merging after a restart.
        var restored = AcpToolCall.FromText(call.Render());
        restored.Merge(Json("""{"status":"failed"}"""));
        Assert.StartsWith("Run the tests\n\n*failed*\n\n```\nnpm test\n```", restored.Render());
    }

    [Trait("Category", "CI")]
    [Fact]
    public void DiffsRenderAsPatchesOrLineDiffs()
    {
        var patch = new AcpToolCall();
        patch.Merge(Json("""{"title":"Edit /w/App.ts","status":"completed","content":[{"type":"diff","path":"/w/App.ts","oldText":null,"newText":"","_meta":{"jetbrains":{"air":{"version":1,"diffPatch":{"version":1,"format":"git_patch","text":"diff --git a/w/App.ts b/w/App.ts\n--- a/w/App.ts\n+++ b/w/App.ts\n@@ -1 +1 @@\n-old\n+new\n"}}}}}]}"""));
        Assert.Contains("```diff\ndiff --git a/w/App.ts b/w/App.ts\n--- a/w/App.ts\n+++ b/w/App.ts\n@@ -1 +1 @@\n-old\n+new\n```", patch.Content);
        Assert.DoesNotContain("+\n", patch.Content);
        var standard = new AcpToolCall();
        standard.Merge(Json("""{"content":[{"type":"diff","path":"/w/a.txt","oldText":"one\ntwo\nthree\n","newText":"one\n2\nthree\n"}]}"""));
        Assert.Contains("--- a/w/a.txt\n+++ b/w/a.txt\n one\n-two\n+2\n three", standard.Content);
        // MCP results show their text, not the JSON envelope.
        var mcp = new AcpToolCall();
        mcp.Merge(Json("""{"rawOutput":{"result":{"content":[{"type":"text","text":"42 rows"}]},"error":null}}"""));
        Assert.Contains("42 rows", mcp.Render());
    }

    [Trait("Category", "CI")]
    [AvaloniaFact]
    public void PlanReviewShowsThePlanAndDefaultToNoFocusesTheDecline()
    {
        var request = JsonNode.Parse("""
            {"sessionId":"s","toolCall":{"toolCallId":"plan-review:p1","kind":"switch_mode","title":"Implement this plan?","rawInput":{"plan":"# Plan\n1. Add tests\n2. Ship"}},
             "options":[{"optionId":"revise_plan","name":"No, keep planning","kind":"reject_once"},{"optionId":"implement_plan","name":"Yes, implement","kind":"allow_once"}],
             "_meta":{"jetbrains":{"air":{"version":1,"permission":{"version":1,"title":"Ready to code?","description":"Reason: plan complete","defaultToNo":true}}}}}
            """)!.AsObject();
        string? chosen = null;
        var card = new PermissionCard(request, option => { chosen = option; return Task.CompletedTask; });
        var window = new Window { Content = card, Width = 600, Height = 600 }; window.Show(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        try
        {
            var texts = card.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("Ready to code?", texts); Assert.Contains("Reason: plan complete", texts);
            Assert.Equal("# Plan\n1. Add tests\n2. Ship", Assert.Single(card.GetLogicalDescendants().OfType<ChatMarkdown>()).Text);
            Assert.DoesNotContain(texts, t => t?.Contains("\"plan\"") == true);
            var decline = card.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "No, keep planning"));
            Assert.True(decline.IsFocused);
            card.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Yes, implement")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("implement_plan", chosen);
        }
        finally { window.Close(); }
    }

    [Trait("Category", "CI")]
    [AvaloniaFact]
    public void CommandApprovalShowsTheCommandNotJson()
    {
        var request = JsonNode.Parse("""{"toolCall":{"toolCallId":"t","title":"Run command","rawInput":{"command":"npm test","cwd":"/w"}},"options":[{"optionId":"allow_once","name":"Yes, proceed","kind":"allow_once"},{"optionId":"cancel","name":"No","kind":"reject_once"}],"_meta":{"jetbrains":{"air":{"version":1,"permission":{"version":1,"title":"Run command?"}}}}}""")!.AsObject();
        var card = new PermissionCard(request, _ => Task.CompletedTask);
        var texts = card.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        Assert.Contains("Run command?", texts); Assert.Contains("npm test", texts);
        Assert.DoesNotContain(texts, t => t?.Contains("\"command\"") == true);
    }
}
