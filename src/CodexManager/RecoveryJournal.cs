using System.Security.Cryptography;
using System.Text;

namespace CodexManager;

public sealed class RecoveryJournal(string directory)
{
    private string PathFor(string id) => Path.Combine(directory, "recovery", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");
    public string? Read(string id)
    {
        var path = PathFor(id);
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    public void Write(string id, string value)
    {
        var path = PathFor(id); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(value)); stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
