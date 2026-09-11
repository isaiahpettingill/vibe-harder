using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;

namespace CodexManager;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
}

public sealed record Workspace(string Id, string Name, string Path, string? Distro = null)
{
    public bool IsWsl => !string.IsNullOrEmpty(Distro);
    public string Host => IsWsl ? $"WSL · {Distro}" : OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
    public string Caption => $"{Name}  ·  {Host}";
    public override string ToString() => Caption;
}

public sealed class Chat : Observable
{
    public IReadOnlyList<SessionConfig> ConfigOptions { get; set; } = [];
    public int ConfigVersion { get; set; }
    public IReadOnlyList<SlashCommand> Commands { get; set; } = [];
    public PendingInput? PendingInput { get; set; }
    public PendingInput? InterruptedInput { get; set; }
    public ObservableCollection<PendingInput> QueuedInputs { get; } = [];
    public bool NeedsLogin { get; set; }
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string WorkspaceId { get; init; }
    public string? SessionId { get; set; }
    public AgentProvider Provider { get; init; }
    public string ProviderIcon => AgentProviders.Get(Provider).Icon;
    public string ProviderLabel => AgentProviders.Get(Provider).Label;
    public bool Archived { get; set; }
    private string title = "New chat";
    public string Title { get => title; set => Set(ref title, value); }
    private string status = "Ready";
    public string Status { get => status; set => Set(ref status, value); }
    public string Draft { get; set; } = "";
    public void RecoverInput(PendingInput input)
    {
        if (string.IsNullOrEmpty(Draft)) Draft = input.Text;
        else if (Draft != input.Text && !string.IsNullOrEmpty(input.Text)) Draft += "\n\n[Recovered interrupted request]\n" + input.Text;
        foreach (var attachment in input.Attachments) if (!Attachments.Contains(attachment)) Attachments.Add(attachment);
    }
    public ObservableCollection<Attachment> Attachments { get; } = [];
    public ObservableCollection<Message> Messages { get; } = [];
    public bool Busy { get; set; }
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public override string ToString() => Title;
}

public sealed record PendingInput(string Text, Attachment[] Attachments);

public sealed class Message : Observable
{
    public AgentProvider Provider { get; init; }
    public ObservableCollection<Attachment> Attachments { get; } = [];
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Role { get; init; } = "assistant";
    public string? ToolId { get; init; }
    public string ToolInput { get; set; } = "";
    private string text = "";
    public string Text { get => text; set => Set(ref text, value); }
    public string Label => Role switch { "user" => "YOU", "tool" => "TOOL", "system" => "SESSION", "thought" => "THINKING", _ => AgentProviders.Get(Provider).Name.ToUpperInvariant() };
}

public sealed record Attachment(string Name, string MimeType, string Data, string? SourcePath = null, string? Reference = null)
{
    private Bitmap? thumbnail;
    [JsonIgnore]
    public Bitmap? Thumbnail
    {
        get
        {
            if (!IsImage) return null;
            try { return thumbnail ??= Bitmap.DecodeToWidth(new MemoryStream(Convert.FromBase64String(Data)), 320); }
            catch { return null; }
        }
    }
    public bool IsImage => MimeType.StartsWith("image/", StringComparison.Ordinal);
    public JsonObject ToContent() => IsImage
        ? RpcJson.Object(("type", "image"), ("mimeType", MimeType), ("data", Data))
        : RpcJson.Object(("type", "resource"), ("resource", RpcJson.Object(("uri", new Uri(SourcePath!).AbsoluteUri), ("mimeType", "text/plain"), ("text", Data))));
}
