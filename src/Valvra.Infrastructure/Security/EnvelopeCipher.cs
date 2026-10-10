using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valvra.Core;

namespace Valvra.Infrastructure.Security;

public sealed record CipherEnvelope(int Format, Guid InstallationId, Guid ResourceId, string KeyId, byte[] WrappedKey,
    byte[] Nonce, byte[] Ciphertext, byte[] Tag);

public sealed class EnvelopeCipher(IKeyProtector protector) : ISecretCipher
{
    public string Encrypt<T>(T payload, Guid id, long version, string purpose, Guid resourceId, Guid installationId)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        byte[] plaintext = [];
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Context(id, version, purpose, resourceId, installationId));
            return JsonSerializer.Serialize(new CipherEnvelope(2, installationId, resourceId, protector.ActiveKeyId,
                protector.Wrap(key), nonce, ciphertext, tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public T Decrypt<T>(string envelope, Guid id, long version, string purpose, Guid resourceId, Guid installationId)
    {
        var value = Parse(envelope);
        if (value.InstallationId != installationId || value.ResourceId != resourceId) throw new CryptographicException("Envelope context mismatch.");
        var key = protector.Unwrap(value.KeyId, value.WrappedKey);
        byte[] plaintext = [];
        try
        {
            plaintext = new byte[value.Ciphertext.Length];
            if (key.Length != 32) throw new CryptographicException("Invalid data key.");
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(value.Nonce, value.Ciphertext, value.Tag, plaintext, Context(id, version, purpose, resourceId, installationId));
            return JsonSerializer.Deserialize<T>(plaintext) ?? throw new CryptographicException("Invalid payload.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Rewrap(string envelope)
    {
        var value = Parse(envelope);
        var key = protector.Unwrap(value.KeyId, value.WrappedKey);
        try
        {
            if (key.Length != 32) throw new CryptographicException("Invalid data key.");
            return JsonSerializer.Serialize(value with { KeyId = protector.ActiveKeyId, WrappedKey = protector.Wrap(key) });
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] Context(Guid id, long version, string purpose, Guid resourceId, Guid installationId)
    {
        if (installationId == Guid.Empty || resourceId == Guid.Empty || id == Guid.Empty || version < 1 || purpose is not ("secret" or "license"))
            throw new CryptographicException("Invalid encryption context.");
        return Encoding.UTF8.GetBytes($"Valvra|2|{installationId:D}|{resourceId:D}|{purpose}|{id:D}|{version}");
    }

    private static CipherEnvelope Parse(string json)
    {
        var result = JsonSerializer.Deserialize<CipherEnvelope>(json) ?? throw new CryptographicException("Missing envelope.");
        if (result.Format != 2 || result.InstallationId == Guid.Empty || result.ResourceId == Guid.Empty || string.IsNullOrWhiteSpace(result.KeyId)
            || result.Nonce is not { Length: 12 } || result.Tag is not { Length: 16 }
            || result.WrappedKey is not { Length: > 0 } || result.Ciphertext is null)
            throw new CryptographicException("Unsupported envelope.");
        return result;
    }
}
