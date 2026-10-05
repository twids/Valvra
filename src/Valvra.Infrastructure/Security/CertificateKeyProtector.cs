using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Valvra.Core;

namespace Valvra.Infrastructure.Security;

public sealed class KeyProtectionOptions
{
    public string ActiveThumbprint { get; set; } = "";
    public string[] AllowedThumbprints { get; set; } = [];
}

public sealed class CertificateKeyProtector(KeyProtectionOptions options) : IKeyProtector
{
    public string ActiveKeyId => Normalize(options.ActiveThumbprint);
    public byte[] Wrap(ReadOnlySpan<byte> key)
    {
        using var certificate = Load(ActiveKeyId);
        if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
            throw new CryptographicException("Active encryption certificate is not valid at the current time.");
        using var rsa = certificate.GetRSAPublicKey() ?? throw new CryptographicException("RSA certificate required.");
        if (rsa.KeySize < 3072) throw new CryptographicException("RSA key must be at least 3072 bits.");
        return rsa.Encrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA256);
    }

    public byte[] Unwrap(string keyId, ReadOnlySpan<byte> wrappedKey)
    {
        using var certificate = Load(keyId);
        using var rsa = certificate.GetRSAPrivateKey() ?? throw new CryptographicException("Private key unavailable.");
        if (rsa.KeySize < 3072) throw new CryptographicException("RSA key must be at least 3072 bits.");
        return rsa.Decrypt(wrappedKey.ToArray(), RSAEncryptionPadding.OaepSHA256);
    }

    private X509Certificate2 Load(string keyId)
    {
        var normalized = Normalize(keyId);
        if (normalized.Length == 0 || !options.AllowedThumbprints.Append(options.ActiveThumbprint)
            .Any(x => Normalize(x) == normalized)) throw new CryptographicException("Unknown key identifier.");
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, false);
        try
        {
            if (matches.Count != 1) throw new CryptographicException("Key protection certificate not found or ambiguous.");
            return new X509Certificate2(matches[0]);
        }
        finally { foreach (var certificate in matches) certificate.Dispose(); }
    }

    private static string Normalize(string thumbprint) => thumbprint.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
}
