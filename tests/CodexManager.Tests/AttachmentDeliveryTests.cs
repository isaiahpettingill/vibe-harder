using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace CodexManager.Tests;

public class AttachmentDeliveryTests
{
    private static string Fixture(string log, string flags) => "node \"" + Path.Combine(AppContext.BaseDirectory, "fake-acp.mjs") + "\" \"--prompt-log=" + log + "\" " + flags;
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("input.bin", "YQBi", "application/octet-stream", "blob")]
    [InlineData("input.BMP", "Qk0=", "image/bmp", "data")]
    [InlineData("input.txt", "aGk=", "text/plain", "text")]
    public async Task StreamOnlyAttachmentsHaveContentButNoLocalSource(string name, string payload, string mime, string field)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(payload));
        var item = await AttachmentFiles.Read(name, stream, TestContext.Current.CancellationToken);
        Assert.Equal(mime, item.MimeType);
        Assert.Null(item.SourcePath);
        var content = item.ToContent();
        Assert.Equal(field == "text" ? "hi" : payload, (content["resource"] ?? content)[field]!.GetValue<string>());
    }

    [Trait("Category", "CI")]
    [Theory]
    [InlineData("..", "attachment")]
    [InlineData(" . ", "attachment")]
    [InlineData("folder\\report.txt", "folder_report.txt")]
    public void OpenedCopiesUsePortableFileNames(string name, string expected)
    {
        var path = AttachmentFiles.WriteCopy(new(name, "text/plain", "example"));
        try { Assert.Equal(expected, Path.GetFileName(path)); Assert.Equal("example"u8.ToArray(), File.ReadAllBytes(path)); }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Trait("Category", "CI")]
    [Fact]
    public void EditedCopyOfPickedFileDoesNotSendUneditedOriginal()
    {
        var original = Path.GetTempFileName();
        var attachment = new Attachment("notes.txt", "text/plain", "original", new Uri(original).AbsoluteUri);
        var copy = AttachmentFiles.WriteCopy(attachment);
        try
        {
            File.WriteAllText(copy, "edited"); File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddMinutes(1));
            var edited = AttachmentFiles.WithSavedEdits(attachment);
            Assert.Equal(copy, AttachmentFiles.LocalPath(edited)); Assert.Equal("edited", edited.Data);
            File.WriteAllBytes(copy, [0xff]); File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddMinutes(2));
            Assert.Throws<IOException>(() => AttachmentFiles.WithSavedEdits(edited));
            using (var file = File.OpenWrite(copy)) file.SetLength(AttachmentFiles.MaximumBytes + 1);
            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddMinutes(3));
            Assert.Throws<IOException>(() => AttachmentFiles.WithSavedEdits(edited));
        }
        finally { File.Delete(original); Directory.Delete(Path.GetDirectoryName(copy)!, true); }
    }

    // The blocks the agent received for the last prompt.
    private static JsonElement[] LastPrompt(string log) => JsonDocument.Parse(File.ReadAllLines(log)[^1]).RootElement.EnumerateArray().ToArray();
    private static string PathIn(JsonElement block) => block.GetProperty("text").GetString()!["Attached file: ".Length..];

    // A file picked from disk, pasted text, and an image, sent to agents with and without image
    // support. The agent gets each as a file it can open, and an image also inline if it accepts one.
    [Trait("Category", "CI")]
    [AvaloniaTheory]
    [InlineData("", true)]
    [InlineData("--no-images", false)]
    public async Task AttachmentsReachTheAgentAsFilesItCanOpen(string flags, bool inlineImage)
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
        Assert.Equal(inlineImage ? ["text", "text", "text", "image", "text"] : ["text", "text", "text", "text"], blocks.Select(b => b.GetProperty("type").GetString()));
        // A file picked on this computer is given by its own path; the paste is written out.
        Assert.Equal(picked, PathIn(blocks[1]));
        Assert.Equal("long pasted text", File.ReadAllText(PathIn(blocks[2])));
        // The image's path is there either way, so the agent can also move or copy it.
        Assert.Equal(Png, File.ReadAllBytes(PathIn(blocks[^1])));
        if (inlineImage) Assert.Equal(Convert.ToBase64String(Png), blocks[3].GetProperty("data").GetString());
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
