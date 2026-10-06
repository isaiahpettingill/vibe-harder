using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace CodexManager;

// Web Push message encryption (RFC 8291, aes128gcm) and VAPID (RFC 8292), as UnifiedPush requires.
// Elliptic-curve work uses BouncyCastle so the computer and the Android app compute it the same way.
public static class WebPush
{
    private static readonly X9ECParameters Curve = ECNamedCurveTable.GetByName("P-256");
    private static readonly ECDomainParameters Domain = new(Curve.Curve, Curve.G, Curve.N, Curve.H);

    public sealed record KeyPair(byte[] PublicKey, byte[] PrivateKey);
    // An uncompressed P-256 public key (65 bytes) and its 32-byte private scalar.
    public static KeyPair Generate()
    {
        var generator = new ECKeyPairGenerator(); generator.Init(new ECKeyGenerationParameters(Domain, new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        return new(((ECPublicKeyParameters)pair.Public).Q.GetEncoded(false), Fixed(((ECPrivateKeyParameters)pair.Private).D.ToByteArrayUnsigned()));
    }

    // Encrypts for a subscriber's p256dh key and auth secret: one record, ready to POST.
    public static byte[] Encrypt(byte[] plaintext, byte[] subscriberPublicKey, byte[] authSecret)
    {
        if (plaintext.Length > 3993) throw new ArgumentException("A push message holds at most 3993 bytes.");
        var server = Generate();
        var salt = RandomNumberGenerator.GetBytes(16);
        var (key, nonce) = Keys(Agree(server.PrivateKey, subscriberPublicKey), authSecret, subscriberPublicKey, server.PublicKey, salt);
        var record = new byte[plaintext.Length + 1]; plaintext.CopyTo(record, 0); record[^1] = 2; // last record delimiter
        var body = Gcm(true, key, nonce, record);
        var message = new byte[16 + 4 + 1 + server.PublicKey.Length + body.Length];
        salt.CopyTo(message, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(16), 4096);
        message[20] = (byte)server.PublicKey.Length; server.PublicKey.CopyTo(message, 21);
        body.CopyTo(message, 21 + server.PublicKey.Length);
        return message;
    }

    public static byte[] Decrypt(byte[] message, KeyPair subscriber, byte[] authSecret)
    {
        if (message.Length < 21) throw new CryptographicException("The push message is too short.");
        var salt = message[..16]; var idLength = message[20];
        if (message.Length < 21 + idLength + 17) throw new CryptographicException("The push message is too short.");
        var serverPublicKey = message[21..(21 + idLength)];
        var (key, nonce) = Keys(Agree(subscriber.PrivateKey, serverPublicKey), authSecret, subscriber.PublicKey, serverPublicKey, salt);
        var record = Gcm(false, key, nonce, message[(21 + idLength)..]);
        var end = Array.LastIndexOf(record, (byte)2);
        if (end < 0 || record.AsSpan(end + 1).IndexOfAnyExcept((byte)0) >= 0) throw new CryptographicException("The push message has no end delimiter.");
        return record[..end];
    }

    private static (byte[] Key, byte[] Nonce) Keys(byte[] shared, byte[] authSecret, byte[] subscriberPublicKey, byte[] serverPublicKey, byte[] salt)
    {
        var info = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(subscriberPublicKey).Concat(serverPublicKey).ToArray();
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, info);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        return (HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0")),
                HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0")));
    }
    private static byte[] Agree(byte[] privateKey, byte[] publicKey)
    {
        var agreement = new ECDHBasicAgreement();
        agreement.Init(new ECPrivateKeyParameters(new Org.BouncyCastle.Math.BigInteger(1, privateKey), Domain));
        return Fixed(agreement.CalculateAgreement(new ECPublicKeyParameters(Curve.Curve.DecodePoint(publicKey), Domain)).ToByteArrayUnsigned());
    }
    private static byte[] Gcm(bool encrypt, byte[] key, byte[] nonce, byte[] input)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, nonce));
        var output = new byte[cipher.GetOutputSize(input.Length)];
        var length = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        try { length += cipher.DoFinal(output, length); }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException error) { throw new CryptographicException("The push message could not be decrypted.", error); }
        return output[..length];
    }
    private static byte[] Fixed(byte[] value) => value.Length >= 32 ? value[^32..] : new byte[32 - value.Length].Concat(value).ToArray();

    // Authorization header that identifies this computer to the push server (RFC 8292).
    public static string Vapid(string endpoint, KeyPair key, string subject, DateTimeOffset now)
    {
        var audience = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var claims = Base64Url(Encoding.UTF8.GetBytes($"{{\"aud\":\"{audience}\",\"exp\":{now.AddHours(12).ToUnixTimeSeconds()},\"sub\":\"{subject}\"}}"));
        var signer = new Org.BouncyCastle.Crypto.Signers.ECDsaSigner(new Org.BouncyCastle.Crypto.Signers.HMacDsaKCalculator(new Org.BouncyCastle.Crypto.Digests.Sha256Digest()));
        signer.Init(true, new ECPrivateKeyParameters(new Org.BouncyCastle.Math.BigInteger(1, key.PrivateKey), Domain));
        var signature = signer.GenerateSignature(SHA256.HashData(Encoding.ASCII.GetBytes(header + "." + claims)));
        var jwt = header + "." + claims + "." + Base64Url(Fixed(signature[0].ToByteArrayUnsigned()).Concat(Fixed(signature[1].ToByteArrayUnsigned())).ToArray());
        return "vapid t=" + jwt + ", k=" + Base64Url(key.PublicKey);
    }
    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] FromBase64Url(string text)
    {
        var value = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(value + new string('=', (4 - value.Length % 4) % 4));
    }
}
