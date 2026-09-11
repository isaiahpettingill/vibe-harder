using System.Security.Cryptography;
using System.Text;

namespace CodexManager;

public static class RemoteKey
{
    public static string Generate(string path)
    {
        using var rsa = RSA.Create(3072);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            file.Write(Encoding.UTF8.GetBytes(rsa.ExportRSAPrivateKeyPem()));
        }
        var parameters = rsa.ExportParameters(false); using var data = new MemoryStream();
        void Field(byte[] bytes) { data.WriteByte((byte)(bytes.Length >> 24)); data.WriteByte((byte)(bytes.Length >> 16)); data.WriteByte((byte)(bytes.Length >> 8)); data.WriteByte((byte)bytes.Length); data.Write(bytes); }
        byte[] Positive(byte[] bytes) => bytes[0] >= 128 ? [0, .. bytes] : bytes;
        Field(Encoding.ASCII.GetBytes("ssh-rsa")); Field(Positive(parameters.Exponent!)); Field(Positive(parameters.Modulus!));
        var key = "ssh-rsa " + Convert.ToBase64String(data.ToArray()) + " codex-manager"; File.WriteAllText(path + ".pub", key); return key;
    }
}
