using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

// Attachments reach the agent as files it can open, not as contents in the prompt: images go
// inline only to agents that accept them, everything else is given by path. A file picked on
// this machine keeps its own path; other attachments, and anything for a WSL workspace, are
// copied to a temporary folder the agent can read.
public sealed partial class ChatRuntime
{
    private bool supportsImages;
    private void ReadPromptCapabilities(JsonElement init) =>
        supportsImages = init.TryGetProperty("agentCapabilities", out var caps) && caps.TryGetProperty("promptCapabilities", out var prompt)
            && prompt.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.True;

    private async Task<List<JsonObject>> PromptContent(string text, IEnumerable<Attachment> attachments)
    {
        var content = new List<JsonObject>();
        if (!string.IsNullOrEmpty(text)) content.Add(RpcJson.Object(("type", "text"), ("text", text)));
        foreach (var attachment in attachments)
            content.Add(attachment.IsImage && supportsImages ? attachment.ToContent()
                : RpcJson.Object(("type", "text"), ("text", "Attached file: " + await Task.Run(() => AttachmentPath(attachment)))));
        return content;
    }

    private string AttachmentPath(Attachment attachment)
    {
        if (!workspace.IsWsl && AttachmentFiles.LocalPath(attachment) is { } local) return local;
        if (!workspace.IsWsl) return AttachmentFiles.WriteCopy(attachment);
        var linux = "/tmp/vibe-harder/attachments/" + Guid.NewGuid().ToString("N")[..12] + "/" + AttachmentFiles.SafeName(attachment.Name);
        var windows = @"\\wsl.localhost\" + workspace.Distro + linux.Replace('/', '\\');
        Directory.CreateDirectory(Path.GetDirectoryName(windows)!);
        File.WriteAllBytes(windows, AttachmentFiles.Bytes(attachment));
        return linux;
    }
}
