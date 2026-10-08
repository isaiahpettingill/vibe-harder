using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace CodexManager.Tests;

public class PermissionPolicyTests
{
    private const string Request = """{"toolCall":{"title":"Run command"},"options":[{"optionId":"deny","kind":"reject_once","name":"Reject"},{"optionId":"forever","kind":"allow_always","name":"Always allow"},{"optionId":"once","kind":"allow_once","name":"Allow"}]}""";
    [Trait("Category", "CI")]
    [Fact]
    public void AllowAllIsOptInAndPersistent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "permission-policy", Guid.NewGuid().ToString("N"));
        using var request = JsonDocument.Parse(Request);
        using (var store = new Store(directory))
        {
            Assert.Null(PermissionPolicy.AutoApprove(store, request.RootElement));
            store.Setting("allowAllPermissions", "1");
            var outcome = PermissionPolicy.AutoApprove(store, request.RootElement)!["outcome"]!;
            Assert.Equal(("selected", "once"), (outcome["outcome"]!.GetValue<string>(), outcome["optionId"]!.GetValue<string>()));
        }
        using var reopened = new Store(directory);
        Assert.NotNull(PermissionPolicy.AutoApprove(reopened, request.RootElement));
        reopened.Setting("allowAllPermissions", "0");
        Assert.Null(PermissionPolicy.AutoApprove(reopened, request.RootElement));
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData(Request, "once")]
    [InlineData("""{"options":[{"optionId":"forever","kind":"allow_always"},{"optionId":"deny","kind":"reject_always"}]}""", "forever")]
    [InlineData("""{"options":[{"kind":"allow_once"},{"optionId":"forever","kind":"allow_always"}]}""", "forever")]
    [InlineData("""{"options":[{"optionId":"deny","kind":"reject_once"}]}""", null)]
    [InlineData("""{"options":[{"optionId":"odd","kind":"ALLOW_ONCE"}]}""", null)]
    [InlineData("""{"options":[]}""", null)]
    [InlineData("""{"options":{"optionId":"once","kind":"allow_once"}}""", null)]
    [InlineData("""{"toolCall":{"title":"Run command"}}""", null)]
    public void AllowAllPicksTheNarrowestAllowOptionAndNeverARejection(string request, string? expected)
    {
        using var document = JsonDocument.Parse(request);
        Assert.Equal(expected, PermissionPolicy.AllowedOption(document.RootElement));
    }

    [Trait("Category", "CI")]
    [AvaloniaFact]
    public void InlineCardReturnsTheSelectedOptionWithoutOpeningAWindow()
    {
        string? selected = null;
        var card = new PermissionCard(JsonNode.Parse(Request)!.AsObject(), id => { selected = id; return Task.CompletedTask; });
        var window = new Window { Content = card }; window.Show();
        try
        {
            card.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Allow")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("once", selected);
            Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
    }
}
