using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class AttachmentDeliveryTests
{
    private static string Fixture(string log, string flags) => "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" \"--prompt-log=" + log + "\" " + flags;
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    // The blocks the agent received for the last prompt.
    private static JsonElement[] LastPrompt(string log) => JsonDocument.Parse(File.ReadAllLines(log)[^1]).RootElement.EnumerateArray().ToArray();
    private static string PathIn(JsonElement block) => block.GetProperty("text").GetString()!["Attached file: ".Length..];

    // A file picked from disk, pasted text, and an image, sent to agents with and without image
    // support. The agent gets each as a file it can open, and an image inline only if it accepts one.
    [Trait("Category", "CI")]
    [AvaloniaTheory]
    [InlineData("", "image")]
    [InlineData("--no-images", "text")]
    public async Task AttachmentsReachTheAgentAsFilesItCanOpen(string flags, string imageBlock)
    {
        var directory = Directory.CreateTempSubdirectory("attachments-").FullName;
        using var store = new Store(directory); var workspace = new Workspace("w", "Files", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w" }; store.Save(chat);
        var picked = Path.Combine(directory, "report.csv"); File.WriteAllText(picked, "a,b\n1,2\n");
        var attachments = new[]
        {
            new Attachment("report.csv", "text/plain", "a,b\n1,2\n", new Uri(picked).AbsoluteUri),
            new Attachment("Pasted text 1.txt", "text/plain", "long pasted text"),
            new Attachment("shot.png", "image/png", Convert.ToBase64String(Png)),
        };
        var log = Path.Combine(directory, "prompts.log");
        await using var runtime = new ChatRuntime(chat, workspace, store, Fixture(log, flags));
        await runtime.Send("look at these", attachments);

        var blocks = LastPrompt(log);
        Assert.Equal(["text", "text", "text", imageBlock], blocks.Select(b => b.GetProperty("type").GetString()));
        // A file picked on this computer is given by its own path; the paste is written out.
        Assert.Equal(picked, PathIn(blocks[1]));
        Assert.Equal("long pasted text", File.ReadAllText(PathIn(blocks[2])));
        if (imageBlock == "text") Assert.Equal(Png, File.ReadAllBytes(PathIn(blocks[3])));
        else Assert.Equal(Convert.ToBase64String(Png), blocks[3].GetProperty("data").GetString());
    }

    // A phone or another computer sends file contents with paths from its own disk. The host
    // never hands such a path to the agent, even one that exists here; it writes the file itself.
    [Trait("Category", "CI")]
    [AvaloniaFact]
    public async Task RemoteAttachmentsAreWrittenOnTheHostInsteadOfTrustingTheClientsPath()
    {
        var directory = Directory.CreateTempSubdirectory("remote-attachments-").FullName;
        using var store = new Store(directory); var workspace = new Workspace("w", "Files", directory); store.Save(workspace);
        var chat = new Chat { WorkspaceId = "w", Title = "Remote" }; store.Save(chat);
        var hostFile = Path.Combine(directory, "secret.txt"); File.WriteAllText(hostFile, "host only");
        var log = Path.Combine(directory, "prompts.log");
        ChatRuntime? runtime = null;
        using var service = new SessionService(store, [workspace], [chat], (c, w) => runtime ??= new ChatRuntime(c, w, store, Fixture(log, "")));
        try
        {
            var attachment = new JsonObject { ["Name"] = "notes.txt", ["MimeType"] = "text/plain", ["Data"] = "from the phone", ["SourcePath"] = new Uri(hostFile).AbsoluteUri };
            await service.Handle(new JsonObject { ["method"] = "send", ["chatId"] = chat.Id, ["text"] = "read this", ["attachments"] = new JsonArray(attachment) });
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(log) && DateTime.UtcNow < until) await Task.Delay(25);
            var given = PathIn(LastPrompt(log)[1]);
            Assert.NotEqual(hostFile, given);
            Assert.Equal("from the phone", File.ReadAllText(given));
        }
        finally { if (runtime is not null) await runtime.DisposeAsync(); }
    }
}
