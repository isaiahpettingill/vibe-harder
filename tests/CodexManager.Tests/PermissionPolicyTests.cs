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
    [Fact]
    public void AllowAllIsOptInPersistentAndSelectsAnAllowOption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "permission-policy", Guid.NewGuid().ToString("N"));
        using var request = JsonDocument.Parse(Request);
        using (var store = new Store(directory))
        {
            Assert.Null(PermissionPolicy.AutoApprove(store, request.RootElement));
            store.Setting("allowAllPermissions", "1");
            Assert.Equal(RpcJson.Permission("once").ToJsonString(), PermissionPolicy.AutoApprove(store, request.RootElement)!.ToJsonString());
        }
        using var reopened = new Store(directory);
        Assert.NotNull(PermissionPolicy.AutoApprove(reopened, request.RootElement));
        reopened.Setting("allowAllPermissions", "0");
        Assert.Null(PermissionPolicy.AutoApprove(reopened, request.RootElement));
        using var noAllow = JsonDocument.Parse("""{"options":[{"optionId":"deny","kind":"reject_once"}]}""");
        Assert.Null(PermissionPolicy.AllowedOption(noAllow.RootElement));
    }
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
