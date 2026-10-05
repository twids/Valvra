using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valvra.Core;

namespace Valvra.Infrastructure.Security;

public sealed record CipherEnvelope(int Format, string KeyId, byte[] WrappedKey,
    byte[] Nonce, byte[] Ciphertext, byte[] Tag);

public sealed class EnvelopeCipher(IKeyProtector protector) : ISecretCipher
{
    public string Encrypt<T>(T payload, Guid id, long version, string purpose)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Context(id, version, purpose));
            return JsonSerializer.Serialize(new CipherEnvelope(1, protector.ActiveKeyId,
                protector.Wrap(key), nonce, ciphertext, tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public T Decrypt<T>(string envelope, Guid id, long version, string purpose)
    {
        var value = Parse(envelope);
        var key = protector.Unwrap(value.KeyId, value.WrappedKey);
        var plaintext = new byte[value.Ciphertext.Length];
        try
        {
            if (key.Length != 32) throw new CryptographicException("Invalid data key.");
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(value.Nonce, value.Ciphertext, value.Tag, plaintext, Context(id, version, purpose));
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

    private static byte[] Context(Guid id, long version, string purpose) =>
        Encoding.UTF8.GetBytes($"Valvra|1|{purpose}|{id:D}|{version}");

    private static CipherEnvelope Parse(string json)
    {
        var result = JsonSerializer.Deserialize<CipherEnvelope>(json) ?? throw new CryptographicException("Missing envelope.");
        if (result.Format != 1 || string.IsNullOrWhiteSpace(result.KeyId)
            || result.Nonce is not { Length: 12 } || result.Tag is not { Length: 16 }
            || result.WrappedKey is not { Length: > 0 } || result.Ciphertext is null)
            throw new CryptographicException("Unsupported envelope.");
        return result;
    }
}
