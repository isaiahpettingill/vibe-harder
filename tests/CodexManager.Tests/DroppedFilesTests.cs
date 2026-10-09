namespace CodexManager.Tests;

public class DroppedFilesTests
{
    // What file managers put on a drag when they offer text instead of a file list
    // (text/uri-list on X11 and Wayland, plain paths elsewhere) and the files it names.
    public static TheoryData<string, string[]> Drops => new()
    {
        { "{uri:a b.txt}", ["a b.txt"] },
        { "# copied by the file manager\r\n{uri:a b.txt}\r\n{uri:c.png}\r\n", ["a b.txt", "c.png"] },
        { "{path:c.png}", ["c.png"] },
        { "\"{path:a b.txt}\"", ["a b.txt"] },
        { "{uri:missing.txt}\n{uri:c.png}", ["c.png"] },
        { "https://example.com/c.png\nrelative/c.png", [] },
        { "", [] },
    };

    [Trait("Category", "CI")]
    [Theory]
    [MemberData(nameof(Drops))]
    public void DroppedTextNamesTheExistingFilesItListsInOrder(string dropped, string[] expected)
    {
        var directory = Directory.CreateTempSubdirectory("dropped-").FullName;
        foreach (var name in new[] { "a b.txt", "c.png" }) File.WriteAllText(Path.Combine(directory, name), name);
        string Resolve(string template) => System.Text.RegularExpressions.Regex.Replace(template, @"\{(uri|path):([^}]+)\}", m =>
            m.Groups[1].Value == "uri" ? new Uri(Path.Combine(directory, m.Groups[2].Value)).AbsoluteUri : Path.Combine(directory, m.Groups[2].Value));
        Assert.Equal(expected.Select(name => Path.Combine(directory, name)), DroppedFiles.Paths(Resolve(dropped)));
    }
}
