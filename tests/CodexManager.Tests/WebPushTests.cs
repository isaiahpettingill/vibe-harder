using System.Text;

namespace CodexManager.Tests;

public class WebPushTests
{
    [Fact]
    public void DecryptsTheRfc8291Example()
    {
        // RFC 8291, Appendix A.
        var subscriber = new WebPush.KeyPair(
            WebPush.FromBase64Url("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"),
            WebPush.FromBase64Url("q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94"));
        var auth = WebPush.FromBase64Url("BTBZMqHH6r4Tts7J_aSIgg");
        var message = WebPush.FromBase64Url("DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN");
        Assert.Equal("When I grow up, I want to be a watermelon", Encoding.UTF8.GetString(WebPush.Decrypt(message, subscriber, auth)));
    }

    [Fact]
    public void EncryptedMessagesDecryptOnlyWithTheSubscriberKeys()
    {
        var phone = WebPush.Generate(); var auth = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var payload = Encoding.UTF8.GetBytes("""{"title":"Reply ready","body":"Fix login"}""");
        var message = WebPush.Encrypt(payload, phone.PublicKey, auth);
        Assert.Equal(payload, WebPush.Decrypt(message, phone, auth));
        Assert.True(message.Length <= 4096);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => WebPush.Decrypt(message, WebPush.Generate(), auth));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => WebPush.Decrypt(message, phone, new byte[16]));
        Assert.Equal(65, phone.PublicKey.Length); Assert.Equal(87, WebPush.Base64Url(phone.PublicKey).Length);
    }

    [Fact]
    public void VapidTokenVerifiesWithThePublicKey()
    {
        var key = WebPush.Generate();
        var header = WebPush.Vapid("https://push.example.net/wpush/v2/abc", key, "mailto:push@example.net", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        Assert.StartsWith("vapid t=", header); Assert.EndsWith(", k=" + WebPush.Base64Url(key.PublicKey), header);
        var jwt = header["vapid t=".Length..header.IndexOf(',')].Split('.');
        Assert.Contains("\"aud\":\"https://push.example.net\"", Encoding.UTF8.GetString(WebPush.FromBase64Url(jwt[1])));
        using var verifier = System.Security.Cryptography.ECDsa.Create(new System.Security.Cryptography.ECParameters
        {
            Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
            Q = new() { X = key.PublicKey[1..33], Y = key.PublicKey[33..] }
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), WebPush.FromBase64Url(jwt[2]), System.Security.Cryptography.HashAlgorithmName.SHA256));
    }
}
